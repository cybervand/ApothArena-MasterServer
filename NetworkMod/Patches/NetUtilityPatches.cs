using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HarmonyLib;
using Lidgren.Network;

namespace ApotheonArena.NetworkMod.Patches
{
    internal static class NetworkRole
    {
        [ThreadStatic] public static bool IsHosting;
        [ThreadStatic] public static bool IsJoining;
    }

    [HarmonyPatch(typeof(NetUtility), "Resolve", new[] { typeof(string) })]
    static class Resolve_String_Patch
    {
        static bool Prefix(string ipOrHost, ref IPAddress __result)
        {
            IPAddress cached;
            if (MasterServer.TryResolveCached(ipOrHost, out cached))
            {
                __result = cached;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(NetUtility), "Resolve", new[] { typeof(string), typeof(int) })]
    static class Resolve_StringInt_Patch
    {
        static bool Prefix(string ipOrHost, int port, ref IPEndPoint __result)
        {
            IPAddress cached;
            if (MasterServer.TryResolveCached(ipOrHost, out cached))
            {
                __result = new IPEndPoint(cached, port);
                return false;
            }
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
                    NetworkModLog.FromSelf(RoleTag(),
                        "local-address-override | from=" + __result + " to=" + picked);
                    __result = picked;
                }

                if (NetworkRole.IsHosting && __result != null)
                    NetworkConfig.RecordDetectedHostIp(__result.ToString());
                if (NetworkRole.IsJoining && __result != null)
                    NetworkConfig.RecordDetectedClientIp(__result.ToString());
            }
            catch (Exception ex)
            {
                NetworkModLog.SelfError(RoleTag(), "local-address-override-failed | " + ex.Message);
            }
        }

        static string RoleTag()
        {
            if (NetworkRole.IsHosting) return NetworkModLogTag.Host;
            if (NetworkRole.IsJoining) return NetworkModLogTag.Client;
            return NetworkModLogTag.Game;
        }
    }

    internal static class MasterServer
    {
        const string OriginalIp = "50.19.227.23";

        static readonly object _lock = new object();
        static string _cachedKey;
        static IPAddress _cachedAddress;
        static string _loggedTarget;

        public static bool TryResolveCached(string input, out IPAddress address)
        {
            address = null;
            if (string.IsNullOrEmpty(input) || input.Trim() != OriginalIp)
                return false;

            string configured = NetworkConfig.MasterServer;
            if (string.IsNullOrEmpty(configured) || configured == OriginalIp)
                return false;

            lock (_lock)
            {
                if (_cachedKey != configured)
                {
                    _cachedAddress = ResolveFresh(configured);
                    _cachedKey = configured;
                }

                if (_cachedAddress == null)
                    return false;

                if (_loggedTarget != configured)
                {
                    _loggedTarget = configured;
                    NetworkModLog.FromSelf(NetworkModLogTag.Game,
                        "resolve-redirect | from=" + OriginalIp + " to=" + configured
                        + " resolved=" + _cachedAddress);
                }

                address = _cachedAddress;
                return true;
            }
        }

        static IPAddress ResolveFresh(string target)
        {
            IPAddress parsed;
            if (IPAddress.TryParse(target, out parsed))
                return parsed;
            try
            {
                foreach (IPAddress a in Dns.GetHostAddresses(target))
                    if (a.AddressFamily == AddressFamily.InterNetwork)
                        return a;
            }
            catch (Exception ex)
            {
                NetworkModLog.SelfError(NetworkModLogTag.Game,
                    "resolve-redirect-failed | target=" + target + " | " + ex.Message);
            }
            return null;
        }
    }

    internal static class LocalIp
    {
        public static IPAddress Pick(IPAddress fallback)
        {
            string configuredOverride = NetworkRole.IsHosting ? NetworkConfig.HostIp
                                       : NetworkRole.IsJoining ? NetworkConfig.ClientIp
                                       : null;
            if (!string.IsNullOrEmpty(configuredOverride))
            {
                IPAddress parsed;
                if (IPAddress.TryParse(configuredOverride, out parsed)) return parsed;
                try { return NetUtility.Resolve(configuredOverride); } catch { }
            }

            IPAddress best = PickBestInterface();
            if (best != null) return best;

            return fallback;
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
