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

        string backup = exePath + ".diagbak";

        Console.WriteLine("In-process diagnose is disabled because it destabilizes the game.");
        if (File.Exists(backup))
            Console.WriteLine("An older diagnose patch backup still exists. Run 'undiagnose' to restore the original executable first.");
        Console.WriteLine("Use the external crash watcher instead:");
        Console.WriteLine(@"  .\DiagnoseTrace\bin\Debug\net8.0\DiagnoseTrace.exe --launch --procdump");
        Console.WriteLine($"That captures transition logs and crash artifacts without patching {ExeName}.");
    }

    public static void Undiagnose()
    {
        string? exePath = Paths.Find(ExeName);
        if (exePath is null) { Console.Error.WriteLine($"Could not find {ExeName}."); Cli.Exit(1); return; }

        string backup = exePath + ".diagbak";
        if (!File.Exists(backup)) { Console.Error.WriteLine("No diagnose backup found."); Cli.Exit(1); return; }

        File.Copy(backup, exePath, overwrite: true);
        File.Delete(backup);
        Console.WriteLine("Diagnose patch removed.");
    }
}
