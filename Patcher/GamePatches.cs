using System.Xml.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables.Rows;
using static Constants;

internal static class GamePatches
{
    public static void Patch(bool includeRouteAwareLocalIpFix = true)
    {
        string? exePath = Paths.Find(ExeName);
        if (exePath is null)
        {
            Console.Error.WriteLine($"Could not find {ExeName} - place Patcher.exe in the game folder.");
            Cli.Exit(1);
            return;
        }

        string browserBackupPath = exePath + ".browserbak";
        if (File.Exists(browserBackupPath))
        {
            Console.Error.WriteLine("Browser patch already applied. Run 'Patcher.exe restore' first to unpatch, or 'Patcher.exe repair' to refresh mods/ payload files.");
            Cli.Exit(1);
            return;
        }

        PatchGameExecutable(exePath, browserBackupPath);

        string gameDir = Path.GetDirectoryName(exePath)!;
        WritePayload(gameDir);
        EnsureProbingPath(gameDir);
        MigrateLegacySidecars(gameDir);
        EnsureDefaultConfig(gameDir);

        Console.WriteLine($"Done! Edit mods/{NetworkConfigFileName} to change the master server.");
    }

    public static void Repair()
    {
        string? exePath = Paths.Find(ExeName);
        if (exePath is null)
        {
            Console.Error.WriteLine($"Could not find {ExeName} - place Patcher.exe in the game folder.");
            Cli.Exit(1);
            return;
        }

        string browserBackupPath = exePath + ".browserbak";
        if (!File.Exists(browserBackupPath))
        {
            Console.Error.WriteLine("Game does not appear to be patched (no backup found). Run 'Patcher.exe patch' first.");
            Cli.Exit(1);
            return;
        }

        string gameDir = Path.GetDirectoryName(exePath)!;
        WritePayload(gameDir);
        EnsureProbingPath(gameDir);
        EnsureDefaultConfig(gameDir);

        Console.WriteLine("Repair complete. Payload files and probing path refreshed; config preserved.");
    }

    static void PatchGameExecutable(string exePath, string backup)
    {
        File.Copy(exePath, backup);

        var module = ModuleDefinition.FromFile(exePath);
        ApplyHarmonyBootstrapPatch(module);
        module.Write(exePath);

        Console.WriteLine($"Patched {ExeName} - Harmony mod loader bootstrap injected into Apotheon.Program::Main.");
    }

    static void ApplyHarmonyBootstrapPatch(ModuleDefinition module)
    {
        var program = module.GetAllTypes().FirstOrDefault(t => t.FullName == "Apotheon.Program");
        if (program is null)
            throw new InvalidOperationException("Could not find Apotheon.Program type in game executable.");

        var main = program.Methods.FirstOrDefault(m => m.Name == "Main" && m.CilMethodBody is not null);
        if (main is null)
            throw new InvalidOperationException("Could not find Apotheon.Program::Main with a CIL body.");

        var existingMarker = main.CilMethodBody!.Instructions.FirstOrDefault(i =>
            i.OpCode == CilOpCodes.Call &&
            i.Operand is IMethodDescriptor called &&
            string.Equals(called.Name?.ToString(), "Init", StringComparison.Ordinal) &&
            called.DeclaringType?.FullName == "ApotheonArena.NetworkMod.ModLoader");
        if (existingMarker is not null)
            return;

        var corlib = module.CorLibTypeFactory;
        var modAsmRef = new AssemblyReference("ApotheonArena.NetworkMod", new Version(1, 0, 0, 0));
        module.AssemblyReferences.Add(modAsmRef);

        var modLoaderType = new TypeReference(module, modAsmRef, "ApotheonArena.NetworkMod", "ModLoader");
        var initMethod = new MemberReference(modLoaderType, "Init",
            MethodSignature.CreateStatic(corlib.Void));

        main.CilMethodBody!.Instructions.Insert(0, new CilInstruction(CilOpCodes.Call, initMethod));
    }

    static void WritePayload(string gameDir)
    {
        string modsDir = Path.Combine(gameDir, ModsDirectoryName);
        Directory.CreateDirectory(modsDir);

        foreach (string resource in new[] { "0Harmony.dll", "ApotheonArena.NetworkMod.dll" })
        {
            string dest = Path.Combine(modsDir, resource);
            WriteEmbeddedPayload(resource, dest);
            Console.WriteLine($"Wrote {ModsDirectoryName}/{resource}.");
        }
    }

