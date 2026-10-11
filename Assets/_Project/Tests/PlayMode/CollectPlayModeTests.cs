using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CollectPlayModeTests
    {
        TestWorld world;
        TacticalPause pause;
        InventoryKit kit;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            kit = new InventoryKit(world, 2, "a", "b");
            kit.BeginMission();
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        (CommandableUnit unit, UnitItems items) Collector(string id, Vector3 ground)
        {
            var unit = world.CreateFighter(ground);
            return (unit, kit.Equip(unit, id));
        }

        LootContainer Container(Vector3 ground, bool searched = true, params (ItemDefinition, int)[] items) =>
            kit.CreateContainer(ground, searched, items);

        [UnityTest]
        public IEnumerator TakeOneEntry_WalksToTheContainer_AndMovesJustThatEntry()
        {
            var container = Container(new Vector3(8f, 0f, 0f), true, (kit.Rifle, 1), (kit.Vest, 1));
            var (unit, items) = Collector("a", new Vector3(0f, 0f, 0f));
            yield return null;
            var rifleId = container.Contents.Entries.First(e => e.DefinitionId == "rifle").InstanceId;

            Assert.That(unit.Issue(new CollectCommand(container, rifleId)), Is.True);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 15f);

            Assert.That(kit.Bag("a").CountOf("rifle"), Is.EqualTo(1));
            Assert.That(container.Contents.CountOf("rifle"), Is.EqualTo(0));
            Assert.That(container.Contents.CountOf("vest"), Is.EqualTo(1), "only the chosen entry moved");
            Assert.That(items.LastCollect.Failure, Is.EqualTo(CollectFailure.None));
            Assert.That(items.LastCollect.Taken, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator TakeWhatFits_WithAFullBag_LeavesTheRestInTheContainer()
        {
            var container = Container(new Vector3(2f, 0f, 0f), true, (kit.Rifle, 1), (kit.Vest, 1), (kit.Medkit, 2));
            var (unit, items) = Collector("a", Vector3.zero);   // the bag holds 2 entries
            yield return null;

            Assert.That(unit.Issue(new CollectCommand(container)), Is.True);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);

            Assert.That(kit.Bag("a").Count, Is.EqualTo(2));
            Assert.That(container.Contents.Count, Is.EqualTo(1), "one entry did not fit and stayed");
            Assert.That(items.LastCollect.Failure, Is.EqualTo(CollectFailure.BagFull));
            Assert.That(items.LastCollect.Left, Is.GreaterThan(0));
            Assert.That(items.LastCollect.Taken, Is.GreaterThan(0));
            var total = kit.Bag("a").Entries.Sum(e => e.Quantity) + container.Contents.Entries.Sum(e => e.Quantity);
            Assert.That(total, Is.EqualTo(4), "nothing was destroyed or duplicated");
        }

        [UnityTest]
        public IEnumerator ABagWithNoRoomForAnItem_TakesNothing_AndSaysSo()
        {
            kit.Bag("a").Add(kit.Rifle, 2);
            var container = Container(new Vector3(2f, 0f, 0f), true, (kit.Vest, 1));
            var (unit, items) = Collector("a", Vector3.zero);
            yield return null;

            unit.Issue(new CollectCommand(container));
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);

            Assert.That(container.Contents.CountOf("vest"), Is.EqualTo(1));
            Assert.That(items.LastCollect.Taken, Is.EqualTo(0));
            Assert.That(items.LastCollect.Failure, Is.EqualTo(CollectFailure.BagFull));
        }

        [UnityTest]
        public IEnumerator TwoOperativesTakingTheSameEntry_ExactlyOneGetsIt()
        {
            var container = Container(new Vector3(2f, 0f, 0f), true, (kit.Rifle, 1));
            var (first, _) = Collector("a", new Vector3(0f, 0f, 1f));
            var (second, _) = Collector("b", new Vector3(0f, 0f, -1f));
            yield return null;
            var id = container.Contents.Entries[0].InstanceId;

            first.Issue(new CollectCommand(container, id));
            second.Issue(new CollectCommand(container, id));
            yield return TestWorld.WaitUntil(() => first.CurrentCommand == null && second.CurrentCommand == null, 10f);

            Assert.That(kit.Bag("a").CountOf("rifle") + kit.Bag("b").CountOf("rifle"), Is.EqualTo(1));
            Assert.That(container.Contents.Count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator TheContainerDisappearingMidWalk_EndsTheOrder_WithoutErrors()
        {
            var container = Container(new Vector3(12f, 0f, 0f), true, (kit.Rifle, 1));
            var (unit, items) = Collector("a", Vector3.zero);
            yield return null;
            unit.Issue(new CollectCommand(container));
            yield return null;

            Object.Destroy(container.gameObject);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 5f);

            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(items.LastCollect.Failure, Is.EqualTo(CollectFailure.NoContainer));
            Assert.That(kit.Bag("a").Count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator TheCollectorDyingMidWalk_EndsTheOrder_TakesNothing_AndLeavesTheContainerAlone()
        {
            var container = Container(new Vector3(12f, 0f, 0f), true, (kit.Rifle, 1));
            var (unit, items) = Collector("a", Vector3.zero);
            yield return null;
            Assert.That(unit.Issue(new CollectCommand(container)), Is.True);
            yield return null;
            Assert.That(unit.CurrentCommand, Is.TypeOf<CollectCommand>(), "the order is running (the unit has to walk)");

            unit.GetComponent<Health>().TakeDamage(1000);
            for (var i = 0; i < 5; i++)
                yield return null;

            Assert.That(unit.CurrentCommand, Is.Null, "death ended the order");
            Assert.That(container.Contents.CountOf("rifle"), Is.EqualTo(1));
            Assert.That(container.Contents.Count, Is.EqualTo(1));
            Assert.That(kit.Bag("a").Count, Is.EqualTo(0));
            Assert.That(items.LastCollect.Taken, Is.EqualTo(0));
            Assert.That(items.CheckCollect(container, false), Is.EqualTo(CollectFailure.Dead), "a dead unit may not take");
        }

        [UnityTest]
        public IEnumerator TheCollectorDyingWhilePaused_InReach_TakesNothing_WhenTheGameResumes()
        {
            var container = Container(new Vector3(1f, 0f, 0f), true, (kit.Rifle, 1));
            var (unit, items) = Collector("a", Vector3.zero);
            yield return null;
            pause.Pause();
            Assert.That(unit.Issue(new CollectCommand(container)), Is.True);
            Assert.That(unit.CurrentCommand, Is.TypeOf<CollectCommand>());

            unit.GetComponent<Health>().TakeDamage(1000);   // the unit is deactivated; its order is dropped
            pause.Resume();
            for (var i = 0; i < 5; i++)
                yield return null;

            Assert.That(container.Contents.CountOf("rifle"), Is.EqualTo(1), "the dead unit took nothing");
            Assert.That(kit.Bag("a").Count, Is.EqualTo(0));
            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(items.LastCollect.Taken, Is.EqualTo(0));
            Assert.That(items.CheckCollect(container, true), Is.EqualTo(CollectFailure.Dead));
        }

        [UnityTest]
        public IEnumerator AnUnsearchedContainer_CannotBeTakenFrom()
        {
            var container = Container(new Vector3(2f, 0f, 0f), false, (kit.Rifle, 1));
            var (unit, items) = Collector("a", Vector3.zero);
            yield return null;

            Assert.That(unit.Issue(new CollectCommand(container)), Is.False);
            Assert.That(items.CheckCollect(container, false), Is.EqualTo(CollectFailure.NotSearched));
            Assert.That(container.Contents.CountOf("rifle"), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Paused_NothingMoves_UntilResume()
        {
            // In reach (1 m, range 1.8 m): the transfer would happen on the first running frame, so only the pause holds it back.
            var container = Container(new Vector3(1f, 0f, 0f), true, (kit.Rifle, 1));
            var (unit, _) = Collector("a", Vector3.zero);
            yield return null;
            pause.Pause();

            Assert.That(unit.Issue(new CollectCommand(container)), Is.True);
            for (var i = 0; i < 5; i++)
                yield return null;
            Assert.That(unit.CurrentCommand, Is.TypeOf<CollectCommand>(), "still waiting");
            Assert.That(container.Contents.CountOf("rifle"), Is.EqualTo(1), "no world loot transfers while paused");
            Assert.That(kit.Bag("a").Count, Is.EqualTo(0));

            pause.Resume();
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);
            Assert.That(kit.Bag("a").CountOf("rifle"), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator StoppedWhilePaused_BeforeItRuns_TakesNothing()
        {
            // In reach and paused: left alone, the order would take on the first running frame.
            var container = Container(new Vector3(1f, 0f, 0f), true, (kit.Rifle, 1));
            var (unit, items) = Collector("a", Vector3.zero);
            yield return null;
            pause.Pause();
            Assert.That(unit.Issue(new CollectCommand(container)), Is.True);
            Assert.That(unit.CurrentCommand, Is.TypeOf<CollectCommand>());

            Assert.That(unit.Issue(new StopCommand()), Is.True);
            pause.Resume();
            for (var i = 0; i < 5; i++)
                yield return null;

            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(container.Contents.CountOf("rifle"), Is.EqualTo(1));
            Assert.That(kit.Bag("a").Count, Is.EqualTo(0));
            Assert.That(items.LastCollect.Taken, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator ReplacedWhilePaused_BeforeItRuns_TakesNothing()
        {
            var container = Container(new Vector3(1f, 0f, 0f), true, (kit.Rifle, 1));
            var (unit, items) = Collector("a", Vector3.zero);
            yield return null;
            pause.Pause();
            Assert.That(unit.Issue(new CollectCommand(container)), Is.True);

            Assert.That(unit.Issue(new MoveCommand(new Vector3(-4f, 0f, 0f))), Is.True);
            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            pause.Resume();
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);

            Assert.That(container.Contents.CountOf("rifle"), Is.EqualTo(1));
            Assert.That(kit.Bag("a").Count, Is.EqualTo(0));
            Assert.That(items.LastCollect.Taken, Is.EqualTo(0));
            Assert.That(unit.transform.position.x, Is.LessThan(-3f), "it did the move instead");
        }

        [UnityTest]
        public IEnumerator AQueuedCollectThatIsRefused_RecordsWhy()
        {
            var container = Container(new Vector3(2f, 0f, 0f), false, (kit.Rifle, 1));
            var (unit, items) = Collector("a", Vector3.zero);
            yield return null;
            Assert.That(unit.Issue(new MoveCommand(new Vector3(-6f, 0f, 0f))), Is.True);

            Assert.That(unit.Issue(new CollectCommand(container), IssueMode.Append), Is.False);

            Assert.That(items.LastCollect.Failure, Is.EqualTo(CollectFailure.NotSearched));
        }

        [UnityTest]
        public IEnumerator TakingEverything_MakesTheContainerUnavailable_TakingSome_DoesNot()
        {
            var container = Container(new Vector3(1f, 0f, 0f), true, (kit.Rifle, 1), (kit.Vest, 1));
            var (unit, items) = Collector("a", Vector3.zero);
            yield return null;
            var rifleId = container.Contents.Entries.First(e => e.DefinitionId == "rifle").InstanceId;

            items.TryCollect(container, rifleId);
            Assert.That(container.IsEmpty, Is.False);
            Assert.That(container.Interactable.IsAvailable, Is.True, "something is left to take");

            items.TryCollect(container, null);
            Assert.That(container.IsEmpty, Is.True);
            Assert.That(container.Interactable.IsAvailable, Is.False, "an empty crate is not an interact target");
        }

        [UnityTest]
        public IEnumerator Sequence_Collect_ThenUse_ThenMove_RunsInOrder_AndRevalidatesEach()
        {
            var container = Container(new Vector3(3f, 0f, 0f), true, (kit.Medkit, 1));
            var (unit, items) = Collector("a", Vector3.zero);
            var health = unit.GetComponent<Health>();
            health.TakeDamage(70);
            yield return null;
            // The use order names an entry the unit does not own yet, so take first, then queue the use.
            Assert.That(unit.Issue(new CollectCommand(container)), Is.True);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);
            var owned = kit.Bag("a").Entries[0].InstanceId;

            Assert.That(unit.Issue(new UseItemCommand(owned)), Is.True);
            Assert.That(unit.Issue(new MoveCommand(new Vector3(-4f, 0f, 0f)), IssueMode.Append), Is.True);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);

            Assert.That(health.Current, Is.EqualTo(70));
            Assert.That(kit.Bag("a").CountOf("medkit"), Is.EqualTo(0));
            Assert.That(unit.transform.position.x, Is.LessThan(-3f));
            Assert.That(items.UsedCount, Is.EqualTo(1));
        }

        [Test]
        public void Command_NeedsAContainer()
        {
            Assert.Throws<System.ArgumentNullException>(() => new CollectCommand(null));
        }
    }
}
