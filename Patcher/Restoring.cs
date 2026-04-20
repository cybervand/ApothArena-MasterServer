using System.Xml.Linq;
using static Constants;

internal static class Restoring
{
    public static void Run()
    {
        bool restoredAny = false;

        string? dll = Paths.Find(LidgrenDllName);
        if (dll is not null)
        {
            string backup = dll + ".bak";
            if (File.Exists(backup))
            {
                File.Copy(backup, dll, overwrite: true);
                File.Delete(backup);
                string netdebugBackup = dll + ".netdebugbak";
                if (File.Exists(netdebugBackup))
                    File.Delete(netdebugBackup);
                Console.WriteLine($"Restored original {LidgrenDllName}.");
                restoredAny = true;
            }

            string localIpBackup = dll + ".localipbak";
            if (File.Exists(localIpBackup))
            {
                File.Copy(localIpBackup, dll, overwrite: true);
                File.Delete(localIpBackup);
                Console.WriteLine($"Restored original {LidgrenDllName} local-IP behavior.");
                restoredAny = true;
            }

            string legacyNetdebugBackup = dll + ".netdebugbak";
            if (File.Exists(legacyNetdebugBackup))
            {
                File.Delete(legacyNetdebugBackup);
                Console.WriteLine($"Removed legacy {LidgrenDllName} network debug backup.");
            }
        }

        string? exePath = Paths.Find(ExeName);
        if (exePath is not null)
        {
            string exeDir = Path.GetDirectoryName(exePath)!;
            string browserBackup = exePath + ".browserbak";
            string netdebugBackup = exePath + ".netdebugbak";
            string diagnoseBackup = exePath + ".diagbak";
            if (File.Exists(browserBackup))
            {
                File.Copy(browserBackup, exePath, overwrite: true);
                File.Delete(browserBackup);
                if (File.Exists(netdebugBackup))
                    File.Delete(netdebugBackup);
                if (File.Exists(diagnoseBackup))
                    File.Delete(diagnoseBackup);
                Console.WriteLine($"Restored original {ExeName} browser behavior.");
                restoredAny = true;
            }
            else if (File.Exists(netdebugBackup))
            {
                File.Copy(netdebugBackup, exePath, overwrite: true);
                File.Delete(netdebugBackup);
                Console.WriteLine($"Restored original {ExeName} from network debug backup.");
                restoredAny = true;
            }

            string modsDir = Path.Combine(exeDir, ModsDirectoryName);
            foreach (string modFile in new[]
            {
                "0Harmony.dll",
                "ApotheonArena.NetworkMod.dll",
                NetworkConfigFileName,
            })
            {
                string modPath = Path.Combine(modsDir, modFile);
                if (File.Exists(modPath))
                {
                    File.Delete(modPath);
                    Console.WriteLine($"Removed {ModsDirectoryName}/{modFile}.");
                    restoredAny = true;
                }

                string legacyPath = Path.Combine(exeDir, modFile);
                if (File.Exists(legacyPath))
                {
                    File.Delete(legacyPath);
                    Console.WriteLine($"Removed legacy {modFile} from game root.");
                    restoredAny = true;
                }
            }

            if (Directory.Exists(modsDir) &&
                Directory.GetFileSystemEntries(modsDir).Length == 0)
            {
                Directory.Delete(modsDir);
                Console.WriteLine($"Removed empty {ModsDirectoryName}/ folder.");
            }

            if (RemoveProbingPath(exeDir))
            {
                Console.WriteLine($"Removed probing privatePath=\"{ModsDirectoryName}\" from {ExeName}.config.");
                restoredAny = true;
            }
        }

        if (!restoredAny)
        {
            Console.Error.WriteLine("No patch backups found - may already be unpatched.");
            Cli.Exit(1);
        }
    }

    static bool RemoveProbingPath(string gameDir)
    {
        string configPath = Path.Combine(gameDir, ExeName + ".config");
        if (!File.Exists(configPath)) return false;

        XDocument doc;
        try { doc = XDocument.Load(configPath); }
        catch { return false; }

        if (doc.Root is null) return false;

        bool changed = false;
        foreach (XElement binding in doc.Root.Descendants()
            .Where(e => e.Name.LocalName == "assemblyBinding")
            .ToList())
        {
            var toRemove = binding.Elements()
                .Where(e => e.Name.LocalName == "probing" &&
                            string.Equals((string?)e.Attribute("privatePath"), ModsDirectoryName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var probing in toRemove)
            {
                probing.Remove();
                changed = true;
            }

            if (!binding.HasElements)
                binding.Remove();
        }

        if (!changed) return false;

        foreach (XElement runtime in doc.Root.Elements("runtime").ToList())
        {
            if (!runtime.HasElements)
                runtime.Remove();
        }

        doc.Save(configPath);
        return true;
    }
}
