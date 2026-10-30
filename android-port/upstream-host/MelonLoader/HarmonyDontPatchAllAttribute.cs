namespace MelonLoader;

/// <summary>
/// MelonLoader skips its assembly-wide Harmony PatchAll for an assembly marked with this. The
/// compat host never runs PatchAll, so the attribute only has to exist.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class HarmonyDontPatchAllAttribute : Attribute;