    static void WriteEmbeddedPayload(string resourceName, string destPath)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using Stream? stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' missing from patcher - rebuild required.");
        using var file = File.Create(destPath);
        stream.CopyTo(file);
    }

    static void EnsureProbingPath(string gameDir)
    {
        string configPath = Path.Combine(gameDir, ExeName + ".config");
        XDocument doc;
        if (File.Exists(configPath))
        {
            try
            {
                doc = XDocument.Load(configPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: {ExeName}.config is not valid XML ({ex.Message}); rewriting from scratch.");
                doc = NewDefaultConfigDoc();
            }
        }
        else
        {
            doc = NewDefaultConfigDoc();
        }

        XElement root = doc.Root ?? new XElement("configuration");
        if (doc.Root is null) doc.Add(root);

        XNamespace asmv1 = "urn:schemas-microsoft-com:asm.v1";
        XElement? runtime = root.Element("runtime");
        if (runtime is null)
        {
            runtime = new XElement("runtime");
            root.Add(runtime);
        }

        XElement? binding = runtime.Elements().FirstOrDefault(e => e.Name.LocalName == "assemblyBinding");
        if (binding is null)
        {
            binding = new XElement(asmv1 + "assemblyBinding");
            runtime.Add(binding);
        }

        bool hasProbing = binding.Elements().Any(e =>
            e.Name.LocalName == "probing" &&
            string.Equals((string?)e.Attribute("privatePath"), ModsDirectoryName, StringComparison.OrdinalIgnoreCase));
        if (!hasProbing)
        {
            XName probingName = (binding.Name.Namespace == XNamespace.None)
                ? (XName)"probing"
                : binding.Name.Namespace + "probing";
            binding.Add(new XElement(probingName, new XAttribute("privatePath", ModsDirectoryName)));
        }

        doc.Save(configPath);
        Console.WriteLine($"Ensured probing privatePath=\"{ModsDirectoryName}\" in {ExeName}.config.");
    }

    static XDocument NewDefaultConfigDoc()
    {
        return XDocument.Parse(
            "<?xml version=\"1.0\"?>" +
            "<configuration>" +
            "<startup><supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.0\"/></startup>" +
            "</configuration>");
    }

    static void MigrateLegacySidecars(string gameDir)
    {
        string legacyMaster = Path.Combine(gameDir, ConfigFileName);
        string legacyLocal = Path.Combine(gameDir, LocalHostIpFileName);
        string legacyPublic = Path.Combine(gameDir, PublicHostIpFileName);

        if (!File.Exists(legacyMaster) && !File.Exists(legacyLocal) && !File.Exists(legacyPublic))
            return;

        string master = ReadFirstContentLine(legacyMaster) ?? OriginalIp;
        string host = ReadFirstContentLine(legacyLocal) ?? string.Empty;
        string publicHost = ReadFirstContentLine(legacyPublic) ?? string.Empty;

        WriteConfig(gameDir, master, host, host, publicHost);

        foreach (string old in new[] { legacyMaster, legacyLocal, legacyPublic })
        {
            if (File.Exists(old))
            {
                File.Delete(old);
                Console.WriteLine($"Migrated and removed legacy {Path.GetFileName(old)}.");
            }
        }
    }

    static void EnsureDefaultConfig(string gameDir)
    {
        string configPath = Path.Combine(gameDir, ModsDirectoryName, NetworkConfigFileName);
        if (File.Exists(configPath)) return;
        WriteConfig(gameDir, OriginalIp, string.Empty, string.Empty, string.Empty);
        Console.WriteLine($"Created default {ModsDirectoryName}/{NetworkConfigFileName}.");
    }

    static void WriteConfig(string gameDir, string master, string hostIp, string clientIp, string publicHostIp)
    {
        string modsDir = Path.Combine(gameDir, ModsDirectoryName);
        Directory.CreateDirectory(modsDir);
        string configPath = Path.Combine(modsDir, NetworkConfigFileName);

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("networkmod",
                new XComment(" Master server host or IP. Required. "),
                new XElement("masterServer", master),
                new XComment(" LAN IP to advertise when hosting. Blank = auto-detect. "),
                new XElement("hostIp", hostIp),
                new XComment(" LAN IP to advertise when joining. Blank = auto-detect. "),
                new XElement("clientIp", clientIp),
                new XComment(" WAN endpoint override (ip or ip:port). Rare; only for hairpin NAT on LAN masters. "),
                new XElement("publicHostIp", publicHostIp),
                new XComment(" Set true to open a separate console window mirroring mod logs. "),
                new XElement("showConsole", "false")));

        doc.Save(configPath);
    }

    static string? ReadFirstContentLine(string path)
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
