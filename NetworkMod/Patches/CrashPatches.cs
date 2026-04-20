using System;
using Apotheon;
using HarmonyLib;

namespace ApotheonArena.NetworkMod.Patches
{
    [HarmonyPatch(typeof(Start), "Crash")]
    static class Start_Crash_Patch
    {
        static void Prefix(ScreenManager SM, string Type, Exception e)
        {
            string type = Type ?? "Unknown";
            string detail = e != null ? e.ToString() : "(null)";
            NetworkModLog.SelfError(NetworkModLogTag.Game,
                "crash | type=" + type + " | " + detail);
        }
    }
}
