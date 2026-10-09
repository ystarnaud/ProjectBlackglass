#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Blackglass.Tests
{
    /// <summary>
    /// The code-built tactical HUD: its panel hierarchy, the objectives and status panels driven straight from a snapshot,
    /// change-only text writes, layout at 16:9, 16:10 and 21:9, and the pointer-only EventSystem.
    /// </summary>
    public class TacticalHudBuildTests
    {
        static readonly string[] PanelNames = { "Objectives", "Status", "Squad", "Operative", "Prompts", "Target", "WorldMarks" };
        static readonly string[] LaterPanels = { "Operative", "Prompts", "Target", "WorldMarks" };

        readonly List<Object> created = new List<Object>();
        TacticalHud hud;
        RectTransform root;

        [TearDown]
        public void TearDown()
        {
            foreach (var item in created)
            {
                if (item != null)
                    Object.DestroyImmediate(item);
            }
            created.Clear();
        }

        /// <summary>An inactive HUD (no canvas, no LateUpdate) built under a plain rect of the given size.</summary>
        void Build(float width = 1920f, float height = 1080f)
        {
            var hudObject = Track(new GameObject("hud"));
            hudObject.SetActive(false);
            hud = hudObject.AddComponent<TacticalHud>();
            hud.Initialize(new HudSources(), null);
            root = HudLayout.CreateRoot(width, height);
            Track(root.gameObject);
            hud.BuildInto(root);
        }

        GameObject Track(GameObject go)
        {
            created.Add(go);
            return go;
        }

        RectTransform Panel(string name) => (RectTransform)root.Find(name);

        [Test]
        public void Build_CreatesTheExpectedPanels()
        {
            Build();

            Assert.That(HudFactory.Font, Is.Not.Null, "the built-in font loads");
            foreach (var name in PanelNames)
                Assert.That(Panel(name), Is.Not.Null, $"panel {name}");
            Assert.That(hud.Objectives.Root, Is.SameAs(Panel("Objectives")));
            Assert.That(hud.Status.Root, Is.SameAs(Panel("Status")));
            Assert.That(hud.Objectives.Header, Is.Not.Null);
            Assert.That(hud.Objectives.RowCapacity, Is.EqualTo(8));
            Assert.That(hud.Status.PauseLabel, Is.Not.Null);
            Assert.That(hud.Status.ResultLabel, Is.Not.Null);
            Assert.That(hud.Status.ExtractionLabel, Is.Not.Null);
            Assert.That(hud.Status.FollowLabel, Is.Not.Null);
            Assert.That(hud.Squad.Root, Is.SameAs(Panel("Squad")));
            foreach (var name in LaterPanels)
            {
                Assert.That(Panel(name).childCount, Is.Zero, $"{name} is an empty rect until its task fills it");
                Assert.That(Panel(name).GetComponents<Graphic>(), Is.Empty, $"{name} draws nothing yet");
            }
            Assert.That(Panel("WorldMarks").GetSiblingIndex(), Is.Zero, "world marks draw beneath the panels");
        }

        [Test]
        public void Objectives_ShowRowsWithKindMarkers_AndHideWithoutAMission()
        {
            Build();
            var s = hud.Snapshot;
            s.HasMission = true;
            s.PhaseText = "in progress";
            AddObjective(s, HudObjectiveKind.Active, "Reach the relay");
            AddObjective(s, HudObjectiveKind.Completed, "Download the logs");
            AddObjective(s, HudObjectiveKind.Failed, "Protect the courier");
            AddObjective(s, HudObjectiveKind.Locked, "Extract the squad");
            AddObjective(s, HudObjectiveKind.Unknown, "Something is stored here");
            hud.ApplySnapshot();

            var panel = hud.Objectives;
            Assert.That(panel.Root.gameObject.activeSelf, Is.True);
            Assert.That(panel.Header.text, Is.EqualTo("MISSION  in progress"));
            string[] markers = { "[ ]", "[x]", "[!]", "[-]", "[?]" };
            for (var i = 0; i < markers.Length; i++)
            {
                Assert.That(panel.Row(i).activeSelf, Is.True, $"row {i}");
                Assert.That(panel.RowMarker(i).text, Is.EqualTo(markers[i]), $"marker {i}");
                Assert.That(panel.RowText(i).text, Is.EqualTo(s.Objectives[i].Text), $"text {i}");
            }
            for (var i = markers.Length; i < panel.RowCapacity; i++)
                Assert.That(panel.Row(i).activeSelf, Is.False, $"unused row {i}");

            Assert.That(panel.RowText(0).color, Is.EqualTo(HudTheme.Text));
            Assert.That(panel.RowMarker(1).color, Is.EqualTo(HudTheme.Good), "completed marker");
            Assert.That(panel.RowText(1).color, Is.EqualTo(HudTheme.TextDim), "completed text is dimmed");
            Assert.That(panel.RowMarker(2).color, Is.EqualTo(HudTheme.Bad), "failed marker");
            Assert.That(panel.RowText(3).color, Is.EqualTo(HudTheme.TextDim), "locked text is dimmed");
            Assert.That(panel.RowMarker(4).color, Is.EqualTo(HudTheme.Warn), "unknown marker");

            s.Objectives.Clear();
            for (var i = 0; i < 10; i++)
                AddObjective(s, HudObjectiveKind.Active, "Row " + i);
            Assert.DoesNotThrow(() => hud.ApplySnapshot(), "more rows than the pool holds are dropped");
            Assert.That(panel.Row(7).activeSelf, Is.True);
            Assert.That(panel.RowText(7).text, Is.EqualTo("Row 7"));

            s.HasMission = false;
            hud.ApplySnapshot();
            Assert.That(panel.Root.gameObject.activeSelf, Is.False, "no mission hides the panel");
        }

        [Test]
        public void PauseBanner_ShowsWhilePausedAndNamesTheResumePrompt()
        {
            Build();
            var s = hud.Snapshot;
            s.IsPaused = true;
            s.ResumePrompt = "Space";
            hud.ApplySnapshot();

            var status = hud.Status;
            Assert.That(status.PauseBanner.gameObject.activeSelf, Is.True);
            Assert.That(status.PauseLabel.text, Is.EqualTo(HudText.Pause("Space")));
            Assert.That(status.PauseLabel.color, Is.EqualTo(HudTheme.Warn));
            Assert.That(status.PauseLabel.fontSize, Is.EqualTo(HudTheme.FontLarge));
            Assert.That(status.PauseGlyph.Length, Is.EqualTo(2), "a double-bar glyph, so pause is not colour-only");
            foreach (var bar in status.PauseGlyph)
                Assert.That(bar.gameObject.activeInHierarchy, Is.True);

            s.ResumePrompt = "Options";
            hud.ApplySnapshot();
            Assert.That(status.PauseLabel.text, Is.EqualTo(HudText.Pause("Options")));

            s.IsPaused = false;
            hud.ApplySnapshot();
            Assert.That(status.PauseBanner.gameObject.activeSelf, Is.False);
        }

        [Test]
        public void ExtractionBlock_ShowsTheStateLabel_AndHidesWhenHidden()
        {
            Build();
            var s = hud.Snapshot;
            var status = hud.Status;
            s.Extraction = HudExtractionState.Active;
            s.ExtractionInside = 2;
            s.ExtractionRequired = 3;
            hud.ApplySnapshot();
            Assert.That(status.RightBlock.gameObject.activeSelf, Is.True);
            Assert.That(status.ExtractionChip.gameObject.activeSelf, Is.True);
            Assert.That(status.ExtractionLabel.text, Is.EqualTo(HudText.ExtractionLabel(HudExtractionState.Active, 2, 3)));
            Assert.That(status.FollowChip.gameObject.activeSelf, Is.False, "no follow source hides the chip");

            s.Extraction = HudExtractionState.Locked;
            hud.ApplySnapshot();
            Assert.That(status.ExtractionLabel.text, Is.EqualTo("EXTRACTION LOCKED"));

            s.Extraction = HudExtractionState.Hidden;
            hud.ApplySnapshot();
            Assert.That(status.RightBlock.gameObject.activeSelf, Is.False, "nothing to show hides the block");

            s.HasFollow = true;
            s.FollowOn = false;
            hud.ApplySnapshot();
            Assert.That(status.RightBlock.gameObject.activeSelf, Is.True);
            Assert.That(status.ExtractionChip.gameObject.activeSelf, Is.False);
            Assert.That(status.FollowChip.gameObject.activeSelf, Is.True);
            Assert.That(status.FollowLabel.text, Is.EqualTo("FOLLOW: OFF"));

            s.FollowOn = true;
            hud.ApplySnapshot();
            Assert.That(status.FollowLabel.text, Is.EqualTo("FOLLOW: ON"));
        }

        [Test]
        public void ResultBanner_ShowsSuccessAndFailure()
        {
            Build();
            var s = hud.Snapshot;
            var status = hud.Status;
            hud.ApplySnapshot();
            Assert.That(status.ResultBanner.gameObject.activeSelf, Is.False, "no result, no banner");

            s.HasMission = true;
            s.BannerText = MissionHudText.Banner(MissionPhase.Success);
            hud.ApplySnapshot();
            Assert.That(status.ResultBanner.gameObject.activeSelf, Is.True);
            Assert.That(status.ResultLabel.text, Is.EqualTo(MissionHudText.Banner(MissionPhase.Success)));
            Assert.That(status.ResultLabel.fontSize, Is.EqualTo(HudTheme.FontBanner));
            Assert.That(status.ResultLabel.color, Is.EqualTo(HudTheme.Good));

            s.BannerText = MissionHudText.Banner(MissionPhase.Failure);
            hud.ApplySnapshot();
            Assert.That(status.ResultLabel.text, Is.EqualTo("MISSION FAILED"));
            Assert.That(status.ResultLabel.color, Is.EqualTo(HudTheme.Bad));

            s.BannerText = string.Empty;
            hud.ApplySnapshot();
            Assert.That(status.ResultBanner.gameObject.activeSelf, Is.False);
        }

        [Test]
        public void Apply_DoesNotReassignUnchangedText()
        {
            Build();
            FillMaximum(hud.Snapshot);
            var before = HudFactory.TextWrites;
            hud.ApplySnapshot();
            Assert.That(HudFactory.TextWrites, Is.GreaterThan(before), "the first apply writes the texts");

            var afterFirst = HudFactory.TextWrites;
            hud.ApplySnapshot();
            Assert.That(HudFactory.TextWrites, Is.EqualTo(afterFirst), "an unchanged snapshot writes no text");

            hud.Snapshot.ResumePrompt = "Menu";
            hud.ApplySnapshot();
            Assert.That(HudFactory.TextWrites, Is.EqualTo(afterFirst + 1), "one changed value writes one text");
        }

        [Test]
        public void CanvasUnits_FollowTheScalerAtMatchHalf()
        {
            Assert.That(HudLayout.CanvasUnits(1920, 1080), Is.EqualTo(new Vector2(1920f, 1080f)));
            Assert.That(HudLayout.CanvasUnits(1280, 720).x, Is.EqualTo(1920f).Within(0.5f));
            Assert.That(HudLayout.CanvasUnits(2560, 1080).x, Is.EqualTo(2217f).Within(1f));
            Assert.That(HudLayout.CanvasUnits(2560, 1080).y, Is.EqualTo(935f).Within(1f));
            Assert.That(HudLayout.CanvasUnits(1920, 1200).x, Is.EqualTo(1822f).Within(1f));
            Assert.That(HudLayout.CanvasUnits(1920, 1200).y, Is.EqualTo(1138f).Within(1f));
        }

        /// <summary>
        /// The root is sized in canvas units, as the CanvasScaler sizes the real canvas for that screen (1920x1200 is about
        /// 1822x1138 units, 2560x1080 about 2217x935).
        /// </summary>
        [TestCase(1920, 1080, TestName = "Layout_StaysInsideAndDoesNotOverlap_AtScreen1920x1080_16x9")]
        [TestCase(1920, 1200, TestName = "Layout_StaysInsideAndDoesNotOverlap_AtScreen1920x1200_16x10")]
        [TestCase(2560, 1080, TestName = "Layout_StaysInsideAndDoesNotOverlap_AtScreen2560x1080_21x9")]
        public void Layout_StaysInsideAndDoesNotOverlap(int screenWidth, int screenHeight)
        {
            var units = HudLayout.CanvasUnits(screenWidth, screenHeight);
            Build(units.x, units.y);
            FillMaximum(hud.Snapshot);
            hud.ApplySnapshot();
            HudLayout.Rebuild(root);

            var status = hud.Status;
            Assert.That(status.PauseBanner.gameObject.activeInHierarchy, Is.True);
            Assert.That(status.ResultBanner.gameObject.activeInHierarchy, Is.True);
            Assert.That(status.RightBlock.gameObject.activeInHierarchy, Is.True);
            Assert.That(hud.Objectives.Row(7).activeInHierarchy, Is.True);

            var objectives = Panel("Objectives");
            var squad = Panel("Squad");
            var operative = Panel("Operative");
            var prompts = Panel("Prompts");
            var target = Panel("Target");
            HudLayout.AssertInside(root, objectives, Panel("Status"), status.PauseBanner, status.ResultBanner, status.RightBlock,
                squad, operative, prompts, target, Panel("WorldMarks"));
            HudLayout.AssertNoOverlap(objectives, status.PauseBanner, status.ResultBanner, status.RightBlock,
                squad, operative, prompts, target);

            Assert.That(HudLayout.WorldRect(objectives).width, Is.EqualTo(420f).Within(0.5f));
            Assert.That(HudLayout.WorldRect(objectives).height, Is.GreaterThan(8 * HudTheme.FontBody), "the layout sized the panel to its rows");
            HudLayout.AssertInside(objectives, (RectTransform)hud.Objectives.Row(7).transform, hud.Objectives.Header.rectTransform);
            for (var i = 0; i < hud.Objectives.RowCapacity; i++)
            {
                var text = hud.Objectives.RowText(i);
                var rowHeight = ((RectTransform)hud.Objectives.Row(i).transform).rect.height;
                Assert.That(text.preferredHeight, Is.GreaterThan(HudTheme.FontBody), $"row {i} wraps (the content is long)");
                Assert.That(rowHeight, Is.GreaterThanOrEqualTo(text.preferredHeight - 0.5f), $"row {i} holds its wrapped text");
                Assert.That(text.rectTransform.rect.height, Is.GreaterThanOrEqualTo(text.preferredHeight - 0.5f), $"row {i} text rect");
            }
            HudLayout.AssertInside(status.PauseBanner, status.PauseLabel.rectTransform, status.PauseGlyph[0].rectTransform, status.PauseGlyph[1].rectTransform);
            HudLayout.AssertInside(status.ResultBanner, status.ResultLabel.rectTransform);
            HudLayout.AssertInside(status.RightBlock, status.ExtractionChip, status.FollowChip);
            Assert.That(HudLayout.WorldRect(squad).width, Is.EqualTo(460f).Within(0.5f));
            Assert.That(HudLayout.WorldRect(squad).height, Is.EqualTo(6 * 72f + 5 * 8f).Within(0.5f));
            Assert.That(HudLayout.WorldRect(operative).size, Is.EqualTo(new Vector2(760f, 250f)));
            Assert.That(HudLayout.WorldRect(prompts).width, Is.EqualTo(380f).Within(0.5f));
            Assert.That(HudLayout.WorldRect(target).size, Is.EqualTo(new Vector2(340f, 140f)));
            Assert.That(HudLayout.WorldRect(Panel("WorldMarks")).width, Is.EqualTo(units.x).Within(0.5f));
            Assert.That(HudLayout.WorldRect(Panel("WorldMarks")).height, Is.EqualTo(units.y).Within(0.5f));
            Assert.That(HudLayout.WorldRect(operative).center.x, Is.EqualTo(HudLayout.WorldRect(root).center.x).Within(0.5f),
                "the operative panel is centred");
            Assert.That(HudLayout.WorldRect(status.PauseBanner).center.x,
                Is.EqualTo(HudLayout.WorldRect(root).center.x).Within(0.5f), "the pause banner is centred");
        }

        [UnityTest]
        public IEnumerator TheEventSystem_IsPointerOnly()
        {
            Assert.That(Object.FindFirstObjectByType<EventSystem>(), Is.Null, "precondition: no EventSystem in the test scene");
            var hudObject = Track(new GameObject("hud"));
            var liveHud = hudObject.AddComponent<TacticalHud>();
            yield return null;

            var canvas = liveHud.GetComponentInChildren<Canvas>();
            Assert.That(canvas, Is.Not.Null);
            Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
            Assert.That(canvas.sortingOrder, Is.EqualTo(10));
            var scaler = canvas.GetComponent<CanvasScaler>();
            Assert.That(scaler.uiScaleMode, Is.EqualTo(CanvasScaler.ScaleMode.ScaleWithScreenSize));
            Assert.That(scaler.referenceResolution, Is.EqualTo(new Vector2(1920f, 1080f)));
            Assert.That(scaler.matchWidthOrHeight, Is.EqualTo(0.5f));
            Assert.That(canvas.GetComponent<GraphicRaycaster>(), Is.Not.Null);
            Assert.That(canvas.transform.Find("Objectives"), Is.Not.Null, "OnEnable builds the hierarchy into the canvas");

            var eventSystem = Object.FindFirstObjectByType<EventSystem>();
            Assert.That(eventSystem, Is.Not.Null, "the HUD creates an EventSystem when the scene has none");
            var module = eventSystem.GetComponent<InputSystemUIInputModule>();
            Assert.That(module, Is.Not.Null);
            AssertPointerOnly(module);

            eventSystem.gameObject.SetActive(false);
            eventSystem.gameObject.SetActive(true);
            yield return null;
            AssertPointerOnly(module);

            foreach (var graphic in liveHud.GetComponentsInChildren<Graphic>(true))
            {
                if (IsButtonGraphic(graphic))
                    continue;
                Assert.That(graphic.raycastTarget, Is.False, $"{graphic.name} must not catch pointer raycasts");
            }
        }

        [UnityTest]
        public IEnumerator AnExistingEventSystem_IsReused()
        {
            var existing = Track(new GameObject("SceneEventSystem", typeof(EventSystem)));
            var hudObject = Track(new GameObject("hud"));
            hudObject.AddComponent<TacticalHud>();
            yield return null;

            Assert.That(Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None).Length, Is.EqualTo(1));
            Assert.That(existing.GetComponent<EventSystem>(), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator AnExistingPlainUIModule_LosesItsNavigationActions()
        {
            var existing = Track(new GameObject("SceneEventSystem"));
            existing.AddComponent<EventSystem>();
            var module = existing.AddComponent<InputSystemUIInputModule>();
            Assert.That(module.move, Is.Not.Null, "precondition: a plain module enables with the default navigation actions");
            var hudObject = Track(new GameObject("hud"));
            hudObject.AddComponent<TacticalHud>();
            yield return null;

            Assert.That(Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None).Length, Is.EqualTo(1), "the scene's system is reused");
            AssertPointerOnly(module);
        }

        static void AssertPointerOnly(InputSystemUIInputModule module)
        {
            Assert.That(module.move == null || module.move.action == null, Is.True, "no move action");
            Assert.That(module.submit == null || module.submit.action == null, Is.True, "no submit action");
            Assert.That(module.cancel == null || module.cancel.action == null, Is.True, "no cancel action");
            Assert.That(module.point, Is.Not.Null, "pointer position still drives the UI");
            Assert.That(module.leftClick, Is.Not.Null, "pointer clicks still drive the UI");
        }

        static bool IsButtonGraphic(Graphic graphic)
        {
            foreach (var selectable in graphic.GetComponentsInParent<Selectable>(true))
            {
                if (selectable.targetGraphic == graphic)
                    return true;
            }
            return false;
        }

        static void AddObjective(HudSnapshot s, HudObjectiveKind kind, string text) =>
            s.Objectives.Add(new HudObjectiveRow { Kind = kind, Text = text });

        /// <summary>The fullest HUD the game can produce today, so layout and write counts see every row in use.</summary>
        static void FillMaximum(HudSnapshot s)
        {
            s.Clear();
            s.HasMission = true;
            s.PhaseText = "extraction open";
            s.BannerText = MissionHudText.Banner(MissionPhase.Success);
            for (var i = 0; i < 8; i++)
                AddObjective(s, (HudObjectiveKind)(i % 5), "Download the security logs at Terminal Kappa " + i + " (45%)");
            s.Extraction = HudExtractionState.Active;
            s.ExtractionInside = 3;
            s.ExtractionRequired = 4;
            s.IsPaused = true;
            s.ResumePrompt = "Options";
            s.HasFollow = true;
            s.FollowOn = true;
            for (var i = 0; i < 6; i++)
                s.Squad.Add(new HudSquadCard
                {
                    Name = "Operative Number " + i, Role = "Ranged", Initials = "ON", Tag = "FOLLOWING",
                    Rank = 3, Health = 80, MaxHealth = 100,
                });
            for (var i = 0; i < 4; i++)
                s.Abilities.Add(new HudAbilitySlot { Slot = i, Prompt = "RT + Up", Name = "Suppressing Fire", State = HudAbilityState.Cooldown, Remaining = 4.3f, Fraction = 0.5f });
            for (var i = 0; i < 8; i++)
                s.Queue.Add(new HudCommandStep { Number = i + 1, Text = "Attack (target lost)", IsCurrent = i == 0 });
            s.QueueHidden = 3;
            for (var i = 0; i < 6; i++)
                s.Prompts.Add(new HudPromptEntry { Label = "Switch character", Prompt = "LB / RB" });
            s.HasControlled = true;
            s.ControlledName = "Darius";
            s.ControlledRole = "Ranged";
            s.ControlledCover = "Corner cover";
            s.ControlledRank = 3;
            s.ControlledHealth = 80;
            s.ControlledMaxHealth = 100;
            s.QueueOwner = "Darius";
            s.CanClearOrders = true;
            s.IsArmed = true;
            s.ArmedLine = "AIMING: Suppressing Fire";
            s.Target = new HudTarget
            {
                Visible = true, Name = "Hostile Rifleman", Detail = "Ranged", Tag = "ATTACKING", CoverText = "Cover 65%",
                Health = 60, MaxHealth = 100,
            };
        }
    }
}
#endif
