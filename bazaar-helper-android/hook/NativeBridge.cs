using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace BazaarHelper;

// Versioned function table published by the Java-loaded JNI library. Android linker namespaces
// can map a second copy of the same .so during P/Invoke; calling original addresses preserves JVM state.
internal static class NativeBridge
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Api
    {
        internal int Version; internal IntPtr Session, Capture, Begin, Complete, Status, Timer;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr SessionCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CaptureCall([MarshalAs(UnmanagedType.LPUTF8Str)] string stamp, [MarshalAs(UnmanagedType.LPUTF8Str)] string json);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StringCall([MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StatusCall([MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void TimerCall(int elapsedMilliseconds);
    private static readonly TimerCall SetTimer;
    private static readonly SessionCall GetSession;
    private static readonly CaptureCall Save;
    private static readonly StringCall BeginRun, CompleteRun;
    private static readonly StatusCall SetStatus;
    static NativeBridge()
    {
        string address = Environment.GetEnvironmentVariable("BAZAARHELPER_BRIDGE_API") ?? throw new InvalidOperationException("JNI gateway missing");
        var api = Marshal.PtrToStructure<Api>(new IntPtr(unchecked((long)ulong.Parse(address, NumberStyles.HexNumber, CultureInfo.InvariantCulture))));
        if (api.Version != 2 || api.Session == IntPtr.Zero || api.Capture == IntPtr.Zero || api.Begin == IntPtr.Zero || api.Complete == IntPtr.Zero || api.Status == IntPtr.Zero || api.Timer == IntPtr.Zero)
            throw new InvalidOperationException("Unsupported JNI gateway ABI");
        SetTimer = Marshal.GetDelegateForFunctionPointer<TimerCall>(api.Timer);
        GetSession = Marshal.GetDelegateForFunctionPointer<SessionCall>(api.Session);
        Save = Marshal.GetDelegateForFunctionPointer<CaptureCall>(api.Capture);
        BeginRun = Marshal.GetDelegateForFunctionPointer<StringCall>(api.Begin);
        CompleteRun = Marshal.GetDelegateForFunctionPointer<StringCall>(api.Complete);
        SetStatus = Marshal.GetDelegateForFunctionPointer<StatusCall>(api.Status);
    }
    internal static void Timer(int elapsedMilliseconds) => SetTimer(elapsedMilliseconds);
    internal static string Session() => Marshal.PtrToStringUTF8(GetSession()) ?? "";
    internal static void Capture(string stamp, object data)
    {
        if (Save(stamp, JsonSerializer.Serialize(data)) != 1)
            throw new IOException("Capture was not persisted");
    }
    internal static void Begin(string run, string? mode, string? hero) => BeginRun(JsonSerializer.Serialize(new { runId = run, gameMode = mode, playerHero = hero }));
    internal static void UpdateRun(string run, uint victories, uint losses, uint day, string? hero) => BeginRun(JsonSerializer.Serialize(new { runId = run, victories, losses, finalDay = day, playerHero = hero }));
    internal static void Complete(string run) => CompleteRun(run);
    internal static void Status(string status) => SetStatus(status);
}
