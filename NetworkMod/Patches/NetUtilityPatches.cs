using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HarmonyLib;
using Lidgren.Network;

namespace ApotheonArena.NetworkMod.Patches
{
    [HarmonyPatch(typeof(NetUtility), "Resolve", new[] { typeof(string) })]
    static class Resolve_String_Patch
    {
        static bool Prefix(ref string ipOrHost)
        {
            ipOrHost = MasterServer.Redirect(ipOrHost);
            return true;
        }
    }

    [HarmonyPatch(typeof(NetUtility), "Resolve", new[] { typeof(string), typeof(int) })]
    static class Resolve_StringInt_Patch
    {
        static bool Prefix(ref string ipOrHost)
        {
            ipOrHost = MasterServer.Redirect(ipOrHost);
            return true;
        }
    }

    [HarmonyPatch(typeof(NetUtility), "GetMyAddress")]
    static class GetMyAddress_Patch
    {
        static void Postfix(ref IPAddress __result, ref IPAddress mask)
        {
            try
            {
                IPAddress picked = LocalIp.Pick(__result);
                if (picked != null && !Equals(picked, __result))
                {
                    ModLoader.Log("GetMyAddress override: " + __result + " -> " + picked);
                    __result = picked;
                }
            }
            catch (Exception ex)
            {
                ModLoader.Log("GetMyAddress postfix error: " + ex.Message);
            }
        }
    }

    internal static class MasterServer
    {
        const string OriginalIp = "50.19.227.23";
        const string ConfigFile = "master_server.txt";

        public static string Redirect(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            if (input.Trim() != OriginalIp) return input;

            string configured = ReadConfigLine(Path.Combine(ModLoader.BaseDirectory, ConfigFile));
            if (string.IsNullOrEmpty(configured)) return input;

            ModLoader.Log("Resolve redirect: " + OriginalIp + " -> " + configured);
            return configured;
        }

        static string ReadConfigLine(string path)
        {
            if (!File.Exists(path)) return null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                return line;
            }
            return null;
        }
    }

    internal static class LocalIp
    {
        const string OverrideFile = "local_host_ip.txt";

        public static IPAddress Pick(IPAddress fallback)
        {
            IPAddress configured = ReadOverrideFile();
            if (configured != null) return configured;

            IPAddress best = PickBestInterface();
            if (best != null) return best;

            return fallback;
        }

        static IPAddress ReadOverrideFile()
        {
            string path = Path.Combine(ModLoader.BaseDirectory, OverrideFile);
            if (!File.Exists(path)) return null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                IPAddress parsed;
                if (IPAddress.TryParse(line, out parsed)) return parsed;
                try { return NetUtility.Resolve(line); } catch { }
                return null;
            }
            return null;
        }

        static IPAddress PickBestInterface()
        {
            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { return null; }

            var candidates = all
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                         || n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .Where(n => HasDefaultGateway(n))
                .ToList();

            foreach (NetworkInterface nic in candidates)
            {
                IPAddress addr = FirstIPv4(nic);
                if (addr != null) return addr;
            }

            return null;
        }

        static bool HasDefaultGateway(NetworkInterface nic)
        {
            try
            {
                foreach (GatewayIPAddressInformation g in nic.GetIPProperties().GatewayAddresses)
                {
                    if (g.Address == null) continue;
                    if (g.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = g.Address.GetAddressBytes();
                    if (b.Length == 4 && (b[0] != 0 || b[1] != 0 || b[2] != 0 || b[3] != 0))
                        return true;
                }
            }
            catch { }
            return false;
        }

        static IPAddress FirstIPv4(NetworkInterface nic)
        {
            try
            {
                foreach (UnicastIPAddressInformation uni in nic.GetIPProperties().UnicastAddresses)
                {
                    IPAddress a = uni.Address;
                    if (a == null || a.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = a.GetAddressBytes();
                    if (b[0] == 127) continue;
                    if (b[0] == 169 && b[1] == 254) continue;
                    return a;
                }
            }
            catch { }
            return null;
        }
    }
}
