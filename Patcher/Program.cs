using static Constants;

// Patches Apotheon / Apotheon Arena to redirect the hardcoded master server
// via a managed Harmony mod loaded from mods/. Any IP or hostname works.

Cli.IsInteractive = args.Length == 0;

Target[] detected = Targets.Detect();

if (Cli.IsInteractive)
{
    try
    {
        if (!ResolveTargetInteractive(detected))
            return;
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

if (!ResolveTargetCli(detected))
    return;

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
        case "3":
        case "repair":
            GamePatches.Repair();
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

bool ResolveTargetCli(Target[] detected)
{
    if (detected.Length == 1)
    {
        Targets.Apply(detected[0]);
        return true;
    }

    if (detected.Length == 0)
    {
        Console.Error.WriteLine("Could not find a supported game executable next to the patcher.");
        Console.Error.WriteLine("Expected one of: " + string.Join(", ", Targets.Known.Select(t => t.ExeName)));
        Cli.Exit(1);
        return false;
    }

    Console.Error.WriteLine("Multiple supported executables found in this folder:");
    foreach (Target t in detected)
        Console.Error.WriteLine($"  {t.DisplayName}  ({t.ExeName})");
    Console.Error.WriteLine("Run the patcher interactively (no arguments) to pick a target.");
    Cli.Exit(1);
    return false;
}

bool ResolveTargetInteractive(Target[] detected)
{
    if (detected.Length == 1)
    {
        Targets.Apply(detected[0]);
        return true;
    }

    if (detected.Length == 0)
    {
        Console.Error.WriteLine("Could not find a supported game executable in this folder.");
        Console.Error.WriteLine("Place the patcher next to one of: " + string.Join(", ", Targets.Known.Select(t => t.ExeName)));
        return false;
    }

    return PromptForTarget(detected);
}

bool PromptForTarget(Target[] detected)
{
    Console.WriteLine("Multiple game executables detected in this folder:");
    for (int i = 0; i < detected.Length; i++)
        Console.WriteLine($"  {i + 1}. {detected[i].DisplayName}  ({detected[i].ExeName})");
    Console.WriteLine();
    Console.Write($"Pick target [1]: ");

    string pick = (Console.ReadLine() ?? string.Empty).Trim();
    if (pick.Length == 0) pick = "1";

    if (!int.TryParse(pick, out int idx) || idx < 1 || idx > detected.Length)
    {
        Console.Error.WriteLine("Invalid selection.");
        return false;
    }

    Targets.Apply(detected[idx - 1]);
    Console.WriteLine();
    return true;
}

void RunInteractiveMenu()
{
    Target[] detected = Targets.Detect();

    Console.WriteLine("Apotheon Master Server Patcher");
    Console.WriteLine($"Target: {TargetDisplayName}  ({ExeName})");
    Console.WriteLine();
    Console.WriteLine("  1. Patch game");
    Console.WriteLine("  2. Restore original files");
    Console.WriteLine("  3. Repair (re-extract mods/ payload)");
    if (detected.Length > 1)
        Console.WriteLine("  4. Change target");
    Console.WriteLine("  0. Exit");
    Console.WriteLine();
    Console.Write("Select option [1]: ");

    string choice = (Console.ReadLine() ?? string.Empty).Trim();
    if (choice.Length == 0)
        choice = "1";

    if (choice is "0" or "exit" or "quit")
        return;

    if (choice == "4" && detected.Length > 1)
    {
        if (PromptForTarget(detected))
            RunInteractiveMenu();
        return;
    }

    if (!TryExecuteCommand(choice))
    {
        Console.Error.WriteLine("Unknown option.");
        Cli.Exit(1);
    }
}

void PrintUsage()
{
    Console.WriteLine("Apotheon Master Server Patcher");
    Console.WriteLine($"Target: {TargetDisplayName}  ({ExeName})");
    Console.WriteLine();
    Console.WriteLine("  Patcher.exe            open the interactive menu");
    Console.WriteLine("  Patcher.exe patch      patch the game (run once)");
    Console.WriteLine("  Patcher.exe restore    restore original game files");
    Console.WriteLine("  Patcher.exe repair     re-extract mods/ payload (keep patched exe)");
    Console.WriteLine();
    Console.WriteLine("Advanced (not shown in the menu):");
    Console.WriteLine("  Patcher.exe diagnose    show external crash-watch instructions");
    Console.WriteLine("  Patcher.exe undiagnose  remove an old in-process diagnose patch");
    Console.WriteLine();
    Console.WriteLine($"  After patching, edit {ModsDirectoryName}/{NetworkConfigFileName} in the game folder.");
    Console.WriteLine("  Supports any IP or hostname - no length limit.");
}

// ---------------------------------------------------------------------------

internal record Target(string ExeName, string DisplayName);

internal static class Targets
{
    public static readonly Target[] Known =
    {
        new("ApotheonArena.exe", "Apotheon Arena"),
        new("Apotheon.exe",      "Apotheon (SP)"),
    };

    public static Target[] Detect()
    {
        return Known.Where(t => Paths.Find(t.ExeName) is not null).ToArray();
    }

    public static void Apply(Target target)
    {
        Constants.ExeName = target.ExeName;
        Constants.TargetDisplayName = target.DisplayName;
    }
}

internal static class Constants
{
    public const string OriginalIp           = "50.19.227.23";
    public const string LidgrenDllName       = "Lidgren.Network.dll";
    public const string ConfigFileName       = "master_server.txt";
    public const string LocalHostIpFileName  = "local_host_ip.txt";
    public const string PublicHostIpFileName = "public_host_ip.txt";
    public const string ModsDirectoryName    = "mods";
    public const string NetworkConfigFileName = "networkmod.config";

    public static string ExeName             = "ApotheonArena.exe";
    public static string TargetDisplayName   = "Apotheon Arena";
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
