using System.Reflection;
using System.Runtime.Loader;
using Xunit;

public sealed class InteropPrefixPatchTests : IDisposable
{
    private static readonly string FixtureDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../fixture/bin/Debug/net6.0")
    );

    private readonly string _work = Directory.CreateTempSubdirectory("bpp-prefix-patch").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    [Fact]
    public void Patched_generator_options_use_opt_out_prefixes()
    {
        var patched = Patch();
        var manager = Load(patched);
        manager.GetMethod("Generate")!.Invoke(null, [false]);
        var options = manager.GetProperty("LastOptions")!.GetValue(null)!;
        Assert.Equal("OptOut", options.GetType().GetProperty("Il2CppPrefixMode")!.GetValue(options)!.ToString());
        Assert.Equal("interop", options.GetType().GetProperty("OutputDir")!.GetValue(options));
    }

    [Fact]
    public void Patched_hash_differs_from_the_original_by_the_marker()
    {
        var original = (string)Load(Path.Combine(FixtureDir, "BepInEx.Unity.IL2CPP.dll"))
            .GetMethod("Hash")!
            .Invoke(null, null)!;
        var patched = (string)Load(Patch()).GetMethod("Hash")!.Invoke(null, null)!;
        Assert.Equal(original + InteropPrefixPatch.HashMarker, patched);
    }

    [Fact]
    public void Patching_twice_changes_nothing()
    {
        var once = Patch();
        var twice = Path.Combine(_work, "twice", "BepInEx.Unity.IL2CPP.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(twice)!);
        Assert.Equal("already patched", InteropPrefixPatch.Apply(once, twice, [FixtureDir]));
        Assert.Equal(File.ReadAllBytes(once), File.ReadAllBytes(twice));
    }

    [Fact]
    public void An_assembly_without_the_manager_is_refused()
    {
        var output = Path.Combine(_work, "out.dll");
        var other = Path.Combine(FixtureDir, "Il2CppInterop.Generator.dll");
        Assert.Throws<PatchException>(() => InteropPrefixPatch.Apply(other, output, []));
        Assert.False(File.Exists(output));
    }

    private string Patch()
    {
        var output = Path.Combine(_work, Guid.NewGuid().ToString("N"), "BepInEx.Unity.IL2CPP.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        InteropPrefixPatch.Apply(
            Path.Combine(FixtureDir, "BepInEx.Unity.IL2CPP.dll"),
            output,
            [FixtureDir]
        );
        return output;
    }

    private static Type Load(string path)
    {
        var context = new AssemblyLoadContext(null, isCollectible: true);
        context.Resolving += (ctx, name) =>
        {
            var candidate = Path.Combine(FixtureDir, name.Name + ".dll");
            return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
        };
        // From bytes: a path load maps the file, and Windows then refuses to delete _work.
        return context
            .LoadFromStream(new MemoryStream(File.ReadAllBytes(path)))
            .GetType("BepInEx.Unity.IL2CPP.Il2CppInteropManager", throwOnError: true)!;
    }
}
