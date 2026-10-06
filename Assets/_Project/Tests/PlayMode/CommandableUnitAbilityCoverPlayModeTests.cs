using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CommandableUnitAbilityCoverPlayModeTests
    {
        TestWorld world;
        CoverLocation point;
        CoverRegistry registry;
        Encounter encounter;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            registry = world.CreateRegistry(point);
            encounter = world.CreateEncounter();
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator MoveToCover_Ability_Move_Attack_RunInOrder_AndTheAbilityFiresFromCover()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, -8f), registry: registry);
            var abilities = world.AddAbilities(unit, encounter, world.CreateAimedShot());
            var aimed = abilities.Definition(0);
            var target = world.CreateDummy(new Vector3(0f, 0f, 5f));
            var brawlTarget = world.CreateDummy(new Vector3(-8f, 0f, -6f));
            encounter.Initialize(new[] { unit.GetComponent<Health>() }, new[] { target, brawlTarget });
            yield return null;

            var statusWhenUsed = CoverStatus.None;
            var positionWhenUsed = Vector3.zero;
            abilities.Used += _ =>
            {
                statusWhenUsed = unit.Cover.Status;
                positionWhenUsed = unit.transform.position;
            };
            var destination = new Vector3(-6f, 0f, -6f);

            Assert.That(unit.Issue(new MoveToCoverCommand(point)), Is.True);
            Assert.That(unit.Issue(AbilityCommand.OnUnit(aimed, target), IssueMode.Append), Is.True);
            Assert.That(unit.Issue(new MoveCommand(destination), IssueMode.Append), Is.True);
            Assert.That(unit.Issue(new AttackCommand(brawlTarget.GetComponent<Health>()), IssueMode.Append), Is.True);
            yield return TestWorld.WaitUntil(() => brawlTarget.Current < brawlTarget.Max, 25f);

            Assert.That(statusWhenUsed, Is.EqualTo(CoverStatus.Occupied), "The ability was used from the cover point");
            Assert.That(TestWorld.HorizontalDistance(positionWhenUsed, point.Position), Is.LessThan(1f));
            Assert.That(target.Current, Is.EqualTo(target.Max - 45));
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, destination), Is.LessThan(2.5f), "The move and the attack ran after it");
            Assert.That(brawlTarget.Current, Is.LessThan(brawlTarget.Max));
        }
    }
}
