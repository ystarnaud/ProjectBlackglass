using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class MissionObjectivesTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
            {
                if (host != null)
                    Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        Health Unit(Vector3 position)
        {
            var host = new GameObject("Unit");
            host.transform.position = position;
            hosts.Add(host);
            return host.AddComponent<Health>();
        }

        static void Kill(Health unit) => unit.TakeDamage(unit.Max);

        // ---- EliminateHostiles

        [Test]
        public void Eliminate_CompletesWhenEveryMemberOfItsGroupIsDead_AndNotBefore()
        {
            var a = Unit(Vector3.zero);
            var b = Unit(Vector3.zero);
            var objective = new EliminateHostilesObjective("kill", "Eliminate security team", new[] { a, b });
            objective.Activate();

            objective.Evaluate();
            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Active));
            Kill(a);
            objective.Evaluate();
            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Active));
            Assert.That(objective.Living, Is.EqualTo(1));
            Kill(b);
            objective.Evaluate();

            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Completed));
        }

        [Test]
        public void Eliminate_IgnoresHostilesOutsideItsGroup()
        {
            var inGroup = Unit(Vector3.zero);
            Unit(Vector3.zero);   // a bystander hostile, not in the group
            var objective = new EliminateHostilesObjective("kill", "Eliminate", new[] { inGroup });
            objective.Activate();

            Kill(inGroup);
            objective.Evaluate();

            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Completed));
        }

        [Test]
        public void Eliminate_CountsDestroyedMembersAsDead_AndAnEmptyGroupAsDone()
        {
            var a = Unit(Vector3.zero);
            var objective = new EliminateHostilesObjective("kill", "Eliminate", new[] { a });
            objective.Activate();
            Object.DestroyImmediate(a.gameObject);
            objective.Evaluate();
            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Completed));

            var empty = new EliminateHostilesObjective("none", "Nothing", Array.Empty<Health>());
            empty.Activate();
            empty.Evaluate();
            Assert.That(empty.State, Is.EqualTo(ObjectiveState.Completed));
        }

        [Test]
        public void Eliminate_DoesNothingWhileInactive_AndDescribesTheCount()
        {
            var a = Unit(Vector3.zero);
            var b = Unit(Vector3.zero);
            var objective = new EliminateHostilesObjective("kill", "Eliminate security team", new[] { a, b });
            Kill(a);
            Kill(b);

            objective.Evaluate();

            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Inactive));
            Assert.That(objective.Describe(), Is.EqualTo("Eliminate security team (0/2)"));
            Assert.That(objective.Type, Is.EqualTo(ObjectiveType.EliminateHostiles));
            Assert.That(objective.HasTarget, Is.False);
        }

        [Test]
        public void Eliminate_DescribeShowsOnlyTheTitleOnceCompleted()
        {
            var a = Unit(Vector3.zero);
            var objective = new EliminateHostilesObjective("kill", "Eliminate security team", new[] { a });
            objective.Activate();
            Kill(a);
            objective.Evaluate();

            Assert.That(objective.Describe(), Is.EqualTo("Eliminate security team"));
        }

        // ---- ReachZone

        [Test]
        public void Reach_CompletesWhenARequiredNumberOfLivingUnitsAreInsideTheRadius()
        {
            var inside = Unit(new Vector3(1f, 0f, 0f));
            var outside = Unit(new Vector3(5f, 0f, 0f));
            var objective = new ReachZoneObjective("extract", "Extraction", Vector3.zero, 2f, 1, new[] { inside, outside }, false);
            objective.Activate();

            objective.Evaluate();

            Assert.That(objective.Inside, Is.EqualTo(1));
            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Completed));
        }

        [Test]
        public void Reach_NeedsTheConfiguredCount_AndIgnoresTheDead()
        {
            var a = Unit(Vector3.zero);
            var b = Unit(new Vector3(0.5f, 0f, 0f));
            var objective = new ReachZoneObjective("extract", "Extraction", Vector3.zero, 2f, 2, new[] { a, b }, false);
            objective.Activate();
            Kill(b);

            objective.Evaluate();
            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Active), "a dead unit in the zone does not count");
        }

        [Test]
        public void Reach_UsesFlatDistance_SoHeightIsIgnored()
        {
            var tall = Unit(new Vector3(1f, 1f, 0f));
            var objective = new ReachZoneObjective("extract", "Extraction", Vector3.zero, 2f, 1, new[] { tall }, false);
            objective.Activate();

            objective.Evaluate();

            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Completed));
        }

        [Test]
        public void Reach_UnitsInsideWhileInactive_DoNotComplete()
        {
            var a = Unit(Vector3.zero);
            var objective = new ReachZoneObjective("extract", "Extraction", Vector3.zero, 2f, 1, new[] { a }, false);

            objective.Evaluate();

            Assert.That(objective.State, Is.EqualTo(ObjectiveState.Inactive));
        }

        [Test]
        public void Reach_ExposesItsTarget_AndItsType()
        {
            var center = new Vector3(3f, 0f, 4f);
            var objective = new ReachZoneObjective("extract", "Extraction", center, 2f, 1, Array.Empty<Health>(), false);

            Assert.That(objective.HasTarget, Is.True);
            Assert.That(objective.TargetPosition, Is.EqualTo(center));
            Assert.That(objective.Type, Is.EqualTo(ObjectiveType.ReachZone));
            Assert.That(objective.IsRequired, Is.False);
        }

        [Test]
        public void Reach_RejectsBadArguments()
        {
            Assert.Throws<ArgumentNullException>(() => new ReachZoneObjective("e", "E", Vector3.zero, 2f, 1, null, false));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ReachZoneObjective("e", "E", Vector3.zero, 0f, 1, Array.Empty<Health>(), false));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ReachZoneObjective("e", "E", Vector3.zero, 2f, 0, Array.Empty<Health>(), false));
            Assert.Throws<ArgumentNullException>(() => new EliminateHostilesObjective("k", "K", null));
        }
    }
}
