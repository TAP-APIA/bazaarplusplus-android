namespace MelonLoader;

/// <summary>Severity of a line a melon logs.</summary>
public enum MelonLogLevel
{
    Msg,
    Warning,
    Error,
}

/// <summary>
/// MelonLoader's logger. Lines go to <see cref="Sink"/>, which the bridge points at its BepInEx
/// log source; until then they go to standard output.
/// </summary>
public static class MelonLogger
{
    /// <summary>Receives (melon name, level, text) for every line.</summary>
    public static Action<string, MelonLogLevel, string> Sink { get; set; } =
        static (name, level, text) => Console.WriteLine($"[{name}] {level}: {text}");

    public static void Msg(string text) => Sink("MelonLoader", MelonLogLevel.Msg, text);

    public static void Warning(string text) => Sink("MelonLoader", MelonLogLevel.Warning, text);

    public static void Error(string text) => Sink("MelonLoader", MelonLogLevel.Error, text);

    /// <summary>A melon's own logger, named after it.</summary>
    public sealed class Instance(string name)
    {
        public string Name { get; } = name;

        public void Msg(string text) => Sink(Name, MelonLogLevel.Msg, text);

        public void Msg(object value) => Msg(value?.ToString() ?? "null");

        public void Warning(string text) => Sink(Name, MelonLogLevel.Warning, text);

        public void Warning(object value) => Warning(value?.ToString() ?? "null");

        public void Error(string text) => Sink(Name, MelonLogLevel.Error, text);

        public void Error(string text, Exception exception) =>
            Sink(Name, MelonLogLevel.Error, $"{text}\n{exception}");

        public void Error(object value) => Error(value?.ToString() ?? "null");
    }
}
