using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CubeWorld.Configuration;
using CubeWorld.Gameplay;
using CubeWorld.Tiles;
using CubeWorld.Utils;
using CubeWorld.World;
using CubeWorld.World.Objects;
using World = CubeWorld.World.CubeWorld;

internal static class Program
{
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(5);
    private static readonly WorldListener Listener = new WorldListener();

    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            if (args.Length is 1 or 2 && args[0] == "smoke")
            {
                Smoke(args.ElementAtOrDefault(1));
                return 0;
            }

            if (args.Length is >= 1 and <= 3 && args[0] == "serve")
            {
                int port = args.Length >= 2 ? int.Parse(args[1], NumberStyles.None) : MultiplayerServerGameplay.SERVER_PORT;
                IPAddress address = args.Length == 3 ? IPAddress.Parse(args[2]) : IPAddress.Loopback;
                if (port > 65535 || address.AddressFamily != AddressFamily.InterNetwork)
                    throw new ArgumentException("Specify a TCP port between 0 and 65535 and an IPv4 address.");
                Serve(port, address);
                return 0;
            }

            Console.Error.WriteLine("Usage: smoke [discovery-url] | serve [port [bind-ipv4]]");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static Config LoadConfiguration(BaseGameplay gameplay)
    {
        string Read(string name) => File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "config", "config_" + name + ".xml"));

        AvailableConfigurations available = new ConfigParserXML().Parse(
            Read("misc"), Read("tiles"), Read("avatars"), Read("items"), Read("generators"));
        return new Config
        {
            tileDefinitions = available.tileDefinitions,
            itemDefinitions = available.itemDefinitions,
            avatarDefinitions = available.avatarDefinitions,
            dayInfo = available.dayInfos[0],
            worldGenerator = available.worldGenerators.Single(generator => generator.name == "Plain"),
            worldSize = available.worldSizes.Single(size => size.name == "Small"),
            extraMaterials = available.extraMaterials,
            gameplay = new GameplayDefinition("Basic", "Basic Gameplay", gameplay, false),
        };
    }

    private static World Generate(Config config)
    {
        World world = new World(Listener, Listener);
        var generation = world.Generate(config);
        while (!generation.Generate())
        {
        }
        return world;
    }

    private static void Smoke(string discoveryUrl)
    {
        Config config = LoadConfiguration(new BasicGameplay());
        World original = Generate(config);
        World restored = new World(Listener, Listener);
        var position = new TilePosition(3, original.sizeY - 2, 3);
        byte dirt = original.tileManager.GetTileDefinitionById("dirt").tileType;
        Require(original.sizeX == 64 && original.sizeY == 32 && original.sizeZ == 64,
            "The Small world has unexpected dimensions.");
        Require(original.tileManager.GetTileSolid(new TilePosition(3, 0, 3)),
            "The Plain generator did not produce a solid floor.");
        Require(original.tileManager.GetTileType(position) == TileDefinition.EMPTY_TILE_TYPE,
            "Expected empty air before placing dirt.");
        original.Update(0);
        Require(original.dayCycleManager.ambientLightLuminance == Tile.MAX_LUMINANCE,
            "Initial day lighting does not match the first keyframe.");
        original.dayCycleManager.Update(144);
        Require(original.dayCycleManager.ambientLightLuminance == 3,
            "The 60% day keyframe should give 20% light.");

        original.gameplay.CreateTile(position, dirt);
        restored.Load(config, original.Save());
        try
        {
            Require(restored.sizeX == 64 && restored.sizeY == 32 && restored.sizeZ == 64,
                "Saved world dimensions changed.");
            Require(restored.tileManager.GetTileType(position) == dirt, "Saved edit did not survive reload.");
            Require(restored.avatarManager.player != null, "Saved player did not survive reload.");
            Require(restored.avatarManager.player.inventory.entries.Count
                == original.avatarManager.player.inventory.entries.Count,
                "Saved player inventory changed.");
            Require(restored.dayCycleManager.ambientLightLuminance == 3,
                "Saved day lighting changed.");
        }
        finally
        {
            restored.Clear();
            original.Clear();
        }
        Console.WriteLine("PASS XML world generation, voxel edit and CW-V1 save/load");

        var hosting = new MultiplayerServerGameplay(new BasicGameplay(), false, IPAddress.Loopback, 0);
        World host = Generate(LoadConfiguration(hosting));
        try
        {
            string hostName = IPAddress.Loopback.ToString();
            int port = hosting.Port;
            if (discoveryUrl != null)
            {
                using var http = new HttpClient { Timeout = NetworkTimeout };
                string owner = "Smoke & test";
                string description = "Loopback world + edit";
                Publish(http, DiscoveryProtocol.Register(discoveryUrl, port, owner, description),
                    CancellationToken.None).GetAwaiter().GetResult();
                string listing = http.GetStringAsync(DiscoveryProtocol.Endpoint(discoveryUrl, "list"))
                    .GetAwaiter().GetResult();
                var server = DiscoveryProtocol.ParseList(listing).Single(entry => entry.Port == port);
                Require(server.Owner == owner && server.Description == description,
                    "Discovery did not preserve the advertised labels.");
                hostName = server.Host;
                port = server.Port;
                Console.WriteLine("PASS HTTP registration and discovery through the Unity wire parser");
            }

            var joining = new MultiplayerClientGameplay(hostName, port);
            World guest = null;
            int guestId;
            try
            {
                PumpUntil(() => joining.initializationDataReceived, () =>
                {
                    host.Update(0);
                    joining.Update(0);
                }, "initial multiplayer snapshot");

                guest = new World(Listener, Listener) { gameplay = joining };
                guest.LoadMultiplayer(joining.initializationData);
                Require(guest.sizeX == host.sizeX && guest.sizeY == host.sizeY && guest.sizeZ == host.sizeZ,
                    "Multiplayer snapshot dimensions differ from the host.");
                Require(guest.tileManager.GetTileType(position) == TileDefinition.EMPTY_TILE_TYPE,
                    "Multiplayer snapshot should show empty air.");
                guestId = guest.avatarManager.player.objectId;
                Require(host.avatarManager.GetAvatarByObjectId(guestId) != null,
                    "Host did not create the remote player.");

                joining.CreateTile(position, dirt);
                PumpUntil(() => guest.tileManager.GetTileType(position) == dirt, () =>
                {
                    host.Update(0);
                    guest.Update(0);
                }, "voxel edit round trip");
                Require(host.tileManager.GetTileType(position) == dirt, "Host did not apply the voxel edit.");
            }
            finally
            {
                if (guest != null)
                    guest.Clear();
                else
                    joining.Clear();
            }

            PumpUntil(() => host.avatarManager.GetAvatarByObjectId(guestId) == null,
                () => host.Update(0), "remote player removal");
            Console.WriteLine("PASS TCP join, multiplayer snapshot, replicated voxel edit and disconnect cleanup");
        }
        finally
        {
            host.Clear();
        }
    }

    private static void Serve(int port, IPAddress address)
    {
        string discoveryUrl = Environment.GetEnvironmentVariable(DiscoveryProtocol.EnvironmentKey);
        if (!string.IsNullOrEmpty(discoveryUrl))
            DiscoveryProtocol.Endpoint(discoveryUrl, "register");

        var hosting = new MultiplayerServerGameplay(new BasicGameplay(), false, address, port);
        World world = Generate(LoadConfiguration(hosting));
        using var shutdown = new CancellationTokenSource();
        using var http = new HttpClient { Timeout = NetworkTimeout };
        ConsoleCancelEventHandler stop = (_, args) =>
        {
            args.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += stop;

        Task heartbeat = Task.CompletedTask;
        try
        {
            Console.WriteLine("CubeWorld listening on " + address + ":" + hosting.Port);
            if (!string.IsNullOrEmpty(discoveryUrl))
            {
                Uri registration = DiscoveryProtocol.Register(discoveryUrl, hosting.Port, "CubeWorld", "Headless game");
                heartbeat = PublishPeriodically(http, registration, shutdown.Token);
            }

            var clock = Stopwatch.StartNew();
            TimeSpan previous = clock.Elapsed;
            while (!shutdown.IsCancellationRequested)
            {
                TimeSpan now = clock.Elapsed;
                world.Update((float)(now - previous).TotalSeconds);
                previous = now;
                Thread.Sleep(20);
            }
        }
        finally
        {
            Console.CancelKeyPress -= stop;
            shutdown.Cancel();
            heartbeat.GetAwaiter().GetResult();
            world.Clear();
        }
    }

    private static async Task PublishPeriodically(HttpClient http, Uri registration, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Publish(http, registration, stop);
                }
                catch (HttpRequestException error)
                {
                    Console.Error.WriteLine("Discovery registration failed: " + error.Message);
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    Console.Error.WriteLine("Discovery registration timed out.");
                }
                await Task.Delay(TimeSpan.FromSeconds(DiscoveryProtocol.HeartbeatSeconds), stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private static async Task Publish(HttpClient http, Uri registration, CancellationToken stop)
    {
        if (await http.GetStringAsync(registration, stop) != "OK")
            throw new HttpRequestException("The directory did not acknowledge registration.");
    }

    private static void PumpUntil(Func<bool> finished, Action update, string operation)
    {
        var clock = Stopwatch.StartNew();
        while (!finished())
        {
            if (clock.Elapsed >= NetworkTimeout)
                throw new TimeoutException("Timed out waiting for " + operation + ".");
            update();
            Thread.Sleep(1);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class WorldListener : ICWListener, ICWFxListener
    {
        public void CreateObject(CWObject value) { }
        public void UpdateObject(CWObject value) { }
        public void DestroyObject(CWObject value) { }
        public void PlaySound(string id, Vector3 position) { }
        public void PlaySound(string id, CWObject source) { }
        public void PlayEffect(string id, Vector3 position) { }
        public void PlayEffect(string id, CWObject source) { }
    }
}
