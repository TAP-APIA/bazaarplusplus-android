using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameClient.Domain.Models.Cards;
using BepInEx.Logging;
using TheBazaar;
using System.Text.Json;
using System.Security.Cryptography;
namespace BazaarHelper;

internal static class Recorder
{
    internal static ManualLogSource Log = null!;
    private static string run = ""; private static Opening? opening; private static readonly HashSet<string> Settled = new();
    // One player fight per run per day; the client keeps reporting PVPCombat after a fight settles.
    private static readonly HashSet<string> RecordedDays = new();
    private static string lastOpponentSources = "";
    private sealed record Opening(string Run, int Day, int Hour, string Hero, string Opponent, string? DataVersion, string? Etag, List<CardRecord> Cards);
    internal sealed class CardRecord
    {
        public string side { get; set; } = "player"; public string zone { get; set; } = "items"; public string templateId { get; set; } = ""; public string instanceId { get; set; } = ""; public string? name
        {
            get; set;
        }
        public string? tier
        {
            get; set;
        }
        public string? size
        {
            get; set;
        }
        public string? enchant
        {
            get; set;
        }
        public string tagsJson { get; set; } = "[]"; public string attributesJson { get; set; } = "{}"; public string? boardSection
        {
            get; set;
        }
        public string? socket
        {
            get; set;
        }
        public int position
        {
            get; set;
        }
    }
    /// <summary>
    /// The player behind a PvP fight: the message's own opponent first, then the game's SimPvpOpponent —
    /// the same order the desktop recorder uses. Monster/PvE fights reuse the combat state without a
    /// player opponent (the placeholder <see cref="EHero.Common"/> is the default enum value), so they
    /// are skipped and only real player-vs-player battles are recorded and uploaded.
    /// </summary>
    private static SimPvpOpponent? PlayerOpponent(NetMessageGameSim message)
    {
        var state = message.Data.CurrentState?.PvpOpponent;
        var sim = Data.SimPvpOpponent;
        // Worth one line per change: if the client ever stops filling both, the export says why nothing
        // is recorded instead of silently uploading monster fights.
        string seen = $"state={(state == null ? "null" : state.Hero.ToString())}, sim={(sim == null ? "null" : sim.Hero.ToString())}";
        if (seen != lastOpponentSources)
        {
            lastOpponentSources = seen;
            Log.LogInfo("PvP opponent sources: " + seen);
        }
        if (Usable(state)) return state;
        if (Usable(sim)) return sim;
        Log.LogDebug("Skip non-player combat: no player opponent hero");
        return null;
    }

    private static bool Usable(SimPvpOpponent? opponent) =>
        opponent != null && opponent.Hero != EHero.Common;

    /// <summary>Hero display name, or null for "no hero" — EHero.Common is the monster/placeholder value.</summary>
    private static string? HeroName(EHero? hero) =>
        hero == null || hero.Value == EHero.Common ? null : hero.Value.ToString();

