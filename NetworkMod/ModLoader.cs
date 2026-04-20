using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace ApotheonArena.NetworkMod
{
    public static class ModLoader
    {
        const string HarmonyId = "apotheonarena.networkmod";

        static bool _initialized;

        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                NetworkModLog.FromSelf(NetworkModLogTag.Game, "mod-starting");
                NetworkConfig.Load();

                if (NetworkConfig.ShowConsole)
                {
                    ConsoleMirror.Enable("Apotheon NetworkMod Log");
                    NetworkModLog.FromSelf(NetworkModLogTag.Game,
                        "console-mirror | attached=" + NetworkModLog.YesNo(ConsoleMirror.IsAttached));
                }

                var harmony = new Harmony(HarmonyId);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                NetworkModLog.FromSelf(NetworkModLogTag.Game, "built-in-patches-applied");

                LoadExternalPlugins(harmony);
                NetworkModLog.FromSelf(NetworkModLogTag.Game, "mod-ready");
            }
            catch (Exception ex)
            {
                NetworkModLog.SelfError(NetworkModLogTag.Game, "init-failed | " + ex);
            }
        }

        static void LoadExternalPlugins(Harmony harmony)
        {
            string modsDir = ModsDirectory;
            if (!Directory.Exists(modsDir))
                return;

            string self = new FileInfo(Assembly.GetExecutingAssembly().Location).FullName;
            foreach (string dll in Directory.GetFiles(modsDir, "*.dll"))
            {
                try
                {
                    if (string.Equals(new FileInfo(dll).FullName, self, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (string.Equals(Path.GetFileName(dll), "0Harmony.dll", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var asm = Assembly.LoadFrom(dll);
                    harmony.PatchAll(asm);
                    NetworkModLog.FromSelf(NetworkModLogTag.Game, "plugin-loaded | file=" + Path.GetFileName(dll));
                }
                catch (Exception ex)
                {
                    NetworkModLog.SelfError(NetworkModLogTag.Game, "plugin-load-failed | file=" + Path.GetFileName(dll) + " | " + ex.Message);
                }
            }
        }

        public static string BaseDirectory
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        public static string ModsDirectory
        {
            get { return Path.Combine(BaseDirectory, "mods"); }
        }
    }
}
