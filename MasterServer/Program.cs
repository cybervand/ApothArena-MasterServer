using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using ApotheonArena.Shared;
using Lidgren.Network;

const int DefaultMasterServerPort = 14343;
const int DefaultHostTimeoutSeconds = 30;
const string DefaultDataDirectoryName = "data";
const byte PacketRegister = 0;
const byte PacketQuit = 1;
const byte PacketNatIntroRequest = 3;
const byte PacketListRequest = 4;
const byte PacketDiagPing = 240;
const byte PacketDiagStatusRequest = 241;
const byte PacketDiagPong = 242;
const byte PacketDiagStatusResponse = 243;
const string AppIdentifier = "Apotheon";
string serverDisplayName = ResolveDefaultServerDisplayName();

var environmentBootstrap = BootstrapEnvironmentFile();
string envPath = environmentBootstrap.EnvPath;

if (environmentBootstrap.WasCreated)
{
    PrintLifecycleBanner(
        "No .env file was found.",
        environmentBootstrap.WasClonedFromExample
            ? "Cloning .env from .env.example."
            : "No .env.example file was found, so a default .env was created.");
}

ServerConfig config;
try
{
    config = LoadConfig();
    serverDisplayName = ResolveServerDisplayName(config);
    MasterLog.Enabled = config.IsDebug;
}
catch (Exception ex)
{
    PrintLifecycleBanner(
        "The server ran into an error during startup.",
        "Error details:",
        ex.Message,
        "Please check the log for more information.");
    return;
}

PrintLifecycleBanner(
    "Welcome!",
    $"Current configuration ({Path.GetFileName(envPath)})",
    $"  MASTER_SERVER_PORT={ReadEnvironmentValueForDisplay("MASTER_SERVER_PORT", config.MasterServerPort.ToString())}",
    $"  HOST_TIMEOUT_SECONDS={ReadEnvironmentValueForDisplay("HOST_TIMEOUT_SECONDS", config.HostTimeoutSeconds.ToString())}",
    $"  LOG_MODE={ReadEnvironmentValueForDisplay("LOG_MODE", config.LogModeName)}",
    $"  DATA_DIRECTORY={ReadEnvironmentValueForDisplay("DATA_DIRECTORY", DefaultDataDirectoryName)}",
    $"  SERVER_DISPLAY_NAME={ReadEnvironmentValueForDisplay("SERVER_DISPLAY_NAME", config.DisplayName)}",
    "Server is starting up...");

var hosts = new Dictionary<long, HostEntry>();
var startedAtUtc = DateTime.UtcNow;
var shouldKeepRunning = 1;
var shutdownAnnounced = 0;
var shutdownCompleted = 0;

void RequestShutdown()
{
    Interlocked.Exchange(ref shouldKeepRunning, 0);
    if (Interlocked.Exchange(ref shutdownAnnounced, 1) != 0)
        return;

    PrintLifecycleBanner("The server is shutting down.");
}

Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    RequestShutdown();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) => RequestShutdown();

var peerConfig = new NetPeerConfiguration(AppIdentifier)
{
    Port = config.MasterServerPort,
};

peerConfig.EnableMessageType(NetIncomingMessageType.UnconnectedData);
peerConfig.EnableMessageType(NetIncomingMessageType.ErrorMessage);
peerConfig.EnableMessageType(NetIncomingMessageType.WarningMessage);
peerConfig.EnableMessageType(NetIncomingMessageType.StatusChanged);

if (config.IsDebug)
{
    peerConfig.EnableMessageType(NetIncomingMessageType.DebugMessage);
    peerConfig.EnableMessageType(NetIncomingMessageType.VerboseDebugMessage);
}

NetPeer? peer = null;

