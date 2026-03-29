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
const string ConfigFileName = "master_server.txt";

if (args.Length == 1 && args[0].Equals("restore", StringComparison.OrdinalIgnoreCase))
{
    Restore();
    return;
}

if (args.Length == 1 && args[0].Equals("netdebug", StringComparison.OrdinalIgnoreCase))
{
    NetDebug();
    return;
}

if (args.Length == 1 && args[0].Equals("unnetdebug", StringComparison.OrdinalIgnoreCase))
{
    UnNetDebug();
    return;
}

if (args.Length == 1 && args[0].Equals("diagnose", StringComparison.OrdinalIgnoreCase))
{
    Diagnose();
    return;
}

if (args.Length == 1 && args[0].Equals("undiagnose", StringComparison.OrdinalIgnoreCase))
{
    Undiagnose();
    return;
}

if (args.Length != 0)
{
    PrintUsage();
    return;
}

string? dllPath = FindFile(LidgrenDllName);
if (dllPath is null)
{
    Console.Error.WriteLine($"Could not find {LidgrenDllName} — place Patcher.exe in the game folder.");
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

PatchLidgren(dllPath, backupPath);

// Create master_server.txt in the game folder if it doesn't already exist
string gameDir    = Path.GetDirectoryName(dllPath)!;
string configPath = Path.Combine(gameDir, ConfigFileName);
if (!File.Exists(configPath))
{
    File.WriteAllText(configPath,
        "# Apotheon Arena - Community Master Server\n" +
        "# Set the IP address or hostname of your master server below.\n" +
        "# Any length is supported. Restart the game after changing.\n" +
        $"{OriginalIp}\n");
    Console.WriteLine($"Created {ConfigFileName} in the game folder — edit it to point at your server.");
}

Console.WriteLine("Done! Edit master_server.txt whenever you need to change the server.");

// ---------------------------------------------------------------------------

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

    // ---- method references -------------------------------------------------
    var fileExists    = new MemberReference(fileType, "Exists",
        MethodSignature.CreateStatic(corlib.Boolean, corlib.String));
    var appendAllText = new MemberReference(fileType, "AppendAllText",
        MethodSignature.CreateStatic(corlib.Void, corlib.String, corlib.String));
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

    // ---- inject helper: static string __ReadServerIp(string path) ----------
    // Reads lines from path, skips blank lines and lines starting with '#',
    // returns the first valid line, or null if none found.
    var netUtility = module.TopLevelTypes.First(t => t.Name == "NetUtility");

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

    // File.AppendAllText("network_debug.log", "[master] " + t + "\n")
    hi.Add(CilOpCodes.Ldstr,    "network_debug.log");
    hi.Add(CilOpCodes.Ldstr,    "[master] ");
    hi.Add(CilOpCodes.Ldloc,    tVar);
    hi.Add(CilOpCodes.Ldstr,    "\n");
    hi.Add(CilOpCodes.Call,     strConcat3);
    hi.Add(CilOpCodes.Call,     appendAllText);
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

    // ---- Inject __GetBestLocalIp: skip APIPA/link-local (169.254.x.x) ---------
    // If GetMyAddress() returns a link-local address (e.g. Hyper-V virtual adapter),
    // this helper walks Dns.GetHostAddresses to find the first real IPv4 address.
    var sysRef = module.AssemblyReferences.FirstOrDefault(r => r.Name == "System");
    if (sysRef is not null)
    {
        var ipAddrRef    = new TypeReference(module, sysRef, "System.Net", "IPAddress");
        var dnsRef       = new TypeReference(module, sysRef, "System.Net", "Dns");
        var ipAddrSig    = new TypeDefOrRefSignature(ipAddrRef);
        var ipAddrArrSig = new SzArrayTypeSignature(ipAddrSig);
        var byteArrSig   = new SzArrayTypeSignature(corlib.Byte);

        var getAddrBytes = new MemberReference(ipAddrRef, "GetAddressBytes",
            MethodSignature.CreateInstance(byteArrSig));
        var dnsHostName  = new MemberReference(dnsRef, "GetHostName",
            MethodSignature.CreateStatic(corlib.String));
        var dnsHostAddrs = new MemberReference(dnsRef, "GetHostAddresses",
            MethodSignature.CreateStatic(ipAddrArrSig, corlib.String));

        var bestIpHelper = new MethodDefinition("__GetBestLocalIp",
            MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(ipAddrSig, ipAddrSig));

        var bipBody = new CilMethodBody(bestIpHelper);
        var bBytes  = new CilLocalVariable(byteArrSig);
        var bAddrs  = new CilLocalVariable(ipAddrArrSig);
        var bIdx    = new CilLocalVariable(corlib.Int32);
        var bAddr   = new CilLocalVariable(ipAddrSig);
        var bC      = new CilLocalVariable(byteArrSig);
        bipBody.LocalVariables.Add(bBytes);
        bipBody.LocalVariables.Add(bAddrs);
        bipBody.LocalVariables.Add(bIdx);
        bipBody.LocalVariables.Add(bAddr);
        bipBody.LocalVariables.Add(bC);

        var loopLbl    = new CilInstructionLabel();
        var checkLbl   = new CilInstructionLabel();
        var nextLbl    = new CilInstructionLabel();
        var retAddrLbl = new CilInstructionLabel();
        var retOrigLbl = new CilInstructionLabel();
        var bi         = bipBody.Instructions;

        // b = original.GetAddressBytes()
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
        bi.Add(CilOpCodes.Br,      checkLbl);

        // loop: addr = addrs[i]
        var bipLoopStart = new CilInstruction(CilOpCodes.Ldloc, bAddrs);
        bi.Add(bipLoopStart); loopLbl.Instruction = bipLoopStart;
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
        bi.Add(CilOpCodes.Bne_Un,  nextLbl);
        // if (c[0] == 127) goto next   [loopback]
        bi.Add(CilOpCodes.Ldloc,   bC);
        bi.Add(CilOpCodes.Ldc_I4_0);
        bi.Add(CilOpCodes.Ldelem_U1);
        bi.Add(CilOpCodes.Ldc_I4,  127);
        bi.Add(CilOpCodes.Beq,     nextLbl);
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
        bi.Add(CilOpCodes.Beq,     nextLbl);
        // retAddr: return addr
        var retAddrStart = new CilInstruction(CilOpCodes.Ldloc, bAddr);
        bi.Add(retAddrStart); retAddrLbl.Instruction = retAddrStart;
        bi.Add(CilOpCodes.Ret);
        // next: i++
        var bipNextStart = new CilInstruction(CilOpCodes.Ldloc, bIdx);
        bi.Add(bipNextStart); nextLbl.Instruction = bipNextStart;
        bi.Add(CilOpCodes.Ldc_I4_1);
        bi.Add(CilOpCodes.Add);
        bi.Add(CilOpCodes.Stloc, bIdx);
        // check: if (i < addrs.Length) goto loop
        var bipCheckStart = new CilInstruction(CilOpCodes.Ldloc, bIdx);
        bi.Add(bipCheckStart); checkLbl.Instruction = bipCheckStart;
        bi.Add(CilOpCodes.Ldloc,  bAddrs);
        bi.Add(CilOpCodes.Ldlen);
        bi.Add(CilOpCodes.Conv_I4);
        bi.Add(CilOpCodes.Blt,    loopLbl);
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
            Console.WriteLine("Injected local-IP fix into NetUtility.GetMyAddress.");
        }
    }

    module.Write(path);

    Console.WriteLine($"Patched {LidgrenDllName} — injected config file lookup into NetUtility.Resolve.");
    Console.WriteLine($"Backup saved as {Path.GetFileName(backup)}");
}

