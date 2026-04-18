using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.DotNet.Signatures.Types;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables.Rows;

// Patches Apotheon Arena to read the master server address from
// master_server.txt instead of using the hardcoded IP.
// Any IP or hostname of any length is supported.

const string OriginalIp     = "50.19.227.23";
const string LidgrenDllName = "Lidgren.Network.dll";
const string ExeName        = "ApotheonArena.exe";
const string ConfigFileName = "master_server.txt";
const string LocalHostIpFileName = "local_host_ip.txt";
const string PublicHostIpFileName = "public_host_ip.txt";

bool isInteractive = args.Length == 0;

if (isInteractive)
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
    PauseBeforeExit();
    return;
}

if (args.Length >= 2 && string.Equals(args[0], "inspect-type", StringComparison.OrdinalIgnoreCase))
{
    InspectType(args[1]);
    return;
}

if (args.Length >= 3 && string.Equals(args[0], "inspect-method", StringComparison.OrdinalIgnoreCase))
{
    InspectMethod(args[1], args[2]);
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
            Patch();
            return true;
        case "2":
        case "restore":
            Restore();
            return true;
        case "patch-basic":
        case "patch-noroute":
            Patch(includeRouteAwareLocalIpFix: false);
            return true;
        case "netdebug":
            SafeNetDebug();
            return true;
        case "unnetdebug":
            SafeUnNetDebug();
            return true;
        case "diagnose":
            Diagnose();
            return true;
        case "undiagnose":
            Undiagnose();
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
        ExitProcess(1);
    }
}

void ExitProcess(int code)
{
    if (isInteractive)
        PauseBeforeExit();
    Environment.Exit(code);
}

void PauseBeforeExit()
{
    Console.WriteLine();
    Console.Write("Press any key to exit...");
    try { Console.ReadKey(intercept: true); }
    catch (InvalidOperationException) { Console.ReadLine(); }
    Console.WriteLine();
}

void Patch(bool includeRouteAwareLocalIpFix = true)
{
    string? exePath = FindFile(ExeName);
    if (exePath is null)
    {
        Console.Error.WriteLine($"Could not find {ExeName} - place Patcher.exe in the game folder.");
        ExitProcess(1);
        return;
    }

    string browserBackupPath = exePath + ".browserbak";
    if (File.Exists(browserBackupPath))
    {
        Console.Error.WriteLine("Browser patch already applied. Run 'Patcher.exe restore' first to unpatch.");
        ExitProcess(1);
        return;
    }

    if (!includeRouteAwareLocalIpFix)
        Console.WriteLine("Using executable-only patch path; route-aware Lidgren fix is skipped.");

    PatchGameExecutable(exePath, browserBackupPath, includeRouteAwareLocalIpFix);

    // Create master_server.txt in the game folder if it doesn't already exist
    string gameDir = Path.GetDirectoryName(exePath)!;
    string configPath = Path.Combine(gameDir, ConfigFileName);
    if (!File.Exists(configPath))
    {
        File.WriteAllText(configPath,
            "# Apotheon Arena - Community Master Server\n" +
            "# Set the IP address or hostname of your master server below.\n" +
            "# Any length is supported. Restart the game after changing.\n" +
            $"{OriginalIp}\n");
        Console.WriteLine($"Created {ConfigFileName} in the game folder - edit it to point at your server.");
    }

    string localHostIpPath = Path.Combine(gameDir, LocalHostIpFileName);
    if (!File.Exists(localHostIpPath))
    {
        File.WriteAllText(localHostIpPath,
            "# Apotheon Arena - Local Host IP Override\n" +
            "# Optional: set the LAN/VPN IP the game should advertise when hosting.\n" +
            "# Leave blank to use Lidgren's normal adapter selection.\n" +
            "# Example:\n" +
            "# 192.168.1.50\n");
        Console.WriteLine($"Created optional {LocalHostIpFileName} in the game folder.");
    }

    string publicHostIpPath = Path.Combine(gameDir, PublicHostIpFileName);
    if (!File.Exists(publicHostIpPath))
    {
        File.WriteAllText(publicHostIpPath,
            "# Apotheon Arena - Public Host Endpoint Override\n" +
            "# Optional: set the WAN endpoint the master server should advertise\n" +
            "# for this host. Use this when the master cannot observe your real\n" +
            "# public IP (for example, when the master runs on the same LAN as\n" +
            "# the host and the router preserves the LAN source on hairpin NAT).\n" +
            "# Format: ip or ip:port  (port defaults to 14242 if omitted).\n" +
            "# Leave blank to let the master use the observed sender address.\n" +
            "# Example:\n" +
            "# 203.0.113.42:14242\n");
        Console.WriteLine($"Created optional {PublicHostIpFileName} in the game folder.");
    }

    Console.WriteLine("Done! Edit master_server.txt whenever you need to change the server.");
}

void PatchGameExecutable(string exePath, string backup, bool includeLocalHostIpOverride)
{
    File.Copy(exePath, backup);

    var module = ModuleDefinition.FromFile(exePath);
    ApplyMasterServerRedirectPatch(module, exePath);
    if (includeLocalHostIpOverride)
        ApplyGameLocalHostIpOverridePatch(module, exePath);
    ApplyAdvertisedExternalEndpointPatch(module, exePath);
    ApplyEmptyServerListMessagePatch(module);
    ApplyExceptionLogPatch(module, exePath);
    module.Write(exePath);

    Console.WriteLine("Patched ApotheonArena.exe — redirected master server lookups to master_server.txt.");
    if (includeLocalHostIpOverride)
        Console.WriteLine($"Patched ApotheonArena.exe — host registration now honors optional {LocalHostIpFileName}.");
    Console.WriteLine("Patched ApotheonArena.exe — empty server lists now show 'No games available.'");
}

void ApplyMasterServerRedirectPatch(ModuleDefinition module, string exePath)
{
    string configPathInGameDir = Path.Combine(Path.GetDirectoryName(exePath)!, ConfigFileName);
    var corlib = module.CorLibTypeFactory;
    var scope  = corlib.CorLibScope;

    var fileType   = new TypeReference(module, scope, "System.IO", "File");
    var stringType = new TypeReference(module, scope, "System", "String");

    var fileExists = new MemberReference(fileType, "Exists",
        MethodSignature.CreateStatic(corlib.Boolean, corlib.String));
    var readAllLines = new MemberReference(fileType, "ReadAllLines",
        MethodSignature.CreateStatic(new SzArrayTypeSignature(corlib.String), corlib.String));
    var strTrim = new MemberReference(stringType, "Trim",
        MethodSignature.CreateInstance(corlib.String));
    var strGetLength = new MemberReference(stringType, "get_Length",
        MethodSignature.CreateInstance(corlib.Int32));
    var strGetChars = new MemberReference(stringType, "get_Chars",
        MethodSignature.CreateInstance(corlib.Char, corlib.Int32));

    var networkType = module.TopLevelTypes.First(t => t.Namespace == "Apotheon" && t.Name == "Network");
    var helper = new MethodDefinition("__GetMasterServerHost",
        MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
        MethodSignature.CreateStatic(corlib.String));

    var body = new CilMethodBody(helper);
    var linesVar = new CilLocalVariable(new SzArrayTypeSignature(corlib.String));
    var iVar = new CilLocalVariable(corlib.Int32);
    var lineVar = new CilLocalVariable(corlib.String);
    body.LocalVariables.Add(linesVar);
    body.LocalVariables.Add(iVar);
    body.LocalVariables.Add(lineVar);

    var loopLabel = new CilInstructionLabel();
    var checkLabel = new CilInstructionLabel();
    var nextLabel = new CilInstructionLabel();
    var fallbackLabel = new CilInstructionLabel();
    var hi = body.Instructions;

    hi.Add(CilOpCodes.Ldstr, configPathInGameDir);
    hi.Add(CilOpCodes.Call, fileExists);
    hi.Add(CilOpCodes.Brfalse, fallbackLabel);
    hi.Add(CilOpCodes.Ldstr, configPathInGameDir);
    hi.Add(CilOpCodes.Call, readAllLines);
    hi.Add(CilOpCodes.Stloc, linesVar);
    hi.Add(CilOpCodes.Ldc_I4_0);
    hi.Add(CilOpCodes.Stloc, iVar);
    hi.Add(CilOpCodes.Br, checkLabel);

    var loopStart = new CilInstruction(CilOpCodes.Ldloc, linesVar);
    hi.Add(loopStart);
    loopLabel.Instruction = loopStart;
    hi.Add(CilOpCodes.Ldloc, iVar);
    hi.Add(CilOpCodes.Ldelem_Ref);
    hi.Add(CilOpCodes.Callvirt, strTrim);
    hi.Add(CilOpCodes.Stloc, lineVar);
    hi.Add(CilOpCodes.Ldloc, lineVar);
    hi.Add(CilOpCodes.Callvirt, strGetLength);
    hi.Add(CilOpCodes.Ldc_I4_0);
    hi.Add(CilOpCodes.Ble, nextLabel);
    hi.Add(CilOpCodes.Ldloc, lineVar);
    hi.Add(CilOpCodes.Ldc_I4_0);
    hi.Add(CilOpCodes.Callvirt, strGetChars);
    hi.Add(CilOpCodes.Ldc_I4_S, (sbyte)35);
    hi.Add(CilOpCodes.Beq, nextLabel);
    hi.Add(CilOpCodes.Ldloc, lineVar);
    hi.Add(CilOpCodes.Ret);

    var nextStart = new CilInstruction(CilOpCodes.Ldloc, iVar);
    hi.Add(nextStart);
    nextLabel.Instruction = nextStart;
    hi.Add(CilOpCodes.Ldc_I4_1);
    hi.Add(CilOpCodes.Add);
    hi.Add(CilOpCodes.Stloc, iVar);

    var checkStart = new CilInstruction(CilOpCodes.Ldloc, iVar);
    hi.Add(checkStart);
    checkLabel.Instruction = checkStart;
    hi.Add(CilOpCodes.Ldloc, linesVar);
    hi.Add(CilOpCodes.Ldlen);
    hi.Add(CilOpCodes.Conv_I4);
    hi.Add(CilOpCodes.Blt, loopLabel);

    var fallbackStart = new CilInstruction(CilOpCodes.Ldstr, OriginalIp);
    hi.Add(fallbackStart);
    fallbackLabel.Instruction = fallbackStart;
    hi.Add(CilOpCodes.Ret);

    helper.CilMethodBody = body;
    body.Instructions.OptimizeMacros();
    networkType.Methods.Add(helper);

    var targets = new[]
    {
        ("Apotheon", "Network", "ServerStart"),
        ("Apotheon", "Network", "ServerQuit"),
        ("Apotheon", "Network", "ServerUpdate"),
        ("Apotheon", "Network", "RequestNATIntroduction"),
        ("Apotheon", "ServerBrowser", "OnInitialize"),
    };

    int patchedSites = 0;
    foreach (var (ns, typeName, methodName) in targets)
    {
        var type = module.TopLevelTypes.First(t => t.Namespace == ns && t.Name == typeName);
        foreach (var method in type.Methods.Where(m => m.Name == methodName && m.CilMethodBody is not null))
        {
            foreach (var instr in method.CilMethodBody!.Instructions)
            {
                if (instr.OpCode == CilOpCodes.Ldstr &&
                    instr.Operand is string s &&
                    string.Equals(s, OriginalIp, StringComparison.Ordinal))
                {
                    instr.OpCode = CilOpCodes.Call;
                    instr.Operand = helper;
                    patchedSites++;
                }
            }

            method.CilMethodBody!.Instructions.OptimizeMacros();
        }
    }

    if (patchedSites == 0)
        throw new InvalidOperationException("Could not locate any master-server string call sites in ApotheonArena.exe.");
}

