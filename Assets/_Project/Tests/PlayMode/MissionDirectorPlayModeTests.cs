#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class MissionDirectorPlayModeTests
    {
        MissionRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        static string[] RootNames() => SceneManager.GetActiveScene().GetRootGameObjects().Select(g => g.name).OrderBy(n => n).ToArray();

        [UnityTest]
        public IEnumerator Generate_ReachesReady_WithTheSquadAndHostilesOnTheNavMesh_AndWiredToTheSystems()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);
            var d = rig.Director;

            Assert.That(d.State, Is.EqualTo(MissionState.Ready), string.Join("\n", d.Report.Failures));
            Assert.That(d.Friendlies, Has.Count.EqualTo(3));
            Assert.That(d.Hostiles, Has.Count.EqualTo(3));
            Assert.That(rig.Encounter.Friendlies, Has.Count.EqualTo(3));
            Assert.That(rig.Encounter.Hostiles, Has.Count.EqualTo(3));
            Assert.That(rig.Encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));
            Assert.That(rig.Selection.Roster, Has.Count.EqualTo(3));
            Assert.That(rig.Active.Unit, Is.EqualTo(d.Friendlies[0]));
            Assert.That(rig.Active.IsTakeoverOn, Is.False);
            Assert.That(rig.Registry.Points, Is.Not.Empty);
            Assert.That(d.Report.CoverCorner, Is.GreaterThan(0));
            foreach (var unit in d.Friendlies.Concat(d.Hostiles))
            {
                Assert.That(unit.GetComponent<NavMeshAgent>().isOnNavMesh, Is.True, unit.name);
                Assert.That(unit.transform.IsChildOf(d.Current.Actors), Is.True, unit.name);
                Assert.That(unit.Cover.IsWired, Is.True, unit.name);
            }
            foreach (var friendly in d.Friendlies)
            {
                Assert.That(friendly.GetComponent<CompanionAI>().IsWired, Is.True);
                Assert.That(friendly.GetComponent<UnitAbilities>().Count, Is.EqualTo(3));
            }
            foreach (var hostile in d.Hostiles)
            {
                Assert.That(hostile.GetComponent<EnemyAI>().IsCoverWired, Is.True);
                Assert.That(hostile.TryGetComponent<UnitAbilities>(out _), Is.False, "hostiles have no abilities");
            }
            Assert.That(d.Friendlies[1].GetComponent<UnitAttacker>().Archetype.DisplayName, Is.EqualTo("Marksman"));
        }

        [UnityTest]
        public IEnumerator Spawns_AreOutsideGeometry_Apart_AndTheTeamsStartSeparated()
        {
            rig = new MissionRig();
            foreach (var seed in new[] { 12345, 1, 2, 3 })
            {
                yield return rig.Generate(seed);
                Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), $"seed {seed}");
                var all = rig.Director.Friendlies.Concat(rig.Director.Hostiles).ToList();
                foreach (var unit in all)
                {
                    var ground = unit.transform.position - Vector3.up;
                    // Only the mission geometry counts: the capsule would otherwise overlap the units' own colliders.
                    var overlaps = Physics.OverlapCapsule(ground + Vector3.up * 0.55f, ground + Vector3.up * 1.45f, 0.45f, ~0, QueryTriggerInteraction.Ignore);
                    Assert.That(overlaps.Where(c => c.transform.IsChildOf(rig.Director.Current.Geometry)), Is.Empty,
                        $"seed {seed}: {unit.name} stands inside geometry");
                }
                for (var i = 0; i < all.Count; i++)
                    for (var j = i + 1; j < all.Count; j++)
                        Assert.That(TestWorld.HorizontalDistance(all[i].transform.position, all[j].transform.position),
                            Is.GreaterThanOrEqualTo(MissionConstants.SpawnSpacing - 0.2f), $"seed {seed}");
                Assert.That(rig.Director.Report.TeamSeparation, Is.GreaterThanOrEqualTo(15.8f), $"seed {seed}");
            }
        }

        // A walkable point as far from the unit as possible, at most 9 m and never under 7 m (beyond the 6 m follow start
        // distance), with a clear straight NavMesh line to it. `direction` points from the unit to the spot.
        static bool TryFindClearSpot(CommandableUnit from, out Vector3 spot, out Vector3 direction)
        {
            var origin = from.transform.position - Vector3.up;
            foreach (var distance in new[] { 9f, 8f, 7.5f, 7f })
                for (var i = 0; i < 32; i++)
                {
                    direction = Quaternion.Euler(0f, i * 11.25f, 0f) * Vector3.forward;
                    if (NavMesh.SamplePosition(origin + direction * distance, out var hit, 0.4f, NavMesh.AllAreas)
                        && TestWorld.HorizontalDistance(hit.position, origin) >= 7f
                        && !NavMesh.Raycast(origin, hit.position, out _, NavMesh.AllAreas))
                    {
                        spot = hit.position;
                        return true;
                    }
                }
            spot = default;
            direction = default;
            return false;
        }

        [UnityTest]
        public IEnumerator Spawn_HoldsEveryCompanion_UntilTheControlledCharacterMoves()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
            var leader = rig.Director.Friendlies[0];
            Assert.That(rig.Director.Friendlies.Select(f => f.GetComponent<CompanionAI>().IsHeld), Is.All.True, "every friendly is held at spawn");

            var companion = rig.Director.Friendlies[1];
            var ai = companion.GetComponent<CompanionAI>();
            Assert.That(TryFindClearSpot(leader, out var spot, out var toward), Is.True, "an open spot 7 to 9 m from the leader");
            companion.GetComponent<NavMeshAgent>().Warp(spot);
            var warped = companion.transform.position;
            var everOrdered = false;
            var deadline = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < deadline)
            {
                everOrdered |= companion.CurrentCommand != null;
                yield return null;
            }
            Assert.That(everOrdered, Is.False, "A companion spawned apart does not squad up by itself");
            Assert.That(TestWorld.HorizontalDistance(companion.transform.position, warped), Is.LessThan(0.1f));
            Assert.That(ai.IsHeld, Is.True);

            // The leader walks toward it along the clear line: the hold ends and the companion (still beyond 6 m) follows.
            leader.SetMoveIntent(toward);
            yield return TestWorld.WaitUntil(() => !ai.IsHeld, 2f);
            leader.SetMoveIntent(Vector3.zero);
            Assert.That(ai.IsHeld, Is.False, "the controlled character moved");
            yield return TestWorld.WaitUntil(() => ai.IsFollowing, 2f);
            Assert.That(ai.IsFollowing, Is.True, "released: the normal follow rules apply");
        }

        [UnityTest]
        public IEnumerator SameSeedTwice_GivesTheSameLayoutAndTheSameCover()
        {
            rig = new MissionRig();
            yield return rig.Generate(4242);
            var first = rig.Director.Report;
            var firstPositions = rig.Director.Friendlies.Concat(rig.Director.Hostiles).Select(u => u.transform.position).ToList();
            yield return rig.Generate(4242);
            var second = rig.Director.Report;

            Assert.That(second.LayoutHash, Is.EqualTo(first.LayoutHash));
            Assert.That(second.CoverTotal, Is.EqualTo(first.CoverTotal));
            Assert.That(second.CoverCorner, Is.EqualTo(first.CoverCorner));
            var secondPositions = rig.Director.Friendlies.Concat(rig.Director.Hostiles).Select(u => u.transform.position).ToList();
            for (var i = 0; i < firstPositions.Count; i++)
                Assert.That(Vector3.Distance(firstPositions[i], secondPositions[i]), Is.LessThan(0.05f));
        }

        [UnityTest]
        public IEnumerator Regenerate_LeavesNoStaleState()
        {
            rig = new MissionRig();
            var persistentRoots = RootNames();
            yield return rig.Generate(11);
            var d = rig.Director;
            var oldRoot = d.Current.Root;
            var oldLocations = rig.Registry.Points.ToList();
            var oldFriendly = d.Friendlies[0];
            var oldHostile = d.Hostiles[0];

            // Dirty every kind of state: a selection, a reserved cover point, queued orders, a dead hostile (death marker).
            rig.Selection.Select(oldFriendly.GetComponent<SelectableUnit>());
            var reserved = oldLocations.First(p => p.Height == CoverHeight.Low);
            Assert.That(oldFriendly.Issue(new MoveToCoverCommand(reserved)), Is.True);
            oldFriendly.Issue(new MoveCommand(oldFriendly.transform.position + Vector3.right), IssueMode.Append);
            oldHostile.GetComponent<Health>().TakeDamage(1000);
            yield return null;
            Assert.That(oldHostile.GetComponent<DeathMarker>().LastMarker, Is.Not.Null);
            Assert.That(reserved.IsClaimed, Is.True);

            yield return rig.Generate(12);
            yield return null;

            Assert.That(d.State, Is.EqualTo(MissionState.Ready));
            Assert.That(oldRoot == null, Is.True, "the old mission root is gone");
            Assert.That(oldFriendly == null && oldHostile == null, Is.True, "old units are gone");
            Assert.That(oldLocations.All(l => !l.IsValid), Is.True, "old cover locations are retired");
            Assert.That(oldLocations.All(l => !l.IsClaimed), Is.True, "no old claim survives");
            Assert.That(rig.Registry.Points.All(p => !oldLocations.Contains(p)), Is.True);
            Assert.That(rig.Selection.Selected, Is.Empty);
            Assert.That(rig.Selection.Roster, Has.Count.EqualTo(3));
            Assert.That(rig.Encounter.Friendlies.All(h => h != null && h.IsAlive), Is.True);
            Assert.That(rig.Encounter.Hostiles, Has.Count.EqualTo(3));
            Assert.That(rig.Encounter.LivingHostiles, Is.EqualTo(3));
            Assert.That(rig.Encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));
            Assert.That(rig.Registry.Points.All(p => !p.IsClaimed), Is.True, "no reservation on the new cover");
            var expectedRoots = persistentRoots.Concat(new[] { GeneratedMission.RootName }).OrderBy(n => n).ToArray();
            Assert.That(RootNames(), Is.EqualTo(expectedRoots), "no marker or other object survived at the scene root");
        }

        [UnityTest]
        public IEnumerator Regenerate_InTheFrameAUnitDies_LeavesNoStaleDeathMarker()
        {
            // F6 is processed before any Update: the regeneration tears the old mission down at once, but its root is only
            // destroyed at the end of the frame, so a killing blow landing later in that frame must not leave a marker.
            rig = new MissionRig();
            var persistentRoots = RootNames();
            yield return rig.Generate(11);
            yield return null;
            var d = rig.Director;
            var friendly = d.Friendlies[0].GetComponent<Health>();
            var hostile = d.Hostiles[0].GetComponent<Health>();

            Assert.That(d.Generate(12), Is.True);
            friendly.TakeDamage(1000);
            hostile.TakeDamage(1000);
            yield return TestWorld.WaitUntil(() => d.State == MissionState.Ready || d.State == MissionState.Failed, 20f);
            yield return null;

            Assert.That(d.State, Is.EqualTo(MissionState.Ready));
            var expectedRoots = persistentRoots.Concat(new[] { GeneratedMission.RootName }).OrderBy(n => n).ToArray();
            Assert.That(RootNames(), Is.EqualTo(expectedRoots), "no death marker survived at the scene root");
        }

        [UnityTest]
        public IEnumerator Generate_WhenSpawningThrows_EndsFailedNotStuck_AndALaterGenerationWorks()
        {
            // A hostile prefab without EnemyAI makes the spawner throw a NullReferenceException mid-attempt.
            rig = new MissionRig();
            var broken = rig.World.Track(new GameObject("BrokenHostile"));
            broken.SetActive(false);
            broken.AddComponent<NavMeshAgent>();
            broken.AddComponent<CommandableUnit>();
            rig.SetHostileSlots(new[] { new HostileSlot { prefab = broken } });
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"Mission generation failed.*seed=5.*attempts=20",
                System.Text.RegularExpressions.RegexOptions.Singleline));

            yield return rig.Generate(5);
            yield return null;

            var d = rig.Director;
            Assert.That(d.State, Is.EqualTo(MissionState.Failed));
            Assert.That(d.Report.Failures, Is.Not.Empty);
            Assert.That(d.Report.Failures.Any(f => f.Contains("exception")), Is.True, string.Join("\n", d.Report.Failures));
            Assert.That(GameObject.Find(GeneratedMission.RootName), Is.Null, "the half-built mission is gone");
            Assert.That(d.Friendlies, Is.Empty);
            Assert.That(d.Hostiles, Is.Empty);
            Assert.That(rig.Encounter.Friendlies, Is.Empty);
            Assert.That(rig.Encounter.Hostiles, Is.Empty);
            Assert.That(rig.Registry.Points, Is.Empty);

            rig.SetHostileSlots(MissionRig.HostileSlots());
            yield return rig.Generate(6);
            yield return null;

            Assert.That(d.State, Is.EqualTo(MissionState.Ready), "the director is not stuck in Generating");
            Assert.That(d.Hostiles, Has.Count.EqualTo(3));
        }

        [UnityTest]
        public IEnumerator Regenerate_WhilePaused_ResumesAndWorks()
        {
            rig = new MissionRig();
            yield return rig.Generate(5);
            rig.Pause.Pause();
            Assert.That(Time.timeScale, Is.EqualTo(0f));

            yield return rig.Generate(6);

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready));
            Assert.That(rig.Pause.IsPaused, Is.False);
            Assert.That(Time.timeScale, Is.EqualTo(1f));
        }

        [UnityTest]
        public IEnumerator Generate_WhileGenerating_IsIgnored()
        {
            rig = new MissionRig();
            Assert.That(rig.Director.Generate(21), Is.True);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Generating));
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("already being generated"));
            Assert.That(rig.Director.Generate(22), Is.False, "a second request while generating is refused");
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);

            Assert.That(rig.Director.Report.Seed, Is.EqualTo(21));
            Assert.That(SceneManager.GetActiveScene().GetRootGameObjects().Count(g => g.name == GeneratedMission.RootName), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ImpossibleSettings_FailCleanly_NoUnitsNoEncounter_AndTheErrorNamesTheSeed()
        {
            rig = new MissionRig(new MissionSettings { gridColumns = 2, gridRows = 2, cellSize = 12, roomCount = 4, minTeamSeparation = 60f, maxAttempts = 3 });
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"Mission generation failed.*seed=77.*attempts=3", System.Text.RegularExpressions.RegexOptions.Singleline));
            yield return rig.Generate(77);

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Failed));
            Assert.That(rig.Director.Report.Succeeded, Is.False);
            Assert.That(rig.Director.Report.Failures, Has.Count.EqualTo(3));
            Assert.That(rig.Director.Friendlies, Is.Empty);
            Assert.That(rig.Encounter.Friendlies, Is.Empty);
            Assert.That(rig.Encounter.Outcome, Is.EqualTo(EncounterOutcome.Ongoing));
            Assert.That(rig.Registry.Points, Is.Empty);
            Assert.That(GameObject.Find(GeneratedMission.RootName), Is.Null);
        }

        [UnityTest]
        public IEnumerator Generate_RebindsAndFocusesTheCamera()
        {
            rig = new MissionRig(withCamera: true);
            yield return rig.Generate(12345);
            var bounds = rig.Director.Report.Bounds;
            var squadCentre = rig.Director.Friendlies.Aggregate(Vector3.zero, (sum, u) => sum + u.transform.position) / 3f;
            yield return null;

            var position = rig.Camera.transform.position;
            Assert.That(position.x, Is.InRange(bounds.min.x - 2.1f, bounds.max.x + 2.1f));
            Assert.That(position.z, Is.InRange(bounds.min.z - 2.1f, bounds.max.z + 2.1f));
            Assert.That(TestWorld.HorizontalDistance(position, squadCentre), Is.LessThan(8f), "the squad is where the camera looks");
        }

        [UnityTest]
        public IEnumerator Clear_RightAfterGenerate_CancelsTheRun()
        {
            rig = new MissionRig();
            rig.Director.Generate(1);
            rig.Director.Clear();
            yield return null;
            yield return null;

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Idle));
            Assert.That(GameObject.Find(GeneratedMission.RootName), Is.Null);
            Assert.That(rig.Encounter.Friendlies, Is.Empty);
            Assert.That(rig.Encounter.Hostiles, Is.Empty);
            Assert.That(rig.Director.Friendlies, Is.Empty);
        }

        [UnityTest]
        public IEnumerator GenerateClearGenerate_InOneFrame_RunsOnlyTheLastRequest_AndLeaksNothing()
        {
            rig = new MissionRig();
            yield return rig.Generate(31);
            yield return null;
            var baseline = Resources.FindObjectsOfTypeAll<NavMeshData>().Length;

            Assert.That(rig.Director.Generate(1), Is.True);
            rig.Director.Clear();
            Assert.That(rig.Director.Generate(2), Is.True);
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready || rig.Director.State == MissionState.Failed, 20f);
            yield return null;

            var d = rig.Director;
            Assert.That(d.State, Is.EqualTo(MissionState.Ready));
            Assert.That(d.Report.Seed, Is.EqualTo(2));
            Assert.That(SceneManager.GetActiveScene().GetRootGameObjects().Count(g => g.name == GeneratedMission.RootName), Is.EqualTo(1));
            Assert.That(d.Friendlies, Has.Count.EqualTo(3));
            Assert.That(d.Hostiles, Has.Count.EqualTo(3));
            Assert.That(rig.Encounter.Friendlies, Has.Count.EqualTo(3));
            Assert.That(rig.Encounter.Hostiles, Has.Count.EqualTo(3));
            Assert.That(Resources.FindObjectsOfTypeAll<NavMeshData>().Length, Is.EqualTo(baseline), "NavMeshData objects");
        }

        [UnityTest]
        public IEnumerator Regenerate_DoesNotLeakNavMeshData()
        {
            // Removing a NavMeshSurface's data only removes the NavMesh instance; the NavMeshData object a bake creates
            // lives on unless the director destroys it too.
            rig = new MissionRig();
            yield return rig.Generate(31);
            yield return null;
            var baseline = Resources.FindObjectsOfTypeAll<NavMeshData>().Length;

            foreach (var seed in new[] { 31, 32, 33 })
            {
                yield return rig.Generate(seed);
                yield return null;
                Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), $"seed {seed}");
                Assert.That(Resources.FindObjectsOfTypeAll<NavMeshData>().Length, Is.EqualTo(baseline), $"NavMeshData objects after seed {seed}");
            }
        }

        // ---- A different level on every Play: the seed at start ----

        [UnityTest]
        public IEnumerator Start_WithNewSeedAtStart_DrawsTheSeedSource_AndTheSettingsShowIt()
        {
            rig = new MissionRig(new MissionSettings { seed = 4321 });
            rig.GenerateAtStart(randomSeed: true, () => 777);

            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready || rig.Director.State == MissionState.Failed, 20f);

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
            Assert.That(rig.Director.Report.Seed, Is.EqualTo(777), "the first generation used the drawn seed, not the Inspector's");
            Assert.That(rig.Director.Settings.seed, Is.EqualTo(777), "the HUD and the Inspector show the seed in use");
        }

        [UnityTest]
        public IEnumerator Start_WithoutNewSeedAtStart_UsesTheInspectorSeed_AndNeverDrawsOne()
        {
            rig = new MissionRig(new MissionSettings { seed = 4321 });
            rig.GenerateAtStart(randomSeed: false, () => throw new System.InvalidOperationException("the seed source must not be used"));

            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready || rig.Director.State == MissionState.Failed, 20f);

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
            Assert.That(rig.Director.Report.Seed, Is.EqualTo(4321));
        }

        [UnityTest]
        public IEnumerator TwoPlays_WithDifferentDrawnSeeds_GiveDifferentLevels()
        {
            rig = new MissionRig();
            rig.GenerateAtStart(randomSeed: true, () => 101);
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready));
            var first = rig.Director.Report.LayoutHash;
            rig.Dispose();

            rig = new MissionRig();
            rig.GenerateAtStart(randomSeed: true, () => 202);
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready));

            Assert.That(rig.Director.Report.LayoutHash, Is.Not.EqualTo(first));
        }

        [UnityTest]
        public IEnumerator F6AfterARandomStart_RepeatsTheDrawnSeed_AndF7DrawsAnother()
        {
            rig = new MissionRig();
            var draws = new System.Collections.Generic.Queue<int>(new[] { 303, 404 });
            rig.GenerateAtStart(randomSeed: true, () => draws.Dequeue());
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);
            var first = rig.Director.Report.LayoutHash;

            Assert.That(rig.Director.RegenerateSame(), Is.True);
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);
            Assert.That(rig.Director.Report.Seed, Is.EqualTo(303));
            Assert.That(rig.Director.Report.LayoutHash, Is.EqualTo(first), "F6 repeats the displayed seed");

            Assert.That(rig.Director.GenerateNew(), Is.True);
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);
            Assert.That(rig.Director.Report.Seed, Is.EqualTo(404), "F7 draws from the seed source");
            Assert.That(rig.Director.Settings.seed, Is.EqualTo(404));
        }

        [Test]
        public void TheDefaultSeedSource_IsNonNegative_AndDoesNotTouchUnityRandom()
        {
            rig = new MissionRig();
            Random.InitState(5);
            var expected = new[] { Random.value, Random.value };
            Random.InitState(5);

            var seed = rig.Director.seedSource();
            var actual = new[] { Random.value, Random.value };

            Assert.That(seed, Is.GreaterThanOrEqualTo(0));
            Assert.That(actual, Is.EqualTo(expected), "drawing a mission seed must not disturb combat randomness");
        }
    }
}
#endif
