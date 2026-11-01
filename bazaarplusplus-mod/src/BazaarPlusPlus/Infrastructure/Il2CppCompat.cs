#nullable enable
using System.Collections.Generic;
using BazaarPlusPlus.Infrastructure;
#if BPP_ANDROID
using Il2CppInterop.Runtime.InteropTypes.Arrays;
#endif

namespace BazaarPlusPlus;

#if BPP_ANDROID
/// <summary>
/// Bridges the IL2CPP collection proxies (<c>Il2CppSystem.Collections.Generic.*</c>) to ordinary
/// .NET collections.
///
/// The generated proxies expose only the IL2CPP enumerator shape (an <c>IEnumerator&lt;T&gt;</c> with
/// <c>Current</c> but no <c>MoveNext</c>) and do not implement
/// <c>System.Collections.Generic.IEnumerable&lt;T&gt;</c>, so C# foreach, LINQ and interface-typed
/// parameters all need one explicit materialisation first.
/// </summary>
internal static class Il2CppCompat
{
    public static List<T> ToManaged<T>(this Il2CppSystem.Collections.Generic.List<T> source)
    {
        Il2CppThreadProbe.Check();
        ArgumentNullException.ThrowIfNull(source);
        var result = new List<T>(source.Count);
        for (var i = 0; i < source.Count; i++)
            result.Add(source[i]);
        return result;
    }

    /// <summary>
    /// Materialises a projected enumerable. The projection's enumerator has no <c>MoveNext</c>, so
    /// the only supported case is an underlying <c>List&lt;T&gt;</c>, which is re-cast and indexed.
    /// Anything else yields an empty sequence instead of failing at runtime.
    /// </summary>
    public static List<T> ToManaged<T>(this Il2CppSystem.Collections.Generic.IEnumerable<T> source)
    {
        Il2CppThreadProbe.Check();
        ArgumentNullException.ThrowIfNull(source);
        var list = source.TryCast<Il2CppSystem.Collections.Generic.List<T>>();
        if (list == null)
            return new List<T>();
        var result = new List<T>(list.Count);
        for (var i = 0; i < list.Count; i++)
            result.Add(list[i]);
        return result;
    }

    /// <summary>
    /// Materialises a native set whose element type is a managed reference type. Value-type sets
    /// (for example enums) must go through <see cref="ToManagedStructSet{T}"/> at the call site,
    /// because the generator only exposes <c>Il2CppStructArray&lt;T&gt;</c> for them.
    /// </summary>
    public static List<T> ToManagedSet<T>(this Il2CppSystem.Collections.Generic.HashSet<T> source)
        where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
    {
        Il2CppThreadProbe.Check();
        ArgumentNullException.ThrowIfNull(source);
        var buffer = new Il2CppReferenceArray<T>(source.Count);
        source.CopyTo(buffer, 0);
        var result = new List<T>(source.Count);
        for (var i = 0; i < buffer.Length; i++)
            result.Add(buffer[i]);
        return result;
    }

    /// <summary>
    /// Materialises a native string set. Its element type is the managed <see cref="string"/>,
    /// which cannot be used with <see cref="Il2CppReferenceArray{T}"/>; the string array overload of
    /// <c>CopyTo</c> is the only projection that accepts it.
    /// </summary>
    public static List<string> ToManagedStringSet(this Il2CppSystem.Collections.Generic.HashSet<string> source)
    {
        Il2CppThreadProbe.Check();
        ArgumentNullException.ThrowIfNull(source);
        var buffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(source.Count);
        source.CopyTo(buffer, 0);
        var result = new List<string>(source.Count);
        for (var i = 0; i < buffer.Length; i++)
            result.Add(buffer[i]);
        return result;
    }

    public static List<T> ToManagedStructSet<T>(this Il2CppSystem.Collections.Generic.HashSet<T> source)
        where T : unmanaged
    {
        Il2CppThreadProbe.Check();
        ArgumentNullException.ThrowIfNull(source);
        var buffer = new Il2CppStructArray<T>(source.Count);
        source.CopyTo(buffer, 0);
        var result = new List<T>(source.Count);
        for (var i = 0; i < buffer.Length; i++)
            result.Add(buffer[i]);
        return result;
    }

