#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Switch;
using UnityEngine.InputSystem.XInput;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The snapshot builder against real gameplay rigs: what the tactical HUD would show, and above all what it must not
    /// (a hostile the player has not observed never reaches the snapshot by name, health, position or target line).
    /// </summary>
    public class HudSnapshotBuilderTests : InputTestFixture
    {
        const string SecretName = "SecretHostile";

        Mouse mouse;
        InputActionAsset actions;
        HudSnapshotBuilder builder;
        HudSnapshot snapshot;
        MissionRig missionRig;
        IntelRig intelRig;
        AbilityRig abilityRig;
        OperativeKit kit;
        TestWorld world;
        SquadRoster roster;
        IntelligenceService service;

        public override void Setup()
        {
            base.Setup();
            mouse = InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
            builder = new HudSnapshotBuilder();
            snapshot = new HudSnapshot();
            missionRig = null;
            intelRig = null;
            abilityRig = null;
            kit = null;
            world = null;
            roster = null;
            service = null;
        }

        public override void TearDown()
        {
            missionRig?.Dispose();
            intelRig?.Dispose();
            abilityRig?.World.Dispose();
            world?.Dispose();
            kit?.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        void Build(HudSources sources, float unscaledTime = 0f) => builder.Build(sources, snapshot, unscaledTime);

        // ---- rigs ----

        IEnumerator StartMission(IntelligenceSettings intelligence = null, MissionSettings settings = null, bool withRoster = true)
        {
            missionRig = new MissionRig(settings);
            if (withRoster)
            {
                kit = OperativeKit.Build();
                roster = missionRig.AddRoster(kit.Definitions, kit.Track);
            }
            if (intelligence != null)
                service = missionRig.AddIntelligence(intelligence);
            yield return missionRig.Generate(12345);
            Assert.That(missionRig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", missionRig.Director.Report.Failures));
            foreach (var hostile in missionRig.Director.Hostiles)
                hostile.GetComponent<EnemyAI>().enabled = false;
        }

        HudSources MissionSources() => new HudSources
        {
            activeCharacter = missionRig.Active, selection = missionRig.Selection, tacticalPause = missionRig.Pause,
            encounter = missionRig.Encounter, director = missionRig.Director, roster = roster, intelligence = service, controls = actions,
        };

        HudSources AbilitySources() => new HudSources
        {
            activeCharacter = abilityRig.Active, selection = abilityRig.Selection, tacticalPause = abilityRig.Pause,
            encounter = abilityRig.Encounter, abilityTargeting = abilityRig.Targeting, commandInput = abilityRig.Input,
            controls = actions, camera = abilityRig.ViewCamera,
        };

        // The intel rig's friendly as the controlled unit, plus the sources that read it.
        HudSources IntelSources(ActiveCharacter active) => new HudSources
        {
            activeCharacter = active, encounter = intelRig.Encounter, intelligence = intelRig.Service, controls = actions,
        };

        Health AddSecretHostile(Vector3 ground)
        {
            var secret = intelRig.AddHostile(ground);
            secret.name = SecretName;
            return secret;
        }

        // A camera, selection, active character, cursor and command input around the intel rig's friendly, and optionally
        // ability targeting. None of them knows about the intelligence service: the HUD must filter on its own.
        (Camera, UnitSelection, ActiveCharacter, TacticalCursor, PlayerCommandInput, AbilityTargeting) WireIntelInput(bool withTargeting = false)
        {
            var cameraObject = intelRig.World.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            var viewCamera = cameraObject.AddComponent<Camera>();
            var systems = intelRig.World.Track(new GameObject("Systems"));
            systems.SetActive(false);
            var selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(intelRig.Friendly);
            var active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(intelRig.Friendly.Unit, null, selection);
            var cursor = systems.AddComponent<TacticalCursor>();
            cursor.Initialize(viewCamera, active, selection, intelRig.Encounter, null, null, null, null, null, null);
            AbilityTargeting targeting = null;
            if (withTargeting)
            {
                // No cursor here: armed, it would take over the pointer (Aiming makes it active without an input device).
                targeting = systems.AddComponent<AbilityTargeting>();
                targeting.Initialize(viewCamera, selection, active, null,
                    TestControls.Ref(actions, "Commands/PointerPosition"), TestControls.Ref(actions, "Commands/QueueModifier"),
                    TestControls.Ref(actions, "Commands/Ability1"), TestControls.Ref(actions, "Commands/Ability2"),
                    TestControls.Ref(actions, "Commands/Ability3"), TestControls.Ref(actions, "Commands/Ability4"));
            }
            var input = systems.AddComponent<PlayerCommandInput>();
            input.Initialize(viewCamera, selection, null,
                TestControls.Ref(actions, "Commands/Command"), TestControls.Ref(actions, "Commands/PointerPosition"),
                TestControls.Ref(actions, "Commands/ToggleTacticalPause"), TestControls.Ref(actions, "Commands/QueueModifier"),
                TestControls.Ref(actions, "Commands/Stop"), TestControls.Ref(actions, "Commands/Cancel"), active);
            systems.SetActive(true);
            return (viewCamera, selection, active, cursor, input, targeting);
        }

        HudSources InputSources(Camera viewCamera, UnitSelection selection, ActiveCharacter active, TacticalCursor cursor,
            PlayerCommandInput input, AbilityTargeting targeting) => new HudSources
        {
            activeCharacter = active, selection = selection, encounter = intelRig.Encounter, intelligence = intelRig.Service,
            cursor = cursor, commandInput = input, abilityTargeting = targeting, camera = viewCamera, controls = actions,
        };

        // From the north end of room A the corridor is out of line: a hostile seen through it is lost (see the geometry tests).
        void LookAwayFromTheCorridor()
        {
            intelRig.Friendly.transform.position = new Vector3(-6.5f, 1f, 1.5f);
            Physics.SyncTransforms();
            intelRig.Service.RunPass();
        }

        // ---- leak checks ----

        static string AllText(HudSnapshot s)
        {
            var text = new StringBuilder();
            void Add(string value) => text.Append(value).Append('\n');
            foreach (var card in s.Squad) { Add(card.Name); Add(card.Role); Add(card.Initials); Add(card.Tag); }
            foreach (var slot in s.Abilities) { Add(slot.Prompt); Add(slot.Name); }
            foreach (var step in s.Queue) Add(step.Text);
            foreach (var row in s.Objectives) Add(row.Text);
            foreach (var prompt in s.Prompts) { Add(prompt.Label); Add(prompt.Prompt); }
            foreach (var mark in s.Marks) { Add(mark.Text); Add(mark.World.ToString("F2")); }
            Add(s.PhaseText); Add(s.BannerText); Add(s.ResumePrompt);
            Add(s.ControlledName); Add(s.ControlledRole); Add(s.ControlledCover);
            Add(s.QueueOwner); Add(s.CasterName); Add(s.ArmedLine);
            Add(s.Target.Name); Add(s.Target.Detail); Add(s.Target.Tag); Add(s.Target.CoverText);
            return text.ToString();
        }

        void AssertNoTraceOf(Health hidden)
        {
            Assert.That(AllText(snapshot), Does.Not.Contain(hidden.name), "the hidden hostile's name");
            Assert.That(snapshot.Target.Unit, Is.Not.SameAs(hidden));
            var position = hidden.transform.position;
            foreach (var mark in snapshot.Marks)
            {
                Assert.That(Vector3.Distance(mark.World, position), Is.GreaterThan(1f), $"a {mark.Kind} mark at the hidden hostile");
                Assert.That(Vector3.Distance(mark.World, position + Vector3.up * 2.3f), Is.GreaterThan(1f), $"a {mark.Kind} mark above it");
            }
        }

        static string Invariant1(float value) => value.ToString("0.0", CultureInfo.InvariantCulture);

        // ---- squad ----

        [UnityTest]
        public IEnumerator Squad_HasOneCardPerFriendly_WithNameRoleRankAndHealth()
        {
            yield return StartMission();
            var sources = MissionSources();
            var friendlies = missionRig.Director.Friendlies;

            Build(sources);

            Assert.That(snapshot.Squad, Has.Count.EqualTo(3));
            for (var i = 0; i < 3; i++)
            {
                var card = snapshot.Squad[i];
                var definition = kit.Definitions[i];
                var health = friendlies[i].GetComponent<Health>();
                Assert.That(card.Unit, Is.SameAs(friendlies[i]));
                Assert.That(card.Name, Is.EqualTo(friendlies[i].GetComponent<UnitIdentity>().DisplayName));
                Assert.That(card.Name, Is.EqualTo(definition.DisplayName));
                Assert.That(card.Name, Does.Not.Contain(definition.Id), "never the operative id");
                Assert.That(card.Role, Is.EqualTo(definition.Role.DisplayName));
                Assert.That(card.Rank, Is.EqualTo(roster.Rank(roster.Members[i])).And.GreaterThan(0));
                Assert.That(card.Initials, Is.EqualTo(HudText.Initials(card.Name)));
                Assert.That(card.Health, Is.EqualTo(health.Current));
                Assert.That(card.MaxHealth, Is.EqualTo(health.Max));
                Assert.That(card.IsDown, Is.False);
            }

            var victim = friendlies[2].GetComponent<Health>();
            victim.TakeDamage(10);
            Build(sources);
            Assert.That(snapshot.Squad[2].Health, Is.EqualTo(victim.Max - 10), "the next build shows the damage");

            victim.TakeDamage(10000);
            yield return null;
            Build(sources);
            Assert.That(snapshot.Squad, Has.Count.EqualTo(3), "a dead operative keeps its card");
            Assert.That(snapshot.Squad[2].IsDown, Is.True);
            Assert.That(snapshot.Squad[2].Health, Is.Zero);
            Assert.That(snapshot.Squad[2].Tag, Is.Empty, "no companion tag on a dead unit");
        }

        [UnityTest]
        public IEnumerator Controlled_And_Selected_AreDistinct()
        {
            yield return StartMission();
            var sources = MissionSources();
            var friendlies = missionRig.Director.Friendlies;
            Assert.That(missionRig.Active.Unit, Is.SameAs(friendlies[0]), "Precondition");

            missionRig.Selection.Select(friendlies[1].GetComponent<SelectableUnit>());
            Build(sources);

            Assert.That(snapshot.Squad.Select(c => c.IsControlled), Is.EqualTo(new[] { true, false, false }));
            Assert.That(snapshot.Squad.Select(c => c.IsSelected), Is.EqualTo(new[] { false, true, false }));
            Assert.That(snapshot.Squad[0].Tag, Is.Empty, "the controlled unit carries no companion tag");
            Assert.That(snapshot.Squad[1].Tag, Is.Not.Empty);
            Assert.That(snapshot.Squad[2].Tag, Is.Not.Empty);

            Assert.That(snapshot.HasControlled, Is.True);
            Assert.That(snapshot.ControlledName, Is.EqualTo(kit.Definitions[0].DisplayName));
            Assert.That(snapshot.ControlledRole, Is.EqualTo(kit.Definitions[0].Role.DisplayName));
            Assert.That(snapshot.ControlledRank, Is.EqualTo(snapshot.Squad[0].Rank));
            Assert.That(snapshot.ControlledHealth, Is.EqualTo(snapshot.Squad[0].Health));
            Assert.That(snapshot.ControlledMaxHealth, Is.EqualTo(snapshot.Squad[0].MaxHealth));
            Assert.That(snapshot.ControlledCover, Is.Not.Empty);
            Assert.That(snapshot.HasFollow, Is.True);
            Assert.That(snapshot.FollowOn, Is.EqualTo(missionRig.Active.IsFollowOn));
        }

        [UnityTest]
        public IEnumerator Tags_FollowParkedAndFollowOff()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            var encounter = world.CreateEncounter();
            var selection = world.Track(new GameObject("Selection")).AddComponent<UnitSelection>();
            var leader = world.CreateFighter(Vector3.zero);
            leader.name = "Leader";
            var active = world.Track(new GameObject("ActiveCharacter")).AddComponent<ActiveCharacter>();
            active.Initialize(leader, null, selection);
            // As in CompanionAIPlayModeTests: everyone within 6 m of the leader, so nothing is ordered before the switch.
            var newLeader = world.CreateCompanion(new Vector3(0f, 0f, 5.5f), active, encounter);
            var far = world.CreateCompanion(new Vector3(0f, 0f, -5.5f), active, encounter);
            var near = world.CreateCompanion(new Vector3(2f, 0f, 4f), active, encounter);
            var members = new Component[] { leader, newLeader, far, near };
            selection.Initialize(members.Select(m => m.gameObject.AddComponent<SelectableUnit>()).ToArray());
            encounter.Initialize(members.Select(m => m.GetComponent<Health>()), new Health[0]);
            var sources = new HudSources { activeCharacter = active, selection = selection, encounter = encounter };
            yield return null;
            yield return null;

            Build(sources);
            Assert.That(snapshot.Squad.Select(c => c.Tag), Is.EqualTo(new[] { "", "FOLLOWING", "FOLLOWING", "FOLLOWING" }));

            active.SetFollow(false);
            Build(sources);
            Assert.That(snapshot.Squad.Select(c => c.Tag), Is.EqualTo(new[] { "", "ATTACHED", "ATTACHED", "ATTACHED" }));
            Assert.That(snapshot.FollowOn, Is.False);

            active.SetFollow(true);
            Assert.That(active.Cycle(1), Is.True, "Tab to the next roster unit");
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(far.IsParked, Is.True, "Precondition: 11 m from the new leader");

            Build(sources);
            Assert.That(snapshot.Squad[1].IsControlled, Is.True);
            Assert.That(snapshot.Squad[1].Tag, Is.Empty);
            Assert.That(snapshot.Squad[2].Tag, Is.EqualTo("PARKED"));
            Assert.That(snapshot.Squad[3].Tag, Is.EqualTo("FOLLOWING"));
            Assert.That(snapshot.Squad[0].Tag, Is.Empty, "the old leader has no CompanionAI");
        }

        [UnityTest]
        public IEnumerator Tags_ACompanionInOrderedCover_ReadsHolding()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            var encounter = world.CreateEncounter();
            var selection = world.Track(new GameObject("Selection")).AddComponent<UnitSelection>();
            var leader = world.CreateFighter(Vector3.zero);
            var active = world.Track(new GameObject("ActiveCharacter")).AddComponent<ActiveCharacter>();
            active.Initialize(leader, null, selection);
            var inCover = world.CreateCompanion(new Vector3(3f, 0f, 0f), active, encounter);
            var following = world.CreateCompanion(new Vector3(-3f, 0f, 0f), active, encounter);
            var members = new Component[] { leader, inCover, following };
            selection.Initialize(members.Select(m => m.gameObject.AddComponent<SelectableUnit>()).ToArray());
            encounter.Initialize(members.Select(m => m.GetComponent<Health>()), new Health[0]);
            var sources = new HudSources { activeCharacter = active, selection = selection, encounter = encounter };
            yield return null;
            yield return null;

            var ground = inCover.transform.position;
            ground.y = 0f;
            var point = world.CreateCoverPoint(ground, Vector3.forward, null);
            var cover = inCover.GetComponent<UnitCover>();
            cover.Initialize(world.CreateRegistry(point));
            Assert.That(cover.TryReserve(point) && cover.TryOccupy(), Is.True, "Precondition");
            Assert.That(cover.OccupiedByOrder, Is.True, "Precondition: an ordered occupancy");
            Assert.That(inCover.IsHeld || inCover.IsParked, Is.False, "Precondition: only the cover order holds it");

            Build(sources);
            Assert.That(snapshot.Squad[1].Tag, Is.EqualTo("HOLDING"));
            Assert.That(snapshot.Squad[2].Tag, Is.EqualTo("FOLLOWING"));
        }

        // ---- abilities ----

        [UnityTest]
        public IEnumerator Abilities_ShowCasterSlotsAndCooldownFill()
        {
            abilityRig = AbilityRig.Build(actions, false);
            yield return null;
            var sources = AbilitySources();

            Build(sources);
            Assert.That(snapshot.CasterName, Is.EqualTo(HudText.CleanName(abilityRig.Caster.name)));
            Assert.That(snapshot.Abilities.Select(a => a.Name), Is.EqualTo(new[] { "Aimed Shot", "Blast", "Mend" }));
            Assert.That(snapshot.Abilities.Select(a => a.Slot), Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(snapshot.Abilities.All(a => a.State == HudAbilityState.Ready && a.Remaining == 0f && a.Fraction == 0f), Is.True);
            for (var i = 0; i < 3; i++)
                Assert.That(snapshot.Abilities[i].Prompt, Is.EqualTo(HudPrompts.AbilityPrompt(i, InputFamily.KeyboardMouse, actions)));

            Assert.That(abilityRig.Abilities.TryUse(AbilityCommand.AtGround(abilityRig.Blast, AbilityRig.BlastGround)), Is.True,
                abilityRig.Abilities.LastFailure.ToString());
            Build(sources);

            var blast = snapshot.Abilities[1];
            Assert.That(blast.State, Is.EqualTo(HudAbilityState.Cooldown));
            Assert.That(blast.Remaining, Is.GreaterThan(0f));
            Assert.That(blast.Fraction, Is.GreaterThan(0f).And.LessThanOrEqualTo(1f));
            Assert.That(blast.Fraction, Is.EqualTo(blast.Remaining / abilityRig.Abilities.EffectiveCooldown(abilityRig.Blast)).Within(1e-5f));
            Assert.That(snapshot.Abilities[0].State, Is.EqualTo(HudAbilityState.Ready));

            abilityRig.CasterHealth.TakeDamage(10000);
            Build(sources);
            Assert.That(snapshot.Abilities.All(a => a.State == HudAbilityState.Unavailable), Is.True, "a dead caster can use nothing");
        }

        [UnityTest]
        public IEnumerator Cooldown_IsFrozenWhilePaused()
        {
            abilityRig = AbilityRig.Build(actions, false);
            yield return null;
            var sources = AbilitySources();
            Assert.That(abilityRig.Abilities.TryUse(AbilityCommand.AtGround(abilityRig.Blast, AbilityRig.BlastGround)), Is.True);

            abilityRig.Pause.Pause();
            Build(sources, Time.unscaledTime);
            Assert.That(snapshot.IsPaused, Is.True);
            var frozen = snapshot.Abilities[1].Remaining;
            for (var i = 0; i < 5; i++)
                yield return null;
            Build(sources, Time.unscaledTime);
            Assert.That(snapshot.Abilities[1].Remaining, Is.EqualTo(frozen), "scaled time stands still in tactical pause");

            abilityRig.Pause.Resume();
            yield return new WaitForSeconds(0.1f);
            Build(sources, Time.unscaledTime);
            Assert.That(snapshot.IsPaused, Is.False);
            Assert.That(snapshot.Abilities[1].Remaining, Is.LessThan(frozen));
        }

        [UnityTest]
        public IEnumerator ArmedAbility_ShowsAimingLine_AndMarksTheSlotArmed()
        {
            abilityRig = AbilityRig.Build(actions, false);
            yield return null;
            var sources = AbilitySources();
            Assert.That(abilityRig.Targeting.Arm(0), Is.True);
            Set(mouse.position, abilityRig.ScreenPointOf(abilityRig.Hostile.transform.position));
            yield return null;
            yield return null;
            var preview = abilityRig.Targeting.Preview;
            Assert.That(preview.HasAim && preview.Target == abilityRig.Hostile && preview.IsValid, Is.True, "Precondition: aiming at the hostile");

            Build(sources);

            Assert.That(snapshot.IsArmed, Is.True);
            Assert.That(snapshot.Abilities[0].State, Is.EqualTo(HudAbilityState.Armed));
            Assert.That(snapshot.Abilities[1].State, Is.EqualTo(HudAbilityState.Ready));
            var expected = "AIMING: Aimed Shot | " + HudText.CleanName(abilityRig.Hostile.name) + " | "
                + Invariant1(preview.Check.Distance) + "/" + Invariant1(abilityRig.Aimed.Range) + " m | OK";
            Assert.That(snapshot.ArmedLine, Is.EqualTo(expected));
            Assert.That(snapshot.Target.Visible, Is.True);
            Assert.That(snapshot.Target.Unit, Is.SameAs(abilityRig.Hostile));
            Assert.That(snapshot.Target.Tag, Is.EqualTo("AIMING"));
            Assert.That(snapshot.Prompts.Select(p => p.Label), Does.Contain("Cast"), "the armed prompts");

            abilityRig.Targeting.Disarm();
            Build(sources);
            Assert.That(snapshot.IsArmed, Is.False);
            Assert.That(snapshot.ArmedLine, Is.Empty);
        }

        [UnityTest]
        public IEnumerator ArmedLine_IsRebuiltOnlyWhenTheShownTenthChanges()
        {
            abilityRig = AbilityRig.Build(actions, false);
            yield return null;
            var sources = AbilitySources();
            Assert.That(abilityRig.Targeting.Arm(1), Is.True, "the Blast: a ground aim that follows the pointer");
            var screen = abilityRig.ScreenPointOf(AbilityRig.BlastGround);
            Set(mouse.position, screen);
            yield return null;
            yield return null;
            Assert.That(abilityRig.Targeting.Preview.HasAim, Is.True, "Precondition: aiming at the ground");
            var first = abilityRig.Targeting.Preview.Check.Distance;
            Build(sources);
            var line = snapshot.ArmedLine;
            var builds = builder.ArmedLineBuilds;
            Assert.That(line, Does.Contain(" | " + (Mathf.RoundToInt(first * 10f) / 10f).ToString("0.0", CultureInfo.InvariantCulture) + "/"));

            // A one-pixel move that keeps the same tenth: the cached line is reused, nothing is rebuilt.
            var sameTenth = false;
            foreach (var offset in new[] { new Vector2(1f, 0f), new Vector2(-1f, 0f), new Vector2(0f, 1f), new Vector2(0f, -1f) })
            {
                Set(mouse.position, screen + offset);
                yield return null;
                yield return null;
                var distance = abilityRig.Targeting.Preview.Check.Distance;
                if (distance == first || Mathf.RoundToInt(distance * 10f) != Mathf.RoundToInt(first * 10f))
                    continue;
                sameTenth = true;
                Build(sources);
                Assert.That(ReferenceEquals(snapshot.ArmedLine, line), Is.True, $"{first} -> {distance}: the same text is reused");
                Assert.That(builder.ArmedLineBuilds, Is.EqualTo(builds), "no rebuild within a tenth");
                break;
            }
            Assert.That(sameTenth, Is.True, "Precondition: a pixel move that changes the distance within its tenth");

            // A move of several metres changes the number: rebuilt once.
            Set(mouse.position, abilityRig.ScreenPointOf(AbilityRig.BlastGround + new Vector3(-3f, 0f, 0f)));
            yield return null;
            yield return null;
            Build(sources);
            Assert.That(snapshot.ArmedLine, Is.Not.EqualTo(line));
            Assert.That(builder.ArmedLineBuilds, Is.EqualTo(builds + 1));
        }

        [UnityTest]
        public IEnumerator CoverText_IsFilledOnlyWhenThePanelCanShowIt_PausedOrAttacking()
        {
            abilityRig = AbilityRig.Build(actions, false);
            abilityRig.Caster.GetComponent<UnitAttacker>().Initialize(12f, 10, 1f, CombatRole.Ranged);   // only a ranged unit reads cover
            yield return null;
            var sources = AbilitySources();
            Set(mouse.position, abilityRig.ScreenPointOf(abilityRig.Hostile.transform.position));
            yield return null;
            yield return null;

            Build(sources, 1f);
            Assert.That(snapshot.Target.Visible && snapshot.Target.Tag == "HOVERED", Is.True, "Precondition: hovered in real time");
            Assert.That(snapshot.Target.CoverText, Is.Empty, "a hovered target in real time: the line is not shown, so not computed");

            abilityRig.Pause.Pause();
            Build(sources, 2f);
            Assert.That(snapshot.Target.Tag, Is.EqualTo("HOVERED"));
            Assert.That(snapshot.Target.CoverText, Is.EqualTo("Exposed"), "paused: the cover line is filled");

            abilityRig.Pause.Resume();
            Set(mouse.position, abilityRig.ScreenPointOf(AbilityRig.BlastGround + new Vector3(0f, 0f, -6f)));
            yield return null;
            Assert.That(abilityRig.Caster.Unit.Issue(new AttackCommand(abilityRig.Hostile)), Is.True);
            Build(sources, 3f);
            Assert.That(snapshot.Target.Tag, Is.EqualTo("ATTACKING"), "Precondition: the attack target, in real time");
            Assert.That(snapshot.Target.CoverText, Is.EqualTo("Exposed"), "the attack target's cover line is filled");
        }

        // ---- command queue ----

        [UnityTest]
        public IEnumerator Queue_ListsCurrentThenPending_AndCapsByMode()
        {
            abilityRig = AbilityRig.Build(actions, false);
            yield return null;
            var sources = AbilitySources();
            var unit = abilityRig.Caster.Unit;
            abilityRig.Pause.Pause();
            Assert.That(unit.Issue(new MoveCommand(new Vector3(0f, 0f, -8f))), Is.True);
            Assert.That(unit.Issue(new AttackCommand(abilityRig.Hostile), IssueMode.Append), Is.True);
            Assert.That(unit.Issue(AbilityCommand.AtGround(abilityRig.Blast, AbilityRig.BlastGround), IssueMode.Append), Is.True);
            for (var i = 0; i < 7; i++)
                Assert.That(unit.Issue(new MoveCommand(new Vector3(i - 3f, 0f, -9f)), IssueMode.Append), Is.True);

            Build(sources);

            Assert.That(snapshot.Queue.Select(s => s.Number), Is.EqualTo(Enumerable.Range(1, 8)));
            Assert.That(snapshot.Queue.Select(s => s.IsCurrent), Is.EqualTo(new[] { true, false, false, false, false, false, false, false }));
            Assert.That(snapshot.Queue[0].Text, Is.EqualTo("Move"));
            Assert.That(snapshot.Queue[1].Text, Is.EqualTo("Attack " + HudText.CleanName(abilityRig.Hostile.name)));
            Assert.That(snapshot.Queue[2].Text, Is.EqualTo("Blast"));
            Assert.That(snapshot.QueueHidden, Is.EqualTo(2), "10 orders, 8 shown while paused");
            Assert.That(snapshot.QueueOwner, Is.EqualTo(HudText.CleanName(unit.name)));
            Assert.That(snapshot.QueueUnit, Is.SameAs(unit));
            Assert.That(snapshot.CanClearOrders, Is.True);

            abilityRig.Pause.Resume();
            Build(sources);
            Assert.That(snapshot.Queue, Has.Count.EqualTo(3), "real time shows 3");
            Assert.That(snapshot.QueueHidden, Is.EqualTo(7));
            Assert.That(snapshot.Queue[0].IsCurrent, Is.True);
        }

        [UnityTest]
        public IEnumerator Queue_PausedSubject_IsTheFirstSelectedUnit()
        {
            abilityRig = AbilityRig.Build(actions, false);
            yield return null;
            var sources = AbilitySources();
            abilityRig.Selection.Select(abilityRig.Ally);
            Assert.That(abilityRig.Ally.Unit.Issue(new MoveCommand(new Vector3(4f, 0f, -9f))), Is.True);

            Build(sources);
            Assert.That(snapshot.QueueUnit, Is.SameAs(abilityRig.Caster.Unit), "real time: the controlled unit");
            Assert.That(snapshot.CanClearOrders, Is.False);

            abilityRig.Pause.Pause();
            Build(sources);
            Assert.That(snapshot.QueueUnit, Is.SameAs(abilityRig.Ally.Unit), "paused: the first selected unit");
            Assert.That(snapshot.Queue.Select(s => s.Text), Is.EqualTo(new[] { "Move" }));
            Assert.That(snapshot.CanClearOrders, Is.True);
        }

        [UnityTest]
        public IEnumerator Queue_NeverNamesAnUnobservedTarget()
        {
            intelRig = new IntelRig(corridor: false);
            var secret = AddSecretHostile(IntelRig.InLineGround);
            intelRig.Begin(IntelRig.Fog());
            var active = intelRig.World.CreateActiveCharacter(intelRig.Friendly.Unit);
            yield return null;
            Assert.That(intelRig.Service.CanTarget(secret), Is.False, "Precondition: behind the closed wall");
            Assert.That(intelRig.Friendly.Unit.Issue(new AttackCommand(secret)), Is.True);

            Build(IntelSources(active));

            Assert.That(snapshot.Queue[0].Text, Is.EqualTo("Attack (target lost)"));
            Assert.That(snapshot.Target.Visible, Is.False);
            AssertNoTraceOf(secret);
        }

        // ---- mission ----

        [UnityTest]
        public IEnumerator Objectives_RespectKnowledge()
        {
            yield return StartMission(IntelligenceSettings.Blind(), withRoster: false);
            var runtime = missionRig.Director.Runtime;
            var unknown = runtime.Objectives.Where(o => o != runtime.Extraction && !o.IsKnown).ToList();
            Assert.That(unknown, Is.Not.Empty, "Precondition: the blind preset hides the goals");
            Assert.That(service.ListsUnknownObjectives, Is.True);

            Build(MissionSources());
            Assert.That(snapshot.HasMission, Is.True);
            Assert.That(snapshot.PhaseText, Is.EqualTo(MissionHudText.PhaseLabel(runtime.Phase)));
            foreach (var objective in unknown)
            {
                Assert.That(snapshot.Objectives.Any(r => r.Kind == HudObjectiveKind.Unknown && r.Text == objective.VagueTitle), Is.True, objective.Id);
                Assert.That(AllText(snapshot), Does.Not.Contain(objective.Title), objective.Id + ": never the real title");
            }
            Assert.That(snapshot.Objectives.Any(r => r.Text == runtime.Extraction.Describe()), Is.False, "extraction has its own strip");
            Assert.That(snapshot.Extraction, Is.EqualTo(HudExtractionState.Locked), "known from the start, not open yet");

            runtime.Extraction.SetKnown(false);
            Build(MissionSources());
            Assert.That(snapshot.Extraction, Is.EqualTo(HudExtractionState.Unknown));
            Assert.That(snapshot.ExtractionInside, Is.Zero);
            Assert.That(snapshot.Marks.Any(m => m.Kind == HudMarkKind.Extraction), Is.False, "an unknown extraction has no label");
            runtime.Extraction.SetKnown(true);

            missionRig.Director.Settings.intelligence.showUnknownObjectives = false;
            yield return missionRig.Generate(12345);
            foreach (var hostile in missionRig.Director.Hostiles)
                hostile.GetComponent<EnemyAI>().enabled = false;
            runtime = missionRig.Director.Runtime;
            Assert.That(service.ListsUnknownObjectives, Is.False, "Precondition");

            Build(MissionSources());
            var known = runtime.Objectives.Count(o => o != runtime.Extraction && o.IsKnown);
            Assert.That(snapshot.Objectives, Has.Count.EqualTo(known), "unknown goals are omitted");
            foreach (var objective in runtime.Objectives.Where(o => !o.IsKnown))
            {
                Assert.That(AllText(snapshot), Does.Not.Contain(objective.Title));
                if (!string.IsNullOrEmpty(objective.VagueTitle))
                    Assert.That(AllText(snapshot), Does.Not.Contain(objective.VagueTitle));
            }
        }

        [UnityTest]
        public IEnumerator Extraction_Active_CountsSquadInsideTheZone()
        {
            yield return StartMission(settings: new MissionSettings { hackTerminal = false, extractionUnits = 2 }, withRoster: false);
            var director = missionRig.Director;
            foreach (var hostile in director.Hostiles)
            {
                var health = hostile.GetComponent<Health>();
                health.TakeDamage(health.Max);
            }
            yield return null;
            yield return null;
            Assert.That(director.Phase, Is.EqualTo(MissionPhase.ExtractionOpen), "Precondition");

            Build(MissionSources());
            Assert.That(snapshot.Extraction, Is.EqualTo(HudExtractionState.Available));
            Assert.That(snapshot.ExtractionInside, Is.Zero);
            Assert.That(snapshot.ExtractionRequired, Is.EqualTo(2));
            Assert.That(snapshot.Marks.Count(m => m.Kind == HudMarkKind.Extraction), Is.EqualTo(1));

            Assert.That(director.Friendlies[0].GetComponent<NavMeshAgent>().Warp(director.Current.ExtractionZone.position), Is.True);
            yield return null;
            Build(MissionSources());
            Assert.That(snapshot.Extraction, Is.EqualTo(HudExtractionState.Active));
            Assert.That(snapshot.ExtractionInside, Is.EqualTo(1));
            Assert.That(snapshot.ExtractionRequired, Is.EqualTo(2));
        }

        // ---- target and marks ----

        [UnityTest]
        public IEnumerator Target_OnlyForObservedHostiles()
        {
            intelRig = new IntelRig(corridor: false);
            var secret = AddSecretHostile(IntelRig.InLineGround);
            intelRig.Begin(IntelRig.Fog());
            var (viewCamera, selection, active, cursor, input, _) = WireIntelInput();
            yield return null;

            Assert.That(intelRig.Service.CanTarget(secret), Is.False, "Precondition");
            Assert.That(intelRig.Friendly.Unit.Issue(new AttackCommand(secret)), Is.True);
            cursor.CycleTarget(1);
            Assert.That(cursor.SoftTarget, Is.SameAs(secret), "Precondition: the soft target");
            Set(mouse.position, (Vector2)viewCamera.WorldToScreenPoint(secret.transform.position));
            yield return null;
            Assert.That(input.ResolveAt(input.PointerScreenPosition).Hostile, Is.SameAs(secret), "Precondition: under the pointer");
            var sources = InputSources(viewCamera, selection, active, cursor, input, null);

            Build(sources, 0f);
            Assert.That(snapshot.Target.Visible, Is.False, "attack target, soft target and hover are all unobserved");
            AssertNoTraceOf(secret);

            intelRig.Service.Scan(secret.transform.position, 3f, 5f);
            Assert.That(intelRig.Service.CanTarget(secret), Is.True, "Precondition: observed now");
            Build(sources, 1f);
            Assert.That(snapshot.Target.Visible, Is.True);
            Assert.That(snapshot.Target.Unit, Is.SameAs(secret));
            Assert.That(snapshot.Target.Name, Is.EqualTo(SecretName));
            Assert.That(snapshot.Target.Health, Is.EqualTo(secret.Current));
            Assert.That(snapshot.Target.MaxHealth, Is.EqualTo(secret.Max));
            Assert.That(snapshot.Target.Tag, Is.EqualTo("HOVERED"));

            builder.PointerOverHud = _ => true;
            Build(sources, 2f);
            Assert.That(snapshot.Target.Tag, Is.EqualTo("TARGETED"), "over the HUD the hover is dropped: the soft target is next");

            builder.PointerOverHud = null;
            Build(sources, 2.05f);
            Assert.That(snapshot.Target.Tag, Is.EqualTo("TARGETED"), "the hover is resolved at most every 0.1 s");
            Build(sources, 2.2f);
            Assert.That(snapshot.Target.Tag, Is.EqualTo("HOVERED"));
        }

        [UnityTest]
        public IEnumerator Target_AHoveredHostileThatIsLost_DisappearsAtOnce_EvenFromTheHoverCache()
        {
            intelRig = new IntelRig(corridor: true);
            var secret = AddSecretHostile(IntelRig.InLineGround);   // seen through the corridor
            intelRig.Begin(IntelRig.Fog());
            var (viewCamera, selection, active, cursor, input, _) = WireIntelInput();
            yield return null;
            Assert.That(intelRig.Service.CanTarget(secret), Is.True, "Precondition: observed");
            Assert.That(intelRig.Friendly.Unit.Issue(new AttackCommand(secret)), Is.True);
            Set(mouse.position, (Vector2)viewCamera.WorldToScreenPoint(secret.transform.position));
            yield return null;
            var sources = InputSources(viewCamera, selection, active, cursor, input, null);

            Build(sources, 10f);
            Assert.That(snapshot.Target.Visible && snapshot.Target.Tag == "HOVERED", Is.True, "Precondition: hovered");
            Assert.That(snapshot.Queue[0].Text, Is.EqualTo("Attack " + SecretName), "Precondition: named while observed");

            LookAwayFromTheCorridor();
            Assert.That(intelRig.Service.StateOfEnemy(secret), Is.EqualTo(KnowledgeState.Discovered), "Precondition: lost");
            Build(sources, 10.05f);   // inside the hover window: the cached hover result is still this hostile

            Assert.That(snapshot.Target.Visible, Is.False);
            Assert.That(snapshot.Queue[0].Text, Is.EqualTo("Attack (target lost)"));
            Assert.That(snapshot.Marks.Count(m => m.Kind == HudMarkKind.LastKnown), Is.EqualTo(1));
            Assert.That(snapshot.Marks.Any(m => m.Kind == HudMarkKind.Hostile), Is.False);
            Assert.That(AllText(snapshot), Does.Not.Contain(SecretName));
        }

        [UnityTest]
        public IEnumerator ArmedLine_DropsTheTarget_TheMomentItIsNoLongerObserved()
        {
            intelRig = new IntelRig(corridor: true);
            var secret = AddSecretHostile(IntelRig.InLineGround);
            intelRig.Begin(IntelRig.Fog());
            var aimed = intelRig.World.CreateAimedShot();
            intelRig.World.AddAbilities(intelRig.Friendly.Unit, intelRig.Encounter, aimed);
            var (viewCamera, selection, active, cursor, input, targeting) = WireIntelInput(withTargeting: true);
            yield return null;
            Assert.That(targeting.Arm(0), Is.True);
            Set(mouse.position, (Vector2)viewCamera.WorldToScreenPoint(secret.transform.position));
            yield return null;
            yield return null;
            Assert.That(targeting.Preview.HasAim && targeting.Preview.Target == secret, Is.True, "Precondition: aiming at it");
            var sources = InputSources(viewCamera, selection, active, cursor, input, targeting);
            Build(sources);
            Assert.That(snapshot.ArmedLine, Does.StartWith("AIMING: Aimed Shot | " + SecretName + " | "), "Precondition");

            LookAwayFromTheCorridor();   // no yield: the preview still holds the target
            Assert.That(targeting.Preview.Target, Is.SameAs(secret), "Precondition: a stale preview");
            Build(sources);

            Assert.That(snapshot.ArmedLine, Is.EqualTo("AIMING: Aimed Shot"));
            Assert.That(snapshot.Target.Visible, Is.False);
            Assert.That(AllText(snapshot), Does.Not.Contain(SecretName));
        }

        [UnityTest]
        public IEnumerator Marks_ObservedLastKnownAndUnknown()
        {
            intelRig = new IntelRig(corridor: true);
            var seen = intelRig.AddHostile(IntelRig.InLineGround);
            var secret = AddSecretHostile(IntelRig.OffAxisGround);
            intelRig.Begin(IntelRig.Fog());
            yield return null;
            Assert.That(intelRig.Service.CanTarget(seen), Is.True, "Precondition");
            Assert.That(intelRig.Service.StateOfEnemy(secret), Is.EqualTo(KnowledgeState.Unknown), "Precondition");
            var sources = new HudSources { encounter = intelRig.Encounter, intelligence = intelRig.Service };

            Build(sources);
            Assert.That(snapshot.Marks, Has.Count.EqualTo(1));
            Assert.That(snapshot.Marks[0].Kind, Is.EqualTo(HudMarkKind.Hostile));
            Assert.That(Vector3.Distance(snapshot.Marks[0].World, seen.transform.position + Vector3.up * 2.3f), Is.LessThan(0.01f));
            Assert.That(snapshot.Marks[0].Text, Is.Empty);
            AssertNoTraceOf(secret);

            LookAwayFromTheCorridor();
            Assert.That(intelRig.Service.StateOfEnemy(seen), Is.EqualTo(KnowledgeState.Discovered), "Precondition: lost");
            Assert.That(intelRig.Service.TryLastKnown(seen, out var lastSeen), Is.True);

            Build(sources);
            Assert.That(snapshot.Marks, Has.Count.EqualTo(1));
            Assert.That(snapshot.Marks[0].Kind, Is.EqualTo(HudMarkKind.LastKnown));
            Assert.That(snapshot.Marks[0].World, Is.EqualTo(lastSeen));
            Assert.That(snapshot.Marks[0].Text, Is.EqualTo("LAST KNOWN"));
            AssertNoTraceOf(secret);
        }

        [UnityTest]
        public IEnumerator TruthView_OnALostHostile_StillGivesOnlyItsLastKnownMark()
        {
            intelRig = new IntelRig(corridor: true);
            var seen = intelRig.AddHostile(IntelRig.InLineGround);
            intelRig.Begin(IntelRig.Fog());
            yield return null;
            Assert.That(intelRig.Service.CanTarget(seen), Is.True, "Precondition: observed");
            LookAwayFromTheCorridor();
            Assert.That(intelRig.Service.StateOfEnemy(seen), Is.EqualTo(KnowledgeState.Discovered), "Precondition: lost");
            Assert.That(intelRig.Service.TryLastKnown(seen, out var lastSeen), Is.True);

            intelRig.Service.TruthView = true;
            Assert.That(Knowledge.IsShown(intelRig.Service, seen), Is.True, "Precondition: the truth view shows it in the debug views");
            Build(new HudSources { encounter = intelRig.Encounter, intelligence = intelRig.Service });

            Assert.That(snapshot.Marks.Count(m => m.Kind == HudMarkKind.Hostile), Is.Zero, "no hostile mark for a lost hostile, truth view or not");
            Assert.That(snapshot.Marks.Count(m => m.Kind == HudMarkKind.LastKnown), Is.EqualTo(1));
            Assert.That(snapshot.Marks.Single(m => m.Kind == HudMarkKind.LastKnown).World, Is.EqualTo(lastSeen), "at its last-known point");
        }

        [UnityTest]
        public IEnumerator TruthView_DoesNotWidenTheHud()
        {
            intelRig = new IntelRig(corridor: false);
            var secret = AddSecretHostile(IntelRig.InLineGround);
            intelRig.Begin(IntelRig.Fog());
            var active = intelRig.World.CreateActiveCharacter(intelRig.Friendly.Unit);
            yield return null;
            Assert.That(intelRig.Friendly.Unit.Issue(new AttackCommand(secret)), Is.True);

            intelRig.Service.TruthView = true;
            Assert.That(Knowledge.IsShown(intelRig.Service, secret), Is.True, "Precondition: the truth view shows it in the debug views");

            Build(IntelSources(active));
            Assert.That(snapshot.Marks.Any(m => m.Kind == HudMarkKind.Hostile), Is.False);
            Assert.That(snapshot.Target.Visible, Is.False);
            Assert.That(snapshot.Queue[0].Text, Is.EqualTo("Attack (target lost)"));
            AssertNoTraceOf(secret);
        }

        // ---- prompts ----

        [UnityTest]
        public IEnumerator Prompts_RebuildOnlyWhenTheKeyChanges()
        {
            world = new TestWorld();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var sources = new HudSources { tacticalPause = pause, controls = actions };
            yield return null;

            Build(sources);
            var first = snapshot.Prompts.ToList();
            Assert.That(first, Is.Not.Empty);
            Assert.That(builder.PromptRebuilds, Is.EqualTo(1));
            var expected = new List<HudPromptEntry>();
            HudPrompts.Build(default, InputFamily.KeyboardMouse, actions, expected);
            Assert.That(first, Is.EqualTo(expected));
            Assert.That(snapshot.ResumePrompt,
                Is.EqualTo(PromptResolver.GetPrompt(actions.FindAction("Commands/ToggleTacticalPause"), InputFamily.KeyboardMouse)));

            var other = new HudSnapshot();
            builder.Build(sources, other, 0.5f);
            Assert.That(builder.PromptRebuilds, Is.EqualTo(1), "same key: no rebuild");
            Assert.That(other.Prompts, Has.Count.EqualTo(first.Count));
            for (var i = 0; i < first.Count; i++)
            {
                Assert.That(ReferenceEquals(other.Prompts[i].Label, first[i].Label), Is.True, first[i].Label);
                Assert.That(ReferenceEquals(other.Prompts[i].Prompt, first[i].Prompt), Is.True, first[i].Label);
            }

            pause.Pause();
            Build(sources);
            Assert.That(builder.PromptRebuilds, Is.EqualTo(2), "paused is a new key");
            Assert.That(snapshot.Prompts.Select(p => p.Label), Does.Contain("Resume"));
            Build(sources);
            Assert.That(builder.PromptRebuilds, Is.EqualTo(2));
        }

        IEnumerator Wake(Gamepad pad)
        {
            Press(pad.selectButton);
            yield return null;
            Release(pad.selectButton);
            yield return null;
        }

        List<HudPromptEntry> Expected(HudPromptContext context, InputFamily family)
        {
            var list = new List<HudPromptEntry>();
            HudPrompts.Build(context, family, actions, list);
            return list;
        }

        static string PromptOf(IEnumerable<HudPromptEntry> entries, string label) => entries.First(e => e.Label == label).Prompt;

        [UnityTest]
        public IEnumerator Prompts_FollowTheFamily()
        {
            world = new TestWorld();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var host = world.Track(new GameObject("InputDevice"));
            host.SetActive(false);
            var device = host.AddComponent<ActiveInputDevice>();
            device.Initialize(actions);
            host.SetActive(true);
            var sources = new HudSources { tacticalPause = pause, inputDevice = device, controls = actions };
            pause.Pause();
            var paused = new HudPromptContext { Paused = true };
            var cancel = actions.FindAction("Commands/Cancel");

            yield return Wake(InputSystem.AddDevice<XInputController>());
            Assert.That(device.Family, Is.EqualTo(InputFamily.Xbox), "Precondition");
            Build(sources);
            var xbox = snapshot.Prompts.ToList();
            Assert.That(xbox, Is.EqualTo(Expected(paused, InputFamily.Xbox)));
            Assert.That(PromptOf(xbox, "Cancel"), Is.EqualTo(PromptResolver.GetPrompt(cancel, InputFamily.Xbox)));
            Assert.That(snapshot.ResumePrompt,
                Is.EqualTo(PromptResolver.GetPrompt(actions.FindAction("Commands/ToggleTacticalPause"), InputFamily.Xbox)));

            yield return Wake(InputSystem.AddDevice<SwitchProControllerHID>());
            Assert.That(device.Family, Is.EqualTo(InputFamily.Nintendo), "Precondition");
            Build(sources);
            var nintendo = snapshot.Prompts.ToList();
            Assert.That(nintendo, Is.EqualTo(Expected(paused, InputFamily.Nintendo)), "no restart needed");
            Assert.That(PromptOf(nintendo, "Cancel"), Is.EqualTo(PromptResolver.GetPrompt(cancel, InputFamily.Nintendo)));
            Assert.That(nintendo, Is.Not.EqualTo(xbox), "the list follows the family");
        }

        // ---- robustness ----

        [Test]
        public void NullSources_BuildAnEmptySnapshotWithoutThrowing()
        {
            snapshot.Squad.Add(default);
            snapshot.ArmedLine = "stale";
            Assert.DoesNotThrow(() => Build(null));
            Assert.That(snapshot.Squad, Is.Empty);
            Assert.That(snapshot.ArmedLine, Is.Empty);

            Assert.DoesNotThrow(() => Build(new HudSources()));
            Assert.That(snapshot.Squad, Is.Empty);
            Assert.That(snapshot.Abilities, Is.Empty);
            Assert.That(snapshot.Queue, Is.Empty);
            Assert.That(snapshot.Objectives, Is.Empty);
            Assert.That(snapshot.Prompts, Is.Empty);
            Assert.That(snapshot.Marks, Is.Empty);
            Assert.That(snapshot.HasMission, Is.False);
            Assert.That(snapshot.HasControlled, Is.False);
            Assert.That(snapshot.HasFollow, Is.False);
            Assert.That(snapshot.IsPaused, Is.False);
            Assert.That(snapshot.IsArmed, Is.False);
            Assert.That(snapshot.ResumePrompt, Is.EqualTo("-"));
            Assert.That(snapshot.Target.Visible, Is.False);
            Assert.That(snapshot.Extraction, Is.EqualTo(HudExtractionState.Hidden));
            Assert.That(snapshot.QueueUnit, Is.Null);
        }
    }
}
#endif
