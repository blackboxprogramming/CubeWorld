using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

public static class DiscoveryProtocol
{
    public const string EnvironmentKey = "CUBEWORLD_DISCOVERY_URL";
    public const string DefaultUrl = "http://127.0.0.1:8081";
    public const int HeartbeatSeconds = 30;

    public sealed class Server
    {
        public readonly string Host;
        public readonly int Port;
        public readonly string Owner;
        public readonly string Description;

        public Server(string host, int port, string owner, string description)
        {
            Host = host;
            Port = port;
            Owner = owner;
            Description = description;
        }
    }

    public static Uri Endpoint(string baseUrl, string route)
    {
        Uri url;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
        {
            throw new ArgumentException("Discovery requires an HTTP(S) base URL.", "baseUrl");
        }
        return new Uri(url.AbsoluteUri.TrimEnd('/') + "/" + route);
    }

    public static Uri Register(string baseUrl, int port, string owner, string description)
    {
        return Endpoint(baseUrl, "register?port=" + port.ToString(CultureInfo.InvariantCulture)
            + "&owner=" + Uri.EscapeDataString(owner)
            + "&description=" + Uri.EscapeDataString(description));
    }

    public static List<Server> ParseList(string body)
    {
        List<Server> servers = new List<Server>();
        foreach (string entry in body.Split(';'))
        {
            string[] fields = entry.Split(',');
            IPAddress address;
            int port;
            if (fields.Length == 4
                && IPAddress.TryParse(fields[0], out address)
                && address.AddressFamily == AddressFamily.InterNetwork
                && address.ToString() == fields[0]
                && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out port)
                && port >= 1 && port <= 65535)
            {
                servers.Add(new Server(fields[0], port, fields[2], fields[3]));
            }
        }
        return servers;
    }
}
