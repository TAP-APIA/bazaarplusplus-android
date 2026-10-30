namespace System.Collections.Frozen;

// .NET 8's FrozenSet, as far as upstream uses it (GameInterop/AndroidCardTemplateIndex). A
// polyfill rather than the System.Collections.Immutable 8.0 package: the launcher's runtime
// already loads its own 6.0 copy of that assembly, and a second version would not load.

/// <summary>A read-only set built once.</summary>
internal sealed class FrozenSet<T> : IReadOnlyCollection<T>
{
    private readonly HashSet<T> _items;

    internal FrozenSet(IEnumerable<T> source, IEqualityComparer<T>? comparer) =>
        _items = new HashSet<T>(source, comparer);

    public int Count => _items.Count;

    public bool Contains(T item) => _items.Contains(item);

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class FrozenSet
{
    public static FrozenSet<T> ToFrozenSet<T>(
        this IEnumerable<T> source,
        IEqualityComparer<T>? comparer = null
    ) => new(source ?? throw new ArgumentNullException(nameof(source)), comparer);
}
