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
        public IEnumerator ExplicitMove_IsNotInterrupted_AndTheUnitStaysParkedWhereItEnded()
        {
            world.CreateEnvironment();
            var (leader, _, companion) = Squad(new Vector3(0f, 0f, -10f));
            var away = new Vector3(0f, 0f, -16f);
            var order = new MoveCommand(away);
            Assert.That(UnitOf(companion).Issue(order), Is.True);   // same frame as creation: before the first think

            yield return TestWorld.WaitUntil(() => UnitOf(companion).CurrentCommand != order, 10f);
            var whereTheOrderEnded = companion.transform.position;
            Assert.That(TestWorld.HorizontalDistance(whereTheOrderEnded, away), Is.LessThan(0.5f), "The explicit move must run to its end");

            // Decision 022: an explicit order parks the unit, and finishing it does not bring the unit back to Follow.
            var everOrdered = false;
            var deadline = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < deadline)
            {
                everOrdered |= UnitOf(companion).CurrentCommand != null;
                yield return null;
            }
            Assert.That(everOrdered, Is.False, "No follow move may start after the explicit order");
            Assert.That(companion.IsParked, Is.True, "The unit is parked at its resulting position");
            Assert.That(companion.State, Is.EqualTo(CompanionState.Idle));
            Assert.That(TestWorld.HorizontalDistance(companion.transform.position, whereTheOrderEnded), Is.LessThan(0.5f),
                "It stays where it ended (a follow move would have walked it 10 m)");
            Assert.That(Gap(companion, leader), Is.GreaterThan(6f), "Still far from the leader, and still not following");
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

            // Decision 022: the old leader is far from the new one, so the switch parks it instead of sending it after
            // the new leader (a switch never makes a unit move).
            yield return new WaitForSeconds(1f);
            Assert.That(companion.State, Is.EqualTo(CompanionState.Controlled));
            Assert.That(leaderAi.IsParked, Is.True, "The far old leader is parked by the switch");
            Assert.That(leaderAi.State, Is.EqualTo(CompanionState.Idle));
            Assert.That(leader.CurrentCommand, Is.Null, "The old leader does not follow the new one");

            // Roles are fully swapped once the new leader walks up to the old one (magnet) and then away again.
            Assert.That(UnitOf(companion).Issue(new MoveCommand(new Vector3(0f, 0f, -4f))), Is.True);
            yield return TestWorld.WaitUntil(() => !leaderAi.IsParked, 6f);
            Assert.That(leaderAi.IsParked, Is.False, "The new leader walked within the magnet radius of the old one");
            yield return TestWorld.WaitUntil(() => UnitOf(companion).CurrentCommand == null, 6f);
            Assert.That(UnitOf(companion).Issue(new MoveCommand(new Vector3(0f, 0f, -14f))), Is.True);
            yield return TestWorld.WaitUntil(() => leaderAi.IsFollowing, 3f);
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
            // Decision 022: the explicit order parked the unit, so it stays where the order ended (still 16 m from the leader).
            var everOrdered = false;
            var deadline = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < deadline)
            {
                everOrdered |= UnitOf(companion).CurrentCommand != null;
                yield return null;
            }
            Assert.That(everOrdered, Is.False, "Then it stays parked: no follow move");
            Assert.That(companion.IsParked, Is.True);
            Assert.That(Gap(companion, leader), Is.GreaterThan(6f));
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
        public IEnumerator CompanionOrderedAwayFromCover_StaysParkedUntilTheLeaderWalksUp_ThenFollowsAgain()
        {
            var (leader, _, companion, point, _) = CoverSquad(new Vector3(0f, 0f, -12f), new Vector3(0f, 0f, -4f));
            Arm(new Health[0], leader, companion);
            Assert.That(UnitOf(companion).Issue(new MoveToCoverCommand(point)), Is.True);
            yield return TestWorld.WaitUntil(() => CoverOf(companion).Status == CoverStatus.Occupied, 5f);
            yield return new WaitForSeconds(1f);
            Assert.That(UnitOf(companion).CurrentCommand, Is.Null, "Precondition: holding cover");

            Assert.That(UnitOf(companion).Issue(new MoveCommand(new Vector3(0f, 0f, -3f))), Is.True);   // 2 m off the point
            yield return TestWorld.WaitUntil(() => UnitOf(companion).CurrentCommand == null, 5f);
            Assert.That(CoverOf(companion).Status, Is.EqualTo(CoverStatus.None), "The move took it off its cover point");
            Assert.That(point.IsClaimed, Is.False);

            // Decision 022: the explicit move parked it; it stays 9 m from the leader and does not follow.
            yield return new WaitForSeconds(1.5f);
            Assert.That(companion.IsParked, Is.True);
            Assert.That(UnitOf(companion).CurrentCommand, Is.Null, "Leaving cover by order does not bring it back to Follow");
            Assert.That(Gap(companion, leader), Is.GreaterThan(6f));

            // The leader walks within the magnet radius (4.5 m) and then away: it follows again.
            Assert.That(leader.Issue(new MoveCommand(new Vector3(0f, 0f, -7f))), Is.True);   // 4 m from the unit
            yield return TestWorld.WaitUntil(() => !companion.IsParked, 5f);
            Assert.That(companion.IsParked, Is.False, "The leader entered the magnet radius");
            Assert.That(leader.Issue(new MoveCommand(new Vector3(0f, 0f, -14f))), Is.True);
            yield return TestWorld.WaitUntil(() => leader.CurrentCommand == null && UnitOf(companion).CurrentCommand == null && Gap(companion, leader) < 6f, 12f);

            Assert.That(Gap(companion, leader), Is.LessThan(6f), "Once the leader has reacquired it, following resumes");
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

        // --- Follow mode and the magnet (decision 022).

        // A leader at the origin and companions at the given positions; the encounter has no hostiles.
        (CommandableUnit leader, ActiveCharacter active, CompanionAI[] companions) SquadOf(params Vector3[] positions)
        {
            var leader = world.CreateFighter(Vector3.zero);
            leader.name = "Leader";
            var active = world.CreateActiveCharacter(leader);
            var companions = positions.Select(p => world.CreateCompanion(p, active, encounter)).ToArray();
            Arm(new Health[0], new Component[] { leader }.Concat(companions).ToArray());
            return (leader, active, companions);
        }

        // Runs for a number of real seconds, calling the check on every frame.
        static IEnumerator ForSeconds(float seconds, System.Action everyFrame)
        {
            var deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                everyFrame();
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator Switching_ParksTheCompanionsFartherThanTheStartDistance_AndNobodyMoves()
        {
            world.CreateEnvironment();
            // Everyone starts within 6 m of the leader, so nothing is ordered before the switch.
            var (leader, active, companions) = SquadOf(new Vector3(0f, 0f, 5.5f), new Vector3(0f, 0f, -5.5f), new Vector3(2f, 0f, 4f));
            var (newLeader, far, near) = (companions[0], companions[1], companions[2]);
            yield return null;
            yield return null;
            Assert.That(new[] { newLeader, far, near }.All(c => !c.IsParked && UnitOf(c).CurrentCommand == null), Is.True, "Precondition");

            active.SetUnit(UnitOf(newLeader));
            for (var i = 0; i < 4; i++)
            {
                Assert.That(leader.CurrentCommand, Is.Null);
                Assert.That(UnitOf(far).CurrentCommand, Is.Null, "A switch alone orders nobody");
                Assert.That(UnitOf(near).CurrentCommand, Is.Null);
                yield return null;
            }
            Assert.That(far.IsParked, Is.True, "11 m from the new leader: parked");
            Assert.That(near.IsParked, Is.False, "2.2 m from the new leader: stays attached");
            Assert.That(newLeader.IsParked, Is.False, "The controlled character is attached by definition");

            // The new leader walks 6 m away: the parked unit never gets an order, the attached one follows.
            Assert.That(UnitOf(newLeader).Issue(new MoveCommand(new Vector3(0f, 0f, 11.5f))), Is.True);
            yield return ForSeconds(2f, () => Assert.That(UnitOf(far).CurrentCommand, Is.Null, "A parked unit is never ordered by following"));
            yield return TestWorld.WaitUntil(() => near.IsFollowing, 3f);
            Assert.That(near.IsFollowing || UnitOf(near).CurrentCommand == null, Is.True);
            Assert.That(near.IsParked, Is.False);
            Assert.That(far.IsParked, Is.True);
        }

        [UnityTest]
        public IEnumerator Switching_ParksTheOldLeaderToo_WhenItIsFarFromTheNewOne()
        {
            world.CreateEnvironment();
            var (leader, active, companions) = SquadOf(new Vector3(0f, 0f, 5.5f));
            // The leader can be a companion too (added while inactive, so OnEnable sees the wiring).
            leader.gameObject.SetActive(false);
            var oldLeaderAi = leader.gameObject.AddComponent<CompanionAI>();
            oldLeaderAi.Initialize(active, encounter);
            leader.gameObject.SetActive(true);
            yield return null;
            yield return null;
            Assert.That(oldLeaderAi.State, Is.EqualTo(CompanionState.Controlled), "Precondition");

            // Walk the future leader 8 m from the old one first, then switch to it.
            Assert.That(UnitOf(companions[0]).Issue(new MoveCommand(new Vector3(0f, 0f, 8f))), Is.True);
            yield return TestWorld.WaitUntil(() => UnitOf(companions[0]).CurrentCommand == null, 5f);
            Assert.That(Gap(companions[0], leader), Is.GreaterThan(6f), "Precondition");
            active.SetUnit(UnitOf(companions[0]));
            yield return null;
            yield return null;

            Assert.That(oldLeaderAi.IsParked, Is.True, "The previous leader is treated like any other companion at the switch");
            yield return ForSeconds(1f, () => Assert.That(leader.CurrentCommand, Is.Null));
        }

        [UnityTest]
        public IEnumerator ParkedUnit_StaysParkedAtASwitch_EvenInsideTheNewLeadersMagnetRadius()
        {
            world.CreateEnvironment();
            var (_, active, companions) = SquadOf(new Vector3(0f, 0f, -5f), new Vector3(0f, 0f, -8f));
            var (newLeader, parked) = (companions[0], companions[1]);
            Assert.That(UnitOf(parked).Issue(new StopCommand()), Is.True);   // parks it before the first think
            yield return null;
            yield return null;
            Assert.That(parked.IsParked, Is.True, "Precondition: a Stop parks");

            active.SetUnit(UnitOf(newLeader));   // 3 m from the parked unit: inside the magnet radius
            yield return ForSeconds(1f, () =>
            {
                Assert.That(parked.IsParked, Is.True, "A switch alone never attaches anyone");
                Assert.That(UnitOf(parked).CurrentCommand, Is.Null);
            });
        }

        [UnityTest]
        public IEnumerator HandOverWhenTheLeaderDies_ParksTheFarCompanions()
        {
            world.CreateEnvironment();
            var (leader, active, companions) = SquadOf(new Vector3(0f, 0f, 5.5f), new Vector3(0f, 0f, -5.5f), new Vector3(2f, 0f, 4f));
            var (newLeader, far, near) = (companions[0], companions[1], companions[2]);
            // A roster, so the death can hand control over to the next eligible unit (the new leader).
            var roster = world.Track(new GameObject("Roster")).AddComponent<UnitSelection>();
            roster.Initialize(leader.gameObject.AddComponent<SelectableUnit>(), newLeader.gameObject.AddComponent<SelectableUnit>(),
                far.gameObject.AddComponent<SelectableUnit>(), near.gameObject.AddComponent<SelectableUnit>());
            active.Initialize(leader, null, roster);
            yield return null;
            yield return null;

            HealthOf(leader).TakeDamage(1000);
            yield return null;
            yield return null;

            Assert.That(active.Unit, Is.SameAs(UnitOf(newLeader)), "Control passed to the next roster unit");
            Assert.That(far.IsParked, Is.True, "Parked by the hand-over: 11 m from the new leader");
            Assert.That(near.IsParked, Is.False);
            yield return ForSeconds(1.5f, () => Assert.That(UnitOf(far).CurrentCommand, Is.Null));
        }

        [UnityTest]
        public IEnumerator Magnet_ParkedUnitAttachesWhenTheLeaderWalksUp_ThenFollowsWhenItLeaves()
        {
            world.CreateEnvironment();
            var (leader, _, companions) = SquadOf(new Vector3(0f, 0f, -12f));
            var parked = companions[0];
            Assert.That(UnitOf(parked).Issue(new StopCommand()), Is.True);   // parks it before the first think
            yield return TestWorld.WaitUntil(() => parked.IsParked, 1f);
            yield return ForSeconds(0.8f, () => Assert.That(UnitOf(parked).CurrentCommand, Is.Null, "Parked: 12 m away and not following"));

            Assert.That(leader.Issue(new MoveCommand(new Vector3(0f, 0f, -8f))), Is.True);   // 4 m from the parked unit
            yield return TestWorld.WaitUntil(() => !parked.IsParked, 4f);
            Assert.That(parked.IsParked, Is.False, "The leader came within 4.5 m");
            yield return TestWorld.WaitUntil(() => leader.CurrentCommand == null, 4f);
            Assert.That(UnitOf(parked).CurrentCommand, Is.Null, "Attached and 4 m away: nothing to follow yet");

            Assert.That(leader.Issue(new MoveCommand(new Vector3(0f, 0f, 6f))), Is.True);
            yield return TestWorld.WaitUntil(() => parked.IsFollowing, 4f);
            Assert.That(parked.IsFollowing, Is.True, "Once attached, it follows the leader away");
            yield return TestWorld.WaitUntil(() => leader.CurrentCommand == null && UnitOf(parked).CurrentCommand == null, 10f);
            Assert.That(Gap(parked, leader), Is.LessThan(6f));
        }

        [UnityTest]
        public IEnumerator Magnet_OnlyTheControlledCharacterAttaches_AnotherCompanionWalkingPastDoesNot()
        {
            world.CreateEnvironment();
            // The leader stays 8 m away; the walker passes 1.5 m from the parked unit, well inside the 4.5 m radius.
            var leader = world.CreateFighter(new Vector3(-8f, 0f, -9f));
            var active = world.CreateActiveCharacter(leader);
            var parked = world.CreateCompanion(new Vector3(0f, 0f, -10.5f), active, encounter);
            var walker = world.CreateCompanion(new Vector3(-8f, 0f, -12f), active, encounter);
            Arm(new Health[0], leader, parked, walker);
            Assert.That(UnitOf(parked).Issue(new StopCommand()), Is.True);
            var order = new MoveCommand(new Vector3(8f, 0f, -12f));
            Assert.That(UnitOf(walker).Issue(order), Is.True);
            yield return null;   // one frame, so the Stop has parked the unit

            var closest = float.PositiveInfinity;
            yield return ForSeconds(4.5f, () =>
            {
                closest = Mathf.Min(closest, Gap(walker, parked));
                Assert.That(parked.IsParked, Is.True, "Only proximity of the controlled character attaches a parked unit");
                Assert.That(UnitOf(parked).CurrentCommand, Is.Null);
            });
            Assert.That(closest, Is.LessThan(4.5f), "Precondition: the walker really passed inside the magnet radius");
            Assert.That(UnitOf(walker).CurrentCommand, Is.Null, "Precondition: the walker finished its walk");
        }

        [UnityTest]
        public IEnumerator AttachedCompanion_StaysAttached_WhenTheLeaderRunsFarAway()
        {
            world.CreateEnvironment();
            var (leader, _, companions) = SquadOf(new Vector3(0f, 0f, -4f));
            var companion = companions[0];
            yield return null;
            Assert.That(leader.Issue(new MoveCommand(new Vector3(0f, 0f, 16f))), Is.True);

            var everParked = false;
            yield return ForSeconds(1.5f, () => everParked |= companion.IsParked);
            Assert.That(Gap(companion, leader), Is.GreaterThan(4.5f), "Precondition: the leader is beyond the magnet radius");
            yield return TestWorld.WaitUntil(() => leader.CurrentCommand == null && UnitOf(companion).CurrentCommand == null && Gap(companion, leader) < 6f, 10f);

            Assert.That(everParked, Is.False, "The magnet radius never detaches an attached unit");
            Assert.That(companion.IsParked, Is.False);
            Assert.That(companion.transform.position.z, Is.GreaterThan(8f), "It kept following the leader north");
        }

        [UnityTest]
        public IEnumerator QueuedExplicitSequence_NeverReturnsToFollowBetweenOrders_AndStaysParkedAfterTheLast()
        {
            world.CreateEnvironment();
            var (leader, _, companions) = SquadOf(new Vector3(0f, 0f, -10f));
            var companion = companions[0];
            var first = new MoveCommand(new Vector3(0f, 0f, -14f));
            var second = new MoveCommand(new Vector3(6f, 0f, -14f));
            Assert.That(UnitOf(companion).Issue(first), Is.True);
            Assert.That(UnitOf(companion).Issue(second, IssueMode.Append), Is.True);

            var sawSecond = false;
            var stray = false;
            var deadline = Time.realtimeSinceStartup + 12f;
            while (Time.realtimeSinceStartup < deadline)
            {
                var current = UnitOf(companion).CurrentCommand;
                sawSecond |= current == second;
                // Only the two explicit orders may ever be current, and nothing (idle) only once both are done.
                stray |= current != first && current != second && !(current == null && sawSecond);
                if (current == null && sawSecond)
                    break;
                yield return null;
            }
            Assert.That(sawSecond, Is.True, "Precondition: the queued order ran");
            Assert.That(stray, Is.False, "A follow order was current between the queued orders");

            var everOrdered = false;
            yield return ForSeconds(2f, () => everOrdered |= UnitOf(companion).CurrentCommand != null);
            Assert.That(everOrdered, Is.False, "The final explicit order completes: the unit is left parked where it ended");
            Assert.That(companion.IsParked, Is.True);
            Assert.That(Gap(companion, leader), Is.GreaterThan(6f));
        }

        [UnityTest]
        public IEnumerator Stop_ViaGroupOrders_ParksAFollowingCompanion()
        {
            world.CreateEnvironment();
            var (leader, _, companions) = SquadOf(new Vector3(0f, 0f, -12f));
            var companion = companions[0];
            yield return TestWorld.WaitUntil(() => companion.IsFollowing, 1f);
            Assert.That(companion.IsFollowing, Is.True, "Precondition: a follow move is running");

            Assert.That(GroupOrders.Issue(new[] { UnitOf(companion) }, new StopCommand(), IssueMode.Replace), Is.EqualTo(1));

            var everOrdered = false;
            yield return ForSeconds(2f, () => everOrdered |= UnitOf(companion).CurrentCommand != null);
            Assert.That(everOrdered, Is.False, "A Stop parks: no new follow move although the leader is far");
            Assert.That(companion.IsParked, Is.True);
            Assert.That(Gap(companion, leader), Is.GreaterThan(6f));
        }

        [UnityTest]
        public IEnumerator Stop_ViaTheUnit_ParksAFollowingCompanion()
        {
            world.CreateEnvironment();
            var (leader, _, companions) = SquadOf(new Vector3(0f, 0f, -12f));
            var companion = companions[0];
            yield return TestWorld.WaitUntil(() => companion.IsFollowing, 1f);

            Assert.That(UnitOf(companion).Issue(new StopCommand()), Is.True);

            var everOrdered = false;
            yield return ForSeconds(2f, () => everOrdered |= UnitOf(companion).CurrentCommand != null);
            Assert.That(everOrdered, Is.False);
            Assert.That(companion.IsParked, Is.True);
            Assert.That(Gap(companion, leader), Is.GreaterThan(6f));
        }

        [UnityTest]
        public IEnumerator OwnStop_WhenTheUnitIsDriven_DoesNotParkIt()
        {
            world.CreateEnvironment();
            var (leader, _, companions) = SquadOf(new Vector3(0f, 0f, -12f));
            var companion = companions[0];
            yield return TestWorld.WaitUntil(() => companion.IsFollowing, 1f);
            Assert.That(companion.IsFollowing, Is.True, "Precondition: a follow move is running");

            // Something other than the player steers the unit: autonomy yields with its own Stop, which is not a parking Stop.
            UnitOf(companion).SetMoveIntent(Vector3.right);
            yield return null;
            yield return null;
            Assert.That(UnitOf(companion).CurrentCommand, Is.Null, "Precondition: the follow move was dropped");
            UnitOf(companion).SetMoveIntent(Vector3.zero);

            yield return TestWorld.WaitUntil(() => companion.IsFollowing, 2f);
            Assert.That(companion.IsParked, Is.False, "The component's own Stop must not park it");
            Assert.That(companion.IsFollowing, Is.True, "Released again, it follows");
            Assert.That(Gap(companion, leader), Is.GreaterThan(6f));
        }

        [UnityTest]
        public IEnumerator FollowOff_AFarAttachedCompanion_IsNotSentAfterTheLeader_AndStaysAttached()
        {
            world.CreateEnvironment();
            var (leader, active, companions) = SquadOf(new Vector3(0f, 0f, -12f));
            var companion = companions[0];
            active.SetFollow(false);   // before the first think

            var everOrdered = false;
            yield return ForSeconds(2f, () => everOrdered |= UnitOf(companion).CurrentCommand != null);

            Assert.That(everOrdered, Is.False, "Follow OFF: no follow moves");
            Assert.That(companion.IsParked, Is.False, "The flag gates movement only; the unit is still attached");
            Assert.That(Gap(companion, leader), Is.GreaterThan(6f));
        }

        [UnityTest]
        public IEnumerator FollowOff_StopsARunningFollowMove()
        {
            world.CreateEnvironment();
            var (_, active, companions) = SquadOf(new Vector3(0f, 0f, -12f));
            var companion = companions[0];
            yield return TestWorld.WaitUntil(() => companion.IsFollowing, 1f);
            Assert.That(companion.IsFollowing, Is.True, "Precondition: a follow move is running");

            active.ToggleFollow();
            yield return null;
            yield return null;

            Assert.That(UnitOf(companion).CurrentCommand, Is.Null, "A follow move already running is stopped");
            Assert.That(companion.IsFollowing, Is.False);
            Assert.That(companion.IsParked, Is.False, "Its own Stop must not park the unit");
            var everOrdered = false;
            yield return ForSeconds(1.5f, () => everOrdered |= UnitOf(companion).CurrentCommand != null);
            Assert.That(everOrdered, Is.False);
        }

        [UnityTest]
        public IEnumerator FollowOff_DoesNotDropAQueuedExplicitOrder_BehindAFollowMove()
        {
            world.CreateEnvironment();
            var (_, active, companions) = SquadOf(new Vector3(0f, 0f, -12f));
            var companion = companions[0];
            yield return TestWorld.WaitUntil(() => companion.IsFollowing, 1f);
            var away = new Vector3(10f, 0f, -12f);
            Assert.That(UnitOf(companion).Issue(new MoveCommand(away), IssueMode.Append), Is.True);

            active.SetFollow(false);
            yield return null;
            yield return null;

            Assert.That(UnitOf(companion).PendingCommands.Count, Is.EqualTo(1), "The player's queue is never wiped by the flag");
            yield return TestWorld.WaitUntil(() => TestWorld.HorizontalDistance(companion.transform.position, away) < 0.5f, 15f);
            Assert.That(TestWorld.HorizontalDistance(companion.transform.position, away), Is.LessThan(0.5f), "The queued order ran");
        }

        [UnityTest]
        public IEnumerator FollowOff_AssistStillAttacksAnEngagedHostile()
        {
            world.CreateEnvironment();
            var leader = world.CreateFighter(Vector3.zero);
            var active = world.CreateActiveCharacter(leader);
            var companion = world.CreateCompanion(new Vector3(3f, 0f, 0f), active, encounter);
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 6f), encounter);
            Arm(new[] { HealthOf(hostile) }, leader, companion);
            active.SetFollow(false);

            yield return TestWorld.WaitUntil(() => companion.State == CompanionState.Assist, 3f);

            Assert.That(companion.State, Is.EqualTo(CompanionState.Assist), "The flag does not change assist");
            Assert.That(companion.AssistTarget, Is.SameAs(HealthOf(hostile)));
        }

        [UnityTest]
        public IEnumerator Parked_AssistStillAttacksAnEngagedHostile()
        {
            world.CreateEnvironment();
            var leader = world.CreateFighter(Vector3.zero);
            var active = world.CreateActiveCharacter(leader);
            var companion = world.CreateCompanion(new Vector3(3f, 0f, 0f), active, encounter);
            var hostile = world.CreateHostile(new Vector3(0f, 0f, 6f), encounter);
            Arm(new[] { HealthOf(hostile) }, leader, companion);
            Assert.That(UnitOf(companion).Issue(new StopCommand()), Is.True);   // parks it before the first think

            yield return TestWorld.WaitUntil(() => companion.State == CompanionState.Assist, 3f);

            Assert.That(companion.IsParked, Is.True);
            Assert.That(companion.State, Is.EqualTo(CompanionState.Assist), "Parking does not change assist");
        }

        [UnityTest]
        public IEnumerator FollowBackOn_AnAttachedFarCompanionFollows_AParkedOneStaysParked()
        {
            world.CreateEnvironment();
            var (_, active, companions) = SquadOf(new Vector3(0f, 0f, -12f), new Vector3(0f, 0f, 12f));
            var (attached, parked) = (companions[0], companions[1]);
            active.SetFollow(false);   // before the first think
            Assert.That(UnitOf(parked).Issue(new StopCommand()), Is.True);
            yield return ForSeconds(1f, () =>
            {
                Assert.That(UnitOf(attached).CurrentCommand, Is.Null);
                Assert.That(UnitOf(parked).CurrentCommand, Is.Null);
            });

            active.SetFollow(true);

            yield return TestWorld.WaitUntil(() => attached.IsFollowing, 1.5f);
            Assert.That(attached.IsFollowing, Is.True, "Follow back ON: the attached far companion follows");
            var everOrdered = false;
            yield return ForSeconds(1.5f, () => everOrdered |= UnitOf(parked).CurrentCommand != null);
            Assert.That(everOrdered, Is.False, "A parked unit stays parked");
            Assert.That(parked.IsParked, Is.True);
        }

        [UnityTest]
        public IEnumerator RetaliationDoesNotPark_AndTheCompanionRejoinsTheSquad()
        {
            world.CreateEnvironment();
            var (leader, _, companions) = SquadOf(new Vector3(3f, 0f, 0f));
            var companion = companions[0];
            var hostile = world.CreateHostile(new Vector3(3f, 0f, 4f), encounter, detectionRange: 0f);   // never notices anyone
            Arm(new[] { HealthOf(hostile) }, leader, companion);
            yield return null;
            yield return null;

            HealthOf(companion).TakeDamage(5, HealthOf(hostile));
            var fought = false;
            var everParked = false;
            yield return ForSeconds(1.5f, () =>
            {
                fought |= UnitOf(companion).CurrentCommand is AttackCommand;
                everParked |= companion.IsParked;
            });
            Assert.That(fought, Is.True, "Precondition: the companion fought back");
            Assert.That(everParked, Is.False, "A retaliation order does not park");

            // Kill the hostile so the fight ends, then check the unit is still attached and follows the leader away.
            HealthOf(hostile).TakeDamage(1000, HealthOf(companion));
            yield return TestWorld.WaitUntil(() => UnitOf(companion).CurrentCommand == null, 2f);
            Assert.That(companion.IsParked, Is.False, "Not parked after the fight either");
            Assert.That(leader.Issue(new MoveCommand(new Vector3(0f, 0f, -14f))), Is.True);
            yield return TestWorld.WaitUntil(() => companion.IsFollowing, 3f);
            Assert.That(companion.IsFollowing, Is.True, "It rejoins the squad");
        }

        [UnityTest]
        public IEnumerator Paused_FollowToggleAndLeaderSwitchAreAccepted_AndTakeEffectAfterResume()
        {
            world.CreateEnvironment();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var leader = world.CreateFighter(Vector3.zero);
            leader.name = "Leader";
            var active = world.CreateActiveCharacter(leader, pause);
            var newLeader = world.CreateCompanion(new Vector3(0f, 0f, 5.5f), active, encounter);
            var far = world.CreateCompanion(new Vector3(0f, 0f, -5.5f), active, encounter);
            var old = world.CreateCompanion(new Vector3(2f, 0f, 4f), active, encounter);
            Arm(new Health[0], leader, newLeader, far, old);
            var units = new[] { leader, UnitOf(newLeader), UnitOf(far), UnitOf(old) };
            yield return null;   // one running frame, so every companion has seen the first leader
            yield return null;
            pause.Pause();
            var starts = units.Select(u => u.transform.position).ToArray();

            active.ToggleFollow();
            active.SetUnit(UnitOf(newLeader));
            Assert.That(active.IsFollowOn, Is.False, "Follow toggles while paused");
            Assert.That(active.Unit, Is.SameAs(UnitOf(newLeader)), "The leader switches while paused");
            yield return new WaitForSecondsRealtime(1f);
            for (var i = 0; i < units.Length; i++)
            {
                Assert.That(units[i].transform.position, Is.EqualTo(starts[i]), $"{units[i].name} moved while paused");
                Assert.That(units[i].CurrentCommand, Is.Null);
            }
            Assert.That(far.IsParked, Is.False, "Nothing is decided on simulation time while paused");

            pause.Resume();
            yield return new WaitForSeconds(0.5f);
            Assert.That(far.IsParked, Is.True, "After resume the switch parks the companion 11 m from the new leader");
            Assert.That(old.IsParked, Is.False);
            for (var i = 0; i < units.Length; i++)
                Assert.That(units[i].CurrentCommand, Is.Null, $"{units[i].name}: follow is OFF, so nobody moves");

            // The flag works after resume: ON again, and the new leader walks off, the attached unit follows.
            active.SetFollow(true);
            Assert.That(UnitOf(newLeader).Issue(new MoveCommand(new Vector3(0f, 0f, 14f))), Is.True);
            yield return TestWorld.WaitUntil(() => old.IsFollowing, 3f);
            Assert.That(old.IsFollowing, Is.True);
            Assert.That(UnitOf(far).CurrentCommand, Is.Null, "The parked unit stays put");
        }

        [UnityTest]
        public IEnumerator Magnet_IsAnEdge_AUnitStoppedNextToTheLeaderStaysParkedUntilItReEntersTheRadius()
        {
            world.CreateEnvironment();
            var (leader, _, companions) = SquadOf(new Vector3(0f, 0f, -3f));
            var unit = companions[0];
            Assert.That(UnitOf(unit).Issue(new StopCommand()), Is.True);   // parked while the leader stands 3 m away
            yield return null;
            yield return ForSeconds(1f, () =>
            {
                Assert.That(unit.IsParked, Is.True, "The leader is already inside the radius: no attach");
                Assert.That(UnitOf(unit).CurrentCommand, Is.Null);
            });

            // Out of the radius (and past the 6 m start distance): still parked, so it does not follow.
            Assert.That(leader.Issue(new MoveCommand(new Vector3(0f, 0f, 8f))), Is.True);
            yield return TestWorld.WaitUntil(() => leader.CurrentCommand == null, 5f);
            Assert.That(Gap(unit, leader), Is.GreaterThan(6f), "Precondition: well outside the radius");
            yield return ForSeconds(1f, () =>
            {
                Assert.That(unit.IsParked, Is.True, "Leaving the radius never attaches");
                Assert.That(UnitOf(unit).CurrentCommand, Is.Null);
            });

            // Back inside: an edge, so now it attaches.
            Assert.That(leader.Issue(new MoveCommand(new Vector3(3f, 0f, 0f))), Is.True);   // 4.2 m from the unit
            yield return TestWorld.WaitUntil(() => !unit.IsParked, 5f);
            Assert.That(unit.IsParked, Is.False, "Entering the radius again attaches it");
        }
    }
}