    public static Dictionary<TKey, TValue> ToManagedDictionary<TKey, TValue>(
        this Il2CppSystem.Collections.Generic.Dictionary<TKey, TValue> source
    )
        where TKey : notnull
    {
        Il2CppThreadProbe.Check();
        ArgumentNullException.ThrowIfNull(source);
        var buffer =
            new Il2CppReferenceArray<Il2CppSystem.Collections.Generic.KeyValuePair<TKey, TValue>>(
                source.Count
            );
        source.CopyTo(buffer, 0);
        var result = new Dictionary<TKey, TValue>(source.Count);
        for (var i = 0; i < buffer.Length; i++)
            result[buffer[i].Key] = buffer[i].Value;
        return result;
    }

    /// <summary>
    /// Reads a native nullable into a managed one. Native <c>Nullable&lt;T&gt;</c> has no C# operator
    /// support, so comparisons and <c>??</c> must go through this helper.
    /// </summary>
    public static unsafe T? ToManagedNullable<T>(this Il2CppSystem.Nullable<T>? source)
        where T : struct
    {
        Il2CppThreadProbe.Check();
        if (source == null || source.Pointer == System.IntPtr.Zero)
            return null;
        // il2cpp_runtime_invoke boxes a nonempty Nullable<T> as T, and an empty one as null.
        // The generated getter still wraps that result as Nullable<T>. Reading hasValue/value
        // from a boxed enum/int therefore reads beyond its payload into unrelated memory.
        if (Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(source.Pointer)
            == Il2CppInterop.Runtime.Il2CppClassPointerStore<T>.NativeClassPtr)
            return System.Runtime.CompilerServices.Unsafe.Read<T>(
                (void*)Il2CppInterop.Runtime.IL2CPP.il2cpp_object_unbox(source.Pointer)
            );
        return source.hasValue ? source.value : null;
    }

    public static T WithDefault<T>(this Il2CppSystem.Nullable<T>? source, T fallback)
        where T : struct => source.ToManagedNullable() ?? fallback;

    public static Il2CppSystem.Nullable<T> ToNativeNullable<T>(this T value)
        where T : struct => new(value);

    public static Il2CppSystem.Nullable<T> ToNativeNullable<T>(this T? value)
        where T : struct => value.HasValue ? new(value.Value) : new Il2CppSystem.Nullable<T>();

    /// <summary>
    /// Bridges a projected non-generic <c>Il2CppSystem.Threading.Tasks.Task</c> into a managed
    /// <see cref="System.Threading.Tasks.Task"/> that completes on the Unity main thread.
    /// </summary>
    public static System.Threading.Tasks.Task AsManagedTask(
        this Il2CppSystem.Threading.Tasks.Task? nativeTask
    )
    {
        Il2CppThreadProbe.Check();
        if (nativeTask == null)
            return System.Threading.Tasks.Task.CompletedTask;
        if (nativeTask.IsCompleted)
            return NativeTaskOutcome(nativeTask);

        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
        BazaarPlusPlus.GameInterop.BppCoroutines.PollOnMainThread(() =>
        {
            if (!nativeTask.IsCompleted)
                return false;
            if (nativeTask.IsCanceled)
                tcs.TrySetCanceled();
            else if (nativeTask.IsFaulted)
                tcs.TrySetException(new InvalidOperationException(nativeTask.Exception?.ToString()));
            else
                tcs.TrySetResult(true);
            return true;
        });
        return tcs.Task;
    }

    private static System.Threading.Tasks.Task NativeTaskOutcome(Il2CppSystem.Threading.Tasks.Task task) =>
        task.IsCanceled
            ? System.Threading.Tasks.Task.FromCanceled(new System.Threading.CancellationToken(true))
            : task.IsFaulted
                ? System.Threading.Tasks.Task.FromException(new InvalidOperationException(task.Exception?.ToString()))
                : System.Threading.Tasks.Task.CompletedTask;

