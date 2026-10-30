using System.Security.Cryptography;
using System.Text;
using Il2CppInterop.Generator;

namespace BepInEx.Unity.IL2CPP;

// Shapes copied from BepInEx master Runtimes/Unity/BepInEx.Unity.IL2CPP/Il2CppInteropManager.cs.
public static class Il2CppInteropManager
{
    public static GeneratorOptions? LastOptions { get; private set; }

    public static string Hash() => ComputeHash();

    public static void Generate(bool regex) =>
        RunIl2CppInteropGenerator(new List<AsmResolver.DotNet.AssemblyDefinition>(), regex);

    private static string ComputeHash()
    {
        using var md5 = MD5.Create();
        var bytes = Encoding.UTF8.GetBytes("game");
        md5.TransformFinalBlock(bytes, 0, bytes.Length);
        return Convert.ToHexString(md5.Hash!);
    }

    private static void RunIl2CppInteropGenerator(
        List<AsmResolver.DotNet.AssemblyDefinition> sourceAssemblies,
        bool regex
    )
    {
        var opts = new GeneratorOptions
        {
            GameAssemblyPath = null,
            Source = sourceAssemblies,
            OutputDir = "interop",
            UnityBaseLibsDir = null,
            ObfuscatedNamesRegex = regex ? new System.Text.RegularExpressions.Regex("x") : null,
        };
        LastOptions = opts;
    }
}
