#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    /// <summary>Calls back after every other LateUpdate of the frame (the HUD builds and applies in its LateUpdate).</summary>
    [DefaultExecutionOrder(32000)]
    internal sealed class LateProbe : MonoBehaviour
    {
        public Action Late;
        void LateUpdate() => Late?.Invoke();
    }

    /// <summary>
    /// The tactical HUD as wired into the shipped scenes (Blackglass/HUD/Wire Scenes): one HUD, one pointer blocker, a
    /// hidden developer overlay, every source the scene has, operative cards, objectives and extraction from the runtime,
    /// Tab and pause reflected at once, a card click that is not a world order, F1, the pointer-only EventSystem, and the
    /// Prototype scene (no director, roster or intelligence) with cards from the selection roster.
    /// </summary>
    public class HudSceneTests : InputTestFixture
    {
        Keyboard keyboard;
        Mouse mouse;
        InputActionAsset actions;
        HudSceneRig rig;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
            rig = new HudSceneRig();
        }

        public override void TearDown()
        {
            HudSceneRig.TearDown();
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator Tap(UnityEngine.InputSystem.Controls.ButtonControl key)
        {
            Press(key);
            yield return null;
            Release(key);
            yield return null;
        }

        IEnumerator LeftClick(Vector2 screen)
        {
            Set(mouse.position, screen);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
            yield return null;
        }

        static int Count<T>() where T : Object => Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;

        // Each HudSources field holds the scene's one instance of its type when the scene has one, and nothing otherwise.
        static void AssertSourcesWired(TacticalHud hud, bool missionScene)
        {
            var serialized = new SerializedObject(hud);
            void Check<T>(string field) where T : Object
            {
                var found = Object.FindFirstObjectByType<T>();
                var value = serialized.FindProperty("sources." + field).objectReferenceValue;
                if (found != null)
                    Assert.That(value, Is.SameAs(found), $"HudSources.{field}");
                else
                    Assert.That(value, Is.Null, $"HudSources.{field}: the scene has no {typeof(T).Name}");
            }
            Check<ActiveCharacter>("activeCharacter");
            Check<UnitSelection>("selection");
            Check<TacticalPause>("tacticalPause");
            Check<Encounter>("encounter");
            Check<MissionDirector>("director");
            Check<SquadRoster>("roster");
            Check<IntelligenceService>("intelligence");
            Check<IntelMapView>("intelMap");
            Check<AbilityTargeting>("abilityTargeting");
            Check<TacticalCursor>("cursor");
            Check<PlayerCommandInput>("commandInput");
            Check<ActiveInputDevice>("inputDevice");
            Assert.That(serialized.FindProperty("sources.controls").objectReferenceValue, Is.SameAs(TestControls.Load()), "HudSources.controls");
            Assert.That(serialized.FindProperty("sources.camera").objectReferenceValue, Is.SameAs(Camera.main), "HudSources.camera");
            Assert.That(serialized.FindProperty("pointerBlocker").objectReferenceValue, Is.SameAs(Object.FindFirstObjectByType<PointerBlocker>()));
            var modifier = serialized.FindProperty("queueModifier").objectReferenceValue as InputActionReference;
            Assert.That(modifier, Is.Not.Null, "the queue modifier");
            Assert.That(modifier.action.actionMap.name + "/" + modifier.action.name, Is.EqualTo("Commands/QueueModifier"));

            var input = Object.FindFirstObjectByType<PlayerCommandInput>();
            Assert.That(new SerializedObject(input).FindProperty("pointerBlocker").objectReferenceValue,
                Is.SameAs(Object.FindFirstObjectByType<PointerBlocker>()), "PlayerCommandInput asks the HUD's blocker");
            if (missionScene)
            {
                Assert.That(Object.FindFirstObjectByType<MissionDirector>(), Is.Not.Null);
                Assert.That(Object.FindFirstObjectByType<SquadRoster>(), Is.Not.Null);
            }
        }

        static void AssertOneHudAndAHiddenOverlay()
        {
            Assert.That(Count<TacticalHud>(), Is.EqualTo(1), "one TacticalHud");
            Assert.That(Count<PointerBlocker>(), Is.EqualTo(1), "one PointerBlocker");
            Assert.That(Count<DeveloperOverlay>(), Is.EqualTo(1), "one DeveloperOverlay");
            Assert.That(Count<DeveloperOverlayInput>(), Is.EqualTo(1), "one DeveloperOverlayInput");
            var overlay = Object.FindFirstObjectByType<DeveloperOverlay>();
            Assert.That(overlay.IsVisible, Is.False, "the developer overlay starts hidden");
            var input = new SerializedObject(Object.FindFirstObjectByType<DeveloperOverlayInput>());
            Assert.That(input.FindProperty("overlay").objectReferenceValue, Is.SameAs(overlay));
            var toggle = input.FindProperty("toggleAction").objectReferenceValue as InputActionReference;
            Assert.That(toggle, Is.Not.Null);
            Assert.That(toggle.action.actionMap.name + "/" + toggle.action.name, Is.EqualTo("Developer/ToggleDebugOverlay"));
        }

        // The one EventSystem is the HUD's pointer-only one: no navigation actions, so a pad never moves UI focus.
        static void AssertPointerOnlyEventSystem()
        {
            var systems = Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None);
            Assert.That(systems, Has.Length.EqualTo(1), "one EventSystem");
            var module = systems[0].GetComponent<BaseInputModule>();
            Assert.That(module, Is.InstanceOf<PointerOnlyInputModule>());
            var pointerOnly = (PointerOnlyInputModule)module;
            Assert.That(pointerOnly.move, Is.Null, "no navigate action");
            Assert.That(pointerOnly.submit, Is.Null, "no submit action");
            Assert.That(pointerOnly.cancel, Is.Null, "no cancel action");
            Assert.That(pointerOnly.point, Is.Not.Null, "the pointer still works");
        }

        static void AssertGatedViews(bool drawing, bool missionScene)
        {
            Assert.That(Object.FindFirstObjectByType<PrototypeHud>().IsDrawing, Is.EqualTo(drawing), "PrototypeHud");
            Assert.That(Object.FindFirstObjectByType<AbilityBarView>().IsDrawing, Is.EqualTo(drawing), "AbilityBarView");
            Assert.That(Object.FindFirstObjectByType<TacticalCursorView>().IsDrawing, Is.EqualTo(drawing), "TacticalCursorView");
            if (!missionScene)
                return;
            Assert.That(Object.FindFirstObjectByType<MissionHud>().IsDrawing, Is.EqualTo(drawing), "MissionHud");
            Assert.That(Object.FindFirstObjectByType<MissionDebugView>().IsDrawing, Is.EqualTo(drawing), "MissionDebugView");
        }

        // ---- ProceduralMission ----

        [UnityTest]
        public IEnumerator ProceduralMission_HasOneHud_AHiddenOverlay_EverySource_AndAPointerOnlyEventSystem()
        {
            yield return rig.LoadMission();

            AssertOneHudAndAHiddenOverlay();
            AssertSourcesWired(rig.Hud, missionScene: true);
            Assert.That(rig.Service, Is.Not.Null, "the scene ships the intelligence service");
            Assert.That(new SerializedObject(rig.Hud).FindProperty("sources.intelligence").objectReferenceValue, Is.SameAs(rig.Service),
                "without the service the HUD would fail open to everything known");
            Assert.That(rig.Hud.isActiveAndEnabled, Is.True);
            Assert.That(rig.Blocker.HasTest, Is.True, "the HUD installed its pointer gate");
            AssertPointerOnlyEventSystem();
            AssertGatedViews(false, missionScene: true);
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator ProceduralMission_ShowsAnOperativeCardPerSquadMember_TheKnownObjectives_AndTheExtraction()
        {
            yield return rig.LoadMission();

            var friendlies = rig.Director.Friendlies;
            Assert.That(friendlies.Count, Is.EqualTo(3), "the scene's squad");
            Assert.That(rig.VisibleCards(), Is.EqualTo(friendlies.Count), "one card per squad member");
            for (var i = 0; i < friendlies.Count; i++)
            {
                var identity = friendlies[i].GetComponent<UnitIdentity>();
                Assert.That(identity.OperativeId, Is.Not.Empty, $"{friendlies[i].name} is a persistent operative");
                var card = rig.Hud.Squad.CardAt(i);
                Assert.That(card.Card.Unit, Is.SameAs(friendlies[i]), $"card {i} in squad order");
                Assert.That(card.NameLabel.text, Is.EqualTo(identity.DisplayName), $"card {i} shows the operative's name");
            }

            Assert.That(rig.Hud.Objectives.Root.gameObject.activeInHierarchy, Is.True);
            var expected = rig.ExpectedObjectiveRows();
            Assert.That(expected, Is.Not.Empty, "precondition: the blind mission lists something");
            Assert.That(rig.ShownObjectiveRows(), Is.EqualTo(expected));
            foreach (var objective in rig.Director.Runtime.Objectives.Where(o => !o.IsKnown))
                Assert.That(rig.AllTexts().Any(t => t.Contains(objective.Title)), Is.False, $"the unknown {objective.Id} is not named");

            Assert.That(rig.Hud.Status.ExtractionChip.gameObject.activeInHierarchy, Is.True);
            Assert.That(rig.Hud.Status.ExtractionLabel.text, Is.EqualTo(rig.ExpectedExtractionLabel()));
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator ProceduralMission_TabMovesTheControlMarkWithinAFrame_AndPauseShowsTheBanner()
        {
            yield return rig.LoadMission();
            var before = rig.Active.Unit;
            Assert.That(before, Is.Not.Null);
            Assert.That(rig.CardOf(before).ControlLabel.gameObject.activeSelf, Is.True, "precondition: the controlled card is marked");

            // The press reaches ActiveCharacter in a frame's input update; the HUD's LateUpdate of that same frame must show
            // it. A probe that runs after every LateUpdate records, per frame, the unit in control and the HUD's controlled card.
            var probe = new GameObject("LateProbe").AddComponent<LateProbe>();
            var seen = new List<(int frame, CommandableUnit active, CommandableUnit card)>();
            probe.Late = () => seen.Add((Time.frameCount, rig.Active.Unit,
                rig.Hud.Snapshot.Squad.Where(c => c.IsControlled).Select(c => c.Unit).FirstOrDefault()));
            Press(keyboard.tabKey);
            yield return null;
            yield return null;
            Object.Destroy(probe.gameObject);
            var after = rig.Active.Unit;
            Assert.That(after, Is.Not.SameAs(before), "Tab passed control");
            var changed = seen.FirstOrDefault(r => r.active == after);
            Assert.That(changed.active, Is.SameAs(after), "the probe saw the frame in which control changed");
            Assert.That(changed.card, Is.SameAs(after), $"the HUD showed the new controlled operative in frame {changed.frame}, the frame control changed");
            Assert.That(rig.CardOf(after).ControlLabel.gameObject.activeSelf, Is.True, "the new controlled card is marked");
            Assert.That(rig.CardOf(before).ControlLabel.gameObject.activeSelf, Is.False, "the old one is not");
            Assert.That(rig.Hud.Operative.NameLabel.text, Is.EqualTo(after.GetComponent<UnitIdentity>().DisplayName));
            Release(keyboard.tabKey);
            yield return null;

            Assert.That(rig.Hud.Status.PauseBanner.gameObject.activeSelf, Is.False);
            yield return Tap(keyboard.spaceKey);
            Assert.That(rig.Pause.IsPaused, Is.True);
            Assert.That(rig.Hud.Status.PauseBanner.gameObject.activeInHierarchy, Is.True, "the pause banner shows");
            Assert.That(rig.Hud.Status.PauseLabel.text, Does.StartWith("TACTICAL PAUSE"));
            yield return Tap(keyboard.spaceKey);
            Assert.That(rig.Pause.IsPaused, Is.False);
            Assert.That(rig.Hud.Status.PauseBanner.gameObject.activeSelf, Is.False, "and hides on resume");
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator ProceduralMission_ALeftClickOnASquadCard_IsNotAWorldOrder_AndOneOffTheHudIs()
        {
            yield return rig.LoadMission(HudSceneRig.Seed);
            var controlled = rig.Active.Unit;
            controlled.Issue(new StopCommand());
            // Paused, so an order stays current where the test can see it (a paused click orders the selection).
            rig.Pause.Pause();
            rig.Selection.Select(controlled.GetComponent<SelectableUnit>());
            yield return null;
            Assert.That(controlled.CurrentCommand, Is.Null, "precondition: the controlled operative is idle");

            // Pan the camera so that open floor beside the operative lies under its card: without the HUD, that click
            // would be a move order.
            var mover = controlled.GetComponent<UnitMover>();
            var cameraRig = Object.FindFirstObjectByType<TacticalCameraController>();
            var home = cameraRig.transform.position;
            var onCard = HudSceneRig.Centre(rig.CardOf(controlled).Root);
            var panned = false;
            for (var distance = 1.5f; distance <= 4.5f && !panned; distance += 1f)
            {
                for (var angle = 0; angle < 360 && !panned; angle += 45)
                {
                    var candidate = controlled.transform.position - Vector3.up + Quaternion.Euler(0f, angle, 0f) * Vector3.forward * distance;
                    if (!NavMesh.SamplePosition(candidate, out var hit, 0.5f, NavMesh.AllAreas) || !mover.CanReach(hit.position))
                        continue;
                    cameraRig.FocusOn(home);
                    var ray = rig.ViewCamera.ScreenPointToRay(onCard);
                    if (!new Plane(Vector3.up, hit.position).Raycast(ray, out var enter))
                        continue;
                    cameraRig.FocusOn(home + (hit.position - ray.GetPoint(enter)));
                    var under = rig.Input.ResolveAt(onCard);
                    panned = under.Kind == PointerTargetKind.Ground
                        && NavMesh.SamplePosition(under.Point, out var walkable, 0.5f, NavMesh.AllAreas) && mover.CanReach(walkable.position);
                }
            }
            Assert.That(panned, Is.True, "precondition: walkable floor under the card, so without the HUD the click would be a move order");
            yield return null;
            Assert.That(rig.Input.ResolveAt(onCard).Kind, Is.EqualTo(PointerTargetKind.Ground), "precondition: the camera stayed there");
            Assert.That(rig.Blocker.IsBlocking(onCard), Is.True, "the card blocks world clicks");

            yield return LeftClick(onCard);
            Assert.That(controlled.CurrentCommand, Is.Null, "no world order through the card");
            Assert.That(rig.Selection.Selected.Select(s => s.Unit), Is.EqualTo(new[] { controlled }), "the click selected the card's operative");
            cameraRig.FocusOn(home);
            yield return null;

            // Open floor near the operative (its pivot stands 1 m above the floor) that the camera sees, the unit can walk
            // to and the HUD leaves clear: that click is still an order.
            var ground = Vector2.zero;
            var found = false;
            for (var distance = 1.5f; distance <= 4.5f && !found; distance += 1f)
            {
                for (var angle = 0; angle < 360 && !found; angle += 45)
                {
                    var candidate = controlled.transform.position - Vector3.up + Quaternion.Euler(0f, angle, 0f) * Vector3.forward * distance;
                    if (!NavMesh.SamplePosition(candidate, out var hit, 0.5f, NavMesh.AllAreas) || !mover.CanReach(hit.position))
                        continue;
                    var screen = (Vector2)rig.ViewCamera.WorldToScreenPoint(hit.position);
                    var pointed = rig.Input.ResolveAt(screen);
                    if (rig.Blocker.IsBlocking(screen) || pointed.Kind != PointerTargetKind.Ground || Vector3.Distance(pointed.Point, hit.position) > 0.3f)
                        continue;
                    ground = screen;
                    found = true;
                }
            }
            Assert.That(found, Is.True, "precondition: open ground beside the operative, off the HUD");
            yield return LeftClick(ground);
            Assert.That(controlled.CurrentCommand, Is.TypeOf<MoveCommand>(), "a click off the HUD is still a world order");
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator ProceduralMission_F1_TogglesTheOverlay_AndTheGatedDebugViews()
        {
            yield return rig.LoadMission();
            AssertGatedViews(false, missionScene: true);
            yield return Tap(keyboard.f1Key);
            Assert.That(rig.Overlay.IsVisible, Is.True);
            AssertGatedViews(true, missionScene: true);
            yield return Tap(keyboard.f1Key);
            Assert.That(rig.Overlay.IsVisible, Is.False);
            AssertGatedViews(false, missionScene: true);
            LogAssert.NoUnexpectedReceived();
        }

        // ---- Prototype ----

        [UnityTest]
        public IEnumerator Prototype_BuildsTheHud_WithCardsFromTheSelectionRoster_AndNoMissionSources()
        {
            yield return rig.LoadPrototype();

            AssertOneHudAndAHiddenOverlay();
            AssertSourcesWired(rig.Hud, missionScene: false);
            Assert.That(Object.FindFirstObjectByType<MissionDirector>(), Is.Null, "precondition: no director");
            Assert.That(Object.FindFirstObjectByType<SquadRoster>(), Is.Null, "precondition: no roster");
            Assert.That(Object.FindFirstObjectByType<IntelligenceService>(), Is.Null, "precondition: no intelligence");
            AssertPointerOnlyEventSystem();

            var roster = rig.Selection.Roster.Select(s => s.Unit).ToArray();
            Assert.That(roster, Has.Length.EqualTo(3));
            Assert.That(rig.VisibleCards(), Is.EqualTo(roster.Length), "one card per roster unit");
            for (var i = 0; i < roster.Length; i++)
            {
                var card = rig.Hud.Squad.CardAt(i);
                Assert.That(card.Card.Unit, Is.SameAs(roster[i]));
                Assert.That(card.NameLabel.text, Is.EqualTo(rig.Hud.Snapshot.Squad[i].Name));
                Assert.That(card.NameLabel.text, Is.Not.Empty);
            }
            Assert.That(rig.Hud.Objectives.Root.gameObject.activeSelf, Is.False, "no mission, no objectives panel");
            Assert.That(rig.CardOf(rig.Active.Unit).ControlLabel.gameObject.activeSelf, Is.True);

            AssertGatedViews(false, missionScene: false);
            yield return Tap(keyboard.f1Key);
            AssertGatedViews(true, missionScene: false);
            yield return Tap(keyboard.spaceKey);
            Assert.That(rig.Hud.Status.PauseBanner.gameObject.activeInHierarchy, Is.True);
            yield return Tap(keyboard.spaceKey);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
#endif