    public static Il2CppSystem.Threading.Tasks.Task AsNativeTask(this System.Threading.Tasks.Task task)
    {
        Il2CppThreadProbe.Check();
        var completion = new Il2CppSystem.Threading.Tasks.TaskCompletionSource<bool>();
        bool Poll()
        {
            if (!task.IsCompleted)
                return false;
            if (task.IsCanceled)
                completion.TrySetCanceled();
            else if (task.IsFaulted)
                completion.TrySetException(new Il2CppSystem.InvalidOperationException(task.Exception?.ToString()));
            else
                completion.TrySetResult(true);
            return true;
        }
        if (!Poll())
            BazaarPlusPlus.GameInterop.BppCoroutines.PollOnMainThread(Poll);
        return completion.Task;
    }

    /// <summary>
    /// Bridges a projected <c>Il2CppSystem.Threading.Tasks.Task&lt;T&gt;</c> into a managed
    /// <see cref="System.Threading.Tasks.Task{TResult}"/> that completes on the Unity main thread.
    /// </summary>
    public static System.Threading.Tasks.Task<T?> AsManagedTask<T>(
        this Il2CppSystem.Threading.Tasks.Task<T>? nativeTask
    )
    {
        Il2CppThreadProbe.Check();
        if (nativeTask == null)
            return System.Threading.Tasks.Task.FromResult<T?>(default);
        if (nativeTask.IsCompleted)
        {
            if (nativeTask.IsFaulted || nativeTask.IsCanceled)
                return System.Threading.Tasks.Task.FromResult<T?>(default);
            return System.Threading.Tasks.Task.FromResult<T?>(nativeTask.Result);
        }

        var tcs = new System.Threading.Tasks.TaskCompletionSource<T?>();
        BazaarPlusPlus.GameInterop.BppCoroutines.PollOnMainThread(() =>
        {
            if (!nativeTask.IsCompleted)
                return false;
            if (nativeTask.IsFaulted || nativeTask.IsCanceled)
                tcs.TrySetResult(default);
            else
                tcs.TrySetResult(nativeTask.Result);
            return true;
        });
        return tcs.Task;
    }

    /// <summary>
    /// Reads a tier attribute without calling <c>IHasTierData.GetAttributeBaseValueAtTier</c>,
    /// whose generated IL2CPP wrapper throws <see cref="System.NullReferenceException"/> inside
    /// <c>Il2CppSystem.ValueType..ctor(IntPtr.Zero)</c> whenever a card does not define that
    /// attribute at the requested tier.
    /// </summary>
    public static int? SafeGetAttributeBaseValueAtTier(
        this BazaarGameShared.Domain.Cards.Interfaces.IHasTierData? tierData,
        BazaarGameShared.Domain.Core.Types.ECardAttributeType attributeType,
        BazaarGameShared.Domain.Core.Types.ETier tier
    )
    {
        Il2CppThreadProbe.Check();
        if (tierData?.Tiers == null)
            return null;
        if (
            tierData.Tiers.TryGetValue(tier, out var tierTemplate)
            && tierTemplate?.Attributes != null
            && tierTemplate.Attributes.TryGetValue(attributeType, out var value)
        )
        {
            return value;
        }
        return null;
    }

    /// <summary>
    /// Converts the projected <c>Il2CppSystem.Guid</c> to a managed <see cref="System.Guid"/>.
    /// The proxy exposes no implicit conversion or byte accessor, so this round-trips through the
    /// canonical string form; game identity conversions are not on a hot path.
    /// </summary>
    public static System.Guid ToSystemGuid(this Il2CppSystem.Guid value) =>
        System.Guid.Parse(value.ToString());

    public static System.Guid? ToSystemGuid(
        this Il2CppSystem.Nullable<Il2CppSystem.Guid>? value
    ) =>
        value.ToManagedNullable() is { } guid
            ? System.Guid.Parse(guid.ToString())
            : null;

