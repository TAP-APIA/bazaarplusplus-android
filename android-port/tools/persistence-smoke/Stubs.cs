// Only storage delegates and logging are substituted; queue/gate are production Android sources.
namespace BazaarPlusPlus.Game.PvpBattles
{
    internal sealed class PvpBattleManifest { public string BattleId { get; set; } = ""; }
}
namespace BazaarPlusPlus.Infrastructure.Logging
{
    internal enum BppLogFeatureScope { CombatReplay }
    internal enum BppLogCorrelationPolicy { Short }
    internal sealed record BppLogEvent(BppLogFeatureScope Scope, string Id);
}
namespace BazaarPlusPlus.Infrastructure
{
    using Logging;
    internal static class BppLog
    {
        public static void InfoEvent(BppLogEvent e, params object[] fields) { }
        public static void DebugEvent(BppLogEvent e, Func<object[]> fields) { }
        public static void DebugEvent(BppLogEvent e, Exception error, Func<object[]> fields) { }
    }
}
