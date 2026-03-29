using System.Net;
using System.Net.Sockets;
using Lidgren.Network;

const string AppIdentifier = "Apotheon";

const byte PacketRegister = 0;
const byte PacketQuit = 1;
const byte PacketNatIntroRequest = 3;
const byte PacketListRequest = 4;

if (args.Length < 1)
{
    PrintUsage();
    return;
}

var command = args[0].Trim().ToLowerInvariant();
if (command == "scenarios")
{
    PrintScenarios();
    return;
}

if (args.Length < 2)
{
    PrintUsage();
    return;
}

var masterHost = args[1].Trim();
var parsed = ParseCommonArgs(args.Skip(2));
ApplyScenario(command, parsed);
var master = ResolveTarget(masterHost, parsed.MasterPort);

Log($"master={master.Address}:{master.Port} command={command}");
if (!string.IsNullOrWhiteSpace(parsed.Scenario))
    Log($"scenario={parsed.Scenario}");

switch (command)
{
    case "host":
        RunHost(master, parsed);
        break;
    case "browse":
        RunBrowse(master, parsed);
        break;
    case "join":
        RunJoin(master, parsed);
        break;
    default:
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        Environment.Exit(1);
        break;
}

static void RunHost(IPEndPoint master, ParsedArgs args)
{
    var hostPort = args.GamePort <= 0 ? 14242 : args.GamePort;
    var peer = CreatePeer(hostPort, enableNatMessages: false);
    var hostId = args.HostId ?? Random.Shared.NextInt64(long.MaxValue);
    var internalIp = ResolveLocalIp(args.LocalIp, hostPort);
    var name = args.Name ?? "Sandbox Host";
    var map = args.Map ?? "dm-palace";
    var players = args.Players ?? 1;
    var maxPlayers = args.MaxPlayers ?? 8;
    var durationSeconds = args.DurationSeconds ?? 30;
    var heartbeatMs = args.HeartbeatMs ?? 1000;
    var reportIp = args.ReportIp ?? internalIp.Address.ToString();
    var reportPort = args.ReportPort ?? internalIp.Port;
    var reportedInternal = new IPEndPoint(IPAddress.Parse(reportIp), reportPort);
    var end = DateTime.UtcNow.AddSeconds(durationSeconds);
    var lastSent = DateTime.MinValue;

    Log($"host-id={hostId}");
    Log($"reported-internal={DescribeEndPoint(reportedInternal)} actual-local={DescribeEndPoint(internalIp)}");
    Log($"duration={durationSeconds}s heartbeat={heartbeatMs}ms");

    try
    {
        while (DateTime.UtcNow < end)
        {
            if ((DateTime.UtcNow - lastSent).TotalMilliseconds >= heartbeatMs)
            {
                var packet = peer.CreateMessage();
                packet.Write(PacketRegister);
                packet.Write(reportedInternal);
                packet.Write(hostId);
                packet.Write(BuildServerInfoJson(reportIp, reportPort, hostId, name, map, players, maxPlayers));
                peer.SendUnconnectedMessage(packet, master);
                lastSent = DateTime.UtcNow;
                Log($"sent register/heartbeat to {DescribeEndPoint(master)}");
            }

            DrainMessages(peer, "host");
            Thread.Sleep(25);
        }

        var quit = peer.CreateMessage();
        quit.Write(PacketQuit);
        quit.Write(reportedInternal);
        quit.Write(hostId);
        peer.SendUnconnectedMessage(quit, master);
        Log("sent quit");
    }
    finally
    {
        peer.Shutdown("done");
    }
}