void ApplyGameLocalHostIpOverridePatch(ModuleDefinition module, string exePath)
{
    string localHostIpPath = Path.Combine(Path.GetDirectoryName(exePath)!, LocalHostIpFileName);
    var corlib = module.CorLibTypeFactory;
    var scope = corlib.CorLibScope;

    var fileType = new TypeReference(module, scope, "System.IO", "File");
    var stringType = new TypeReference(module, scope, "System", "String");
    var ipAddressType = new TypeReference(module, scope, "System.Net", "IPAddress");

    var fileExists = new MemberReference(fileType, "Exists",
        MethodSignature.CreateStatic(corlib.Boolean, corlib.String));
    var readAllLines = new MemberReference(fileType, "ReadAllLines",
        MethodSignature.CreateStatic(new SzArrayTypeSignature(corlib.String), corlib.String));
    var strTrim = new MemberReference(stringType, "Trim",
        MethodSignature.CreateInstance(corlib.String));
    var strGetLength = new MemberReference(stringType, "get_Length",
        MethodSignature.CreateInstance(corlib.Int32));
    var strGetChars = new MemberReference(stringType, "get_Chars",
        MethodSignature.CreateInstance(corlib.Char, corlib.Int32));

    var networkType = module.TopLevelTypes.First(t => t.Namespace == "Apotheon" && t.Name == "Network");
    var serverStart = networkType.Methods.First(m => m.Name == "ServerStart" && m.CilMethodBody is not null);

    var getMyAddressCall = serverStart.CilMethodBody!.Instructions.FirstOrDefault(i =>
        i.OpCode == CilOpCodes.Call &&
        i.Operand is IMethodDescriptor called &&
        string.Equals(called.Name?.ToString(), "GetMyAddress", StringComparison.Ordinal));

    if (getMyAddressCall?.Operand is not IMethodDescriptor getMyAddressMethod)
        throw new InvalidOperationException("Could not locate Lidgren NetUtility.GetMyAddress call in ServerStart.");

    var getMyAddressSig = (MethodSignature)getMyAddressMethod.Signature!;
    var resolveStringMethod = new MemberReference(
        (ITypeDefOrRef)getMyAddressMethod.DeclaringType!,
        "Resolve",
        MethodSignature.CreateStatic(new TypeDefOrRefSignature(ipAddressType), corlib.String));

    var readConfigHelper = networkType.Methods.FirstOrDefault(m => m.Name == "__ReadConfigLine");
    if (readConfigHelper is null)
    {
        readConfigHelper = new MethodDefinition("__ReadConfigLine",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(corlib.String, corlib.String));

        var body = new CilMethodBody(readConfigHelper);
        var linesVar = new CilLocalVariable(new SzArrayTypeSignature(corlib.String));
        var indexVar = new CilLocalVariable(corlib.Int32);
        var lineVar = new CilLocalVariable(corlib.String);
        body.LocalVariables.Add(linesVar);
        body.LocalVariables.Add(indexVar);
        body.LocalVariables.Add(lineVar);

        var loopLabel = new CilInstructionLabel();
        var checkLabel = new CilInstructionLabel();
        var nextLabel = new CilInstructionLabel();
        var hi = body.Instructions;

        hi.Add(CilOpCodes.Ldarg_0);
        hi.Add(CilOpCodes.Call, readAllLines);
        hi.Add(CilOpCodes.Stloc, linesVar);
        hi.Add(CilOpCodes.Ldc_I4_0);
        hi.Add(CilOpCodes.Stloc, indexVar);
        hi.Add(CilOpCodes.Br, checkLabel);

        var loopStart = new CilInstruction(CilOpCodes.Ldloc, linesVar);
        hi.Add(loopStart);
        loopLabel.Instruction = loopStart;
        hi.Add(CilOpCodes.Ldloc, indexVar);
        hi.Add(CilOpCodes.Ldelem_Ref);
        hi.Add(CilOpCodes.Callvirt, strTrim);
        hi.Add(CilOpCodes.Stloc, lineVar);
        hi.Add(CilOpCodes.Ldloc, lineVar);
        hi.Add(CilOpCodes.Callvirt, strGetLength);
        hi.Add(CilOpCodes.Ldc_I4_0);
        hi.Add(CilOpCodes.Ble, nextLabel);
        hi.Add(CilOpCodes.Ldloc, lineVar);
        hi.Add(CilOpCodes.Ldc_I4_0);
        hi.Add(CilOpCodes.Callvirt, strGetChars);
        hi.Add(CilOpCodes.Ldc_I4_S, (sbyte)35);
        hi.Add(CilOpCodes.Beq, nextLabel);
        hi.Add(CilOpCodes.Ldloc, lineVar);
        hi.Add(CilOpCodes.Ret);

        var nextStart = new CilInstruction(CilOpCodes.Ldloc, indexVar);
        hi.Add(nextStart);
        nextLabel.Instruction = nextStart;
        hi.Add(CilOpCodes.Ldc_I4_1);
        hi.Add(CilOpCodes.Add);
        hi.Add(CilOpCodes.Stloc, indexVar);

        var checkStart = new CilInstruction(CilOpCodes.Ldloc, indexVar);
        hi.Add(checkStart);
        checkLabel.Instruction = checkStart;
        hi.Add(CilOpCodes.Ldloc, linesVar);
        hi.Add(CilOpCodes.Ldlen);
        hi.Add(CilOpCodes.Conv_I4);
        hi.Add(CilOpCodes.Blt, loopLabel);
        hi.Add(CilOpCodes.Ldnull);
        hi.Add(CilOpCodes.Ret);

        body.Instructions.OptimizeMacros();
        readConfigHelper.CilMethodBody = body;
        networkType.Methods.Add(readConfigHelper);
    }

    var preferredIpHelper = networkType.Methods.FirstOrDefault(m => m.Name == "__GetAdvertisedLocalIp");
    if (preferredIpHelper is null)
    {
        preferredIpHelper = new MethodDefinition("__GetAdvertisedLocalIp",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(getMyAddressSig.ReturnType, getMyAddressSig.ParameterTypes.ToArray()));

        var body = new CilMethodBody(preferredIpHelper);
        var configuredHostVar = new CilLocalVariable(corlib.String);
        var configuredIpVar = new CilLocalVariable(new TypeDefOrRefSignature(ipAddressType));
        body.LocalVariables.Add(configuredHostVar);
        body.LocalVariables.Add(configuredIpVar);

        var fallbackLabel = new CilInstructionLabel();
        var hi = body.Instructions;
        hi.Add(CilOpCodes.Ldstr, localHostIpPath);
        hi.Add(CilOpCodes.Call, fileExists);
        hi.Add(CilOpCodes.Brfalse, fallbackLabel);
        hi.Add(CilOpCodes.Ldstr, localHostIpPath);
        hi.Add(CilOpCodes.Call, readConfigHelper);
        hi.Add(CilOpCodes.Stloc, configuredHostVar);
        hi.Add(CilOpCodes.Ldloc, configuredHostVar);
        hi.Add(CilOpCodes.Brfalse, fallbackLabel);
        hi.Add(CilOpCodes.Ldloc, configuredHostVar);
        hi.Add(CilOpCodes.Call, resolveStringMethod);
        hi.Add(CilOpCodes.Stloc, configuredIpVar);
        hi.Add(CilOpCodes.Ldloc, configuredIpVar);
        hi.Add(CilOpCodes.Brfalse, fallbackLabel);
        hi.Add(CilOpCodes.Ldarg_0);
        hi.Add(CilOpCodes.Ldnull);
        hi.Add(CilOpCodes.Stind_Ref);
        hi.Add(CilOpCodes.Ldloc, configuredIpVar);
        hi.Add(CilOpCodes.Ret);

        var fallbackStart = new CilInstruction(CilOpCodes.Ldarg_0);
        hi.Add(fallbackStart);
        fallbackLabel.Instruction = fallbackStart;
        hi.Add(CilOpCodes.Call, getMyAddressMethod);
        hi.Add(CilOpCodes.Ret);

        preferredIpHelper.CilMethodBody = body;
        body.Instructions.OptimizeMacros();
        networkType.Methods.Add(preferredIpHelper);
    }

    int patchedCalls = 0;
    foreach (var methodName in new[] { "ServerStart", "ServerUpdate" })
    {
        var method = networkType.Methods.First(m => m.Name == methodName && m.CilMethodBody is not null);
        var instructions = method.CilMethodBody!.Instructions;
        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            if (instr.OpCode != CilOpCodes.Call ||
                instr.Operand is not IMethodDescriptor called ||
                !string.Equals(called.Name?.ToString(), "GetMyAddress", StringComparison.Ordinal))
                continue;

            bool isServerInfoIpAssignment =
                i + 2 < instructions.Count &&
                instructions[i + 1].OpCode == CilOpCodes.Callvirt &&
                instructions[i + 1].Operand is IMethodDescriptor toStringMethod &&
                string.Equals(toStringMethod.Name?.ToString(), "ToString", StringComparison.Ordinal) &&
                instructions[i + 2].OpCode == CilOpCodes.Stfld &&
                instructions[i + 2].Operand is IFieldDescriptor field &&
                string.Equals(field.Name?.ToString(), "IPAddress", StringComparison.Ordinal) &&
                string.Equals(field.DeclaringType?.Name?.ToString(), "ServerInfo", StringComparison.Ordinal);

            if (!isServerInfoIpAssignment)
                continue;

            instr.Operand = preferredIpHelper;
            patchedCalls++;
        }

        instructions.OptimizeMacros();
    }

    if (patchedCalls == 0)
        throw new InvalidOperationException("Could not patch ServerInfo.IPAddress assignment in Apotheon.Network.");
}

void ApplyAdvertisedExternalEndpointPatch(ModuleDefinition module, string exePath)
{
    string publicHostIpPath = Path.Combine(Path.GetDirectoryName(exePath)!, PublicHostIpFileName);
    var corlib = module.CorLibTypeFactory;
    var scope = corlib.CorLibScope;

    var fileType = new TypeReference(module, scope, "System.IO", "File");
    var stringType = new TypeReference(module, scope, "System", "String");

    var fileExists = new MemberReference(fileType, "Exists",
        MethodSignature.CreateStatic(corlib.Boolean, corlib.String));
    var readAllLines = new MemberReference(fileType, "ReadAllLines",
        MethodSignature.CreateStatic(new SzArrayTypeSignature(corlib.String), corlib.String));
    var strTrim = new MemberReference(stringType, "Trim",
        MethodSignature.CreateInstance(corlib.String));
    var strGetLength = new MemberReference(stringType, "get_Length",
        MethodSignature.CreateInstance(corlib.Int32));
    var strGetChars = new MemberReference(stringType, "get_Chars",
        MethodSignature.CreateInstance(corlib.Char, corlib.Int32));

    var networkType = module.TopLevelTypes.First(t => t.Namespace == "Apotheon" && t.Name == "Network");

    // Locate the json Write(String) call in ServerStart so we can derive the
    // NetOutgoingMessage type reference and the msg local slot. The json write
    // is the first NetOutgoingMessage.Write(String) that follows a Write(Int64)
    // on the same type within the method.
    var serverStart = networkType.Methods.First(m => m.Name == "ServerStart" && m.CilMethodBody is not null);
    var (writeStringIdx, writeStringCall, _) = FindRegisterJsonWrite(serverStart);
    if (writeStringCall is null)
        throw new InvalidOperationException("Could not locate json Write(String) call in ServerStart register packet builder.");

    var writeStringMethod = (IMethodDescriptor)writeStringCall.Operand!;
    var netOutgoingMessageType = (ITypeDefOrRef)writeStringMethod.DeclaringType!;

    var readConfigHelper = networkType.Methods.FirstOrDefault(m => m.Name == "__ReadConfigLine");
    if (readConfigHelper is null)
    {
        readConfigHelper = new MethodDefinition("__ReadConfigLine",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(corlib.String, corlib.String));

        var body = new CilMethodBody(readConfigHelper);
        var linesVar = new CilLocalVariable(new SzArrayTypeSignature(corlib.String));
        var indexVar = new CilLocalVariable(corlib.Int32);
        var lineVar = new CilLocalVariable(corlib.String);
        body.LocalVariables.Add(linesVar);
        body.LocalVariables.Add(indexVar);
        body.LocalVariables.Add(lineVar);

        var loopLabel = new CilInstructionLabel();
        var checkLabel = new CilInstructionLabel();
        var nextLabel = new CilInstructionLabel();
        var hi = body.Instructions;

        hi.Add(CilOpCodes.Ldarg_0);
        hi.Add(CilOpCodes.Call, readAllLines);
        hi.Add(CilOpCodes.Stloc, linesVar);
        hi.Add(CilOpCodes.Ldc_I4_0);
        hi.Add(CilOpCodes.Stloc, indexVar);
        hi.Add(CilOpCodes.Br, checkLabel);

        var loopStart = new CilInstruction(CilOpCodes.Ldloc, linesVar);
        hi.Add(loopStart);
        loopLabel.Instruction = loopStart;
        hi.Add(CilOpCodes.Ldloc, indexVar);
        hi.Add(CilOpCodes.Ldelem_Ref);
        hi.Add(CilOpCodes.Callvirt, strTrim);
        hi.Add(CilOpCodes.Stloc, lineVar);
        hi.Add(CilOpCodes.Ldloc, lineVar);
        hi.Add(CilOpCodes.Callvirt, strGetLength);
        hi.Add(CilOpCodes.Ldc_I4_0);
        hi.Add(CilOpCodes.Ble, nextLabel);
        hi.Add(CilOpCodes.Ldloc, lineVar);
        hi.Add(CilOpCodes.Ldc_I4_0);
        hi.Add(CilOpCodes.Callvirt, strGetChars);
        hi.Add(CilOpCodes.Ldc_I4_S, (sbyte)35);
        hi.Add(CilOpCodes.Beq, nextLabel);
        hi.Add(CilOpCodes.Ldloc, lineVar);
        hi.Add(CilOpCodes.Ret);

        var nextStart = new CilInstruction(CilOpCodes.Ldloc, indexVar);
        hi.Add(nextStart);
        nextLabel.Instruction = nextStart;
        hi.Add(CilOpCodes.Ldc_I4_1);
        hi.Add(CilOpCodes.Add);
        hi.Add(CilOpCodes.Stloc, indexVar);

        var checkStart = new CilInstruction(CilOpCodes.Ldloc, indexVar);
        hi.Add(checkStart);
        checkLabel.Instruction = checkStart;
        hi.Add(CilOpCodes.Ldloc, linesVar);
        hi.Add(CilOpCodes.Ldlen);
        hi.Add(CilOpCodes.Conv_I4);
        hi.Add(CilOpCodes.Blt, loopLabel);
        hi.Add(CilOpCodes.Ldnull);
        hi.Add(CilOpCodes.Ret);

        body.Instructions.OptimizeMacros();
        readConfigHelper.CilMethodBody = body;
        networkType.Methods.Add(readConfigHelper);
    }

    var writeHelper = networkType.Methods.FirstOrDefault(m => m.Name == "__WriteAdvertisedExternalEndpoint");
    if (writeHelper is null)
    {
        writeHelper = new MethodDefinition("__WriteAdvertisedExternalEndpoint",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(corlib.Void, new TypeDefOrRefSignature(netOutgoingMessageType)));

        var body = new CilMethodBody(writeHelper);
        var rawVar = new CilLocalVariable(corlib.String);
        body.LocalVariables.Add(rawVar);

        var emptyLabel = new CilInstructionLabel();
        var doWriteLabel = new CilInstructionLabel();
        var hi = body.Instructions;

        hi.Add(CilOpCodes.Ldstr, publicHostIpPath);
        hi.Add(CilOpCodes.Call, fileExists);
        hi.Add(CilOpCodes.Brfalse, emptyLabel);
        hi.Add(CilOpCodes.Ldstr, publicHostIpPath);
        hi.Add(CilOpCodes.Call, readConfigHelper);
        hi.Add(CilOpCodes.Stloc, rawVar);
        hi.Add(CilOpCodes.Ldloc, rawVar);
        hi.Add(CilOpCodes.Brtrue, doWriteLabel);

        var emptyStart = new CilInstruction(CilOpCodes.Ldstr, string.Empty);
        hi.Add(emptyStart);
        emptyLabel.Instruction = emptyStart;
        hi.Add(CilOpCodes.Stloc, rawVar);

        var doWriteStart = new CilInstruction(CilOpCodes.Ldarg_0);
        hi.Add(doWriteStart);
        doWriteLabel.Instruction = doWriteStart;
        hi.Add(CilOpCodes.Ldloc, rawVar);
        hi.Add(CilOpCodes.Callvirt, writeStringMethod);
        hi.Add(CilOpCodes.Ret);

        writeHelper.CilMethodBody = body;
        body.Instructions.OptimizeMacros();
        networkType.Methods.Add(writeHelper);
    }

    int injectedCount = 0;
    foreach (var methodName in new[] { "ServerStart", "ServerUpdate" })
    {
        var method = networkType.Methods.First(m => m.Name == methodName && m.CilMethodBody is not null);
        var (idx, call, msgLocal) = FindRegisterJsonWrite(method);
        if (idx < 0 || call is null || msgLocal is null)
            throw new InvalidOperationException($"Could not find json Write(String) call in {methodName} register packet builder.");

        var instructions = method.CilMethodBody!.Instructions;
        instructions.Insert(idx + 1, new CilInstruction(CilOpCodes.Ldloc, msgLocal));
        instructions.Insert(idx + 2, new CilInstruction(CilOpCodes.Call, writeHelper));
        instructions.OptimizeMacros();
        injectedCount++;
    }

    if (injectedCount == 0)
        throw new InvalidOperationException("Could not inject advertised-external-endpoint writer into any method.");
}

