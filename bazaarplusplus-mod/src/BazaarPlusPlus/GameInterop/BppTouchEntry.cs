#nullable enable
using System;
using BazaarPlusPlus.Game.CollectionPanel;
using BazaarPlusPlus.Game.HistoryPanel;
using BazaarPlusPlus.Game.LiveBuildPanel;
using BazaarPlusPlus.GameInterop.Fonts;
using BazaarPlusPlus.Infrastructure;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BazaarPlusPlus.GameInterop;

/// <summary>
/// On-screen touch floating dock for the Android port.
///
/// Renders a ScreenSpaceOverlay UGUI Canvas at sorting order 32000 so it is always visible above
/// both game canvases and BPP overlay panels. All helper methods that take delegates or non-Unity
/// types are static so <c>ClassInjector</c> never generates trampolines for them.
/// </summary>
internal sealed class BppTouchEntry : MonoBehaviour
{
    private static int _nextPanel;
    private GameObject? _canvasRoot;
    private GameObject? _drawerRoot;
    private bool _expanded = true;
    private bool _multiTouchLatch;
    private bool _fontApplied;

    private void OnEnable()
    {
        // Do not build UGUI/TMP objects during Chainloader startup (Plugin.Load): the game's first
        // scene and TMP resources are not initialized yet. Update() builds the dock once the native
        // font runtime is ready.
    }

    private void Update()
    {
        try
        {
            if (_canvasRoot == null)
            {
                if (TheBazaar.Localization.NotoFontFallbackRuntime._loadedSansPrimary != null)
                    EnsureCanvas();
            }
            else if (!_fontApplied)
            {
                TryApplyFont();
            }

            PollThreeFingerGesture();
        }
        catch (Exception ex)
        {
            BppStartupTrace.Write(
                "touch-entry:update-failed " + ex.GetType().Name + " | " + ex.Message
            );
        }
    }

    private void OnDestroy()
    {
        if (_canvasRoot != null)
            Destroy(_canvasRoot);
    }

    private void EnsureCanvas()
    {
        if (_canvasRoot != null)
            return;

        _canvasRoot = BppGameObjectFactory.Create(
            "BppTouchEntryCanvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster)
        );
        _canvasRoot.transform.SetParent(transform, false);

        var canvas = _canvasRoot.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32000;

        var scaler = _canvasRoot.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        // Dock container anchored near top-right (left of the corner X/settings buttons).
        var dockRect = CreateRect("BppTouchDock", _canvasRoot.transform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-180f, -18f), new Vector2(460f, 52f));

        // Main toggle pill on the right end of the bar.
        CreatePillButton(
            dockRect,
            "ToggleBtn",
            "BPP",
            new Vector2(368f, 0f),
            new Vector2(92f, 48f),
            new Color(0.14f, 0.11f, 0.08f, 0.92f),
            new Color(0.96f, 0.82f, 0.46f, 1f),
            ToggleDrawer
        );

        var drawerRect = CreateRect(
            "Drawer",
            dockRect,
            new Vector2(0f, 1f),
            new Vector2(0f, 1f),
            Vector2.zero,
            new Vector2(360f, 48f)
        );
        _drawerRoot = drawerRect.gameObject;

        CreatePillButton(
            drawerRect,
            "BuildsBtn",
            "配装 Builds",
            new Vector2(0f, 0f),
            new Vector2(114f, 48f),
            new Color(0.18f, 0.14f, 0.09f, 0.92f),
            new Color(0.95f, 0.90f, 0.78f, 1f),
            static () =>
            {
                BppStartupTrace.Write("touch-entry:builds");
                LiveBuildPanel.ToggleFromUi();
            }
        );

        CreatePillButton(
            drawerRect,
            "CardsBtn",
            "图鉴 Cards",
            new Vector2(122f, 0f),
            new Vector2(114f, 48f),
            new Color(0.18f, 0.14f, 0.09f, 0.92f),
            new Color(0.95f, 0.90f, 0.78f, 1f),
            static () =>
            {
                BppStartupTrace.Write("touch-entry:cards");
                CollectionPanel.ToggleFromUi();
            }
        );

