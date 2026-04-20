using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using ApotheonArena.NetworkMod.Patches;

namespace ApotheonArena.NetworkMod
{
    internal static class NetworkModLogTag
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

    internal static class NetworkModLog
    {
        static long _sequence;
        static readonly object _fileLock = new object();

        public static bool Enabled = true;

        public static void Write(string[] tags, string message)
        {
            if (!Enabled) return;
            long seq = Interlocked.Increment(ref _sequence);
            string line = FormatTimestamp() + " [" + seq.ToString("D6") + "] " + FormatTags(tags) + " " + message;
            try
            {
                string logDir = Path.Combine(ModLoader.BaseDirectory, "Logs");
                Directory.CreateDirectory(logDir);
                string path = Path.Combine(logDir, "networkmod.log");
                lock (_fileLock)
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch { }

            ConsoleMirror.WriteLine(line);
        }

        public static void FromSelf(string selfRole, string message)
        {
            Write(new[] { NetworkModLogTag.From, selfRole }, message);
        }

        public static void SelfError(string selfRole, string message)
        {
            Write(new[] { NetworkModLogTag.From, selfRole, NetworkModLogTag.Error }, message);
        }

        public static void SelfLidgren(string selfRole, string message)
        {
            Write(new[] { NetworkModLogTag.From, selfRole, NetworkModLogTag.Lidgren }, message);
        }

        public static void SelfLidgrenError(string selfRole, string message)
        {
            Write(new[] { NetworkModLogTag.From, selfRole, NetworkModLogTag.Lidgren, NetworkModLogTag.Error }, message);
        }

        public static void FromPeer(string peerRole, string message)
        {
            Write(new[] { NetworkModLogTag.From, NetworkModLogTag.Peer, peerRole, NetworkModLogTag.Lidgren }, message);
        }

        public static void ToPeer(string peerRole, string message)
        {
            Write(new[] { NetworkModLogTag.To, NetworkModLogTag.Peer, peerRole, NetworkModLogTag.Lidgren }, message);
        }

        public static string FormatTimestamp()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff") + "Z";
        }

        public static string Describe(IPEndPoint endpoint)
        {
            return endpoint == null ? "<null>" : endpoint.Address + ":" + endpoint.Port;
        }

        public static string Quote(string value)
        {
            return value == null ? "<null>" : "\"" + value + "\"";
        }

        public static string YesNo(bool value)
        {
            return value ? "yes" : "no";
        }

        public static string PeerFor(IPEndPoint endpoint)
        {
            if (endpoint == null) return NetworkModLogTag.Server;
            string addr = endpoint.Address != null ? endpoint.Address.ToString() : "";
            string master = NetworkConfig.MasterServer ?? "";
            if (!string.IsNullOrEmpty(master) && string.Equals(addr, master, StringComparison.OrdinalIgnoreCase))
                return NetworkModLogTag.Server;
            if (NetworkRole.IsHosting) return NetworkModLogTag.Client;
            return NetworkModLogTag.Host;
        }

        static string FormatTags(string[] tags)
        {
            if (tags == null || tags.Length == 0) return string.Empty;
            StringBuilder sb = new StringBuilder(tags.Length * 10);
            foreach (string tag in tags)
            {
                sb.Append('[');
                sb.Append(tag);
                sb.Append(']');
            }
            return sb.ToString();
        }
    }
}
