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

        // The scene's director draws a fresh seed at start (decision 026), so it opens on a different level every time.
        // The tests need a known layout: wait for that first mission, then regenerate seed 12345 and bind to it. The
        // first generation's state is not asserted on, but a drawn seed that exhausts every attempt calls Debug.LogError,
        // which fails the test anyway (unlikely: seeds 1 to 200 all generate within 6 attempts). The second must succeed.
        IEnumerator LoadMission()
        {
            var loading = SceneManager.LoadSceneAsync("ProceduralMission", LoadSceneMode.Single);
            Assert.That(loading, Is.Not.Null, "The ProceduralMission scene is not in the build settings: run the scene builder");
            yield return loading;
            director = Object.FindFirstObjectByType<MissionDirector>();
            Assert.That(director, Is.Not.Null, "The scene has no MissionDirector: run the scene builder");
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 20f);
            Assert.That(director.Generate(DeterministicSeed), Is.True,
                $"the first, drawn-seed mission is still {director.State} after 20 s, so Generate was refused");
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 20f);
            Assert.That(director.State, Is.EqualTo(MissionState.Ready), director.Report.Failure);
            Bind();
            yield return null;
        }

        const int DeterministicSeed = 12345;

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

        // An open NavMesh spot `distance` metres from `from` that `from` can see (unless `needSight` is false). A room is
        // only 7 to 10 m across, so when no visible spot lies exactly `distance` away (layouts change with the generator), the
        // nearest distance within 3 m of it that has one is used: still well inside every range these tests rely on.
        bool TryFindVisibleSpot(CommandableUnit from, float distance, out Vector3 spot, bool needSight = true)
        {
            var attacker = from.GetComponent<UnitAttacker>();
            var ground = from.transform.position - Vector3.up;
            for (var offset = 0f; offset <= (needSight ? 3f : 0f); offset += 0.5f)
            {
                foreach (var sign in new[] { 0f, -1f, 1f })
                {
                    if (offset == 0f && sign != 0f || offset > 0f && sign == 0f)
                        continue;
                    var radius = Mathf.Max(1f, distance + sign * offset);   // never the caster's own spot
                    for (var step = 0; step < 16; step++)
                    {
                        var angle = step * Mathf.PI * 2f / 16f;
                        var candidate = ground + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                        if (!NavMesh.SamplePosition(candidate, out var hit, 0.5f, NavMesh.AllAreas))
                            continue;
                        if (needSight && !attacker.HasLineOfSightToPoint(hit.position + Vector3.up))
                            continue;
                        spot = hit.position;
                        return true;
                    }
                }
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
            Assert.That(hostiles, Has.Length.EqualTo(director.Report.HostileSpawns + director.Report.Guards));
            Assert.That(registry.Points.Any(p => p.Placement == CoverPlacement.Corner), Is.True);
            Assert.That(registry.Points.Any(p => p.Height == CoverHeight.Low), Is.True);
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));
            Assert.That(active.Unit, Is.EqualTo(squad[0]));
            Assert.That(squad[0].GetComponent<UnitAnimationDriver>(), Is.Not.Null, "slot 0 is Darius");
            Assert.That(squad[0].GetComponent<UnitAttacker>().Archetype.Role, Is.EqualTo(CombatRole.Ranged), "Darius carries a rifle: a ranged unit, not a melee one");
            Assert.That(squad[0].GetComponent<UnitAttacker>().Range, Is.GreaterThan(2f), "his attack range is a ranged one");
        }

        [UnityTest]
        public IEnumerator Scene_OpensOnADrawnSeed_NotOnTheInspectorSeed_EveryTime()
        {
            // Two Plays (two loads). Only facts that cannot collide: the flag is on in the scene (it takes the field's
            // default, the scene file does not store it) and the seed in use is not the stored one, which a clock draw
            // never equals in practice (one value in 2^31). Two loads drawing different seeds is not asserted: the
            // clock could repeat.
            for (var load = 1; load <= 2; load++)
            {
                var loading = SceneManager.LoadSceneAsync("ProceduralMission", LoadSceneMode.Single);
                yield return loading;
                var sceneDirector = Object.FindFirstObjectByType<MissionDirector>();
                Assert.That(sceneDirector, Is.Not.Null, $"load {load}");
                Assert.That(sceneDirector.NewSeedAtStart, Is.True, $"load {load}: the scene draws a new seed at start");
                yield return TestWorld.WaitUntil(() => sceneDirector.State == MissionState.Ready || sceneDirector.State == MissionState.Failed, 20f);
                Assert.That(sceneDirector.Report.Seed, Is.Not.EqualTo(DeterministicSeed), $"load {load}");
                Assert.That(sceneDirector.Settings.seed, Is.EqualTo(sceneDirector.Report.Seed), $"load {load}: the Inspector shows the seed in use");
                PrototypeSceneTests.DestroySceneObjects();
            }
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

            // Attack: the companions would assist on the same hostile, so they are switched off: only the leader's own
            // (ranged) attack can kill it. The hostile stands 6 m away, inside the leader's range, so it may fire at once.
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
            Assert.That(reachedAttack, Is.True, "the leader attacked");
            Assert.That((attacker.Hits - hitsBefore) * attacker.Damage, Is.GreaterThanOrEqualTo(hostile.Max), "the leader's own hits killed it");
            Assert.That(encounter.LivingHostiles, Is.EqualTo(hostiles.Length - 1));
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

        // Each ability is cast by the operative that carries it (decision 036): Kestrel's Aimed Shot, Darius's Blast,
        // Sable's Mend.
        [UnityTest]
        public IEnumerator Scene_Abilities_AimedShotHitsInRange_BlastHitsTheGround_MendHeals_AndRangeAndCooldownApply()
        {
            yield return LoadMission();
            var caster = squad[1];   // Kestrel, the Marksman: Aimed Shot is her slot 1
            var abilities = caster.GetComponent<UnitAbilities>();
            Assert.That(abilities.Definition(0).DisplayName, Is.EqualTo("Aimed Shot"), "precondition: Kestrel's slot 1");
            // The shot hostiles must not fight back: a retaliation would change the squad's health during the
            // "no friendly fire" check below, which is about the Blast alone.
            foreach (var target in hostiles.Take(2))
                if (target.TryGetComponent<AutoRetaliate>(out var hostileRetaliate))
                    hostileRetaliate.enabled = false;
            var hostile = BringHostileNear(hostiles[0], caster, 9f);
            var before = hostile.Current;

            Assert.That(caster.Issue(AbilityCommand.OnUnit(abilities.Definition(0), hostile)), Is.True);
            yield return TestWorld.WaitUntil(() => hostile.Current < before, 3f);
            Assert.That(hostile.Current, Is.LessThan(before), "single-target ability");
            Assert.That(abilities.IsReady(0), Is.False, "cooldown starts");
            Assert.That(abilities.Check(abilities.Definition(0), hostile, null).Failure, Is.EqualTo(AbilityFailure.OnCooldown),
                "a second Aimed Shot at the same hostile, in range, is refused for the cooldown");

            // Blast: Darius's slot 2. Kestrel's AI is off from here, so only the Blast can hurt the second hostile.
            caster.GetComponent<CompanionAI>().enabled = false;
            caster.Issue(new StopCommand());
            var blaster = squad[0];
            var blasterAbilities = blaster.GetComponent<UnitAbilities>();
            Assert.That(blasterAbilities.Definition(1).DisplayName, Is.EqualTo("Blast"), "precondition: Darius's slot 2");
            var second = BringHostileNear(hostiles[1], blaster, 8f);
            var secondBefore = second.Current;

            // A friendly inside the 3 m blast radius (1.5 m from the aim point), so "no friendly fire" is not vacuous. Its AI
            // is off, and so is the last hostile's, so no other source can change its health.
            var bystander = squad[2];
            bystander.GetComponent<CompanionAI>().enabled = false;
            if (bystander.TryGetComponent<AutoRetaliate>(out var retaliate))
                retaliate.enabled = false;
            hostiles[2].GetComponent<EnemyAI>().enabled = false;
            bystander.Issue(new StopCommand());
            var aim = second.transform.position;
            var bystanderSpot = default(NavMeshHit);
            var foundSpot = new[] { 1.5f, 1.2f, 1f, 0.8f }.SelectMany(r => Around.Select(d => aim + d * r)).Any(p =>
                NavMesh.SamplePosition(p, out bystanderSpot, 1.2f, NavMesh.AllAreas) && CoverRules.FlatDistance(bystanderSpot.position, aim) < 1.9f);
            Assert.That(foundSpot, Is.True, $"open floor within 2 m of the blast aim point {aim}");
            bystander.GetComponent<NavMeshAgent>().Warp(bystanderSpot.position);
            Assert.That(CoverRules.FlatDistance(bystander.transform.position, aim), Is.LessThan(2f), "the friendly stands in the blast radius");
            var friendlyBefore = squad.Select(u => HealthOf(u).Current).ToArray();
            Assert.That(blaster.Issue(AbilityCommand.AtGround(blasterAbilities.Definition(1), second.transform.position)), Is.True);
            yield return TestWorld.WaitUntil(() => second.Current < secondBefore, 3f);
            Assert.That(second.Current, Is.LessThan(secondBefore), "ground-targeted ability");
            Assert.That(squad.Select(u => HealthOf(u).Current).ToArray(), Is.EqualTo(friendlyBefore), "no friendly fire");

            // Mend: Sable's slot 1, on a hurt Kestrel standing 3 m from her.
            var healer = squad[2];
            var healerAbilities = healer.GetComponent<UnitAbilities>();
            Assert.That(healerAbilities.Definition(0).DisplayName, Is.EqualTo("Mend"), "precondition: Sable's slot 1");
            BringFriendlyNear(squad[1], healer, 3f);
            HealthOf(squad[1]).TakeDamage(50);
            var hurt = HealthOf(squad[1]).Current;
            Assert.That(healer.Issue(AbilityCommand.OnUnit(healerAbilities.Definition(0), HealthOf(squad[1]))), Is.True,
                "Sable stands within Mend range of Kestrel");
            yield return TestWorld.WaitUntil(() => HealthOf(squad[1]).Current > hurt, 3f);
            Assert.That(HealthOf(squad[1]).Current, Is.GreaterThan(hurt), "Mend heals");

            // Range: the leader has used its Blast but not its Aimed Shot (cooldowns are per slot), so only the distance
            // (beyond 14 m) can refuse it. Range is checked
            // before sight, so the spot need not be in sight.
            var ready = squad[0].GetComponent<UnitAbilities>();
            Assert.That(ready.IsReady(0), Is.True, "precondition: the leader's Aimed Shot is ready");
            var far = BringHostileNear(hostiles[2], squad[0], 17f, needSight: false);
            var farCheck = ready.Check(ready.Definition(0), far, null);
            Assert.That(farCheck.Distance, Is.GreaterThan(ready.Definition(0).Range));
            Assert.That(farCheck.Failure, Is.EqualTo(AbilityFailure.OutOfRange), "a hostile beyond Aimed Shot's range fails the range check");
            // Decision 029: that is not a refusal: the order is accepted and the leader walks into range.
            Assert.That(squad[0].Issue(AbilityCommand.OnUnit(ready.Definition(0), far)), Is.True, "accepted: the leader approaches");
            Assert.That(squad[0].CurrentCommand, Is.TypeOf<AbilityCommand>());
        }

        [UnityTest]
        public IEnumerator Scene_TacticalPause_FreezesSimulation_PlansOrders_AndResumesThem()
        {
            yield return LoadMission();
            var unit = squad[0];
            var start = unit.transform.position;
            Assert.That(TryFindOrderPoint(unit, 2f, out var destination, mustBeSeen: false), Is.True, "reachable open floor 2 m away");
            pause.Pause();
            Assert.That(unit.Issue(new MoveCommand(destination)), Is.True);
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
            // Away from the companion, so it ends up beyond its follow start distance (6 m): in a small spawn room the
            // nearest such floor can be in the next room, so farther distances are tried too.
            var destination = default(Vector3);
            var foundDestination = new[] { 8f, 10f, 12f, 14f }.Any(d =>
                TryFindOrderPoint(squad[0], d, out destination, mustBeSeen: false, awayFrom: far) && TestWorld.HorizontalDistance(destination, far) > 7f);
            Assert.That(foundDestination, Is.True, "reachable open floor 8 to 14 m from the leader and over 7 m from the companion");
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
            Assert.That(Object.FindObjectsByType<EnemyAI>(FindObjectsSortMode.None), Has.Length.EqualTo(director.Report.HostileSpawns + director.Report.Guards));
            Assert.That(Object.FindObjectsByType<SelectableUnit>(FindObjectsSortMode.None), Has.Length.EqualTo(3));
        }

        [UnityTest]
        public IEnumerator Scene_Regeneration_DisarmsAnArmedAbility_AtOnce()
        {
            // AbilityTargeting also disarms itself on its next Update (its caster is gone), so the director's own disarm is only
            // visible in the same frame the regeneration starts: the request is made directly, as F6 does, and checked at once.
            yield return LoadMission();
            yield return Tap(keyboard.digit2Key);
            Assert.That(targeting.IsArmed, Is.True, "precondition: key 2 armed the active character's second ability (Darius's Blast)");

            Assert.That(director.RegenerateSame(), Is.True);
            Assert.That(targeting.IsArmed, Is.False, "starting a regeneration disarms the ability armed for the old squad");
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready, 20f);
            yield return null;

            Assert.That(targeting.IsArmed, Is.False);
        }

        // The geometry the generator builds really blocks sight: open floor is in sight, floor behind a generated wall is not
        // (an Aimed Shot at a hostile there is refused for the sight, not for the range).
        [UnityTest]
        public IEnumerator Scene_AGeneratedWall_BlocksSight_AndOpenGroundDoesNot()
        {
            yield return LoadMission();
            var leader = squad[0];
            var attacker = leader.GetComponent<UnitAttacker>();
            var eye = leader.transform.position + Vector3.up * LineOfSight.EyeHeight;

            Assert.That(TryFindVisibleSpot(leader, 5f, out var open), Is.True, "open floor in sight of the leader");
            Assert.That(attacker.HasLineOfSightToPoint(open + Vector3.up), Is.True, "open ground does not block sight");

            // Floor 4 to 13 m away (inside Aimed Shot's 14 m) that the leader cannot see because a generated wall is in the way.
            var ground = leader.transform.position - Vector3.up;
            var hidden = default(Vector3);
            var found = false;
            for (var distance = 4f; distance <= 13f && !found; distance += 1f)
            {
                for (var step = 0; step < 32 && !found; step++)
                {
                    var angle = step * Mathf.PI * 2f / 32f;
                    var candidate = ground + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;
                    if (!NavMesh.SamplePosition(candidate, out var hit, 0.5f, NavMesh.AllAreas))
                        continue;
                    var target = hit.position + Vector3.up;
                    if (attacker.HasLineOfSightToPoint(target))
                        continue;
                    var toTarget = target - eye;
                    var wallInTheWay = Physics.RaycastAll(eye, toTarget.normalized, toTarget.magnitude, ~0, QueryTriggerInteraction.Ignore)
                        .Any(h => h.collider.transform.IsChildOf(director.Current.Geometry));
                    if (!wallInTheWay)
                        continue;
                    hidden = hit.position;
                    found = true;
                }
            }
            Assert.That(found, Is.True, "floor within 13 m hidden from the leader by a generated wall");

            var hostile = hostiles[0];
            hostile.GetComponent<EnemyAI>().enabled = false;
            hostile.Issue(new StopCommand());
            hostile.GetComponent<NavMeshAgent>().Warp(hidden);
            var abilities = leader.GetComponent<UnitAbilities>();
            Assert.That(abilities.IsReady(0), Is.True, "precondition: the leader's Aimed Shot is ready");
            var check = abilities.Check(abilities.Definition(0), HealthOf(hostile), null);

            Assert.That(attacker.HasLineOfSightToPoint(hostile.transform.position), Is.False, "the wall blocks sight to the hostile");
            Assert.That(check.Distance, Is.LessThanOrEqualTo(abilities.Definition(0).Range), "in range: only the sight can refuse it");
            Assert.That(check.Failure, Is.EqualTo(AbilityFailure.NoLineOfSight));
            // Decision 029: not a refusal either: the order is accepted and the leader repositions.
            Assert.That(leader.Issue(AbilityCommand.OnUnit(abilities.Definition(0), HealthOf(hostile))), Is.True, "accepted: the leader repositions");
            Assert.That(leader.CurrentCommand, Is.TypeOf<AbilityCommand>());
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
            // Centre the camera on the unit first, as the steps below do for their targets: in a cluttered spawn room
            // the unit can stand where the default framing does not resolve it (decision 026, walls hide the floor).
            cameraRig.FocusOn(unit.transform.position);
            yield return null;
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

            // An ability: Mend is Sable's slot 1 (decision 036), so with Sable selected (the selection is the caster)
            // RT + D-pad up arms it, and the cursor then snaps to friendlies only.
            selection.Select(squad[2].GetComponent<SelectableUnit>());
            var ally = squad[1];
            cameraRig.FocusOn(ally.transform.position);
            yield return null;
            Press(pad.rightTrigger);
            yield return null;
            yield return Tap(pad.dpad.up);
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

        [UnityTest]
        public IEnumerator TheSceneSquad_IsThreeDistinctPersistentOperatives()
        {
            yield return LoadMission();
            var identities = squad.Select(u => u.GetComponent<UnitIdentity>()).ToArray();
            foreach (var identity in identities)
                Assert.That(identity != null, Is.True, "every squad member is an operative");

            Assert.That(identities.Select(i => i.DisplayName), Is.EqualTo(new[] { "Darius", "Kestrel", "Sable" }));
            Assert.That(identities.Select(i => i.RoleName), Is.EqualTo(new[] { "Assault", "Recon", "Support" }));
            Assert.That(identities.Select(i => i.OperativeId).Distinct().Count(), Is.EqualTo(3));
            Assert.That(squad.Select(u => HealthOf(u).Max), Is.EqualTo(new[] { 130, 80, 100 }));
            Assert.That(squad[0].GetComponent<UnitAnimationDriver>(), Is.Not.Null, "Darius keeps his model");
            Assert.That(squad[1].GetComponent<UnitAttacker>().Archetype.DisplayName, Is.EqualTo("Marksman"));
            Assert.That(active.Unit, Is.SameAs(squad[0]));
        }

        [UnityTest]
        public IEnumerator TheSceneRoster_KeepsXp_WhenTheMissionIsRegenerated()
        {
            yield return LoadMission();
            var roster = Object.FindFirstObjectByType<SquadRoster>();
            Assert.That(roster, Is.Not.Null);
            var kestrelId = squad[1].GetComponent<UnitIdentity>().OperativeId;
            Assert.That(roster.AwardExperience(kestrelId, 120), Is.True);

            Assert.That(director.Generate(777), Is.True);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 20f);
            Assert.That(director.State, Is.EqualTo(MissionState.Ready), director.Report.Failure);

            var kestrel = director.Friendlies[1].GetComponent<UnitIdentity>();
            Assert.That(kestrel.OperativeId, Is.EqualTo(kestrelId));
            Assert.That(kestrel.State.Experience, Is.EqualTo(120));
            Assert.That(director.Friendlies[0].GetComponent<UnitIdentity>().State.Experience, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator TheSceneHasTheOperativePanelAndItsDeveloperKeys()
        {
            yield return LoadMission();
            Assert.That(Object.FindFirstObjectByType<OperativePanelView>(), Is.Not.Null);
            Assert.That(Object.FindFirstObjectByType<OperativeDeveloperInput>(), Is.Not.Null);

            var panel = Object.FindFirstObjectByType<OperativePanelView>();
            Press(keyboard.f9Key);
            yield return null;
            Release(keyboard.f9Key);
            yield return null;
            Assert.That(panel.IsVisible, Is.True);
        }

        // The scene roster is wired to the scene's director: a mission that really ends in Success (objectives done, a
        // unit in the open extraction zone) awards every operative the track's mission-completion XP.
        [UnityTest]
        public IEnumerator TheSceneRoster_AwardsMissionCompletionXp_WhenTheSceneMissionSucceeds()
        {
            yield return LoadMission();
            var roster = Object.FindFirstObjectByType<SquadRoster>();
            Assert.That(roster, Is.Not.Null);
            Assert.That(roster.States().Select(s => s.Experience), Is.EqualTo(new[] { 0, 0, 0 }), "precondition: a fresh roster");

            foreach (var hostile in hostiles)
            {
                hostile.GetComponent<EnemyAI>().enabled = false;
                HealthOf(hostile).TakeDamage(HealthOf(hostile).Max);
            }
            squad[1].GetComponent<CompanionAI>().enabled = false;   // it waits in the zone instead of following the leader
            Assert.That(squad[1].GetComponent<NavMeshAgent>().Warp(director.Current.ExtractionZone.position), Is.True);
            var terminal = director.Current.Terminal;
            Assert.That(squad[0].GetComponent<UnitMover>().TrySnap(terminal.Position, out var stand), Is.True);
            Assert.That(squad[0].GetComponent<NavMeshAgent>().Warp(stand), Is.True);
            Assert.That(squad[0].Issue(new InteractCommand(terminal)), Is.True);
            yield return TestWorld.WaitUntil(() => director.Phase == MissionPhase.Success, 15f);
            Assert.That(director.Phase, Is.EqualTo(MissionPhase.Success));

            var xp = roster.Track.MissionCompletionXp;
            Assert.That(xp, Is.GreaterThan(0));
            Assert.That(roster.States().Select(s => s.Experience), Is.EqualTo(new[] { xp, xp, xp }),
                "the roster heard the scene director's MissionFinished(Success)");
        }
    }
}
#endif