// Wraps an existing method body in a try/catch so exceptions thrown while the
// injected helper runs cannot crash the host game. The caller has already
// appended the happy-path IL; this rewrites every Ret into a Leave to a shared
// end label and appends a catch handler that pops the exception, emits the
// fallback value (if any), and falls through to the same end label.
void WrapMethodBodyInTryCatch(
    MethodDefinition method,
    ModuleDefinition module,
    Action<CilInstructionCollection> emitFallback)
{
    var body = method.CilMethodBody!;
    var instructions = body.Instructions;
    if (instructions.Count == 0)
        return;

    var sig = (MethodSignature)method.Signature!;
    var returnType = sig.ReturnType;
    bool isVoid = string.Equals(returnType.FullName, "System.Void", StringComparison.Ordinal);

    CilLocalVariable? resultVar = null;
    if (!isVoid)
    {
        resultVar = new CilLocalVariable(returnType);
        body.LocalVariables.Add(resultVar);
    }

    var endLabel = new CilInstructionLabel();

    for (int i = instructions.Count - 1; i >= 0; i--)
    {
        var ins = instructions[i];
        if (ins.OpCode != CilOpCodes.Ret) continue;

        if (isVoid)
        {
            ins.OpCode = CilOpCodes.Leave;
            ins.Operand = endLabel;
        }
        else
        {
            ins.OpCode = CilOpCodes.Stloc;
            ins.Operand = resultVar;
            instructions.Insert(i + 1, new CilInstruction(CilOpCodes.Leave, endLabel));
        }
    }

    var tryStartInstr = instructions[0];
    int handlerStartIdx = instructions.Count;

    instructions.Add(new CilInstruction(CilOpCodes.Pop));
    emitFallback(instructions);
    if (!isVoid)
        instructions.Add(new CilInstruction(CilOpCodes.Stloc, resultVar));
    instructions.Add(new CilInstruction(CilOpCodes.Leave, endLabel));

    CilInstruction endInstr;
    if (isVoid)
    {
        endInstr = new CilInstruction(CilOpCodes.Ret);
        instructions.Add(endInstr);
    }
    else
    {
        endInstr = new CilInstruction(CilOpCodes.Ldloc, resultVar);
        instructions.Add(endInstr);
        instructions.Add(new CilInstruction(CilOpCodes.Ret));
    }
    endLabel.Instruction = endInstr;

    var handlerStartInstr = instructions[handlerStartIdx];
    var scope = module.CorLibTypeFactory.CorLibScope;
    var exceptionType = new TypeReference(module, scope, "System", "Exception");

    body.ExceptionHandlers.Add(new CilExceptionHandler
    {
        HandlerType = CilExceptionHandlerType.Exception,
        TryStart = new CilInstructionLabel(tryStartInstr),
        TryEnd = new CilInstructionLabel(handlerStartInstr),
        HandlerStart = new CilInstructionLabel(handlerStartInstr),
        HandlerEnd = new CilInstructionLabel(endInstr),
        ExceptionType = exceptionType,
    });
}

(int Index, CilInstruction? Call, CilLocalVariable? MsgLocal) FindRegisterJsonWrite(MethodDefinition method)
{
    var body = method.CilMethodBody!;
    var instructions = body.Instructions;
    bool sawWriteInt64 = false;

    for (int i = 0; i < instructions.Count; i++)
    {
        var instr = instructions[i];
        if (instr.OpCode != CilOpCodes.Callvirt && instr.OpCode != CilOpCodes.Call)
            continue;
        if (instr.Operand is not IMethodDescriptor called)
            continue;
        if (!string.Equals(called.Name?.ToString(), "Write", StringComparison.Ordinal))
            continue;
        if (!string.Equals(called.DeclaringType?.Name?.ToString(), "NetOutgoingMessage", StringComparison.Ordinal))
            continue;
        if (called.Signature is not MethodSignature sig || sig.ParameterTypes.Count != 1)
            continue;

        var paramFullName = sig.ParameterTypes[0].FullName;
        if (string.Equals(paramFullName, "System.Int64", StringComparison.Ordinal))
        {
            sawWriteInt64 = true;
            continue;
        }

        if (!sawWriteInt64)
            continue;

        if (!string.Equals(paramFullName, "System.String", StringComparison.Ordinal))
            continue;

        CilLocalVariable? msgLocal = null;
        if (i >= 2)
            msgLocal = ResolveLdlocLocal(instructions[i - 2], body);

        return (i, instr, msgLocal);
    }

    return (-1, null, null);
}

CilLocalVariable? ResolveLdlocLocal(CilInstruction instr, CilMethodBody body)
{
    if (instr.Operand is CilLocalVariable v)
        return v;

    int index = instr.OpCode.Code switch
    {
        CilCode.Ldloc_0 => 0,
        CilCode.Ldloc_1 => 1,
        CilCode.Ldloc_2 => 2,
        CilCode.Ldloc_3 => 3,
        _ => -1,
    };

    return index >= 0 && index < body.LocalVariables.Count
        ? body.LocalVariables[index]
        : null;
}

void PatchSafeLocalHostIpOverride()
{
    const string BackupSuffix = ".localipbak";

    string? dllPath = FindFile(LidgrenDllName);
    if (dllPath is null)
    {
        Console.Error.WriteLine($"Could not find {LidgrenDllName}.");
        ExitProcess(1);
        return;
    }

    string backupPath = dllPath + BackupSuffix;
    if (File.Exists(backupPath))
        return;

    File.Copy(dllPath, backupPath);

    string localHostIpPath = Path.Combine(Path.GetDirectoryName(dllPath)!, LocalHostIpFileName);
    var module = ModuleDefinition.FromFile(dllPath);
    var corlib = module.CorLibTypeFactory;
    var scope = corlib.CorLibScope;

    var fileType = new TypeReference(module, scope, "System.IO", "File");
    var stringType = new TypeReference(module, scope, "System", "String");
    var netUtility = module.TopLevelTypes.First(t => t.Name == "NetUtility");

    var fileExists = new MemberReference(fileType, "Exists",
        MethodSignature.CreateStatic(corlib.Boolean, corlib.String));
    var readAllLines = new MemberReference(fileType, "ReadAllLines",
        MethodSignature.CreateStatic(new SzArrayTypeSignature(corlib.String), corlib.String));
    var strTrim = new MemberReference(stringType, "Trim",
        MethodSignature.CreateInstance(corlib.String));
    var strGetLength = new MemberReference(stringType, "get_Length",
        MethodSignature.CreateInstance(corlib.Int32));
    var strGetChars = new MemberReference(stringType, "get_Chars",
        MethodSignature.CreateInstance(corlib.Char, corlib.Int32));

    var readConfigHelper = netUtility.Methods.FirstOrDefault(m => m.Name == "__ReadConfigLine");
    if (readConfigHelper is null)
    {
        readConfigHelper = new MethodDefinition("__ReadConfigLine",
            MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(corlib.String, corlib.String));

        var body = new CilMethodBody(readConfigHelper);
        var linesVar = new CilLocalVariable(new SzArrayTypeSignature(corlib.String));
        var indexVar = new CilLocalVariable(corlib.Int32);
        var lineVar = new CilLocalVariable(corlib.String);
        body.LocalVariables.Add(linesVar);
        body.LocalVariables.Add(indexVar);
        body.LocalVariables.Add(lineVar);

        var loopLabel = new CilInstructionLabel();
        var checkLabel = new CilInstructionLabel();
        var nextLabel = new CilInstructionLabel();
        var hi = body.Instructions;

        hi.Add(CilOpCodes.Ldarg_0);
        hi.Add(CilOpCodes.Call, readAllLines);
        hi.Add(CilOpCodes.Stloc, linesVar);
        hi.Add(CilOpCodes.Ldc_I4_0);
        hi.Add(CilOpCodes.Stloc, indexVar);
        hi.Add(CilOpCodes.Br, checkLabel);

        var loopStart = new CilInstruction(CilOpCodes.Ldloc, linesVar);
        hi.Add(loopStart);
        loopLabel.Instruction = loopStart;
        hi.Add(CilOpCodes.Ldloc, indexVar);
        hi.Add(CilOpCodes.Ldelem_Ref);
        hi.Add(CilOpCodes.Callvirt, strTrim);
        hi.Add(CilOpCodes.Stloc, lineVar);
        hi.Add(CilOpCodes.Ldloc, lineVar);
        hi.Add(CilOpCodes.Callvirt, strGetLength);
        hi.Add(CilOpCodes.Ldc_I4_0);
        hi.Add(CilOpCodes.Ble, nextLabel);
        hi.Add(CilOpCodes.Ldloc, lineVar);
        hi.Add(CilOpCodes.Ldc_I4_0);
        hi.Add(CilOpCodes.Callvirt, strGetChars);
        hi.Add(CilOpCodes.Ldc_I4_S, (sbyte)35);
        hi.Add(CilOpCodes.Beq, nextLabel);
        hi.Add(CilOpCodes.Ldloc, lineVar);
        hi.Add(CilOpCodes.Ret);

        var nextStart = new CilInstruction(CilOpCodes.Ldloc, indexVar);
        hi.Add(nextStart);
        nextLabel.Instruction = nextStart;
        hi.Add(CilOpCodes.Ldc_I4_1);
        hi.Add(CilOpCodes.Add);
        hi.Add(CilOpCodes.Stloc, indexVar);

        var checkStart = new CilInstruction(CilOpCodes.Ldloc, indexVar);
        hi.Add(checkStart);
        checkLabel.Instruction = checkStart;
        hi.Add(CilOpCodes.Ldloc, linesVar);
        hi.Add(CilOpCodes.Ldlen);
        hi.Add(CilOpCodes.Conv_I4);
        hi.Add(CilOpCodes.Blt, loopLabel);
        hi.Add(CilOpCodes.Ldnull);
        hi.Add(CilOpCodes.Ret);

        body.Instructions.OptimizeMacros();
        readConfigHelper.CilMethodBody = body;
        netUtility.Methods.Add(readConfigHelper);
    }

    var resolveStringMethod = netUtility.Methods.First(m =>
        m.Name == "Resolve" &&
        m.Parameters.Count == 1 &&
        m.Signature is not null &&
        m.Signature.ReturnType.IsTypeOf("System.Net", "IPAddress"));

    var preferredIpHelper = netUtility.Methods.FirstOrDefault(m => m.Name == "__GetPreferredLocalIp");
    if (preferredIpHelper is null)
    {
        var ipAddressType = new TypeReference(module, scope, "System.Net", "IPAddress");
        preferredIpHelper = new MethodDefinition("__GetPreferredLocalIp",
            MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(new TypeDefOrRefSignature(ipAddressType), new TypeDefOrRefSignature(ipAddressType)));

        var body = new CilMethodBody(preferredIpHelper);
        var configuredHostVar = new CilLocalVariable(corlib.String);
        var configuredIpVar = new CilLocalVariable(new TypeDefOrRefSignature(ipAddressType));
        body.LocalVariables.Add(configuredHostVar);
        body.LocalVariables.Add(configuredIpVar);

        var fallbackLabel = new CilInstructionLabel();
        var hi = body.Instructions;
        hi.Add(CilOpCodes.Ldstr, localHostIpPath);
        hi.Add(CilOpCodes.Call, fileExists);
        hi.Add(CilOpCodes.Brfalse, fallbackLabel);
        hi.Add(CilOpCodes.Ldstr, localHostIpPath);
        hi.Add(CilOpCodes.Call, readConfigHelper);
        hi.Add(CilOpCodes.Stloc, configuredHostVar);
        hi.Add(CilOpCodes.Ldloc, configuredHostVar);
        hi.Add(CilOpCodes.Brfalse, fallbackLabel);
        hi.Add(CilOpCodes.Ldloc, configuredHostVar);
        hi.Add(CilOpCodes.Call, resolveStringMethod);
        hi.Add(CilOpCodes.Stloc, configuredIpVar);
        hi.Add(CilOpCodes.Ldloc, configuredIpVar);
        hi.Add(CilOpCodes.Brfalse, fallbackLabel);
        hi.Add(CilOpCodes.Ldloc, configuredIpVar);
        hi.Add(CilOpCodes.Ret);

        var fallbackStart = new CilInstruction(CilOpCodes.Ldarg_0);
        hi.Add(fallbackStart);
        fallbackLabel.Instruction = fallbackStart;
        hi.Add(CilOpCodes.Ret);

        body.Instructions.OptimizeMacros();
        preferredIpHelper.CilMethodBody = body;
        netUtility.Methods.Add(preferredIpHelper);
    }

    var getMyAddressMethod = netUtility.Methods.FirstOrDefault(m =>
        m.Name == "GetMyAddress" && m.CilMethodBody is not null);

    if (getMyAddressMethod is not null)
    {
        var instructions = getMyAddressMethod.CilMethodBody!.Instructions;
        var retIndexes = Enumerable.Range(0, instructions.Count)
            .Where(i => instructions[i].OpCode == CilOpCodes.Ret)
            .OrderByDescending(i => i)
            .ToList();

        foreach (int retIndex in retIndexes)
            instructions.Insert(retIndex, new CilInstruction(CilOpCodes.Call, preferredIpHelper));

        getMyAddressMethod.CilMethodBody!.Instructions.OptimizeMacros();
    }

    module.Write(dllPath);
    Console.WriteLine($"Patched {LidgrenDllName} - added optional {LocalHostIpFileName} override to NetUtility.GetMyAddress.");
}

