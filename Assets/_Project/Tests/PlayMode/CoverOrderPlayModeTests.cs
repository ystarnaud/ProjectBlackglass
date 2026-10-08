using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverOrderPlayModeTests
    {
        TestWorld world;
        GameObject environment;
        CoverLocation point;
        CoverRegistry registry;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            // A 0.9 m wall from z -0.25 to 0.25 (x -2..2) with one point 0.75 m south of it.
            environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry = world.CreateRegistry(point);
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        static UnitCover CoverOf(Component unit) => unit.GetComponent<UnitCover>();

        CommandableUnit Fighter(Vector3 at, CombatRole role = CombatRole.Melee, float range = 2f) =>
            world.CreateFighter(at, role: role, range: range, registry: registry);

        IEnumerator WaitOccupied(CommandableUnit unit, float timeout = 10f) =>
            TestWorld.WaitUntil(() => CoverOf(unit).Status == CoverStatus.Occupied, timeout);

        [UnityTest]
        public IEnumerator MoveToCover_ReservesOnIssue_ThenOccupiesOnArrival()
        {
            var unit = Fighter(new Vector3(0f, 0f, -8f));
            var order = new MoveToCoverCommand(point);

            Assert.That(unit.Issue(order), Is.True);
            Assert.That(unit.CurrentCommand, Is.SameAs(order));
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Reserved), "Reserved the moment the order starts");
            Assert.That(point.Claimant, Is.SameAs(CoverOf(unit)));
            Assert.That(point.IsOccupied, Is.False);

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);

            Assert.That(unit.CurrentCommand, Is.Null, "The order ends on arrival");
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied));
            Assert.That(CoverOf(unit).OccupiedByOrder, Is.True);
            Assert.That(point.IsOccupied, Is.True);
            Assert.That(CoverRules.FlatDistance(unit.transform.position, point.Position), Is.LessThan(0.6f));
        }

        [UnityTest]
        public IEnumerator SecondUnit_OrderedToAClaimedPoint_IsRefused_AndKeepsItsOrders()
        {
            var first = Fighter(new Vector3(0f, 0f, -8f));
            var second = Fighter(new Vector3(4f, 0f, -8f));
            var move = new MoveCommand(new Vector3(4f, 0f, -4f));
            Assert.That(second.Issue(move), Is.True);
            Assert.That(first.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return null;

            Assert.That(second.Issue(new MoveToCoverCommand(point)), Is.False);
            Assert.That(second.CurrentCommand, Is.SameAs(move), "A refused order leaves the unit's orders alone");
            Assert.That(CoverOf(second).Status, Is.EqualTo(CoverStatus.None));
            Assert.That(point.Claimant, Is.SameAs(CoverOf(first)));
        }

        [UnityTest]
        public IEnumerator ThreeIdleUnits_GivenTheSameCoverOrder_OnlyOneTakesIt()
        {
            var units = new[] { Fighter(new Vector3(-3f, 0f, -8f)), Fighter(new Vector3(0f, 0f, -8f)), Fighter(new Vector3(3f, 0f, -8f)) };
            yield return null;

            Assert.That(GroupOrders.Issue(units, new MoveToCoverCommand(point), IssueMode.Replace), Is.EqualTo(1));
            Assert.That(units.Count(u => u.CurrentCommand is MoveToCoverCommand), Is.EqualTo(1));
            Assert.That(units.Count(u => u.CurrentCommand == null), Is.EqualTo(2), "The others keep their (empty) orders");
            Assert.That(units.Count(u => CoverOf(u).Status == CoverStatus.Reserved), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator TwoBusyUnits_QueueTheSamePoint_TheSecondIsSkipped()
        {
            // The near unit's move is 2 m, the far unit's 6 m, so the near one reaches its cover order first.
            var near = Fighter(new Vector3(-2f, 0f, -6f));
            var far = Fighter(new Vector3(2f, 0f, -16f));
            Assert.That(near.Issue(new MoveCommand(new Vector3(-2f, 0f, -4f))), Is.True);
            Assert.That(far.Issue(new MoveCommand(new Vector3(2f, 0f, -10f))), Is.True);
            var order = new MoveToCoverCommand(point);
            Assert.That(near.Issue(order, IssueMode.Append), Is.True);
            Assert.That(far.Issue(order, IssueMode.Append), Is.True);
            Assert.That(point.IsClaimed, Is.False, "A queued cover order reserves nothing until it starts");

            yield return TestWorld.WaitUntil(() => CoverOf(near).Status == CoverStatus.Occupied && far.CurrentCommand == null, 15f);

            Assert.That(CoverOf(near).Status, Is.EqualTo(CoverStatus.Occupied));
            Assert.That(far.CurrentCommand, Is.Null, "The second cover order was skipped when its turn came");
            Assert.That(CoverOf(far).Status, Is.EqualTo(CoverStatus.None));
            Assert.That(point.Claimant, Is.SameAs(CoverOf(near)));
        }

        [UnityTest]
        public IEnumerator ReplacingACoverOrderWithAMove_ReleasesTheReservation()
        {
            var unit = Fighter(new Vector3(0f, 0f, -8f));
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return null;

            Assert.That(unit.Issue(new MoveCommand(new Vector3(4f, 0f, -8f))), Is.True);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None));
            Assert.That(point.IsClaimed, Is.False);
        }

        [UnityTest]
        public IEnumerator ReplacingACoverOrderWithARefusedMove_KeepsTheReservation()
        {
            var unit = Fighter(new Vector3(0f, 0f, -8f));
            var order = new MoveToCoverCommand(point);
            Assert.That(unit.Issue(order), Is.True);
            yield return null;

            LogAssert.Expect(LogType.Warning, new Regex("no walkable NavMesh point"));
            Assert.That(unit.Issue(new MoveCommand(new Vector3(100f, 0f, 100f))), Is.False);
            Assert.That(unit.CurrentCommand, Is.SameAs(order));
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Reserved), "A refused replacement changes nothing");
            Assert.That(point.Claimant, Is.SameAs(CoverOf(unit)));
        }

        [UnityTest]
        public IEnumerator ACoverOrderToASecondPoint_ReleasesTheFirst()
        {
            var second = world.CreateCoverPoint(new Vector3(1.5f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry.Initialize(point, second);
            var unit = Fighter(new Vector3(0f, 0f, -8f));
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return null;

            Assert.That(unit.Issue(new MoveToCoverCommand(second)), Is.True);
            Assert.That(CoverOf(unit).Point, Is.SameAs(second));
            Assert.That(point.IsClaimed, Is.False, "A unit never holds two points");
            Assert.That(second.Claimant, Is.SameAs(CoverOf(unit)));
        }

        [UnityTest]
        public IEnumerator Stop_ReleasesAReservation_ButNotAnOccupancy()
        {
            var unit = Fighter(new Vector3(0f, 0f, -8f));
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return null;
            Assert.That(unit.Issue(new StopCommand()), Is.True);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None), "Stop cancels the walk, so the reservation goes");
            Assert.That(point.IsClaimed, Is.False);

            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return WaitOccupied(unit);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied), "Precondition");

            Assert.That(unit.Issue(new StopCommand()), Is.True);
            yield return null;
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied), "A unit standing in cover stays in cover when stopped");
            Assert.That(point.Claimant, Is.SameAs(CoverOf(unit)));
        }

        [UnityTest]
        public IEnumerator WalkingAwayFromOrderedCover_ReleasesIt()
        {
            var unit = Fighter(new Vector3(0f, 0f, -8f));
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return WaitOccupied(unit);
            Assert.That(CoverOf(unit).OccupiedByOrder, Is.True, "Precondition");

            Assert.That(unit.Issue(new MoveCommand(new Vector3(0f, 0f, -8f))), Is.True);
            yield return TestWorld.WaitUntil(() => CoverOf(unit).Status == CoverStatus.None, 3f);

            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None));
            Assert.That(CoverOf(unit).OccupiedByOrder, Is.False);
            Assert.That(point.IsClaimed, Is.False);
        }

        [UnityTest]
        public IEnumerator DirectControl_DropsTheCoverOrder_AndSteeringAwayLater_ReleasesTheOccupancy()
        {
            var unit = Fighter(new Vector3(0f, 0f, -8f));
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            unit.SetMoveIntent(Vector3.back);
            yield return null;
            yield return null;
            Assert.That(unit.CurrentCommand, Is.Null, "Held keys drop every order");
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None), "...and the reservation with it");
            Assert.That(point.IsClaimed, Is.False);

            unit.SetMoveIntent(Vector3.zero);
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            yield return WaitOccupied(unit);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied), "Precondition");

            unit.SetMoveIntent(Vector3.back);
            yield return TestWorld.WaitUntil(() => CoverOf(unit).Status == CoverStatus.None, 3f);
            unit.SetMoveIntent(Vector3.zero);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None), "Driven more than 1 m away, the occupancy ends");
        }

        [UnityTest]
        public IEnumerator AppendedCover_BehindAMove_RunsAfterIt()
        {
            var unit = Fighter(new Vector3(0f, 0f, -8f));
            Assert.That(unit.Issue(new MoveCommand(new Vector3(3f, 0f, -6f))), Is.True);
            var order = new MoveToCoverCommand(point);
            Assert.That(unit.Issue(order, IssueMode.Append), Is.True);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None), "Not reserved while pending");

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == order, 10f);
            Assert.That(unit.CurrentCommand, Is.SameAs(order), "The cover order started after the move");
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Reserved));
            yield return WaitOccupied(unit);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied));
        }

        [UnityTest]
        public IEnumerator CoverOrderWhilePaused_ReservesAtOnce_ThenRunsAfterResume_AndTheAppendedAttackFiresFromCover()
        {
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var unit = Fighter(new Vector3(0f, 0f, -8f), CombatRole.Ranged, 8f);
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 5f));   // 6 m north of the point, over the wall
            yield return new WaitForFixedUpdate();

            pause.Pause();
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            Assert.That(unit.Issue(new AttackCommand(dummy), IssueMode.Append), Is.True);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Reserved), "Planning while paused reserves the point");
            var start = unit.transform.position;
            yield return new WaitForSecondsRealtime(1f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.LessThan(0.01f), "Nothing moves while paused");
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Reserved));

            pause.Resume();
            yield return WaitOccupied(unit);
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied));
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 4f);

            Assert.That(dummy.Current, Is.LessThan(dummy.Max), "The appended attack ran");
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.Occupied), "A ranged unit fires from cover without leaving it");
            Assert.That(unit.AttackPhase, Is.EqualTo(AttackPhase.Attack));
        }

        [UnityTest]
        public IEnumerator DestroyingThePointMidWalk_EndsTheOrderSilently_AndTheNextOrderRuns()
        {
            var unit = Fighter(new Vector3(0f, 0f, -10f));
            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            var next = new MoveCommand(new Vector3(4f, 0f, -8f));
            Assert.That(unit.Issue(next, IssueMode.Append), Is.True);
            yield return null;

            point.Retire();
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == next, 2f);

            Assert.That(unit.CurrentCommand, Is.SameAs(next), "The vanished point ends the cover order and the queue moves on");
            Assert.That(CoverOf(unit).Status, Is.EqualTo(CoverStatus.None));
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator BlockedPoint_OrderGivesUpAfterThreeSeconds_AndTheNextOrderRuns()
        {
            // The blocker stands on the point itself. It would auto-occupy it (within occupyRadius) once it has stood
            // still for a frame, but the walker's order reserves the point synchronously in Issue, before either
            // unit's first Update. Agent avoidance keeps the walker about 1 m (two agent radii) from the blocker,
            // so HasArrived (0.2 m from the point) never turns true and the walk stalls. Once the walker gives up,
            // the blocker takes the point by standing there, which is why the final claim check is "not the walker".
            var walker = Fighter(new Vector3(0f, 0f, -8f));
            var blocker = Fighter(new Vector3(0f, 0f, -1f));
            // Highest avoidance priority: the blocker ignores the walker, so avoidance never nudges it off the spot.
            blocker.GetComponent<UnityEngine.AI.NavMeshAgent>().avoidancePriority = 0;
            Assert.That(walker.Issue(new MoveToCoverCommand(point)), Is.True);
            var next = new MoveCommand(new Vector3(4f, 0f, -8f));
            Assert.That(walker.Issue(next, IssueMode.Append), Is.True);
            Assert.That(point.Claimant, Is.SameAs(CoverOf(walker)), "Precondition: the walker holds the reservation");

            var closest = float.PositiveInfinity;
            var deadline = Time.realtimeSinceStartup + 8f;
            while (walker.CurrentCommand != next && Time.realtimeSinceStartup < deadline)
            {
                closest = Mathf.Min(closest, CoverRules.FlatDistance(walker.transform.position, point.Position));
                yield return null;
            }

            Assert.That(closest, Is.GreaterThan(0.2f), "Precondition: avoidance held the walker off the point (it never arrived)");
            Assert.That(walker.CurrentCommand, Is.SameAs(next), "After 3 s without progress the cover order gives up");
            Assert.That(CoverOf(walker).Status, Is.EqualTo(CoverStatus.None), "No stale reservation");
            Assert.That(point.Claimant, Is.Not.SameAs(CoverOf(walker)));
            Assert.That(blocker.CurrentCommand, Is.Null);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
