using System;
using System.Net;
using System.Text;
using System.Threading;

namespace ApotheonArena.Shared;

internal static class MasterLogTag
{
    public const string From = "FROM";
    public const string To = "TO";
    public const string Peer = "PEER";

    public const string Server = "SERVER";
    public const string Host = "HOST";
    public const string Client = "CLIENT";
    public const string Game = "GAME";

    public const string Lidgren = "LIDGREN";

    public const string Error = "ERROR";
    public const string Warn = "WARN";
}

internal static class MasterLog
{
    static long _sequence;

    public static bool Enabled { get; set; } = true;

    public static void Write(string[] tags, string message)
    {
        if (!Enabled) return;
        var seq = Interlocked.Increment(ref _sequence);
        Console.WriteLine($"{FormatTimestamp()} [{seq:D6}] {FormatTags(tags)} {message}");
    }

    public static void FromSelf(string selfRole, string message) =>
        Write(new[] { MasterLogTag.From, selfRole }, message);

    public static void SelfError(string selfRole, string message) =>
        Write(new[] { MasterLogTag.From, selfRole, MasterLogTag.Error }, message);

    public static void SelfLidgren(string selfRole, string message) =>
        Write(new[] { MasterLogTag.From, selfRole, MasterLogTag.Lidgren }, message);

    public static void SelfLidgrenError(string selfRole, string message) =>
        Write(new[] { MasterLogTag.From, selfRole, MasterLogTag.Lidgren, MasterLogTag.Error }, message);

    public static void FromPeer(string peerRole, string message) =>
        Write(new[] { MasterLogTag.From, MasterLogTag.Peer, peerRole, MasterLogTag.Lidgren }, message);

    public static void ToPeer(string peerRole, string message) =>
        Write(new[] { MasterLogTag.To, MasterLogTag.Peer, peerRole, MasterLogTag.Lidgren }, message);

    public static string FormatTimestamp() =>
        $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z";

    public static string Describe(IPEndPoint? endpoint) =>
        endpoint is null ? "<null>" : $"{endpoint.Address}:{endpoint.Port}";

    public static string Quote(string? value) =>
        value is null ? "<null>" : $"\"{value}\"";

    public static string YesNo(bool value) => value ? "yes" : "no";

    static string FormatTags(string[] tags)
    {
        if (tags is null || tags.Length == 0)
            return string.Empty;
        var sb = new StringBuilder(tags.Length * 10);
        foreach (var tag in tags)
        {
            sb.Append('[');
            sb.Append(tag);
            sb.Append(']');
        }
        return sb.ToString();
    }
}
