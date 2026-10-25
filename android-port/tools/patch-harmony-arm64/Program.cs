using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// Patch only the Android ARM64 distribution. AAPCS64 passes the single-pointer
// CancellationToken struct in a register by value. The x64-oriented converter
// instead boxes memory at that register value (the CancellationTokenSource object).
// Emit ldarga for this one known 8-byte struct so boxing reads the token itself.
if (args.Length != 2) throw new ArgumentException("Expected input and output HarmonySupport DLL paths.");
using var module = ModuleDefinition.ReadModule(args[0]);
var owner = module.Types.Single(t => t.Name == "Il2CppDetourMethodPatcher");
var convert = owner.Methods.Single(m => m.Name == "EmitConvertArgumentToManaged");
// Inserting even one byte can overflow the original Nullable branch's 127-byte
// short jump. Widen branches before editing so Cecil preserves their targets.
convert.Body.SimplifyMacros();
if (owner.Methods.Any(m => m.Name == "BppAndroidUsesIndirectStructArgument"))
    throw new InvalidOperationException("Input has already been patched.");
var calls = convert.Body.Instructions.Where(i => i.OpCode == OpCodes.Call
    && i.Operand is MethodReference m && m.FullName == "System.Boolean System.Environment::get_Is64BitProcess()").ToArray();
if (calls.Length == 0) throw new InvalidOperationException($"Expected original ABI selection, found {calls.Length}.");

// In newer HarmonySupport, there is a call in the Nullable unbox branch and a call in the ValueType box branch.
// We patch the boxing branch (the one near il2cpp_value_box).
var original = calls.Length == 1 ? calls[0] : calls.Last();
var is64 = (MethodReference)original.Operand;
var helper = new MethodDefinition("BppAndroidUsesIndirectStructArgument",
    MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Boolean);
helper.Parameters.Add(new ParameterDefinition("type", ParameterAttributes.None, convert.Parameters[2].ParameterType));
owner.Methods.Add(helper);
var fullName = new MethodReference("get_FullName", module.TypeSystem.String, helper.Parameters[0].ParameterType) { HasThis = true };
var equal = new MethodReference("op_Equality", module.TypeSystem.Boolean, module.TypeSystem.String);
equal.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
equal.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
var il = helper.Body.GetILProcessor();
var otherType = il.Create(OpCodes.Call, is64);
il.Emit(OpCodes.Ldarg_0);
il.Emit(OpCodes.Callvirt, fullName);
il.Emit(OpCodes.Ldstr, "Il2CppSystem.Threading.CancellationToken");
il.Emit(OpCodes.Call, equal);
il.Emit(OpCodes.Brfalse_S, otherType);
il.Emit(OpCodes.Ldc_I4_0);
il.Emit(OpCodes.Ret);
il.Append(otherType);
il.Emit(OpCodes.Ret);
convert.Body.GetILProcessor().InsertBefore(original, Instruction.Create(OpCodes.Ldarg, convert.Parameters[2]));
original.Operand = helper;
module.Write(args[1]);
using var verified = ModuleDefinition.ReadModule(args[1]);
var method = verified.Types.Single(t => t.Name == owner.Name).Methods.Single(m => m.Name == convert.Name);
if (method.Body.Instructions.Count(i => i.Operand is MethodReference m && m.Name == helper.Name) != 1)
    throw new InvalidOperationException("Written converter does not contain the ABI correction.");
Console.WriteLine($"Patched Android ARM64 CancellationToken boxing: {args[1]}");
