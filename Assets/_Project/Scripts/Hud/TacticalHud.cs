using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Blackglass
{
    /// <summary>
    /// The player's tactical HUD root. On enable it creates a screen-space overlay canvas, builds the panel hierarchy once
    /// and, if the scene has no EventSystem, a pointer-only one. Each LateUpdate (unscaled time, so it runs in tactical
    /// pause) the snapshot builder reads gameplay into the snapshot and every panel applies it, writing only what changed.
    /// Clicks on the HUD's buttons become HudRequests (the same calls the existing input makes). While enabled it installs
    /// its pointer gate in the PointerBlocker and the hover target, so a press over a visible panel is not a world click.
    /// Gameplay never references this class.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TacticalHud : MonoBehaviour
    {
        const int SortingOrder = 10;
        static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);

        [SerializeField] HudSources sources;
        [SerializeField] PointerBlocker pointerBlocker;   // the HUD installs its pointer gate here while enabled
        // The queue modifier: held, a card click adds or removes the unit instead of selecting only it.
        [SerializeField] InputActionReference queueModifier;

        readonly HudSnapshotBuilder builder = new HudSnapshotBuilder();
        readonly List<HudPanel> panels = new List<HudPanel>();
        readonly HudPointerGate gate = new HudPointerGate();
        HudRequests requests;
        GameObject canvasObject;

        internal HudSnapshot Snapshot { get; } = new HudSnapshot();
        internal RectTransform Root { get; private set; }
        internal ObjectivesPanel Objectives { get; private set; }
        internal StatusPanel Status { get; private set; }
        internal SquadPanel Squad { get; private set; }
        internal OperativePanel Operative { get; private set; }
        internal PromptPanel Prompts { get; private set; }
        internal TargetPanel Target { get; private set; }
        internal WorldMarkLayer Marks { get; private set; }
        internal HudPointerGate Gate => gate;
        internal HudSnapshotBuilder Builder => builder;

        /// <summary>The requests the buttons send, over the current sources and queue modifier.</summary>
        internal HudRequests Requests => requests ?? (requests = new HudRequests(sources, queueModifier));

        internal void Initialize(HudSources hudSources, PointerBlocker blocker)
        {
            // While enabled the gate is installed: move it from the old blocker to the new one.
            if (isActiveAndEnabled && pointerBlocker != blocker)
            {
                if (pointerBlocker != null)
                    pointerBlocker.SetTest(null);
                if (blocker != null)
                    blocker.SetTest(gate.IsOver);
            }
            if (isActiveAndEnabled)
                WithdrawMapPlacement();
            sources = hudSources;
            pointerBlocker = blocker;
            requests = null;
            if (isActiveAndEnabled)
                InstallMapPlacement();
        }

        internal void SetQueueModifier(InputActionReference modifier)
        {
            queueModifier = modifier;
            requests = null;
        }

        /// <summary>Builds the whole hierarchy under `root`, once (tests pass their own rect to check layout).</summary>
        internal RectTransform BuildInto(RectTransform root)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));
            if (Root != null)
            {
                if (Root == root)
                    return Root;
                throw new InvalidOperationException($"The HUD is already built under {Root.name}.");
            }
            Root = root;

            Marks = Add(new WorldMarkLayer(root));   // first child: drawn beneath every panel
            Marks.CameraSource = ResolveCamera;
            Objectives = Add(new ObjectivesPanel(root));
            Status = Add(new StatusPanel(root));
            Squad = Add(new SquadPanel(root));
            Operative = Add(new OperativePanel(root));
            Prompts = Add(new PromptPanel(root));
            Target = Add(new TargetPanel(root));
            RegisterBlockingRects();
            WireButtons();
            return root;
        }

        // What blocks a world click: the panels that take space, while visible, and the pause and result banners while shown
        // (a click on TACTICAL PAUSE must not order a move behind it; neither banner follows the hover). Not the full-screen
        // roots (the world marks, the status root), and not the target panel: it is information only and it appears and hides with
        // the hover, which reads this gate, so blocking there would make it flicker over a hostile behind it and swallow the
        // click on that hostile. Nothing that follows the hover may be registered. The squad and prompt zones count only
        // where they draw something.
        void RegisterBlockingRects()
        {
            gate.Register(Objectives.Root);
            gate.Register(Status.RightBlock);
            gate.Register(Status.PauseBanner);
            gate.Register(Status.ResultBanner);
            gate.Register(Squad.Footprint);
            gate.Register(Operative.Root);
            gate.Register(Prompts.Background);
        }

        void WireButtons()
        {
            for (var i = 0; i < Squad.CardCapacity; i++)
                Squad.CardAt(i).Clicked += OnCardClicked;
            for (var i = 0; i < Operative.SlotCapacity; i++)
                Operative.SlotAt(i).Clicked += slot => Requests.ToggleAbility(slot);
            Operative.ClearClicked += unit => Requests.ClearOrders(unit);
            Status.FollowClicked += () => Requests.ToggleFollow();
            Status.PauseClicked += () => Requests.TogglePause();
        }

        void OnCardClicked(CommandableUnit unit, bool doubleClick)
        {
            if (doubleClick)
                Requests.TakeControl(unit);
            else
                Requests.SelectUnit(unit, Requests.AdditiveHeld);
        }

        internal void ApplySnapshot()
        {
            for (var i = 0; i < panels.Count; i++)
                panels[i].Apply(Snapshot);
        }

        void OnEnable()
        {
            if (Root == null)
                BuildCanvas();
            else if (canvasObject != null)
                canvasObject.SetActive(true);
            EnsureEventSystem();
            if (pointerBlocker != null)
                pointerBlocker.SetTest(gate.IsOver);
            builder.PointerOverHud = gate.IsOver;
            InstallMapPlacement();
        }

        void OnDisable()
        {
            WithdrawMapPlacement();
            if (pointerBlocker != null)
                pointerBlocker.SetTest(null);
            builder.PointerOverHud = null;
            if (canvasObject != null)
                canvasObject.SetActive(false);
        }

        void LateUpdate()
        {
            builder.Build(sources, Snapshot, Time.unscaledTime);
            ApplySnapshot();
        }

        // Gap between the squad roster and the intel map above it, in canvas units.
        const float MapGap = 12f;
        readonly Vector3[] mapCorners = new Vector3[4];
        Func<Vector2, Rect?> mapPlacement;   // created once; compared on withdraw so only our own placement is removed

        void InstallMapPlacement()
        {
            if (sources != null && sources.intelMap != null)
                sources.intelMap.PanelSource = mapPlacement ??= MapPanelAbove;
        }

        void WithdrawMapPlacement()
        {
            if (sources != null && sources.intelMap != null && sources.intelMap.PanelSource == mapPlacement)
                sources.intelMap.PanelSource = null;
        }

        /// <summary>
        /// Where the intel map goes: left-aligned with the squad cards, its bottom edge MapGap above the top card (above the
        /// zone's bottom margin when the squad has no cards), in IMGUI coordinates (y down) and scaled with the canvas, so it
        /// follows the roster as the squad shrinks or grows.
        /// </summary>
        internal Rect? MapPanelAbove(Vector2 naturalSize)
        {
            if (Squad == null)
                return null;
            var hasCards = Squad.Footprint.gameObject.activeInHierarchy;
            var anchor = hasCards ? Squad.Footprint : Squad.Root;
            anchor.GetWorldCorners(mapCorners);   // an overlay canvas lays its corners out in screen pixels, y up
            var scale = Root != null ? Root.lossyScale.x : 1f;
            var size = naturalSize * scale;
            var restingY = hasCards ? mapCorners[1].y + MapGap * scale : mapCorners[0].y;
            return new Rect(mapCorners[0].x, Screen.height - restingY - size.y, size.x, size.y);
        }

        // The world marks' camera: the wired one, else the scene's main camera.
        Camera ResolveCamera() => sources != null && sources.camera != null ? sources.camera : Camera.main;

        T Add<T>(T panel) where T : HudPanel
        {
            panels.Add(panel);
            return panel;
        }

        void BuildCanvas()
        {
            canvasObject = new GameObject("HudCanvas", typeof(RectTransform));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            canvasObject.AddComponent<GraphicRaycaster>();
            BuildInto((RectTransform)canvasObject.transform);
        }

        /// <summary>
        /// A pointer-only EventSystem under the HUD, only when the scene has none. A scene's own EventSystem is reused, but
        /// a plain Input System UI module on it loses its navigation actions, so a pad never moves UI focus there either.
        /// </summary>
        void EnsureEventSystem()
        {
            var existing = EventSystem.current != null ? EventSystem.current : FindFirstObjectByType<EventSystem>();
            if (existing != null)
            {
                if (existing.TryGetComponent<InputSystemUIInputModule>(out var module) && !(module is PointerOnlyInputModule))
                    PointerOnlyInputModule.StripNavigation(module);
                return;
            }
            var eventSystem = new GameObject("EventSystem");
            eventSystem.transform.SetParent(transform, false);
            eventSystem.AddComponent<EventSystem>();
            eventSystem.AddComponent<PointerOnlyInputModule>();
        }
    }
}
