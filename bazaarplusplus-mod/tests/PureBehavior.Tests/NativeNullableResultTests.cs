using BazaarPlusPlus.Infrastructure;
using Xunit;

public sealed class NativeNullableResultTests
{
    [Fact]
    public void Empty_native_nullable_never_constructs_or_reads_a_value_proxy()
    {
        var reads = 0;
        Assert.Null(
            NativeNullableResult.Read<int>(
                IntPtr.Zero,
                _ =>
                {
                    reads++;
                    throw new Exception();
                }
            )
        );
        Assert.Equal(0, reads);
    }

    [Fact]
    public void Present_nullable_preserves_the_actual_value_including_zero()
    {
        foreach (var value in new[] { 0, 17, -1 })
            Assert.Equal(
                value,
                NativeNullableResult.Read(
                    new IntPtr(123),
                    ptr =>
                    {
                        Assert.Equal(new IntPtr(123), ptr);
                        return value;
                    }
                )
            );
    }

    [Fact]
    public void Broken_present_values_are_not_silently_reported_as_no_enchantment() =>
        Assert.Throws<InvalidOperationException>(() =>
            NativeNullableResult.Read<int>(
                new IntPtr(1),
                _ => throw new InvalidOperationException()
            )
        );
}