void Restore()
{
    string? dll = FindFile(LidgrenDllName);
    if (dll is null)
    {
        Console.Error.WriteLine($"Could not find {LidgrenDllName}.");
        Environment.Exit(1);
        return;
    }

    string backup = dll + ".bak";
    if (!File.Exists(backup))
    {
        Console.Error.WriteLine("No backup found — may already be unpatched.");
        Environment.Exit(1);
        return;
    }

    File.Copy(backup, dll, overwrite: true);
    File.Delete(backup);
    Console.WriteLine($"Restored original {LidgrenDllName}.");
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

void Diagnose()
{
    const string ExeName    = "ApotheonArena.exe";
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
    const string ExeName    = "ApotheonArena.exe";
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

    var appendAll  = new MemberReference(fileType,   "AppendAllText",
        MethodSignature.CreateStatic(corlib.Void, corlib.String, corlib.String));
    var concat3    = new MemberReference(stringType, "Concat",
        MethodSignature.CreateStatic(corlib.String, corlib.String, corlib.String, corlib.String));
    var concat4    = new MemberReference(stringType, "Concat",
        MethodSignature.CreateStatic(corlib.String, corlib.String, corlib.String, corlib.String, corlib.String));

    var toString   = new MemberReference(
        new TypeReference(module, scope, "System", "Int32"), "ToString",
        MethodSignature.CreateInstance(corlib.String));

    // Patch NetUtility.Resolve(string, int) — log every call
    var netUtility = module.TopLevelTypes.First(t => t.Name == "NetUtility");
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
        // File.AppendAllText("network_debug.log", "[resolve] " + ipOrHost + ":" + portStr + "\n")
        new(CilOpCodes.Ldstr,   "network_debug.log"),
        new(CilOpCodes.Ldstr,   "[resolve] "),
        new(CilOpCodes.Ldarg,   resolve.Parameters[0]),
        new(CilOpCodes.Ldstr,   ":"),
        new(CilOpCodes.Call,    concat3),
        new(CilOpCodes.Ldloc,   portStrVar),
        new(CilOpCodes.Ldstr,   "\n"),
        new(CilOpCodes.Call,    concat3),
        new(CilOpCodes.Call,    appendAll),
    };

    for (int i = 0; i < resolvePrefix.Count; i++)
        rBody.Instructions.Insert(i, resolvePrefix[i]);
    rBody.Instructions.OptimizeMacros();

    // Patch NetPeer.Connect(string host, int port) — log every connect attempt
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
            new(CilOpCodes.Ldstr,  "network_debug.log"),
            new(CilOpCodes.Ldstr,  "[connect] "),
            new(CilOpCodes.Ldarg,  connect.Parameters[0]),
            new(CilOpCodes.Ldstr,  ":"),
            new(CilOpCodes.Call,   concat3),
            new(CilOpCodes.Ldloc,  cPortVar),
            new(CilOpCodes.Ldstr,  "\n"),
            new(CilOpCodes.Call,   concat3),
            new(CilOpCodes.Call,   appendAll),
        };

        for (int i = 0; i < connectPrefix.Count; i++)
            cBody.Instructions.Insert(i, connectPrefix[i]);
        cBody.Instructions.OptimizeMacros();
    }

    var objToString = new MemberReference(
        new TypeReference(module, scope, "System", "Object"), "ToString",
        MethodSignature.CreateInstance(corlib.String));

    // Patch NetPeer.Connect(IPEndPoint) — log P2P connection attempts
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
            new(CilOpCodes.Ldstr,   "network_debug.log"),
            new(CilOpCodes.Ldstr,   "[connect-ep] "),
            new(CilOpCodes.Ldloc,   epStrVar),
            new(CilOpCodes.Ldstr,   "\n"),
            new(CilOpCodes.Call,    concat3),
            new(CilOpCodes.Call,    appendAll),
        };

        for (int i = 0; i < epPrefix.Count; i++)
            epBody.Instructions.Insert(i, epPrefix[i]);
        epBody.Instructions.OptimizeMacros();
    }

    // Patch NetPeer.SendUnconnectedMessage(NetOutgoingMessage, IPEndPoint) — log master server traffic
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
            new(CilOpCodes.Ldstr,   "network_debug.log"),
            new(CilOpCodes.Ldstr,   "[send-unconnected] → "),
            new(CilOpCodes.Ldloc,   suStrVar),
            new(CilOpCodes.Ldstr,   "\n"),
            new(CilOpCodes.Call,    concat3),
            new(CilOpCodes.Call,    appendAll),
        };

        for (int i = 0; i < suPrefix.Count; i++)
            suBody.Instructions.Insert(i, suPrefix[i]);
        suBody.Instructions.OptimizeMacros();
    }

    // Patch GetMyAddress — log the local IP Lidgren picks for this machine.
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
                new(CilOpCodes.Ldstr,    "network_debug.log"),
                new(CilOpCodes.Ldstr,    "[local-ip] "),
                new(CilOpCodes.Ldloc,    gaStrVar),
                new(CilOpCodes.Ldstr,    "\n"),
                new(CilOpCodes.Call,     concat3),
                new(CilOpCodes.Call,     appendAll),
            };

            for (int i = 0; i < logCode.Count; i++)
                instrs.Insert(retIdx + i, logCode[i]);
        }
        gaBody.Instructions.OptimizeMacros();
    }

    // Patch NetConnection.SetStatus — log every connection state change with endpoint.
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
            // File.AppendAllText("network_debug.log", "[conn-status] " + ep + " -> " + status + "\n")
            new(CilOpCodes.Ldstr,    "network_debug.log"),
            new(CilOpCodes.Ldstr,    "[conn-status] "),
            new(CilOpCodes.Ldloc,    ssEpVar),
            new(CilOpCodes.Ldstr,    " -> "),
            new(CilOpCodes.Call,     concat3),
            new(CilOpCodes.Ldloc,    ssStatusVar),
            new(CilOpCodes.Ldstr,    "\n"),
            new(CilOpCodes.Call,     concat3),
            new(CilOpCodes.Call,     appendAll),
        };

        for (int i = 0; i < ssPrefix.Count; i++)
            ssBody.Instructions.Insert(i, ssPrefix[i]);
        ssBody.Instructions.OptimizeMacros();
    }

    module.Write(dll);
    Console.WriteLine("Network debug enabled. Logged to network_debug.log:");
    Console.WriteLine("  [master]           master server IP from config file");
    Console.WriteLine("  [resolve]          DNS/IP resolution");
    Console.WriteLine("  [local-ip]         local IP Lidgren detected for this machine");
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
    Console.WriteLine("Apotheon Arena — Master Server Patcher");
    Console.WriteLine();
    Console.WriteLine("  ApotheonArenaMPpatch.exe            patch the game (run once)");
    Console.WriteLine("  ApotheonArenaMPpatch.exe restore    restore original Lidgren.Network.dll");
    Console.WriteLine("  ApotheonArenaMPpatch.exe diagnose   inject crash logger into ApotheonArena.exe");
    Console.WriteLine("  ApotheonArenaMPpatch.exe undiagnose remove crash logger");
    Console.WriteLine("  ApotheonArenaMPpatch.exe netdebug   log networking calls to network_debug.log");
    Console.WriteLine("  ApotheonArenaMPpatch.exe unnetdebug remove network debug logging");
    Console.WriteLine();
    Console.WriteLine($"  After patching, edit {ConfigFileName} in the game folder.");
    Console.WriteLine("  Supports any IP or hostname — no length limit.");
}