try
{
    peer = new NetPeer(peerConfig);
    peer.Start();

    PrintLifecycleBanner(
        "The server is up and ready to use!",
        "Listening for game servers and players.");

    MasterLog.FromSelf(MasterLogTag.Server,$"debug-mode | detailed-packet-logging={(config.IsDebug ? "enabled" : "disabled")}");
    MasterLog.FromSelf(MasterLogTag.Server,$"diagnostics-enabled | ping={PacketDiagPing} | status={PacketDiagStatusRequest}");

    while (Volatile.Read(ref shouldKeepRunning) == 1)
    {
        try
        {
            ExpireHosts();
            ProcessMessages();
        }
        catch (Exception ex)
        {
            LogError(
                "The server ran into an error while processing traffic.",
                "Error details:",
                ex.Message);
            MasterLog.SelfError(MasterLogTag.Server, ex.ToString());
        }

        Thread.Sleep(10);
    }
}
catch (Exception ex)
{
    PrintLifecycleBanner(
        "The server ran into an error during startup.",
        "Error details:",
        ex.Message,
        "Please check the log for more information.");
    MasterLog.SelfError(MasterLogTag.Server, ex.ToString());
    return;
}
finally
{
    if (peer is not null)
    {
        try
        {
            peer.Shutdown("Server shutting down");
        }
        catch (Exception ex)
        {
            MasterLog.SelfError(MasterLogTag.Server, ex.ToString());
        }
    }

    if (Interlocked.Exchange(ref shutdownCompleted, 1) == 0)
    {
        PrintLifecycleBanner("The server has shut down safely.");
    }
}

void ExpireHosts()
{
    var cutoff = DateTime.UtcNow.AddSeconds(-config.HostTimeoutSeconds);
    foreach (var id in new List<long>(hosts.Keys))
    {
        var host = hosts[id];
        if (host.LastSeen >= cutoff)
            continue;

        hosts.Remove(id);

        LogInfo(
            "Server removed after missing heartbeats",
            $"Server ID: {host.Id}",
            $"Name: {DisplayOrUnknown(host.Info.Name, "Unknown server")}",
            $"Last known External IP: {MasterLog.Describe(host.ExternalIP)}",
            $"Last known Reported LAN IP: {MasterLog.Describe(host.InternalIP)}");

        MasterLog.FromSelf(MasterLogTag.Server,$"remove-stale-host | {DescribeHost(host)}");
        LogHostsDebug("after-expire");
    }
}

void ProcessMessages()
{
    NetIncomingMessage msg;
    while ((msg = peer!.ReadMessage()) is not null)
    {
        try
        {
            switch (msg.MessageType)
            {
                case NetIncomingMessageType.UnconnectedData:
                    HandleUnconnected(msg);
                    break;

                case NetIncomingMessageType.StatusChanged:
                    var status = (NetConnectionStatus)msg.ReadByte();
                    var reason = SafeReadRemainingString(msg);
                    MasterLog.SelfLidgren(MasterLogTag.Server,$"status-changed | sender={MasterLog.Describe(msg.SenderEndPoint)} | status={status} | reason={MasterLog.Quote(reason)}");
                    break;

                case NetIncomingMessageType.ErrorMessage:
                case NetIncomingMessageType.WarningMessage:
                case NetIncomingMessageType.DebugMessage:
                case NetIncomingMessageType.VerboseDebugMessage:
                    MasterLog.SelfLidgren(MasterLogTag.Server,$"library-message | type={msg.MessageType} | sender={MasterLog.Describe(msg.SenderEndPoint)} | text={MasterLog.Quote(SafeReadRemainingString(msg))}");
                    break;

                default:
                    MasterLog.SelfLidgren(MasterLogTag.Server,$"unhandled-message | type={msg.MessageType} | sender={MasterLog.Describe(msg.SenderEndPoint)}");
                    break;
            }
        }
        catch (Exception ex)
        {
            LogError(
                "The server ran into an error while reading a message.",
                "Error details:",
                ex.Message);
            MasterLog.SelfError(
                MasterLogTag.Server,
                $"processing-failed | type={msg.MessageType} | sender={MasterLog.Describe(msg.SenderEndPoint)} | pos={msg.PositionInBytes}/{msg.LengthBytes}");
            MasterLog.SelfError(MasterLogTag.Server, ex.ToString());
        }
        finally
        {
            peer!.Recycle(msg);
        }
    }
}