void PatchLidgren(string path, string backup, bool includeRouteAwareLocalIpFix)
{
    File.Copy(path, backup);

    var module = ModuleDefinition.FromFile(path);
    var corlib = module.CorLibTypeFactory;
    var scope  = corlib.CorLibScope;
    var configPathInGameDir = Path.Combine(Path.GetDirectoryName(path)!, ConfigFileName);

    // ---- type references ---------------------------------------------------
    var fileType        = new TypeReference(module, scope, "System.IO", "File");
    var stringType      = new TypeReference(module, scope, "System",    "String");
    var dateTimeType    = new TypeReference(module, scope, "System",    "DateTime");
    var dateTimeSig     = new TypeDefOrRefSignature(dateTimeType);
    string logsDirPath  = Path.Combine(Path.GetDirectoryName(path)!, "Logs");
    string networkLogPath = Path.Combine(logsDirPath, "network_debug.log");
    Directory.CreateDirectory(logsDirPath);

    // ---- method references -------------------------------------------------
    var fileExists    = new MemberReference(fileType, "Exists",
        MethodSignature.CreateStatic(corlib.Boolean, corlib.String));
    var appendAllText = new MemberReference(fileType, "AppendAllText",
        MethodSignature.CreateStatic(corlib.Void, corlib.String, corlib.String));
    var strConcat2    = new MemberReference(stringType, "Concat",
        MethodSignature.CreateStatic(corlib.String, corlib.String, corlib.String));
    var readAllLines  = new MemberReference(fileType, "ReadAllLines",
        MethodSignature.CreateStatic(new SzArrayTypeSignature(corlib.String), corlib.String));
    var strConcat3    = new MemberReference(stringType, "Concat",
        MethodSignature.CreateStatic(corlib.String, corlib.String, corlib.String, corlib.String));
    var strEquals     = new MemberReference(stringType,    "op_Equality",
        MethodSignature.CreateStatic(corlib.Boolean, corlib.String, corlib.String));
    var strTrim       = new MemberReference(stringType,    "Trim",
        MethodSignature.CreateInstance(corlib.String));
    var strGetLength  = new MemberReference(stringType,    "get_Length",
        MethodSignature.CreateInstance(corlib.Int32));
    var strGetChars   = new MemberReference(stringType,    "get_Chars",
        MethodSignature.CreateInstance(corlib.Char, corlib.Int32));
    var getUtcNow     = new MemberReference(dateTimeType, "get_UtcNow",
        MethodSignature.CreateStatic(dateTimeSig));
    var dateTimeFmt   = new MemberReference(dateTimeType, "ToString",
        MethodSignature.CreateInstance(corlib.String, corlib.String));

    // ---- inject helper: static string __ReadServerIp(string path) ----------
    // Reads lines from path, skips blank lines and lines starting with '#',
    // returns the first valid line, or null if none found.
    var netUtility = module.TopLevelTypes.First(t => t.Name == "NetUtility");

    var logHelper   = new MethodDefinition("__NetDebugLog",
        MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
        MethodSignature.CreateStatic(corlib.Void, corlib.String));

    var lBody       = new CilMethodBody(logHelper);
    var tsVar       = new CilLocalVariable(corlib.String);
    var lineVar     = new CilLocalVariable(corlib.String);
    lBody.LocalVariables.Add(tsVar);
    lBody.LocalVariables.Add(lineVar);

    var li          = lBody.Instructions;
    li.Add(CilOpCodes.Call,     getUtcNow);
    li.Add(CilOpCodes.Ldstr,    "yyyy-MM-dd HH:mm:ss.fff'Z' ");
    li.Add(CilOpCodes.Call,     dateTimeFmt);
    li.Add(CilOpCodes.Stloc,    tsVar);
    li.Add(CilOpCodes.Ldloc,    tsVar);
    li.Add(CilOpCodes.Ldarg_0);
    li.Add(CilOpCodes.Call,     strConcat2);
    li.Add(CilOpCodes.Stloc,    lineVar);
    li.Add(CilOpCodes.Ldstr,    networkLogPath);
    li.Add(CilOpCodes.Ldloc,    lineVar);
    li.Add(CilOpCodes.Call,     appendAllText);
    li.Add(CilOpCodes.Ret);

    lBody.Instructions.OptimizeMacros();
    logHelper.CilMethodBody = lBody;
    netUtility.Methods.Add(logHelper);

    var helper     = new MethodDefinition("__ReadServerIp",
        MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
        MethodSignature.CreateStatic(corlib.String, corlib.String));

    var hBody      = new CilMethodBody(helper);
    var linesVar   = new CilLocalVariable(new SzArrayTypeSignature(corlib.String));
    var iVar       = new CilLocalVariable(corlib.Int32);
    var tVar       = new CilLocalVariable(corlib.String);
    hBody.LocalVariables.Add(linesVar);
    hBody.LocalVariables.Add(iVar);
    hBody.LocalVariables.Add(tVar);

    var loopLabel  = new CilInstructionLabel();
    var checkLabel = new CilInstructionLabel();
    var nextLabel  = new CilInstructionLabel();
    var hi         = hBody.Instructions;

    // lines = File.ReadAllLines(path)
    hi.Add(CilOpCodes.Ldarg_0);
    hi.Add(CilOpCodes.Call,     readAllLines);
    hi.Add(CilOpCodes.Stloc,    linesVar);
    // i = 0
    hi.Add(CilOpCodes.Ldc_I4_0);
    hi.Add(CilOpCodes.Stloc,    iVar);
    // goto check
    hi.Add(CilOpCodes.Br,       checkLabel);

    // loop body: t = lines[i].Trim()
    var loopStart = new CilInstruction(CilOpCodes.Ldloc, linesVar);
    hi.Add(loopStart);
    loopLabel.Instruction = loopStart;
    hi.Add(CilOpCodes.Ldloc,    iVar);
    hi.Add(CilOpCodes.Ldelem_Ref);
    hi.Add(CilOpCodes.Callvirt, strTrim);
    hi.Add(CilOpCodes.Stloc,    tVar);

    // if (t.Length <= 0) goto next
    hi.Add(CilOpCodes.Ldloc,    tVar);
    hi.Add(CilOpCodes.Callvirt, strGetLength);
    hi.Add(CilOpCodes.Ldc_I4_0);
    hi.Add(CilOpCodes.Ble,      nextLabel);

    // if (t[0] == '#') goto next
    hi.Add(CilOpCodes.Ldloc,    tVar);
    hi.Add(CilOpCodes.Ldc_I4_0);
    hi.Add(CilOpCodes.Callvirt, strGetChars);
    hi.Add(CilOpCodes.Ldc_I4_S, (sbyte)35); // '#'
    hi.Add(CilOpCodes.Beq,      nextLabel);

    // __NetDebugLog("[master] " + t + "\n")
    hi.Add(CilOpCodes.Ldstr,    "[master] ");
    hi.Add(CilOpCodes.Ldloc,    tVar);
    hi.Add(CilOpCodes.Ldstr,    "\n");
    hi.Add(CilOpCodes.Call,     strConcat3);
    hi.Add(CilOpCodes.Call,     logHelper);
    // return t
    hi.Add(CilOpCodes.Ldloc,    tVar);
    hi.Add(CilOpCodes.Ret);

    // next: i++
    var nextStart = new CilInstruction(CilOpCodes.Ldloc, iVar);
    hi.Add(nextStart);
    nextLabel.Instruction = nextStart;
    hi.Add(CilOpCodes.Ldc_I4_1);
    hi.Add(CilOpCodes.Add);
    hi.Add(CilOpCodes.Stloc,    iVar);

    // check: if (i < lines.Length) goto loop
    var checkStart = new CilInstruction(CilOpCodes.Ldloc, iVar);
    hi.Add(checkStart);
    checkLabel.Instruction = checkStart;
    hi.Add(CilOpCodes.Ldloc,    linesVar);
    hi.Add(CilOpCodes.Ldlen);
    hi.Add(CilOpCodes.Conv_I4);
    hi.Add(CilOpCodes.Blt,      loopLabel);

    // return null
    hi.Add(CilOpCodes.Ldnull);
    hi.Add(CilOpCodes.Ret);

    hBody.Instructions.OptimizeMacros();
    helper.CilMethodBody = hBody;
    netUtility.Methods.Add(helper);

    // ---- patch Resolve(string, int32) to call the helper -------------------
    var resolve  = netUtility.Methods.First(m =>
        m.Name == "Resolve" &&
        m.Parameters.Count == 2 &&
        m.Parameters[1].ParameterType.IsTypeOf("System", "Int32"));

    var body     = resolve.CilMethodBody!;
    var pathVar  = new CilLocalVariable(corlib.String);
    var resultVar = new CilLocalVariable(corlib.String);
    body.LocalVariables.Add(pathVar);
    body.LocalVariables.Add(resultVar);

    var skipLabel = new CilInstructionLabel();
    skipLabel.Instruction = body.Instructions[0];

    var prefix = new List<CilInstruction>
    {
        // if (ipOrHost == null) goto original
        new(CilOpCodes.Ldarg,    resolve.Parameters[0]),
        new(CilOpCodes.Brfalse,  skipLabel),

        // if (ipOrHost.Trim() != "50.19.227.23") goto original
        new(CilOpCodes.Ldarg,    resolve.Parameters[0]),
        new(CilOpCodes.Callvirt, strTrim),
        new(CilOpCodes.Ldstr,    OriginalIp),
        new(CilOpCodes.Call,     strEquals),
        new(CilOpCodes.Brfalse,  skipLabel),

        // configPath = "<absolute path to game folder>/master_server.txt"
        new(CilOpCodes.Ldstr,    configPathInGameDir),
        new(CilOpCodes.Stloc,    pathVar),

        // if (!File.Exists(configPath)) goto original
        new(CilOpCodes.Ldloc,    pathVar),
        new(CilOpCodes.Call,     fileExists),
        new(CilOpCodes.Brfalse,  skipLabel),

        // result = __ReadServerIp(configPath)
        new(CilOpCodes.Ldloc,    pathVar),
        new(CilOpCodes.Call,     helper),
        new(CilOpCodes.Stloc,    resultVar),

        // if (result == null) goto original
        new(CilOpCodes.Ldloc,    resultVar),
        new(CilOpCodes.Brfalse,  skipLabel),

        // ipOrHost = result
        new(CilOpCodes.Ldloc,    resultVar),
        new(CilOpCodes.Starg,    resolve.Parameters[0]),
    };

    for (int i = 0; i < prefix.Count; i++)
        body.Instructions.Insert(i, prefix[i]);

    body.Instructions.OptimizeMacros();

    // ---- Inject __GetBestLocalIp: prefer the route Windows would actually use ---
    // This makes VPN/Tailscale work when they are the real route to the master
    // server, while still falling back to a safer IPv4 scan if needed.
    var sysRef = module.AssemblyReferences.FirstOrDefault(r => r.Name == "System");
    if (includeRouteAwareLocalIpFix && sysRef is not null)
    {
        var ipAddrRef    = new TypeReference(module, sysRef, "System.Net", "IPAddress");
        var dnsRef       = new TypeReference(module, sysRef, "System.Net", "Dns");
        var endPointRef  = new TypeReference(module, sysRef, "System.Net", "EndPoint");
        var ipEpRef      = new TypeReference(module, sysRef, "System.Net", "IPEndPoint");
        var socketRef    = new TypeReference(module, sysRef, "System.Net.Sockets", "Socket");
        var afRef        = new TypeReference(module, sysRef, "System.Net.Sockets", "AddressFamily");
        var stRef        = new TypeReference(module, sysRef, "System.Net.Sockets", "SocketType");
        var ptRef        = new TypeReference(module, sysRef, "System.Net.Sockets", "ProtocolType");
        var ipAddrSig    = new TypeDefOrRefSignature(ipAddrRef);
        var ipAddrArrSig = new SzArrayTypeSignature(ipAddrSig);
        var byteArrSig   = new SzArrayTypeSignature(corlib.Byte);
        var endPointSig  = new TypeDefOrRefSignature(endPointRef);
        var ipEpSig      = new TypeDefOrRefSignature(ipEpRef);
        var socketSig    = new TypeDefOrRefSignature(socketRef);
        var afSig        = new TypeDefOrRefSignature(afRef);
        var stSig        = new TypeDefOrRefSignature(stRef);
        var ptSig        = new TypeDefOrRefSignature(ptRef);

        var getAddrBytes = new MemberReference(ipAddrRef, "GetAddressBytes",
            MethodSignature.CreateInstance(byteArrSig));
        var dnsHostName  = new MemberReference(dnsRef, "GetHostName",
            MethodSignature.CreateStatic(corlib.String));
        var dnsHostAddrs = new MemberReference(dnsRef, "GetHostAddresses",
            MethodSignature.CreateStatic(ipAddrArrSig, corlib.String));
        var socketCtor   = new MemberReference(socketRef, ".ctor",
            MethodSignature.CreateInstance(corlib.Void, afSig, stSig, ptSig));
        var socketConnect = new MemberReference(socketRef, "Connect",
            MethodSignature.CreateInstance(corlib.Void, endPointSig));
        var socketGetLocalEndPoint = new MemberReference(socketRef, "get_LocalEndPoint",
            MethodSignature.CreateInstance(endPointSig));
        var socketClose = new MemberReference(socketRef, "Close",
            MethodSignature.CreateInstance(corlib.Void));
        var ipEpCtor = new MemberReference(ipEpRef, ".ctor",
            MethodSignature.CreateInstance(corlib.Void, ipAddrSig, corlib.Int32));
        var ipEpGetAddress = new MemberReference(ipEpRef, "get_Address",
            MethodSignature.CreateInstance(ipAddrSig));

        var bestIpHelper = new MethodDefinition("__GetBestLocalIp",
            MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(ipAddrSig, ipAddrSig));

        var bipBody = new CilMethodBody(bestIpHelper);
        var routeHost = new CilLocalVariable(corlib.String);
        var bBytes  = new CilLocalVariable(byteArrSig);
        var bAddrs  = new CilLocalVariable(ipAddrArrSig);
        var bIdx    = new CilLocalVariable(corlib.Int32);
        var bAddr   = new CilLocalVariable(ipAddrSig);
        var bC      = new CilLocalVariable(byteArrSig);
        var bSocket = new CilLocalVariable(socketSig);
        var bLocalEp = new CilLocalVariable(ipEpSig);
        bipBody.LocalVariables.Add(bBytes);
        bipBody.LocalVariables.Add(bAddrs);
        bipBody.LocalVariables.Add(bIdx);
        bipBody.LocalVariables.Add(bAddr);
        bipBody.LocalVariables.Add(bC);
        bipBody.LocalVariables.Add(routeHost);
        bipBody.LocalVariables.Add(bSocket);
        bipBody.LocalVariables.Add(bLocalEp);

        var routeLoopLbl = new CilInstructionLabel();
        var routeCheckLbl = new CilInstructionLabel();
        var routeNextLbl = new CilInstructionLabel();
        var routeHostReadyLbl = new CilInstructionLabel();
        var routeFoundLbl = new CilInstructionLabel();
        var scanLoopLbl    = new CilInstructionLabel();
        var scanCheckLbl   = new CilInstructionLabel();
        var scanNextLbl    = new CilInstructionLabel();
        var retAddrLbl = new CilInstructionLabel();
        var retOrigLbl = new CilInstructionLabel();
        var bi         = bipBody.Instructions;

        // routeHost = __ReadServerIp(configPath) ?? OriginalIp
        bi.Add(CilOpCodes.Ldstr,    configPathInGameDir);
        bi.Add(CilOpCodes.Call,     helper);
        bi.Add(CilOpCodes.Stloc,    routeHost);
        bi.Add(CilOpCodes.Ldloc,    routeHost);
        bi.Add(CilOpCodes.Brtrue,   routeHostReadyLbl);
        bi.Add(CilOpCodes.Ldstr,    OriginalIp);
        bi.Add(CilOpCodes.Stloc,    routeHost);
        var routeHostReadyStart = new CilInstruction(CilOpCodes.Ldloc, routeHost);
        bi.Add(routeHostReadyStart); routeHostReadyLbl.Instruction = routeHostReadyStart;
        bi.Add(CilOpCodes.Call,     dnsHostAddrs);
        bi.Add(CilOpCodes.Stloc,    bAddrs);
        // i = 0; goto routeCheck
        bi.Add(CilOpCodes.Ldc_I4_0);
        bi.Add(CilOpCodes.Stloc,    bIdx);
        bi.Add(CilOpCodes.Br,       routeCheckLbl);

        // route loop: addr = addrs[i]
        var routeLoopStart = new CilInstruction(CilOpCodes.Ldloc, bAddrs);
        bi.Add(routeLoopStart); routeLoopLbl.Instruction = routeLoopStart;
        bi.Add(CilOpCodes.Ldloc,    bIdx);
        bi.Add(CilOpCodes.Ldelem_Ref);
        bi.Add(CilOpCodes.Stloc,    bAddr);
        // c = addr.GetAddressBytes(); skip non-IPv4
        bi.Add(CilOpCodes.Ldloc,    bAddr);
        bi.Add(CilOpCodes.Callvirt, getAddrBytes);
        bi.Add(CilOpCodes.Stloc,    bC);
        bi.Add(CilOpCodes.Ldloc,    bC);
        bi.Add(CilOpCodes.Ldlen);
        bi.Add(CilOpCodes.Conv_I4);
        bi.Add(CilOpCodes.Ldc_I4,   4);
        bi.Add(CilOpCodes.Bne_Un,   routeNextLbl);
        // socket = new Socket(InterNetwork, Dgram, Udp)
        bi.Add(CilOpCodes.Ldc_I4,   2);  // AddressFamily.InterNetwork
        bi.Add(CilOpCodes.Ldc_I4,   2);  // SocketType.Dgram
        bi.Add(CilOpCodes.Ldc_I4,   17); // ProtocolType.Udp
        bi.Add(CilOpCodes.Newobj,   socketCtor);
        bi.Add(CilOpCodes.Stloc,    bSocket);
        // socket.Connect(new IPEndPoint(addr, 14343))
        bi.Add(CilOpCodes.Ldloc,    bSocket);
        bi.Add(CilOpCodes.Ldloc,    bAddr);
        bi.Add(CilOpCodes.Ldc_I4,   14343);
        bi.Add(CilOpCodes.Newobj,   ipEpCtor);
        bi.Add(CilOpCodes.Callvirt, socketConnect);
        // localEp = (IPEndPoint)socket.LocalEndPoint; socket.Close()
        bi.Add(CilOpCodes.Ldloc,    bSocket);
        bi.Add(CilOpCodes.Callvirt, socketGetLocalEndPoint);
        bi.Add(CilOpCodes.Castclass, ipEpRef);
        bi.Add(CilOpCodes.Stloc,    bLocalEp);
        bi.Add(CilOpCodes.Ldloc,    bSocket);
        bi.Add(CilOpCodes.Callvirt, socketClose);
        // if (localEp == null) goto routeNext
        bi.Add(CilOpCodes.Ldloc,    bLocalEp);
        bi.Add(CilOpCodes.Brfalse,  routeNextLbl);
        // addr = localEp.Address; if IPv4 and not loopback -> return addr
        bi.Add(CilOpCodes.Ldloc,    bLocalEp);
        bi.Add(CilOpCodes.Callvirt, ipEpGetAddress);
        bi.Add(CilOpCodes.Stloc,    bAddr);
        bi.Add(CilOpCodes.Ldloc,    bAddr);
        bi.Add(CilOpCodes.Brfalse,  routeNextLbl);
        bi.Add(CilOpCodes.Ldloc,    bAddr);
        bi.Add(CilOpCodes.Callvirt, getAddrBytes);
        bi.Add(CilOpCodes.Stloc,    bC);
        bi.Add(CilOpCodes.Ldloc,    bC);
        bi.Add(CilOpCodes.Ldlen);
        bi.Add(CilOpCodes.Conv_I4);
        bi.Add(CilOpCodes.Ldc_I4,   4);
        bi.Add(CilOpCodes.Bne_Un,   routeNextLbl);
        bi.Add(CilOpCodes.Ldloc,    bC);
        bi.Add(CilOpCodes.Ldc_I4_0);
        bi.Add(CilOpCodes.Ldelem_U1);
        bi.Add(CilOpCodes.Ldc_I4,   127);
        bi.Add(CilOpCodes.Beq,      routeNextLbl);
        var routeFoundStart = new CilInstruction(CilOpCodes.Ldloc, bAddr);
        bi.Add(routeFoundStart); routeFoundLbl.Instruction = routeFoundStart;
        bi.Add(CilOpCodes.Ret);
        // routeNext: i++
        var routeNextStart = new CilInstruction(CilOpCodes.Ldloc, bIdx);
        bi.Add(routeNextStart); routeNextLbl.Instruction = routeNextStart;
        bi.Add(CilOpCodes.Ldc_I4_1);
        bi.Add(CilOpCodes.Add);
        bi.Add(CilOpCodes.Stloc,    bIdx);
        // routeCheck: if (i < addrs.Length) goto routeLoop
        var routeCheckStart = new CilInstruction(CilOpCodes.Ldloc, bIdx);
        bi.Add(routeCheckStart); routeCheckLbl.Instruction = routeCheckStart;
        bi.Add(CilOpCodes.Ldloc,    bAddrs);
        bi.Add(CilOpCodes.Ldlen);
        bi.Add(CilOpCodes.Conv_I4);
        bi.Add(CilOpCodes.Blt,      routeLoopLbl);

        // b = original.GetAddressBytes()
        bi.Add(CilOpCodes.Ldarg_0);
        bi.Add(CilOpCodes.Brfalse,  retOrigLbl);
        bi.Add(CilOpCodes.Ldarg_0);
        bi.Add(CilOpCodes.Callvirt, getAddrBytes);
        bi.Add(CilOpCodes.Stloc, bBytes);
        // if (b.Length != 4) goto retOrig
        bi.Add(CilOpCodes.Ldloc,   bBytes);
        bi.Add(CilOpCodes.Ldlen);
        bi.Add(CilOpCodes.Conv_I4);
        bi.Add(CilOpCodes.Ldc_I4,  4);
        bi.Add(CilOpCodes.Bne_Un,  retOrigLbl);
        // if (b[0] != 169) goto retOrig
        bi.Add(CilOpCodes.Ldloc,   bBytes);
        bi.Add(CilOpCodes.Ldc_I4_0);
        bi.Add(CilOpCodes.Ldelem_U1);
        bi.Add(CilOpCodes.Ldc_I4,  169);
        bi.Add(CilOpCodes.Bne_Un,  retOrigLbl);
        // if (b[1] != 254) goto retOrig
        bi.Add(CilOpCodes.Ldloc,   bBytes);
        bi.Add(CilOpCodes.Ldc_I4_1);
        bi.Add(CilOpCodes.Ldelem_U1);
        bi.Add(CilOpCodes.Ldc_I4,  254);
        bi.Add(CilOpCodes.Bne_Un,  retOrigLbl);
        // addrs = Dns.GetHostAddresses(Dns.GetHostName())
        bi.Add(CilOpCodes.Call,    dnsHostName);
        bi.Add(CilOpCodes.Call,    dnsHostAddrs);
        bi.Add(CilOpCodes.Stloc,   bAddrs);
        // i = 0; goto check
        bi.Add(CilOpCodes.Ldc_I4_0);
        bi.Add(CilOpCodes.Stloc,   bIdx);
        bi.Add(CilOpCodes.Br,      scanCheckLbl);

        // loop: addr = addrs[i]
        var bipLoopStart = new CilInstruction(CilOpCodes.Ldloc, bAddrs);
        bi.Add(bipLoopStart); scanLoopLbl.Instruction = bipLoopStart;
        bi.Add(CilOpCodes.Ldloc,   bIdx);
        bi.Add(CilOpCodes.Ldelem_Ref);
        bi.Add(CilOpCodes.Stloc,   bAddr);
        // c = addr.GetAddressBytes()
        bi.Add(CilOpCodes.Ldloc,   bAddr);
        bi.Add(CilOpCodes.Callvirt, getAddrBytes);
        bi.Add(CilOpCodes.Stloc,   bC);
        // if (c.Length != 4) goto next   [skip IPv6]
        bi.Add(CilOpCodes.Ldloc,   bC);
        bi.Add(CilOpCodes.Ldlen);
        bi.Add(CilOpCodes.Conv_I4);
        bi.Add(CilOpCodes.Ldc_I4,  4);
        bi.Add(CilOpCodes.Bne_Un,  scanNextLbl);
        // if (c[0] == 127) goto next   [loopback]
        bi.Add(CilOpCodes.Ldloc,   bC);
        bi.Add(CilOpCodes.Ldc_I4_0);
        bi.Add(CilOpCodes.Ldelem_U1);
        bi.Add(CilOpCodes.Ldc_I4,  127);
        bi.Add(CilOpCodes.Beq,     scanNextLbl);
        // if (c[0] != 169) goto retAddr  [good non-169 address]
        bi.Add(CilOpCodes.Ldloc,   bC);
        bi.Add(CilOpCodes.Ldc_I4_0);
        bi.Add(CilOpCodes.Ldelem_U1);
        bi.Add(CilOpCodes.Ldc_I4,  169);
        bi.Add(CilOpCodes.Bne_Un,  retAddrLbl);
        // c[0]==169: if (c[1] == 254) goto next  [still link-local]
        bi.Add(CilOpCodes.Ldloc,   bC);
        bi.Add(CilOpCodes.Ldc_I4_1);
        bi.Add(CilOpCodes.Ldelem_U1);
        bi.Add(CilOpCodes.Ldc_I4,  254);
        bi.Add(CilOpCodes.Beq,     scanNextLbl);
        // retAddr: return addr
        var retAddrStart = new CilInstruction(CilOpCodes.Ldloc, bAddr);
        bi.Add(retAddrStart); retAddrLbl.Instruction = retAddrStart;
        bi.Add(CilOpCodes.Ret);
        // next: i++
        var bipNextStart = new CilInstruction(CilOpCodes.Ldloc, bIdx);
        bi.Add(bipNextStart); scanNextLbl.Instruction = bipNextStart;
        bi.Add(CilOpCodes.Ldc_I4_1);
        bi.Add(CilOpCodes.Add);
        bi.Add(CilOpCodes.Stloc, bIdx);
        // check: if (i < addrs.Length) goto loop
        var bipCheckStart = new CilInstruction(CilOpCodes.Ldloc, bIdx);
        bi.Add(bipCheckStart); scanCheckLbl.Instruction = bipCheckStart;
        bi.Add(CilOpCodes.Ldloc,  bAddrs);
        bi.Add(CilOpCodes.Ldlen);
        bi.Add(CilOpCodes.Conv_I4);
        bi.Add(CilOpCodes.Blt,    scanLoopLbl);
        // retOrig: return original
        var retOrigStart = new CilInstruction(CilOpCodes.Ldarg_0);
        bi.Add(retOrigStart); retOrigLbl.Instruction = retOrigStart;
        bi.Add(CilOpCodes.Ret);

        bipBody.Instructions.OptimizeMacros();
        bestIpHelper.CilMethodBody = bipBody;
        netUtility.Methods.Add(bestIpHelper);

        // Wrap every ret in GetMyAddress with __GetBestLocalIp
        var getMyAddrMethod = netUtility.Methods.FirstOrDefault(m =>
            m.Name == "GetMyAddress" && m.CilMethodBody is not null);
        if (getMyAddrMethod is not null)
        {
            var gaI    = getMyAddrMethod.CilMethodBody!.Instructions;
            var gaRets = Enumerable.Range(0, gaI.Count)
                .Where(i => gaI[i].OpCode == CilOpCodes.Ret)
                .OrderByDescending(i => i)
                .ToList();
            foreach (int ri in gaRets)
                gaI.Insert(ri, new CilInstruction(CilOpCodes.Call, bestIpHelper));
            getMyAddrMethod.CilMethodBody!.Instructions.OptimizeMacros();
            Console.WriteLine("Injected route-aware local-IP fix into NetUtility.GetMyAddress.");
        }
    }
    else if (!includeRouteAwareLocalIpFix)
    {
        Console.WriteLine("Skipped route-aware local-IP fix in NetUtility.GetMyAddress.");
    }

    module.Write(path);

    Console.WriteLine($"Patched {LidgrenDllName} â€” injected config file lookup into NetUtility.Resolve.");
    Console.WriteLine($"Backup saved as {Path.GetFileName(backup)}");
}