    /// <summary>Managed <see cref="System.Guid"/> to its projected counterpart.</summary>
    public static Il2CppSystem.Guid ToNativeGuid(this System.Guid value) =>
        Il2CppSystem.Guid.Parse(value.ToString("D"));

    public static Il2CppSystem.Nullable<Il2CppSystem.Guid> ToNativeGuid(this System.Guid? value) =>
        value.HasValue
            ? Il2CppSystem.Guid.Parse(value.Value.ToString("D")).ToNativeNullable()
            : new Il2CppSystem.Nullable<Il2CppSystem.Guid>();

    /// <summary>
    /// Materialises the keys of a projected read-only dictionary. Its <c>KeyCollection</c> exposes
    /// neither an indexer nor a usable enumerator, only <c>CopyTo</c>, so the keys are copied into a
    /// native struct array first.
    /// </summary>
    public static List<TKey> ToManagedKeys<TKey, TValue>(
        this Il2CppSystem.Collections.ObjectModel.ReadOnlyDictionary<TKey, TValue> source
    )
        where TKey : unmanaged
    {
        Il2CppThreadProbe.Check();
        ArgumentNullException.ThrowIfNull(source);
        var buffer = new Il2CppStructArray<TKey>(source.Count);
        source.Keys.CopyTo(buffer, 0);
        var result = new List<TKey>(source.Count);
        for (var i = 0; i < buffer.Length; i++)
            result.Add(buffer[i]);
        return result;
    }

    /// <summary>
    /// Materialises a native dictionary keyed by <c>Il2CppSystem.Guid</c>, converting each key to
    /// its managed counterpart. The key collection only exposes <c>CopyTo</c>, so the keys are
    /// copied into a native struct array first.
    /// </summary>
    public static Dictionary<System.Guid, TValue> ToManagedGuidDictionary<TValue>(
        this Il2CppSystem.Collections.Generic.Dictionary<Il2CppSystem.Guid, TValue> source
    )
    {
        Il2CppThreadProbe.Check();
        ArgumentNullException.ThrowIfNull(source);
        var keys = new Il2CppStructArray<Il2CppSystem.Guid>(source.Count);
        source.Keys.CopyTo(keys, 0);
        var result = new Dictionary<System.Guid, TValue>(source.Count);
        for (var i = 0; i < keys.Length; i++)
            result[keys[i].ToSystemGuid()] = source[keys[i]];
        return result;
    }

    /// <summary>
    /// Materialises a projected read-only dictionary. The projection declares no enumerator, so the
    /// runtime object is re-cast to <c>Dictionary&lt;TKey,TValue&gt;</c> and copied from there.
    /// </summary>
    public static Dictionary<TKey, TValue> ToManagedDictionary<TKey, TValue>(
        this Il2CppSystem.Collections.Generic.IReadOnlyDictionary<TKey, TValue> source
    )
    {
        Il2CppThreadProbe.Check();
        ArgumentNullException.ThrowIfNull(source);
        var concrete = source.TryCast<Il2CppSystem.Collections.Generic.Dictionary<TKey, TValue>>();
        return concrete == null ? new Dictionary<TKey, TValue>() : concrete.ToManagedDictionary();
    }

    /// <summary>
    /// Converts the projected <c>ValueTuple&lt;string, bool&gt;</c> tooltip payload to its managed
    /// counterpart, which is what Harmony-injected fields and locals expect.
    /// </summary>
    public static (string? Item, bool Flag) ToManagedTooltipTuple(
        this Il2CppSystem.ValueTuple<string, bool> source
    ) => (source.Item1, source.Item2);

    /// <summary>Managed sequence to the projected IL2CPP list.</summary>
    public static Il2CppSystem.Collections.Generic.List<T> ToNativeList<T>(
        this IEnumerable<T> source
    )
    {
        Il2CppThreadProbe.Check();
        var result = new Il2CppSystem.Collections.Generic.List<T>();
        foreach (var item in source)
            result.Add(item);
        return result;
    }