void HandleUnconnected(NetIncomingMessage msg)
{
    if (msg.LengthBytes - msg.PositionInBytes < 1)
    {
        MasterLog.FromSelf(MasterLogTag.Server,$"drop-empty-packet | sender={MasterLog.Describe(msg.SenderEndPoint)}");
        return;
    }

    byte type = msg.ReadByte();
    MasterLog.FromSelf(MasterLogTag.Server,$"decode-packet | type={PacketTypeName(type)} | sender={MasterLog.Describe(msg.SenderEndPoint)} | remaining={msg.LengthBytes - msg.PositionInBytes}B");

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
            MasterLog.FromSelf(MasterLogTag.Server,$"unknown-packet-type | raw={type} | sender={MasterLog.Describe(msg.SenderEndPoint)}");
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
    var effectiveInternal = Sanitize(reportedInternal, sender, id);
    var effectiveExternal = reportedExternal ?? sender;
    var extraBytes = msg.LengthBytes - msg.PositionInBytes;
    bool isNew = !hosts.ContainsKey(id);
    var info = ParseServerInfo(json);

    hosts[id] = new HostEntry(id, effectiveInternal, effectiveExternal, json, info, DateTime.UtcNow);

    if (isNew)
    {
        LogInfo(
            "Server came online",
            $"Server ID: {id}",
            $"Name: {DisplayOrUnknown(info.Name, "Unknown server")}",
            $"Map: {DisplayOrUnknown(info.Map, "Unknown map")}",
            $"Players: {info.Players}/{info.MaxPlayers}",
            $"External IP: {MasterLog.Describe(effectiveExternal)}",
            $"Reported LAN IP: {MasterLog.Describe(effectiveInternal)}");
    }
    else
    {
        LogInfo(
            "Server heartbeat received",
            $"Server ID: {id}",
            $"Name: {DisplayOrUnknown(info.Name, "Unknown server")}",
            $"Players: {info.Players}/{info.MaxPlayers}",
            $"External IP: {MasterLog.Describe(effectiveExternal)}",
            $"Reported LAN IP: {MasterLog.Describe(effectiveInternal)}");
    }

    if (info.ParseIssue is not null)
    {
        LogWarning(
            "Warning: server info could not be read correctly",
            $"Server ID: {id}",
            "The server will stay online, but some details may be missing.");
        MasterLog.FromPeer(MasterLogTag.Host,$"server-info-parse-issue | serverId={id} | reason={MasterLog.Quote(info.ParseIssue)}");
    }

    if (IsPrivateIpv4(effectiveExternal.Address))
    {
        LogWarning(
            "Warning: server reported a LAN-only address",
            $"Server ID: {id}",
            $"Reported LAN IP: {MasterLog.Describe(effectiveInternal)}",
            "Remote players may not be able to connect.");
    }

    MasterLog.FromPeer(
        MasterLogTag.Host,
        $"{(isNew ? "new-host" : "refresh")} | serverId={id} | sender={MasterLog.Describe(sender)} | reportedInt={MasterLog.Describe(reportedInternal)} | effectiveInt={MasterLog.Describe(effectiveInternal)} | reportedExt={MasterLog.Quote(reportedExternalRaw)} | effectiveExt={MasterLog.Describe(effectiveExternal)} | jsonLength={json.Length} | extraBytes={extraBytes}");
    MasterLog.FromPeer(
        MasterLogTag.Host,
        $"server-info | serverId={id} | name={MasterLog.Quote(info.Name)} | map={MasterLog.Quote(info.Map)} | players={info.Players}/{info.MaxPlayers} | advertisedIp={MasterLog.Quote(info.AdvertisedIp)}");
    LogHostsDebug(isNew ? "after-register" : "after-heartbeat");
}