void Restore()
{
    bool restoredAny = false;

    string? dll = FindFile(LidgrenDllName);
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

    string? exePath = FindFile(ExeName);
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
    }

    if (!restoredAny)
    {
        Console.Error.WriteLine("No patch backups found â€” may already be unpatched.");
        ExitProcess(1);
    }
}

string? FindFile(string name)
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

void InspectType(string typeName)
{
    string? exePath = FindFile(ExeName);
    if (exePath is null)
    {
        Console.Error.WriteLine($"Could not find {ExeName}.");
        ExitProcess(1);
        return;
    }

    var module = ModuleDefinition.FromFile(exePath);
    var type = module.GetAllTypes().FirstOrDefault(t => t.FullName == typeName || t.Name == typeName);
    if (type is null)
    {
        Console.Error.WriteLine($"Type not found: {typeName}");
        ExitProcess(1);
        return;
    }

    Console.WriteLine(type.FullName);
    Console.WriteLine("Fields:");
    foreach (var field in type.Fields)
        Console.WriteLine($"  {field.Signature?.FieldType.FullName} {field.Name}");
    Console.WriteLine("Methods:");
    foreach (var method in type.Methods)
        Console.WriteLine($"  {method.Name}");
}

void InspectMethod(string typeName, string methodName)
{
    string? exePath = FindFile(ExeName);
    if (exePath is null)
    {
        Console.Error.WriteLine($"Could not find {ExeName}.");
        ExitProcess(1);
        return;
    }

    var module = ModuleDefinition.FromFile(exePath);
    var type = module.GetAllTypes().FirstOrDefault(t => t.FullName == typeName || t.Name == typeName);
    if (type is null)
    {
        Console.Error.WriteLine($"Type not found: {typeName}");
        ExitProcess(1);
        return;
    }

    var methods = type.Methods.Where(m => m.Name == methodName).ToList();
    if (methods.Count == 0)
    {
        Console.Error.WriteLine($"Method not found: {type.FullName}::{methodName}");
        ExitProcess(1);
        return;
    }

    foreach (var method in methods)
    {
        Console.WriteLine($"{type.FullName}::{method.Name}");
        Console.WriteLine($"  Params: {string.Join(", ", method.Parameters.Select(p => p.ParameterType.FullName + " " + p.Name))}");
        Console.WriteLine($"  Return: {method.Signature?.ReturnType.FullName}");

        if (method.CilMethodBody is null)
        {
            Console.WriteLine("  <no CIL body>");
            continue;
        }

        int index = 0;
        foreach (var instr in method.CilMethodBody.Instructions)
        {
            Console.WriteLine($"  IL_{index:D4}: {instr.OpCode,-12} {FormatInspectOperand(instr.Operand)}");
            index++;
        }

        foreach (var eh in method.CilMethodBody.ExceptionHandlers)
        {
            Console.WriteLine($"  EH: {eh.HandlerType} type={eh.ExceptionType?.FullName} " +
                $"tryStart={FormatInspectLabel(eh.TryStart)} tryEnd={FormatInspectLabel(eh.TryEnd)} " +
                $"handlerStart={FormatInspectLabel(eh.HandlerStart)} handlerEnd={FormatInspectLabel(eh.HandlerEnd)}");
        }
    }
}

