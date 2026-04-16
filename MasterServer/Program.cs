using System.Net;
using Lidgren.Network;

const int Port = 14343;
const int HostTimeoutSeconds = 30;
const byte PacketRegister = 0;
const byte PacketQuit = 1;
const byte PacketNatIntroRequest = 3;
const byte PacketListRequest = 4;
const byte PacketDiagPing = 240;
const byte PacketDiagStatusRequest = 241;
const byte PacketDiagPong = 242;
const byte PacketDiagStatusResponse = 243;

var hosts = new Dictionary<long, HostEntry>();
long logSequence = 0;
var startedAtUtc = DateTime.UtcNow;

var config = new NetPeerConfiguration("Apotheon") { Port = Port };
config.EnableMessageType(NetIncomingMessageType.UnconnectedData);
config.EnableMessageType(NetIncomingMessageType.ErrorMessage);
config.EnableMessageType(NetIncomingMessageType.WarningMessage);
config.EnableMessageType(NetIncomingMessageType.DebugMessage);
config.EnableMessageType(NetIncomingMessageType.VerboseDebugMessage);
config.EnableMessageType(NetIncomingMessageType.StatusChanged);

var peer = new NetPeer(config);
peer.Start();

Log("startup", $"Master server running on UDP {Port}");
Log("startup", $"Host timeout: {HostTimeoutSeconds}s");
Log("startup", "Detailed packet logging enabled");
Log("startup", $"Diagnostic packets enabled: ping={PacketDiagPing}, status={PacketDiagStatusRequest}");

while (true)
{
    try
    {
        ExpireHosts();
        ProcessMessages();
    }
    catch (Exception ex)
    {
        LogException("loop", ex, "Unhandled exception in main loop");
    }

    Thread.Sleep(10);
}

void ExpireHosts()
{
    var cutoff = DateTime.UtcNow.AddSeconds(-HostTimeoutSeconds);
    foreach (var id in new List<long>(hosts.Keys))
    {
        var host = hosts[id];
        if (host.LastSeen >= cutoff)
            continue;

        var age = DateTime.UtcNow - host.LastSeen;
        Log("expire", $"Removing stale host {DescribeHost(host)} age={age.TotalSeconds:F1}s");
        hosts.Remove(id);
        LogHosts("after-expire");
    }
}

void ProcessMessages()
{
    NetIncomingMessage msg;
    while ((msg = peer.ReadMessage()) != null)
    {
        try
        {
            LogIncomingEnvelope(msg);

            switch (msg.MessageType)
            {
                case NetIncomingMessageType.UnconnectedData:
                    HandleUnconnected(msg);
                    break;

                case NetIncomingMessageType.StatusChanged:
                    var status = (NetConnectionStatus)msg.ReadByte();
                    var reason = SafeReadRemainingString(msg);
                    Log("status", $"StatusChanged sender={DescribeEndPoint(msg.SenderEndPoint)} status={status} reason={Quote(reason)}");
                    break;

                case NetIncomingMessageType.ErrorMessage:
                case NetIncomingMessageType.WarningMessage:
                case NetIncomingMessageType.DebugMessage:
                case NetIncomingMessageType.VerboseDebugMessage:
                    Log("lidgren", $"{msg.MessageType} sender={DescribeEndPoint(msg.SenderEndPoint)} text={Quote(SafeReadRemainingString(msg))}");
                    break;

                default:
                    Log("message", $"Unhandled message type {msg.MessageType} from {DescribeEndPoint(msg.SenderEndPoint)}");
                    break;
            }
        }
        catch (Exception ex)
        {
            LogException(
                "message",
                ex,
                $"Failed while processing {msg.MessageType} from {DescribeEndPoint(msg.SenderEndPoint)} pos={msg.PositionInBytes}/{msg.LengthBytes}");
        }
        finally
        {
            peer.Recycle(msg);
        }
    }
}