    /// <summary>Managed sequence to the projected IL2CPP set.</summary>
    public static Il2CppSystem.Collections.Generic.HashSet<T> ToNativeHashSet<T>(
        this IEnumerable<T> source
    )
    {
        Il2CppThreadProbe.Check();
        var result = new Il2CppSystem.Collections.Generic.HashSet<T>();
        foreach (var item in source)
            result.Add(item);
        return result;
    }

    /// <summary>Managed dictionary to the projected IL2CPP dictionary.</summary>
    public static Il2CppSystem.Collections.Generic.Dictionary<TKey, TValue> ToNativeDictionary<
        TKey,
        TValue
    >(this Dictionary<TKey, TValue> source)
        where TKey : notnull
    {
        Il2CppThreadProbe.Check();
        ArgumentNullException.ThrowIfNull(source);
        var result = new Il2CppSystem.Collections.Generic.Dictionary<TKey, TValue>(source.Count);
        foreach (var pair in source)
            result[pair.Key] = pair.Value;
        return result;
    }
}
#endif

/// <summary>
/// Cross-platform Unity downcast and reflection helpers.
    ///
    /// On IL2CPP, properties such as <c>Component.transform</c> and <c>Button.targetGraphic</c>
    /// return managed wrapper instances typed as the declared base class (<c>Transform</c>,
    /// <c>Graphic</c>), so C# reference casts (<c>(RectTransform)</c>, <c>as RectTransform</c>,
    /// <c>is Image</c>) fail at runtime even when the underlying native object is a
    /// <c>RectTransform</c> or <c>Image</c>. In addition, IL2CPP fields are exposed on proxy types
    /// as C# properties rather than <see cref="System.Reflection.FieldInfo"/>.
    /// </summary>
    internal static class BppUnityCast
    {
        public static UnityEngine.RectTransform? AsRectTransform(
            this UnityEngine.Transform? transform
        )
        {
            Il2CppThreadProbe.Check();
            if (transform == null)
                return null;
#if BPP_ANDROID
            return transform.TryCast<UnityEngine.RectTransform>();
#else
            return transform as UnityEngine.RectTransform;
#endif
        }

        public static UnityEngine.RectTransform RequireRectTransform(
            this UnityEngine.Transform transform
        )
        {
            Il2CppThreadProbe.Check();
#if BPP_ANDROID
            return transform.TryCast<UnityEngine.RectTransform>()
                ?? throw new System.InvalidCastException(
                    $"Transform '{transform.name}' is not a RectTransform."
                );
#else
            return (UnityEngine.RectTransform)transform;
#endif
        }

        public static UnityEngine.UI.Image? AsImage(this UnityEngine.UI.Graphic? graphic)
        {
            Il2CppThreadProbe.Check();
            if (graphic == null)
                return null;
#if BPP_ANDROID
            return graphic.TryCast<UnityEngine.UI.Image>();
#else
            return graphic as UnityEngine.UI.Image;
#endif
        }

        public static TMPro.TMP_Text? AsTmpText(this UnityEngine.UI.Graphic? graphic)
        {
            Il2CppThreadProbe.Check();
            if (graphic == null)
                return null;
#if BPP_ANDROID
            return graphic.TryCast<TMPro.TMP_Text>();
#else
            return graphic as TMPro.TMP_Text;
#endif
        }

        public static void GetWorldCornersManaged(
            this UnityEngine.RectTransform? rect,
            UnityEngine.Vector3[]? fourCornersArray
        )
        {
            Il2CppThreadProbe.Check();
            if (rect == null || fourCornersArray == null || fourCornersArray.Length < 4)
                return;
#if BPP_ANDROID
            var native =
                new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<UnityEngine.Vector3>(
                    4
                );
            rect.GetWorldCorners(native);
            for (var i = 0; i < 4; i++)
                fourCornersArray[i] = native[i];
#else
            rect.GetWorldCorners(fourCornersArray);
#endif
        }

        public static T? AsIl2Cpp<T>(this object? value)
            where T : class
        {
            Il2CppThreadProbe.Check();
            if (value == null)
                return null;
            if (value is T direct)
                return direct;
#if BPP_ANDROID
            if (
                value is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase il2cppObj
                && typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).IsAssignableFrom(
                    typeof(T)
                )
            )
            {
                return il2cppAccessor<T>.TryCast(il2cppObj);
            }
#endif
            return null;
        }

