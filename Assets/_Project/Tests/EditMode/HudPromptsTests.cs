using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    public class HudPromptsTests
    {
        const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        static readonly InputFamily[] PadFamilies = { InputFamily.Xbox, InputFamily.PlayStation, InputFamily.Nintendo, InputFamily.GenericGamepad };

        static InputActionAsset Controls => AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);

        static string Resolve(string actionPath, InputFamily family) =>
            PromptResolver.GetPrompt(Controls.FindAction(actionPath, throwIfNotFound: true), family);

        static List<HudPromptEntry> Build(HudPromptContext context, InputFamily family)
        {
            var entries = new List<HudPromptEntry>();
            HudPrompts.Build(context, family, Controls, entries);
            return entries;
        }

        static string PromptOf(List<HudPromptEntry> entries, string label) => entries.First(entry => entry.Label == label).Prompt;

        static string[] Labels(List<HudPromptEntry> entries) => entries.Select(entry => entry.Label).ToArray();

        [Test]
        public void RealTime_WithoutATerminal_ListsTheCoreOrders()
        {
            var entries = Build(default, InputFamily.KeyboardMouse);

            Assert.That(Labels(entries), Is.SupersetOf(new[] { "Order", "Pause", "Switch", "Follow", "Stop" }));
            Assert.That(Labels(entries), Does.Not.Contain("Interact"));
            foreach (var entry in entries)
            {
                Assert.That(entry.Prompt, Is.Not.Empty, entry.Label);
                Assert.That(entry.Prompt, Is.Not.EqualTo("-"), entry.Label);
            }
            Assert.That(PromptOf(entries, "Order"), Is.EqualTo(Resolve("Commands/Command", InputFamily.KeyboardMouse)));
        }

        [Test]
        public void RealTime_NextToATerminal_PutsInteractFirst()
        {
            var context = new HudPromptContext { TerminalName = "Terminal" };

            var keyboard = Build(context, InputFamily.KeyboardMouse);
            Assert.That(keyboard[0].Label, Is.EqualTo("Interact Terminal"));
            Assert.That(keyboard[0].Prompt, Is.EqualTo(Resolve("Commands/Interact", InputFamily.KeyboardMouse)));

            foreach (var family in PadFamilies)
            {
                var pad = Build(context, family);
                Assert.That(pad[0].Label, Is.EqualTo("Interact Terminal"), family.ToString());
                Assert.That(pad[0].Prompt, Is.EqualTo(Resolve("Commands/Confirm", family)), family.ToString());
            }
        }

        [Test]
        public void Paused_ListsResumeOrderQueueCancelSwitch()
        {
            var entries = Build(new HudPromptContext { Paused = true }, InputFamily.KeyboardMouse);
            Assert.That(Labels(entries), Is.SupersetOf(new[] { "Resume", "Order", "Queue", "Cancel", "Switch" }));
            Assert.That(PromptOf(entries, "Resume"), Is.EqualTo(Resolve("Commands/ToggleTacticalPause", InputFamily.KeyboardMouse)));
        }

        [Test]
        public void ArmedAbility_WinsOverPaused()
        {
            var entries = Build(new HudPromptContext { Paused = true, AbilityArmed = true }, InputFamily.Xbox);
            Assert.That(Labels(entries), Is.SupersetOf(new[] { "Cast", "Cancel", "Queue" }));
            Assert.That(Labels(entries), Does.Not.Contain("Resume"));
            Assert.That(PromptOf(entries, "Cast"), Is.EqualTo(Resolve("Commands/Confirm", InputFamily.Xbox)));
        }

        [Test]
        public void CancelPrompt_FollowsTheFamilysBindings()
        {
            var context = new HudPromptContext { Paused = true };
            var xbox = PromptOf(Build(context, InputFamily.Xbox), "Cancel");
            var playStation = PromptOf(Build(context, InputFamily.PlayStation), "Cancel");
            var nintendo = PromptOf(Build(context, InputFamily.Nintendo), "Cancel");
            var keyboard = PromptOf(Build(context, InputFamily.KeyboardMouse), "Cancel");

            foreach (var family in new[] { InputFamily.KeyboardMouse }.Concat(PadFamilies))
                Assert.That(PromptOf(Build(context, family), "Cancel"), Is.EqualTo(Resolve("Commands/Cancel", family)), family.ToString());

            // Nintendo labels the east button "B" like Xbox does (PromptResolverTests): the physical swap lives in the bindings,
            // so the text matches Xbox while the resolver stays the single source of truth (checked above).
            Assert.That(nintendo, Is.EqualTo("B"));
            Assert.That(playStation, Is.Not.EqualTo(xbox));
            Assert.That(keyboard, Is.Not.EqualTo(xbox));
        }

        [Test]
        public void TheList_NeverExceedsSixEntries()
        {
            var contexts = new[]
            {
                default(HudPromptContext),
                new HudPromptContext { TerminalName = "Terminal" },
                new HudPromptContext { Paused = true },
                new HudPromptContext { Paused = true, TerminalName = "Terminal" },
                new HudPromptContext { AbilityArmed = true, TerminalName = "Terminal" },
            };
            foreach (var family in new[] { InputFamily.KeyboardMouse }.Concat(PadFamilies))
            {
                foreach (var context in contexts)
                    Assert.That(Build(context, family).Count, Is.InRange(1, 6), family.ToString());
            }
        }

        [Test]
        public void AbilityPrompt_ReadsTheSlotsBinding()
        {
            Assert.That(HudPrompts.AbilityPrompt(0, InputFamily.Xbox, Controls), Is.EqualTo(Resolve("Commands/Ability1", InputFamily.Xbox)));
            Assert.That(HudPrompts.AbilityPrompt(0, InputFamily.Xbox, Controls), Does.Contain(" + "), "a chord such as RT + D-pad Up");
            Assert.That(HudPrompts.AbilityPrompt(1, InputFamily.KeyboardMouse, Controls), Is.EqualTo("2"));
        }

        [Test]
        public void AnEntryWhosePathsAllResolveToNothing_IsSkipped()
        {
            // Only the pause action exists, bound for keyboard: every other entry resolves to "-" and must not be listed.
            var asset = UnityEngine.ScriptableObject.CreateInstance<InputActionAsset>();
            try
            {
                var map = asset.AddActionMap("Commands");
                map.AddAction("ToggleTacticalPause").AddBinding("<Keyboard>/space", groups: "KeyboardMouse");
                var entries = new List<HudPromptEntry>();

                HudPrompts.Build(default, InputFamily.KeyboardMouse, asset, entries);

                Assert.That(Labels(entries), Is.EqualTo(new[] { "Pause" }));
                Assert.That(entries[0].Prompt, Is.Not.EqualTo("-").And.Not.Empty);

                HudPrompts.Build(default, InputFamily.Xbox, asset, entries);
                Assert.That(entries, Is.Empty, "nothing is bound for the pad");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void NullControls_GiveNothingAndDoNotThrow()
        {
            var entries = new List<HudPromptEntry> { new HudPromptEntry { Label = "stale", Prompt = "x" } };
            Assert.DoesNotThrow(() => HudPrompts.Build(new HudPromptContext { TerminalName = "T" }, InputFamily.Xbox, null, entries));
            Assert.That(entries, Is.Empty);
            Assert.That(HudPrompts.AbilityPrompt(2, InputFamily.Xbox, null), Is.EqualTo("3"));
        }
    }
}
