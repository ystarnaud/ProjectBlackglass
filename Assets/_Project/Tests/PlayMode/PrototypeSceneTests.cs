#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PrototypeSceneTests : InputTestFixture
    {
        static readonly string[] FriendlyNames = { "FriendlyUnit_1", "FriendlyUnit_2", "FriendlyUnit_3" };
        static readonly string[] HostileNames = { "HostileUnit_1", "HostileUnit_2", "HostileUnit_3" };

        CommandableUnit unit;
        Health firstHostile;
        TacticalPause pause;
        UnitSelection selection;
        Encounter encounter;

        // Loaded from each test rather than [UnitySetUp]: the scene must load after InputTestFixture has isolated the
        // input system, otherwise the scene's actions (shared InputActionAsset) leak into later fixtures.
        IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
            unit = GameObject.Find(FriendlyNames[0]).GetComponent<CommandableUnit>();
            firstHostile = FindHostileHealth(HostileNames[0]);
            pause = Object.FindFirstObjectByType<TacticalPause>();
            selection = Object.FindFirstObjectByType<UnitSelection>();
            encounter = Object.FindFirstObjectByType<Encounter>();
        }

        // Every obstacle of the arena: all of them are cover surfaces (not all of them yield cover, see CoverCountsPerObstacle).
        static readonly string[] ArenaObstacles =
        {
            "Obstacle_A", "Obstacle_B", "Obstacle_C", "Obstacle_D", "Obstacle_E", "Obstacle_F", "Obstacle_CentralWall",
            "Pillar_G", "Pillar_H", "Barrier_I", "Crate_J", "Crate_K", "LowWall_L", "LowWall_M", "LowWall_N",
        };

        // Decision 027: the locations each obstacle yields (corner count, face count). Low obstacles get face points along
        // every face; tall walls only corners that open outward (the peek point must be on the NavMesh, which is eroded
        // around neighbouring obstacles, and the stand point must be walkable: Obstacle_B, Obstacle_E and Obstacle_F keep
        // three of four corners; Obstacle_E's north-west candidate was already dropped by the stand-point filter before 027); tall pillars, crates and
        // stubs (Obstacle_A, Obstacle_D, Pillar_G, Pillar_H, Crate_J, Crate_K) none.
        static readonly (string name, int corners, int faces)[] CoverCountsPerObstacle =
        {
            ("Barrier_I", 4, 0), ("Obstacle_CentralWall", 4, 0), ("Obstacle_B", 3, 0), ("Obstacle_E", 3, 0), ("Obstacle_F", 3, 0),
            ("Obstacle_C", 0, 11), ("LowWall_L", 0, 6), ("LowWall_M", 0, 8), ("LowWall_N", 0, 4),
            ("Obstacle_A", 0, 0), ("Obstacle_D", 0, 0), ("Pillar_G", 0, 0), ("Pillar_H", 0, 0), ("Crate_J", 0, 0), ("Crate_K", 0, 0),
        };

        internal static CommandableUnit[] FindSquad() =>
            FriendlyNames.Select(n => GameObject.Find(n).GetComponent<CommandableUnit>()).ToArray();

        internal static EnemyAI[] FindHostiles() =>
            HostileNames.Select(n => GameObject.Find(n).GetComponent<EnemyAI>()).ToArray();

        internal static Health FindHostileHealth(string name) => GameObject.Find(name).GetComponent<Health>();

        public override void TearDown()
        {
            Time.timeScale = 1f;
            DestroySceneObjects();
            base.TearDown();
        }

        // The scene's components share the project's InputActionAsset; destroy them while the input isolation is
        // still active so their actions are disabled and do not leak into later tests.
        internal static void DestroySceneObjects()
        {
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                Object.DestroyImmediate(root);
        }

        [UnityTest]
        public IEnumerator Cover_IsDiscoveredFromTheArenaGeometry_LowWallsAndTallWallCorners()
        {
            yield return LoadScene();
            var registry = Object.FindFirstObjectByType<CoverRegistry>();
            yield return TestWorld.WaitUntil(() => registry.Points.Count > 0, 2f);
            Assert.That(registry.Points, Is.Not.Empty, "Cover is discovered at start");

            foreach (var wall in new[] { "LowWall_L", "LowWall_M", "LowWall_N" })
            {
                var collider = GameObject.Find(wall).GetComponent<Collider>();
                var own = registry.Points.Where(p => p.Obstacle == collider).ToList();
                Assert.That(own, Is.Not.Empty, $"{wall} has cover");
                Assert.That(own.All(p => p.Height == CoverHeight.Low && p.Placement == CoverPlacement.Face), Is.True, wall);
            }
            Assert.That(registry.Points.Any(p => p.Name == "Cover_LowWall_L_S1"), Is.True, "The south-west point of LowWall_L keeps its name");

            var barrier = GameObject.Find("Barrier_I").GetComponent<Collider>();
            var barrierCorners = registry.Points.Where(p => p.Obstacle == barrier && p.Placement == CoverPlacement.Corner).ToList();
            Assert.That(barrierCorners, Has.Count.EqualTo(4), "A free-standing tall wall has four corners");
            Assert.That(barrierCorners.All(p => p.HasPeek && p.Height == CoverHeight.Tall), Is.True);

            var central = GameObject.Find("Obstacle_CentralWall").GetComponent<Collider>();
            var centralCorners = registry.Points.Where(p => p.Obstacle == central && p.Placement == CoverPlacement.Corner).ToList();
            Assert.That(centralCorners.Count, Is.EqualTo(4),
                "The 45-degree central wall keeps all four corners, including the one about 2.1 m from Barrier_I's east-end corners:" +
                "locations of different objects never remove each other");

            Assert.That(CoverCountsPerObstacle.Select(c => c.name), Is.EquivalentTo(ArenaObstacles), "every obstacle has an expectation");
            foreach (var (name, corners, faces) in CoverCountsPerObstacle)
            {
                var collider = GameObject.Find(name).GetComponent<Collider>();
                var own = registry.Points.Where(p => p.Obstacle == collider).ToList();
                Assert.That(own.Count(p => p.Placement == CoverPlacement.Corner), Is.EqualTo(corners), $"{name} corner locations");
                Assert.That(own.Count(p => p.Placement == CoverPlacement.Face), Is.EqualTo(faces), $"{name} face locations");
            }
            Assert.That(registry.Points.Where(p => p.Height == CoverHeight.Tall).All(p => p.Placement == CoverPlacement.Corner && p.HasPeek), Is.True,
                "every Tall location is a corner that opens outward: no cover along a tall wall");
            Assert.That(registry.Points, Has.Count.EqualTo(CoverCountsPerObstacle.Sum(c => c.corners + c.faces)), "46 locations in all");

            // The scene stores the generation settings on its CoverDiscovery component, which overrides the class
            // defaults: pin them, and pin the gaps they produce (a 3 m wall: usable 2 m, three south points 1 m apart).
            var settings = Object.FindFirstObjectByType<CoverDiscovery>().Settings;
            Assert.That(settings.spacing, Is.EqualTo(1f), "The scene's CoverDiscovery spacing");
            Assert.That(settings.mergeDistance, Is.EqualTo(0.9f), "The scene's CoverDiscovery mergeDistance");
            var lowWallL = GameObject.Find("LowWall_L").GetComponent<Collider>();
            var south = registry.Points.Where(p => p.Obstacle == lowWallL && p.Facing.z > 0.5f).OrderBy(p => p.Position.x).ToList();
            Assert.That(south, Has.Count.EqualTo(3), "LowWall_L's south face has three points at 1 m spacing");
            for (var i = 1; i < south.Count; i++)
            {
                var gap = south[i].Position.x - south[i - 1].Position.x;
                Assert.That(gap, Is.GreaterThanOrEqualTo(1f - 1e-3f), $"{south[i - 1].Name} to {south[i].Name}");
                Assert.That(gap, Is.LessThan(2f), $"{south[i - 1].Name} to {south[i].Name}");
            }
        }

        [UnityTest]
        public IEnumerator NoDiscoveredLocation_SitsWithinTheOccupyRadiusOfAnyUnitStart()
        {
            yield return LoadScene();
            var registry = Object.FindFirstObjectByType<CoverRegistry>();
            yield return TestWorld.WaitUntil(() => registry.Points.Count > 0, 2f);
            Assert.That(registry.Points, Is.Not.Empty, "Cover is discovered at start");

            var units = Object.FindObjectsByType<CommandableUnit>(FindObjectsSortMode.None);
            Assert.That(units, Has.Length.EqualTo(FriendlyNames.Length + HostileNames.Length), "Three friendlies and three hostiles");
            foreach (var unit in units)
            {
                var radius = unit.Cover.OccupyRadius;
                foreach (var point in registry.Points)
                    Assert.That(CoverRules.FlatDistance(unit.transform.position, point.Position), Is.GreaterThan(radius),
                        $"{point.Name} would be claimed by {unit.name} standing at its start");
            }
        }

        [UnityTest]
        public IEnumerator Scene_ContainsWiredSquadAndHostiles_AndRunsWithoutErrors()
        {
            yield return LoadScene();
            var friendlies = Object.FindObjectsByType<SelectableUnit>(FindObjectsSortMode.None);
            Assert.That(friendlies.Select(f => f.name), Is.EquivalentTo(FriendlyNames));
            Assert.That(selection, Is.Not.Null, "UnitSelection missing");
            Assert.That(selection.Roster, Is.EquivalentTo(friendlies));
            Assert.That(selection.Selected, Is.Empty, "Nothing should be selected at start");
            foreach (var friendly in friendlies)
            {
                Assert.That(friendly.GetComponent<SelectionIndicator>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponent<CommandQueueView>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponent<Health>(), Is.Not.Null, $"{friendly.name} has no Health");
                Assert.That(friendly.GetComponent<Health>().Max, Is.EqualTo(100), friendly.name);
                Assert.That(friendly.GetComponent<HitFlash>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponent<DeathMarker>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponent<AutoRetaliate>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponent<AttackLineView>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponent<CompanionAI>(), Is.Not.Null, $"{friendly.name} has no CompanionAI");
                Assert.That(friendly.GetComponent<CompanionAI>().IsWired, Is.True, $"{friendly.name}'s CompanionAI is not wired");
                // Phase 7 archetypes: FriendlyUnit_2 is the Marksman (long range, hard hitting), FriendlyUnit_3 the standard ranged.
                var (expectedRole, expectedRange, expectedDamage) = friendly.name switch
                {
                    "FriendlyUnit_2" => (CombatRole.Ranged, 16f, 40),
                    "FriendlyUnit_3" => (CombatRole.Ranged, 8f, 15),
                    _ => (CombatRole.Melee, 2f, 25),
                };
                Assert.That(friendly.GetComponent<UnitAttacker>().Role, Is.EqualTo(expectedRole), friendly.name);
                Assert.That(friendly.GetComponent<UnitAttacker>().Range, Is.EqualTo(expectedRange), friendly.name);
                Assert.That(friendly.GetComponent<UnitAttacker>().Damage, Is.EqualTo(expectedDamage), friendly.name);
                Assert.That(friendly.GetComponent<EnemyAI>(), Is.Null, $"{friendly.name} must not have enemy AI");
                Assert.That(friendly.GetComponentsInChildren<Collider>(true), Has.Length.EqualTo(1),
                    $"{friendly.name}: only the capsule may have a collider, so debug visuals never block clicks");
            }

            Assert.That(GameObject.Find("TrainingDummy"), Is.Null, "The training dummy should be gone");
            var hostiles = Object.FindObjectsByType<EnemyAI>(FindObjectsSortMode.None);
            Assert.That(hostiles.Select(h => h.name), Is.EquivalentTo(HostileNames));
            foreach (var hostile in hostiles)
            {
                Assert.That(hostile.GetComponent<Health>().Max, Is.EqualTo(60), hostile.name);
                var ranged = hostile.name == "HostileUnit_3";
                Assert.That(hostile.GetComponent<UnitAttacker>().Role, Is.EqualTo(ranged ? CombatRole.Ranged : CombatRole.Melee), hostile.name);
                Assert.That(hostile.GetComponent<UnitAttacker>().Range, Is.EqualTo(ranged ? 8f : 2f), hostile.name);
                Assert.That(hostile.GetComponent<UnitAttacker>().Damage, Is.EqualTo(ranged ? 8 : 10), hostile.name);
                Assert.That(hostile.GetComponent<UnitAttacker>().Cooldown, Is.EqualTo(ranged ? 1.5f : 1.2f).Within(0.001f), hostile.name);
                Assert.That(hostile.GetComponent<CompanionAI>(), Is.Null, $"{hostile.name} must not have companion AI");
                Assert.That(hostile.GetComponent<HitFlash>(), Is.Not.Null, hostile.name);
                Assert.That(hostile.GetComponent<DeathMarker>(), Is.Not.Null, hostile.name);
                Assert.That(hostile.GetComponent<AutoRetaliate>(), Is.Not.Null, hostile.name);
                Assert.That(hostile.GetComponent<AttackLineView>(), Is.Not.Null, hostile.name);
                Assert.That(hostile.GetComponent<SelectableUnit>(), Is.Null, $"{hostile.name} must not be selectable");
                Assert.That(hostile.GetComponentsInChildren<Collider>(true), Has.Length.EqualTo(1), hostile.name);
                Assert.That(hostile.State, Is.EqualTo(EnemyState.Idle), $"{hostile.name} should start idle, out of range of the squad");
            }

            Assert.That(encounter, Is.Not.Null, "Encounter missing");
            Assert.That(encounter.Friendlies.Select(h => h.name), Is.EquivalentTo(FriendlyNames));
            Assert.That(encounter.Hostiles.Select(h => h.name), Is.EquivalentTo(HostileNames));
            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));
            Assert.That(GameObject.Find("Obstacle_E"), Is.Not.Null);
            Assert.That(GameObject.Find("Obstacle_F"), Is.Not.Null);
            foreach (var obstacle in new[] { "Pillar_G", "Pillar_H", "Barrier_I", "Crate_J", "Crate_K" })
                Assert.That(GameObject.Find(obstacle), Is.Not.Null, $"{obstacle} missing");
            foreach (var wall in new[] { "LowWall_L", "LowWall_M", "LowWall_N" })
                Assert.That(GameObject.Find(wall), Is.Not.Null, $"{wall} missing");
            var registry = Object.FindFirstObjectByType<CoverRegistry>();
            Assert.That(registry, Is.Not.Null, "CoverRegistry missing");
            Assert.That(GameObject.Find("CoverPoints"), Is.Null, "Hand-placed cover points are gone: cover is discovered");
            Assert.That(Object.FindFirstObjectByType<CoverDiscovery>(), Is.Not.Null, "CoverDiscovery missing");
            foreach (var surfaceName in ArenaObstacles)
                Assert.That(GameObject.Find(surfaceName).GetComponent<CoverSurface>(), Is.Not.Null, $"{surfaceName} is not cover-generating");
            yield return TestWorld.WaitUntil(() => registry.Points.Count > 0, 2f);
            Assert.That(registry.Points, Is.Not.Empty, "Cover is discovered at start");
            foreach (var point in registry.Points)
            {
                Assert.That(point.Obstacle, Is.Not.Null, $"{point.Name} has no obstacle");
                Assert.That(point.IsValid, Is.True, point.Name);
                Assert.That(point.IsClaimed, Is.False, $"{point.Name} starts claimed");
                Assert.That(UnityEngine.AI.NavMesh.SamplePosition(point.Position, out _, 0.5f, UnityEngine.AI.NavMesh.AllAreas), Is.True,
                    $"{point.Name} at {point.Position} is off the NavMesh");
            }
            Assert.That(Object.FindFirstObjectByType<CoverView>(), Is.Not.Null, "CoverView missing");
            Assert.That(Object.FindFirstObjectByType<PlayerCommandInput>().IsCoverWired, Is.True, "PlayerCommandInput.coverRegistry is not wired");
            foreach (var hostile in hostiles)
                Assert.That(hostile.IsCoverWired, Is.True, $"{hostile.name}'s EnemyAI has no cover registry");
            foreach (var unit in friendlies.Select(f => f.Unit).Concat(hostiles.Select(h => h.GetComponent<CommandableUnit>())))
                Assert.That(unit.Cover.IsWired, Is.True, $"{unit.name}'s UnitCover is not wired");

            Assert.That(pause, Is.Not.Null, "TacticalPause missing");
            Assert.That(Object.FindFirstObjectByType<PlayerCommandInput>(), Is.Not.Null);
            Assert.That(Object.FindFirstObjectByType<TacticalCameraController>(), Is.Not.Null);
            var hud = Object.FindFirstObjectByType<PrototypeHud>();
            Assert.That(hud, Is.Not.Null);
            Assert.That(hud.IsEncounterWired, Is.True, "PrototypeHud.encounter is not wired in the scene");
            var active = Object.FindFirstObjectByType<ActiveCharacter>();
            Assert.That(active, Is.Not.Null, "ActiveCharacter missing");
            Assert.That(active.Unit, Is.Not.Null, "ActiveCharacter has no unit");
            Assert.That(active.Unit.name, Is.EqualTo(FriendlyNames[0]), "The game starts controlling FriendlyUnit_1");
            Assert.That(active.HasUnit, Is.True);
            Assert.That(active.IsTakeoverOn, Is.False, "The game starts in free mode");
            var marker = Object.FindFirstObjectByType<ActiveCharacterMarker>();
            Assert.That(marker, Is.Not.Null, "ActiveMarker missing");
            Assert.That(marker.GetComponentsInChildren<Collider>(true), Is.Empty, "The marker must not block clicks");
            Assert.That(Object.FindFirstObjectByType<DirectControlInput>(), Is.Not.Null, "DirectControlInput missing");
            Assert.That(Object.FindFirstObjectByType<DirectControlInput>().IsFollowWired, Is.True, "DirectControlInput.toggleFollowAction is not wired in the scene");
            Assert.That(active.IsFollowOn, Is.True, "The game starts with follow on");
            Assert.That(Camera.main, Is.Not.Null);

            // Any error or exception logged during this second fails the test automatically.
            yield return new WaitForSeconds(1f);
            Assert.That(hostiles.All(h => h.State == EnemyState.Idle), Is.True, "Hostiles must stay idle while the squad is far away");
            var companions = FindSquad().Select(u => u.GetComponent<CompanionAI>()).ToArray();
            Assert.That(companions.All(c => !c.IsParked), Is.True, "Everyone starts attached");
            Assert.That(companions[0].State, Is.EqualTo(CompanionState.Controlled), "FriendlyUnit_1 is controlled");
            Assert.That(companions[1].State, Is.EqualTo(CompanionState.Idle), "FriendlyUnit_2 starts within follow distance");
            Assert.That(companions[2].State, Is.EqualTo(CompanionState.Idle), "FriendlyUnit_3 starts within follow distance");
        }

        [UnityTest]
        public IEnumerator ControlledCharacterMoves_CompanionsFollow()
        {
            yield return LoadScene();
            var squad = FindSquad();
            Assert.That(unit.Issue(new MoveCommand(new Vector3(-14f, 0f, 0f))), Is.True);

            // Waiting for the leader to arrive too: at load the companions already stand within 6 m of it.
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null && squad.Skip(1).All(u => u.CurrentCommand == null
                && TestWorld.HorizontalDistance(u.transform.position, unit.transform.position) < 6f), 25f);

            Assert.That(unit.CurrentCommand, Is.Null, "The controlled character did not arrive");
            foreach (var companion in squad.Skip(1))
                Assert.That(TestWorld.HorizontalDistance(companion.transform.position, unit.transform.position), Is.LessThan(6f),
                    $"{companion.name} did not follow the controlled character");
        }

        [UnityTest]
        public IEnumerator Tab_ThenMove_TheOldLeaderFollowsTheNewOne()
        {
            yield return LoadScene();
            var squad = FindSquad();
            var active = Object.FindFirstObjectByType<ActiveCharacter>();
            Assert.That(active.Cycle(1), Is.True);
            Assert.That(active.Unit, Is.SameAs(squad[1]));
            Assert.That(squad[1].Issue(new MoveCommand(new Vector3(-14f, 0f, 0f))), Is.True);

            yield return TestWorld.WaitUntil(() => squad[1].CurrentCommand == null && squad[0].CurrentCommand == null
                && TestWorld.HorizontalDistance(squad[0].transform.position, squad[1].transform.position) < 6f, 25f);

            Assert.That(squad[1].CurrentCommand, Is.Null, "FriendlyUnit_2 did not arrive");
            Assert.That(TestWorld.HorizontalDistance(squad[0].transform.position, squad[1].transform.position), Is.LessThan(6f),
                "FriendlyUnit_1 must follow once FriendlyUnit_2 is controlled");
            Assert.That(squad[0].GetComponent<CompanionAI>().State, Is.Not.EqualTo(CompanionState.Controlled));
        }

        [UnityTest]
        public IEnumerator Move_AroundTheCentralWall_Arrives()
        {
            yield return LoadScene();
            var destination = new Vector3(6f, 0f, 6f);
            Assert.That(unit.Issue(new MoveCommand(destination)), Is.True);

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 15f);

            Assert.That(unit.CurrentCommand, Is.Null, "Move did not finish in time");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, destination), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator Squad_AttacksEveryHostile_AndWins()
        {
            yield return LoadScene();
            var squad = FindSquad();
            var hostiles = HostileNames.Select(FindHostileHealth).ToArray();
            Assert.That(GroupOrders.Issue(squad, new AttackCommand(hostiles[0]), IssueMode.Replace), Is.EqualTo(3));
            Assert.That(GroupOrders.Issue(squad, new AttackCommand(hostiles[1]), IssueMode.Append), Is.EqualTo(3));
            Assert.That(GroupOrders.Issue(squad, new AttackCommand(hostiles[2]), IssueMode.Append), Is.EqualTo(3));

            yield return TestWorld.WaitUntil(() => encounter.Outcome != EncounterOutcome.Ongoing, 60f);

            Assert.That(encounter.Outcome, Is.EqualTo(EncounterOutcome.Victory), "The squad should beat three hostiles");
            Assert.That(hostiles.All(h => !h.IsAlive && !h.gameObject.activeSelf), Is.True);
            Assert.That(encounter.LivingFriendlies, Is.GreaterThan(0));
            foreach (var name in HostileNames)
                Assert.That(GameObject.Find($"{name} (dead)"), Is.Not.Null, $"{name} left no corpse marker");
            Assert.That(PrototypeHud.DescribeOutcome(encounter.Outcome), Does.StartWith("VICTORY"));
        }

        [UnityTest]
        public IEnumerator FriendlyWalksIntoView_HostilesEngageIt()
        {
            yield return LoadScene();
            var hostiles = FindHostiles();
            var health = unit.GetComponent<Health>();
            // The gap between Obstacle_E and Obstacle_F, in plain sight of HostileUnit_1.
            Assert.That(unit.Issue(new MoveCommand(new Vector3(10f, 0f, 6f))), Is.True);

            yield return TestWorld.WaitUntil(() => hostiles.Any(h => h.Target == health) || health.Current < health.Max, 25f);

            Assert.That(hostiles.Any(h => h.Target == health) || health.Current < health.Max, Is.True,
                "No hostile engaged the friendly that walked into view");
        }

        [UnityTest]
        public IEnumerator Pause_HoldsTheUnitUntilResume()
        {
            yield return LoadScene();
            var start = unit.transform.position;
            pause.Pause();
            unit.Issue(new MoveCommand(new Vector3(-10f, 0f, 0f)));

            yield return new WaitForSecondsRealtime(1f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.LessThan(0.01f));

            pause.Resume();
            yield return TestWorld.WaitUntil(() => TestWorld.HorizontalDistance(unit.transform.position, start) > 1f, 5f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.GreaterThan(1f));
        }

        [UnityTest]
        public IEnumerator RepeatedPauseDuringTheFight_InScene_LogsNoErrors()
        {
            yield return LoadScene();
            var squad = FindSquad();
            GroupOrders.Issue(squad, new AttackCommand(firstHostile), IssueMode.Replace);
            yield return TestWorld.WaitUntil(() => firstHostile.Current < firstHostile.Max, 20f);
            Assert.That(firstHostile.Current, Is.LessThan(firstHostile.Max), "Precondition: the fight started");

            for (var i = 0; i < 6; i++)
            {
                pause.Pause();
                yield return new WaitForSecondsRealtime(0.15f);
                pause.Resume();
                yield return new WaitForSeconds(0.2f);
            }

            Assert.That(pause.IsPaused, Is.False);
            Assert.That(encounter.Outcome, Is.Not.EqualTo(EncounterOutcome.Defeat));
        }

        [UnityTest]
        public IEnumerator GroupMove_TheSquadArrivesAtDistinctPoints()
        {
            yield return LoadScene();
            var squad = FindSquad();
            var destination = new Vector3(-6f, 0f, 0f);
            Assert.That(GroupOrders.Issue(squad, new MoveCommand(destination), IssueMode.Replace), Is.EqualTo(3));

            yield return TestWorld.WaitUntil(() => squad.All(u => u.CurrentCommand == null), 15f);

            Assert.That(squad.All(u => u.CurrentCommand == null), Is.True, "Group move did not finish in time");
            for (var i = 0; i < squad.Length; i++)
            for (var j = i + 1; j < squad.Length; j++)
                Assert.That(TestWorld.HorizontalDistance(squad[i].transform.position, squad[j].transform.position), Is.GreaterThan(1f));
        }
    }

    public class PrototypeSceneInputTests : InputTestFixture
    {
        public override void TearDown()
        {
            Time.timeScale = 1f;
            PrototypeSceneTests.DestroySceneObjects();
            base.TearDown();
        }

        static IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
        }

        static Health FirstHostile() => PrototypeSceneTests.FindHostileHealth("HostileUnit_1");

        IEnumerator LeftClickAt(Mouse mouse, Vector2 screenPoint)
        {
            Set(mouse.position, screenPoint);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
        }

        IEnumerator Tap(KeyControl key)
        {
            Press(key);
            yield return null;
            Release(key);
            yield return null;
        }

        [UnityTest]
        public IEnumerator PausedClickNearACoverMarker_InScene_OrdersCover_ThenTheUnitOccupiesItAfterResume()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var registry = Object.FindFirstObjectByType<CoverRegistry>();
            yield return TestWorld.WaitUntil(() => registry.Points.Count > 0, 2f);
            var point = registry.Points.Single(p => p.Name == "Cover_LowWall_L_S1");

            yield return Tap(keyboard.spaceKey);
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(squad[0].transform.position));
            // 0.25 m east of the point, on open ground south of LowWall_L (nothing between it and the camera). Within the
            // 0.5 m click radius of S1 only: S2 is 1 m east of S1, so a click 0.5 m east would be a tie between them.
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(point.Position + new Vector3(0.25f, 0f, 0f)));

            Assert.That(squad[0].CurrentCommand, Is.TypeOf<MoveToCoverCommand>(), "A paused click beside a marker orders cover");
            Assert.That(((MoveToCoverCommand)squad[0].CurrentCommand).Point, Is.SameAs(point));
            Assert.That(squad[0].Cover.Status, Is.EqualTo(CoverStatus.Reserved));
            Assert.That(squad[1].CurrentCommand, Is.Null, "Only the selected unit is ordered");

            yield return Tap(keyboard.spaceKey);
            yield return TestWorld.WaitUntil(() => squad[0].Cover.Status == CoverStatus.Occupied, 20f);

            Assert.That(squad[0].Cover.Status, Is.EqualTo(CoverStatus.Occupied), "After resume the unit walks there and takes it");
            Assert.That(squad[0].Cover.Point, Is.SameAs(point));
        }

        [UnityTest]
        public IEnumerator PausedClickCompanionThenHostile_InScene_OnlyThatUnitAttacks()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var hostile = FirstHostile();

            yield return Tap(keyboard.spaceKey);
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(squad[1].transform.position));
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(hostile.transform.position));

            Assert.That(squad[1].CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)squad[1].CurrentCommand).Target, Is.SameAs(hostile));
            Assert.That(squad[0].CurrentCommand, Is.Null);
            Assert.That(squad[2].CurrentCommand, Is.Null);
        }

        // Drags a selection box around every squad member.
        IEnumerator BoxSelect(Mouse mouse, CommandableUnit[] squad)
        {
            var screenPoints = squad.Select(u => (Vector2)Camera.main.WorldToScreenPoint(u.transform.position)).ToArray();
            var margin = new Vector2(25f, 25f);
            var from = screenPoints.Aggregate(Vector2.Min) - margin;
            var to = screenPoints.Aggregate(Vector2.Max) + margin;

            Set(mouse.position, from);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Set(mouse.position, to);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
        }

        [UnityTest]
        public IEnumerator PausedBoxSelectSquadThenClickGround_InScene_AllThreeGetMoves()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();

            yield return Tap(keyboard.spaceKey);
            yield return BoxSelect(mouse, squad);
            Assert.That(Object.FindFirstObjectByType<UnitSelection>().Selected, Has.Count.EqualTo(3));

            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, -1f)));   // plain ground: no cover point lies within the 0.5 m cover-click radius of this click

            foreach (var member in squad)
                Assert.That(member.CurrentCommand, Is.TypeOf<MoveCommand>(), member.name);
        }

        [UnityTest]
        public IEnumerator RealTimeBoxSelectSquadThenClickGround_InScene_AllThreeGetMoves()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();

            yield return BoxSelect(mouse, squad);
            Assert.That(Object.FindFirstObjectByType<UnitSelection>().Selected, Has.Count.EqualTo(3));

            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, -1f)));

            foreach (var member in squad)
                Assert.That(member.CurrentCommand, Is.TypeOf<MoveCommand>(), $"{member.name}: real-time group orders must reach every selected unit");
        }

        [UnityTest]
        public IEnumerator StopAndClearSelection_InScene_UseTheSceneBindings()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var selection = Object.FindFirstObjectByType<UnitSelection>();
            yield return Tap(keyboard.spaceKey);   // paused: clicks order the selection

            yield return BoxSelect(mouse, squad);
            Assert.That(selection.Selected, Has.Count.EqualTo(3));
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, -1f)));
            foreach (var member in squad)
                Assert.That(member.CurrentCommand, Is.TypeOf<MoveCommand>(), member.name);

            Press(keyboard.leftShiftKey);
            yield return null;
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, -4f)));   // (-6, 0, 4) is hidden behind Barrier_I
            Release(keyboard.leftShiftKey);
            yield return null;
            foreach (var member in squad)
                Assert.That(member.PendingCommands, Has.Count.EqualTo(1), $"{member.name}: Shift-click did not queue an order");

            Press(keyboard.xKey);
            yield return null;
            Release(keyboard.xKey);
            yield return null;
            foreach (var member in squad)
            {
                Assert.That(member.CurrentCommand, Is.Null, $"{member.name}: X did not stop the unit");
                Assert.That(member.PendingCommands, Is.Empty, member.name);
            }

            Press(keyboard.escapeKey);
            yield return null;
            Release(keyboard.escapeKey);
            yield return null;
            Assert.That(selection.Selected, Is.Empty, "Esc did not clear the selection");
        }

        [UnityTest]
        public IEnumerator HoldingW_InScene_PansTheCameraRig()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var rig = Object.FindFirstObjectByType<TacticalCameraController>();
            var start = rig.transform.position;

            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(rig.transform.position.z, Is.GreaterThan(start.z + 0.5f));
        }

        [UnityTest]
        public IEnumerator RealTimeClickOnHostile_InScene_TheControlledCharacterAttacks()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var hostile = FirstHostile();

            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(hostile.transform.position));

            Assert.That(squad[0].CurrentCommand, Is.TypeOf<AttackCommand>(), "The controlled character did not attack");
            Assert.That(((AttackCommand)squad[0].CurrentCommand).Target, Is.SameAs(hostile));
            Assert.That(squad[1].CurrentCommand, Is.Null);
            Assert.That(squad[2].CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator VThenW_InScene_DrivesThePrimary_AndTheCameraFollows()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var primary = PrototypeSceneTests.FindSquad()[0];
            var rig = Object.FindFirstObjectByType<TacticalCameraController>();
            var start = primary.transform.position;

            yield return Tap(keyboard.vKey);
            Press(keyboard.wKey);
            yield return TestWorld.WaitUntil(() => TestWorld.HorizontalDistance(primary.transform.position, start) > 1.5f, 3f);
            Release(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.6f);

            Assert.That(TestWorld.HorizontalDistance(primary.transform.position, start), Is.GreaterThan(1.5f),
                "V then W did not drive the primary character");
            Assert.That(TestWorld.HorizontalDistance(rig.transform.position, primary.transform.position), Is.LessThan(0.5f),
                "The camera did not follow the primary character");
        }

        [UnityTest]
        public IEnumerator PausedSquadOrder_ThenTakeover_OnlyThePrimaryDropsItsOrders()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();

            yield return Tap(keyboard.spaceKey);
            yield return BoxSelect(mouse, squad);
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, -1f)));
            foreach (var member in squad)
                Assert.That(member.CurrentCommand, Is.TypeOf<MoveCommand>(), $"{member.name}: precondition");
            yield return Tap(keyboard.vKey);
            yield return Tap(keyboard.spaceKey);   // resume: the plan starts running

            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.3f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(squad[0].CurrentCommand, Is.Null, "Manual input must take the primary character back");
            Assert.That(squad[1].CurrentCommand, Is.TypeOf<MoveCommand>(), "Takeover cancelled a companion's order");
            Assert.That(squad[2].CurrentCommand, Is.TypeOf<MoveCommand>(), "Takeover cancelled a companion's order");
        }

        [UnityTest]
        public IEnumerator Tab_InScene_SwitchesToTheNextFriendly_AndItsClicksAttack()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var hostile = FirstHostile();
            var active = Object.FindFirstObjectByType<ActiveCharacter>();
            var selection = Object.FindFirstObjectByType<UnitSelection>();
            var marker = Object.FindFirstObjectByType<ActiveCharacterMarker>();

            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.SameAs(squad[1]), "Tab did not switch to FriendlyUnit_2");
            Assert.That(selection.Selected, Is.EqualTo(new[] { squad[1].GetComponent<SelectableUnit>() }));
            yield return new WaitForSecondsRealtime(1f);   // let the camera finish focusing before aiming the click

            Assert.That(TestWorld.HorizontalDistance(marker.transform.position, squad[1].transform.position), Is.LessThan(0.01f),
                "The marker did not move to the new active character");
            var hostileOnScreen = Camera.main.WorldToScreenPoint(hostile.transform.position);
            Assert.That(new Rect(0f, 0f, Screen.width, Screen.height).Contains(hostileOnScreen), Is.True,
                "Precondition: HostileUnit_1 is on screen after the camera focused FriendlyUnit_2");
            yield return LeftClickAt(mouse, hostileOnScreen);

            Assert.That(squad[1].CurrentCommand, Is.TypeOf<AttackCommand>(), "The attack must come from FriendlyUnit_2");
            Assert.That(((AttackCommand)squad[1].CurrentCommand).Target, Is.SameAs(hostile));
            Assert.That(squad[0].CurrentCommand, Is.Null);
            Assert.That(squad[2].CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator F_InScene_TogglesFollow_AlsoWhilePaused()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var active = Object.FindFirstObjectByType<ActiveCharacter>();
            Assert.That(active.IsFollowOn, Is.True, "Precondition: follow starts on");

            yield return Tap(keyboard.fKey);
            Assert.That(active.IsFollowOn, Is.False, "F did not turn follow off");
            yield return Tap(keyboard.fKey);
            Assert.That(active.IsFollowOn, Is.True, "F did not turn follow back on");

            yield return Tap(keyboard.spaceKey);
            Assert.That(active.IsPaused, Is.True, "Precondition: paused");
            yield return Tap(keyboard.fKey);
            Assert.That(active.IsFollowOn, Is.False, "F must work while paused");
        }

        [UnityTest]
        public IEnumerator Tab_InScene_ParksTheCompanionsFarFromTheNewLeader()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var ais = squad.Select(u => u.GetComponent<CompanionAI>()).ToArray();
            // FriendlyUnit_2 walks 12 m from the others (its explicit order parks it, which is fine), then becomes the leader.
            Assert.That(squad[1].Issue(new MoveCommand(new Vector3(-14f, 0f, 0f))), Is.True);
            yield return TestWorld.WaitUntil(() => squad[1].CurrentCommand == null, 10f);
            Assert.That(TestWorld.HorizontalDistance(squad[0].transform.position, squad[1].transform.position), Is.GreaterThan(6f), "Precondition");

            yield return Tap(keyboard.tabKey);
            yield return null;

            var active = Object.FindFirstObjectByType<ActiveCharacter>();
            Assert.That(active.Unit, Is.SameAs(squad[1]));
            Assert.That(ais[0].IsParked, Is.True, "FriendlyUnit_1 is beyond 6 m of the new leader");
            Assert.That(squad[0].CurrentCommand, Is.Null, "Tab alone never sends anyone anywhere");
        }

        [UnityTest]
        public IEnumerator ShiftTab_InScene_SwitchesBackToTheLastFriendly()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var active = Object.FindFirstObjectByType<ActiveCharacter>();

            Press(keyboard.leftShiftKey);
            yield return null;
            yield return Tap(keyboard.tabKey);
            Release(keyboard.leftShiftKey);
            yield return null;

            Assert.That(active.Unit, Is.SameAs(squad[2]), "Shift+Tab did not switch to FriendlyUnit_3");
        }
    }
}
#endif
