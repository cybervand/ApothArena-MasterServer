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
            string netdebugBackup = Path.Combine(exeDir, "ApotheonArena.exe.netdebugbak");
            string diagnoseBackup = Path.Combine(exeDir, "ApotheonArena.exe.diagbak");
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

            foreach (string modFile in new[] { "ApotheonArena.NetworkMod.dll", "0Harmony.dll" })
            {
                string modPath = Path.Combine(exeDir, modFile);
                if (File.Exists(modPath))
                {
                    File.Delete(modPath);
                    Console.WriteLine($"Removed mod file {modFile}.");
                    restoredAny = true;
                }
            }
        }

        if (!restoredAny)
        {
            Console.Error.WriteLine("No patch backups found - may already be unpatched.");
            Cli.Exit(1);
        }
    }
}
