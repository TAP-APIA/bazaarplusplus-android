using BazaarPlusPlus.OfficialPayload;
using Mono.Cecil;
using Xunit;

public sealed class PayloadCompatibilityTests : IDisposable
{
    private readonly string _melon = Directory.CreateTempSubdirectory("bpp-payload-compat").FullName;

    public PayloadCompatibilityTests()
    {
        // A real assembly as the "mod": it references Mono.Cecil, which only a host directory has.
        var mods = Directory.CreateDirectory(Path.Combine(_melon, "Mods")).FullName;
        File.Copy(
            typeof(PayloadCompatibility).Assembly.Location,
            Path.Combine(mods, "BazaarPlusPlus.OfficialPayload.dll")
        );
    }

    public void Dispose() => Directory.Delete(_melon, recursive: true);

    [Fact]
    public void A_payload_binds_when_the_host_provides_everything_it_references()
    {
        var cecil = Path.GetDirectoryName(typeof(ModuleDefinition).Assembly.Location)!;
        Assert.Null(PayloadCompatibility.Check(_melon, new[] { cecil }));
    }

    [Fact]
    public void A_payload_referencing_a_missing_host_assembly_is_refused()
    {
        Assert.Equal(
            "assembly_missing:Mono.Cecil@BazaarPlusPlus.OfficialPayload.dll",
            PayloadCompatibility.Check(_melon, Array.Empty<string>())
        );
    }

    [Fact]
    public void A_payload_without_mods_is_refused()
    {
        Directory.Delete(Path.Combine(_melon, "Mods"), recursive: true);
        Assert.Equal("mods_missing", PayloadCompatibility.Check(_melon, Array.Empty<string>()));
    }
}
