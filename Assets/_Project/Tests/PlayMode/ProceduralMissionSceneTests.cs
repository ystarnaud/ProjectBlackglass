#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XInput;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// Every existing system in a generated mission: the ProceduralMission scene generates its own mission at start (the
    /// scene's seed), and these tests play it. A test that needs a unit somewhere warps it there (its AI off when it must
    /// not act). Only an Xbox-style simulated pad is used: a simulated DualSense discards delta events (see
    /// PrototypeSceneControllerTests).
    /// </summary>
    public class ProceduralMissionSceneTests : InputTestFixture
    {
        Keyboard keyboard;
        Mouse mouse;
        InputActionAsset actions;
        MissionDirector director;
        CommandableUnit[] squad;
        CommandableUnit[] hostiles;
        TacticalPause pause;
        UnitSelection selection;
        Encounter encounter;
        CoverRegistry registry;
        ActiveCharacter active;
        AbilityTargeting targeting;
        TacticalCursor cursor;
        Camera viewCamera;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
        }

        public override void TearDown()
        {
            Time.timeScale = 1f;
            // Only the mission scene: without it the active scene is the test runner's own, which must survive.
            if (SceneManager.GetActiveScene().name == "ProceduralMission")
                PrototypeSceneTests.DestroySceneObjects();
            TestControls.Reset(actions);
            base.TearDown();
        }

        // The scene's director generates its own seed at start; wait for it, then bind the scene's systems.
        IEnumerator LoadMission()
        {
            var loading = SceneManager.LoadSceneAsync("ProceduralMission", LoadSceneMode.Single);
            Assert.That(loading, Is.Not.Null, "The ProceduralMission scene is not in the build settings: run the scene builder");
            yield return loading;
            director = Object.FindFirstObjectByType<MissionDirector>();
            Assert.That(director, Is.Not.Null, "The scene has no MissionDirector: run the scene builder");
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 20f);
            Assert.That(director.State, Is.EqualTo(MissionState.Ready), director.Report.Failure);
            Bind();
            yield return null;
        }

        void Bind()
        {
            squad = director.Friendlies.ToArray();
            hostiles = director.Hostiles.ToArray();
            pause = Object.FindFirstObjectByType<TacticalPause>();
            selection = Object.FindFirstObjectByType<UnitSelection>();
            encounter = Object.FindFirstObjectByType<Encounter>();
            registry = Object.FindFirstObjectByType<CoverRegistry>();
            active = Object.FindFirstObjectByType<ActiveCharacter>();
            targeting = Object.FindFirstObjectByType<AbilityTargeting>();
            cursor = Object.FindFirstObjectByType<TacticalCursor>();
            viewCamera = Camera.main;
        }

        static Health HealthOf(Component unit) => unit.GetComponent<Health>();

        static readonly Vector3[] Around =
        {
            new Vector3(1f, 0f, 0f), new Vector3(-1f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, -1f),
            new Vector3(0.7071f, 0f, 0.7071f), new Vector3(-0.7071f, 0f, 0.7071f), new Vector3(0.7071f, 0f, -0.7071f),
            new Vector3(-0.7071f, 0f, -0.7071f),
        };

        // True when the camera sees this ground point: it is on screen and the ray through it lands on the floor right
        // there, not on a wall in front of it (a 3 m wall hides about 2 m of floor behind it at the default pitch).
        bool CameraSeesGround(Vector3 groundPoint)
        {
            var screen = viewCamera.WorldToScreenPoint(groundPoint);
            if (screen.z <= 0f || screen.x < 20f || screen.x > viewCamera.pixelWidth - 20f || screen.y < 20f || screen.y > viewCamera.pixelHeight - 20f)
                return false;
            var seen = PointerTargetResolver.Resolve(viewCamera, screen, 500f, ~0, null, 0f);
            return seen.Kind == PointerTargetKind.Ground && Vector3.Distance(seen.Point, groundPoint) < 0.3f;
        }

        // Open floor `distance` metres from the unit that it can walk to and (when `mustBeSeen`) the camera sees: the first
        // of the eight directions, or with `awayFrom` the one pointing most directly away from that point.
        bool TryFindOrderPoint(CommandableUnit unit, float distance, out Vector3 point, bool mustBeSeen = true, Vector3? awayFrom = null)
        {
            var mover = unit.GetComponent<UnitMover>();
            var ground = unit.transform.position - Vector3.up;
            var away = awayFrom.HasValue ? unit.transform.position - awayFrom.Value : Vector3.zero;
            away.y = 0f;
            foreach (var direction in Around.OrderByDescending(d => Vector3.Dot(d, away)))
            {
                if (!NavMesh.SamplePosition(ground + direction * distance, out var hit, 0.5f, NavMesh.AllAreas))
                    continue;
                if (!mover.CanReach(hit.position) || (mustBeSeen && !CameraSeesGround(hit.position)))
                    continue;
                point = hit.position;
                return true;
            }
            point = default;
            return false;
        }

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        IEnumerator CursorOn(Vector3 worldPoint)
        {
            cursor.SetScreenPosition(viewCamera.WorldToScreenPoint(worldPoint));
            yield return null;
        }

        // An open NavMesh spot `distance` metres from `from` that `from` can see (unless `needSight` is false).
        bool TryFindVisibleSpot(CommandableUnit from, float distance, out Vector3 spot, bool needSight = true)
        {
            var attacker = from.GetComponent<UnitAttacker>();
            var ground = from.transform.position - Vector3.up;
            for (var step = 0; step < 16; step++)
            {
                var angle = step * Mathf.PI * 2f / 16f;
                var candidate = ground + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;
                if (!NavMesh.SamplePosition(candidate, out var hit, 0.5f, NavMesh.AllAreas))
                    continue;
                if (needSight && !attacker.HasLineOfSightToPoint(hit.position + Vector3.up))
                    continue;
                spot = hit.position;
                return true;
            }
            spot = default;
            return false;
        }

        // Stands a hostile `distance` metres from the caster on open ground (in sight of it unless `needSight` is false), with its AI off.
        Health BringHostileNear(CommandableUnit hostile, CommandableUnit caster, float distance, bool needSight = true)
        {
            hostile.GetComponent<EnemyAI>().enabled = false;
            hostile.Issue(new StopCommand());
            if (!TryFindVisibleSpot(caster, distance, out var spot, needSight))
            {
                Assert.Fail($"No open, visible spot {distance} m from {caster.name}");
                return null;
            }
            hostile.GetComponent<NavMeshAgent>().Warp(spot);
            return HealthOf(hostile);
        }

        // Stands a friendly `distance` metres from the caster in sight of it (spawn spacing alone does not guarantee Mend range).
        void BringFriendlyNear(CommandableUnit friendly, CommandableUnit caster, float distance)
        {
            friendly.Issue(new StopCommand());
            Assert.That(TryFindVisibleSpot(caster, distance, out var spot), Is.True, $"No open, visible spot {distance} m from {caster.name}");
            friendly.GetComponent<NavMeshAgent>().Warp(spot);
        }

        [UnityTest]
        public IEnumerator Scene_GeneratesAMissionOnLoad_WithTheWholeSquadAndHostiles()
        {
            yield return LoadMission();

            Assert.That(squad, Has.Length.EqualTo(3));
            Assert.That(hostiles, Has.Length.EqualTo(3));
            Assert.That(registry.Points.Any(p => p.Placement == CoverPlacement.Corner), Is.True);
            Assert.That(registry.Points.Any(p => p.Height == CoverHeight.Low), Is.True);
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));
            Assert.That(active.Unit, Is.EqualTo(squad[0]));
        }

        [UnityTest]
        public IEnumerator Scene_Move_ThenAttack_ThenTheHostileDies()
        {
            yield return LoadMission();
            var hostile = BringHostileNear(hostiles[0], squad[0], 6f);

            // Move: a reachable point 6 m away; the unit must get there, not merely end its order.
            var start = squad[1].transform.position;
            Assert.That(TryFindOrderPoint(squad[1], 6f, out var destination, mustBeSeen: false), Is.True, "reachable open floor 6 m away");
            Assert.That(squad[1].Issue(new MoveCommand(destination)), Is.True);
            yield return TestWorld.WaitUntil(() => squad[1].CurrentCommand == null, 10f);
            Assert.That(squad[1].CurrentCommand, Is.Null, "the move order ended");
            Assert.That(TestWorld.HorizontalDistance(squad[1].transform.position, destination), Is.LessThan(1f), "it arrived");
            Assert.That(TestWorld.HorizontalDistance(squad[1].transform.position, start), Is.GreaterThan(4f), "it travelled");

            // Attack: the companions would assist on the same hostile, so they are switched off: only the leader's melee
            // attack can kill it.
            squad[1].GetComponent<CompanionAI>().enabled = false;
            squad[2].GetComponent<CompanionAI>().enabled = false;
            var attacker = squad[0].GetComponent<UnitAttacker>();
            var hitsBefore = attacker.Hits;
            var reachedAttack = false;
            Assert.That(squad[0].Issue(new AttackCommand(hostile)), Is.True);
            yield return TestWorld.WaitUntil(() =>
            {
                reachedAttack |= squad[0].AttackPhase == AttackPhase.Attack;
                return !hostile.IsAlive;
            }, 20f);

            Assert.That(hostile.IsAlive, Is.False);
            Assert.That(reachedAttack, Is.True, "the leader closed in and attacked");
            Assert.That((attacker.Hits - hitsBefore) * attacker.Damage, Is.GreaterThanOrEqualTo(hostile.Max), "the leader's own hits killed it");
            Assert.That(encounter.LivingHostiles, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator Scene_MoveToCover_ReachesAGeneratedLocation_AndOccupiesIt()
        {
            yield return LoadMission();
            var unit = squad[0];
            var location = registry.Points.Where(p => p.Height == CoverHeight.Low)
                .OrderBy(p => TestWorld.HorizontalDistance(p.Position, unit.transform.position)).First();

            Assert.That(unit.Issue(new MoveToCoverCommand(location)), Is.True);
            yield return TestWorld.WaitUntil(() => unit.Cover.Status == CoverStatus.Occupied, 15f);

            Assert.That(unit.Cover.Status, Is.EqualTo(CoverStatus.Occupied));
            Assert.That(unit.Cover.Point, Is.EqualTo(location));
        }

        [UnityTest]
        public IEnumerator Scene_Abilities_AimedShotHitsInRange_BlastHitsTheGround_MendHeals_AndRangeAndCooldownApply()
        {
            yield return LoadMission();
            var caster = squad[1];   // the Marksman
            var abilities = caster.GetComponent<UnitAbilities>();
            var hostile = BringHostileNear(hostiles[0], caster, 9f);
            var before = hostile.Current;

            Assert.That(caster.Issue(AbilityCommand.OnUnit(abilities.Definition(0), hostile)), Is.True);
            yield return TestWorld.WaitUntil(() => hostile.Current < before, 3f);
            Assert.That(hostile.Current, Is.LessThan(before), "single-target ability");
            Assert.That(abilities.IsReady(0), Is.False, "cooldown starts");
            Assert.That(abilities.Check(abilities.Definition(0), hostile, null).Failure, Is.EqualTo(AbilityFailure.OnCooldown),
                "a second Aimed Shot at the same hostile, in range, is refused for the cooldown");

            var second = BringHostileNear(hostiles[1], caster, 8f);
            var secondBefore = second.Current;
            var friendlyBefore = squad.Select(u => HealthOf(u).Current).ToArray();
            Assert.That(caster.Issue(AbilityCommand.AtGround(abilities.Definition(1), second.transform.position)), Is.True);
            yield return TestWorld.WaitUntil(() => second.Current < secondBefore, 3f);
            Assert.That(second.Current, Is.LessThan(secondBefore), "ground-targeted ability");
            Assert.That(squad.Select(u => HealthOf(u).Current).ToArray(), Is.EqualTo(friendlyBefore), "no friendly fire");

            BringFriendlyNear(squad[2], caster, 3f);
            HealthOf(squad[2]).TakeDamage(50);
            var hurt = HealthOf(squad[2]).Current;
            Assert.That(caster.Issue(AbilityCommand.OnUnit(abilities.Definition(2), HealthOf(squad[2]))), Is.True,
                "the Marksman stands within Mend range of the squad");
            yield return TestWorld.WaitUntil(() => HealthOf(squad[2]).Current > hurt, 3f);
            Assert.That(HealthOf(squad[2]).Current, Is.GreaterThan(hurt), "Mend heals");

            // Range: the leader has not used its Aimed Shot, so only the distance (beyond 14 m) can refuse it. Range is checked
            // before sight, so the spot need not be in sight.
            var ready = squad[0].GetComponent<UnitAbilities>();
            Assert.That(ready.IsReady(0), Is.True, "precondition: the leader's Aimed Shot is ready");
            var far = BringHostileNear(hostiles[2], squad[0], 17f, needSight: false);
            var farCheck = ready.Check(ready.Definition(0), far, null);
            Assert.That(farCheck.Distance, Is.GreaterThan(ready.Definition(0).Range));
            Assert.That(farCheck.Failure, Is.EqualTo(AbilityFailure.OutOfRange), "a hostile beyond Aimed Shot's range is refused for the range");
        }

        [UnityTest]
        public IEnumerator Scene_TacticalPause_FreezesSimulation_PlansOrders_AndResumesThem()
        {
            yield return LoadMission();
            var unit = squad[0];
            var start = unit.transform.position;
            pause.Pause();
            Assert.That(unit.Issue(new MoveCommand(start - Vector3.up + new Vector3(2f, 0f, 0f))), Is.True);
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(Vector3.Distance(unit.transform.position, start), Is.LessThan(0.05f), "frozen while paused");

            pause.Resume();
            yield return TestWorld.WaitUntil(() => Vector3.Distance(unit.transform.position, start) > 1f, 5f);
            Assert.That(Vector3.Distance(unit.transform.position, start), Is.GreaterThan(1f));
        }

        [UnityTest]
        public IEnumerator Scene_EnemyAI_AcquiresAFriendlyInSight_AndTheCompanionsFollow()
        {
            yield return LoadMission();
            var hostile = hostiles[0];
            // Inside the hostile's 12 m detection range; the nearer distances are for a leader standing near a room wall.
            var spot = default(Vector3);
            var found = new[] { 8f, 7f, 6f, 5f, 4f }.Any(d => TryFindVisibleSpot(squad[0], d, out spot));
            Assert.That(found, Is.True, "an open spot in sight of the leader");
            hostile.GetComponent<NavMeshAgent>().Warp(spot);   // its AI stays on: it must acquire the leader itself
            yield return TestWorld.WaitUntil(() => hostile.CurrentCommand is AttackCommand || hostile.CurrentCommand is MoveToCoverCommand, 5f);
            Assert.That(hostile.CurrentCommand, Is.Not.Null, "a hostile within detection range and sight acts");

            // End the fight before the follow check: a companion assists before it follows, and a ranged one assists
            // from where it stands, so with this hostile alive whether it moves depends on the fight.
            HealthOf(hostile).TakeDamage(1000);
            yield return null;

            active.SetUnit(squad[0]);
            var companion = squad[2].GetComponent<CompanionAI>();
            var far = squad[2].transform.position;
            var leaderStart = squad[0].transform.position;
            // Away from the companion, so it ends up beyond its follow start distance.
            Assert.That(TryFindOrderPoint(squad[0], 8f, out var destination, mustBeSeen: false, awayFrom: far), Is.True,
                "reachable open floor 8 m from the leader");
            Assert.That(squad[0].Issue(new MoveCommand(destination)), Is.True);
            var followed = false;
            yield return TestWorld.WaitUntil(() =>
            {
                followed |= companion.IsFollowing;
                return followed && TestWorld.HorizontalDistance(squad[2].transform.position, far) > 1f;
            }, 8f);
            Assert.That(TestWorld.HorizontalDistance(squad[0].transform.position, leaderStart), Is.GreaterThan(1f), "the leader moved");
            Assert.That(followed, Is.True, "the companion gave itself a follow order");
            Assert.That(TestWorld.HorizontalDistance(squad[2].transform.position, far), Is.GreaterThan(1f), "a companion moves with the leader");
        }

        [UnityTest]
        public IEnumerator Scene_VictoryAndDefeat_Resolve()
        {
            yield return LoadMission();
            foreach (var hostile in hostiles)
                HealthOf(hostile).TakeDamage(1000);
            yield return null;
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Victory));

            yield return LoadMission();
            foreach (var friendly in squad)
                HealthOf(friendly).TakeDamage(1000);
            yield return null;
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Defeat));
        }

        [UnityTest]
        public IEnumerator Scene_KeyboardF6_Regenerates_AndLeavesNoStaleUnitsOrCover()
        {
            yield return LoadMission();
            var oldUnits = squad.Concat(hostiles).ToArray();
            var oldLocations = registry.Points.ToList();
            var hash = director.Report.LayoutHash;

            Press(keyboard.f6Key);
            yield return null;
            Release(keyboard.f6Key);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready, 20f);
            yield return null;

            Assert.That(director.Report.LayoutHash, Is.EqualTo(hash), "same seed, same layout");
            Assert.That(oldUnits.All(u => u == null), Is.True);
            Assert.That(oldLocations.All(l => !l.IsValid), Is.True);
            Assert.That(Object.FindObjectsByType<EnemyAI>(FindObjectsSortMode.None), Has.Length.EqualTo(3));
            Assert.That(Object.FindObjectsByType<SelectableUnit>(FindObjectsSortMode.None), Has.Length.EqualTo(3));
        }

        [UnityTest]
        public IEnumerator Scene_KeyboardAndMouse_StillOrderTheSelectedUnit()
        {
            yield return LoadMission();
            selection.Select(squad[0].GetComponent<SelectableUnit>());
            Assert.That(TryFindOrderPoint(squad[0], 3f, out var target), Is.True, "open floor the camera sees 3 m from the unit");
            Set(mouse.position, (Vector2)viewCamera.WorldToScreenPoint(target));
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;

            Assert.That(squad[0].CurrentCommand is MoveCommand || squad[0].CurrentCommand is MoveToCoverCommand, Is.True);
        }

        // A port of PrototypeSceneControllerTests.Scene_PadCover_CursorSnapsToGeneratedCover_AndMoveToCoverQueues, plus
        // the ability and target-cycling snaps: the cursor snaps around the ground point under it, so the camera is put
        // over each thing the cursor has to reach (the camera does not follow the active character without takeover).
        [UnityTest]
        public IEnumerator Scene_Pad_CursorSnapsToGeneratedCoverEnemiesAndFriendlies_AndCameraStaysUsable()
        {
            yield return LoadMission();
            var family = Object.FindFirstObjectByType<ActiveInputDevice>();
            var cameraRig = Object.FindFirstObjectByType<TacticalCameraController>();
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Tap(pad.selectButton);   // View is unbound: it only wakes the pad
            Assert.That(family.Family, Is.EqualTo(InputFamily.Xbox));
            yield return Tap(pad.startButton);
            Assert.That(pause.IsPaused, Is.True, "Start pauses");

            var unit = squad[0];
            yield return CursorOn(unit.transform.position);
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            yield return Tap(pad.buttonSouth);
            Assert.That(selection.Selected.Select(s => s.Unit), Is.EquivalentTo(new[] { unit }));

            // Cover: the nearest open low location the cursor can snap to.
            CoverLocation chosen = null;
            var candidates = registry.Points.Where(p => p.IsValid && p.Height == CoverHeight.Low)
                .OrderBy(p => CoverRules.FlatDistance(p.Position, unit.transform.position)).ToList();
            foreach (var point in candidates)
            {
                var crowded = squad.Concat(hostiles).Any(u => CoverRules.FlatDistance(u.transform.position, point.Position) < 2.5f);
                if (crowded)
                    continue;
                cameraRig.FocusOn(point.Position);
                yield return null;
                yield return CursorOn(point.Position);
                if (cursor.Target.Kind == PointerTargetKind.Cover && cursor.Target.Cover == point)
                {
                    chosen = point;
                    break;
                }
            }
            Assert.That(chosen, Is.Not.Null, $"The cursor could not snap to any of {candidates.Count} open low cover locations");

            yield return Tap(pad.buttonSouth);
            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveToCoverCommand>(), "Confirm on cover orders MoveToCover");
            Assert.That(pause.IsPaused, Is.True, "the order waits for the resume");

            // An ability: RT + D-pad down arms Mend, and the cursor then snaps to friendlies only.
            var ally = squad[2];
            cameraRig.FocusOn(ally.transform.position);
            yield return null;
            Press(pad.rightTrigger);
            yield return null;
            yield return Tap(pad.dpad.down);
            Release(pad.rightTrigger);
            yield return null;
            yield return null;
            Assert.That(targeting.ArmedAbility.DisplayName, Is.EqualTo("Mend"));
            Assert.That(cursor.SnapTo, Is.EqualTo(PointerTargetKind.Friendly));
            // Floor beside the ally (not the ally itself, within the 1.2 m snap radius), so the friendly is reached by the
            // snap, not by the ray. Floor right behind a unit is hidden by its body, and floor near a wall by the wall.
            var beside = new[] { 0.8f, 1f, 1.1f }.SelectMany(r => Around.Select(d => ally.transform.position - Vector3.up + d * r))
                .Where(CameraSeesGround).ToList();
            Assert.That(beside, Is.Not.Empty, "the camera sees floor beside the ally");
            yield return CursorOn(beside[0]);
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly), "the armed heal snaps to a friendly");
            Assert.That(cursor.Target.Friendly.Unit, Is.SameAs(ally));
            yield return Tap(pad.buttonEast);
            Assert.That(targeting.IsArmed, Is.False, "Cancel backs out of the ability");

            // Target cycling: D-pad right puts the cursor on the hostile nearest the active character.
            var nearest = hostiles.OrderBy(h => CoverRules.FlatDistance(h.transform.position, active.Unit.transform.position)).First();
            cameraRig.FocusOn(nearest.transform.position);
            yield return null;
            yield return Tap(pad.dpad.right);
            Assert.That(cursor.SoftTarget, Is.SameAs(HealthOf(nearest)), "NextTarget picks the nearest hostile");
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Hostile), "and the cursor snaps onto it");
            Assert.That(cursor.Target.Hostile, Is.SameAs(HealthOf(nearest)));

            // Running again: the camera has the right stick back.
            yield return Tap(pad.startButton);
            Assert.That(pause.IsPaused, Is.False);
            Assert.That(cursor.IsActive, Is.False, "Running with takeover off: the camera owns the right stick");
            var yaw = cameraRig.Yaw;
            Set(pad.rightStick, new Vector2(1f, 0f));
            yield return new WaitForSecondsRealtime(0.3f);
            Set(pad.rightStick, Vector2.zero);
            yield return null;
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(yaw, cameraRig.Yaw)), Is.GreaterThan(5f), "the right stick turns the camera");
        }
    }
}
#endif
