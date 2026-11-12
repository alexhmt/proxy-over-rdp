using System.Reflection;

namespace RdpProxy;

internal enum ProxyMode : byte
{
    ServerProxy = 0,
    ClientProxy = 1
}

// Client/Server identify the RDP endpoints. Mode independently chooses which
// endpoint owns SOCKS and which one resolves DNS and opens destination sockets.
internal sealed record ProxyOptions(ProxyMode Mode, int Port, bool IsClient, string? AuthFile = null)
{
    public static string Build { get; } = typeof(ProxyOptions).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "development";
    public bool HasListener => Mode == ProxyMode.ClientProxy ? IsClient : !IsClient;

    public static ProxyOptions Parse(string[] args, bool isClient)
    {
        ProxyMode mode = ProxyMode.ServerProxy;
        int port = 1080;
        bool portSpecified = false;
        string? authFile = null;
        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i];
            if (isClient && (option.Equals("-Embedding", StringComparison.OrdinalIgnoreCase) ||
                option.Equals("/Embedding", StringComparison.OrdinalIgnoreCase))) continue;
            if (option == "--mode" && ++i < args.Length)
            {
                mode = args[i].ToLowerInvariant() switch
                {
                    "server-proxy" => ProxyMode.ServerProxy,
                    "client-proxy" => ProxyMode.ClientProxy,
                    _ => throw new ArgumentException("Mode must be server-proxy or client-proxy.")
                };
            }
            else if (option == "--port" && ++i < args.Length && int.TryParse(args[i], out int parsed) && parsed is >= 1 and <= 65535)
            {
                port = parsed;
                portSpecified = true;
            }
            else if (option == "--auth-file" && ++i < args.Length && !string.IsNullOrWhiteSpace(args[i]))
            {
                authFile = Path.GetFullPath(args[i]);
            }
            else throw new ArgumentException("Usage: [--mode server-proxy|client-proxy] [--port PORT] [--auth-file PATH on the SOCKS side]");
        }
        var result = new ProxyOptions(mode, port, isClient, authFile);
        if (portSpecified && !result.HasListener) throw new ArgumentException("--port applies only to the endpoint hosting SOCKS5 in this mode.");
        if (authFile != null && !result.HasListener) throw new ArgumentException("--auth-file applies only to the endpoint hosting SOCKS5 in this mode.");
        return result;
    }
}