void OnQuit(NetIncomingMessage msg)
{
    var sender = msg.SenderEndPoint;
    var reportedInternal = msg.ReadIPEndPoint();
    var id = msg.ReadInt64();
    var extraBytes = msg.LengthBytes - msg.PositionInBytes;

    if (hosts.Remove(id, out var removedHost))
    {
        LogInfo(
            "Server went offline",
            $"Server ID: {id}",
            $"Name: {DisplayOrUnknown(removedHost.Info.Name, "Unknown server")}",
            $"External IP: {MasterLog.Describe(removedHost.ExternalIP)}",
            $"Reported LAN IP: {MasterLog.Describe(removedHost.InternalIP)}");
    }
    else
    {
        LogInfo(
            "Server went offline",
            $"Server ID: {id}",
            "Name: Unknown server",
            $"External IP: {MasterLog.Describe(sender)}",
            $"Reported LAN IP: {MasterLog.Describe(reportedInternal)}");
    }

    MasterLog.FromPeer(MasterLogTag.Host,$"host-quit | serverId={id} | sender={MasterLog.Describe(sender)} | reportedInt={MasterLog.Describe(reportedInternal)} | removed={MasterLog.YesNo(removedHost is not null)} | extraBytes={extraBytes}");
    LogHostsDebug("after-quit");
}

void OnNatIntro(NetIncomingMessage msg)
{
    var clientExternal = msg.SenderEndPoint;
    var clientReportedInternal = msg.ReadIPEndPoint();
    var clientInternal = Sanitize(clientReportedInternal, clientExternal, null);
    var hostId = msg.ReadInt64();
    var token = msg.ReadString();
    var extraBytes = msg.LengthBytes - msg.PositionInBytes;

    LogInfo(
        "Player is trying to join a server",
        "Client:",
        $"  External IP: {MasterLog.Describe(clientExternal)}",
        $"  Reported LAN IP: {MasterLog.Describe(clientInternal)}",
        $"Target Server ID: {hostId}");

    if (!hosts.TryGetValue(hostId, out var host))
    {
        LogWarning(
            "A player tried to join, but that server was no longer online",
            "Client:",
            $"  External IP: {MasterLog.Describe(clientExternal)}",
            $"  Reported LAN IP: {MasterLog.Describe(clientInternal)}",
            $"Target Server ID: {hostId}");
        LogHostsDebug("nat-miss");
        MasterLog.FromPeer(MasterLogTag.Client,$"target-missing | hostId={hostId} | knownHosts={hosts.Count} | token={MasterLog.Quote(token)} | extraBytes={extraBytes}");
        return;
    }

    LogInfo(
        "Join request sent to host",
        $"Server ID: {hostId}",
        $"Host External IP: {MasterLog.Describe(host.ExternalIP)}",
        $"Host Reported LAN IP: {MasterLog.Describe(host.InternalIP)}",
        $"Client External IP: {MasterLog.Describe(clientExternal)}",
        $"Client Reported LAN IP: {MasterLog.Describe(clientInternal)}");

    peer!.Introduce(host.InternalIP, host.ExternalIP, clientInternal, clientExternal, token);

    MasterLog.FromPeer(
        MasterLogTag.Client,
        $"intro-request | sender={MasterLog.Describe(clientExternal)} | hostId={hostId} | reportedClientInt={MasterLog.Describe(clientReportedInternal)} | effectiveClientInt={MasterLog.Describe(clientInternal)} | token={MasterLog.Quote(token)} | extraBytes={extraBytes}");
    MasterLog.ToPeer(MasterLogTag.Host, $"introduce | hostId={hostId} | hostExternal={MasterLog.Describe(host.ExternalIP)} | hostInternal={MasterLog.Describe(host.InternalIP)} | clientExternal={MasterLog.Describe(clientExternal)} | clientInternal={MasterLog.Describe(clientInternal)} | token={MasterLog.Quote(token)}");
    MasterLog.ToPeer(MasterLogTag.Client, $"introduce | hostId={hostId} | hostExternal={MasterLog.Describe(host.ExternalIP)} | hostInternal={MasterLog.Describe(host.InternalIP)} | clientExternal={MasterLog.Describe(clientExternal)} | clientInternal={MasterLog.Describe(clientInternal)} | token={MasterLog.Quote(token)}");
}

