using Apotheon;
using HarmonyLib;

namespace ApotheonArena.NetworkMod.Patches
{
    [HarmonyPatch(typeof(Network), "ServerStart")]
    static class Network_ServerStart_Patch
    {
        static void Prefix()
        {
            NetworkRole.IsHosting = true;
            NetworkModLog.FromSelf(NetworkModLogTag.Host, "server-start");
        }
        static void Postfix() { NetworkRole.IsHosting = false; }
    }

    [HarmonyPatch(typeof(Network), "ServerUpdate")]
    static class Network_ServerUpdate_Patch
    {
        static void Prefix() { NetworkRole.IsHosting = true; }
        static void Postfix() { NetworkRole.IsHosting = false; }
    }

    [HarmonyPatch(typeof(Network), "ServerQuit")]
    static class Network_ServerQuit_Patch
    {
        static void Prefix()
        {
            NetworkRole.IsHosting = true;
            NetworkModLog.FromSelf(NetworkModLogTag.Host, "server-quit");
        }
        static void Postfix() { NetworkRole.IsHosting = false; }
    }

    [HarmonyPatch(typeof(Network), "RequestNATIntroduction", new[] { typeof(long) })]
    static class Network_RequestNATIntroduction_Patch
    {
        static void Prefix(long hostid)
        {
            NetworkRole.IsJoining = true;
            NetworkModLog.FromSelf(NetworkModLogTag.Client,
                "nat-request | hostId=" + hostid);
        }
        static void Postfix() { NetworkRole.IsJoining = false; }
    }
}