string FormatInspectLabel(AsmResolver.PE.DotNet.Cil.ICilLabel? label)
{
    if (label is null) return "<null>";
    if (label is CilInstructionLabel cil && cil.Instruction is not null)
        return $"{cil.Instruction.OpCode}";
    return label.ToString() ?? "<?>";
}

string FormatInspectOperand(object? operand)
{
    return operand switch
    {
        null => string.Empty,
        string s => $"\"{s}\"",
        IMethodDescriptor m => m.FullName,
        IFieldDescriptor f => f.FullName,
        ITypeDescriptor t => t.FullName,
        CilInstructionLabel l when l.Instruction is not null => $"-> {l.Instruction.OpCode}",
        _ => operand.ToString() ?? string.Empty
    };
}

void ApplyExceptionLogPatch(ModuleDefinition module, string exePath)
{
    string logPath = Path.Combine(Path.GetDirectoryName(exePath)!, "exception.log");
    var startType = module.TopLevelTypes.First(t => t.Namespace == "Apotheon" && t.Name == "Start");
    var crash = startType.Methods.First(m => m.Name == "Crash");
    var body = crash.CilMethodBody!;
    var instrs = body.Instructions;
    if (instrs.Count == 0) return;

    var corlib = module.CorLibTypeFactory;
    var scope = corlib.CorLibScope;
    var fileType = new TypeReference(module, scope, "System.IO", "File");
    var stringType = new TypeReference(module, scope, "System", "String");
    var objectType = new TypeReference(module, scope, "System", "Object");

    var appendAllText = new MemberReference(fileType, "AppendAllText",
        MethodSignature.CreateStatic(corlib.Void, corlib.String, corlib.String)).ImportWith(module.DefaultImporter);
    var stringConcat3 = new MemberReference(stringType, "Concat",
        MethodSignature.CreateStatic(corlib.String, corlib.String, corlib.String, corlib.String)).ImportWith(module.DefaultImporter);
    var objectToString = new MemberReference(objectType, "ToString",
        MethodSignature.CreateInstance(corlib.String)).ImportWith(module.DefaultImporter);

    var prepend = new List<CilInstruction>
    {
        new(CilOpCodes.Ldstr, logPath),
        new(CilOpCodes.Ldstr, "\r\n=== "),
        new(CilOpCodes.Ldarg_1),
        new(CilOpCodes.Ldstr, " ===\r\n"),
        new(CilOpCodes.Call, stringConcat3),
        new(CilOpCodes.Call, appendAllText),
        new(CilOpCodes.Ldstr, logPath),
        new(CilOpCodes.Ldarg_2),
        new(CilOpCodes.Callvirt, objectToString),
        new(CilOpCodes.Ldstr, "\r\n"),
        new(CilOpCodes.Call, MakeStringConcat2(module, stringType, corlib)),
        new(CilOpCodes.Call, appendAllText),
    };

    for (int i = 0; i < prepend.Count; i++)
        instrs.Insert(i, prepend[i]);

    Console.WriteLine($"Patched ApotheonArena.exe — injected exception logger writing to {Path.GetFileName(logPath)}.");
}

IMethodDescriptor MakeStringConcat2(ModuleDefinition module, TypeReference stringType, CorLibTypeFactory corlib)
{
    return new MemberReference(stringType, "Concat",
        MethodSignature.CreateStatic(corlib.String, corlib.String, corlib.String)).ImportWith(module.DefaultImporter);
}

void ApplyEmptyServerListMessagePatch(ModuleDefinition module)
{
    var serverBrowser = module.TopLevelTypes.First(t => t.Namespace == "Apotheon" && t.Name == "ServerBrowser");
    var screenElement = module.TopLevelTypes.First(t => t.Namespace == "Apotheon" && t.Name == "ScreenElement");
    var screenManager = module.TopLevelTypes.First(t => t.Namespace == "Apotheon" && t.Name == "ScreenManager");
    var messageBox = module.TopLevelTypes.First(t => t.Namespace == "Apotheon" && t.Name == "MessageBoxScreen");

    var update = serverBrowser.Methods.First(m => m.Name == "Update" && m.CilMethodBody is not null);
    var addScreen = screenManager.Methods.First(m => m.Name == "AddScreen" && m.Parameters.Count == 1);
    var popupCtor = messageBox.Methods.First(m =>
        m.IsConstructor &&
        m.Parameters.Count == 1 &&
        m.Parameters[0].ParameterType.IsTypeOf("System", "String"));
    var screenManagerField = screenElement.Fields.First(f => f.Name == "ScreenManager");

    var body = update.CilMethodBody!;
    var instrs = body.Instructions;

    CilInstruction? boolBranch = null;
    for (int i = 0; i <= instrs.Count - 4; i++)
    {
        if (instrs[i].OpCode != CilOpCodes.Callvirt || instrs[i].Operand is not IMethodDescriptor called ||
            !string.Equals(called.Name?.ToString(), "ReadBoolean", StringComparison.Ordinal))
            continue;

        if (instrs[i + 3].OpCode != CilOpCodes.Brfalse && instrs[i + 3].OpCode != CilOpCodes.Brfalse_S)
            continue;

        boolBranch = instrs[i + 3];
        break;
    }

    if (boolBranch is null || boolBranch.Operand is not CilInstructionLabel endTargetLabel || endTargetLabel.Instruction is null)
        throw new InvalidOperationException("Could not locate the empty-list branch in ServerBrowser.Update.");

    var endTarget = endTargetLabel.Instruction;
    int insertIndex = instrs.IndexOf(endTarget);
    if (insertIndex < 0)
        throw new InvalidOperationException("Could not locate the end-of-loop target in ServerBrowser.Update.");

    var popupLabel = new CilInstructionLabel();
    var popupStart = new CilInstruction(CilOpCodes.Ldarg_0);
    popupLabel.Instruction = popupStart;

    var exitLabel = new CilInstructionLabel();
    exitLabel.Instruction = endTarget;

    var popupCode = new List<CilInstruction>
    {
        popupStart,
        new(CilOpCodes.Ldfld, screenManagerField),
        new(CilOpCodes.Ldstr, "No games available."),
        new(CilOpCodes.Newobj, popupCtor),
        new(CilOpCodes.Callvirt, addScreen),
        new(CilOpCodes.Br, exitLabel),
    };

    boolBranch.Operand = popupLabel;

    for (int i = 0; i < popupCode.Count; i++)
        instrs.Insert(insertIndex + i, popupCode[i]);

    body.Instructions.OptimizeMacros();
}

void Diagnose()
{
    string? exePath = FindFile(ExeName);
    if (exePath is null)
    {
        Console.Error.WriteLine($"Could not find {ExeName}.");
        ExitProcess(1); return;
    }

    string exeDir = Path.GetDirectoryName(exePath)!;
    string backup = Path.Combine(exeDir, "ApotheonArena.exe.diagbak");

    Console.WriteLine("In-process diagnose is disabled because it destabilizes the game.");
    if (File.Exists(backup))
        Console.WriteLine("An older diagnose patch backup still exists. Run 'undiagnose' to restore the original executable first.");
    Console.WriteLine("Use the external crash watcher instead:");
    Console.WriteLine(@"  .\DiagnoseTrace\bin\Debug\net8.0\DiagnoseTrace.exe --launch --procdump");
    Console.WriteLine("That captures transition logs and crash artifacts without patching ApotheonArena.exe.");
}

void Undiagnose()
{
    const string BackupName = "ApotheonArena.exe.diagbak";

    string? exePath = FindFile(ExeName);
    if (exePath is null) { Console.Error.WriteLine($"Could not find {ExeName}."); ExitProcess(1); return; }

    string backup = Path.Combine(Path.GetDirectoryName(exePath)!, BackupName);
    if (!File.Exists(backup)) { Console.Error.WriteLine("No diagnose backup found."); ExitProcess(1); return; }

    File.Copy(backup, exePath, overwrite: true);
    File.Delete(backup);
    Console.WriteLine("Diagnose patch removed.");
}

