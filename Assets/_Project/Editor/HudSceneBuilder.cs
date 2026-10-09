#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Wires the tactical HUD into the ProceduralMission and Prototype scenes: a root TacticalHud (with the scene's
    /// PointerBlocker) whose HudSources point at the scene's systems (the mission director, the squad roster and the
    /// intelligence service only where the scene has them), the developer overlay and its F1 input, the pointer blocker on
    /// PlayerCommandInput and the overlay flag on the four IMGUI debug views. A scene's own EventSystem with a plain Input
    /// System UI module is moved onto the pointer-only module; without one the HUD creates its own at runtime. Idempotent:
    /// existing objects are found and refilled, never duplicated.
    /// </summary>
    public static class HudSceneBuilder
    {
        const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";
        static readonly string[] ScenePaths =
        {
            "Assets/_Project/Scenes/ProceduralMission.unity",
            "Assets/_Project/Scenes/Prototype.unity",
        };

        [MenuItem("Blackglass/HUD/Wire Scenes")]
        public static void WireMenu() => Wire();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void WireFromCommandLine() => Wire();

        /// <summary>
        /// Wires both scenes. In the Editor, unsaved changes in any open scene are offered for saving first (Cancel stops
        /// here, nothing is touched), and the scenes that were open are reopened afterwards.
        /// </summary>
        public static void Wire()
        {
            SceneSetup[] previous = null;
            if (!Application.isBatchMode)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    return;
                previous = EditorSceneManager.GetSceneManagerSetup();
            }
            try
            {
                foreach (var path in ScenePaths)
                    WireScene(path);
            }
            finally
            {
                // An untitled scene has no path to reopen; then the last wired scene stays open.
                if (previous != null && previous.Length > 0 && previous.All(setup => !string.IsNullOrEmpty(setup.path)))
                    EditorSceneManager.RestoreSceneManagerSetup(previous);
            }
        }

        static void WireScene(string path)
        {
            // Open the scene first: opening it in Single mode unloads unreferenced assets (see OperativeDataBuilder).
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            var controls = AssetDatabase.LoadAssetAtPath<InputActionAsset>(ControlsPath);
            if (controls == null)
                throw new InvalidOperationException($"The input actions are missing at {ControlsPath}.");

            var hud = Find<TacticalHud>();
            if (hud == null)
                hud = new GameObject("TacticalHud").AddComponent<TacticalHud>();
            var blocker = Find<PointerBlocker>();
            if (blocker == null)
                blocker = hud.gameObject.AddComponent<PointerBlocker>();

            var overlay = Find<DeveloperOverlay>();
            if (overlay == null)
                overlay = new GameObject("DeveloperOverlay").AddComponent<DeveloperOverlay>();
            var overlayInput = Find<DeveloperOverlayInput>();
            if (overlayInput == null)
                overlayInput = overlay.gameObject.AddComponent<DeveloperOverlayInput>();
            Set(overlayInput, "overlay", overlay);
            Set(overlayInput, "toggleAction", ActionReference("Developer", "ToggleDebugOverlay"));

            var camera = Camera.main != null ? Camera.main : Find<Camera>();
            var serialized = new SerializedObject(hud);
            Put(serialized, "sources.activeCharacter", Find<ActiveCharacter>());
            Put(serialized, "sources.selection", Find<UnitSelection>());
            Put(serialized, "sources.tacticalPause", Find<TacticalPause>());
            Put(serialized, "sources.encounter", Find<Encounter>());
            Put(serialized, "sources.director", Find<MissionDirector>());       // null where the scene has none
            Put(serialized, "sources.roster", Find<SquadRoster>());
            Put(serialized, "sources.intelligence", Find<IntelligenceService>());
            Put(serialized, "sources.abilityTargeting", Find<AbilityTargeting>());
            Put(serialized, "sources.cursor", Find<TacticalCursor>());
            Put(serialized, "sources.commandInput", Find<PlayerCommandInput>());
            Put(serialized, "sources.inputDevice", Find<ActiveInputDevice>());
            Put(serialized, "sources.controls", controls);
            Put(serialized, "sources.camera", camera);
            Put(serialized, "sources.intelMap", Find<IntelMapView>());
            Put(serialized, "pointerBlocker", blocker);
            Put(serialized, "queueModifier", ActionReference("Commands", "QueueModifier"));
            serialized.ApplyModifiedPropertiesWithoutUndo();

            var commandInput = Find<PlayerCommandInput>();
            if (commandInput != null)
                Set(commandInput, "pointerBlocker", blocker);
            else
                Debug.LogWarning($"HudSceneBuilder: no PlayerCommandInput in {path}; the pointer blocker is not consulted.");

            GateWithOverlay<PrototypeHud>(overlay);
            GateWithOverlay<AbilityBarView>(overlay);
            GateWithOverlay<MissionHud>(overlay);
            GateWithOverlay<MissionDebugView>(overlay);

            MakeEventSystemPointerOnly();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        static void GateWithOverlay<T>(DeveloperOverlay overlay) where T : Component
        {
            var view = Find<T>();
            if (view != null)
                Set(view, "overlay", overlay);
        }

        // A scene EventSystem must not navigate: a plain Input System UI module (or any other module) is replaced by the
        // pointer-only one. Neither scene has an EventSystem today, so normally this does nothing.
        static void MakeEventSystemPointerOnly()
        {
            var eventSystem = Find<EventSystem>();
            if (eventSystem == null)
                return;
            foreach (var module in eventSystem.GetComponents<BaseInputModule>())
            {
                if (!(module is PointerOnlyInputModule))
                    UnityEngine.Object.DestroyImmediate(module);
            }
            if (eventSystem.GetComponent<PointerOnlyInputModule>() == null)
                eventSystem.gameObject.AddComponent<PointerOnlyInputModule>();
            var pointerOnly = new SerializedObject(eventSystem.GetComponent<PointerOnlyInputModule>());
            foreach (var navigation in new[] { "m_MoveAction", "m_SubmitAction", "m_CancelAction" })
            {
                var property = pointerOnly.FindProperty(navigation);
                if (property != null)
                    property.objectReferenceValue = null;
            }
            pointerOnly.ApplyModifiedPropertiesWithoutUndo();
        }

        static T Find<T>() where T : UnityEngine.Object => UnityEngine.Object.FindFirstObjectByType<T>();

        static void Put(SerializedObject serialized, string path, UnityEngine.Object value)
        {
            var property = serialized.FindProperty(path);
            if (property == null)
                throw new InvalidOperationException($"{serialized.targetObject.GetType().Name} has no serialized field '{path}'.");
            property.objectReferenceValue = value;
        }

        static void Set(UnityEngine.Object target, string path, UnityEngine.Object value)
        {
            var serialized = new SerializedObject(target);
            Put(serialized, path, value);
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        static InputActionReference ActionReference(string map, string action)
        {
            var reference = AssetDatabase.LoadAllAssetsAtPath(ControlsPath).OfType<InputActionReference>()
                .FirstOrDefault(r => r.action != null && r.action.name == action && r.action.actionMap != null && r.action.actionMap.name == map);
            if (reference == null)
                throw new InvalidOperationException($"No InputActionReference for '{map}/{action}' in {ControlsPath}.");
            return reference;
        }
    }
}
#endif
