#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Blackglass.Tests
{
    /// <summary>
    /// The operative panel driven straight from a snapshot: controlled info, ability slots and their non-colour-only
    /// states, the aiming strip, the command queue and CLEAR, change-only writes and layout.
    /// </summary>
    public class HudOperativePanelTests
    {
        const string RankSeparator = " · ";
        const string Dash = "—";

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

        void Build(float width = 1920f, float height = 1080f)
        {
            var hudObject = new GameObject("hud");
            created.Add(hudObject);
            hudObject.SetActive(false);
            hud = hudObject.AddComponent<TacticalHud>();
            hud.Initialize(new HudSources(), null);
            root = HudLayout.CreateRoot(width, height);
            created.Add(root.gameObject);
            hud.BuildInto(root);
        }

        RectTransform Panel(string name) => (RectTransform)root.Find(name);

        static void Controlled(HudSnapshot s, string name = "Darius Vale", string role = "Ranged", int rank = 3, int health = 64, int max = 100,
            string cover = "Low cover")
        {
            s.HasControlled = true;
            s.ControlledName = name; s.ControlledRole = role; s.ControlledRank = rank;
            s.ControlledHealth = health; s.ControlledMaxHealth = max; s.ControlledCover = cover;
        }

        static HudAbilitySlot Slot(int slot, HudAbilityState state = HudAbilityState.Ready, string name = null, float remaining = 0f, float fraction = 0f) =>
            new HudAbilitySlot
            {
                Slot = slot, Prompt = "RT + " + slot, Name = name ?? "Ability " + slot, State = state, Remaining = remaining, Fraction = fraction,
            };

        static void Orders(HudSnapshot s, int steps, int hidden = 0, string owner = "Darius Vale", bool canClear = true)
        {
            s.QueueOwner = owner;
            s.QueueHidden = hidden;
            s.CanClearOrders = canClear;
            for (var i = 0; i < steps; i++)
                s.Queue.Add(new HudCommandStep { Number = i + 1, Text = "Step " + (i + 1), IsCurrent = i == 0 });
        }

        // ---- font ----

        [Test]
        public void TheStateGlyphs_AreInTheBuiltInFont()
        {
            Assert.That(HudFactory.Font.HasCharacter('—'), Is.True, "the unavailable marker; otherwise it must be a plain hyphen");
            Assert.That(HudFactory.Font.HasCharacter('·'), Is.True);
        }

        // ---- build ----

        [Test]
        public void Build_CreatesTheHiddenPoolsInsideTheOperativeZone()
        {
            Build();
            var panel = hud.Operative;
            Assert.That(panel.Root, Is.SameAs(Panel("Operative")));
            Assert.That(panel.SlotCapacity, Is.EqualTo(4));
            Assert.That(panel.StepCapacity, Is.EqualTo(8));
            for (var i = 0; i < panel.SlotCapacity; i++)
                Assert.That(panel.SlotAt(i).Root.gameObject.activeSelf, Is.False, $"slot {i} starts hidden");
            for (var i = 0; i < panel.StepCapacity; i++)
                Assert.That(panel.StepAt(i).gameObject.activeSelf, Is.False, $"step {i} starts hidden");
            Assert.That(panel.ClearButton.gameObject.activeSelf, Is.False);
        }

        // ---- controlled operative ----

        [Test]
        public void TheControlledRow_ShowsNameRoleRankHealthAndCover()
        {
            Build();
            Controlled(hud.Snapshot);
            hud.ApplySnapshot();

            var panel = hud.Operative;
            Assert.That(panel.NoUnitLabel.gameObject.activeSelf, Is.False);
            Assert.That(panel.InfoGroup.gameObject.activeSelf, Is.True);
            Assert.That(panel.NameLabel.text, Is.EqualTo("Darius Vale"));
            Assert.That(panel.NameLabel.fontSize, Is.EqualTo(HudTheme.FontLarge));
            Assert.That(panel.RoleLabel.text, Is.EqualTo("Ranged" + RankSeparator + "Rank 3"));
            Assert.That(panel.HealthLabel.text, Is.EqualTo(HudText.Health(64, 100)));
            Assert.That(panel.HealthFill.fillAmount, Is.EqualTo(0.64f).Within(0.001f));
            Assert.That(panel.CoverLabel.text, Is.EqualTo("Low cover"));

            hud.Snapshot.ControlledHealth = 10;
            hud.Snapshot.ControlledCover = "Exposed";
            hud.ApplySnapshot();
            Assert.That(panel.HealthLabel.text, Is.EqualTo("10/100"));
            Assert.That(panel.HealthFill.fillAmount, Is.EqualTo(0.1f).Within(0.001f));
            Assert.That(panel.HealthFill.color, Is.EqualTo(HudTheme.Bad), "low health");
            Assert.That(panel.CoverLabel.text, Is.EqualTo("Exposed"));
        }

        [Test]
        public void TheRoleLine_HidesRankZero_AndNeverLeadsWithASeparator()
        {
            Build();
            var s = hud.Snapshot;
            Controlled(s, role: "Support", rank: 0);
            hud.ApplySnapshot();
            Assert.That(hud.Operative.RoleLabel.text, Is.EqualTo("Support"));

            s.ControlledRank = 4;
            hud.ApplySnapshot();
            Assert.That(hud.Operative.RoleLabel.text, Is.EqualTo("Support" + RankSeparator + "Rank 4"));

            s.ControlledRole = null;
            hud.ApplySnapshot();
            Assert.That(hud.Operative.RoleLabel.text, Is.EqualTo("Rank 4"));

            s.ControlledRole = "";
            hud.ApplySnapshot();
            Assert.That(hud.Operative.RoleLabel.text, Is.EqualTo("Rank 4"));

            s.ControlledRank = 0;
            hud.ApplySnapshot();
            Assert.That(hud.Operative.RoleLabel.text, Is.EqualTo(string.Empty));
        }

        [Test]
        public void WithoutAControlledUnit_ThePanelSaysSo_AndShowsNoAbilitiesOrQueue()
        {
            Build();
            var s = hud.Snapshot;
            Controlled(s);
            s.Abilities.Add(Slot(0));
            s.IsArmed = true; s.ArmedLine = "AIMING: Ability 0";
            Orders(s, 3, 2);
            hud.ApplySnapshot();
            Assert.That(hud.Operative.ClearButton.gameObject.activeInHierarchy, Is.True, "precondition");

            s.Clear();
            hud.ApplySnapshot();

            var panel = hud.Operative;
            Assert.That(panel.NoUnitLabel.gameObject.activeSelf, Is.True);
            Assert.That(panel.NoUnitLabel.text, Is.EqualTo("No unit in control"));
            Assert.That(panel.InfoGroup.gameObject.activeSelf, Is.False);
            for (var i = 0; i < panel.SlotCapacity; i++)
                Assert.That(panel.SlotAt(i).Root.gameObject.activeInHierarchy, Is.False, $"slot {i}");
            Assert.That(panel.AimingStrip.gameObject.activeInHierarchy, Is.False);
            Assert.That(panel.OrdersLabel.gameObject.activeInHierarchy, Is.False);
            for (var i = 0; i < panel.StepCapacity; i++)
                Assert.That(panel.StepAt(i).gameObject.activeInHierarchy, Is.False, $"step {i}");
            Assert.That(panel.MoreLabel.gameObject.activeInHierarchy, Is.False);
            Assert.That(panel.ClearButton.gameObject.activeInHierarchy, Is.False);
            Assert.That(panel.QueueUnit, Is.Null);

            Controlled(s);
            hud.ApplySnapshot();
            Assert.That(panel.NoUnitLabel.gameObject.activeSelf, Is.False, "a unit again");
            Assert.That(panel.NameLabel.text, Is.EqualTo("Darius Vale"));
        }

        // ---- abilities ----

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(6)]
        public void TheSlotCount_FollowsTheSnapshot(int entries)
        {
            Build();
            var s = hud.Snapshot;
            Controlled(s);
            for (var i = 0; i < entries; i++)
                s.Abilities.Add(Slot(i));
            hud.ApplySnapshot();

            var shown = Mathf.Min(entries, 4);
            for (var i = 0; i < 4; i++)
            {
                var slot = hud.Operative.SlotAt(i);
                Assert.That(slot.Root.gameObject.activeSelf, Is.EqualTo(i < shown), $"slot {i}");
                if (i < shown)
                {
                    Assert.That(slot.NameLabel.text, Is.EqualTo("Ability " + i));
                    Assert.That(slot.PromptLabel.text, Is.EqualTo("RT + " + i));
                    Assert.That(slot.Slot.Slot, Is.EqualTo(i), "the slot keeps its applied data");
                }
            }
        }

        [Test]
        public void EveryAbilityState_HasItsOwnTextMarker_NotJustAColour()
        {
            Build();
            var s = hud.Snapshot;
            Controlled(s);
            s.Abilities.Add(Slot(0, HudAbilityState.Ready));
            s.Abilities.Add(Slot(1, HudAbilityState.Cooldown, remaining: 4.3f, fraction: 0.43f));
            s.Abilities.Add(Slot(2, HudAbilityState.Armed));
            s.Abilities.Add(Slot(3, HudAbilityState.Unavailable));
            hud.ApplySnapshot();

            var panel = hud.Operative;
            Assert.That(panel.SlotAt(0).StateLabel.text, Is.EqualTo("READY"));
            Assert.That(panel.SlotAt(1).StateLabel.text, Is.EqualTo(HudText.Seconds(4.3f)));
            Assert.That(panel.SlotAt(2).StateLabel.text, Is.EqualTo("ARMED"));
            Assert.That(panel.SlotAt(3).StateLabel.text, Is.EqualTo(Dash));
            var markers = Enumerable.Range(0, 4).Select(i => panel.SlotAt(i).StateLabel.text).ToArray();
            Assert.That(markers, Is.Unique, "the four states read differently as text");

            Assert.That(panel.SlotAt(0).NameLabel.text, Is.EqualTo("Ability 0"));
            Assert.That(panel.SlotAt(2).NameLabel.text, Is.EqualTo("> Ability 2"), "armed: a > prefix");
            Assert.That(panel.SlotAt(2).ArmedFrame.gameObject.activeSelf, Is.True, "armed: a bright frame");
            Assert.That(panel.SlotAt(0).ArmedFrame.gameObject.activeSelf, Is.False);
            Assert.That(panel.SlotAt(1).ArmedFrame.gameObject.activeSelf, Is.False);
            Assert.That(panel.SlotAt(3).ContentAlpha, Is.LessThan(1f), "unavailable: dimmed");
            Assert.That(panel.SlotAt(0).ContentAlpha, Is.EqualTo(1f));
            Assert.That(panel.SlotAt(0).CooldownOverlay.gameObject.activeSelf, Is.False);
            Assert.That(panel.SlotAt(2).CooldownOverlay.gameObject.activeSelf, Is.False);

            // A state leaves nothing behind when the next snapshot says otherwise.
            s.Abilities[2] = Slot(2, HudAbilityState.Ready);
            s.Abilities[3] = Slot(3, HudAbilityState.Ready);
            s.Abilities[1] = Slot(1, HudAbilityState.Ready);
            hud.ApplySnapshot();
            for (var i = 1; i < 4; i++)
            {
                var slot = panel.SlotAt(i);
                Assert.That(slot.StateLabel.text, Is.EqualTo("READY"), $"slot {i}");
                Assert.That(slot.NameLabel.text, Is.EqualTo("Ability " + i), $"slot {i}: no stale prefix");
                Assert.That(slot.ArmedFrame.gameObject.activeSelf, Is.False);
                Assert.That(slot.CooldownOverlay.gameObject.activeSelf, Is.False);
                Assert.That(slot.ContentAlpha, Is.EqualTo(1f));
            }
        }

        [Test]
        public void TheCooldownOverlay_FillsByFraction_AndTheNumberIsHudTextSeconds()
        {
            Build();
            var s = hud.Snapshot;
            Controlled(s);
            s.Abilities.Add(Slot(0, HudAbilityState.Cooldown, remaining: 7.26f, fraction: 0.726f));
            s.Abilities.Add(Slot(1, HudAbilityState.Cooldown, remaining: 25f, fraction: 1f));
            hud.ApplySnapshot();

            var first = hud.Operative.SlotAt(0);
            Assert.That(first.CooldownOverlay.gameObject.activeSelf, Is.True);
            Assert.That(first.CooldownOverlay.type, Is.EqualTo(Image.Type.Filled));
            Assert.That(first.CooldownOverlay.fillMethod, Is.EqualTo(Image.FillMethod.Vertical));
            Assert.That(first.CooldownOverlay.fillAmount, Is.EqualTo(0.726f).Within(0.0001f));
            Assert.That(first.StateLabel.text, Is.EqualTo(HudText.Seconds(7.26f)));
            Assert.That(first.StateLabel.text, Is.EqualTo("7.3"));
            Assert.That(hud.Operative.SlotAt(1).StateLabel.text, Is.EqualTo("25"));
            Assert.That(hud.Operative.SlotAt(1).CooldownOverlay.fillAmount, Is.EqualTo(1f));

            s.Abilities[0] = Slot(0, HudAbilityState.Cooldown, remaining: 1.01f, fraction: 0.1f);
            hud.ApplySnapshot();
            Assert.That(first.CooldownOverlay.fillAmount, Is.EqualTo(0.1f).Within(0.0001f));
            Assert.That(first.StateLabel.text, Is.EqualTo(HudText.Seconds(1.01f)));
        }

        [Test]
        public void TheCooldownNumber_IsWrittenOnlyWhenItsTextChanges()
        {
            Build();
            var s = hud.Snapshot;
            Controlled(s);
            s.Abilities.Add(Slot(0, HudAbilityState.Cooldown, remaining: 8.0f, fraction: 0.8f));
            hud.ApplySnapshot();
            var baseline = HudFactory.TextWrites;

            // Six frames of decay within one tenth: the text is the same, so nothing is written.
            for (var i = 0; i < 6; i++)
            {
                s.Abilities[0] = Slot(0, HudAbilityState.Cooldown, remaining: 7.99f - i * 0.016f, fraction: 0.79f - i * 0.002f);
                hud.ApplySnapshot();
            }
            Assert.That(hud.Operative.SlotAt(0).StateLabel.text, Is.EqualTo(HudText.Seconds(7.99f - 5 * 0.016f)));
            Assert.That(HudFactory.TextWrites, Is.EqualTo(baseline), "the whole decay stays within the tenth 8.0, so nothing is written");

            s.Abilities[0] = Slot(0, HudAbilityState.Cooldown, remaining: 7.85f, fraction: 0.78f);
            hud.ApplySnapshot();
            Assert.That(HudFactory.TextWrites, Is.EqualTo(baseline + 1), "the next tenth writes once");
        }

        [Test]
        public void ASlot_IsAClickTarget_ThatKeepsItsData()
        {
            Build();
            Controlled(hud.Snapshot);
            hud.Snapshot.Abilities.Add(Slot(2, name: "Blast"));
            hud.ApplySnapshot();

            var slot = hud.Operative.SlotAt(0);
            var button = slot.Root.GetComponent<Button>();
            Assert.That(button, Is.Not.Null);
            Assert.That(button.targetGraphic, Is.SameAs(slot.Background));
            Assert.That(slot.Background.raycastTarget, Is.True);
            Assert.That(slot.Slot.Slot, Is.EqualTo(2));
            var clicks = 0;
            slot.Clicked += _ => clicks++;
            hud.ApplySnapshot();
            Assert.That(clicks, Is.Zero, "nothing raises the event yet");
            foreach (var graphic in slot.Root.GetComponentsInChildren<Graphic>(true))
            {
                if (graphic != slot.Background)
                    Assert.That(graphic.raycastTarget, Is.False, $"{graphic.name} must not catch pointer raycasts");
            }
        }

        // ---- aiming strip ----

        [Test]
        public void TheAimingStrip_IsVisibleOnlyWhileArmed()
        {
            Build();
            var s = hud.Snapshot;
            Controlled(s);
            s.Abilities.Add(Slot(0));
            hud.ApplySnapshot();
            Assert.That(hud.Operative.AimingStrip.gameObject.activeSelf, Is.False);

            s.IsArmed = true;
            s.ArmedLine = "AIMING: Ability 0 | Hostile | 4.0/12.0 m | OK";
            hud.ApplySnapshot();
            Assert.That(hud.Operative.AimingStrip.gameObject.activeSelf, Is.True);
            Assert.That(hud.Operative.AimingLabel.text, Is.EqualTo(s.ArmedLine));

            s.ArmedLine = "AIMING: Ability 0";
            hud.ApplySnapshot();
            Assert.That(hud.Operative.AimingLabel.text, Is.EqualTo("AIMING: Ability 0"));

            s.IsArmed = false;
            s.ArmedLine = string.Empty;
            hud.ApplySnapshot();
            Assert.That(hud.Operative.AimingStrip.gameObject.activeSelf, Is.False);
        }

        // ---- queue ----

        [Test]
        public void TheQueue_ListsNumberedSteps_WithTheCurrentOneMarked()
        {
            Build();
            var s = hud.Snapshot;
            Controlled(s);
            Orders(s, 5);
            hud.ApplySnapshot();

            var panel = hud.Operative;
            Assert.That(panel.OrdersLabel.gameObject.activeSelf, Is.True);
            Assert.That(panel.OrdersLabel.text, Is.EqualTo("ORDERS: Darius Vale"));
            Assert.That(panel.StepAt(0).text, Is.EqualTo("> Step 1"));
            Assert.That(panel.StepAt(1).text, Is.EqualTo("2 Step 2"));
            Assert.That(panel.StepAt(4).text, Is.EqualTo("5 Step 5"));
            for (var i = 0; i < 8; i++)
                Assert.That(panel.StepAt(i).gameObject.activeSelf, Is.EqualTo(i < 5), $"step {i}");
            Assert.That(panel.MoreLabel.gameObject.activeSelf, Is.False);
        }

        [Test]
        public void TheQueue_ShowsPlusNMore_WhenStepsAreHidden()
        {
            Build();
            var s = hud.Snapshot;
            Controlled(s);
            Orders(s, 8, hidden: 3);
            hud.ApplySnapshot();
            Assert.That(hud.Operative.MoreLabel.gameObject.activeSelf, Is.True);
            Assert.That(hud.Operative.MoreLabel.text, Is.EqualTo("+3 more"));
            Assert.That(hud.Operative.StepAt(7).text, Is.EqualTo("8 Step 8"));

            s.QueueHidden = 12;
            hud.ApplySnapshot();
            Assert.That(hud.Operative.MoreLabel.text, Is.EqualTo("+12 more"));

            s.QueueHidden = 0;
            hud.ApplySnapshot();
            Assert.That(hud.Operative.MoreLabel.gameObject.activeSelf, Is.False);
        }

        [Test]
        public void TheQueue_LeavesNoStaleLines_WhenItShrinks()
        {
            Build();
            var s = hud.Snapshot;
            Controlled(s);
            Orders(s, 8, hidden: 2);
            hud.ApplySnapshot();

            s.Queue.RemoveRange(2, 6);
            s.QueueHidden = 0;
            hud.ApplySnapshot();
            for (var i = 0; i < 8; i++)
            {
                Assert.That(hud.Operative.StepAt(i).gameObject.activeSelf, Is.EqualTo(i < 2), $"step {i}");
                if (i >= 2)
                    Assert.That(hud.Operative.StepAt(i).text, Is.Empty, $"step {i} keeps no old text");
            }
            Assert.That(hud.Operative.MoreLabel.gameObject.activeSelf, Is.False);

            // Growing again shows the new text, not the one from before the shrink.
            s.Queue.Add(new HudCommandStep { Number = 3, Text = "Fresh", IsCurrent = false });
            hud.ApplySnapshot();
            Assert.That(hud.Operative.StepAt(2).text, Is.EqualTo("3 Fresh"));

            s.Queue.Clear();
            s.CanClearOrders = false;
            hud.ApplySnapshot();
            for (var i = 0; i < 8; i++)
                Assert.That(hud.Operative.StepAt(i).gameObject.activeSelf, Is.False);
            Assert.That(hud.Operative.ClearButton.gameObject.activeSelf, Is.False);
        }

        [Test]
        public void TheCurrentStep_FollowsTheSnapshot_NotThePosition()
        {
            Build();
            var s = hud.Snapshot;
            Controlled(s);
            Orders(s, 3);
            hud.ApplySnapshot();
            Assert.That(hud.Operative.StepAt(0).text, Is.EqualTo("> Step 1"));

            // The first order finished: what was step 2 is current now.
            s.Queue.RemoveAt(0);
            s.Queue[0] = new HudCommandStep { Number = 1, Text = "Step 2", IsCurrent = true };
            s.Queue[1] = new HudCommandStep { Number = 2, Text = "Step 3", IsCurrent = false };
            hud.ApplySnapshot();
            Assert.That(hud.Operative.StepAt(0).text, Is.EqualTo("> Step 2"));
            Assert.That(hud.Operative.StepAt(1).text, Is.EqualTo("2 Step 3"));
            Assert.That(hud.Operative.StepAt(2).gameObject.activeSelf, Is.False);
        }

        [Test]
        public void Clear_IsVisibleOnlyWithCanClearOrders_AndCarriesTheQueueUnit()
        {
            Build();
            var unitObject = new GameObject("unit", typeof(CommandableUnit));
            created.Add(unitObject);
            var unit = unitObject.GetComponent<CommandableUnit>();
            var s = hud.Snapshot;
            Controlled(s);
            Orders(s, 2, canClear: false);
            s.QueueUnit = unit;
            hud.ApplySnapshot();
            var panel = hud.Operative;
            Assert.That(panel.ClearButton.gameObject.activeSelf, Is.False);
            Assert.That(panel.QueueUnit, Is.SameAs(unit), "the unit is read every apply");

            s.CanClearOrders = true;
            hud.ApplySnapshot();
            Assert.That(panel.ClearButton.gameObject.activeSelf, Is.True);
            Assert.That(panel.ClearButton.GetComponentInChildren<Text>().text, Is.EqualTo("CLEAR"));
            var button = panel.ClearButton.GetComponent<Button>();
            Assert.That(button, Is.Not.Null);
            Assert.That(button.targetGraphic, Is.SameAs(panel.ClearBackground));
            Assert.That(panel.ClearBackground.raycastTarget, Is.True);
            foreach (var graphic in panel.ClearButton.GetComponentsInChildren<Graphic>(true))
            {
                if (graphic != panel.ClearBackground)
                    Assert.That(graphic.raycastTarget, Is.False, $"{graphic.name} must not catch pointer raycasts");
            }
            var clears = 0;
            panel.ClearClicked += _ => clears++;
            hud.ApplySnapshot();
            Assert.That(clears, Is.Zero, "nothing raises the event yet");

            var other = new GameObject("other", typeof(CommandableUnit));
            created.Add(other);
            s.QueueUnit = other.GetComponent<CommandableUnit>();
            hud.ApplySnapshot();
            Assert.That(panel.QueueUnit, Is.SameAs(other.GetComponent<CommandableUnit>()), "no stale unit for a click");

            s.QueueUnit = null;
            hud.ApplySnapshot();
            Assert.That(panel.QueueUnit, Is.Null);
        }

        // ---- writes ----

        [Test]
        public void ApplyingTheSameSnapshotTwice_WritesNoText()
        {
            Build();
            var s = hud.Snapshot;
            Controlled(s);
            s.Abilities.Add(Slot(0, HudAbilityState.Ready));
            s.Abilities.Add(Slot(1, HudAbilityState.Cooldown, remaining: 4.3f, fraction: 0.43f));
            s.Abilities.Add(Slot(2, HudAbilityState.Armed));
            s.Abilities.Add(Slot(3, HudAbilityState.Unavailable));
            s.IsArmed = true;
            s.ArmedLine = "AIMING: Ability 2 | Hostile | 4.0/12.0 m | OK";
            Orders(s, 8, hidden: 2);
            var before = HudFactory.TextWrites;
            hud.ApplySnapshot();
            Assert.That(HudFactory.TextWrites, Is.GreaterThan(before));

            var afterFirst = HudFactory.TextWrites;
            hud.ApplySnapshot();
            Assert.That(HudFactory.TextWrites, Is.EqualTo(afterFirst), "an unchanged snapshot writes no text");

            s.ControlledHealth = 63;
            hud.ApplySnapshot();
            Assert.That(HudFactory.TextWrites, Is.EqualTo(afterFirst + 1), "one changed health writes one text");
        }

        // ---- layout ----

        [TestCase(1920, 1080, TestName = "Operative_Layout_At1920x1080_16x9")]
        [TestCase(1920, 1200, TestName = "Operative_Layout_At1920x1200_16x10")]
        [TestCase(2560, 1080, TestName = "Operative_Layout_At2560x1080_21x9")]
        public void TheFullestPanel_StaysInsideItself_AndDoesNotOverlapTheOthers(int screenWidth, int screenHeight)
        {
            var units = HudLayout.CanvasUnits(screenWidth, screenHeight);
            Build(units.x, units.y);
            var s = hud.Snapshot;
            s.HasMission = true;
            s.PhaseText = "extraction open";
            for (var i = 0; i < 8; i++)
                s.Objectives.Add(new HudObjectiveRow { Kind = HudObjectiveKind.Active, Text = "Download the security logs at Terminal Kappa " + i + " (45%)" });
            s.IsPaused = true;
            s.ResumePrompt = "Options";
            s.BannerText = MissionHudText.Banner(MissionPhase.Success);
            s.Extraction = HudExtractionState.Active;
            s.ExtractionInside = 3;
            s.ExtractionRequired = 4;
            s.HasFollow = true;
            for (var i = 0; i < 6; i++)
                s.Squad.Add(new HudSquadCard { Name = "Operative Number " + i, Role = "Support", Initials = "ON", Tag = "FOLLOWING", Rank = 12, Health = 100, MaxHealth = 100 });
            for (var i = 0; i < 6; i++)
                s.Prompts.Add(new HudPromptEntry { Label = "Switch character", Prompt = "LB / RB" });

            Controlled(s, "Darius Okonkwo-Reyes", "Reconnaissance", 12, 100, 100, "Corner cover");
            var states = new[] { HudAbilityState.Cooldown, HudAbilityState.Armed, HudAbilityState.Ready, HudAbilityState.Unavailable };
            for (var i = 0; i < 4; i++)
                s.Abilities.Add(Slot(i, states[i], "Suppressing Fire Barrage", 25f, 1f));
            s.IsArmed = true;
            s.ArmedLine = "AIMING: Suppressing Fire | Hostile Rifleman 12 | 10.0/12.0 m | Moving into range";
            s.QueueOwner = "Darius Valentinian Okonkwo-Reyes";
            s.QueueHidden = 12;
            s.CanClearOrders = true;
            for (var i = 0; i < 8; i++)
                s.Queue.Add(new HudCommandStep { Number = i + 1, Text = "Attack Hostile Rifleman Number Seven", IsCurrent = i == 0 });
            hud.ApplySnapshot();
            HudLayout.Rebuild(root);

            var panel = hud.Operative;
            var operative = Panel("Operative");
            Assert.That(HudLayout.WorldRect(operative).size, Is.EqualTo(new Vector2(OperativePanel.Width, OperativePanel.Height)));
            Assert.That(panel.ClearButton.gameObject.activeInHierarchy, Is.True);
            Assert.That(panel.StepAt(7).gameObject.activeInHierarchy, Is.True);
            Assert.That(panel.AimingStrip.gameObject.activeInHierarchy, Is.True);

            var parts = new List<RectTransform>
            {
                panel.NameLabel.rectTransform, panel.RoleLabel.rectTransform, panel.HealthBar, panel.HealthLabel.rectTransform,
                panel.CoverLabel.rectTransform, panel.AimingLabel.rectTransform, panel.OrdersLabel.rectTransform,
                panel.MoreLabel.rectTransform, panel.ClearButton,
            };
            for (var i = 0; i < 4; i++)
            {
                var slot = panel.SlotAt(i);
                Assert.That(slot.Root.gameObject.activeInHierarchy, Is.True);
                Assert.That(HudLayout.WorldRect(slot.Root).size, Is.EqualTo(new Vector2(170f, 64f)).Using(Vector2Comparer));
                HudLayout.AssertInside(slot.Root, slot.PromptChip, slot.PromptLabel.rectTransform, slot.NameLabel.rectTransform,
                    slot.StateLabel.rectTransform, slot.ArmedFrame);
                HudLayout.AssertNoOverlap(slot.PromptChip, slot.StateLabel.rectTransform, slot.NameLabel.rectTransform);
                Assert.That(slot.NameLabel.preferredHeight, Is.LessThanOrEqualTo(slot.NameLabel.rectTransform.rect.height + 0.5f), "a long name fits its box");
                parts.Add(slot.Root);
            }
            HudLayout.AssertNoOverlap(Enumerable.Range(0, 4).Select(i => panel.SlotAt(i).Root).ToArray());
            for (var i = 0; i < 8; i++)
            {
                var step = panel.StepAt(i);
                parts.Add(step.rectTransform);
                Assert.That(step.preferredHeight, Is.LessThanOrEqualTo(step.rectTransform.rect.height + 0.5f), $"step {i} is one line");
            }
            HudLayout.AssertInside(operative, parts.ToArray());
            HudLayout.AssertInside(operative, panel.AimingStrip);
            HudLayout.AssertInside(panel.AimingStrip, panel.AimingLabel.rectTransform);
            HudLayout.AssertNoOverlap(parts.ToArray());   // every part of the panel has its own space
            Assert.That(panel.NameLabel.preferredHeight, Is.LessThanOrEqualTo(panel.NameLabel.rectTransform.rect.height + 0.5f), "the name fits one line");
            Assert.That(panel.RoleLabel.preferredHeight, Is.LessThanOrEqualTo(panel.RoleLabel.rectTransform.rect.height + 0.5f));
            Assert.That(panel.AimingLabel.preferredHeight, Is.LessThanOrEqualTo(panel.AimingLabel.rectTransform.rect.height + 0.5f), "the aiming line fits one line");

            HudLayout.AssertInside(root, operative);
            HudLayout.AssertNoOverlap(Panel("Objectives"), hud.Status.PauseBanner, hud.Status.ResultBanner, hud.Status.RightBlock,
                Panel("Squad"), operative, Panel("Prompts"), Panel("Target"));
        }

        static readonly IEqualityComparer<Vector2> Vector2Comparer = new NearComparer();

        sealed class NearComparer : IEqualityComparer<Vector2>
        {
            public bool Equals(Vector2 a, Vector2 b) => Mathf.Abs(a.x - b.x) < 0.5f && Mathf.Abs(a.y - b.y) < 0.5f;
            public int GetHashCode(Vector2 v) => 0;
        }
    }

    /// <summary>The operative panel fed by the real snapshot builder over a real ability rig, across tactical pause.</summary>
    public class HudOperativePauseTests : InputTestFixture
    {
        readonly List<Object> created = new List<Object>();
        InputActionAsset actions;
        AbilityRig rig;

        public override void Setup()
        {
            base.Setup();
            actions = TestControls.Load();
            rig = null;
        }

        public override void TearDown()
        {
            foreach (var item in created)
            {
                if (item != null)
                    Object.DestroyImmediate(item);
            }
            created.Clear();
            rig?.World.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        [UnityTest]
        public IEnumerator TheCooldown_StaysFrozenWhilePaused_AndDoesNotJumpOnResume()
        {
            rig = AbilityRig.Build(actions, false);
            yield return null;
            var sources = new HudSources
            {
                activeCharacter = rig.Active, selection = rig.Selection, tacticalPause = rig.Pause,
                encounter = rig.Encounter, abilityTargeting = rig.Targeting, commandInput = rig.Input,
                controls = actions, camera = rig.ViewCamera,
            };
            var hudObject = new GameObject("hud");
            created.Add(hudObject);
            hudObject.SetActive(false);
            var hud = hudObject.AddComponent<TacticalHud>();
            hud.Initialize(sources, null);
            var root = HudLayout.CreateRoot(1920f, 1080f);
            created.Add(root.gameObject);
            hud.BuildInto(root);
            var builder = new HudSnapshotBuilder();
            void Frame()
            {
                builder.Build(sources, hud.Snapshot, Time.unscaledTime);
                hud.ApplySnapshot();
            }

            Assert.That(rig.Abilities.TryUse(AbilityCommand.AtGround(rig.Blast, AbilityRig.BlastGround)), Is.True, rig.Abilities.LastFailure.ToString());
            yield return null;
            Frame();
            var slot = hud.Operative.SlotAt(1);
            Assert.That(slot.Root.gameObject.activeSelf, Is.True);
            Assert.That(slot.Slot.State, Is.EqualTo(HudAbilityState.Cooldown), "precondition: Blast is cooling down");

            rig.Pause.Pause();
            Assert.That(Time.timeScale, Is.Zero);
            Frame();
            var frozenText = slot.StateLabel.text;
            var frozenFill = slot.CooldownOverlay.fillAmount;
            var writes = HudFactory.TextWrites;
            Assert.That(frozenText, Is.EqualTo(HudText.Seconds(slot.Slot.Remaining)));
            for (var i = 0; i < 6; i++)
            {
                yield return null;
                Frame();
                Assert.That(slot.StateLabel.text, Is.EqualTo(frozenText), $"frame {i}: the number stands still in tactical pause");
                Assert.That(slot.CooldownOverlay.fillAmount, Is.EqualTo(frozenFill), $"frame {i}: so does the fill");
            }
            Assert.That(HudFactory.TextWrites, Is.EqualTo(writes), "a paused HUD writes no text");

            rig.Pause.Resume();
            Frame();
            Assert.That(slot.StateLabel.text, Is.EqualTo(frozenText), "no jump on the resume frame");
            Assert.That(slot.CooldownOverlay.fillAmount, Is.EqualTo(frozenFill).Within(0.01f));

            yield return new WaitForSeconds(0.4f);
            Frame();
            Assert.That(float.Parse(slot.StateLabel.text, CultureInfo.InvariantCulture), Is.LessThan(float.Parse(frozenText, CultureInfo.InvariantCulture)),
                "running again, the number counts down");
            Assert.That(slot.CooldownOverlay.fillAmount, Is.LessThan(frozenFill), "and the fill drains");
        }
    }
}
#endif