void SafeNetDebug()
{
    const string BackupName = "ApotheonArena.exe.netdebugbak";

    string? exePath = FindFile(ExeName);
    if (exePath is null)
    {
        Console.Error.WriteLine($"Could not find {ExeName}.");
        ExitProcess(1);
        return;
    }

    string exeDir = Path.GetDirectoryName(exePath)!;
    string backup = Path.Combine(exeDir, BackupName);
    string? dll = FindFile(LidgrenDllName);
    string? legacyBackup = dll is null ? null : dll + ".netdebugbak";

    if (File.Exists(backup))
    {
        Console.Error.WriteLine("Network debug already enabled. Run 'unnetdebug' first.");
        ExitProcess(1);
        return;
    }

    if (legacyBackup is not null && File.Exists(legacyBackup))
    {
        Console.Error.WriteLine("Legacy Lidgren network debug backup found. Run 'unnetdebug' first.");
        ExitProcess(1);
        return;
    }

    File.Copy(exePath, backup);

    var module = ModuleDefinition.FromFile(exePath);
    var corlib = module.CorLibTypeFactory;
    var scope = corlib.CorLibScope;

    var fileType = new TypeReference(module, scope, "System.IO", "File");
    var stringType = new TypeReference(module, scope, "System", "String");
    var dateTimeType = new TypeReference(module, scope, "System", "DateTime");
    var dateTimeSig = new TypeDefOrRefSignature(dateTimeType);
    string logsDirPath = Path.Combine(exeDir, "Logs");
    string networkLogPath = Path.Combine(logsDirPath, "network_debug.log");
    Directory.CreateDirectory(logsDirPath);

    var appendAll = new MemberReference(fileType, "AppendAllText",
        MethodSignature.CreateStatic(corlib.Void, corlib.String, corlib.String));
    var concat2 = new MemberReference(stringType, "Concat",
        MethodSignature.CreateStatic(corlib.String, corlib.String, corlib.String));
    var concat3 = new MemberReference(stringType, "Concat",
        MethodSignature.CreateStatic(corlib.String, corlib.String, corlib.String, corlib.String));
    var getUtcNow = new MemberReference(dateTimeType, "get_UtcNow",
        MethodSignature.CreateStatic(dateTimeSig));
    var dateTimeFmt = new MemberReference(dateTimeType, "ToString",
        MethodSignature.CreateInstance(corlib.String, corlib.String));

    var networkType = module.TopLevelTypes.First(t => t.Namespace == "Apotheon" && t.Name == "Network");
    var serverBrowser = module.TopLevelTypes.FirstOrDefault(t => t.Namespace == "Apotheon" && t.Name == "ServerBrowser");

    var logHelper = networkType.Methods.FirstOrDefault(m => m.Name == "__NetDebugLog");
    if (logHelper is null)
    {
        logHelper = new MethodDefinition("__NetDebugLog",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(corlib.Void, corlib.String));
        networkType.Methods.Add(logHelper);
    }

    var logBody = new CilMethodBody(logHelper);
    var tsVar = new CilLocalVariable(corlib.String);
    var lineVar = new CilLocalVariable(corlib.String);
    logBody.LocalVariables.Add(tsVar);
    logBody.LocalVariables.Add(lineVar);

    var li = logBody.Instructions;
    li.Add(CilOpCodes.Call, getUtcNow);
    li.Add(CilOpCodes.Ldstr, "yyyy-MM-dd HH:mm:ss.fff'Z' ");
    li.Add(CilOpCodes.Call, dateTimeFmt);
    li.Add(CilOpCodes.Stloc, tsVar);
    li.Add(CilOpCodes.Ldloc, tsVar);
    li.Add(CilOpCodes.Ldarg_0);
    li.Add(CilOpCodes.Call, concat2);
    li.Add(CilOpCodes.Stloc, lineVar);
    li.Add(CilOpCodes.Ldstr, networkLogPath);
    li.Add(CilOpCodes.Ldloc, lineVar);
    li.Add(CilOpCodes.Call, appendAll);
    li.Add(CilOpCodes.Ret);
    logBody.Instructions.OptimizeMacros();
    logHelper.CilMethodBody = logBody;

    MethodDefinition? masterHostProvider = networkType.Methods.FirstOrDefault(m =>
        m.Name == "__GetMasterServerHost" &&
        m.Parameters.Count == 0 &&
        m.Signature is not null &&
        m.Signature.ReturnType.IsTypeOf("System", "String"));

    if (masterHostProvider is null)
    {
        masterHostProvider = new MethodDefinition("__NetDebugGetMasterServerHost",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(corlib.String));
        var hostBody = new CilMethodBody(masterHostProvider);
        hostBody.Instructions.Add(CilOpCodes.Ldstr, OriginalIp);
        hostBody.Instructions.Add(CilOpCodes.Ret);
        hostBody.Instructions.OptimizeMacros();
        masterHostProvider.CilMethodBody = hostBody;
        networkType.Methods.Add(masterHostProvider);
    }

    void AddLogPrefix(MethodDefinition method, string eventName, bool includeMasterHost)
    {
        if (method.CilMethodBody is null)
            return;

        var prefix = new List<CilInstruction>
        {
            new(CilOpCodes.Ldstr, $"[{eventName}]\n"),
            new(CilOpCodes.Call, logHelper),
        };

        if (includeMasterHost)
        {
            prefix.Add(new CilInstruction(CilOpCodes.Ldstr, "[master] "));
            prefix.Add(new CilInstruction(CilOpCodes.Call, masterHostProvider));
            prefix.Add(new CilInstruction(CilOpCodes.Ldstr, "\n"));
            prefix.Add(new CilInstruction(CilOpCodes.Call, concat3));
            prefix.Add(new CilInstruction(CilOpCodes.Call, logHelper));
        }

        for (int i = 0; i < prefix.Count; i++)
            method.CilMethodBody.Instructions.Insert(i, prefix[i]);

        method.CilMethodBody.Instructions.OptimizeMacros();
    }

    AddLogPrefix(networkType.Methods.First(m => m.Name == "ServerStart" && m.CilMethodBody is not null), "server-start", includeMasterHost: true);
    AddLogPrefix(networkType.Methods.First(m => m.Name == "ServerQuit" && m.CilMethodBody is not null), "server-quit", includeMasterHost: true);
    AddLogPrefix(networkType.Methods.First(m => m.Name == "RequestNATIntroduction" && m.CilMethodBody is not null), "nat-request", includeMasterHost: true);

    if (serverBrowser is not null)
    {
        var onInitialize = serverBrowser.Methods.FirstOrDefault(m => m.Name == "OnInitialize" && m.CilMethodBody is not null);
        if (onInitialize is not null)
            AddLogPrefix(onInitialize, "browser-init", includeMasterHost: true);

        var refresh = serverBrowser.Methods.FirstOrDefault(m => m.Name == "Refresh" && m.CilMethodBody is not null);
        if (refresh is not null)
            AddLogPrefix(refresh, "browser-refresh", includeMasterHost: true);
    }

    module.Write(exePath);

    Console.WriteLine("Network debug enabled. Logged to Logs\\network_debug.log:");
    Console.WriteLine("  [browser-init]     server browser initialization");
    Console.WriteLine("  [browser-refresh]  server browser refresh requests");
    Console.WriteLine("  [server-start]     hosting flow started");
    Console.WriteLine("  [server-quit]      hosting flow stopped");
    Console.WriteLine("  [nat-request]      join/NAT introduction requested");
    Console.WriteLine("  [master]           master server host used by the active flow");
}

void SafeUnNetDebug()
{
    bool removedAny = false;

    string? exePath = FindFile(ExeName);
    if (exePath is not null)
    {
        string backup = Path.Combine(Path.GetDirectoryName(exePath)!, "ApotheonArena.exe.netdebugbak");
        if (File.Exists(backup))
        {
            File.Copy(backup, exePath, overwrite: true);
            File.Delete(backup);
            Console.WriteLine("Network debug disabled.");
            removedAny = true;
        }
    }

    string? dll = FindFile(LidgrenDllName);
    if (dll is not null)
    {
        string legacyBackup = dll + ".netdebugbak";
        if (File.Exists(legacyBackup))
        {
            File.Copy(legacyBackup, dll, overwrite: true);
            File.Delete(legacyBackup);
            Console.WriteLine("Legacy Lidgren network debug disabled.");
            removedAny = true;
        }
    }

    if (!removedAny)
    {
        Console.Error.WriteLine("Network debug not enabled.");
        ExitProcess(1);
    }
}

