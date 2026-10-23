using Mono.Cecil;

// Portable DTO codecs need CLR MessagePack interfaces. The game has unsigned IL2CPP
// proxy classes with the same simple names; give the portable runtime private identities.
// Namespaces and wire contracts remain unchanged, and native messages keep the game codec.
if (args.Length != 2) throw new ArgumentException("Expected build-output and destination directories.");
var source = Path.GetFullPath(args[0]);
var destination = Path.GetFullPath(args[1]);
Directory.CreateDirectory(destination);
var aliases = new Dictionary<string, string>
{
    ["MessagePack"] = "Bpp.Managed.MessagePack",
    ["MessagePack.Annotations"] = "Bpp.Managed.MessagePack.Annotations",
    ["Microsoft.NET.StringTools"] = "Bpp.Managed.StringTools"
};
foreach (var name in new[] { "BazaarPlusPlus.ModApi", "MessagePack", "MessagePack.Annotations", "Microsoft.NET.StringTools" })
{
    using var module = ModuleDefinition.ReadModule(Path.Combine(source, name + ".dll"));
    if (aliases.TryGetValue(module.Assembly.Name.Name, out var alias))
        module.Assembly.Name.Name = alias;
    foreach (var reference in module.AssemblyReferences)
        if (aliases.TryGetValue(reference.Name, out var renamed)) reference.Name = renamed;
    var path = Path.Combine(destination, module.Assembly.Name.Name + ".dll");
    module.Write(path);
    using var check = ModuleDefinition.ReadModule(path);
    if (check.AssemblyReferences.Any(reference => aliases.ContainsKey(reference.Name)))
        throw new InvalidOperationException($"Unisolated MessagePack reference: {path}");
    Console.WriteLine(check.Assembly.Name.FullName);
}
