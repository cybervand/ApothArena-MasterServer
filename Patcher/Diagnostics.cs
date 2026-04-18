using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.DotNet.Signatures.Types;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables.Rows;
using static Constants;

internal static class Diagnostics
{
    public static void InspectType(string typeName)
    {
        string? exePath = Paths.Find(ExeName);
        if (exePath is null)
        {
            Console.Error.WriteLine($"Could not find {ExeName}.");
            Cli.Exit(1);
            return;
        }

        var module = ModuleDefinition.FromFile(exePath);
        var type = module.GetAllTypes().FirstOrDefault(t => t.FullName == typeName || t.Name == typeName);
        if (type is null)
        {
            Console.Error.WriteLine($"Type not found: {typeName}");
            Cli.Exit(1);
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

    public static void InspectMethod(string typeName, string methodName)
    {
        string? exePath = Paths.Find(ExeName);
        if (exePath is null)
        {
            Console.Error.WriteLine($"Could not find {ExeName}.");
            Cli.Exit(1);
            return;
        }

        var module = ModuleDefinition.FromFile(exePath);
        var type = module.GetAllTypes().FirstOrDefault(t => t.FullName == typeName || t.Name == typeName);
        if (type is null)
        {
            Console.Error.WriteLine($"Type not found: {typeName}");
            Cli.Exit(1);
            return;
        }

        var methods = type.Methods.Where(m => m.Name == methodName).ToList();
        if (methods.Count == 0)
        {
            Console.Error.WriteLine($"Method not found: {type.FullName}::{methodName}");
            Cli.Exit(1);
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

    static string FormatInspectLabel(AsmResolver.PE.DotNet.Cil.ICilLabel? label)
    {
        if (label is null) return "<null>";
        if (label is CilInstructionLabel cil && cil.Instruction is not null)
            return $"{cil.Instruction.OpCode}";
        return label.ToString() ?? "<?>";
    }

    static string FormatInspectOperand(object? operand)
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

    public static void Diagnose()
    {
        string? exePath = Paths.Find(ExeName);
        if (exePath is null)
        {
            Console.Error.WriteLine($"Could not find {ExeName}.");
            Cli.Exit(1); return;
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

    public static void Undiagnose()
    {
        const string BackupName = "ApotheonArena.exe.diagbak";

        string? exePath = Paths.Find(ExeName);
        if (exePath is null) { Console.Error.WriteLine($"Could not find {ExeName}."); Cli.Exit(1); return; }

        string backup = Path.Combine(Path.GetDirectoryName(exePath)!, BackupName);
        if (!File.Exists(backup)) { Console.Error.WriteLine("No diagnose backup found."); Cli.Exit(1); return; }

        File.Copy(backup, exePath, overwrite: true);
        File.Delete(backup);
        Console.WriteLine("Diagnose patch removed.");
    }

    public static void EnableNetDebug()
    {
        const string BackupName = "ApotheonArena.exe.netdebugbak";

        string? exePath = Paths.Find(ExeName);
        if (exePath is null)
        {
            Console.Error.WriteLine($"Could not find {ExeName}.");
            Cli.Exit(1);
            return;
        }

        string exeDir = Path.GetDirectoryName(exePath)!;
        string backup = Path.Combine(exeDir, BackupName);
        string? dll = Paths.Find(LidgrenDllName);
        string? legacyBackup = dll is null ? null : dll + ".netdebugbak";

        if (File.Exists(backup))
        {
            Console.Error.WriteLine("Network debug already enabled. Run 'unnetdebug' first.");
            Cli.Exit(1);
            return;
        }

        if (legacyBackup is not null && File.Exists(legacyBackup))
        {
            Console.Error.WriteLine("Legacy Lidgren network debug backup found. Run 'unnetdebug' first.");
            Cli.Exit(1);
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

    public static void DisableNetDebug()
    {
        bool removedAny = false;

        string? exePath = Paths.Find(ExeName);
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

        string? dll = Paths.Find(LidgrenDllName);
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
            Cli.Exit(1);
        }
    }

    public static void LegacyLidgrenNetDebug()
    {
        string? dll = Paths.Find(LidgrenDllName);
        if (dll is null)
        {
            Console.Error.WriteLine($"Could not find {LidgrenDllName}.");
            Cli.Exit(1); return;
        }

        string backup = dll + ".netdebugbak";
        if (File.Exists(backup))
        {
            Console.Error.WriteLine("Network debug already enabled. Run 'unnetdebug' first.");
            Cli.Exit(1); return;
        }

        File.Copy(dll, backup);

        var module = ModuleDefinition.FromFile(dll);
        var corlib = module.CorLibTypeFactory;
        var scope = corlib.CorLibScope;

        var fileType = new TypeReference(module, scope, "System.IO", "File");
        var stringType = new TypeReference(module, scope, "System", "String");
        var dateTimeType = new TypeReference(module, scope, "System", "DateTime");
        var dateTimeSig = new TypeDefOrRefSignature(dateTimeType);
        string logsDirPath = Path.Combine(Path.GetDirectoryName(dll)!, "Logs");
        string networkLogPath = Path.Combine(logsDirPath, "network_debug.log");
        Directory.CreateDirectory(logsDirPath);

        var appendAll = new MemberReference(fileType, "AppendAllText",
            MethodSignature.CreateStatic(corlib.Void, corlib.String, corlib.String));
        var concat2 = new MemberReference(stringType, "Concat",
            MethodSignature.CreateStatic(corlib.String, corlib.String, corlib.String));
        var concat3 = new MemberReference(stringType, "Concat",
            MethodSignature.CreateStatic(corlib.String, corlib.String, corlib.String, corlib.String));
        var concat4 = new MemberReference(stringType, "Concat",
            MethodSignature.CreateStatic(corlib.String, corlib.String, corlib.String, corlib.String, corlib.String));

        var toString = new MemberReference(
            new TypeReference(module, scope, "System", "Int32"), "ToString",
            MethodSignature.CreateInstance(corlib.String));
        var getUtcNow = new MemberReference(dateTimeType, "get_UtcNow",
            MethodSignature.CreateStatic(dateTimeSig));
        var dateTimeFmt = new MemberReference(dateTimeType, "ToString",
            MethodSignature.CreateInstance(corlib.String, corlib.String));

        var netUtility = module.TopLevelTypes.First(t => t.Name == "NetUtility");
        var logHelper = netUtility.Methods.FirstOrDefault(m => m.Name == "__NetDebugLog");
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
        var resolve = netUtility.Methods.First(m =>
            m.Name == "Resolve" &&
            m.Parameters.Count == 2 &&
            m.Parameters[1].ParameterType.IsTypeOf("System", "Int32"));

        var rBody = resolve.CilMethodBody!;
        var portStrVar = new CilLocalVariable(corlib.String);
        rBody.LocalVariables.Add(portStrVar);

        var resolvePrefix = new List<CilInstruction>
        {
            new(CilOpCodes.Ldarga,   resolve.Parameters[1]),
            new(CilOpCodes.Call,     toString),
            new(CilOpCodes.Stloc,   portStrVar),
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

        var netPeer = module.TopLevelTypes.First(t => t.Name == "NetPeer");
        var connect = netPeer.Methods.FirstOrDefault(m =>
            m.Name == "Connect" &&
            m.Parameters.Count >= 2 &&
            m.Parameters[0].ParameterType.IsTypeOf("System", "String") &&
            m.Parameters[1].ParameterType.IsTypeOf("System", "Int32"));

        if (connect?.CilMethodBody is not null)
        {
            var cBody = connect.CilMethodBody;
            var cPortVar = new CilLocalVariable(corlib.String);
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

        var connectEp = netPeer.Methods.FirstOrDefault(m =>
            m.Name == "Connect" &&
            m.Parameters.Count >= 1 &&
            m.Parameters[0].ParameterType.IsTypeOf("System.Net", "IPEndPoint"));

        if (connectEp?.CilMethodBody is not null)
        {
            var epBody = connectEp.CilMethodBody;
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

        var sendUnconnected = netPeer.Methods.FirstOrDefault(m =>
            m.Name == "SendUnconnectedMessage" &&
            m.Parameters.Count >= 2 &&
            m.Parameters[1].ParameterType.IsTypeOf("System.Net", "IPEndPoint"));

        if (sendUnconnected?.CilMethodBody is not null)
        {
            var suBody = sendUnconnected.CilMethodBody;
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

        var getMyAddr = netUtility.Methods.FirstOrDefault(m =>
            m.Name == "GetMyAddress" && m.CilMethodBody is not null);

        if (getMyAddr is not null)
        {
            var gaBody = getMyAddr.CilMethodBody!;
            var gaStrVar = new CilLocalVariable(corlib.String);
            gaBody.LocalVariables.Add(gaStrVar);

            var instrs = gaBody.Instructions;
            var retIndices = Enumerable.Range(0, instrs.Count)
                .Where(i => instrs[i].OpCode == CilOpCodes.Ret)
                .OrderByDescending(i => i)
                .ToList();

            foreach (int retIdx in retIndices)
            {
                var retInstr = instrs[retIdx];
                var skipLog = new CilInstructionLabel();
                skipLog.Instruction = retInstr;

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

        var netConn = module.TopLevelTypes.FirstOrDefault(t => t.Name == "NetConnection");
        var setStatus = netConn?.Methods.FirstOrDefault(m =>
            m.Name == "SetStatus" && m.Parameters.Count >= 1 && m.CilMethodBody is not null);
        var getRemoteEp = netConn?.Methods.FirstOrDefault(m => m.Name == "get_RemoteEndPoint");
        var statusEnum = module.TopLevelTypes.FirstOrDefault(t => t.Name == "NetConnectionStatus");

        if (setStatus is not null && getRemoteEp is not null && statusEnum is not null)
        {
            var ssBody = setStatus.CilMethodBody!;
            var ssEpVar = new CilLocalVariable(corlib.String);
            var ssStatusVar = new CilLocalVariable(corlib.String);
            ssBody.LocalVariables.Add(ssEpVar);
            ssBody.LocalVariables.Add(ssStatusVar);

            var ssPrefix = new List<CilInstruction>
            {
                new(CilOpCodes.Ldarg_0),
                new(CilOpCodes.Callvirt, getRemoteEp),
                new(CilOpCodes.Callvirt, objToString),
                new(CilOpCodes.Stloc,    ssEpVar),
                new(CilOpCodes.Ldarg,    setStatus.Parameters[0]),
                new(CilOpCodes.Box,      statusEnum),
                new(CilOpCodes.Callvirt, objToString),
                new(CilOpCodes.Stloc,    ssStatusVar),
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

        var netIncoming = module.TopLevelTypes.FirstOrDefault(t => t.Name == "NetIncomingMessage");
        var incomingTypeEnum = module.TopLevelTypes.FirstOrDefault(t => t.Name == "NetIncomingMessageType");
        var getMsgType = netIncoming?.Methods.FirstOrDefault(m => m.Name == "get_MessageType");
        var getLengthBytes = netIncoming?.Methods.FirstOrDefault(m => m.Name == "get_LengthBytes");
        var getSenderEp = netIncoming?.Methods.FirstOrDefault(m => m.Name == "get_SenderEndPoint");
        var readMessage = netPeer.Methods.FirstOrDefault(m =>
            m.Name == "ReadMessage" && m.Parameters.Count == 0 && m.CilMethodBody is not null);

        if (readMessage is not null && netIncoming is not null && incomingTypeEnum is not null &&
            getMsgType is not null && getLengthBytes is not null && getSenderEp is not null)
        {
            var rmBody = readMessage.CilMethodBody!;
            var rmEpVar = new CilLocalVariable(corlib.String);
            var rmTypeVar = new CilLocalVariable(corlib.String);
            var rmLenVar = new CilLocalVariable(corlib.Int32);
            var rmLenStr = new CilLocalVariable(corlib.String);
            rmBody.LocalVariables.Add(rmEpVar);
            rmBody.LocalVariables.Add(rmTypeVar);
            rmBody.LocalVariables.Add(rmLenVar);
            rmBody.LocalVariables.Add(rmLenStr);

            var rmInstrs = rmBody.Instructions;
            var rmRetIndices = Enumerable.Range(0, rmInstrs.Count)
                .Where(i => rmInstrs[i].OpCode == CilOpCodes.Ret)
                .OrderByDescending(i => i)
                .ToList();

            foreach (int retIdx in rmRetIndices)
            {
                var retInstr = rmInstrs[retIdx];
                var skipLog = new CilInstructionLabel();
                skipLog.Instruction = retInstr;
                var senderToString = new CilInstructionLabel();
                var senderDone = new CilInstructionLabel();

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

    public static void LegacyLidgrenUnNetDebug()
    {
        string? dll = Paths.Find(LidgrenDllName);
        if (dll is null) { Console.Error.WriteLine($"Could not find {LidgrenDllName}."); Cli.Exit(1); return; }

        string backup = dll + ".netdebugbak";
        if (!File.Exists(backup)) { Console.Error.WriteLine("Network debug not enabled."); Cli.Exit(1); return; }

        File.Copy(backup, dll, overwrite: true);
        File.Delete(backup);
        Console.WriteLine("Network debug disabled.");
    }
}
