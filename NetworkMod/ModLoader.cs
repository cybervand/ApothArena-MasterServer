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
                Log("Starting");
                var harmony = new Harmony(HarmonyId);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log("Applied built-in networking patches.");

                LoadExternalPlugins(harmony);
                Log("Ready.");
            }
            catch (Exception ex)
            {
                Log("Init failed: " + ex);
            }
        }

        static void LoadExternalPlugins(Harmony harmony)
        {
            string modsDir = Path.Combine(BaseDirectory, "mods");
            if (!Directory.Exists(modsDir))
                return;

            foreach (string dll in Directory.GetFiles(modsDir, "*.dll"))
            {
                try
                {
                    var asm = Assembly.LoadFrom(dll);
                    harmony.PatchAll(asm);
                    Log("Loaded plugin: " + Path.GetFileName(dll));
                }
                catch (Exception ex)
                {
                    Log("Failed to load plugin " + Path.GetFileName(dll) + ": " + ex.Message);
                }
            }
        }

        internal static string BaseDirectory
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        internal static void Log(string message)
        {
            try
            {
                string logDir = Path.Combine(BaseDirectory, "Logs");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "networkmod.log"),
                    DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff'Z' ") + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
