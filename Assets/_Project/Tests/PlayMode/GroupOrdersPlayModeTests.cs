using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class GroupOrdersPlayModeTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown() => world.Dispose();

        [UnityTest]
        public IEnumerator Move_ThreeUnitsArriveAtDistinctPointsAroundTheDestination_OthersGetNothing()
        {
            world.CreateEnvironment();
            var units = new[]
            {
                world.CreateUnit(new Vector3(-6f, 0f, -6f)),
                world.CreateUnit(new Vector3(-4f, 0f, -6f)),
                world.CreateUnit(new Vector3(-2f, 0f, -6f)),
            };
            var outsider = world.CreateUnit(new Vector3(6f, 0f, -6f));
            yield return null;
            var destination = new Vector3(4f, 0f, 4f);

            Assert.That(GroupOrders.Issue(units, new MoveCommand(destination), IssueMode.Replace), Is.EqualTo(3));
            Assert.That(outsider.CurrentCommand, Is.Null);

            yield return TestWorld.WaitUntil(() => units.All(u => u.CurrentCommand == null), 15f);

            Assert.That(units.All(u => u.CurrentCommand == null), Is.True, "Group move did not finish in time");
            for (var i = 0; i < units.Length; i++)
            {
                Assert.That(TestWorld.HorizontalDistance(units[i].transform.position, destination), Is.LessThan(2f), $"unit {i}");
                for (var j = i + 1; j < units.Length; j++)
                    Assert.That(TestWorld.HorizontalDistance(units[i].transform.position, units[j].transform.position), Is.GreaterThan(1f), $"units {i} and {j} overlap");
            }
        }

        [UnityTest]
        public IEnumerator OffsetPointOffTheNavMesh_FallsBackToTheClickedPoint()
        {
            world.CreateEnvironment();   // ground spans -20..20; the NavMesh edge is about 0.5 m inside it
            var first = world.CreateUnit(new Vector3(14f, 0f, 0f));
            var second = world.CreateUnit(new Vector3(14f, 0f, 3f));
            yield return null;
            var destination = new Vector3(19f, 0f, 0f);

            // With 3 m spacing the second slot is (22, 0, 0): more than 2 m from any walkable point.
            LogAssert.Expect(LogType.Warning, new Regex("no walkable NavMesh point"));
            Assert.That(GroupOrders.Issue(new[] { first, second }, new MoveCommand(destination), IssueMode.Replace, 3f), Is.EqualTo(2));

            Assert.That(second.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(((MoveCommand)second.CurrentCommand).Destination, Is.EqualTo(destination));
        }
    }
}