void OnListRequest(NetIncomingMessage msg)
{
    var client = msg.SenderEndPoint;

    LogInfo(
        "Server list checked",
        "Client:",
        $"  External IP: {MasterLog.Describe(client)}",
        hosts.Count == 0
            ? "Result: no servers available"
            : $"Result: {hosts.Count} server(s) available");

    if (hosts.Count == 0)
    {
        var empty = peer!.CreateMessage();
        empty.Write(false);
        peer.SendUnconnectedMessage(empty, client);
        MasterLog.ToPeer(MasterLogTag.Client, $"unconnected-send | kind=empty-list | client={MasterLog.Describe(client)}");
        return;
    }

    int sent = 0;
    foreach (var host in hosts.Values)
    {
        string selectedIp = PickIp(host.InternalIP, host.ExternalIP, client);
        bool sameSubnet = selectedIp == host.InternalIP.Address.ToString();
        string patchedJson = FixServerInfoIp(host.ServerInfoJson, selectedIp);

        var response = peer.CreateMessage();
        response.Write(true);
        response.Write(host.Id);
        response.Write(host.InternalIP);
        response.Write(host.ExternalIP);
        response.Write(patchedJson);
        response.Write(string.Empty);
        peer.SendUnconnectedMessage(response, client);

        sent++;
        MasterLog.ToPeer(
            MasterLogTag.Client,
            $"unconnected-send | kind=list-entry | serverId={host.Id} | client={MasterLog.Describe(client)} | chosenIp={selectedIp} | sameSubnet={MasterLog.YesNo(sameSubnet)} | hostInt={MasterLog.Describe(host.InternalIP)} | hostExt={MasterLog.Describe(host.ExternalIP)} | jsonChanged={MasterLog.YesNo(patchedJson != host.ServerInfoJson)}");
    }

    MasterLog.FromSelf(MasterLogTag.Server,$"list-response-complete | client={MasterLog.Describe(client)} | sentHosts={sent}");
}

void OnDiagnosticPing(NetIncomingMessage msg)
{
    var sender = msg.SenderEndPoint;
    var nonce = SafeReadRemainingString(msg);
    var uptime = DateTime.UtcNow - startedAtUtc;

    LogInfo(
        "Diagnostic ping received",
        "Client:",
        $"  External IP: {MasterLog.Describe(sender)}");

    var response = peer!.CreateMessage();
    response.Write(PacketDiagPong);
    response.Write(nonce);
    response.Write("ApothArena-masterserver");
    response.Write(DateTime.UtcNow.ToString("O"));
    response.Write((long)uptime.TotalMilliseconds);
    response.Write(hosts.Count);
    peer.SendUnconnectedMessage(response, sender);

    MasterLog.ToPeer(MasterLogTag.Client, $"unconnected-send | kind=diag-pong | client={MasterLog.Describe(sender)} | nonce={MasterLog.Quote(nonce)}");
}

void OnDiagnosticStatusRequest(NetIncomingMessage msg)
{
    var sender = msg.SenderEndPoint;
    var nonce = msg.ReadString();
    bool includeHosts = msg.ReadBoolean();
    var uptime = DateTime.UtcNow - startedAtUtc;
    var snapshot = hosts.Values.OrderBy(h => h.Id).ToList();

    LogInfo(
        "Diagnostic status request received",
        "Client:",
        $"  External IP: {MasterLog.Describe(sender)}",
        $"Include server list: {MasterLog.YesNo(includeHosts)}");

    var response = peer!.CreateMessage();
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
            response.Write(MasterLog.Describe(host.ExternalIP));
            response.Write(MasterLog.Describe(host.InternalIP));
            response.Write((int)Math.Round(age.TotalSeconds));
            response.Write(host.ServerInfoJson);
        }
    }
    else
    {
        response.Write(0);
    }

    peer.SendUnconnectedMessage(response, sender);
    MasterLog.ToPeer(MasterLogTag.Client, $"unconnected-send | kind=diag-status | client={MasterLog.Describe(sender)} | nonce={MasterLog.Quote(nonce)} | includeHosts={MasterLog.YesNo(includeHosts)}");
}

