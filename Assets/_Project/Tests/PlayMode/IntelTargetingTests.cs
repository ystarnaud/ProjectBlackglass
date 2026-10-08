#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The pointer resolver, ability validation, companions and terminals respect what the player knows.</summary>
    public class IntelTargetingTests
    {
        IntelRig rig;
        Camera viewCamera;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        void MakeCamera()
        {
            var cameraObject = rig.World.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();
        }

        PointerTarget Resolve(Vector3 worldPoint, IntelligenceService intelligence) =>
            PointerTargetResolver.Resolve(viewCamera, viewCamera.WorldToScreenPoint(worldPoint), 500f, ~0, null, 0f, intelligence);

        // ---- the pointer ----

        [UnityTest]
        public IEnumerator AClickOnAnUnobservedHostile_ResolvesToGround_NotToTheHostile()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            MakeCamera();
            yield return null;

            var target = Resolve(hostile.transform.position, rig.Service);
            Assert.That(target.Kind, Is.EqualTo(PointerTargetKind.Ground), "the collider is still there, the knowledge is not");
            Assert.That(target.Hostile, Is.Null);
        }

        [UnityTest]
        public IEnumerator AClickOnAnObservedHostile_StillResolvesToTheHostile()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            MakeCamera();
            yield return null;
            Assert.That(rig.Service.CanTarget(hostile), Is.True, "Precondition");

            var target = Resolve(hostile.transform.position, rig.Service);
            Assert.That(target.Kind, Is.EqualTo(PointerTargetKind.Hostile));
            Assert.That(target.Hostile, Is.SameAs(hostile));
        }

        [UnityTest]
        public IEnumerator WithoutAService_OrWithFogOff_TheResolverIsAsBefore()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(new IntelligenceSettings());   // fog off
            MakeCamera();
            yield return null;
            Assert.That(Resolve(hostile.transform.position, null).Kind, Is.EqualTo(PointerTargetKind.Hostile));
            Assert.That(Resolve(hostile.transform.position, rig.Service).Kind, Is.EqualTo(PointerTargetKind.Hostile));
        }

        [UnityTest]
        public IEnumerator AClickOnALastKnownMarkerSpot_IsGround_NotATarget()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog(s => s.enemyMarkersAtStart = 1));
            MakeCamera();
            yield return null;
            Assert.That(rig.Service.StateOfEnemy(hostile), Is.EqualTo(KnowledgeState.Discovered));
            Assert.That(Resolve(hostile.transform.position, rig.Service).Kind, Is.EqualTo(PointerTargetKind.Ground));
        }

        [UnityTest]
        public IEnumerator AClickOnAnUnknownCameraTerminal_IsNotAnInteractable_UntilItsDeviceIsDiscovered()
        {
            rig = new IntelRig(corridor: false);
            var host = rig.World.Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            host.name = "CameraTerminal";
            host.transform.position = new Vector3(-6.5f, 0.6f, -3f);
            host.transform.localScale = new Vector3(0.8f, 1.2f, 0.8f);
            var terminal = host.AddComponent<MissionInteractable>();
            terminal.Initialize(1.8f, 2f, "Camera control");
            terminal.SetAvailable(true);
            Physics.SyncTransforms();
            var security = new SecurityPlan(true, 0, new Vector2Int(19, 9), new CameraMount[0]);   // device 0 in the far corner: not seen
            rig.Begin(IntelRig.Fog(), new IntelligenceMission { Security = security, CameraTerminal = terminal });
            MakeCamera();
            yield return null;

            Assert.That(Resolve(host.transform.position + Vector3.up * 0.2f, rig.Service).Kind, Is.EqualTo(PointerTargetKind.Ground));
            rig.Service.Model.RevealDevice(SecurityPlan.TerminalDeviceId);
            Assert.That(Resolve(host.transform.position + Vector3.up * 0.2f, rig.Service).Kind, Is.EqualTo(PointerTargetKind.Interactable));
        }

        [UnityTest]
        public IEnumerator AFriendlyIsAlwaysResolved_FogOrNot()
        {
            rig = new IntelRig(corridor: false);
            rig.Begin(IntelRig.Fog());
            MakeCamera();
            yield return null;
            var target = Resolve(rig.Friendly.transform.position, rig.Service);
            Assert.That(target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
        }

        // ---- ability validation ----

        [UnityTest]
        public IEnumerator AUnitAbility_OnAnUnobservedHostile_FailsAsNoTarget_AndWorksOnceItIsObserved()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.OffAxisGround);   // behind the corridor wall: not observed
            rig.Begin(IntelRig.Fog());
            var aimed = rig.World.CreateAimedShot();
            var abilities = rig.World.AddAbilities(rig.Friendly.Unit, rig.Encounter, aimed);
            abilities.SetIntelligence(rig.Service);
            yield return null;

            var hidden = abilities.Check(aimed, hostile, null);
            Assert.That(hidden.Failure, Is.EqualTo(AbilityFailure.NoTarget));
            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.False);
            Assert.That(hostile.Current, Is.EqualTo(hostile.Max), "nothing was hit");

            rig.Service.Scan(hostile.transform.position, 2f, 5f);
            Assert.That(abilities.Check(aimed, hostile, null).Failure, Is.Not.EqualTo(AbilityFailure.NoTarget));
        }

        [UnityTest]
        public IEnumerator AHealOnAFriendly_IsNeverGated()
        {
            rig = new IntelRig(corridor: false);
            rig.Begin(IntelRig.Fog());
            var mend = rig.World.CreateMend();
            var abilities = rig.World.AddAbilities(rig.Friendly.Unit, rig.Encounter, mend);
            abilities.SetIntelligence(rig.Service);
            yield return null;
            Assert.That(abilities.Check(mend, rig.FriendlyHealth, null).Failure, Is.Not.EqualTo(AbilityFailure.NoTarget));
        }

        // ---- companions ----

        [UnityTest]
        public IEnumerator ACompanion_DoesNotAssistAgainstAnUnobservedEngagedHostile()
        {
            rig = new IntelRig(corridor: false);
            var engaged = rig.World.CreateFighter(new Vector3(-1f, 0f, 8f), 60, 10, 1f, CombatRole.Melee, 2f);
            engaged.name = "Engaged";
            var engagedHealth = engaged.GetComponent<Health>();
            rig.Hostiles.Add(engagedHealth);
            rig.Begin(IntelRig.Fog());
            var active = rig.World.CreateActiveCharacter(rig.Friendly.Unit);
            var companion = rig.World.CreateCompanion(new Vector3(-8f, 0f, 8f), active, rig.Encounter);
            companion.Initialize(active, rig.Encounter, assist: 30f);
            companion.SetIntelligence(rig.Service);
            Assert.That(engaged.Issue(new AttackCommand(rig.FriendlyHealth)), Is.True, "Precondition: the hostile is engaged");
            yield return null;
            Assert.That(rig.Service.CanTarget(engagedHealth), Is.False, "Precondition: nobody has seen it");

            Assert.That(companion.ChooseAssistTarget(), Is.Null);
            rig.Service.Scan(engaged.transform.position, 3f, 5f);
            Assert.That(companion.ChooseAssistTarget(), Is.SameAs(engagedHealth), "once it is seen the companion helps");
        }

        [UnityTest]
        public IEnumerator ACompanion_DoesNotFollowTheLeadersAttackOnAnUnobservedHostile()
        {
            rig = new IntelRig(corridor: false);
            var target = rig.World.CreateFighter(new Vector3(-1f, 0f, 8f), 60, 10, 1f, CombatRole.Melee, 2f);
            target.name = "LeaderTarget";
            var targetHealth = target.GetComponent<Health>();
            rig.Hostiles.Add(targetHealth);
            rig.Begin(IntelRig.Fog());
            var active = rig.World.CreateActiveCharacter(rig.Friendly.Unit);
            var companion = rig.World.CreateCompanion(new Vector3(-8f, 0f, 8f), active, rig.Encounter);
            companion.Initialize(active, rig.Encounter, assist: 30f);
            companion.SetIntelligence(rig.Service);
            Assert.That(rig.Friendly.Unit.Issue(new AttackCommand(targetHealth)), Is.True, "Precondition: the leader attacks it");
            yield return null;
            Assert.That(rig.Friendly.Unit.AttackTarget, Is.SameAs(targetHealth), "Precondition: the leader's current command is that attack");
            Assert.That(rig.Service.CanTarget(targetHealth), Is.False, "Precondition: nobody has seen it");

            Assert.That(companion.ChooseAssistTarget(), Is.Null, "the leader's target is not known, so the companion does not follow it");
            rig.Service.Scan(target.transform.position, 3f, 5f);
            Assert.That(companion.ChooseAssistTarget(), Is.SameAs(targetHealth), "once it is seen the companion follows the leader");
        }

        // ---- terminals ----

        [UnityTest]
        public IEnumerator NearestAvailable_WithAFilter_SkipsWhatTheFilterRejects()
        {
            rig = new IntelRig(corridor: false);
            var registry = rig.World.Track(new GameObject("Interactables")).AddComponent<InteractableRegistry>();
            MissionInteractable Make(float x)
            {
                var host = rig.World.Track(new GameObject("T" + x));
                host.transform.position = new Vector3(x, 0f, 0f);
                var item = host.AddComponent<MissionInteractable>();
                item.Initialize(1.8f, 2f);
                item.SetAvailable(true);
                return item;
            }
            var near = Make(1f);
            var far = Make(2f);
            registry.Rebuild(new[] { near, far });
            yield return null;
            Assert.That(registry.NearestAvailable(Vector3.zero, 5f), Is.SameAs(near));
            Assert.That(registry.NearestAvailable(Vector3.zero, 5f, item => item != near), Is.SameAs(far));
            Assert.That(registry.NearestAvailable(Vector3.zero, 5f, item => false), Is.Null);
        }
    }
}
#endif
