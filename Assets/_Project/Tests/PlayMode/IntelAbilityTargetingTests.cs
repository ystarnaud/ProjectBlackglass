#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class IntelAbilityTargetingTests : InputTestFixture
    {
        InputActionAsset actions;
        AbilityRig rig;
        IntelligenceService service;

        public override void Setup()
        {
            base.Setup();
            InputSystem.AddDevice<Keyboard>();
            InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
            rig = AbilityRig.Build(actions, false);
        }

        public override void TearDown()
        {
            service.Clear();
            rig.World.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        // One big room covering the whole arena (tile (x, y) is world (x - 20, y - 20)); walls and range do the hiding.
        static MissionLayout ArenaLayout()
        {
            const int size = 40;
            var floor = new bool[size * size];
            for (var i = 0; i < floor.Length; i++)
                floor[i] = true;
            return new MissionLayout(1, 1, size, size, floor)
            {
                Rooms = new[] { new MissionRoom(0, Vector2Int.zero, new RectInt(0, 0, size, size)) },
                FriendlyRoom = 0,
            };
        }

        void BeginFog(float observationRange, IntelligenceMission mission = null)
        {
            mission = mission ?? new IntelligenceMission();
            mission.Layout = ArenaLayout();
            service = rig.World.Track(new GameObject("Intelligence")).AddComponent<IntelligenceService>();
            service.Begin(mission,
                new IntelligenceSettings { fogEnabled = true, map = MapKnowledge.None, objectives = ObjectiveKnowledge.None, observationRange = observationRange },
                rig.Encounter);
            rig.Targeting.SetIntelligence(service);
            rig.Abilities.SetIntelligence(service);
            rig.Input.SetIntelligence(service);
        }

        [UnityTest]
        public IEnumerator AnAbilityAimedAtAnUnobservedHostile_PreviewsAsNoAim_AndConfirmingItIsRefused()
        {
            BeginFog(observationRange: 4f);   // the near hostile is 8.5 m from the caster
            yield return null;
            Assert.That(service.CanTarget(rig.Hostile), Is.False, "Precondition");
            Assert.That(rig.Targeting.Arm(0), Is.True);

            var pointed = PointerTarget.OnHostile(rig.Hostile, rig.Hostile.transform.position);
            var preview = rig.Targeting.Evaluate(rig.Aimed, pointed, queued: false);
            Assert.That(preview.HasAim, Is.False, "an unobserved hostile is not a target");
            Assert.That(preview.Failure, Is.EqualTo(AbilityFailure.NoTarget));

            Assert.That(rig.Targeting.Confirm(pointed, queue: false), Is.False);
            Assert.That(rig.Abilities.LastFailure, Is.EqualTo(AbilityFailure.NoTarget));
            Assert.That(rig.Hostile.Current, Is.EqualTo(rig.Hostile.Max));
            Assert.That(rig.Targeting.IsArmed, Is.True, "still armed: another target can be tried");
        }

        [UnityTest]
        public IEnumerator AGroundAbility_CanBeAimedAnywhereInRange_ButItsPreviewListsOnlyObservedHostiles()
        {
            BeginFog(observationRange: 4f);
            yield return null;
            Assert.That(rig.Targeting.Arm(1), Is.True);   // Blast: ground, radius 3
            var pointed = PointerTarget.OnGround(AbilityRig.BlastGround);   // 2 m from the unobserved near hostile
            var preview = rig.Targeting.Evaluate(rig.Blast, pointed, queued: false);

            Assert.That(preview.HasAim, Is.True, "undiscovered ground can be aimed at");
            Assert.That(preview.Check.IsValid, Is.True, preview.Check.Failure.ToString());
            Assert.That(rig.Targeting.AreaHits, Is.Empty, "the preview names nobody the player cannot see");
            Assert.That(AbilityDescriptions.Preview(preview, rig.Targeting.AreaHits), Does.Contain("hits 0"));
        }

        [UnityTest]
        public IEnumerator AGroundAbility_StillHitsEveryHostileInTheBlast_EvenOnesTheCasterCannotSee()
        {
            BeginFog(observationRange: 4f);
            yield return null;
            var before = rig.Hostile.Current;
            Assert.That(rig.Abilities.TryUse(AbilityCommand.AtGround(rig.Blast, AbilityRig.BlastGround)), Is.True);
            Assert.That(rig.Hostile.Current, Is.LessThan(before), "blind fire works: the gate is display only");
        }

        [UnityTest]
        public IEnumerator TheGateOpens_WhenTheHostileIsObserved()
        {
            BeginFog(observationRange: 14f);   // 8.5 m: inside the range, in open sight
            yield return null;
            Assert.That(service.CanTarget(rig.Hostile), Is.True, "Precondition");
            Assert.That(rig.Targeting.Arm(1), Is.True);
            rig.Targeting.Evaluate(rig.Blast, PointerTarget.OnGround(AbilityRig.BlastGround), queued: false);
            Assert.That(rig.Targeting.AreaHits, Does.Contain(rig.Hostile));
        }

        [UnityTest]
        public IEnumerator TheTerminalPrompt_FollowsWhetherTheCameraTerminalDeviceIsKnown()
        {
            // The terminal stands beside the caster but its device (the plan's tile, in the far corner of the arena) is not seen.
            var host = rig.World.Track(new GameObject("CameraTerminal"));
            host.transform.position = rig.Caster.transform.position + new Vector3(1f, -1f, 0f);
            var terminal = host.AddComponent<MissionInteractable>();
            terminal.Initialize(1.8f, 2f, "Camera control");
            terminal.SetAvailable(true);
            var registry = rig.World.Track(new GameObject("Interactables")).AddComponent<InteractableRegistry>();
            registry.Rebuild(new[] { terminal });
            rig.Input.WireInteraction(registry, null);

            BeginFog(observationRange: 4f, new IntelligenceMission
            {
                Security = new SecurityPlan(true, 0, new Vector2Int(39, 39), new CameraMount[0]),
                CameraTerminal = terminal,
            });
            yield return null;

            Assert.That(rig.Input.NearbyInteractable, Is.Null, "the unknown terminal is not offered, though the caster stands beside it");
            service.Model.RevealDevice(SecurityPlan.TerminalDeviceId);
            Assert.That(rig.Input.NearbyInteractable, Is.SameAs(terminal));
        }
    }
}
#endif
