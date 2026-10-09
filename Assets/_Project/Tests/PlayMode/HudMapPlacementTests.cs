#if UNITY_EDITOR
using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The intel map sits above the squad roster on the left: the HUD hands the map its placement while enabled, the
    /// map keeps its old bottom-right default without one, and the placement follows the roster as the squad changes size.
    /// </summary>
    public class HudMapPlacementTests : InputTestFixture
    {
        InputActionAsset actions;
        AbilityRig rig;
        PointerBlocker blocker;
        HudSources sources;
        IntelMapView map;
        GameObject hudObject;
        TacticalHud hud;

        public override void Setup()
        {
            base.Setup();
            InputSystem.AddDevice<Keyboard>();
            InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
            rig = AbilityRig.Build(actions, withCursor: false);
            blocker = rig.World.Track(new GameObject("PointerBlocker")).AddComponent<PointerBlocker>();
            rig.Input.SetPointerBlocker(blocker);
            map = rig.World.Track(new GameObject("Map")).AddComponent<IntelMapView>();
            sources = new HudSources
            {
                activeCharacter = rig.Active, selection = rig.Selection, tacticalPause = rig.Pause, encounter = rig.Encounter,
                abilityTargeting = rig.Targeting, commandInput = rig.Input, controls = actions, camera = rig.ViewCamera,
                intelMap = map,
            };
        }

        public override void TearDown()
        {
            if (hudObject != null)
                UnityEngine.Object.DestroyImmediate(hudObject);
            foreach (var eventSystem in UnityEngine.Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None))
                UnityEngine.Object.DestroyImmediate(eventSystem.gameObject);
            rig.World.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator StartHud()
        {
            hudObject = new GameObject("hud");
            hudObject.SetActive(false);
            hud = hudObject.AddComponent<TacticalHud>();
            hud.Initialize(sources, blocker);
            hudObject.SetActive(true);
            yield return null;
            yield return null;
        }

        // Fills the squad part of the snapshot with `count` cards and applies it now (the next LateUpdate would overwrite it).
        void ShowCards(int count)
        {
            hud.Snapshot.Clear();
            for (var i = 0; i < count; i++)
                hud.Snapshot.Squad.Add(new HudSquadCard { Unit = rig.Selection.Roster[0].Unit, Name = "Unit" + i, Role = "Role", Health = 10, MaxHealth = 10 });
            hud.ApplySnapshot();
        }

        // A RectTransform's screen rectangle in IMGUI coordinates (y down): an overlay canvas lays its world corners out in pixels.
        static Rect GuiRect(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return Rect.MinMaxRect(corners[0].x, Screen.height - corners[1].y, corners[2].x, Screen.height - corners[0].y);
        }

        // ---- the map view on its own ----

        [Test]
        public void WithoutAPlacementSource_TheMapKeepsItsBottomRightDefault()
        {
            var panel = map.CurrentPanel(1920f, 1080f);
            Assert.That(panel, Is.EqualTo(new Rect(1920f - 260f - 10f, 1080f - 190f - 10f, 260f, 190f)));
        }

        [Test]
        public void APlacementSource_MovesTheMap_AndIsKeptOnScreen()
        {
            map.PanelSource = size => new Rect(24f, 300f, size.x, size.y);
            Assert.That(map.CurrentPanel(1920f, 1080f), Is.EqualTo(new Rect(24f, 300f, 260f, 190f)));

            map.PanelSource = size => new Rect(-50f, 2000f, size.x, size.y);
            var clamped = map.CurrentPanel(1920f, 1080f);
            Assert.That(clamped.x, Is.EqualTo(0f));
            Assert.That(clamped.yMax, Is.EqualTo(1080f), "pushed back up onto the screen");

            map.PanelSource = size => null;
            Assert.That(map.CurrentPanel(1920f, 1080f).x, Is.EqualTo(1920f - 260f - 10f), "null means the default");
        }

        // ---- the HUD's placement ----

        [UnityTest]
        public IEnumerator TheHud_InstallsItsPlacementWhileEnabled_AndWithdrawsOnlyItsOwn()
        {
            Assert.That(map.PanelSource, Is.Null);
            yield return StartHud();
            Assert.That(map.PanelSource, Is.Not.Null, "installed on enable");

            hudObject.SetActive(false);
            Assert.That(map.PanelSource, Is.Null, "withdrawn on disable: the map is back in its corner");

            Func<Vector2, Rect?> other = size => new Rect(1f, 2f, size.x, size.y);
            hudObject.SetActive(true);
            map.PanelSource = other;
            hudObject.SetActive(false);
            Assert.That(map.PanelSource, Is.SameAs(other), "someone else's placement is left alone");
        }

        [UnityTest]
        public IEnumerator TheMap_SitsAboveTheTopCard_LeftAlignedWithTheRoster([Values(1, 3, 6)] int cards)
        {
            yield return StartHud();
            ShowCards(cards);
            var scale = hud.Root.lossyScale.x;
            var footprint = GuiRect(hud.Squad.Footprint);

            var panel = map.CurrentPanel(Screen.width, Screen.height);
            Assert.That(panel.x, Is.EqualTo(footprint.x).Within(0.5f), "left edges line up");
            Assert.That(footprint.y - panel.yMax, Is.EqualTo(12f * scale).Within(0.5f), "a small gap above the top card");
            Assert.That(panel.width, Is.EqualTo(260f * scale).Within(0.5f), "scaled with the canvas like the rest of the HUD");
            Assert.That(panel.Overlaps(footprint), Is.False, "never over the cards");
        }

        [UnityTest]
        public IEnumerator TheMap_FollowsTheRoster_AsTheSquadShrinks()
        {
            yield return StartHud();
            ShowCards(3);
            var withThree = map.CurrentPanel(Screen.width, Screen.height);
            ShowCards(2);
            var withTwo = map.CurrentPanel(Screen.width, Screen.height);
            Assert.That(withTwo.y, Is.GreaterThan(withThree.y), "one card fewer: the map comes down by a card");
            Assert.That(withTwo.x, Is.EqualTo(withThree.x).Within(0.01f));
        }

        [UnityTest]
        public IEnumerator WithNoCards_TheMapRestsOnTheRostersBottomMargin()
        {
            yield return StartHud();
            ShowCards(0);
            var scale = hud.Root.lossyScale.x;
            var panel = map.CurrentPanel(Screen.width, Screen.height);
            Assert.That(Screen.height - panel.yMax, Is.EqualTo(HudTheme.Margin * scale).Within(0.5f), "same bottom margin as the squad column");
            Assert.That(panel.x, Is.EqualTo(HudTheme.Margin * scale).Within(0.5f));
        }
    }
}
#endif
