# CubeWorld

CubeWorld is a Unity voxel-game demo with terrain generators, tile rules, items and avatars defined in the XML files in [CubeWorld/Assets/Resources](CubeWorld/Assets/Resources).

- [CubeWorld](CubeWorld) is the Unity desktop project.
- [CubeWorldLibrary](CubeWorldLibrary) is the original C# simulation and multiplayer engine.
- [CubeWorldWeb](CubeWorldWeb) contains the self-hosted multiplayer directory and the archived Unity Web Player files.
- [tools/headless](tools/headless) runs the engine without the Unity editor.

## Run locally

Install Python 3.10 or newer and the .NET 8 SDK. Start the multiplayer directory:

```sh
python3 CubeWorldWeb/cubeworldweb/scripts/cubeworld.py
```

The directory listens on `127.0.0.1:8081` and stores its leases in `cubeworld.sqlite3`. In a second terminal, start a dedicated game host:

```sh
export CUBEWORLD_DISCOVERY_URL=http://127.0.0.1:8081
dotnet run --project tools/headless -- serve
```

Inspect active hosts from another terminal with `curl -fsS http://127.0.0.1:8081/servers`.

The host creates a **Small / Plain** world and listens on TCP `127.0.0.1:9999`. It publishes its endpoint every 30 seconds when `CUBEWORLD_DISCOVERY_URL` is set. `serve <port> <bind-ipv4>` changes the listen address. For LAN play, bind the directory and the game host to reachable IPv4 interfaces and point each client at the same directory. The directory advertises the peer address it actually sees, so that address must be reachable from joining clients. Run both services on loopback or a trusted LAN.

For the graphical client, open `CubeWorld/` with the editor version in [ProjectVersion.txt](CubeWorld/ProjectSettings/ProjectVersion.txt), open `Assets/Main.unity`, and press Play. Choose **Join Multiplayer** to join the dedicated host, or generate a world with **Host Multiplayer** enabled to host in Unity. Set `CUBEWORLD_DISCOVERY_URL` in the editor or player's environment to use a different directory. Unity defaults to the local directory. The graphical host and the headless host need different ports when they run on the same address.

## Discovery API

| Route | Response |
| --- | --- |
| `GET /register?owner=…&description=…&port=…` | `OK` after creating or renewing the peer IP and port's 60-second lease. |
| `GET /list` | The newest 20 active entries in the original `ip,port,owner,description;` format. |
| `GET /servers` | The same active entries as JSON. |
| `GET /health` | `OK` if the database is readable. |

URL-encode label values. Labels must be 2–80 printable characters and exclude commas and semicolons, which delimit the legacy list format.

## Verify

```sh
dotnet build tools/headless --configuration Release
CUBEWORLD_HEADLESS_DLL=tools/headless/bin/Release/net8.0/CubeWorld.Headless.dll \
  python3 -m unittest discover -s CubeWorldWeb/cubeworldweb/tests -v
```

The suite exercises the live directory, world generation, a `CW-V1` save/load round trip, the TCP world snapshot, voxel replication, and disconnect cleanup. It also launches the dedicated host as a separate process and joins its advertised TCP port.

The Unity desktop project imports `CubeWorld/Assets/Libs/CubeWorldLibrary.dll`. Rebuild it after changing engine source by building [CubeWorldLibrary.csproj](CubeWorldLibrary/CubeWorldLibrary.csproj) with the .NET Framework 3.5 reference assemblies. The project copies the resulting DLL into Unity's asset directory. Build the headless project with `-p:UseUnityLibrary=true` to exercise that packaged engine with the same tests.

![Screenshot](Screenshots/ss1.png)