static void RunBrowse(IPEndPoint master, ParsedArgs args)
{
    var peer = CreatePeer(0, enableNatMessages: false);
    var timeoutMs = args.TimeoutMs ?? 3000;
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

    try
    {
        var packet = peer.CreateMessage();
        packet.Write(PacketListRequest);
        peer.SendUnconnectedMessage(packet, master);
        Log($"sent list request to {DescribeEndPoint(master)}");

        var servers = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (!TryReadMessage(peer, out var msg) || msg is null)
            {
                Thread.Sleep(10);
                continue;
            }

            try
            {
                if (msg.MessageType != NetIncomingMessageType.UnconnectedData)
                {
                    PrintSystemMessage(msg, "browse");
                    continue;
                }

                var hasServer = msg.ReadBoolean();
                if (!hasServer)
                {
                    Log("browse received explicit empty-list response from master server");
                    continue;
                }

                var hostId = msg.ReadInt64();
                var internalIp = msg.ReadIPEndPoint();
                var externalIp = msg.ReadIPEndPoint();
                var json = msg.ReadString();
                var country = msg.ReadString();
                servers++;

                Log($"server[{servers}] hasServer={hasServer} id={hostId} int={DescribeEndPoint(internalIp)} ext={DescribeEndPoint(externalIp)} country={Quote(country)}");
                Log($"server[{servers}] info={json}");
            }
            finally
            {
                peer.Recycle(msg);
            }
        }

        Log($"browse complete, received {servers} server entries");
    }
    finally
    {
        peer.Shutdown("done");
    }
}

static void RunJoin(IPEndPoint master, ParsedArgs args)
{
    if (args.HostId is null)
    {
        Console.Error.WriteLine("join requires --host-id=<id>");
        Environment.Exit(1);
        return;
    }

    var bindPort = args.GamePort <= 0 ? 0 : args.GamePort;
    var peer = CreatePeer(bindPort, enableNatMessages: true);
    var timeoutMs = args.TimeoutMs ?? 5000;
    var defaultReportPort = bindPort == 0 ? 14242 : bindPort;
    var localIp = ResolveLocalIp(args.LocalIp, defaultReportPort);
    var reportIp = args.ReportIp ?? localIp.Address.ToString();
    var reportPort = args.ReportPort ?? defaultReportPort;
    var reportedInternal = new IPEndPoint(IPAddress.Parse(reportIp), reportPort);
    var token = args.Token ?? Guid.NewGuid().ToString("N");
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

    Log($"join host-id={args.HostId.Value} token={token}");
    Log($"reported-internal={DescribeEndPoint(reportedInternal)} actual-local={DescribeEndPoint(localIp)}");

    try
    {
        var packet = peer.CreateMessage();
        packet.Write(PacketNatIntroRequest);
        packet.Write(reportedInternal);
        packet.Write(args.HostId.Value);
        packet.Write(token);
        peer.SendUnconnectedMessage(packet, master);
        Log($"sent nat-intro request to {DescribeEndPoint(master)}");

        while (DateTime.UtcNow < deadline)
        {
            if (!TryReadMessage(peer, out var msg) || msg is null)
            {
                Thread.Sleep(10);
                continue;
            }

            try
            {
                if (msg.MessageType == NetIncomingMessageType.UnconnectedData)
                {
                    Log($"join received unconnected data bytes={msg.LengthBytes} from {DescribeEndPoint(msg.SenderEndPoint)}");
                    continue;
                }

                PrintSystemMessage(msg, "join");
            }
            finally
            {
                peer.Recycle(msg);
            }
        }

        Log("join observation window ended");
    }
    finally
    {
        peer.Shutdown("done");
    }
}