void HandleUnconnected(NetIncomingMessage msg)
{
    if (msg.LengthBytes - msg.PositionInBytes < 1)
    {
        Log("packet", $"Dropped empty unconnected packet from {DescribeEndPoint(msg.SenderEndPoint)}");
        return;
    }

    byte type = msg.ReadByte();
    Log("packet", $"Decoded {PacketTypeName(type)} from {DescribeEndPoint(msg.SenderEndPoint)} remaining={msg.LengthBytes - msg.PositionInBytes}B");

    switch (type)
    {
        case PacketRegister:
            OnRegister(msg);
            break;

        case PacketQuit:
            OnQuit(msg);
            break;

        case PacketNatIntroRequest:
            OnNatIntro(msg);
            break;

        case PacketListRequest:
            OnListRequest(msg);
            break;

        case PacketDiagPing:
            OnDiagnosticPing(msg);
            break;

        case PacketDiagStatusRequest:
            OnDiagnosticStatusRequest(msg);
            break;

        default:
            Log("packet", $"Unknown unconnected type={type} sender={DescribeEndPoint(msg.SenderEndPoint)}");
            break;
    }
}

void OnRegister(NetIncomingMessage msg)
{
    var sender = msg.SenderEndPoint;
    var reportedInternal = msg.ReadIPEndPoint();
    var id = msg.ReadInt64();
    var json = msg.ReadString();
    var reportedExternalRaw = TryReadTrailingString(msg);
    var reportedExternal = TryParseEndPoint(reportedExternalRaw, sender.Port);
    var effectiveInternal = Sanitize(reportedInternal, sender);
    var effectiveExternal = reportedExternal ?? sender;
    var externalSource = reportedExternal is null ? "sender" : "reported";
    var extraBytes = msg.LengthBytes - msg.PositionInBytes;
    bool sanitized = !EndPointsEqual(reportedInternal, effectiveInternal);
    bool isNew = !hosts.ContainsKey(id);

    hosts[id] = new HostEntry(id, effectiveInternal, effectiveExternal, json, DateTime.UtcNow);

    Log(
        isNew ? "register" : "heartbeat",
        $"{(isNew ? "New host" : "Refresh")} id={id} sender={DescribeEndPoint(sender)} reportedInt={DescribeEndPoint(reportedInternal)} effectiveInt={DescribeEndPoint(effectiveInternal)} reportedExt={Quote(reportedExternalRaw)} effectiveExt={DescribeEndPoint(effectiveExternal)} extSource={externalSource} sanitized={sanitized} jsonLength={json.Length} extraBytes={extraBytes}");
    Log("register", $"Server info for id={id}: {json}");
    LogHosts(isNew ? "after-register" : "after-heartbeat");
}

void OnQuit(NetIncomingMessage msg)
{
    var sender = msg.SenderEndPoint;
    var internalIp = msg.ReadIPEndPoint();
    var id = msg.ReadInt64();
    var extraBytes = msg.LengthBytes - msg.PositionInBytes;
    bool removed = hosts.Remove(id);

    Log("quit", $"Host quit id={id} sender={DescribeEndPoint(sender)} reportedInt={DescribeEndPoint(internalIp)} removed={removed} extraBytes={extraBytes}");
    LogHosts("after-quit");
}

void OnNatIntro(NetIncomingMessage msg)
{
    var clientExternal = msg.SenderEndPoint;
    var clientReportedInternal = msg.ReadIPEndPoint();
    var clientInternal = Sanitize(clientReportedInternal, clientExternal);
    var hostId = msg.ReadInt64();
    var token = msg.ReadString();
    var extraBytes = msg.LengthBytes - msg.PositionInBytes;
    bool sanitized = !EndPointsEqual(clientReportedInternal, clientInternal);

    Log(
        "nat",
        $"Request sender={DescribeEndPoint(clientExternal)} hostId={hostId} reportedClientInt={DescribeEndPoint(clientReportedInternal)} effectiveClientInt={DescribeEndPoint(clientInternal)} sanitized={sanitized} token={Quote(token)} extraBytes={extraBytes}");

    if (!hosts.TryGetValue(hostId, out var host))
    {
        Log("nat", $"Unknown hostId={hostId}; knownHosts={hosts.Count}");
        LogHosts("nat-miss");
        return;
    }

    Log("nat", $"Introducing client {DescribeEndPoint(clientExternal)} <-> host {DescribeHost(host)}");
    peer.Introduce(host.InternalIP, host.ExternalIP, clientInternal, clientExternal, token);
    Log("nat", $"Introduce sent for hostId={hostId} token={Quote(token)}");
}

