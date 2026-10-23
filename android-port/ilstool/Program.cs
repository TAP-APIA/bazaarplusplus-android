using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Mono.Cecil;
using Mono.Cecil.Cil;

var src = args[0];
var dst = args[1];
File.Copy(src, dst, true);
using (var mod = ModuleDefinition.ReadModule(dst, new ReaderParameters { ReadWrite = true }))
{
    var il2cpp = mod.GetTypes().First(t => t.Name == "IL2CPP");
    var unbox = il2cpp.Methods.First(m => m.Name == "il2cpp_object_unbox");

    if (unbox.IsPInvokeImpl)
    {
        // 1. Create native P/Invoke clone
        var nativeUnbox = new MethodDefinition(
            "il2cpp_object_unbox_native",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.PInvokeImpl,
            mod.TypeSystem.IntPtr
        );
        nativeUnbox.ImplAttributes = unbox.ImplAttributes;
        nativeUnbox.PInvokeInfo = new PInvokeInfo(
            unbox.PInvokeInfo.Attributes,
            "il2cpp_object_unbox",
            unbox.PInvokeInfo.Module
        );
        nativeUnbox.Parameters.Add(new ParameterDefinition("obj", ParameterAttributes.None, mod.TypeSystem.IntPtr));
        il2cpp.Methods.Add(nativeUnbox);

        // 2. Add static field s_ZeroUnboxBuffer
        var zeroBufField = new FieldDefinition(
            "s_ZeroUnboxBuffer",
            FieldAttributes.Private | FieldAttributes.Static,
            mod.TypeSystem.IntPtr
        );
        il2cpp.Fields.Add(zeroBufField);

        // 3. Convert il2cpp_object_unbox to managed wrapper
        unbox.PInvokeInfo = null;
        unbox.IsPInvokeImpl = false;
        unbox.IsPreserveSig = false;
        unbox.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;
        unbox.Body = new MethodBody(unbox);

        var allocHGlobal = mod.ImportReference(
            typeof(Marshal).GetMethod("AllocHGlobal", new[] { typeof(int) })
        );

        var il = unbox.Body.GetILProcessor();
        var isZero = il.Create(OpCodes.Ldsfld, zeroBufField);
        var clearBuf = il.Create(OpCodes.Ldsfld, zeroBufField);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse_S, isZero);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, nativeUnbox);
        il.Emit(OpCodes.Ret);

        il.Append(isZero);
        il.Emit(OpCodes.Brtrue_S, clearBuf);
        il.Emit(OpCodes.Ldc_I4, 256);
        il.Emit(OpCodes.Call, allocHGlobal);
        il.Emit(OpCodes.Stsfld, zeroBufField);

        il.Append(clearBuf);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4, 256);
        il.Emit(OpCodes.Initblk);
        il.Emit(OpCodes.Ldsfld, zeroBufField);
        il.Emit(OpCodes.Ret);
    }

    mod.Write();
}

using (var v = ModuleDefinition.ReadModule(dst))
{
    var il2cpp = v.GetTypes().First(t => t.Name == "IL2CPP");
    var m = il2cpp.Methods.First(x => x.Name == "il2cpp_object_unbox");
    Console.WriteLine($"=== {m.FullName} ===");
    foreach (var ins in m.Body.Instructions) Console.WriteLine("  " + ins);
}
