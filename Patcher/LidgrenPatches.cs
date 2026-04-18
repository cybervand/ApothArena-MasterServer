using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.DotNet.Signatures.Types;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables.Rows;
using static Constants;

internal static class LidgrenPatches
{
    public static void PatchSafeLocalHostIpOverride()
    {
        const string BackupSuffix = ".localipbak";

        string? dllPath = Paths.Find(LidgrenDllName);
        if (dllPath is null)
        {
            Console.Error.WriteLine($"Could not find {LidgrenDllName}.");
            Cli.Exit(1);
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
        var resolveStringSig = (MethodSignature)resolveStringMethod.Signature!;
        var ipAddressSig = resolveStringSig.ReturnType;

        var preferredIpHelper = netUtility.Methods.FirstOrDefault(m => m.Name == "__GetPreferredLocalIp");
        if (preferredIpHelper is null)
        {
            preferredIpHelper = new MethodDefinition("__GetPreferredLocalIp",
                MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
                MethodSignature.CreateStatic(ipAddressSig, ipAddressSig));

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

    public static void PatchLidgren(string path, string backup, bool includeRouteAwareLocalIpFix)
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

        hi.Add(CilOpCodes.Ldarg_0);
        hi.Add(CilOpCodes.Call,     readAllLines);
        hi.Add(CilOpCodes.Stloc,    linesVar);
        hi.Add(CilOpCodes.Ldc_I4_0);
        hi.Add(CilOpCodes.Stloc,    iVar);
        hi.Add(CilOpCodes.Br,       checkLabel);

        var loopStart = new CilInstruction(CilOpCodes.Ldloc, linesVar);
        hi.Add(loopStart);
        loopLabel.Instruction = loopStart;
        hi.Add(CilOpCodes.Ldloc,    iVar);
        hi.Add(CilOpCodes.Ldelem_Ref);
        hi.Add(CilOpCodes.Callvirt, strTrim);
        hi.Add(CilOpCodes.Stloc,    tVar);

        hi.Add(CilOpCodes.Ldloc,    tVar);
        hi.Add(CilOpCodes.Callvirt, strGetLength);
        hi.Add(CilOpCodes.Ldc_I4_0);
        hi.Add(CilOpCodes.Ble,      nextLabel);

        hi.Add(CilOpCodes.Ldloc,    tVar);
        hi.Add(CilOpCodes.Ldc_I4_0);
        hi.Add(CilOpCodes.Callvirt, strGetChars);
        hi.Add(CilOpCodes.Ldc_I4_S, (sbyte)35); // '#'
        hi.Add(CilOpCodes.Beq,      nextLabel);

        hi.Add(CilOpCodes.Ldstr,    "[master] ");
        hi.Add(CilOpCodes.Ldloc,    tVar);
        hi.Add(CilOpCodes.Ldstr,    "\n");
        hi.Add(CilOpCodes.Call,     strConcat3);
        hi.Add(CilOpCodes.Call,     logHelper);
        hi.Add(CilOpCodes.Ldloc,    tVar);
        hi.Add(CilOpCodes.Ret);

        var nextStart = new CilInstruction(CilOpCodes.Ldloc, iVar);
        hi.Add(nextStart);
        nextLabel.Instruction = nextStart;
        hi.Add(CilOpCodes.Ldc_I4_1);
        hi.Add(CilOpCodes.Add);
        hi.Add(CilOpCodes.Stloc,    iVar);

        var checkStart = new CilInstruction(CilOpCodes.Ldloc, iVar);
        hi.Add(checkStart);
        checkLabel.Instruction = checkStart;
        hi.Add(CilOpCodes.Ldloc,    linesVar);
        hi.Add(CilOpCodes.Ldlen);
        hi.Add(CilOpCodes.Conv_I4);
        hi.Add(CilOpCodes.Blt,      loopLabel);

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
            new(CilOpCodes.Ldarg,    resolve.Parameters[0]),
            new(CilOpCodes.Brfalse,  skipLabel),

            new(CilOpCodes.Ldarg,    resolve.Parameters[0]),
            new(CilOpCodes.Callvirt, strTrim),
            new(CilOpCodes.Ldstr,    OriginalIp),
            new(CilOpCodes.Call,     strEquals),
            new(CilOpCodes.Brfalse,  skipLabel),

            new(CilOpCodes.Ldstr,    configPathInGameDir),
            new(CilOpCodes.Stloc,    pathVar),

            new(CilOpCodes.Ldloc,    pathVar),
            new(CilOpCodes.Call,     fileExists),
            new(CilOpCodes.Brfalse,  skipLabel),

            new(CilOpCodes.Ldloc,    pathVar),
            new(CilOpCodes.Call,     helper),
            new(CilOpCodes.Stloc,    resultVar),

            new(CilOpCodes.Ldloc,    resultVar),
            new(CilOpCodes.Brfalse,  skipLabel),

            new(CilOpCodes.Ldloc,    resultVar),
            new(CilOpCodes.Starg,    resolve.Parameters[0]),
        };

        for (int i = 0; i < prefix.Count; i++)
            body.Instructions.Insert(i, prefix[i]);

        body.Instructions.OptimizeMacros();

        // ---- Inject __GetBestLocalIp: prefer the route Windows would actually use ---
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
            bi.Add(CilOpCodes.Ldc_I4_0);
            bi.Add(CilOpCodes.Stloc,    bIdx);
            bi.Add(CilOpCodes.Br,       routeCheckLbl);

            var routeLoopStart = new CilInstruction(CilOpCodes.Ldloc, bAddrs);
            bi.Add(routeLoopStart); routeLoopLbl.Instruction = routeLoopStart;
            bi.Add(CilOpCodes.Ldloc,    bIdx);
            bi.Add(CilOpCodes.Ldelem_Ref);
            bi.Add(CilOpCodes.Stloc,    bAddr);
            bi.Add(CilOpCodes.Ldloc,    bAddr);
            bi.Add(CilOpCodes.Callvirt, getAddrBytes);
            bi.Add(CilOpCodes.Stloc,    bC);
            bi.Add(CilOpCodes.Ldloc,    bC);
            bi.Add(CilOpCodes.Ldlen);
            bi.Add(CilOpCodes.Conv_I4);
            bi.Add(CilOpCodes.Ldc_I4,   4);
            bi.Add(CilOpCodes.Bne_Un,   routeNextLbl);
            bi.Add(CilOpCodes.Ldc_I4,   2);  // AddressFamily.InterNetwork
            bi.Add(CilOpCodes.Ldc_I4,   2);  // SocketType.Dgram
            bi.Add(CilOpCodes.Ldc_I4,   17); // ProtocolType.Udp
            bi.Add(CilOpCodes.Newobj,   socketCtor);
            bi.Add(CilOpCodes.Stloc,    bSocket);
            bi.Add(CilOpCodes.Ldloc,    bSocket);
            bi.Add(CilOpCodes.Ldloc,    bAddr);
            bi.Add(CilOpCodes.Ldc_I4,   14343);
            bi.Add(CilOpCodes.Newobj,   ipEpCtor);
            bi.Add(CilOpCodes.Callvirt, socketConnect);
            bi.Add(CilOpCodes.Ldloc,    bSocket);
            bi.Add(CilOpCodes.Callvirt, socketGetLocalEndPoint);
            bi.Add(CilOpCodes.Castclass, ipEpRef);
            bi.Add(CilOpCodes.Stloc,    bLocalEp);
            bi.Add(CilOpCodes.Ldloc,    bSocket);
            bi.Add(CilOpCodes.Callvirt, socketClose);
            bi.Add(CilOpCodes.Ldloc,    bLocalEp);
            bi.Add(CilOpCodes.Brfalse,  routeNextLbl);
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
            var routeNextStart = new CilInstruction(CilOpCodes.Ldloc, bIdx);
            bi.Add(routeNextStart); routeNextLbl.Instruction = routeNextStart;
            bi.Add(CilOpCodes.Ldc_I4_1);
            bi.Add(CilOpCodes.Add);
            bi.Add(CilOpCodes.Stloc,    bIdx);
            var routeCheckStart = new CilInstruction(CilOpCodes.Ldloc, bIdx);
            bi.Add(routeCheckStart); routeCheckLbl.Instruction = routeCheckStart;
            bi.Add(CilOpCodes.Ldloc,    bAddrs);
            bi.Add(CilOpCodes.Ldlen);
            bi.Add(CilOpCodes.Conv_I4);
            bi.Add(CilOpCodes.Blt,      routeLoopLbl);

            bi.Add(CilOpCodes.Ldarg_0);
            bi.Add(CilOpCodes.Brfalse,  retOrigLbl);
            bi.Add(CilOpCodes.Ldarg_0);
            bi.Add(CilOpCodes.Callvirt, getAddrBytes);
            bi.Add(CilOpCodes.Stloc, bBytes);
            bi.Add(CilOpCodes.Ldloc,   bBytes);
            bi.Add(CilOpCodes.Ldlen);
            bi.Add(CilOpCodes.Conv_I4);
            bi.Add(CilOpCodes.Ldc_I4,  4);
            bi.Add(CilOpCodes.Bne_Un,  retOrigLbl);
            bi.Add(CilOpCodes.Ldloc,   bBytes);
            bi.Add(CilOpCodes.Ldc_I4_0);
            bi.Add(CilOpCodes.Ldelem_U1);
            bi.Add(CilOpCodes.Ldc_I4,  169);
            bi.Add(CilOpCodes.Bne_Un,  retOrigLbl);
            bi.Add(CilOpCodes.Ldloc,   bBytes);
            bi.Add(CilOpCodes.Ldc_I4_1);
            bi.Add(CilOpCodes.Ldelem_U1);
            bi.Add(CilOpCodes.Ldc_I4,  254);
            bi.Add(CilOpCodes.Bne_Un,  retOrigLbl);
            bi.Add(CilOpCodes.Call,    dnsHostName);
            bi.Add(CilOpCodes.Call,    dnsHostAddrs);
            bi.Add(CilOpCodes.Stloc,   bAddrs);
            bi.Add(CilOpCodes.Ldc_I4_0);
            bi.Add(CilOpCodes.Stloc,   bIdx);
            bi.Add(CilOpCodes.Br,      scanCheckLbl);

            var bipLoopStart = new CilInstruction(CilOpCodes.Ldloc, bAddrs);
            bi.Add(bipLoopStart); scanLoopLbl.Instruction = bipLoopStart;
            bi.Add(CilOpCodes.Ldloc,   bIdx);
            bi.Add(CilOpCodes.Ldelem_Ref);
            bi.Add(CilOpCodes.Stloc,   bAddr);
            bi.Add(CilOpCodes.Ldloc,   bAddr);
            bi.Add(CilOpCodes.Callvirt, getAddrBytes);
            bi.Add(CilOpCodes.Stloc,   bC);
            bi.Add(CilOpCodes.Ldloc,   bC);
            bi.Add(CilOpCodes.Ldlen);
            bi.Add(CilOpCodes.Conv_I4);
            bi.Add(CilOpCodes.Ldc_I4,  4);
            bi.Add(CilOpCodes.Bne_Un,  scanNextLbl);
            bi.Add(CilOpCodes.Ldloc,   bC);
            bi.Add(CilOpCodes.Ldc_I4_0);
            bi.Add(CilOpCodes.Ldelem_U1);
            bi.Add(CilOpCodes.Ldc_I4,  127);
            bi.Add(CilOpCodes.Beq,     scanNextLbl);
            bi.Add(CilOpCodes.Ldloc,   bC);
            bi.Add(CilOpCodes.Ldc_I4_0);
            bi.Add(CilOpCodes.Ldelem_U1);
            bi.Add(CilOpCodes.Ldc_I4,  169);
            bi.Add(CilOpCodes.Bne_Un,  retAddrLbl);
            bi.Add(CilOpCodes.Ldloc,   bC);
            bi.Add(CilOpCodes.Ldc_I4_1);
            bi.Add(CilOpCodes.Ldelem_U1);
            bi.Add(CilOpCodes.Ldc_I4,  254);
            bi.Add(CilOpCodes.Beq,     scanNextLbl);
            var retAddrStart = new CilInstruction(CilOpCodes.Ldloc, bAddr);
            bi.Add(retAddrStart); retAddrLbl.Instruction = retAddrStart;
            bi.Add(CilOpCodes.Ret);
            var bipNextStart = new CilInstruction(CilOpCodes.Ldloc, bIdx);
            bi.Add(bipNextStart); scanNextLbl.Instruction = bipNextStart;
            bi.Add(CilOpCodes.Ldc_I4_1);
            bi.Add(CilOpCodes.Add);
            bi.Add(CilOpCodes.Stloc, bIdx);
            var bipCheckStart = new CilInstruction(CilOpCodes.Ldloc, bIdx);
            bi.Add(bipCheckStart); scanCheckLbl.Instruction = bipCheckStart;
            bi.Add(CilOpCodes.Ldloc,  bAddrs);
            bi.Add(CilOpCodes.Ldlen);
            bi.Add(CilOpCodes.Conv_I4);
            bi.Add(CilOpCodes.Blt,    scanLoopLbl);
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

        Console.WriteLine($"Patched {LidgrenDllName} - injected config file lookup into NetUtility.Resolve.");
        Console.WriteLine($"Backup saved as {Path.GetFileName(backup)}");
    }
}