string FixServerInfoIp(string json, string replacementIp)
{
    var start = json.IndexOf("\"IPAddress\":\"", StringComparison.Ordinal);
    if (start < 0)
    {
        MasterLog.FromSelf(MasterLogTag.Server,"json-patch-skip | reason=no-ipaddress-field");
        return json;
    }

    var valueStart = start + 13;
    var valueEnd = json.IndexOf('"', valueStart);
    if (valueEnd < 0)
    {
        MasterLog.FromSelf(MasterLogTag.Server,"json-patch-skip | reason=malformed-ipaddress-field");
        return json;
    }

    return json[..valueStart] + replacementIp + json[valueEnd..];
}

string PickIp(IPEndPoint hostInternal, IPEndPoint hostExternal, IPEndPoint client)
{
    var hostBytes = hostInternal.Address.GetAddressBytes();
    var clientBytes = client.Address.GetAddressBytes();
    bool sameSubnet =
        hostBytes.Length == 4 &&
        clientBytes.Length == 4 &&
        hostBytes[0] == clientBytes[0] &&
        hostBytes[1] == clientBytes[1] &&
        hostBytes[2] == clientBytes[2];

    return sameSubnet
        ? hostInternal.Address.ToString()
        : hostExternal.Address.ToString();
}

IPEndPoint Sanitize(IPEndPoint ep, IPEndPoint fallback, long? hostId)
{
    var bytes = ep.Address.GetAddressBytes();
    bool linkLocal = bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    if (!linkLocal)
        return ep;

    var sanitized = new IPEndPoint(fallback.Address, ep.Port);
    LogWarning(
        "Warning: server reported a link-local address",
        hostId is null ? "Server ID: unknown" : $"Server ID: {hostId}",
        $"Reported LAN IP: {MasterLog.Describe(ep)}",
        $"The master server used: {MasterLog.Describe(sanitized)}");
    MasterLog.FromSelf(MasterLogTag.Server,$"replace-link-local | original={MasterLog.Describe(ep)} | replacement={MasterLog.Describe(sanitized)}");
    return sanitized;
}

ParsedServerInfo ParseServerInfo(string json)
{
    if (string.IsNullOrWhiteSpace(json))
        return new ParsedServerInfo("Unknown server", "Unknown map", 0, 0, null, "empty payload");

    try
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return new ParsedServerInfo(
            GetStringProperty(root, "Name", "Unknown server"),
            GetStringProperty(root, "Map", "Unknown map"),
            GetIntProperty(root, "Players"),
            GetIntProperty(root, "MaxPlayers"),
            TryGetStringProperty(root, "IPAddress"),
            null);
    }
    catch (Exception ex)
    {
        return new ParsedServerInfo("Unknown server", "Unknown map", 0, 0, null, ex.Message);
    }
}

void LogHostsDebug(string reason)
{
    if (!config.IsDebug)
        return;

    if (hosts.Count == 0)
    {
        MasterLog.FromSelf(MasterLogTag.Server,$"hosts-snapshot | reason={reason} | count=0");
        return;
    }

    var snapshot = string.Join(
        " | ",
        hosts.Values
            .OrderBy(h => h.Id)
            .Select(DescribeHost));

    MasterLog.FromSelf(MasterLogTag.Server,$"hosts-snapshot | reason={reason} | count={hosts.Count} | hosts={snapshot}");
}

void LogInfo(string title, params string[] lines)
{
    WriteFriendlyLog(title, lines);
}

void LogWarning(string title, params string[] lines)
{
    WriteFriendlyLog(title, lines);
}

