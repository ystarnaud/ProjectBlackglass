using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CompanionAIPlayModeTests
    {
        TestWorld world;
        Encounter encounter;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            encounter = world.CreateEncounter();
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        static Health HealthOf(Component unit) => unit.GetComponent<Health>();
        static CommandableUnit UnitOf(Component c) => c.GetComponent<CommandableUnit>();
        static float Gap(Component a, Component b) => TestWorld.HorizontalDistance(a.transform.position, b.transform.position);

        void Arm(Health[] hostiles, params Component[] friendlies) =>
            encounter.Initialize(friendlies.Select(f => HealthOf(f)), hostiles);

        // A leader on open ground with a companion at the given offset; the encounter has no hostiles.
        (CommandableUnit leader, ActiveCharacter active, CompanionAI companion) Squad(Vector3 companionPosition)
        {
            var leader = world.CreateFighter(Vector3.zero);
            leader.name = "Leader";
            var active = world.CreateActiveCharacter(leader);
            var companion = world.CreateCompanion(companionPosition, active, encounter);
            Arm(new Health[0], leader, companion);
            return (leader, active, companion);
        }

        [UnityTest]
        public IEnumerator IdleCompanionFarFromTheLeader_WalksCloser_AndStops()
        {
            world.CreateEnvironment();
            var (leader, _, companion) = Squad(new Vector3(0f, 0f, -12f));

            yield return TestWorld.WaitUntil(() => companion.State == CompanionState.Follow, 1f);
            Assert.That(companion.State, Is.EqualTo(CompanionState.Follow), "A companion 12 m away must start following");
            yield return TestWorld.WaitUntil(() => UnitOf(companion).CurrentCommand == null, 10f);
            var arrived = Gap(companion, leader);
            yield return new WaitForSeconds(1f);

            Assert.That(UnitOf(companion).CurrentCommand, Is.Null, "Once close it stays put");
            Assert.That(companion.State, Is.EqualTo(CompanionState.Idle));
            Assert.That(arrived, Is.LessThanOrEqualTo(4.5f).And.GreaterThanOrEqualTo(2.5f), "It stops short of the leader, not on top of it");
            Assert.That(Gap(companion, leader), Is.EqualTo(arrived).Within(0.3f), "No pacing after arrival");
        }

        [UnityTest]
        public IEnumerator CompanionNearTheLeader_IssuesNoMove()
        {
            world.CreateEnvironment();
            var (_, _, companion) = Squad(new Vector3(3f, 0f, 0f));
            var start = companion.transform.position;
            var everOrdered = false;

            var deadline = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < deadline)
            {
                everOrdered |= UnitOf(companion).CurrentCommand != null;
                yield return null;
            }

            Assert.That(everOrdered, Is.False, "Inside the start distance the companion must not move");
            Assert.That(TestWorld.HorizontalDistance(companion.transform.position, start), Is.LessThan(0.05f));
            Assert.That(companion.State, Is.EqualTo(CompanionState.Idle));
        }

        [UnityTest]
        public IEnumerator LeaderWalksAway_CompanionRepathsAndKeepsUp()
        {
            world.CreateEnvironment();
            var (leader, _, companion) = Squad(new Vector3(2f, 0f, 0f));
            Assert.That(leader.Issue(new MoveCommand(new Vector3(0f, 0f, 15f))), Is.True);

            yield return TestWorld.WaitUntil(() => leader.CurrentCommand == null, 10f);
            Assert.That(leader.CurrentCommand, Is.Null, "Precondition: the leader arrived");
            yield return TestWorld.WaitUntil(() => UnitOf(companion).CurrentCommand == null && Gap(companion, leader) < 6f, 10f);

            Assert.That(Gap(companion, leader), Is.LessThan(6f), "The companion must end up near the leader");
            Assert.That(companion.transform.position.z, Is.GreaterThan(8f), "It followed the leader north");
        }

        [UnityTest]
        public IEnumerator ExplicitMove_IsNotInterrupted_AndFollowResumesAfterwards()
        {
            world.CreateEnvironment();
            var (leader, _, companion) = Squad(new Vector3(0f, 0f, -10f));
            var away = new Vector3(0f, 0f, -16f);
            var order = new MoveCommand(away);
            Assert.That(UnitOf(companion).Issue(order), Is.True);   // same frame as creation: before the first think

            yield return TestWorld.WaitUntil(() => UnitOf(companion).CurrentCommand != order, 10f);
            var whereTheOrderEnded = companion.transform.position;
            Assert.That(TestWorld.HorizontalDistance(whereTheOrderEnded, away), Is.LessThan(0.5f), "The explicit move must run to its end");

            yield return TestWorld.WaitUntil(() => Gap(companion, leader) < 6f, 12f);
            Assert.That(Gap(companion, leader), Is.LessThan(6f), "Following resumes once the explicit order is done");
        }

        [UnityTest]
        public IEnumerator QueuedOrder_BehindAFollowMove_IsNotInterrupted()
        {
            world.CreateEnvironment();
            var (leader, _, companion) = Squad(new Vector3(0f, 0f, -12f));
            yield return TestWorld.WaitUntil(() => companion.IsFollowing, 1f);
            Assert.That(companion.IsFollowing, Is.True, "Precondition: a follow move is running");

            var away = new Vector3(10f, 0f, -12f);
            Assert.That(UnitOf(companion).Issue(new MoveCommand(away), IssueMode.Append), Is.True);
            Assert.That(companion.State, Is.EqualTo(CompanionState.Orders), "A pending order makes the companion 'under orders'");

            yield return TestWorld.WaitUntil(() => TestWorld.HorizontalDistance(companion.transform.position, away) < 0.5f, 15f);
            Assert.That(TestWorld.HorizontalDistance(companion.transform.position, away), Is.LessThan(0.5f),
                "The queued order must run after the follow move, not be dropped");
        }

        [UnityTest]
        public IEnumerator SwitchingTheControlledCharacter_SwapsRoles()
        {
            world.CreateEnvironment();
            var (leader, active, companion) = Squad(new Vector3(0f, 0f, -12f));
            // The leader can be a companion too. Added while inactive, so OnEnable sees the wiring and logs no warning.
            leader.gameObject.SetActive(false);
            var leaderAi = leader.gameObject.AddComponent<CompanionAI>();
            leaderAi.Initialize(active, encounter);
            leader.gameObject.SetActive(true);
            yield return TestWorld.WaitUntil(() => companion.IsFollowing, 1f);
            Assert.That(companion.IsFollowing, Is.True, "Precondition");
            Assert.That(leaderAi.State, Is.EqualTo(CompanionState.Controlled));

            active.SetUnit(UnitOf(companion));
            yield return null;   // one simulation frame: the per-frame controlled check, not the 0.25 s tick
            Assert.That(UnitOf(companion).CurrentCommand, Is.Null, "The newly controlled unit drops its own follow move on the next frame");
            yield return TestWorld.WaitUntil(() => leaderAi.IsFollowing, 1f);

            Assert.That(companion.State, Is.EqualTo(CompanionState.Controlled));
            Assert.That(leaderAi.State, Is.EqualTo(CompanionState.Follow), "The old leader now follows the new one");
        }

        [UnityTest]
        public IEnumerator LeaderWalksIntoAFollowingCompanion_NeverSendsItBackwards()
        {
            world.CreateEnvironment();
            var (leader, _, companion) = Squad(new Vector3(0f, 0f, -13f));
            yield return TestWorld.WaitUntil(() => companion.IsFollowing, 1f);
            Assert.That(companion.IsFollowing, Is.True, "Precondition");
            Assert.That(leader.Issue(new MoveCommand(new Vector3(0f, 0f, -10f))), Is.True);   // straight at the companion

            // Every follow move must aim no farther from the leader than the companion already stands.
            UnitCommand lastSeen = null;
            var deadline = Time.realtimeSinceStartup + 6f;
            while (Time.realtimeSinceStartup < deadline)
            {
                var current = UnitOf(companion).CurrentCommand;
                if (current is MoveCommand move && current != lastSeen)
                {
                    lastSeen = current;
                    var leaderNow = leader.transform.position;
                    Assert.That(TestWorld.HorizontalDistance(move.Destination, leaderNow),
                        Is.LessThanOrEqualTo(TestWorld.HorizontalDistance(companion.transform.position, leaderNow) + 0.1f),
                        "A follow move was aimed away from the leader");
                }
                yield return null;
            }
            Assert.That(Gap(companion, leader), Is.LessThan(6f), "It settles near the leader");
        }

        [UnityTest]
        public IEnumerator FollowPointInsideABlock_IsSnapped_AndTheCompanionStillArrives()
        {
            // A 3 m block between them: the raw follow point (3.5 m from the leader, toward the companion) is inside
            // it, farther than MoveTo's 2 m snap from the eroded mesh. No warning may be logged; the companion arrives.
            world.CreateEnvironment((new Vector3(0f, 1f, -4f), new Vector3(3f, 2f, 3f)));
            var (leader, _, companion) = Squad(new Vector3(0f, 0f, -9f));

            yield return TestWorld.WaitUntil(() => UnitOf(companion).CurrentCommand == null && Gap(companion, leader) < 6f, 12f);

            Assert.That(Gap(companion, leader), Is.LessThan(6f), "The companion must get around the block");
            Assert.That(companion.State, Is.EqualTo(CompanionState.Idle), "It arrived and settled; nothing is being retried");
        }

        [UnityTest]
        public IEnumerator Switching_LeavesAnExplicitOrderOnTheNewLeaderAlone()
        {
            world.CreateEnvironment();
            var (leader, active, companion) = Squad(new Vector3(0f, 0f, -12f));
            var order = new MoveCommand(new Vector3(0f, 0f, -16f));
            Assert.That(UnitOf(companion).Issue(order), Is.True);

            active.SetUnit(UnitOf(companion));
            yield return null;   // one simulation frame: the per-frame controlled check must not stop an explicit order
            Assert.That(UnitOf(companion).CurrentCommand, Is.SameAs(order), "An explicit order is never stopped by the hand-over");

            yield return TestWorld.WaitUntil(() => UnitOf(companion).CurrentCommand == null, 10f);
            Assert.That(TestWorld.HorizontalDistance(companion.transform.position, order.Destination), Is.LessThan(0.5f),
                "The order ran to completion");
        }

        [UnityTest]
        public IEnumerator ControlledCompanionWithQueuedOrders_KeepsItsQueue()
        {
            world.CreateEnvironment();
            var (_, active, companion) = Squad(new Vector3(0f, 0f, -12f));
            yield return TestWorld.WaitUntil(() => companion.IsFollowing, 1f);
            Assert.That(companion.IsFollowing, Is.True, "Precondition: a follow move is running");
            var queued = new Vector3(6f, 0f, -12f);
            Assert.That(UnitOf(companion).Issue(new MoveCommand(queued), IssueMode.Append), Is.True);

            active.SetUnit(UnitOf(companion));
            yield return null;   // one simulation frame: the per-frame controlled check

            Assert.That(UnitOf(companion).CurrentCommand, Is.TypeOf<MoveCommand>(), "The hand-over must not stop while orders are pending");
            Assert.That(UnitOf(companion).PendingCommands.Count, Is.EqualTo(1), "The player's queued order is kept");

            yield return TestWorld.WaitUntil(() => TestWorld.HorizontalDistance(companion.transform.position, queued) < 0.5f, 15f);
            Assert.That(TestWorld.HorizontalDistance(companion.transform.position, queued), Is.LessThan(0.5f), "The queued order ran");
        }

        [UnityTest]
        public IEnumerator HostileAttackingTheLeader_IsAttackedByTheCompanion()
        {
            world.CreateEnvironment();
            var leader = world.CreateFighter(Vector3.zero);
            var active = world.CreateActiveCharacter(leader);
            var companion = world.CreateCompanion(new Vector3(3f, 0f, 0f), active, encounter);
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 6f), encounter);
            Arm(new[] { HealthOf(hostile) }, leader, companion);

            yield return TestWorld.WaitUntil(() => companion.State == CompanionState.Assist, 3f);

            Assert.That(companion.State, Is.EqualTo(CompanionState.Assist), "An engaged hostile 6 m away must be assisted against");
            Assert.That(companion.AssistTarget, Is.SameAs(HealthOf(hostile)));
            Assert.That(UnitOf(companion).CurrentCommand, Is.TypeOf<AttackCommand>());
        }

        [UnityTest]
        public IEnumerator UnawareIdleHostileNearby_IsLeftAlone()
        {
            world.CreateEnvironment();
            var leader = world.CreateFighter(Vector3.zero);
            var active = world.CreateActiveCharacter(leader);
            var companion = world.CreateCompanion(new Vector3(3f, 0f, 0f), active, encounter);
            var hostile = world.CreateHostile(new Vector3(3f, 0f, 5f), encounter, detectionRange: 0f);   // never notices anyone
            Arm(new[] { HealthOf(hostile) }, leader, companion);

            yield return new WaitForSeconds(1.5f);

            Assert.That(UnitOf(companion).CurrentCommand, Is.Null, "Companions do not start fights with unaware hostiles");
            Assert.That(HealthOf(hostile).Current, Is.EqualTo(HealthOf(hostile).Max));
            Assert.That(companion.State, Is.EqualTo(CompanionState.Idle));
        }

        [UnityTest]
        public IEnumerator LeadersTarget_IsPreferredOverANearerEngagedHostile()
        {
            world.CreateEnvironment();
            var leader = world.CreateFighter(Vector3.zero);
            var active = world.CreateActiveCharacter(leader);
            var companion = world.CreateCompanion(new Vector3(0f, 0f, -2f), active, encounter);
            var leaderTarget = world.CreateHostile(new Vector3(0f, 0f, 7f), encounter, detectionRange: 0f);
            var nearer = world.CreateHostile(new Vector3(-4f, 0f, -2f), encounter, detectionRange: 0f);
            Arm(new[] { HealthOf(leaderTarget), HealthOf(nearer) }, leader, companion);
            Assert.That(UnitOf(nearer).Issue(new AttackCommand(HealthOf(leader))), Is.True);   // engaged, with the leader
            Assert.That(leader.Issue(new AttackCommand(HealthOf(leaderTarget))), Is.True);

            yield return TestWorld.WaitUntil(() => companion.AssistTarget != null, 2f);

            Assert.That(companion.AssistTarget, Is.SameAs(HealthOf(leaderTarget)), "The leader's target beats a nearer engaged hostile");
        }

        [UnityTest]
        public IEnumerator ExplicitOrderDuringAssist_ReplacesIt_AndIsNotRetaken()
        {
            world.CreateEnvironment();
            var leader = world.CreateFighter(Vector3.zero);
            var active = world.CreateActiveCharacter(leader);
            var companion = world.CreateCompanion(new Vector3(3f, 0f, 0f), active, encounter);
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 6f), encounter);
            Arm(new[] { HealthOf(hostile) }, leader, companion);
            yield return TestWorld.WaitUntil(() => companion.State == CompanionState.Assist, 3f);
            Assert.That(companion.State, Is.EqualTo(CompanionState.Assist), "Precondition");

            var retreat = new MoveCommand(new Vector3(12f, 0f, -8f));
            Assert.That(UnitOf(companion).Issue(retreat), Is.True);
            var deadline = Time.realtimeSinceStartup + 1.5f;
            while (Time.realtimeSinceStartup < deadline)
            {
                Assert.That(UnitOf(companion).CurrentCommand, Is.SameAs(retreat), "The AI must not retake control while the explicit move runs");
                Assert.That(companion.State, Is.EqualTo(CompanionState.Orders));
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator DeadCompanion_DoesNothing()
        {
            world.CreateEnvironment();
            var (_, _, companion) = Squad(new Vector3(0f, 0f, -12f));
            HealthOf(companion).TakeDamage(1000);

            yield return new WaitForSeconds(0.8f);

            Assert.That(companion.State, Is.EqualTo(CompanionState.Dead));
            Assert.That(UnitOf(companion).CurrentCommand, Is.Null);
            // Any exception logged while the dead companion's object is inactive fails the test on its own.
        }

        [UnityTest]
        public IEnumerator Paused_CompanionNeitherMovesNorGainsOrders_AndAcceptsOrders()
        {
            world.CreateEnvironment();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            pause.Pause();
            var (leader, _, companion) = Squad(new Vector3(0f, 0f, -12f));   // created paused: the first think must wait
            var start = companion.transform.position;

            yield return new WaitForSecondsRealtime(1f);
            Assert.That(UnitOf(companion).CurrentCommand, Is.Null, "No autonomous order while paused");
            Assert.That(companion.transform.position, Is.EqualTo(start));

            var order = new MoveCommand(new Vector3(0f, 0f, -16f));
            Assert.That(UnitOf(companion).Issue(order), Is.True, "Tactical orders are still accepted while paused");
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(companion.transform.position, Is.EqualTo(start), "Accepted, but not run until resume");

            pause.Resume();
            yield return TestWorld.WaitUntil(() => UnitOf(companion).CurrentCommand != order, 8f);
            Assert.That(TestWorld.HorizontalDistance(companion.transform.position, order.Destination), Is.LessThan(0.5f),
                "After resume the explicit order runs first, uninterrupted by follow");
            yield return TestWorld.WaitUntil(() => Gap(companion, leader) < 6f, 12f);
            Assert.That(Gap(companion, leader), Is.LessThan(6f), "Then follow resumes");
        }

        static UnitCover CoverOf(Component unit) => unit.GetComponent<UnitCover>();

        // A 0.9 m wall from z -0.25 to 0.25 (x -2..2) with one point 0.75 m south of it, a sturdy leader and a
        // companion whose UnitCover is wired to the registry.
        (CommandableUnit leader, ActiveCharacter active, CompanionAI companion, CoverPoint point, CoverRegistry registry) CoverSquad(Vector3 leaderAt, Vector3 companionAt)
        {
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            var point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            var registry = world.CreateRegistry(point);
            var leader = world.CreateFighter(leaderAt, maxHealth: 300);
            leader.name = "Leader";
            var active = world.CreateActiveCharacter(leader);
            var companion = world.CreateCompanion(companionAt, active, encounter, registry: registry);
            return (leader, active, companion, point, registry);
        }

        [UnityTest]
        public IEnumerator CompanionOrderedIntoCover_HoldsIt_InsteadOfFollowing()
        {
            var (leader, _, companion, point, _) = CoverSquad(new Vector3(0f, 0f, -12f), new Vector3(0f, 0f, -4f));
            Arm(new Health[0], leader, companion);
            Assert.That(UnitOf(companion).Issue(new MoveToCoverCommand(point)), Is.True);   // same frame as creation: before the first think
            yield return TestWorld.WaitUntil(() => CoverOf(companion).Status == CoverStatus.Occupied, 5f);
            Assert.That(CoverOf(companion).OccupiedByOrder, Is.True, "Precondition: ordered cover");
            yield return new WaitForSeconds(2f);

            Assert.That(UnitOf(companion).CurrentCommand, Is.Null, "No follow move was issued");
            Assert.That(companion.State, Is.EqualTo(CompanionState.Idle));
            Assert.That(CoverOf(companion).Status, Is.EqualTo(CoverStatus.Occupied));
            Assert.That(Gap(companion, leader), Is.GreaterThan(6f), "It stayed 11 m from the leader, well beyond the follow start distance");
        }

        [UnityTest]
        public IEnumerator MeleeCompanionHoldingCover_DoesNotAssist_AgainstAHostileItCannotHitFromThere()
        {
            // The leader and a hostile fight in melee 7.8 m from the companion's point: engaged and inside assist range.
            var (leader, _, companion, point, registry) = CoverSquad(new Vector3(6f, 0f, 5.5f), new Vector3(0f, 0f, -4f));
            var hostile = world.CreateHostile(new Vector3(6f, 0f, 4f), encounter, maxHealth: 300, registry: registry);
            Arm(new[] { HealthOf(hostile) }, leader, companion);
            Assert.That(UnitOf(companion).Issue(new MoveToCoverCommand(point)), Is.True);
            yield return TestWorld.WaitUntil(() => CoverOf(companion).Status == CoverStatus.Occupied, 5f);
            Assert.That(CoverOf(companion).OccupiedByOrder, Is.True, "Precondition: ordered cover");
            yield return TestWorld.WaitUntil(() => hostile.Target == HealthOf(leader), 2f);
            Assert.That(hostile.Target, Is.SameAs(HealthOf(leader)), "Precondition: the hostile is engaged with the leader");

            var everLeft = false;
            var deadline = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < deadline)
            {
                everLeft |= companion.State == CompanionState.Assist || UnitOf(companion).CurrentCommand != null;
                yield return null;
            }

            Assert.That(everLeft, Is.False, "A melee companion cannot hit the hostile from its cover, so it holds instead of charging");
            Assert.That(CoverOf(companion).Status, Is.EqualTo(CoverStatus.Occupied));
        }

        [UnityTest]
        public IEnumerator CompanionOrderedAwayFromCover_FollowsAgain()
        {
            var (leader, _, companion, point, _) = CoverSquad(new Vector3(0f, 0f, -12f), new Vector3(0f, 0f, -4f));
            Arm(new Health[0], leader, companion);
            Assert.That(UnitOf(companion).Issue(new MoveToCoverCommand(point)), Is.True);
            yield return TestWorld.WaitUntil(() => CoverOf(companion).Status == CoverStatus.Occupied, 5f);
            yield return new WaitForSeconds(1f);
            Assert.That(UnitOf(companion).CurrentCommand, Is.Null, "Precondition: holding cover");

            Assert.That(UnitOf(companion).Issue(new MoveCommand(new Vector3(0f, 0f, -3f))), Is.True);   // 2 m off the point
            yield return TestWorld.WaitUntil(() => Gap(companion, leader) < 6f, 12f);

            Assert.That(Gap(companion, leader), Is.LessThan(6f), "Once it has left its cover, following resumes");
            Assert.That(CoverOf(companion).Status, Is.EqualTo(CoverStatus.None));
            Assert.That(point.IsClaimed, Is.False);
        }

        [UnityTest]
        public IEnumerator CompanionThatStopsOnAPointByChance_StillFollows()
        {
            // A second point exactly where the follow move ends: 3.5 m from the leader on the companion's side.
            var (leader, _, companion, point, registry) = CoverSquad(new Vector3(0f, 0f, -12f), new Vector3(0f, 0f, 2f));
            var byChance = world.CreateCoverPoint(new Vector3(0f, 0f, -8.5f), Vector3.forward, point.Obstacle);
            registry.Initialize(point, byChance);
            Arm(new Health[0], leader, companion);

            yield return TestWorld.WaitUntil(() => CoverOf(companion).Status == CoverStatus.Occupied, 12f);
            Assert.That(CoverOf(companion).Point, Is.SameAs(byChance), "The follow move ended on the point and the companion stood still");
            Assert.That(CoverOf(companion).OccupiedByOrder, Is.False);

            Assert.That(leader.Issue(new MoveCommand(new Vector3(0f, 0f, -18f))), Is.True);
            yield return TestWorld.WaitUntil(() => leader.CurrentCommand == null, 10f);
            yield return TestWorld.WaitUntil(() => Gap(companion, leader) < 6f && CoverOf(companion).Status == CoverStatus.None, 12f);

            Assert.That(Gap(companion, leader), Is.LessThan(6f), "An incidental occupancy never holds a companion");
            Assert.That(byChance.IsClaimed, Is.False);
        }
    }
}