static ParsedArgs ParseCommonArgs(IEnumerable<string> args)
{
    var parsed = new ParsedArgs();

    foreach (var arg in args)
    {
        if (TryParseOption(arg, "--master-port=", out var masterPort))
        {
            parsed.MasterPort = int.Parse(masterPort);
            continue;
        }

        if (TryParseOption(arg, "--game-port=", out var gamePort))
        {
            parsed.GamePort = int.Parse(gamePort);
            continue;
        }

        if (TryParseOption(arg, "--timeout=", out var timeout))
        {
            parsed.TimeoutMs = int.Parse(timeout);
            continue;
        }

        if (TryParseOption(arg, "--duration=", out var duration))
        {
            parsed.DurationSeconds = int.Parse(duration);
            continue;
        }

        if (TryParseOption(arg, "--heartbeat-ms=", out var heartbeat))
        {
            parsed.HeartbeatMs = int.Parse(heartbeat);
            continue;
        }

        if (TryParseOption(arg, "--host-id=", out var hostId))
        {
            parsed.HostId = long.Parse(hostId);
            continue;
        }

        if (TryParseOption(arg, "--name=", out var name))
        {
            parsed.Name = name;
            continue;
        }

        if (TryParseOption(arg, "--map=", out var map))
        {
            parsed.Map = map;
            continue;
        }

        if (TryParseOption(arg, "--players=", out var players))
        {
            parsed.Players = int.Parse(players);
            continue;
        }

        if (TryParseOption(arg, "--max-players=", out var maxPlayers))
        {
            parsed.MaxPlayers = int.Parse(maxPlayers);
            continue;
        }

        if (TryParseOption(arg, "--local-ip=", out var localIp))
        {
            parsed.LocalIp = localIp;
            continue;
        }

        if (TryParseOption(arg, "--report-ip=", out var reportIp))
        {
            parsed.ReportIp = reportIp;
            continue;
        }

        if (TryParseOption(arg, "--report-port=", out var reportPort))
        {
            parsed.ReportPort = int.Parse(reportPort);
            continue;
        }

        if (TryParseOption(arg, "--token=", out var token))
        {
            parsed.Token = token;
            continue;
        }

        if (TryParseOption(arg, "--scenario=", out var scenario))
        {
            parsed.Scenario = scenario.Trim().ToLowerInvariant();
            continue;
        }

        Console.Error.WriteLine($"Unknown argument: {arg}");
        PrintUsage();
        Environment.Exit(1);
    }

    return parsed;
}

static bool TryParseOption(string arg, string prefix, out string value)
{
    if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        value = arg[prefix.Length..];
        return true;
    }

    value = string.Empty;
    return false;
}

static void ApplyScenario(string command, ParsedArgs args)
{
    if (string.IsNullOrWhiteSpace(args.Scenario))
        return;

    switch (args.Scenario)
    {
        case "good-default":
            ApplyDefaults(
                args,
                heartbeatMs: command == "host" ? 1000 : null,
                durationSeconds: command == "host" ? 30 : null,
                name: command == "host" ? "Scenario Good Host" : null);
            break;

        case "bad-link-local":
            ApplyDefaults(
                args,
                reportIp: command == "host" ? "169.254.83.107" : "169.254.83.108",
                reportPort: 14242,
                heartbeatMs: command == "host" ? 1000 : null,
                durationSeconds: command == "host" ? 30 : null,
                name: command == "host" ? "Scenario LinkLocal Host" : null);
            break;

        case "tailscale":
            ApplyDefaults(
                args,
                reportIp: command == "host" ? "100.88.77.66" : "100.88.77.67",
                reportPort: 14242,
                heartbeatMs: command == "host" ? 1000 : null,
                durationSeconds: command == "host" ? 30 : null,
                name: command == "host" ? "Scenario Tailscale Host" : null);
            break;

        case "private-lan":
            ApplyDefaults(
                args,
                reportIp: command == "host" ? "192.168.1.50" : "192.168.1.60",
                reportPort: 14242,
                heartbeatMs: command == "host" ? 1000 : null,
                durationSeconds: command == "host" ? 30 : null,
                name: command == "host" ? "Scenario Private LAN Host" : null);
            break;

        default:
            throw new InvalidOperationException($"Unknown scenario '{args.Scenario}'. Run 'MasterServerLab scenarios' to list presets.");
    }
}

