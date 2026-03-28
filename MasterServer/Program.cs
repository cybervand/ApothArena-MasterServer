using System.Net;
using Lidgren.Network;

const int Port = 14343;
const int HostTimeoutSeconds = 30;

var hosts = new Dictionary<long, HostEntry>();

var config = new NetPeerConfiguration("Apotheon") { Port = Port };
config.EnableMessageType(NetIncomingMessageType.UnconnectedData);

var peer = new NetPeer(config);
peer.Start();
Console.WriteLine($"[ApothArena-masterserver] Running on UDP {Port}");

while (true)
{
    ExpireHosts();
    ProcessMessages();
    Thread.Sleep(10);
}

void ExpireHosts()
{
    var cutoff = DateTime.UtcNow.AddSeconds(-HostTimeoutSeconds);
    foreach (var id in new List<long>(hosts.Keys))
        if (hosts[id].LastSeen < cutoff)
        {
            Console.WriteLine($"[expire] host {id}");
            hosts.Remove(id);
        }
}

void ProcessMessages()
{
    NetIncomingMessage msg;
    while ((msg = peer.ReadMessage()) != null)
    {
        if (msg.MessageType == NetIncomingMessageType.UnconnectedData)
            HandleUnconnected(msg);
        peer.Recycle(msg);
    }
}

void HandleUnconnected(NetIncomingMessage msg)
{
    byte type = msg.ReadByte();
    switch (type)
    {
        case 0: OnRegister(msg);     break; // host heartbeat/register
        case 1: OnQuit(msg);         break; // host quit
        case 3: OnNatIntro(msg);     break; // client requests NAT intro
        case 4: OnListRequest(msg);  break; // client wants server list
        default:
            Console.WriteLine($"[warn] Unknown type {type} from {msg.SenderEndPoint}");
            break;
    }
}

void OnRegister(NetIncomingMessage msg)
{
    var internalIP = msg.ReadIPEndPoint();
    var id        = msg.ReadInt64();
    var json      = msg.ReadString();
    var externalIP = msg.SenderEndPoint;

    internalIP = Sanitize(internalIP, externalIP);

    bool isNew = !hosts.ContainsKey(id);
    hosts[id] = new HostEntry(id, internalIP, externalIP, json, DateTime.UtcNow);

    if (isNew)
        Console.WriteLine($"[register]  id={id}  ext={externalIP}  int={internalIP}  info={json}");
    else
        Console.WriteLine($"[heartbeat] id={id}  ext={externalIP}  int={internalIP}");
}

void OnQuit(NetIncomingMessage msg)
{
    msg.ReadIPEndPoint(); // internalIP sent by game, not needed
    var id = msg.ReadInt64();
    hosts.Remove(id);
    Console.WriteLine($"[quit] {id}");
}

void OnNatIntro(NetIncomingMessage msg)
{
    var clientInternal = Sanitize(msg.ReadIPEndPoint(), msg.SenderEndPoint);
    var hostId         = msg.ReadInt64();
    var token          = msg.ReadString();
    var clientExternal = msg.SenderEndPoint;

    if (!hosts.TryGetValue(hostId, out var host))
    {
        Console.WriteLine($"[nat] Unknown host {hostId} from {clientExternal}");
        return;
    }

    Console.WriteLine($"[nat] introducing:");
    Console.WriteLine($"[nat]   client  ext={clientExternal}  int={clientInternal}");
    Console.WriteLine($"[nat]   host    ext={host.ExternalIP}  int={host.InternalIP}");
    Console.WriteLine($"[nat]   token={token}");
    peer.Introduce(host.InternalIP, host.ExternalIP, clientInternal, clientExternal, token);
    Console.WriteLine($"[nat] introduce sent");
}

void OnListRequest(NetIncomingMessage msg)
{
    var client = msg.SenderEndPoint;
    foreach (var host in hosts.Values)
    {
        var res = peer.CreateMessage();
        res.Write(true);             // hasServer
        res.Write(host.Id);
        res.Write(host.InternalIP);
        res.Write(host.ExternalIP);
        res.Write(FixServerInfoIp(host.ServerInfoJson, PickIp(host.InternalIP, host.ExternalIP, client)));
        res.Write("");               // country — unknown
        peer.SendUnconnectedMessage(res, client);
    }
    Console.WriteLine($"[list] Sent {hosts.Count} server(s) to {client}");
}

string FixServerInfoIp(string json, string externalIp)
{
    var start = json.IndexOf("\"IPAddress\":\"");
    if (start < 0) return json;
    var valueStart = start + 13;
    var valueEnd   = json.IndexOf('"', valueStart);
    if (valueEnd < 0) return json;
    return json[..valueStart] + externalIp + json[valueEnd..];
}

string PickIp(IPEndPoint hostInternal, IPEndPoint hostExternal, IPEndPoint client)
{
    var h = hostInternal.Address.GetAddressBytes();
    var c = client.Address.GetAddressBytes();
    if (h.Length == 4 && c.Length == 4 &&
        h[0] == c[0] && h[1] == c[1] && h[2] == c[2])
        return hostInternal.Address.ToString();
    return hostExternal.Address.ToString();
}

IPEndPoint Sanitize(IPEndPoint ep, IPEndPoint fallback)
{
    var b = ep.Address.GetAddressBytes();
    bool linkLocal = b.Length == 4 && b[0] == 169 && b[1] == 254;
    return linkLocal ? new IPEndPoint(fallback.Address, ep.Port) : ep;
}

record HostEntry(long Id, IPEndPoint InternalIP, IPEndPoint ExternalIP, string ServerInfoJson, DateTime LastSeen);
