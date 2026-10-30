using Mono.Cecil;
using Mono.Cecil.Cil;

/// <summary>The edits Program.cs describes, callable from the tests.</summary>
internal static class InteropPrefixPatch
{
    public const string HashMarker = "|il2cpp-prefix:OptOut";

    /// <summary>Patches <paramref name="input"/> into <paramref name="output"/>; returns what it did.</summary>
    public static string Apply(string input, string output, IEnumerable<string> searchDirectories)
    {
        input = Path.GetFullPath(input);
        output = Path.GetFullPath(output);
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
        foreach (var dir in searchDirectories)
            resolver.AddSearchDirectory(Path.GetFullPath(dir));
        var bytes = File.ReadAllBytes(input);
        using var module = ModuleDefinition.ReadModule(
            new MemoryStream(bytes),
            new ReaderParameters { AssemblyResolver = resolver }
        );
        var manager =
            module.GetTypes().FirstOrDefault(t => t.Name == "Il2CppInteropManager")
            ?? throw new PatchException("type Il2CppInteropManager not found");
        var prefix = PatchPrefixMode(module, manager);
        var hash = PatchHash(module, manager);
        if (!prefix && !hash)
        {
            if (input != output)
                File.Copy(input, output, overwrite: true);
            return "already patched";
        }
        module.Write(output);
        return $"patched prefix_mode={prefix} hash_marker={hash} -> {output}";
    }

    internal static bool PatchPrefixMode(ModuleDefinition module, TypeDefinition manager)
    {
        var method =
            manager.Methods.FirstOrDefault(m => m.Name == "RunIl2CppInteropGenerator" && m.HasBody)
            ?? throw new PatchException("method RunIl2CppInteropGenerator not found");
        var instructions = method.Body.Instructions;
        var create = instructions
            .Where(i =>
                i.OpCode == OpCodes.Newobj
                && i.Operand is MethodReference ctor
                && ctor.DeclaringType.Name == "GeneratorOptions"
            )
            .ToList();
        if (create.Count != 1)
            throw new PatchException(
                $"expected one new GeneratorOptions in RunIl2CppInteropGenerator, found {create.Count}"
            );
        if (
            instructions.Any(i =>
                i.Operand is MethodReference m && m.Name == "set_Il2CppPrefixMode"
            )
        )
            return false;

        var options = ((MethodReference)create[0].Operand).DeclaringType.Resolve()
            ?? throw new PatchException("GeneratorOptions could not be resolved; pass BepInEx/core");
        var setter =
            options.Methods.FirstOrDefault(m => m.Name == "set_Il2CppPrefixMode")
            ?? throw new PatchException(
                "this Il2CppInterop.Generator has no Il2CppPrefixMode (needs 1.4.5 or later)"
            );
        var modeType = setter.Parameters[0].ParameterType.Resolve();
        var optOut =
            modeType.Fields.FirstOrDefault(f => f.IsLiteral && f.Name == "OptOut")
            ?? throw new PatchException("PrefixMode has no OptOut");

        var il = method.Body.GetILProcessor();
        var dup = il.Create(OpCodes.Dup);
        var value = il.Create(OpCodes.Ldc_I4, Convert.ToInt32(optOut.Constant));
        var set = il.Create(OpCodes.Callvirt, module.ImportReference(setter));
        il.InsertAfter(create[0], dup);
        il.InsertAfter(dup, value);
        il.InsertAfter(value, set);
        return true;
    }

    internal static bool PatchHash(ModuleDefinition module, TypeDefinition manager)
    {
        var method =
            manager.Methods.FirstOrDefault(m =>
                m.Name == "ComputeHash"
                && m.HasBody
                && m.ReturnType.FullName == "System.String"
                && m.Parameters.Count == 0
            ) ?? throw new PatchException("method string ComputeHash() not found");
        if (
            method.Body.Instructions.Any(i =>
                i.OpCode == OpCodes.Ldstr && (string)i.Operand == HashMarker
            )
        )
            return false;
        var concat = module.ImportReference(
            typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])
        );
        var il = method.Body.GetILProcessor();
        foreach (var ret in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
        {
            // Keep branch targets on the original ret valid: the new code goes in front of it as the
            // same instruction object, which becomes the marker load.
            var originalRet = il.Create(OpCodes.Ret);
            ret.OpCode = OpCodes.Ldstr;
            ret.Operand = HashMarker;
            var call = il.Create(OpCodes.Call, concat);
            il.InsertAfter(ret, call);
            il.InsertAfter(call, originalRet);
        }
        return true;
    }
}

internal sealed class PatchException(string message) : Exception(message);
