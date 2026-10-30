// Makes the launcher's BepInEx generate interop the way MelonLoader/LemonLoader does
// (Il2CppPrefixMode.OptOut: Il2CppTheBazaar, Il2CppTMPro, Il2Cpp, Il2CppNewtonsoft.Json, ...), so
// upstream's Android Host binds to it unchanged (android-port/upstream-host/README.md).
//
// Two edits to BepInEx.Unity.IL2CPP.dll's Il2CppInteropManager:
//   RunIl2CppInteropGenerator: sets GeneratorOptions.Il2CppPrefixMode = OptOut on the options it builds;
//   ComputeHash: appends a marker, so interop generated without the prefix counts as out of date
//                and is regenerated once.
// Idempotent; fails without writing when the expected shapes are missing.
//
// Usage: patch-bepinex-interop-prefix <BepInEx.Unity.IL2CPP.dll> <output.dll> [<search dir>...]
//   The input's own directory is searched for Il2CppInterop.Generator.dll; pass BepInEx/core when
//   the input sits elsewhere.

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "usage: patch-bepinex-interop-prefix <BepInEx.Unity.IL2CPP.dll> <output.dll> [<search dir>...]"
    );
    return 2;
}

try
{
    var result = InteropPrefixPatch.Apply(args[0], args[1], args.Skip(2));
    Console.WriteLine(result);
    return 0;
}
catch (PatchException exception)
{
    Console.Error.WriteLine($"not patched: {exception.Message}");
    return 1;
}
