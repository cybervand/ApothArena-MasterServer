using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.DotNet.Signatures.Types;
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
            Console.Error.WriteLine("Browser patch already applied. Run 'Patcher.exe restore' first to unpatch.");
            Cli.Exit(1);
            return;
        }

        if (!includeRouteAwareLocalIpFix)
            Console.WriteLine("Using executable-only patch path; route-aware Lidgren fix is skipped.");

        PatchGameExecutable(exePath, browserBackupPath, includeRouteAwareLocalIpFix);

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

    static void PatchGameExecutable(string exePath, string backup, bool includeLocalHostIpOverride)
    {
        File.Copy(exePath, backup);

        var module = ModuleDefinition.FromFile(exePath);
        ApplyMasterServerRedirectPatch(module, exePath);
        if (includeLocalHostIpOverride)
            ApplyGameLocalHostIpOverridePatch(module, exePath);
        ApplyAdvertisedExternalEndpointPatch(module, exePath);
        ApplyEmptyServerListMessagePatch(module);
        ApplyExceptionLogPatch(module, exePath);
        ApplyHarmonyBootstrapPatch(module, exePath);
        module.Write(exePath);

        Console.WriteLine("Patched ApotheonArena.exe - redirected master server lookups to master_server.txt.");
        if (includeLocalHostIpOverride)
            Console.WriteLine($"Patched ApotheonArena.exe - host registration now honors optional {LocalHostIpFileName}.");
        Console.WriteLine("Patched ApotheonArena.exe - empty server lists now show 'No games available.'");
        Console.WriteLine("Patched ApotheonArena.exe - Harmony mod loader bootstrap injected into Apotheon.Program::Main.");
    }

    static void ApplyHarmonyBootstrapPatch(ModuleDefinition module, string exePath)
    {
        string exeDir = Path.GetDirectoryName(exePath)!;

        WriteEmbeddedPayload("ApotheonArena.NetworkMod.dll", Path.Combine(exeDir, "ApotheonArena.NetworkMod.dll"));
        WriteEmbeddedPayload("0Harmony.dll", Path.Combine(exeDir, "0Harmony.dll"));

        string modsDir = Path.Combine(exeDir, "mods");
        if (!Directory.Exists(modsDir))
        {
            Directory.CreateDirectory(modsDir);
            Console.WriteLine("Created mods/ folder next to ApotheonArena.exe.");
        }

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
        var modAsmName = "ApotheonArena.NetworkMod";
        var modAsmRef = new AssemblyReference(modAsmName, new Version(1, 0, 0, 0));
        module.AssemblyReferences.Add(modAsmRef);

        var modLoaderType = new TypeReference(module, modAsmRef, "ApotheonArena.NetworkMod", "ModLoader");
        var initMethod = new MemberReference(modLoaderType, "Init",
            MethodSignature.CreateStatic(corlib.Void));

        main.CilMethodBody!.Instructions.Insert(0, new CilInstruction(CilOpCodes.Call, initMethod));
    }

    static void WriteEmbeddedPayload(string resourceName, string destPath)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using Stream? stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' missing from patcher - rebuild required.");
        using var file = File.Create(destPath);
        stream.CopyTo(file);
    }

    static void ApplyMasterServerRedirectPatch(ModuleDefinition module, string exePath)
    {
        string configPathInGameDir = Path.Combine(Path.GetDirectoryName(exePath)!, ConfigFileName);
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

    static void ApplyGameLocalHostIpOverridePatch(ModuleDefinition module, string exePath)
    {
        string localHostIpPath = Path.Combine(Path.GetDirectoryName(exePath)!, LocalHostIpFileName);
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
        var serverStart = networkType.Methods.First(m => m.Name == "ServerStart" && m.CilMethodBody is not null);

        var getMyAddressCall = serverStart.CilMethodBody!.Instructions.FirstOrDefault(i =>
            i.OpCode == CilOpCodes.Call &&
            i.Operand is IMethodDescriptor called &&
            string.Equals(called.Name?.ToString(), "GetMyAddress", StringComparison.Ordinal));

        if (getMyAddressCall?.Operand is not IMethodDescriptor getMyAddressMethod)
            throw new InvalidOperationException("Could not locate Lidgren NetUtility.GetMyAddress call in ServerStart.");

        var getMyAddressSig = (MethodSignature)getMyAddressMethod.Signature!;
        var ipAddressSig = getMyAddressSig.ReturnType;
        var resolveStringMethod = new MemberReference(
            (ITypeDefOrRef)getMyAddressMethod.DeclaringType!,
            "Resolve",
            MethodSignature.CreateStatic(ipAddressSig, corlib.String));

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
            var configuredIpVar = new CilLocalVariable(ipAddressSig);
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

    static void ApplyAdvertisedExternalEndpointPatch(ModuleDefinition module, string exePath)
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

    static void WrapMethodBodyInTryCatch(
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

    static (int Index, CilInstruction? Call, CilLocalVariable? MsgLocal) FindRegisterJsonWrite(MethodDefinition method)
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

    static CilLocalVariable? ResolveLdlocLocal(CilInstruction instr, CilMethodBody body)
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

    static void ApplyEmptyServerListMessagePatch(ModuleDefinition module)
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

    static void ApplyExceptionLogPatch(ModuleDefinition module, string exePath)
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

        Console.WriteLine($"Patched ApotheonArena.exe - injected exception logger writing to {Path.GetFileName(logPath)}.");
    }

    static IMethodDescriptor MakeStringConcat2(ModuleDefinition module, TypeReference stringType, CorLibTypeFactory corlib)
    {
        return new MemberReference(stringType, "Concat",
            MethodSignature.CreateStatic(corlib.String, corlib.String, corlib.String)).ImportWith(module.DefaultImporter);
    }
}
