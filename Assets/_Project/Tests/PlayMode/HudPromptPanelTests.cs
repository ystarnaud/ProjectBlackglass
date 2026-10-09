#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.DualShock.LowLevel;
using UnityEngine.InputSystem.Switch;
using UnityEngine.InputSystem.XInput;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Blackglass.Tests
{
    /// <summary>
    /// The prompt panel driven straight from a snapshot: one chip row per entry, pooled rows, change-only writes, a chip
    /// that fits the longest real prompt of every input family, layout, and the source rule that all prompt text comes
    /// from the resolver.
    /// </summary>
    public class HudPromptPanelTests
    {
        const string TerminalName = "Terminal Kappa";
        static readonly InputFamily[] Families =
        {
            InputFamily.Xbox, InputFamily.PlayStation, InputFamily.Nintendo, InputFamily.GenericGamepad, InputFamily.KeyboardMouse,
        };

        readonly List<Object> created = new List<Object>();
        TacticalHud hud;
        RectTransform root;
        InputActionAsset actions;

        [SetUp]
        public void SetUp() => actions = TestControls.Load();

        [TearDown]
        public void TearDown()
        {
            foreach (var item in created)
            {
                if (item != null)
                    Object.DestroyImmediate(item);
            }
            created.Clear();
            TestControls.Reset(actions);
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

        static HudPromptEntry Entry(string label, string prompt) => new HudPromptEntry { Label = label, Prompt = prompt };

        /// <summary>The fullest list for a family: a terminal in reach while paused (six entries, the cap).</summary>
        List<HudPromptEntry> Fullest(InputFamily family)
        {
            var list = new List<HudPromptEntry>();
            HudPrompts.Build(new HudPromptContext { Paused = true, TerminalName = TerminalName }, family, actions, list);
            return list;
        }

        /// <summary>Every list any context can produce for a family.</summary>
        IEnumerable<List<HudPromptEntry>> EveryList(InputFamily family)
        {
            foreach (var paused in new[] { false, true })
            foreach (var armed in new[] { false, true })
            foreach (var terminal in new[] { null, TerminalName })
            {
                var list = new List<HudPromptEntry>();
                HudPrompts.Build(new HudPromptContext { Paused = paused, AbilityArmed = armed, TerminalName = terminal }, family, actions, list);
                yield return list;
            }
        }

        void Show(IList<HudPromptEntry> entries)
        {
            hud.Snapshot.Prompts.Clear();
            hud.Snapshot.Prompts.AddRange(entries);
            hud.ApplySnapshot();
        }

        void AssertShows(IList<HudPromptEntry> entries)
        {
            var panel = hud.Prompts;
            Assert.That(entries.Count, Is.LessThanOrEqualTo(panel.RowCapacity));
            for (var i = 0; i < panel.RowCapacity; i++)
            {
                if (i >= entries.Count)
                {
                    Assert.That(panel.Row(i).activeSelf, Is.False, $"unused row {i}");
                    continue;
                }
                Assert.That(panel.Row(i).activeSelf, Is.True, $"row {i}");
                Assert.That(panel.Chip(i).text, Is.EqualTo(entries[i].Prompt), $"chip {i}");
                Assert.That(panel.Label(i).text, Is.EqualTo(entries[i].Label), $"label {i}");
            }
        }

        // ---- rows ----

        [Test]
        public void Build_HasSixRowsInThePromptZone_AndNothingShowsWithoutPrompts()
        {
            Build();
            var panel = hud.Prompts;
            Assert.That(panel.Root, Is.SameAs(Panel("Prompts")));
            Assert.That(panel.RowCapacity, Is.EqualTo(6));
            hud.ApplySnapshot();
            for (var i = 0; i < panel.RowCapacity; i++)
                Assert.That(panel.Row(i).activeSelf, Is.False, $"row {i}");
            Assert.That(panel.Background.gameObject.activeSelf, Is.False, "an empty list draws no box");
        }

        [Test]
        public void Rows_MatchTheSnapshot_AndUnusedRowsAreHidden()
        {
            Build();
            var entries = new List<HudPromptEntry> { Entry("Order", "Left Mouse"), Entry("Pause", "Space"), Entry("Switch", "Tab / Q") };
            Show(entries);
            AssertShows(entries);
            Assert.That(hud.Prompts.Background.gameObject.activeSelf, Is.True);

            entries.RemoveAt(2);
            Show(entries);
            AssertShows(entries);

            entries.Clear();
            Show(entries);
            AssertShows(entries);
            Assert.That(hud.Prompts.Background.gameObject.activeSelf, Is.False);

            for (var i = 0; i < 9; i++)
                entries.Add(Entry("Label " + i, "P" + i));
            Assert.DoesNotThrow(() => Show(entries), "more entries than rows are dropped");
            AssertShows(entries.Take(6).ToList());
        }

        [Test]
        public void ChangingTheFamilyPrompts_ChangesTheChips_WithoutRecreatingRows()
        {
            Build();
            var panel = hud.Prompts;
            var texts = new List<Text>();
            for (var i = 0; i < panel.RowCapacity; i++)
            {
                texts.Add(panel.Chip(i));
                texts.Add(panel.Label(i));
            }
            var graphics = root.GetComponentsInChildren<Graphic>(true).Length;

            var xbox = Fullest(InputFamily.Xbox);
            Show(xbox);
            AssertShows(xbox);
            foreach (var family in new[] { InputFamily.PlayStation, InputFamily.Nintendo, InputFamily.KeyboardMouse })
            {
                var list = Fullest(family);
                Show(list);
                AssertShows(list);
                if (family != InputFamily.Nintendo)
                    Assert.That(list.Select(e => e.Prompt), Is.Not.EqualTo(xbox.Select(e => e.Prompt)), $"{family} reads differently from Xbox");
            }

            var again = new List<Text>();
            for (var i = 0; i < panel.RowCapacity; i++)
            {
                again.Add(panel.Chip(i));
                again.Add(panel.Label(i));
            }
            Assert.That(again.Count, Is.EqualTo(texts.Count));
            for (var i = 0; i < texts.Count; i++)
                Assert.That(again[i], Is.SameAs(texts[i]), $"text {i} is reused");
            Assert.That(root.GetComponentsInChildren<Graphic>(true).Length, Is.EqualTo(graphics), "no rows or graphics were created");
        }

        [Test]
        public void AnIdenticalApply_WritesNoText_AndAChangeWritesOnlyWhatChanged()
        {
            Build();
            var entries = Fullest(InputFamily.PlayStation);
            Show(entries);
            var before = HudFactory.TextWrites;
            hud.ApplySnapshot();
            hud.ApplySnapshot();
            Assert.That(HudFactory.TextWrites, Is.EqualTo(before), "an unchanged snapshot writes no text");

            var copy = entries.ToList();
            copy[2] = Entry(copy[2].Label, "Different");
            Show(copy);
            Assert.That(HudFactory.TextWrites, Is.EqualTo(before + 1), "one changed chip writes one text");
        }

        [Test]
        public void AFullPanelApply_AllocatesNothingOnceWarm()
        {
            Build();
            Show(Fullest(InputFamily.Xbox));
            hud.ApplySnapshot();
            var start = System.GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 20; i++)
                hud.ApplySnapshot();
            Assert.That(System.GC.GetAllocatedBytesForCurrentThread() - start, Is.LessThanOrEqualTo(0L));
        }

        [Test]
        public void ThePanel_IsNotAnInteractiveElement()
        {
            Build();
            Show(Fullest(InputFamily.Xbox));
            var graphics = hud.Prompts.Root.GetComponentsInChildren<Graphic>(true);
            Assert.That(graphics, Is.Not.Empty);
            foreach (var graphic in graphics)
                Assert.That(graphic.raycastTarget, Is.False, $"{graphic.name} must not catch pointer raycasts");
            Assert.That(hud.Prompts.Root.GetComponentsInChildren<Selectable>(true), Is.Empty);
        }

        [Test]
        public void TheFamilyNameIsNeverShown()
        {
            Build();
            Show(Fullest(InputFamily.PlayStation));
            var shown = string.Join("|", hud.Prompts.Root.GetComponentsInChildren<Text>(true).Select(t => t.text));
            foreach (var family in Families)
                Assert.That(shown, Does.Not.Contain(family.ToString()), $"{family}");
        }

        // ---- fit and layout ----

        [Test]
        public void EveryRealPrompt_FitsItsChipOnOneLine_ForEveryFamily()
        {
            Build();
            var panel = hud.Prompts;
            var longest = "";
            var widest = 0f;
            var widestText = "";
            foreach (var family in Families)
            {
                foreach (var list in EveryList(family))
                {
                    Show(list);
                    HudLayout.Rebuild(root);
                    for (var i = 0; i < list.Count; i++)
                    {
                        var chip = panel.Chip(i);
                        if (list[i].Prompt.Length > longest.Length)
                            longest = list[i].Prompt;
                        if (chip.preferredWidth > widest) { widest = chip.preferredWidth; widestText = list[i].Prompt; }
                        Assert.That(chip.preferredHeight, Is.LessThan(chip.fontSize * 1.8f), $"{family} '{list[i].Prompt}' wraps in its chip");
                        Assert.That(chip.preferredWidth, Is.LessThanOrEqualTo(chip.rectTransform.rect.width + 0.5f),
                            $"{family} '{list[i].Prompt}' is wider than its chip");
                        HudLayout.AssertInside(panel.ChipBox(i), chip.rectTransform);
                        HudLayout.AssertInside(panel.Root, panel.ChipBox(i), panel.Label(i).rectTransform);
                        HudLayout.AssertNoOverlap(panel.ChipBox(i), panel.Label(i).rectTransform);
                        var label = panel.Label(i);
                        var terminalOnWidePad = family == InputFamily.GenericGamepad && list[i].Label.StartsWith("Interact");
                        if (!terminalOnWidePad)   // the widest chord leaves too little room for a long terminal name; it truncates
                            Assert.That(label.preferredHeight, Is.LessThan(label.fontSize * 1.8f), $"{family} label '{list[i].Label}' wraps");
                    }
                }
            }
            Assert.That(longest.Length, Is.GreaterThan(5), "the sweep found real prompts");
            Debug.Log($"Longest prompt text: '{longest}', widest '{widestText}' = {widest}");
        }

        [TestCase(1920, 1080, TestName = "Prompts_Layout_At1920x1080_16x9")]
        [TestCase(1920, 1200, TestName = "Prompts_Layout_At1920x1200_16x10")]
        [TestCase(2560, 1080, TestName = "Prompts_Layout_At2560x1080_21x9")]
        public void Layout_StaysInTheFootprint_AndDoesNotOverlapThePanels(int screenWidth, int screenHeight)
        {
            var units = HudLayout.CanvasUnits(screenWidth, screenHeight);
            Build(units.x, units.y);
            var s = hud.Snapshot;
            FillOtherPanels(s);
            var worst = Fullest(InputFamily.Xbox);
            foreach (var family in Families)
            {
                foreach (var list in EveryList(family))
                {
                    if (list.Count > worst.Count || (list.Count == worst.Count && list.Sum(e => e.Prompt.Length) > worst.Sum(e => e.Prompt.Length)))
                        worst = list;
                }
            }
            s.Prompts.AddRange(worst);
            hud.ApplySnapshot();
            HudLayout.Rebuild(root);

            var panel = hud.Prompts;
            var prompts = Panel("Prompts");
            Assert.That(HudLayout.WorldRect(prompts).size, Is.EqualTo(new Vector2(380f, 220f)), "the footprint is unchanged");
            var rows = Enumerable.Range(0, worst.Count).Select(i => (RectTransform)panel.Row(i).transform).ToArray();
            Assert.That(rows.Length, Is.EqualTo(6));
            HudLayout.AssertInside(prompts, rows);
            HudLayout.AssertNoOverlap(rows);
            HudLayout.AssertInside(prompts, panel.Background);

            var status = hud.Status;
            HudLayout.AssertInside(root, prompts);
            HudLayout.AssertNoOverlap(Panel("Objectives"), status.PauseBanner, status.ResultBanner, status.RightBlock,
                Panel("Squad"), Panel("Operative"), prompts, Panel("Target"));
        }

        static void FillOtherPanels(HudSnapshot s)
        {
            s.Clear();
            s.HasMission = true;
            s.PhaseText = "extraction open";
            s.BannerText = MissionHudText.Banner(MissionPhase.Success);
            for (var i = 0; i < 8; i++)
                s.Objectives.Add(new HudObjectiveRow { Kind = HudObjectiveKind.Active, Text = "Download the security logs at Terminal Kappa " + i + " (45%)" });
            s.Extraction = HudExtractionState.Active;
            s.ExtractionInside = 3;
            s.ExtractionRequired = 4;
            s.IsPaused = true;
            s.ResumePrompt = "Options";
            s.HasFollow = true;
            s.FollowOn = true;
            for (var i = 0; i < 6; i++)
                s.Squad.Add(new HudSquadCard { Name = "Operative Number " + i, Role = "Ranged", Initials = "ON", Tag = "FOLLOWING", Rank = 3, Health = 80, MaxHealth = 100 });
            for (var i = 0; i < 4; i++)
                s.Abilities.Add(new HudAbilitySlot { Slot = i, Prompt = "RT + Up", Name = "Suppressing Fire", State = HudAbilityState.Cooldown, Remaining = 4.3f, Fraction = 0.5f });
            for (var i = 0; i < 8; i++)
                s.Queue.Add(new HudCommandStep { Number = i + 1, Text = "Attack (target lost)", IsCurrent = i == 0 });
            s.QueueHidden = 3;
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
            s.Target = new HudTarget { Visible = true, Name = "Hostile Rifleman", Detail = "Ranged", Tag = "ATTACKING", CoverText = "Cover 65%", Health = 60, MaxHealth = 100 };
        }

        // ---- source rule ----

        [Test]
        public void HudSource_HasNoHardCodedPromptText_AndNoDeviceNames()
        {
            string[] promptLiterals = { "\"Press ", "\"Left Click", "\"Space\"", "\"Tab\"" };
            string[] forbidden =
            {
                "Gamepad.current", "Keyboard.current", "Mouse.current", "Pointer.current",
                "buttonSouth", "buttonEast", "buttonWest", "buttonNorth",
                "XInputController", "DualShock", "DualSense", "SwitchPro",
            };
            var folder = Path.Combine(Application.dataPath, "_Project", "Scripts", "Hud");
            var files = Directory.GetFiles(folder, "*.cs");
            Assert.That(files.Length, Is.GreaterThan(10));
            foreach (var file in files)
            {
                var code = StripComments(File.ReadAllText(file));
                foreach (var literal in promptLiterals.Concat(forbidden))
                    Assert.That(code, Does.Not.Contain(literal), $"{Path.GetFileName(file)} contains {literal}");
            }
        }

        [Test]
        public void StripComments_RemovesLineBlockAndDocComments_AndKeepsCode()
        {
            var code = StripComments("var a = \"Tab\"; // \"Space\"\n/* \"Tab\" */ var b = 1;\n/// <summary>\"Space\"</summary>\nvar c = 2;");
            Assert.That(code, Does.Contain("\"Tab\""));
            Assert.That(code, Does.Not.Contain("\"Space\""));
            Assert.That(code, Does.Contain("var b = 1;"));
            Assert.That(code, Does.Contain("var c = 2;"));
        }

        static string StripComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            return Regex.Replace(source, @"//[^\r\n]*", string.Empty);
        }
    }

    /// <summary>
    /// The prompt panel on a live HUD: switching the active input family (simulated devices) changes the chips on the
    /// next LateUpdate, with no restart.
    /// </summary>
    public class HudPromptHotSwitchTests : InputTestFixture
    {
        InputActionAsset actions;
        GameObject host;
        GameObject hudObject;
        GameObject pauseObject;
        ActiveInputDevice device;
        TacticalPause pause;
        TacticalHud hud;

        public override void Setup()
        {
            base.Setup();
            InputSystem.AddDevice<Mouse>();
            InputSystem.AddDevice<Keyboard>();
            actions = TestControls.Load();
            host = new GameObject("InputDevice");
            host.SetActive(false);
            device = host.AddComponent<ActiveInputDevice>();
            device.Initialize(actions);
            host.SetActive(true);
            pauseObject = new GameObject("Pause");
            pause = pauseObject.AddComponent<TacticalPause>();
            pause.Pause();
            hudObject = new GameObject("hud");
            hudObject.SetActive(false);
            hud = hudObject.AddComponent<TacticalHud>();
            hud.Initialize(new HudSources { tacticalPause = pause, inputDevice = device, controls = actions }, null);
            hudObject.SetActive(true);
        }

        public override void TearDown()
        {
            Object.DestroyImmediate(hudObject);
            Object.DestroyImmediate(host);
            Object.DestroyImmediate(pauseObject);
            foreach (var eventSystem in Object.FindObjectsByType<UnityEngine.EventSystems.EventSystem>(FindObjectsSortMode.None))
                Object.DestroyImmediate(eventSystem.gameObject);
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator Wake(Gamepad pad)
        {
            PressSelect(pad, true);
            yield return null;
            PressSelect(pad, false);
            yield return null;
        }

        // The DualSense layout discards delta state events, so queue a whole report for it.
        void PressSelect(Gamepad pad, bool down)
        {
            if (pad is DualSenseGamepadHID)
                InputSystem.QueueStateEvent(pad, new DualSenseHIDInputReport
                {
                    leftStickX = 128, leftStickY = 128, rightStickX = 128, rightStickY = 128,
                    buttons0 = 8, buttons1 = (byte)(down ? 1 << 4 : 0),
                });
            else if (down)
                Press(pad.selectButton);
            else
                Release(pad.selectButton);
        }

        List<HudPromptEntry> Expected(InputFamily family)
        {
            var list = new List<HudPromptEntry>();
            HudPrompts.Build(new HudPromptContext { Paused = true }, family, actions, list);
            return list;
        }

        void AssertChips(InputFamily family)
        {
            var expected = Expected(family);
            Assert.That(expected, Is.Not.Empty);
            var panel = hud.Prompts;
            for (var i = 0; i < panel.RowCapacity; i++)
            {
                Assert.That(panel.Row(i).activeSelf, Is.EqualTo(i < expected.Count), $"{family} row {i}");
                if (i >= expected.Count)
                    continue;
                Assert.That(panel.Chip(i).text, Is.EqualTo(expected[i].Prompt), $"{family} chip {i}");
                Assert.That(panel.Label(i).text, Is.EqualTo(expected[i].Label), $"{family} label {i}");
            }
        }

        string CancelChip()
        {
            var panel = hud.Prompts;
            for (var i = 0; i < panel.RowCapacity; i++)
            {
                if (panel.Row(i).activeSelf && panel.Label(i).text == "Cancel")
                    return panel.Chip(i).text;
            }
            Assert.Fail("no Cancel chip");
            return null;
        }

        [UnityTest]
        public IEnumerator TheChips_FollowTheActiveFamily_OnTheNextLateUpdate()
        {
            var cancel = actions.FindAction("Commands/Cancel", true);
            yield return null;
            Assert.That(device.Family, Is.EqualTo(InputFamily.KeyboardMouse), "Precondition");
            AssertChips(InputFamily.KeyboardMouse);
            var keyboardCancel = CancelChip();
            Assert.That(keyboardCancel, Is.EqualTo(PromptResolver.GetPrompt(cancel, InputFamily.KeyboardMouse)));
            var rowTexts = Enumerable.Range(0, hud.Prompts.RowCapacity).Select(i => hud.Prompts.Chip(i)).ToArray();

            yield return Wake(InputSystem.AddDevice<XInputController>());
            yield return null;
            Assert.That(device.Family, Is.EqualTo(InputFamily.Xbox), "Precondition");
            AssertChips(InputFamily.Xbox);
            var xboxCancel = CancelChip();
            Assert.That(xboxCancel, Is.EqualTo(PromptResolver.GetPrompt(cancel, InputFamily.Xbox)));
            Assert.That(Expected(InputFamily.Xbox).Select(e => e.Prompt), Is.Not.EqualTo(Expected(InputFamily.KeyboardMouse).Select(e => e.Prompt)));

            yield return Wake(InputSystem.AddDevice<DualSenseGamepadHID>());
            yield return null;
            Assert.That(device.Family, Is.EqualTo(InputFamily.PlayStation), "Precondition");
            AssertChips(InputFamily.PlayStation);
            Assert.That(CancelChip(), Is.EqualTo(PromptResolver.GetPrompt(cancel, InputFamily.PlayStation)));
            Assert.That(CancelChip(), Is.Not.EqualTo(xboxCancel), "PlayStation reads differently from Xbox");

            yield return Wake(InputSystem.AddDevice<SwitchProControllerHID>());
            yield return null;
            Assert.That(device.Family, Is.EqualTo(InputFamily.Nintendo), "Precondition");
            AssertChips(InputFamily.Nintendo);
            Assert.That(CancelChip(), Is.EqualTo(PromptResolver.GetPrompt(cancel, InputFamily.Nintendo)), "Nintendo prints its own binding");

            Press(Keyboard.current.f12Key);
            yield return null;
            Release(Keyboard.current.f12Key);
            yield return null;
            Assert.That(device.Family, Is.EqualTo(InputFamily.KeyboardMouse), "Precondition");
            AssertChips(InputFamily.KeyboardMouse);
            Assert.That(CancelChip(), Is.EqualTo(keyboardCancel));
            Assert.That(CancelChip(), Is.Not.EqualTo(xboxCancel), "keyboard reads differently from Xbox");

            for (var i = 0; i < rowTexts.Length; i++)
                Assert.That(hud.Prompts.Chip(i), Is.SameAs(rowTexts[i]), "rows survive every switch");
        }
    }
}
#endif
