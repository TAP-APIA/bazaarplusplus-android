using System.Runtime.InteropServices;
using Mono.Cecil;

namespace BazaarPlusPlus.OfficialPayload;

/// <summary>
/// Whether a payload binds against this device before it is swapped in. Upstream builds against
/// LemonLoader's interop and loader; here the interop is generated on the device by BepInEx and
/// the loader is the MelonLoader compat assembly, so a member upstream calls may be missing.
/// Every assembly, type and member the payload references outside itself and the runtime must
/// resolve in the host directories, at a version no lower than the one referenced.
/// </summary>
public static class PayloadCompatibility
{
    /// <param name="payloadMelonDirectory">The payload's <c>MelonLoader</c> directory.</param>
    /// <param name="hostDirectories">Where the host provides the rest: interop, BepInEx core, plugins.</param>
    /// <returns>Null when compatible, otherwise a reason code naming the first gap.</returns>
    public static string? Check(string payloadMelonDirectory, IEnumerable<string> hostDirectories)
    {
        var mods = Path.Combine(payloadMelonDirectory, "Mods");
        var userLibs = Path.Combine(payloadMelonDirectory, "UserLibs");
        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        using var resolver = new DirectoryResolver(
            new[] { mods, userLibs }.Concat(hostDirectories).Append(runtimeDirectory)
        );
        var parameters = new ReaderParameters { AssemblyResolver = resolver };

        if (!Directory.Exists(mods))
            return "mods_missing";
        foreach (var path in Directory.GetFiles(mods, "*.dll"))
        {
            using var module = ModuleDefinition.ReadModule(path, parameters);
            var failure = CheckModule(module, resolver, runtimeDirectory);
            if (failure != null)
                return $"{failure}@{Path.GetFileName(path)}";
        }
        return null;
    }

    private static string? CheckModule(
        ModuleDefinition module,
        IAssemblyResolver resolver,
        string runtimeDirectory
    )
    {
        var framework = TargetFrameworkMajor(module);
        if (framework > Environment.Version.Major)
            return $"target_framework:{framework}";

        foreach (var reference in module.AssemblyReferences)
        {
            if (IsRuntimeAssembly(reference.Name, runtimeDirectory))
                continue;
            AssemblyDefinition resolved;
            try
            {
                resolved = resolver.Resolve(reference);
            }
            catch (AssemblyResolutionException)
            {
                return $"assembly_missing:{reference.Name}";
            }
            if (resolved.Name.Version < reference.Version)
                return $"assembly_too_old:{reference.Name}:{resolved.Name.Version}<{reference.Version}";
        }

        foreach (var type in module.GetTypeReferences())
        {
            if (IsRuntimeScope(type.Scope, runtimeDirectory))
                continue;
            if (type.Resolve() == null)
                return $"type_missing:{type.FullName}";
        }

        foreach (var member in module.GetMemberReferences())
        {
            // Multi-dimensional array accessors are synthesized by the runtime.
            if (member.DeclaringType is ArrayType)
                continue;
            if (IsRuntimeScope(member.DeclaringType.GetElementType().Scope, runtimeDirectory))
                continue;
            if (member.Resolve() == null)
                return $"member_missing:{member.FullName}";
        }
        return null;
    }

    // The runtime is what it is: the target framework check covers it, member by member it would
    // only repeat the JIT's own binding.
    private static bool IsRuntimeScope(IMetadataScope scope, string runtimeDirectory) =>
        scope is AssemblyNameReference reference && IsRuntimeAssembly(reference.Name, runtimeDirectory);

    private static bool IsRuntimeAssembly(string name, string runtimeDirectory) =>
        name is "netstandard" or "mscorlib"
        || File.Exists(Path.Combine(runtimeDirectory, name + ".dll"));

    // Cecil's own resolver also searches the running process's trusted assemblies, which would
    // answer for what the device lacks. This one looks only where it is told, first match wins.
    private sealed class DirectoryResolver : IAssemblyResolver
    {
        private readonly string[] _directories;
        private readonly Dictionary<string, AssemblyDefinition> _cache = new(StringComparer.Ordinal);

        public DirectoryResolver(IEnumerable<string> directories) =>
            _directories = directories.Where(Directory.Exists).ToArray();

        public AssemblyDefinition Resolve(AssemblyNameReference name) =>
            Resolve(name, new ReaderParameters());

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (_cache.TryGetValue(name.Name, out var cached))
                return cached;
            foreach (var directory in _directories)
            {
                var path = Path.Combine(directory, name.Name + ".dll");
                if (!File.Exists(path))
                    continue;
                var assembly = AssemblyDefinition.ReadAssembly(
                    path,
                    new ReaderParameters { AssemblyResolver = this }
                );
                _cache[name.Name] = assembly;
                return assembly;
            }
            throw new AssemblyResolutionException(name);
        }

        public void Dispose()
        {
            foreach (var assembly in _cache.Values)
                assembly.Dispose();
            _cache.Clear();
        }
    }

    private static int TargetFrameworkMajor(ModuleDefinition module)
    {
        var attribute = module.Assembly.CustomAttributes.FirstOrDefault(attribute =>
            attribute.AttributeType.FullName
            == "System.Runtime.Versioning.TargetFrameworkAttribute"
        );
        var text = attribute?.ConstructorArguments.FirstOrDefault().Value as string;
        const string prefix = ".NETCoreApp,Version=v";
        return
            text != null
            && text.StartsWith(prefix, StringComparison.Ordinal)
            && Version.TryParse(text[prefix.Length..], out var version)
            ? version.Major
            : 0;
    }
}