void OnListRequest(NetIncomingMessage msg)
{
    var client = msg.SenderEndPoint;
    Log("list", $"List request from {DescribeEndPoint(client)} hostsAvailable={hosts.Count}");

    if (hosts.Count == 0)
    {
        var empty = peer.CreateMessage();
        empty.Write(false);
        peer.SendUnconnectedMessage(empty, client);
        Log("list", $"Sent empty list marker to {DescribeEndPoint(client)}");
        return;
    }

    int sent = 0;
    foreach (var host in hosts.Values)
    {
        string selectedIp = PickIp(host.InternalIP, host.ExternalIP, client);
        bool sameSubnet = selectedIp == host.InternalIP.Address.ToString();
        string patchedJson = FixServerInfoIp(host.ServerInfoJson, selectedIp);

        var res = peer.CreateMessage();
        res.Write(true);
        res.Write(host.Id);
        res.Write(host.InternalIP);
        res.Write(host.ExternalIP);
        res.Write(patchedJson);
        res.Write("");
        peer.SendUnconnectedMessage(res, client);

        sent++;
        Log(
            "list",
            $"Sent host id={host.Id} to {DescribeEndPoint(client)} chosenIp={selectedIp} sameSubnet={sameSubnet} hostInt={DescribeEndPoint(host.InternalIP)} hostExt={DescribeEndPoint(host.ExternalIP)} jsonChanged={patchedJson != host.ServerInfoJson}");
    }

    Log("list", $"Completed list response to {DescribeEndPoint(client)} sentHosts={sent}");
}

void OnDiagnosticPing(NetIncomingMessage msg)
{
    var sender = msg.SenderEndPoint;
    var nonce = SafeReadRemainingString(msg);
    var uptime = DateTime.UtcNow - startedAtUtc;

    Log("diag", $"Ping request from {DescribeEndPoint(sender)} nonce={Quote(nonce)} uptime={uptime.TotalSeconds:F1}s hosts={hosts.Count}");

    var response = peer.CreateMessage();
    response.Write(PacketDiagPong);
    response.Write(nonce);
    response.Write("ApothArena-masterserver");
    response.Write(DateTime.UtcNow.ToString("O"));
    response.Write((long)uptime.TotalMilliseconds);
    response.Write(hosts.Count);
    peer.SendUnconnectedMessage(response, sender);

    Log("diag", $"Pong sent to {DescribeEndPoint(sender)} nonce={Quote(nonce)}");
}

void OnDiagnosticStatusRequest(NetIncomingMessage msg)
{
    var sender = msg.SenderEndPoint;
    var nonce = msg.ReadString();
    bool includeHosts = msg.ReadBoolean();
    var uptime = DateTime.UtcNow - startedAtUtc;
    var snapshot = hosts.Values.OrderBy(h => h.Id).ToList();

    Log("diag", $"Status request from {DescribeEndPoint(sender)} nonce={Quote(nonce)} includeHosts={includeHosts} hostCount={snapshot.Count}");

    var response = peer.CreateMessage();
    response.Write(PacketDiagStatusResponse);
    response.Write(nonce);
    response.Write(DateTime.UtcNow.ToString("O"));
    response.Write((long)uptime.TotalMilliseconds);
    response.Write(snapshot.Count);
    response.Write(includeHosts);

    if (includeHosts)
    {
        response.Write(snapshot.Count);
        foreach (var host in snapshot)
        {
            var age = DateTime.UtcNow - host.LastSeen;
            response.Write(host.Id);
            response.Write(DescribeEndPoint(host.ExternalIP));
            response.Write(DescribeEndPoint(host.InternalIP));
            response.Write((int)Math.Round(age.TotalSeconds));
            response.Write(host.ServerInfoJson);
        }
    }
    else
    {
        response.Write(0);
    }

    peer.SendUnconnectedMessage(response, sender);

    Log("diag", $"Status response sent to {DescribeEndPoint(sender)} nonce={Quote(nonce)} includedHosts={includeHosts}");
}

string FixServerInfoIp(string json, string replacementIp)
{
    var start = json.IndexOf("\"IPAddress\":\"", StringComparison.Ordinal);
    if (start < 0)
    {
        Log("json", "Server info JSON did not contain an IPAddress field; leaving payload unchanged");
        return json;
    }

    var valueStart = start + 13;
    var valueEnd = json.IndexOf('"', valueStart);
    if (valueEnd < 0)
    {
        Log("json", "Server info JSON had a malformed IPAddress field; leaving payload unchanged");
        return json;
    }

    return json[..valueStart] + replacementIp + json[valueEnd..];
}