#if BPP_ANDROID
        private static class il2cppAccessor<T>
            where T : class
        {
            private static readonly System.Func<
                Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase,
                T?
            >? Caster = CreateCaster();

            private static System.Func<
                Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase,
                T?
            >? CreateCaster()
            {
                var method = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
                    .GetMethod("TryCast")
                    ?.MakeGenericMethod(typeof(T));
                return method == null
                    ? null
                    : (System.Func<Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase, T?>)
                        System.Delegate.CreateDelegate(
                            typeof(System.Func<
                                Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase,
                                T?
                            >),
                            method
                        );
            }

            public static T? TryCast(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase obj) =>
                Caster?.Invoke(obj);
        }
#endif
    }

    internal sealed class BppFieldAccessor
    {
        private readonly System.Reflection.FieldInfo? _field;
        private readonly System.Reflection.PropertyInfo? _property;

        private BppFieldAccessor(
            System.Reflection.FieldInfo? field,
            System.Reflection.PropertyInfo? property
        )
        {
            _field = field;
            _property = property;
        }

        public static BppFieldAccessor? Find(System.Type? type, string name)
        {
            if (type == null)
                return null;
#if BPP_ANDROID
            var prop = HarmonyLib.AccessTools.Property(type, name);
            if (prop != null)
                return new BppFieldAccessor(null, prop);
#endif
            var field = HarmonyLib.AccessTools.Field(type, name);
            if (field != null)
                return new BppFieldAccessor(field, null);
#if !BPP_ANDROID
            var fallbackProp = HarmonyLib.AccessTools.Property(type, name);
            if (fallbackProp != null)
                return new BppFieldAccessor(null, fallbackProp);
#endif
            return null;
        }

        public object? GetValue(object? instance)
        {
            Il2CppThreadProbe.Check();
            if (instance == null)
                return null;
            if (_field != null)
                return _field.GetValue(instance);
            if (_property != null && _property.CanRead)
                return _property.GetValue(instance);
            return null;
        }

        public T? GetValue<T>(object? instance)
            where T : class => GetValue(instance).AsIl2Cpp<T>();

        public T? GetStaticValue<T>() where T : class
        {
            Il2CppThreadProbe.Check();
            if (_field?.IsStatic == true)
                return _field.GetValue(null).AsIl2Cpp<T>();
            if (_property?.GetMethod?.IsStatic == true)
                return _property.GetValue(null).AsIl2Cpp<T>();
            return null;
        }

        public T[]? GetReferenceArray<T>(object? instance)
#if BPP_ANDROID
            where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
#else
            where T : class
#endif
        {
            var raw = GetValue(instance);
            if (raw == null)
                return null;
            if (raw is T[] array)
                return array;
#if BPP_ANDROID
            if (raw is Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<T> il2cppArray)
            {
                var result = new T[il2cppArray.Length];
                for (var i = 0; i < il2cppArray.Length; i++)
                    result[i] = il2cppArray[i];
                return result;
            }
#endif
            return null;
        }

        public void SetReferenceArray<T>(object? instance, T[] value)
#if BPP_ANDROID
            where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
#else
            where T : class
#endif
        {
            Il2CppThreadProbe.Check();
            if (instance == null)
                return;
#if BPP_ANDROID
            if (
                _property != null
                && _property.CanWrite
                && _property.PropertyType
                    == typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<T>)
            )
            {
                _property.SetValue(
                    instance,
                    new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<T>(value)
                );
                return;
            }
#endif
            SetValue(instance, value);
        }

        public void SetValue(object? instance, object? value)
        {
            Il2CppThreadProbe.Check();
            if (instance == null)
                return;
            if (_field != null)
                _field.SetValue(instance, value);
            else if (_property != null && _property.CanWrite)
                _property.SetValue(instance, value);
        }
    }
