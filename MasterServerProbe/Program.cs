using System.Diagnostics;
using System.Net;
using Lidgren.Network;

const int DefaultPort = 14343;
const int DefaultTimeoutMs = 3000;
const byte PacketDiagPing = 240;
const byte PacketDiagStatusRequest = 241;
const byte PacketDiagPong = 242;
const byte PacketDiagStatusResponse = 243;

if (args.Length < 2)
{
    PrintUsage();
    return;
}

var command = args[0].Trim().ToLowerInvariant();
var host = args[1].Trim();
var port = DefaultPort;
var timeoutMs = DefaultTimeoutMs;
var includeHosts = true;

foreach (var arg in args.Skip(2))
{
    if (int.TryParse(arg, out var parsedPort))
    {
        port = parsedPort;
        continue;
    }

    if (arg.Equals("--no-hosts", StringComparison.OrdinalIgnoreCase))
    {
        includeHosts = false;
        continue;
    }

    if (arg.StartsWith("--timeout=", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(arg["--timeout=".Length..], out var parsedTimeout))
    {
        timeoutMs = parsedTimeout;
        continue;
    }

    Console.Error.WriteLine($"Unknown argument: {arg}");
    PrintUsage();
    Environment.Exit(1);
    return;
}

var target = ResolveTarget(host, port);
Console.WriteLine($"Target: {target.Address}:{target.Port}");
Console.WriteLine($"Command: {command}");

switch (command)
{
    case "ping":
        RunPing(target, timeoutMs);
        break;

    case "status":
        RunStatus(target, timeoutMs, includeHosts);
        break;

    default:
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        Environment.Exit(1);
        break;
}

static void RunPing(IPEndPoint target, int timeoutMs)
{
    var peer = CreatePeer();
    var nonce = Guid.NewGuid().ToString("N");
    var request = peer.CreateMessage();
    request.Write(PacketDiagPing);
    request.Write(nonce);

    var timer = Stopwatch.StartNew();
    peer.SendUnconnectedMessage(request, target);
    Console.WriteLine($"Sent ping nonce={nonce}");

    while (timer.ElapsedMilliseconds < timeoutMs)
    {
        if (TryReadMessage(peer, out var msg) && msg is not null)
        {
            if (msg.MessageType != NetIncomingMessageType.UnconnectedData)
            {
                peer.Recycle(msg);
                continue;
            }

            var packetType = msg.ReadByte();
            if (packetType != PacketDiagPong)
            {
                peer.Recycle(msg);
                continue;
            }

            var responseNonce = msg.ReadString();
            var banner = msg.ReadString();
            var serverUtc = msg.ReadString();
            var uptimeMs = msg.ReadInt64();
            var hostCount = msg.ReadInt32();
            peer.Recycle(msg);

            if (!string.Equals(responseNonce, nonce, StringComparison.Ordinal))
                continue;

            Console.WriteLine("Reachable: yes");
            Console.WriteLine($"Round-trip: {timer.ElapsedMilliseconds} ms");
            Console.WriteLine($"Server: {banner}");
            Console.WriteLine($"Server UTC: {serverUtc}");
            Console.WriteLine($"Server uptime: {FormatDuration(uptimeMs)}");
            Console.WriteLine($"Tracked hosts: {hostCount}");
            peer.Shutdown("done");
            return;
        }

        Thread.Sleep(10);
    }

    Console.WriteLine($"Reachable: no response within {timeoutMs} ms");
    peer.Shutdown("timeout");
    Environment.Exit(2);
}

static void RunStatus(IPEndPoint target, int timeoutMs, bool includeHosts)
{
    var peer = CreatePeer();
    var nonce = Guid.NewGuid().ToString("N");
    var request = peer.CreateMessage();
    request.Write(PacketDiagStatusRequest);
    request.Write(nonce);
    request.Write(includeHosts);

    var timer = Stopwatch.StartNew();
    peer.SendUnconnectedMessage(request, target);
    Console.WriteLine($"Sent status request nonce={nonce} includeHosts={includeHosts}");

    while (timer.ElapsedMilliseconds < timeoutMs)
    {
        if (TryReadMessage(peer, out var msg) && msg is not null)
        {
            if (msg.MessageType != NetIncomingMessageType.UnconnectedData)
            {
                peer.Recycle(msg);
                continue;
            }

            var packetType = msg.ReadByte();
            if (packetType != PacketDiagStatusResponse)
            {
                peer.Recycle(msg);
                continue;
            }

            var responseNonce = msg.ReadString();
            var serverUtc = msg.ReadString();
            var uptimeMs = msg.ReadInt64();
            var hostCount = msg.ReadInt32();
            var responseIncludesHosts = msg.ReadBoolean();
            var returnedHosts = msg.ReadInt32();

            if (!string.Equals(responseNonce, nonce, StringComparison.Ordinal))
            {
                peer.Recycle(msg);
                continue;
            }

            Console.WriteLine("Reachable: yes");
            Console.WriteLine($"Round-trip: {timer.ElapsedMilliseconds} ms");
            Console.WriteLine($"Server UTC: {serverUtc}");
            Console.WriteLine($"Server uptime: {FormatDuration(uptimeMs)}");
            Console.WriteLine($"Tracked hosts: {hostCount}");
            Console.WriteLine($"Host details included: {responseIncludesHosts}");

            for (var i = 0; i < returnedHosts; i++)
            {
                var hostId = msg.ReadInt64();
                var external = msg.ReadString();
                var internalEp = msg.ReadString();
                var ageSeconds = msg.ReadInt32();
                var json = msg.ReadString();

                Console.WriteLine($"Host {i + 1}: id={hostId} ext={external} int={internalEp} age={ageSeconds}s");
                Console.WriteLine($"  info={json}");
            }

            peer.Recycle(msg);
            peer.Shutdown("done");
            return;
        }

        Thread.Sleep(10);
    }

    Console.WriteLine($"Reachable: no response within {timeoutMs} ms");
    peer.Shutdown("timeout");
    Environment.Exit(2);
}

static NetPeer CreatePeer()
{
    var config = new NetPeerConfiguration("Apotheon");
    config.EnableMessageType(NetIncomingMessageType.UnconnectedData);
    config.EnableMessageType(NetIncomingMessageType.WarningMessage);
    config.EnableMessageType(NetIncomingMessageType.ErrorMessage);

    var peer = new NetPeer(config);
    peer.Start();
    return peer;
}

static bool TryReadMessage(NetPeer peer, out NetIncomingMessage? message)
{
    message = peer.ReadMessage();
    while (message is not null &&
           message.MessageType is NetIncomingMessageType.WarningMessage or NetIncomingMessageType.ErrorMessage)
    {
        var text = SafeReadRemainingString(message);
        Console.WriteLine($"[{message.MessageType}] {text}");
        peer.Recycle(message);
        message = peer.ReadMessage();
    }

    return message is not null;
}

static IPEndPoint ResolveTarget(string host, int port)
{
    if (IPAddress.TryParse(host, out var address))
        return new IPEndPoint(address, port);

    var addresses = Dns.GetHostAddresses(host);
    var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
    if (ipv4 is not null)
        return new IPEndPoint(ipv4, port);

    if (addresses.Length > 0)
        return new IPEndPoint(addresses[0], port);

    throw new InvalidOperationException($"Could not resolve host '{host}'.");
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

static string FormatDuration(long totalMilliseconds)
{
    var duration = TimeSpan.FromMilliseconds(totalMilliseconds);
    if (duration.TotalHours >= 1)
        return $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s";
    if (duration.TotalMinutes >= 1)
        return $"{duration.Minutes}m {duration.Seconds}s";
    return $"{duration.TotalSeconds:F1}s";
}

static void PrintUsage()
{
    Console.WriteLine("MasterServerProbe");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  MasterServerProbe ping <host> [port] [--timeout=3000]");
    Console.WriteLine("  MasterServerProbe status <host> [port] [--timeout=3000] [--no-hosts]");
}