void NetDebug()
{
    string? dll = FindFile(LidgrenDllName);
    if (dll is null)
    {
        Console.Error.WriteLine($"Could not find {LidgrenDllName}.");
        ExitProcess(1); return;
    }

    string backup = dll + ".netdebugbak";
    if (File.Exists(backup))
    {
        Console.Error.WriteLine("Network debug already enabled. Run 'unnetdebug' first.");
        ExitProcess(1); return;
    }

    File.Copy(dll, backup);

    var module     = ModuleDefinition.FromFile(dll);
    var corlib     = module.CorLibTypeFactory;
    var scope      = corlib.CorLibScope;

    var fileType        = new TypeReference(module, scope, "System.IO", "File");
    var stringType      = new TypeReference(module, scope, "System",    "String");
    var dateTimeType    = new TypeReference(module, scope, "System",    "DateTime");
    var dateTimeSig     = new TypeDefOrRefSignature(dateTimeType);
    string logsDirPath  = Path.Combine(Path.GetDirectoryName(dll)!, "Logs");
    string networkLogPath = Path.Combine(logsDirPath, "network_debug.log");
    Directory.CreateDirectory(logsDirPath);

    var appendAll  = new MemberReference(fileType,   "AppendAllText",
        MethodSignature.CreateStatic(corlib.Void, corlib.String, corlib.String));
    var concat2    = new MemberReference(stringType, "Concat",
        MethodSignature.CreateStatic(corlib.String, corlib.String, corlib.String));
    var concat3    = new MemberReference(stringType, "Concat",
        MethodSignature.CreateStatic(corlib.String, corlib.String, corlib.String, corlib.String));
    var concat4    = new MemberReference(stringType, "Concat",
        MethodSignature.CreateStatic(corlib.String, corlib.String, corlib.String, corlib.String, corlib.String));

    var toString   = new MemberReference(
        new TypeReference(module, scope, "System", "Int32"), "ToString",
        MethodSignature.CreateInstance(corlib.String));
    var getUtcNow  = new MemberReference(dateTimeType, "get_UtcNow",
        MethodSignature.CreateStatic(dateTimeSig));
    var dateTimeFmt = new MemberReference(dateTimeType, "ToString",
        MethodSignature.CreateInstance(corlib.String, corlib.String));

    // Patch NetUtility.Resolve(string, int) â€” log every call
    var netUtility = module.TopLevelTypes.First(t => t.Name == "NetUtility");
    var logHelper  = netUtility.Methods.FirstOrDefault(m => m.Name == "__NetDebugLog");
    if (logHelper is null)
    {
        logHelper = new MethodDefinition("__NetDebugLog",
            MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(corlib.Void, corlib.String));
        netUtility.Methods.Add(logHelper);
    }

    var lBody = new CilMethodBody(logHelper);
    var tsVar = new CilLocalVariable(corlib.String);
    var lineVar = new CilLocalVariable(corlib.String);
    lBody.LocalVariables.Add(tsVar);
    lBody.LocalVariables.Add(lineVar);

    var li = lBody.Instructions;
    li.Add(CilOpCodes.Call,     getUtcNow);
    li.Add(CilOpCodes.Ldstr,    "yyyy-MM-dd HH:mm:ss.fff'Z' ");
    li.Add(CilOpCodes.Call,     dateTimeFmt);
    li.Add(CilOpCodes.Stloc,    tsVar);
    li.Add(CilOpCodes.Ldloc,    tsVar);
    li.Add(CilOpCodes.Ldarg_0);
    li.Add(CilOpCodes.Call,     concat2);
    li.Add(CilOpCodes.Stloc,    lineVar);
    li.Add(CilOpCodes.Ldstr,    networkLogPath);
    li.Add(CilOpCodes.Ldloc,    lineVar);
    li.Add(CilOpCodes.Call,     appendAll);
    li.Add(CilOpCodes.Ret);

    lBody.Instructions.OptimizeMacros();
    logHelper.CilMethodBody = lBody;
    var resolve    = netUtility.Methods.First(m =>
        m.Name == "Resolve" &&
        m.Parameters.Count == 2 &&
        m.Parameters[1].ParameterType.IsTypeOf("System", "Int32"));

    var rBody      = resolve.CilMethodBody!;
    var portStrVar = new CilLocalVariable(corlib.String);
    rBody.LocalVariables.Add(portStrVar);

    var resolvePrefix = new List<CilInstruction>
    {
        // portStr = port.ToString()
        new(CilOpCodes.Ldarga,   resolve.Parameters[1]),
        new(CilOpCodes.Call,     toString),
        new(CilOpCodes.Stloc,   portStrVar),
        // __NetDebugLog("[resolve] " + ipOrHost + ":" + portStr + "\n")
        new(CilOpCodes.Ldstr,   "[resolve] "),
        new(CilOpCodes.Ldarg,   resolve.Parameters[0]),
        new(CilOpCodes.Ldstr,   ":"),
        new(CilOpCodes.Call,    concat3),
        new(CilOpCodes.Ldloc,   portStrVar),
        new(CilOpCodes.Ldstr,   "\n"),
        new(CilOpCodes.Call,    concat3),
        new(CilOpCodes.Call,    logHelper),
    };

    for (int i = 0; i < resolvePrefix.Count; i++)
        rBody.Instructions.Insert(i, resolvePrefix[i]);
    rBody.Instructions.OptimizeMacros();

    // Patch NetPeer.Connect(string host, int port) â€” log every connect attempt
    var netPeer = module.TopLevelTypes.First(t => t.Name == "NetPeer");
    var connect = netPeer.Methods.FirstOrDefault(m =>
        m.Name == "Connect" &&
        m.Parameters.Count >= 2 &&
        m.Parameters[0].ParameterType.IsTypeOf("System", "String") &&
        m.Parameters[1].ParameterType.IsTypeOf("System", "Int32"));

    if (connect?.CilMethodBody is not null)
    {
        var cBody      = connect.CilMethodBody;
        var cPortVar   = new CilLocalVariable(corlib.String);
        cBody.LocalVariables.Add(cPortVar);

        var connectPrefix = new List<CilInstruction>
        {
            new(CilOpCodes.Ldarga,  connect.Parameters[1]),
            new(CilOpCodes.Call,    toString),
            new(CilOpCodes.Stloc,  cPortVar),
            new(CilOpCodes.Ldstr,  "[connect] "),
            new(CilOpCodes.Ldarg,  connect.Parameters[0]),
            new(CilOpCodes.Ldstr,  ":"),
            new(CilOpCodes.Call,   concat3),
            new(CilOpCodes.Ldloc,  cPortVar),
            new(CilOpCodes.Ldstr,  "\n"),
            new(CilOpCodes.Call,   concat3),
            new(CilOpCodes.Call,   logHelper),
        };

        for (int i = 0; i < connectPrefix.Count; i++)
            cBody.Instructions.Insert(i, connectPrefix[i]);
        cBody.Instructions.OptimizeMacros();
    }

    var objToString = new MemberReference(
        new TypeReference(module, scope, "System", "Object"), "ToString",
        MethodSignature.CreateInstance(corlib.String));

    // Patch NetPeer.Connect(IPEndPoint) â€” log P2P connection attempts
    var connectEp = netPeer.Methods.FirstOrDefault(m =>
        m.Name == "Connect" &&
        m.Parameters.Count >= 1 &&
        m.Parameters[0].ParameterType.IsTypeOf("System.Net", "IPEndPoint"));

    if (connectEp?.CilMethodBody is not null)
    {
        var epBody   = connectEp.CilMethodBody;
        var epStrVar = new CilLocalVariable(corlib.String);
        epBody.LocalVariables.Add(epStrVar);

        var epPrefix = new List<CilInstruction>
        {
            new(CilOpCodes.Ldarg,    connectEp.Parameters[0]),
            new(CilOpCodes.Callvirt, objToString),
            new(CilOpCodes.Stloc,   epStrVar),
            new(CilOpCodes.Ldstr,   "[connect-ep] "),
            new(CilOpCodes.Ldloc,   epStrVar),
            new(CilOpCodes.Ldstr,   "\n"),
            new(CilOpCodes.Call,    concat3),
            new(CilOpCodes.Call,    logHelper),
        };

        for (int i = 0; i < epPrefix.Count; i++)
            epBody.Instructions.Insert(i, epPrefix[i]);
        epBody.Instructions.OptimizeMacros();
    }

    // Patch NetPeer.SendUnconnectedMessage(NetOutgoingMessage, IPEndPoint) â€” log master server traffic
    var sendUnconnected = netPeer.Methods.FirstOrDefault(m =>
        m.Name == "SendUnconnectedMessage" &&
        m.Parameters.Count >= 2 &&
        m.Parameters[1].ParameterType.IsTypeOf("System.Net", "IPEndPoint"));

    if (sendUnconnected?.CilMethodBody is not null)
    {
        var suBody   = sendUnconnected.CilMethodBody;
        var suStrVar = new CilLocalVariable(corlib.String);
        suBody.LocalVariables.Add(suStrVar);

        var suPrefix = new List<CilInstruction>
        {
            new(CilOpCodes.Ldarg,    sendUnconnected.Parameters[1]),
            new(CilOpCodes.Callvirt, objToString),
            new(CilOpCodes.Stloc,   suStrVar),
            new(CilOpCodes.Ldstr,   "[send-unconnected] -> "),
            new(CilOpCodes.Ldloc,   suStrVar),
            new(CilOpCodes.Ldstr,   "\n"),
            new(CilOpCodes.Call,    concat3),
            new(CilOpCodes.Call,    logHelper),
        };

        for (int i = 0; i < suPrefix.Count; i++)
            suBody.Instructions.Insert(i, suPrefix[i]);
        suBody.Instructions.OptimizeMacros();
    }

    // Patch GetMyAddress â€” log the local IP Lidgren picks for this machine.
    // Inserts logging before every ret so all return paths are covered.
    var getMyAddr = netUtility.Methods.FirstOrDefault(m =>
        m.Name == "GetMyAddress" && m.CilMethodBody is not null);

    if (getMyAddr is not null)
    {
        var gaBody   = getMyAddr.CilMethodBody!;
        var gaStrVar = new CilLocalVariable(corlib.String);
        gaBody.LocalVariables.Add(gaStrVar);

        var instrs     = gaBody.Instructions;
        var retIndices = Enumerable.Range(0, instrs.Count)
            .Where(i => instrs[i].OpCode == CilOpCodes.Ret)
            .OrderByDescending(i => i)
            .ToList();

        foreach (int retIdx in retIndices)
        {
            var retInstr = instrs[retIdx];
            var skipLog  = new CilInstructionLabel();
            skipLog.Instruction = retInstr;

            // Stack before ret: [IPAddress]. dup + brfalse skips log on null.
            var logCode = new List<CilInstruction>
            {
                new(CilOpCodes.Dup),
                new(CilOpCodes.Brfalse, skipLog),
                new(CilOpCodes.Dup),
                new(CilOpCodes.Callvirt, objToString),
                new(CilOpCodes.Stloc,    gaStrVar),
                new(CilOpCodes.Ldstr,    "[local-ip] "),
                new(CilOpCodes.Ldloc,    gaStrVar),
                new(CilOpCodes.Ldstr,    "\n"),
                new(CilOpCodes.Call,     concat3),
                new(CilOpCodes.Call,     logHelper),
            };

            for (int i = 0; i < logCode.Count; i++)
                instrs.Insert(retIdx + i, logCode[i]);
        }
        gaBody.Instructions.OptimizeMacros();
    }

    // Patch NetConnection.SetStatus â€” log every connection state change with endpoint.
    var netConn     = module.TopLevelTypes.FirstOrDefault(t => t.Name == "NetConnection");
    var setStatus   = netConn?.Methods.FirstOrDefault(m =>
        m.Name == "SetStatus" && m.Parameters.Count >= 1 && m.CilMethodBody is not null);
    var getRemoteEp = netConn?.Methods.FirstOrDefault(m => m.Name == "get_RemoteEndPoint");
    var statusEnum  = module.TopLevelTypes.FirstOrDefault(t => t.Name == "NetConnectionStatus");

    if (setStatus is not null && getRemoteEp is not null && statusEnum is not null)
    {
        var ssBody      = setStatus.CilMethodBody!;
        var ssEpVar     = new CilLocalVariable(corlib.String);
        var ssStatusVar = new CilLocalVariable(corlib.String);
        ssBody.LocalVariables.Add(ssEpVar);
        ssBody.LocalVariables.Add(ssStatusVar);

        var ssPrefix = new List<CilInstruction>
        {
            // epStr = this.RemoteEndPoint.ToString()
            new(CilOpCodes.Ldarg_0),
            new(CilOpCodes.Callvirt, getRemoteEp),
            new(CilOpCodes.Callvirt, objToString),
            new(CilOpCodes.Stloc,    ssEpVar),
            // statusStr = ((NetConnectionStatus)arg0).ToString()
            new(CilOpCodes.Ldarg,    setStatus.Parameters[0]),
            new(CilOpCodes.Box,      statusEnum),
            new(CilOpCodes.Callvirt, objToString),
            new(CilOpCodes.Stloc,    ssStatusVar),
            // __NetDebugLog("[conn-status] " + ep + " -> " + status + "\n")
            new(CilOpCodes.Ldstr,    "[conn-status] "),
            new(CilOpCodes.Ldloc,    ssEpVar),
            new(CilOpCodes.Ldstr,    " -> "),
            new(CilOpCodes.Call,     concat3),
            new(CilOpCodes.Ldloc,    ssStatusVar),
            new(CilOpCodes.Ldstr,    "\n"),
            new(CilOpCodes.Call,     concat3),
            new(CilOpCodes.Call,     logHelper),
        };

        for (int i = 0; i < ssPrefix.Count; i++)
            ssBody.Instructions.Insert(i, ssPrefix[i]);
        ssBody.Instructions.OptimizeMacros();
    }

    // Patch NetPeer.ReadMessage() â€” log incoming Lidgren message types and sizes.
    var netIncoming      = module.TopLevelTypes.FirstOrDefault(t => t.Name == "NetIncomingMessage");
    var incomingTypeEnum = module.TopLevelTypes.FirstOrDefault(t => t.Name == "NetIncomingMessageType");
    var getMsgType       = netIncoming?.Methods.FirstOrDefault(m => m.Name == "get_MessageType");
    var getLengthBytes   = netIncoming?.Methods.FirstOrDefault(m => m.Name == "get_LengthBytes");
    var getSenderEp      = netIncoming?.Methods.FirstOrDefault(m => m.Name == "get_SenderEndPoint");
    var readMessage      = netPeer.Methods.FirstOrDefault(m =>
        m.Name == "ReadMessage" && m.Parameters.Count == 0 && m.CilMethodBody is not null);

    if (readMessage is not null && netIncoming is not null && incomingTypeEnum is not null &&
        getMsgType is not null && getLengthBytes is not null && getSenderEp is not null)
    {
        var rmBody    = readMessage.CilMethodBody!;
        var rmEpVar   = new CilLocalVariable(corlib.String);
        var rmTypeVar = new CilLocalVariable(corlib.String);
        var rmLenVar  = new CilLocalVariable(corlib.Int32);
        var rmLenStr  = new CilLocalVariable(corlib.String);
        rmBody.LocalVariables.Add(rmEpVar);
        rmBody.LocalVariables.Add(rmTypeVar);
        rmBody.LocalVariables.Add(rmLenVar);
        rmBody.LocalVariables.Add(rmLenStr);

        var rmInstrs    = rmBody.Instructions;
        var rmRetIndices = Enumerable.Range(0, rmInstrs.Count)
            .Where(i => rmInstrs[i].OpCode == CilOpCodes.Ret)
            .OrderByDescending(i => i)
            .ToList();

        foreach (int retIdx in rmRetIndices)
        {
            var retInstr = rmInstrs[retIdx];
            var skipLog  = new CilInstructionLabel();
            skipLog.Instruction = retInstr;
            var senderToString = new CilInstructionLabel();
            var senderDone     = new CilInstructionLabel();

            var senderToStringInstr = new CilInstruction(CilOpCodes.Callvirt, objToString);
            senderToString.Instruction = senderToStringInstr;
            var senderDoneInstr = new CilInstruction(CilOpCodes.Dup);
            senderDone.Instruction = senderDoneInstr;

            var recvLogCode = new List<CilInstruction>
            {
                new(CilOpCodes.Dup),
                new(CilOpCodes.Brfalse, skipLog),
                new(CilOpCodes.Dup),
                new(CilOpCodes.Callvirt, getMsgType),
                new(CilOpCodes.Box, incomingTypeEnum),
                new(CilOpCodes.Callvirt, objToString),
                new(CilOpCodes.Stloc, rmTypeVar),
                new(CilOpCodes.Dup),
                new(CilOpCodes.Callvirt, getSenderEp),
                new(CilOpCodes.Dup),
                new(CilOpCodes.Brtrue, senderToString),
                new(CilOpCodes.Pop),
                new(CilOpCodes.Ldstr, "(none)"),
                new(CilOpCodes.Stloc, rmEpVar),
                new(CilOpCodes.Br, senderDone),
                senderToStringInstr,
                new(CilOpCodes.Stloc, rmEpVar),
                senderDoneInstr,
                new(CilOpCodes.Callvirt, getLengthBytes),
                new(CilOpCodes.Stloc, rmLenVar),
                new(CilOpCodes.Ldloca, rmLenVar),
                new(CilOpCodes.Call, toString),
                new(CilOpCodes.Stloc, rmLenStr),
                new(CilOpCodes.Ldstr, "[recv] "),
                new(CilOpCodes.Ldloc, rmTypeVar),
                new(CilOpCodes.Ldstr, " sender="),
                new(CilOpCodes.Call, concat3),
                new(CilOpCodes.Ldloc, rmEpVar),
                new(CilOpCodes.Ldstr, " bytes="),
                new(CilOpCodes.Call, concat3),
                new(CilOpCodes.Ldloc, rmLenStr),
                new(CilOpCodes.Ldstr, "\n"),
                new(CilOpCodes.Call, concat3),
                new(CilOpCodes.Call, logHelper),
            };

            for (int i = 0; i < recvLogCode.Count; i++)
                rmInstrs.Insert(retIdx + i, recvLogCode[i]);
        }

        rmBody.Instructions.OptimizeMacros();
    }

    module.Write(dll);
    Console.WriteLine("Network debug enabled. Logged to Logs\\network_debug.log:");
    Console.WriteLine("  [master]           master server IP from config file");
    Console.WriteLine("  [resolve]          DNS/IP resolution");
    Console.WriteLine("  [local-ip]         local IP Lidgren detected for this machine");
    Console.WriteLine("  [recv]             incoming Lidgren message types, sender endpoints, and byte sizes");
    Console.WriteLine("  [connect]          connection attempt (host:port)");
    Console.WriteLine("  [connect-ep]       connection attempt (IPEndPoint, P2P)");
    Console.WriteLine("  [send-unconnected] unconnected packet sent to master server");
    Console.WriteLine("  [conn-status]      connection state changes");
}

void UnNetDebug()
{
    string? dll = FindFile(LidgrenDllName);
    if (dll is null) { Console.Error.WriteLine($"Could not find {LidgrenDllName}."); ExitProcess(1); return; }

    string backup = dll + ".netdebugbak";
    if (!File.Exists(backup)) { Console.Error.WriteLine("Network debug not enabled."); ExitProcess(1); return; }

    File.Copy(backup, dll, overwrite: true);
    File.Delete(backup);
    Console.WriteLine("Network debug disabled.");
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
