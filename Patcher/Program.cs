using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.DotNet.Signatures.Types;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables.Rows;

// Patches Lidgren.Network.dll's NetUtility.Resolve() to read the master server
// address from master_server.txt instead of using the hardcoded IP.
// Any IP or hostname of any length is supported.

const string OriginalIp     = "50.19.227.23";
const string LidgrenDllName = "Lidgren.Network.dll";
const string ExeName        = "ApotheonArena.exe";
const string ConfigFileName = "master_server.txt";

if (args.Length == 0)
{
    RunInteractiveMenu();
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
        case "3":
        case "netdebug":
            NetDebug();
            return true;
        case "4":
        case "unnetdebug":
            UnNetDebug();
            return true;
        case "5":
        case "diagnose":
            Diagnose();
            return true;
        case "6":
        case "undiagnose":
            Undiagnose();
            return true;
        case "7":
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
    Console.WriteLine("  3. Enable network debug");
    Console.WriteLine("  4. Disable network debug");
    Console.WriteLine("  5. Enable crash diagnose patch");
    Console.WriteLine("  6. Disable crash diagnose patch");
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
        Environment.Exit(1);
    }
}

void Patch()
{
    string? dllPath = FindFile(LidgrenDllName);
    if (dllPath is null)
    {
        Console.Error.WriteLine($"Could not find {LidgrenDllName} - place Patcher.exe in the game folder.");
        Environment.Exit(1);
        return;
    }

    string backupPath = dllPath + ".bak";
    if (File.Exists(backupPath))
    {
        Console.Error.WriteLine("Already patched. Run 'Patcher.exe restore' first to unpatch.");
        Environment.Exit(1);
        return;
    }

    string? exePath = FindFile(ExeName);
    string? browserBackupPath = exePath is null ? null : exePath + ".browserbak";
    if (browserBackupPath is not null && File.Exists(browserBackupPath))
    {
        Console.Error.WriteLine("Browser patch already applied. Run 'Patcher.exe restore' first to unpatch.");
        Environment.Exit(1);
        return;
    }

    PatchLidgren(dllPath, backupPath);

    if (exePath is not null && browserBackupPath is not null)
        PatchEmptyServerListMessage(exePath, browserBackupPath);
    else
        Console.WriteLine($"Could not find {ExeName} - skipped empty-list browser patch.");

    // Create master_server.txt in the game folder if it doesn't already exist
    string gameDir = Path.GetDirectoryName(dllPath)!;
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

    Console.WriteLine("Done! Edit master_server.txt whenever you need to change the server.");
}