        CreatePillButton(
            drawerRect,
            "HistoryBtn",
            "历史 History",
            new Vector2(244f, 0f),
            new Vector2(114f, 48f),
            new Color(0.18f, 0.14f, 0.09f, 0.92f),
            new Color(0.95f, 0.90f, 0.78f, 1f),
            static () =>
            {
                BppStartupTrace.Write("touch-entry:history");
                HistoryPanel.ToggleFromUi();
            }
        );

        TryApplyFont();
        BppStartupTrace.Write("touch-entry:ugui-ready");
    }

    private void ToggleDrawer()
    {
        _expanded = !_expanded;
        if (_drawerRoot != null)
            _drawerRoot.SetActive(_expanded);
    }

    private void TryApplyFont()
    {
        if (_canvasRoot == null)
            return;
        if (NativeGameTypography.PrepareOwnedText(out var prep) != NativeGameTypography.Outcome.Ready || prep == null)
            return;

        var labels = _canvasRoot.GetComponentsInChildren<TextMeshProUGUI>(true);
        if (labels == null || labels.Length == 0)
            return;

        for (var i = 0; i < labels.Length; i++)
        {
            if (labels[i] != null)
                prep.Apply(labels[i]);
        }
        _fontApplied = true;
    }

    private void PollThreeFingerGesture()
    {
        var touchscreen = Touchscreen.current;
        if (touchscreen == null)
            return;

        var pressed = 0;
        var touches = touchscreen.touches;
        for (var i = 0; i < touches.Count; i++)
        {
            if (touches[i].press.isPressed)
                pressed++;
        }

        if (pressed < 3)
        {
            _multiTouchLatch = false;
            return;
        }

        if (_multiTouchLatch)
            return;

        _multiTouchLatch = true;
        CycleNext();
    }

    private static RectTransform CreateRect(
        string name,
        Transform parent,
        Vector2 anchor,
        Vector2 pivot,
        Vector2 anchoredPosition,
        Vector2 sizeDelta
    )
    {
        var go = BppGameObjectFactory.Create(name, typeof(RectTransform));
        var rect = go.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = pivot;
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = sizeDelta;
        return rect;
    }

    private static void CreatePillButton(
        Transform parent,
        string name,
        string title,
        Vector2 anchoredPos,
        Vector2 size,
        Color bgColor,
        Color textColor,
        Action onClick
    )
    {
        var borderRect = CreateRect(
            name,
            parent,
            new Vector2(0f, 1f),
            new Vector2(0f, 1f),
            anchoredPos,
            size
        );
        var borderImage = borderRect.gameObject.AddComponent<Image>();
        borderImage.color = new Color(0.82f, 0.66f, 0.32f, 0.90f);

        var innerRect = CreateRect(
            "Bg",
            borderRect,
            Vector2.zero,
            Vector2.zero,
            new Vector2(2f, 2f),
            size - new Vector2(4f, 4f)
        );
        var bgImage = innerRect.gameObject.AddComponent<Image>();
        bgImage.color = bgColor;

        var button = borderRect.gameObject.AddComponent<Button>();
        button.targetGraphic = bgImage;
        button.onClick.AddListener(onClick);

        var labelRect = CreateRect("Label", innerRect, Vector2.zero, Vector2.zero, Vector2.zero, size - new Vector2(4f, 4f));
        var label = labelRect.gameObject.AddComponent<TextMeshProUGUI>();
        label.text = title;
        label.fontSize = 18f;
        label.color = textColor;
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;
    }

    private static void CycleNext()
    {
        switch (_nextPanel % 3)
        {
            case 0:
                LiveBuildPanel.ToggleFromUi();
                break;
            case 1:
                CollectionPanel.ToggleFromUi();
                break;
            default:
                HistoryPanel.ToggleFromUi();
                break;
        }

        BppStartupTrace.Write("touch-entry:panel " + _nextPanel % 3);
        _nextPanel++;
    }
}
