using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Blackglass
{
    /// <summary>
    /// The player's tactical HUD root. On enable it creates a screen-space overlay canvas, builds the panel hierarchy once
    /// and, if the scene has no EventSystem, a pointer-only one. Each LateUpdate (unscaled time, so it runs in tactical
    /// pause) the snapshot builder reads gameplay into the snapshot and every panel applies it, writing only what changed.
    /// Gameplay never references this class.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TacticalHud : MonoBehaviour
    {
        const int SortingOrder = 10;
        static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);

        [SerializeField] HudSources sources;
        [SerializeField] PointerBlocker pointerBlocker;   // the HUD installs its pointer test here (Task 9)

        readonly HudSnapshotBuilder builder = new HudSnapshotBuilder();
        readonly List<HudPanel> panels = new List<HudPanel>();
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

        internal void Initialize(HudSources hudSources, PointerBlocker blocker)
        {
            sources = hudSources;
            pointerBlocker = blocker;
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
            return root;
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
        }

        void OnDisable()
        {
            if (canvasObject != null)
                canvasObject.SetActive(false);
        }

        void LateUpdate()
        {
            builder.Build(sources, Snapshot, Time.unscaledTime);
            ApplySnapshot();
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