void LogError(string title, params string[] lines)
{
    WriteFriendlyLog(title, lines);
}

void WriteFriendlyLog(string title, params string[] lines)
{
    Console.WriteLine($"{MasterLog.FormatTimestamp()}  {title}");
    foreach (var line in lines)
        Console.WriteLine(line);
}

void PrintLifecycleBanner(params string[] lines)
{
    Console.WriteLine(MasterLog.FormatTimestamp());
    Console.WriteLine($"------------ {serverDisplayName} ------------");
    foreach (var line in lines)
        Console.WriteLine(line);
    Console.WriteLine($"------------ {serverDisplayName} ------------");
}

string ResolveServerDisplayName(ServerConfig config)
{
    return string.IsNullOrWhiteSpace(config.DisplayName)
        ? ResolveDefaultServerDisplayName()
        : config.DisplayName;
}

string ResolveDefaultServerDisplayName()
{
    var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
    var product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
    var title = assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title;

    if (!string.IsNullOrWhiteSpace(product))
        return product;

    if (!string.IsNullOrWhiteSpace(title))
        return title;

    return assembly.GetName().Name ?? "MasterServer";
}

ServerConfig LoadConfig()
{
    int masterServerPort = ReadIntEnvironmentVariable("MASTER_SERVER_PORT", DefaultMasterServerPort, 1, 65535);
    int hostTimeoutSeconds = ReadIntEnvironmentVariable("HOST_TIMEOUT_SECONDS", DefaultHostTimeoutSeconds, 5, 3600);
    var logMode = ReadLogModeEnvironmentVariable("LOG_MODE");
    var dataDirectory = ReadDataDirectory();
    string displayName = ReadEnvironmentValueForDisplay("SERVER_DISPLAY_NAME", ResolveDefaultServerDisplayName());

    return new ServerConfig(masterServerPort, hostTimeoutSeconds, logMode, dataDirectory, displayName);
}

EnvironmentBootstrapResult BootstrapEnvironmentFile()
{
    string envDirectory = ResolveEnvironmentDirectory();
    string envPath = Path.Combine(envDirectory, ".env");
    string examplePath = Path.Combine(envDirectory, ".env.example");
    bool wasCreated = false;
    bool wasClonedFromExample = false;

    if (!File.Exists(envPath))
    {
        wasCreated = true;
        if (File.Exists(examplePath))
        {
            File.Copy(examplePath, envPath);
            wasClonedFromExample = true;
        }
        else
        {
            File.WriteAllLines(
                envPath,
                new[]
                {
                    "# UDP port the master server listens on.",
                    $"MASTER_SERVER_PORT={DefaultMasterServerPort}",
                    string.Empty,
                    "# Remove hosts after this many seconds without a heartbeat.",
                    $"HOST_TIMEOUT_SECONDS={DefaultHostTimeoutSeconds}",
                    string.Empty,
                    "# player = friendly startup/activity logs for operators",
                    "# debug  = player logs plus packet-level Lidgren/network tracing",
                    "LOG_MODE=player",
                    string.Empty,
                    "# Relative paths are resolved inside the app folder.",
                    $"DATA_DIRECTORY={DefaultDataDirectoryName}",
                    string.Empty,
                    "# Name shown in startup, shutdown, and error banners.",
                    $"SERVER_DISPLAY_NAME={ResolveDefaultServerDisplayName()}",
                });
        }
    }

    LoadEnvironmentFile(envPath);
    return new EnvironmentBootstrapResult(envPath, wasCreated, wasClonedFromExample);
}

string ResolveEnvironmentDirectory()
{
    var candidates = new[]
    {
        AppContext.BaseDirectory,
        Environment.CurrentDirectory,
        Path.Combine(Environment.CurrentDirectory, "MasterServer"),
    };

    foreach (var candidate in candidates)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
            continue;

        if (File.Exists(Path.Combine(candidate, ".env")) || File.Exists(Path.Combine(candidate, ".env.example")))
            return candidate;
    }

    return AppContext.BaseDirectory;
}