static void ApplyDefaults(
    ParsedArgs args,
    string? reportIp = null,
    int? reportPort = null,
    int? heartbeatMs = null,
    int? durationSeconds = null,
    string? name = null)
{
    if (string.IsNullOrWhiteSpace(args.ReportIp) && !string.IsNullOrWhiteSpace(reportIp))
        args.ReportIp = reportIp;
    if (args.ReportPort is null && reportPort is not null)
        args.ReportPort = reportPort;
    if (args.HeartbeatMs is null && heartbeatMs is not null)
        args.HeartbeatMs = heartbeatMs;
    if (args.DurationSeconds is null && durationSeconds is not null)
        args.DurationSeconds = durationSeconds;
    if (string.IsNullOrWhiteSpace(args.Name) && !string.IsNullOrWhiteSpace(name))
        args.Name = name;
}

static NetPeer CreatePeer(int port, bool enableNatMessages)
{
    var config = new NetPeerConfiguration(AppIdentifier)
    {
        Port = port
    };

    config.EnableMessageType(NetIncomingMessageType.UnconnectedData);
    config.EnableMessageType(NetIncomingMessageType.WarningMessage);
    config.EnableMessageType(NetIncomingMessageType.ErrorMessage);
    config.EnableMessageType(NetIncomingMessageType.StatusChanged);
    if (enableNatMessages)
        config.EnableMessageType(NetIncomingMessageType.NatIntroductionSuccess);

    var peer = new NetPeer(config);
    peer.Start();
    return peer;
}

static bool TryReadMessage(NetPeer peer, out NetIncomingMessage? message)
{
    message = peer.ReadMessage();
    return message is not null;
}

static void DrainMessages(NetPeer peer, string role)
{
    while (TryReadMessage(peer, out var msg) && msg is not null)
    {
        try
        {
            PrintSystemMessage(msg, role);
        }
        finally
        {
            peer.Recycle(msg);
        }
    }
}

static void PrintSystemMessage(NetIncomingMessage msg, string role)
{
    switch (msg.MessageType)
    {
        case NetIncomingMessageType.WarningMessage:
        case NetIncomingMessageType.ErrorMessage:
        case NetIncomingMessageType.DebugMessage:
        case NetIncomingMessageType.VerboseDebugMessage:
            Log($"{role} {msg.MessageType}: {SafeReadRemainingString(msg)}");
            break;
        case NetIncomingMessageType.StatusChanged:
            var status = (NetConnectionStatus)msg.ReadByte();
            Log($"{role} status: {status} reason={Quote(SafeReadRemainingString(msg))}");
            break;
        case NetIncomingMessageType.NatIntroductionSuccess:
            try
            {
                var endpoint = msg.ReadIPEndPoint();
                var token = SafeReadRemainingString(msg);
                Log($"{role} nat-success: endpoint={DescribeEndPoint(endpoint)} token={Quote(token)}");
            }
            catch (Exception ex)
            {
                Log($"{role} nat-success: could not parse payload ({ex.GetType().Name}: {ex.Message}) bytes={msg.LengthBytes}");
            }
            break;
        default:
            Log($"{role} message-type={msg.MessageType} sender={DescribeEndPoint(msg.SenderEndPoint)} bytes={msg.LengthBytes}");
            break;
    }
}

static IPEndPoint ResolveTarget(string host, int port)
{
    if (IPAddress.TryParse(host, out var parsed))
        return new IPEndPoint(parsed, port);

    var addresses = Dns.GetHostAddresses(host);
    var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
    if (ipv4 is not null)
        return new IPEndPoint(ipv4, port);
    if (addresses.Length > 0)
        return new IPEndPoint(addresses[0], port);

    throw new InvalidOperationException($"Could not resolve '{host}'.");
}

static IPEndPoint ResolveLocalIp(string? localIp, int port)
{
    if (!string.IsNullOrWhiteSpace(localIp))
        return new IPEndPoint(IPAddress.Parse(localIp), port);

    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    socket.Connect("8.8.8.8", 53);
    return new IPEndPoint(((IPEndPoint)socket.LocalEndPoint!).Address, port);
}