    internal static void BlockPlayback()
    {
        opening = null;
    }
    internal static void Failure(Exception e)
    {
        Log.LogWarning("Recorder degraded: " + e.GetType().Name + " " + e.Message);
        NativeBridge.Status("采集出现兼容性问题，请保留日志");
    }
    internal static void Observe(INetMessage message)
    {
        var aggregate = message.TryCast<NetMessageAggregate>();
        if (aggregate != null)
        {
            for (int i = 0; i < aggregate.Messages.Count; i++)
                Observe(aggregate.Messages[i]);
            return;
        }
        var initialized = message.TryCast<NetMessageRunInitialized>();
        if (initialized != null)
        {
            CombatTimer.Hide();
            if (run != initialized.RunId)
            {
                opening = null;
                run = initialized.RunId;
            }
            NativeBridge.Begin(run, initialized.PlayMode, HeroName(Data.Run?.Player?.Hero));
            return;
        }
        var game = message.TryCast<NetMessageGameSim>();
        if (game == null || string.IsNullOrWhiteSpace(run))
            return;
        if (game.Data.Run is { } progress)
            NativeBridge.UpdateRun(run, progress.Victories, progress.Defeats, progress.Day, HeroName(Data.Run?.Player?.Hero));
        if (game.Data.CurrentState?.StateName == ERunState.PVPCombat)
        {
            var opponent = PlayerOpponent(game);
            if (opponent == null)
                return;
            var cards = OpeningCards(game);
            if (cards.Count == 0)
                return;
            var live = Data.Run;

            // Only managed immutable values survive the opening callback. Never retain native messages.
            int day = (int)(game.Data.Run?.Day ?? live?.Day ?? 0), hour = (int)(game.Data.Run?.Hour ?? live?.Hour ?? 0);
            string fight = run + "|" + day;
            if (opening is { } previous && previous.Run == run && previous.Day == day && previous.Hour == hour)
                return;
            if (day > 0 && !RecordedDays.Add(fight))
                return;
            opening = new Opening(run, day, hour, HeroName(live?.Player?.Hero) ?? "", opponent.Hero.ToString(), game.Data.Run?.DataVersion ?? live?.DataVersion?.ToString(), ReadEtag(), cards);
            Log.LogInfo($"PvP opening: player={cards.Count(c => c.side == "player")}, opponent={cards.Count(c => c.side == "opponent")}, opponent hero={opponent.Hero}, enchanted={cards.Count(c => c.side == "player" && c.zone == "items" && c.enchant != null)}");
        }
        var state = game.Data.CurrentState?.StateName.ToString();
        if (state is "EndRunDefeat" or "EndRunVictory")
            NativeBridge.Complete(run);
    }
    private static List<CardRecord> OpeningCards(NetMessageGameSim message)
    {
        var result = new Dictionary<string, CardRecord>();
        var events = message.Data.Events;
        for (int i = 0; i < events.Count; i++)
        {
            var spawn = events[i].TryCast<GameSimEventCardSpawned>();
            if (spawn == null)
                continue;
            var section = OptionalValue.Read<EInventorySection>(spawn, "Section");
            if (spawn.Type != ECardType.Skill && section != EInventorySection.Hand)
                continue;
            message.Data.Cards.TryGetValue(spawn.InstanceId, out var update);
            result[spawn.InstanceId] = Project(spawn.InstanceId, spawn.TemplateId, spawn.CombatantId, spawn.Type, update, spawn);
        }
        for (int i = 0; i < events.Count; i++)
        {
            var skill = events[i].TryCast<GameSimEventPlayerSkillEquipped>();
            if (skill == null)
                continue;
            message.Data.Cards.TryGetValue(skill.InstanceId, out var update);
            if (result.TryGetValue(skill.InstanceId, out var known))
            {
                known.zone = "skills";
                known.side = skill.Owner == ECombatantId.Opponent ? "opponent" : "player";
            }
            else
                result[skill.InstanceId] = Project(skill.InstanceId, "", skill.Owner, ECardType.Skill, update, null);
        }
        return result.Values.ToList();
    }
    private static CardRecord Project(string instance, string template, ECombatantId side, ECardType type, SimUpdateCard? update, GameSimEventCardSpawned? spawn)
    {
        Card? live = null;
        try
        {
            live = Data.GetCard(instance);
        }
        catch { }
        var section = OptionalValue.Read<EInventorySection>(update?.Placement, "Section") ?? OptionalValue.Read<EInventorySection>(spawn, "Section") ?? OptionalValue.Read<EInventorySection>(live, "Section");
        var socket = OptionalValue.Read<EContainerSocketId>(update?.Placement, "Socket") ?? OptionalValue.Read<EContainerSocketId>(spawn, "Socket") ?? OptionalValue.Read<EContainerSocketId>(live, "LeftSocketId");
        var card = new CardRecord { side = side == ECombatantId.Opponent ? "opponent" : "player", zone = type == ECardType.Skill ? "skills" : "items", instanceId = instance, templateId = string.IsNullOrEmpty(template) ? live?.TemplateId.ToString() ?? "" : template, name = live?.Template?.InternalName, tier = (OptionalValue.Read<ETier>(update, "Tier") ?? live?.Tier)?.ToString(), size = (OptionalValue.Read<ECardSize>(update, "Size") ?? live?.Size)?.ToString(), enchant = Enchantment(update, live)?.ToString(), boardSection = section?.ToString(), socket = socket?.ToString(), position = socket.HasValue ? (int)socket.Value : 0 };
        var attributes = new Dictionary<string, int>();
        if (update?.Attributes != null)
        {
            var it = update.Attributes.GetEnumerator();
            while (it.MoveNext())
            {
                var p = it.Current;
                attributes[p.Key.ToString()] = p.Value.Value;
            }
        }
        else if (live?.Attributes != null)
        {
            var it = live.Attributes.GetEnumerator();
            while (it.MoveNext())
            {
                var p = it.Current;
                attributes[p.Key.ToString()] = p.Value;
            }
        }
        card.attributesJson = JsonSerializer.Serialize(attributes);
        var tags = new List<string>();
        if (update?.Tags != null)
        {
            var it = update.Tags.GetEnumerator();
            while (it.MoveNext())
                tags.Add(it.Current.ToString());
        }
        else if (live?.Tags != null)
        {
            var it = live.Tags.GetEnumerator();
            while (it.MoveNext())
                tags.Add(it.Current.ToString());
        }
        card.tagsJson = JsonSerializer.Serialize(tags);
        if (live?.TryCast<SkillCard>() is { } skill)
            card.name = skill.Template?.Localization?.Title?.Text ?? card.name;
        return card;
    }
    private static bool enchantFallbackFailed;
    /// <summary>
    /// Enchantment arrives in the fight message for the opponent's board (there is no local card to read),
    /// but the message never carries the player's own enchantments — SimUpdateCard leaves the field null for
    /// cards the client already owns, so those only exist on the local ItemCard (same live-card path the
    /// desktop recorder uses). Skills have no Enchantment member at all, so the read must tolerate a missing
    /// member, and any other failure falls back to "no enchantment" instead of losing the battle.
    /// </summary>
    private static EEnchantmentType? Enchantment(SimUpdateCard? update, Card? live)
    {
        var fromMessage = OptionalValue.Read<EEnchantmentType>(update, "Enchantment");
        if (fromMessage != null)
            return fromMessage;
        try { return OptionalValue.TryRead<EEnchantmentType>(live, "Enchantment"); }
        catch (Exception error)
        {
            if (!enchantFallbackFailed)
            {
                enchantFallbackFailed = true;
                Log.LogWarning("Live enchantment read failed: " + error.GetType().Name);
            }
            return null;
        }
    }
    internal static void Settle(NetMessageCombatSim message)
    {
        var o = opening;
        if (o == null || o.Run != run)
            return;
        string? outcome = message.Data.Winner == ECombatantId.Player ? "win" : message.Data.Loser == ECombatantId.Player ? "loss" : null;
        if (outcome == null)
            return;
        var id = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(o.Run + ":" + o.Day + ":" + o.Hour + ":" + message.MessageId)));
        if (Settled.Contains(id))
            return;
        // Read the account stamp now: this is settlement, not opening or a future upload.
        var stamp = NativeBridge.Session();
        var cards = o.Cards.ToList();
        if (!cards.Any(c => c.side == "player" && c.zone == "items"))
        {
            var live = Data.GetCards<Card>(ECombatantId.Player, new Il2CppSystem.Nullable<EInventorySection>(EInventorySection.Hand));
            var it = live.GetEnumerator();
            while (it.MoveNext())
            {
                var c = it.Current;
                cards.Add(Project(c.InstanceId.ToString(), c.TemplateId.ToString(), ECombatantId.Player, c.Type, null, null));
            }
        }
        if (!cards.Any(c => c.side == "player" && c.zone == "skills") && Data.Run?.Player?.Skills != null)
        {
            var it = Data.Run.Player.Skills.GetEnumerator();
            while (it.MoveNext())
            {
                var c = it.Current;
                cards.Add(Project(c.InstanceId.ToString(), c.TemplateId.ToString(), ECombatantId.Player, ECardType.Skill, null, null));
            }
        }
        var metrics = new List<object>();
        bool metricsComplete = true;
        try
        {
            var names = new Dictionary<string, string> { { "DamageDone", "damage" }, { "ShieldAdded", "shield" }, { "HealAdded", "healing" }, { "PoisonAdded", "poison" }, { "BurnAdded", "burn" }, { "HastedCardsCount", "haste" }, { "SlowedCardsCount", "slow" }, { "FrozenCardsCount", "freeze" }, { "UseCount", "use_count" }, { "RegenAdded", "regen" }, { "RageAdded", "rage" }, { "TempoAdded", "tempo_added" }, { "TempoSpent", "tempo_spent" } };
            var stats = message.Data.CardStats.GetEnumerator();
            while (stats.MoveNext())
            {
                var pair = stats.Current;
                var source = cards.FirstOrDefault(c => c.instanceId == pair.Key);
                if (source == null)
                    continue;
                var it = pair.Value.GetEnumerator();
                while (it.MoveNext())
                {
                    var p = it.Current;
                    if (!names.TryGetValue(p.Key.ToString(), out var metric))
                        continue;
                    metrics.Add(new
                    {
                        side = source.side,
                        sourceInstanceId = pair.Key,
                        sourceKind = source.zone == "skills" ? "skill" : "item",
                        metric,
                        value = p.Value,
                        unit = metric is "haste" or "slow" or "freeze" or "use_count" ? "applications" : "amount",
                        eventCount = 1,
                        coverage = "complete"
                    });
                }
            }
            var actions = new Dictionary<string, string> { { "CardCharge", "charge" }, { "CardHaste", "haste_event" }, { "CardSlow", "slow_event" }, { "CardFreeze", "freeze_event" }, { "CardDestroy", "destroy" }, { "CardModifyAttribute", "attribute_change" }, { "FlyingStart", "flying" }, { "CardDisable", "disable" }, { "CardForceUse", "force_use" } };
            var counts = new Dictionary<(string, string), int>();
            for (int frame = 0; frame < message.Data.Frames.Count; frame++)
            {
                var events = message.Data.Frames[frame].Events;
                for (int index = 0; index < events.Count; index++)
                {
                    var effect = events[index].TryCast<CombatSimEventEffectExecuted>();
                    if (effect == null || !actions.TryGetValue(effect.ActionType.ToString(), out var metric))
                        continue;
                    var source = EffectSource.Read(effect);
                    if (string.IsNullOrEmpty(source) || !cards.Any(c => c.instanceId == source))
                        continue;
                    var key = (source, metric);
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }
            foreach (var pair in counts)
            {
                var source = cards.First(c => c.instanceId == pair.Key.Item1);
                metrics.Add(new
                {
                    side = source.side,
                    sourceInstanceId = source.instanceId,
                    sourceKind = source.zone == "skills" ? "skill" : "item",
                    metric = pair.Key.Item2,
                    value = pair.Value,
                    unit = "applications",
                    eventCount = pair.Value,
                    coverage = "complete"
                });
            }
        }
        catch (Exception error)
        {
            metricsComplete = false;
            Log.LogWarning("Combat metrics partial: " + error.GetType().Name);
        }
        var complete = metricsComplete && cards.Any(c => c.side == "player" && c.zone == "items") && cards.Any(c => c.side == "opponent" && c.zone == "items");
        NativeBridge.Capture(stamp, new
        {
            runId = o.Run,
            dataVersion = o.DataVersion,
            gameDataEtag = o.Etag,
            dataVersionSource = o.DataVersion == null ? "unknown" : "recorded",
            detail = new
            {
                battle = new
                {
                    battleId = id,
                    recordedAt = DateTimeOffset.UtcNow.ToString("O"),
                    day = o.Day > 0 ? o.Day : (int)(Data.Run?.Day ?? 0),
                    hour = o.Hour,
                    playerHero = HeroName(Data.Run?.Player?.Hero) ?? o.Hero,
                    opponentHero = o.Opponent,
                    result = outcome,
                    durationMs = Math.Max(0, message.Data.Frames.Count - 1) * 50,
                    captureStatus = complete ? "complete" : "partial"
                },
                cards,
                metrics
            }
        });
        Settled.Add(id);
        if (Settled.Count > 256)
            Settled.Remove(Settled.First());
        // The client re-sends CombatSim for the same fight with a fresh MessageId; keeping the opening
        // alive made each resend look like a new battle (two or three records for one day).
        opening = null;
        NativeBridge.Status($"已记录第 {o.Day} 天 PvP · 我方 {cards.Count(c => c.side == "player")} / 对手 {cards.Count(c => c.side == "opponent")} 张卡");
    }
    private static string? ReadEtag()
    {
        try
        {
            var path = Path.Combine(TheBazaar.DataManagement.DataManifestActions.GetCachePath(), "manifest.json");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var etag = json.RootElement.GetProperty("Entries").GetProperty("GameData").GetProperty("ETag").GetString()?.Trim('"').ToLowerInvariant();
            return etag != null && System.Text.RegularExpressions.Regex.IsMatch(etag, "\\A[0-9a-f]{16,128}\\z") ? etag : null;
        }
        catch { return null; }
    }
}