void LoadEnvironmentFile(string envPath)
{
    if (!File.Exists(envPath))
        return;

    foreach (var rawLine in File.ReadAllLines(envPath))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
            continue;

        int separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0)
            continue;

        string key = line[..separatorIndex].Trim();
        string value = line[(separatorIndex + 1)..].Trim();
        if (key.Length == 0 || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            continue;

        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
            value = value[1..^1];

        Environment.SetEnvironmentVariable(key, value);
    }
}

string ReadEnvironmentValueForDisplay(string name, string fallback)
{
    var raw = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
}

int ReadIntEnvironmentVariable(string name, int fallback, int minValue, int maxValue)
{
    var raw = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(raw))
        return fallback;

    if (!int.TryParse(raw, out var value) || value < minValue || value > maxValue)
        throw new InvalidOperationException($"{name} must be a number between {minValue} and {maxValue}.");

    return value;
}

LogMode ReadLogModeEnvironmentVariable(string name)
{
    var raw = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(raw))
        return LogMode.Player;

    return raw.Trim().ToLowerInvariant() switch
    {
        "player" => LogMode.Player,
        "debug" => LogMode.Debug,
        _ => throw new InvalidOperationException($"{name} must be either 'player' or 'debug'."),
    };
}

string ReadDataDirectory()
{
    var raw = Environment.GetEnvironmentVariable("DATA_DIRECTORY");
    var path = !string.IsNullOrWhiteSpace(raw)
        ? raw.Trim()
        : DefaultDataDirectoryName;

    if (!Path.IsPathRooted(path))
        path = Path.GetFullPath(Path.Combine(ResolveEnvironmentDirectory(), path));

    Directory.CreateDirectory(path);
    return path;
}

int GetIntProperty(JsonElement root, string name)
{
    if (root.TryGetProperty(name, out var value))
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), out number))
        {
            return number;
        }
    }

    return 0;
}

string GetStringProperty(JsonElement root, string name, string fallback)
{
    var value = TryGetStringProperty(root, name);
    return string.IsNullOrWhiteSpace(value) ? fallback : value;
}

string? TryGetStringProperty(JsonElement root, string name)
{
    if (!root.TryGetProperty(name, out var value))
        return null;

    return value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => bool.TrueString,
        JsonValueKind.False => bool.FalseString,
        _ => null,
    };
}

string DescribeHost(HostEntry host)
{
    var age = DateTime.UtcNow - host.LastSeen;
    return $"id={host.Id} ext={MasterLog.Describe(host.ExternalIP)} int={MasterLog.Describe(host.InternalIP)} age={age.TotalSeconds:F1}s";
}

bool IsPrivateIpv4(IPAddress address)
{
    var bytes = address.GetAddressBytes();
    if (bytes.Length != 4)
        return false;

    return
        bytes[0] == 10 ||
        (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
        (bytes[0] == 192 && bytes[1] == 168);
}

string DisplayOrUnknown(string? value, string fallback)
{
    return string.IsNullOrWhiteSpace(value) ? fallback : value;
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

    return IPAddress.TryParse(ipPart, out var address) ? new IPEndPoint(address, port) : null;
}

record ServerConfig(int MasterServerPort, int HostTimeoutSeconds, LogMode LogMode, string DataDirectory, string DisplayName)
{
    public bool IsDebug => LogMode == LogMode.Debug;
    public string LogModeName => LogMode == LogMode.Debug ? "debug" : "player";
}

record EnvironmentBootstrapResult(string EnvPath, bool WasCreated, bool WasClonedFromExample);

enum LogMode
{
    Player,
    Debug,
}

record ParsedServerInfo(string Name, string Map, int Players, int MaxPlayers, string? AdvertisedIp, string? ParseIssue);

record HostEntry(
    long Id,
    IPEndPoint InternalIP,
    IPEndPoint ExternalIP,
    string ServerInfoJson,
    ParsedServerInfo Info,
    DateTime LastSeen);