static string BuildServerInfoJson(string ip, int port, long hostId, string name, string map, int players, int maxPlayers)
{
    return
        "{" +
        $"\"IPAddress\":\"{ip}\"," +
        $"\"Port\":{port}," +
        $"\"Id\":{hostId}," +
        $"\"Name\":\"{EscapeJson(name)}\"," +
        $"\"Map\":\"{EscapeJson(map)}\"," +
        $"\"Players\":{players}," +
        $"\"MaxPlayers\":{maxPlayers}," +
        "\"Bots\":0," +
        "\"GameMode\":0," +
        "\"WeaponMode\":0," +
        "\"Ping\":0" +
        "}";
}

static string EscapeJson(string value)
{
    return value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}

static string SafeReadRemainingString(NetIncomingMessage msg)
{
    try
    {
        return msg.LengthBytes > msg.PositionInBytes ? msg.ReadString() : string.Empty;
    }
    catch
    {
        return "<unreadable>";
    }
}

static void Log(string message)
{
    Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}Z {message}");
}

static string DescribeEndPoint(IPEndPoint? endpoint)
{
    return endpoint is null ? "<null>" : $"{endpoint.Address}:{endpoint.Port}";
}

static string Quote(string? value)
{
    return value is null ? "<null>" : $"\"{value}\"";
}

static void PrintUsage()
{
    Console.WriteLine("MasterServerLab");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  MasterServerLab scenarios");
    Console.WriteLine("  MasterServerLab host <master-host> [options]");
    Console.WriteLine("  MasterServerLab browse <master-host> [options]");
    Console.WriteLine("  MasterServerLab join <master-host> --host-id=<id> [options]");
    Console.WriteLine();
    Console.WriteLine("Common options:");
    Console.WriteLine("  --master-port=14343");
    Console.WriteLine("  --game-port=14242");
    Console.WriteLine("  --timeout=3000");
    Console.WriteLine("  --local-ip=<ip>");
    Console.WriteLine("  --report-ip=<ip>");
    Console.WriteLine("  --report-port=<port>");
    Console.WriteLine("  --scenario=<preset>");
    Console.WriteLine();
    Console.WriteLine("Host options:");
    Console.WriteLine("  --duration=30");
    Console.WriteLine("  --heartbeat-ms=1000");
    Console.WriteLine("  --host-id=<long>");
    Console.WriteLine("  --name=<server name>");
    Console.WriteLine("  --map=<map id>");
    Console.WriteLine("  --players=<n>");
    Console.WriteLine("  --max-players=<n>");
    Console.WriteLine();
    Console.WriteLine("Join options:");
    Console.WriteLine("  --token=<value>");
}

static void PrintScenarios()
{
    Console.WriteLine("MasterServerLab scenarios");
    Console.WriteLine();
    Console.WriteLine("good-default");
    Console.WriteLine("  Uses normal defaults with no intentionally bad endpoint data.");
    Console.WriteLine("bad-link-local");
    Console.WriteLine("  Forces a 169.254.x.x reported internal IP to simulate APIPA / wrong-adapter cases.");
    Console.WriteLine("tailscale");
    Console.WriteLine("  Forces a 100.x.y.z reported internal IP to simulate a Tailscale/VPN-routed client.");
    Console.WriteLine("private-lan");
    Console.WriteLine("  Forces a 192.168.x.x reported internal IP to simulate a normal private-LAN report.");
}

sealed class ParsedArgs
{
    public int MasterPort { get; set; } = 14343;
    public int GamePort { get; set; } = 0;
    public int? TimeoutMs { get; set; }
    public int? DurationSeconds { get; set; }
    public int? HeartbeatMs { get; set; }
    public long? HostId { get; set; }
    public string? Name { get; set; }
    public string? Map { get; set; }
    public int? Players { get; set; }
    public int? MaxPlayers { get; set; }
    public string? LocalIp { get; set; }
    public string? ReportIp { get; set; }
    public int? ReportPort { get; set; }
    public string? Token { get; set; }
    public string? Scenario { get; set; }
}