string PickIp(IPEndPoint hostInternal, IPEndPoint hostExternal, IPEndPoint client)
{
    var h = hostInternal.Address.GetAddressBytes();
    var c = client.Address.GetAddressBytes();
    bool sameSubnet =
        h.Length == 4 &&
        c.Length == 4 &&
        h[0] == c[0] &&
        h[1] == c[1] &&
        h[2] == c[2];

    return sameSubnet
        ? hostInternal.Address.ToString()
        : hostExternal.Address.ToString();
}

IPEndPoint Sanitize(IPEndPoint ep, IPEndPoint fallback)
{
    var bytes = ep.Address.GetAddressBytes();
    bool linkLocal = bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    if (!linkLocal)
        return ep;

    var sanitized = new IPEndPoint(fallback.Address, ep.Port);
    Log("sanitize", $"Replaced link-local endpoint {DescribeEndPoint(ep)} with {DescribeEndPoint(sanitized)}");
    return sanitized;
}

void LogIncomingEnvelope(NetIncomingMessage msg)
{
    Log(
        "recv",
        $"type={msg.MessageType} sender={DescribeEndPoint(msg.SenderEndPoint)} bytes={msg.LengthBytes} seq={msg.SequenceChannel} pos={msg.PositionInBytes}");
}

void LogHosts(string reason)
{
    if (hosts.Count == 0)
    {
        Log("hosts", $"{reason}: no active hosts");
        return;
    }

    var snapshot = string.Join(
        " | ",
        hosts.Values
            .OrderBy(h => h.Id)
            .Select(DescribeHost));

    Log("hosts", $"{reason}: count={hosts.Count} {snapshot}");
}

void Log(string category, string message)
{
    var sequence = Interlocked.Increment(ref logSequence);
    Console.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z [{sequence:D6}] [{category}] {message}");
}

void LogException(string category, Exception ex, string context)
{
    Log(category, $"{context} exception={ex.GetType().Name} message={Quote(ex.Message)}");
    Log(category, ex.ToString());
}

string DescribeHost(HostEntry host)
{
    var age = DateTime.UtcNow - host.LastSeen;
    return $"id={host.Id} ext={DescribeEndPoint(host.ExternalIP)} int={DescribeEndPoint(host.InternalIP)} age={age.TotalSeconds:F1}s";
}

string DescribeEndPoint(IPEndPoint? ep)
{
    return ep is null ? "<null>" : $"{ep.Address}:{ep.Port}";
}

bool EndPointsEqual(IPEndPoint a, IPEndPoint b)
{
    return a.Port == b.Port && Equals(a.Address, b.Address);
}

string PacketTypeName(byte type)
{
    return type switch
    {
        PacketRegister => "register/heartbeat",
        PacketQuit => "quit",
        PacketNatIntroRequest => "nat-intro-request",
        PacketListRequest => "list-request",
        PacketDiagPing => "diag-ping",
        PacketDiagStatusRequest => "diag-status-request",
        PacketDiagPong => "diag-pong",
        PacketDiagStatusResponse => "diag-status-response",
        _ => $"unknown({type})",
    };
}

string SafeReadRemainingString(NetIncomingMessage msg)
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

string? TryReadTrailingString(NetIncomingMessage msg)
{
    if (msg.LengthBytes - msg.PositionInBytes <= 0)
        return null;

    try
    {
        var value = msg.ReadString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
    catch
    {
        return null;
    }
}

IPEndPoint? TryParseEndPoint(string? raw, int defaultPort)
{
    if (string.IsNullOrWhiteSpace(raw))
        return null;

    var text = raw.Trim();
    string ipPart = text;
    int port = defaultPort;

    int firstColon = text.IndexOf(':');
    int lastColon = text.LastIndexOf(':');
    bool looksLikeIpv4WithPort = firstColon >= 0 && firstColon == lastColon && lastColon < text.Length - 1;
    if (looksLikeIpv4WithPort)
    {
        ipPart = text[..lastColon];
        if (!int.TryParse(text[(lastColon + 1)..], out port) || port <= 0 || port > 65535)
            port = defaultPort;
    }

    return IPAddress.TryParse(ipPart, out var addr) ? new IPEndPoint(addr, port) : null;
}

string Quote(string? value)
{
    return value is null ? "<null>" : $"\"{value}\"";
}

record HostEntry(long Id, IPEndPoint InternalIP, IPEndPoint ExternalIP, string ServerInfoJson, DateTime LastSeen);
