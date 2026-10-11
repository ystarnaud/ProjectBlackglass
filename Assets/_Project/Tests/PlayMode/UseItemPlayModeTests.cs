using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UseItemPlayModeTests
    {
        const string Op = "op-1";
        TestWorld world;
        InventoryKit kit;
        TacticalPause pause;
        CommandableUnit unit;
        UnitItems items;
        Health health;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            kit = new InventoryKit(world, 8, Op);
            kit.BeginMission();
            unit = world.CreateFighter(Vector3.zero);
            items = kit.Equip(unit, Op);
            health = unit.GetComponent<Health>();
            kit.Bag(Op).Add(kit.Medkit, 2);
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        string MedkitId => kit.Bag(Op).Entries[0].InstanceId;
        int Medkits => kit.Bag(Op).CountOf("medkit");

        [UnityTest]
        public IEnumerator Use_Heals_AndConsumesExactlyOne()
        {
            health.TakeDamage(60);
            yield return null;

            Assert.That(unit.Issue(new UseItemCommand(MedkitId)), Is.True);
            yield return null;

            Assert.That(health.Current, Is.EqualTo(80));
            Assert.That(Medkits, Is.EqualTo(1));
            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(items.UsedCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Use_AtFullHealth_IsRejected_AndNothingIsConsumed()
        {
            yield return null;

            var accepted = unit.Issue(new UseItemCommand(MedkitId));

            Assert.That(accepted, Is.False);
            Assert.That(items.LastUseFailure, Is.EqualTo(ItemUseFailure.NoEffect));
            Assert.That(Medkits, Is.EqualTo(2));
            Assert.That(health.Current, Is.EqualTo(health.Max));
        }

        [UnityTest]
        public IEnumerator Checking_NeverConsumes()
        {
            health.TakeDamage(60);
            yield return null;

            for (var i = 0; i < 5; i++)
                Assert.That(items.Check(MedkitId), Is.EqualTo(ItemUseFailure.None));

            Assert.That(Medkits, Is.EqualTo(2));
            Assert.That(health.Current, Is.EqualTo(40));
        }

        [UnityTest]
        public IEnumerator Paused_TheOrderWaits_AndRunsOnResume()
        {
            health.TakeDamage(60);
            yield return null;
            pause.Pause();

            Assert.That(unit.Issue(new UseItemCommand(MedkitId)), Is.True);
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(health.Current, Is.EqualTo(40), "nothing heals while paused");
            Assert.That(Medkits, Is.EqualTo(2));

            pause.Resume();
            yield return null;
            yield return null;
            Assert.That(health.Current, Is.EqualTo(80));
            Assert.That(Medkits, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Paused_ThenStop_ConsumesNothing()
        {
            health.TakeDamage(60);
            yield return null;
            pause.Pause();
            unit.Issue(new UseItemCommand(MedkitId));

            unit.Issue(new StopCommand());
            pause.Resume();
            yield return null;
            yield return null;

            Assert.That(Medkits, Is.EqualTo(2));
            Assert.That(health.Current, Is.EqualTo(40));
        }

        [UnityTest]
        public IEnumerator TwoQueuedUses_WithOneMedkit_FirstSucceeds_SecondFailsCleanly()
        {
            kit.Bag(Op).Remove(MedkitId, 1);   // one medkit left
            health.TakeDamage(90);
            yield return null;
            Assert.That(unit.Issue(new MoveCommand(new Vector3(4f, 0f, 0f))), Is.True);
            var id = MedkitId;

            Assert.That(unit.Issue(new UseItemCommand(id), IssueMode.Append), Is.True, "both are accepted: the item is owned now");
            Assert.That(unit.Issue(new UseItemCommand(id), IssueMode.Append), Is.True);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);

            Assert.That(health.Current, Is.EqualTo(50), "one use healed 40");
            Assert.That(Medkits, Is.EqualTo(0));
            Assert.That(items.LastUseFailure, Is.EqualTo(ItemUseFailure.NotOwned));
            Assert.That(unit.PendingCommands, Is.Empty, "the queue moved on, nothing is stuck");
        }

        [UnityTest]
        public IEnumerator LosingTheItemBeforeItsTurn_FailsCleanly_WithoutConsumingAnything()
        {
            health.TakeDamage(60);
            yield return null;
            unit.Issue(new MoveCommand(new Vector3(4f, 0f, 0f)));
            var id = MedkitId;
            Assert.That(unit.Issue(new UseItemCommand(id), IssueMode.Append), Is.True);

            kit.Bag(Op).Remove(id, 2);   // another route took them
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);

            Assert.That(items.LastUseFailure, Is.EqualTo(ItemUseFailure.NotOwned));
            Assert.That(health.Current, Is.EqualTo(40));
            Assert.That(items.UsedCount, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator HealthChangingBeforeItsTurn_IsJudgedAtExecution()
        {
            health.TakeDamage(30);
            yield return null;
            unit.Issue(new MoveCommand(new Vector3(4f, 0f, 0f)));
            Assert.That(unit.Issue(new UseItemCommand(MedkitId), IssueMode.Append), Is.True);

            health.Heal(100);   // healed some other way while walking
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);

            Assert.That(Medkits, Is.EqualTo(2), "full health at execution: not consumed");
            Assert.That(items.LastUseFailure, Is.EqualTo(ItemUseFailure.NoEffect));
        }

        [UnityTest]
        public IEnumerator AHeldMoveKey_DoesNotDropAUsableItemOrder()
        {
            health.TakeDamage(60);
            yield return null;
            unit.SetMoveIntent(Vector3.forward);

            Assert.That(unit.Issue(new UseItemCommand(MedkitId)), Is.True);
            yield return null;
            yield return null;
            unit.SetMoveIntent(Vector3.zero);

            Assert.That(Medkits, Is.EqualTo(1));
            Assert.That(health.Current, Is.EqualTo(80));
        }

        [UnityTest]
        public IEnumerator ADeadUnit_CannotUse()
        {
            yield return null;
            health.TakeDamage(1000);
            var id = MedkitId;

            Assert.That(items.Check(id), Is.EqualTo(ItemUseFailure.Dead));
            Assert.That(unit.Issue(new UseItemCommand(id)), Is.False);
            Assert.That(Medkits, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator NonConsumables_AndUnknownEntries_AreNotUsable()
        {
            kit.Bag(Op).Add(kit.Rifle, 1);
            health.TakeDamage(60);
            yield return null;
            var rifleId = kit.Bag(Op).Entries[1].InstanceId;

            Assert.That(items.Check(rifleId), Is.EqualTo(ItemUseFailure.NotUsable));
            Assert.That(items.Check("nope"), Is.EqualTo(ItemUseFailure.NotOwned));
            Assert.That(unit.Issue(new UseItemCommand(rifleId)), Is.False);
        }

        [UnityTest]
        public IEnumerator UsingAfterTheMissionEnded_FailsWithNoLoadout()
        {
            health.TakeDamage(60);
            yield return null;
            var id = MedkitId;
            kit.Inventory.Core.AbortMission();

            Assert.That(items.Check(id), Is.EqualTo(ItemUseFailure.NoLoadout));
            Assert.That(unit.Issue(new UseItemCommand(id)), Is.False);
        }

        [Test]
        public void Command_NeedsAnId()
        {
            Assert.Throws<System.ArgumentException>(() => new UseItemCommand(" "));
        }
    }
}
