#nullable enable
using BazaarPlusPlus.Infrastructure;
using BazaarGameClient.Domain.Models.Cards;
using BazaarGameShared.Domain.Cards.Socket;
using BazaarGameShared.Domain.Core.Types;
using BazaarPlusPlus.Game.Input;
using HarmonyLib;
using TheBazaar;
using UnityEngine;

namespace BazaarPlusPlus.Game.MusicNotes;

// The game already renders placed notes above items outside combat. Shift adds only the
// implied sockets, including occupied ones, using native assets, animation and async cleanup.
internal sealed class NativeMusicNotePreviewController : MonoBehaviour
{
    private static readonly System.Reflection.FieldInfo? CatalogField = AccessTools.Field(
        typeof(BoardManager),
        "_socketEffectVfxCatalog"
    );
    private readonly List<MusicNoteSpawnPlacement> _placements = [];
    private readonly List<MusicNoteSpawnPlacement> _shown = [];
    private BoardManager? _board;
    private MusicNoteSpawnHintPresenter? _presenter;
    private float _nextRefresh;
    private readonly NativeMusicNoteBackgroundPreview _nativeBackgrounds = new();

    private void Update()
    {
        // A managed exception escaping a per-frame callback becomes std::terminate across the
        // IL2CPP boundary and kills the process with no managed stack.
        try
        {
            TickGuarded();
        }
        catch (Exception ex)
        {
            BppStartupTrace.Write("NativeMusicNotePreviewController-update-exception:" + ex.GetType().FullName + " | " + ex.Message);
        }
    }

    private void TickGuarded()
    {
        var board = Singleton<BoardManager>.Instance;
        if (board != _board)
        {
            Release();
            _board = board;
        }

        if (
            board == null
            || !board.isActiveAndEnabled
            || !Application.isFocused
            || !BppHotkeyService.IsActive(BppHotkeyActionId.HoldUpgradePreview)
            || Data.Run?.Player?.Socket?.Container == null
            || Data.IsInCombat
            || AppState.CurrentState is ReplayState
            || board.IsRecapViewOpen
            || Data.NewDayTransitionController?.IsActive == true
            || SearchOpponentTransition.IsInTransition
        )
        {
            Clear();
            return;
        }

        if (Time.unscaledTime < _nextRefresh)
            return;
        _nextRefresh = Time.unscaledTime + 0.15f;

        if (_presenter == null)
        {
            if (
                CatalogField?.GetValue(board) is not SocketEffectVfxCatalog catalog
                || catalog == null
            )
                return;
            // Do not borrow BoardManager's presenter: native card hover clears that instance.
            _presenter = new MusicNoteSpawnHintPresenter(catalog);
        }

        _placements.Clear();
        var player = Data.Run.Player;
        var container = player.Socket.Container;
        var placed = new int?[container.Sockets.Length];
        _nativeBackgrounds.BeginRefresh();
        foreach (var entity in Data.Entities.Values)
        {
            if (
                entity is SocketEffect effect
                && ReferenceEquals(effect.Owner, player)
                && effect.Section.ToManagedNullable() == EInventorySection.Hand
                && effect.LeftSocketId.HasValue
                && (int)effect.LeftSocketId.Value >= 0
                && (int)effect.LeftSocketId.Value < placed.Length
                && effect.Template.TryCast<TCardMusicNoteSocketEffect>() is { } note
            )
            {
                var socket = (int)effect.LeftSocketId.Value;
                placed[socket] = (int)note.MusicNote;
                if (!container.IsSocketLocked(socket))
                    _nativeBackgrounds.Show(
                        Data.CardAndSkillLookup.GetCardController(effect) as SocketEffectController
                    );
            }
        }
        _nativeBackgrounds.EndRefresh();
        var letters = MusicNoteSocketInference.Resolve(placed);
        for (var i = 0; i < letters.Length; i++)
        {
            // Placed notes already have native visuals. Item/non-note-effect occupancy does
            // not suppress an inferred letter; only locked sockets are excluded.
            if (placed[i].HasValue || letters[i] is not int letter || container.IsSocketLocked(i))
                continue;
            var target = board.GetItemSocketController((EContainerSocketId)i, ECombatantId.Player);
            if (target != null && target.gameObject.activeInHierarchy)
                _placements.Add(new MusicNoteSpawnPlacement((EMusicNote)letter, target.transform));
        }

        if (_placements.SequenceEqual(_shown))
            return;
        _shown.Clear();
        _shown.AddRange(_placements);
        _presenter.Show(
            _placements
                .ToNativeList()
                .TryCast<Il2CppSystem.Collections.Generic.IReadOnlyList<MusicNoteSpawnPlacement>>()!
        );
    }

    private void Clear()
    {
        _nativeBackgrounds.Restore();
        if (_shown.Count != 0)
            _presenter?.Clear();
        _shown.Clear();
        _nextRefresh = 0f;
    }

    private void Release()
    {
        _nativeBackgrounds.Restore();
        _presenter?.Dispose();
        _presenter = null;
        _board = null;
        _shown.Clear();
        _nextRefresh = 0f;
    }

    private void OnDisable() => Release();

    private void OnDestroy() => Release();
}
