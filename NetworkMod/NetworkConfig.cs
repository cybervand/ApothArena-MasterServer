using System;
using System.IO;
using System.Xml.Linq;

namespace ApotheonArena.NetworkMod
{
    internal static class NetworkConfig
    {
        public const string FileName = "networkmod.config";
        public const string DefaultMasterServer = "50.19.227.23";

        public static string MasterServer = DefaultMasterServer;
        public static string HostIp = "";
        public static string ClientIp = "";
        public static string PublicHostIp = "";
        public static bool ShowConsole = false;

        static string Path { get { return System.IO.Path.Combine(ModLoader.ModsDirectory, FileName); } }

        public static void Load()
        {
            string path = Path;
            if (!File.Exists(path))
            {
                NetworkModLog.FromSelf(NetworkModLogTag.Game, "config-seed-defaults | file=" + FileName);
                Save();
                return;
            }

            try
            {
                XDocument doc = XDocument.Load(path);
                XElement root = doc.Root;
                if (root == null) return;

                MasterServer = ReadText(root, "masterServer", DefaultMasterServer);
                HostIp       = ReadText(root, "hostIp", "");
                ClientIp     = ReadText(root, "clientIp", "");
                PublicHostIp = ReadText(root, "publicHostIp", "");
                ShowConsole  = ReadBool(root, "showConsole", false);

                NetworkModLog.FromSelf(NetworkModLogTag.Game,
                    "config-loaded | masterServer=" + MasterServer
                    + " hostIp=" + Describe(HostIp)
                    + " clientIp=" + Describe(ClientIp)
                    + " publicHostIp=" + Describe(PublicHostIp)
                    + " showConsole=" + NetworkModLog.YesNo(ShowConsole));
            }
            catch (Exception ex)
            {
                NetworkModLog.SelfError(NetworkModLogTag.Game, "config-load-failed | " + ex.Message + " (using defaults)");
            }
        }

        public static void Save()
        {
            try
            {
                var doc = new XDocument(
                    new XDeclaration("1.0", "utf-8", null),
                    new XElement("networkmod",
                        new XComment(" Master server host or IP. Required. "),
                        new XElement("masterServer", MasterServer ?? DefaultMasterServer),
                        new XComment(" LAN IP to advertise when hosting. Blank = auto-detect. "),
                        new XElement("hostIp", HostIp ?? ""),
                        new XComment(" LAN IP to advertise when joining. Blank = auto-detect. "),
                        new XElement("clientIp", ClientIp ?? ""),
                        new XComment(" WAN endpoint override (ip or ip:port). Rare; only for hairpin NAT on LAN masters. "),
                        new XElement("publicHostIp", PublicHostIp ?? ""),
                        new XComment(" Set true to open a separate console window mirroring mod logs. "),
                        new XElement("showConsole", ShowConsole ? "true" : "false")));

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                doc.Save(Path);
            }
            catch (Exception ex)
            {
                NetworkModLog.SelfError(NetworkModLogTag.Game, "config-save-failed | " + ex.Message);
            }
        }

        public static void RecordDetectedHostIp(string detected)
        {
            if (string.IsNullOrEmpty(detected)) return;
            if (!string.IsNullOrEmpty(HostIp)) return;
            HostIp = detected;
            Save();
        }

        public static void RecordDetectedClientIp(string detected)
        {
            if (string.IsNullOrEmpty(detected)) return;
            if (!string.IsNullOrEmpty(ClientIp)) return;
            ClientIp = detected;
            Save();
        }

        static string ReadText(XElement root, string name, string fallback)
        {
            XElement el = root.Element(name);
            if (el == null) return fallback;
            string value = (el.Value ?? "").Trim();
            return value.Length == 0 ? fallback : value;
        }

        static bool ReadBool(XElement root, string name, bool fallback)
        {
            XElement el = root.Element(name);
            if (el == null) return fallback;
            string value = (el.Value ?? "").Trim().ToLowerInvariant();
            if (value.Length == 0) return fallback;
            if (value == "true" || value == "1" || value == "yes" || value == "on") return true;
            if (value == "false" || value == "0" || value == "no" || value == "off") return false;
            return fallback;
        }

        static string Describe(string value)
        {
            return string.IsNullOrEmpty(value) ? "<auto>" : value;
        }
    }
}