void PatchLidgren(string path, string backup)
{
    File.Copy(path, backup);

    var module = ModuleDefinition.FromFile(path);
    var corlib = module.CorLibTypeFactory;
    var scope  = corlib.CorLibScope;
    var configPathInGameDir = Path.Combine(Path.GetDirectoryName(path)!, ConfigFileName);

    // ---- type references ---------------------------------------------------
    var fileType      = new TypeReference(module, scope, "System.IO", "File");
    var stringType    = new TypeReference(module, scope, "System",    "String");
    var dateTimeType  = new TypeReference(module, scope, "System",    "DateTime");
    var dateTimeSig   = new TypeDefOrRefSignature(dateTimeType);

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
    li.Add(CilOpCodes.Ldstr,    "network_debug.log");
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
    if (sysRef is not null)
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
            Console.WriteLine($"Restored original {LidgrenDllName}.");
            restoredAny = true;
        }
    }

    string? exePath = FindFile(ExeName);
    if (exePath is not null)
    {
        string browserBackup = exePath + ".browserbak";
        if (File.Exists(browserBackup))
        {
            File.Copy(browserBackup, exePath, overwrite: true);
            File.Delete(browserBackup);
            Console.WriteLine($"Restored original {ExeName} browser behavior.");
            restoredAny = true;
        }
    }

    if (!restoredAny)
    {
        Console.Error.WriteLine("No patch backups found â€” may already be unpatched.");
        Environment.Exit(1);
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

void PatchEmptyServerListMessage(string exePath, string backup)
{
    File.Copy(exePath, backup);

    var module = ModuleDefinition.FromFile(exePath);
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
    module.Write(exePath);

    Console.WriteLine("Patched ApotheonArena.exe â€” empty server lists now show 'No games available.'");
    Console.WriteLine($"Backup saved as {Path.GetFileName(backup)}");
}

void Diagnose()
{
    const string BackupName = "ApotheonArena.exe.diagbak";

    string? exePath = FindFile(ExeName);
    if (exePath is null)
    {
        Console.Error.WriteLine($"Could not find {ExeName}.");
        Environment.Exit(1); return;
    }

    string backup = Path.Combine(Path.GetDirectoryName(exePath)!, BackupName);
    if (File.Exists(backup))
    {
        Console.Error.WriteLine("Diagnose patch already applied. Run 'undiagnose' first.");
        Environment.Exit(1); return;
    }

    File.Copy(exePath, backup);

    var module  = ModuleDefinition.FromFile(exePath);
    var corlib  = module.CorLibTypeFactory;
    var scope   = corlib.CorLibScope;

    var fileType  = new TypeReference(module, scope, "System.IO", "File");
    var appendAll = new MemberReference(fileType, "AppendAllText",
        MethodSignature.CreateStatic(corlib.Void, corlib.String, corlib.String));

    var startType   = module.TopLevelTypes.First(t => t.Namespace == "Apotheon" && t.Name == "Start");
    var crashMethod = startType.Methods.First(m => m.Name == "Crash");
    var body        = crashMethod.CilMethodBody!;
    var exParam     = crashMethod.Parameters[2];

    var exType     = new TypeReference(module, scope, "System", "Exception");
    var exToString = new MemberReference(exType, "ToString",
        MethodSignature.CreateInstance(corlib.String));
    var strConcat  = new MemberReference(
        new TypeReference(module, scope, "System", "String"), "Concat",
        MethodSignature.CreateStatic(corlib.String, corlib.String, corlib.String, corlib.String));

    var logEntryVar = new CilLocalVariable(corlib.String);
    body.LocalVariables.Add(logEntryVar);

    var prefix = new List<CilInstruction>
    {
        new(CilOpCodes.Ldstr,    "---CRASH---\n"),
        new(CilOpCodes.Ldarg,    exParam),
        new(CilOpCodes.Callvirt, exToString),
        new(CilOpCodes.Ldstr,    "\n"),
        new(CilOpCodes.Call,     strConcat),
        new(CilOpCodes.Stloc,    logEntryVar),
        new(CilOpCodes.Ldstr,    "crash.log"),
        new(CilOpCodes.Ldloc,    logEntryVar),
        new(CilOpCodes.Call,     appendAll),
    };

    for (int i = 0; i < prefix.Count; i++)
        body.Instructions.Insert(i, prefix[i]);

    body.Instructions.OptimizeMacros();
    module.Write(exePath);

    Console.WriteLine("Diagnose patch applied to ApotheonArena.exe.");
    Console.WriteLine("Reproduce the crash, then check crash.log in the game folder.");
}

void Undiagnose()
{
    const string BackupName = "ApotheonArena.exe.diagbak";

    string? exePath = FindFile(ExeName);
    if (exePath is null) { Console.Error.WriteLine($"Could not find {ExeName}."); Environment.Exit(1); return; }

    string backup = Path.Combine(Path.GetDirectoryName(exePath)!, BackupName);
    if (!File.Exists(backup)) { Console.Error.WriteLine("No diagnose backup found."); Environment.Exit(1); return; }

    File.Copy(backup, exePath, overwrite: true);
    File.Delete(backup);
    Console.WriteLine("Diagnose patch removed.");
}

void NetDebug()
{
    string? dll = FindFile(LidgrenDllName);
    if (dll is null)
    {
        Console.Error.WriteLine($"Could not find {LidgrenDllName}.");
        Environment.Exit(1); return;
    }

    string backup = dll + ".netdebugbak";
    if (File.Exists(backup))
    {
        Console.Error.WriteLine("Network debug already enabled. Run 'unnetdebug' first.");
        Environment.Exit(1); return;
    }

    File.Copy(dll, backup);

    var module     = ModuleDefinition.FromFile(dll);
    var corlib     = module.CorLibTypeFactory;
    var scope      = corlib.CorLibScope;

    var fileType   = new TypeReference(module, scope, "System.IO", "File");
    var stringType = new TypeReference(module, scope, "System",    "String");
    var dateTimeType = new TypeReference(module, scope, "System",  "DateTime");
    var dateTimeSig  = new TypeDefOrRefSignature(dateTimeType);

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
        li.Add(CilOpCodes.Ldstr,    "network_debug.log");
        li.Add(CilOpCodes.Ldloc,    lineVar);
        li.Add(CilOpCodes.Call,     appendAll);
        li.Add(CilOpCodes.Ret);

        lBody.Instructions.OptimizeMacros();
        logHelper.CilMethodBody = lBody;
        netUtility.Methods.Add(logHelper);
    }
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
    Console.WriteLine("Network debug enabled. Logged to network_debug.log:");
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
    if (dll is null) { Console.Error.WriteLine($"Could not find {LidgrenDllName}."); Environment.Exit(1); return; }

    string backup = dll + ".netdebugbak";
    if (!File.Exists(backup)) { Console.Error.WriteLine("Network debug not enabled."); Environment.Exit(1); return; }

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
    Console.WriteLine("  ApotheonArenaMPpatch.exe restore    restore original Lidgren.Network.dll");
    Console.WriteLine("  ApotheonArenaMPpatch.exe diagnose   inject crash logger into ApotheonArena.exe");
    Console.WriteLine("  ApotheonArenaMPpatch.exe undiagnose remove crash logger");
    Console.WriteLine("  ApotheonArenaMPpatch.exe netdebug   log networking calls to network_debug.log");
    Console.WriteLine("  ApotheonArenaMPpatch.exe unnetdebug remove network debug logging");
    Console.WriteLine();
    Console.WriteLine($"  After patching, edit {ConfigFileName} in the game folder.");
    Console.WriteLine("  Supports any IP or hostname - no length limit.");
}
