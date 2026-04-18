using static Constants;

// Patches Apotheon Arena to read the master server address from
// master_server.txt instead of using the hardcoded IP.
// Any IP or hostname of any length is supported.

Cli.IsInteractive = args.Length == 0;

if (Cli.IsInteractive)
{
    try
    {
        RunInteractiveMenu();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Error: {ex.Message}");
        Console.Error.WriteLine(ex.StackTrace);
    }
    Cli.PauseBeforeExit();
    return;
}

if (args.Length >= 2 && string.Equals(args[0], "inspect-type", StringComparison.OrdinalIgnoreCase))
{
    Diagnostics.InspectType(args[1]);
    return;
}

if (args.Length >= 3 && string.Equals(args[0], "inspect-method", StringComparison.OrdinalIgnoreCase))
{
    Diagnostics.InspectMethod(args[1], args[2]);
    return;
}

if (args.Length == 1 && TryExecuteCommand(args[0]))
    return;

PrintUsage();
return;

// ---------------------------------------------------------------------------

bool TryExecuteCommand(string command)
{
    switch (command.ToLowerInvariant())
    {
        case "1":
        case "patch":
            GamePatches.Patch();
            return true;
        case "2":
        case "restore":
            Restoring.Run();
            return true;
        case "patch-basic":
        case "patch-noroute":
            GamePatches.Patch(includeRouteAwareLocalIpFix: false);
            return true;
        case "netdebug":
            Diagnostics.EnableNetDebug();
            return true;
        case "unnetdebug":
            Diagnostics.DisableNetDebug();
            return true;
        case "diagnose":
            Diagnostics.Diagnose();
            return true;
        case "undiagnose":
            Diagnostics.Undiagnose();
            return true;
        case "menu":
            RunInteractiveMenu();
            return true;
        case "0":
        case "exit":
        case "quit":
            return true;
        default:
            return false;
    }
}

void RunInteractiveMenu()
{
    Console.WriteLine("Apotheon Arena - Master Server Patcher");
    Console.WriteLine();
    Console.WriteLine("  1. Patch game");
    Console.WriteLine("  2. Restore original files");
    Console.WriteLine("  0. Exit");
    Console.WriteLine();
    Console.Write("Select option [1]: ");

    string choice = (Console.ReadLine() ?? string.Empty).Trim();
    if (choice.Length == 0)
        choice = "1";

    if (choice is "0" or "exit" or "quit")
        return;

    if (!TryExecuteCommand(choice))
    {
        Console.Error.WriteLine("Unknown option.");
        Cli.Exit(1);
    }
}

void PrintUsage()
{
    Console.WriteLine("Apotheon Arena - Master Server Patcher");
    Console.WriteLine();
    Console.WriteLine("  ApotheonArenaMPpatch.exe            open the interactive menu");
    Console.WriteLine("  ApotheonArenaMPpatch.exe patch      patch the game (run once)");
    Console.WriteLine("  ApotheonArenaMPpatch.exe restore    restore original game files");
    Console.WriteLine();
    Console.WriteLine("Advanced (not shown in the menu):");
    Console.WriteLine("  ApotheonArenaMPpatch.exe patch-basic patch without the local-IP override");
    Console.WriteLine("  ApotheonArenaMPpatch.exe netdebug    log networking calls to Logs\\network_debug.log");
    Console.WriteLine("  ApotheonArenaMPpatch.exe unnetdebug  remove network debug logging");
    Console.WriteLine("  ApotheonArenaMPpatch.exe diagnose    show external crash-watch instructions");
    Console.WriteLine("  ApotheonArenaMPpatch.exe undiagnose  remove an old in-process diagnose patch");
    Console.WriteLine();
    Console.WriteLine($"  After patching, edit {ConfigFileName} in the game folder.");
    Console.WriteLine("  Supports any IP or hostname - no length limit.");
}

// ---------------------------------------------------------------------------

internal static class Constants
{
    public const string OriginalIp          = "50.19.227.23";
    public const string LidgrenDllName      = "Lidgren.Network.dll";
    public const string ExeName             = "ApotheonArena.exe";
    public const string ConfigFileName      = "master_server.txt";
    public const string LocalHostIpFileName = "local_host_ip.txt";
    public const string PublicHostIpFileName = "public_host_ip.txt";
}

internal static class Cli
{
    public static bool IsInteractive;

    public static void Exit(int code)
    {
        if (IsInteractive)
            PauseBeforeExit();
        Environment.Exit(code);
    }

    public static void PauseBeforeExit()
    {
        Console.WriteLine();
        Console.Write("Press any key to exit...");
        try { Console.ReadKey(intercept: true); }
        catch (InvalidOperationException) { Console.ReadLine(); }
        Console.WriteLine();
    }
}

internal static class Paths
{
    public static string? Find(string name)
    {
        foreach (string dir in new[]
        {
            AppContext.BaseDirectory,                          // next to the exe
            Path.Combine(AppContext.BaseDirectory, ".."),     // one level up (subfolder layout)
            Directory.GetCurrentDirectory(),                  // working directory (dev/dotnet run)
        })
        {
            string candidate = Path.GetFullPath(Path.Combine(dir, name));
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
