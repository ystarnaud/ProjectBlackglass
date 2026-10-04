using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class DeathMarkerTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        DeathMarker AddMarker(CommandableUnit unit)
        {
            var marker = unit.gameObject.AddComponent<DeathMarker>();
            marker.Initialize(null);
            return marker;
        }

        [UnityTest]
        public IEnumerator Death_LeavesAFlatMarkerWhereTheUnitFell_AndTheUnitIsGone()
        {
            world.CreateEnvironment();
            var unit = world.CreateFighter(new Vector3(3f, 0f, -4f));
            unit.name = "Victim";
            var marker = AddMarker(unit);
            var health = unit.GetComponent<Health>();
            yield return null;
            Assert.That(marker.LastMarker, Is.Null);

            health.TakeDamage(health.Max);
            yield return null;

            var corpse = marker.LastMarker;
            Assert.That(corpse, Is.Not.Null, "No marker was spawned");
            world.Track(corpse);
            Assert.That(corpse.name, Is.EqualTo("Victim (dead)"));
            Assert.That(corpse.transform.parent, Is.Null, "The marker must be a root object so it outlives the unit");
            Assert.That(corpse.activeInHierarchy, Is.True);
            Assert.That(TestWorld.HorizontalDistance(corpse.transform.position, unit.transform.position), Is.LessThan(0.01f));
            Assert.That(corpse.transform.position.y, Is.EqualTo(0.03f).Within(0.001f));
            Assert.That(corpse.transform.localScale.y, Is.LessThan(0.05f), "The marker must be flat");
            Assert.That(corpse.GetComponentsInChildren<Collider>(true), Is.Empty, "The marker must never block clicks");
            Assert.That(unit.gameObject.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator TwoFighters_FightUntilOneDies_TheLoserLeavesAMarker()
        {
            world.CreateEnvironment();
            var strong = world.CreateFighter(new Vector3(0f, 0f, -6f), maxHealth: 100, damage: 25, cooldown: 0.3f);
            var weak = world.CreateFighter(new Vector3(0f, 0f, 6f), maxHealth: 50, damage: 5, cooldown: 0.3f);
            var strongMarker = AddMarker(strong);
            var weakMarker = AddMarker(weak);
            var weakHealth = weak.GetComponent<Health>();
            yield return null;

            strong.Issue(new AttackCommand(weakHealth));
            yield return TestWorld.WaitUntil(() => !weakHealth.IsAlive, 15f);
            yield return null;

            Assert.That(weakHealth.IsAlive, Is.False, "The fight did not end in time");
            Assert.That(weak.gameObject.activeSelf, Is.False);
            Assert.That(weakMarker.LastMarker, Is.Not.Null);
            world.Track(weakMarker.LastMarker);
            Assert.That(strongMarker.LastMarker, Is.Null, "Only the loser leaves a marker");
            Assert.That(strong.GetComponent<Health>().Current, Is.LessThan(100), "The weak unit should have fought back");
            Assert.That(strong.CurrentCommand, Is.Null, "The winner's attack order must finish");
        }

        [UnityTest]
        public IEnumerator Marker_UsesTheGivenMaterial()
        {
            world.CreateEnvironment();
            var unit = world.CreateFighter(Vector3.zero);
            var marker = unit.gameObject.AddComponent<DeathMarker>();
            var material = world.Track(new Material(Shader.Find("Universal Render Pipeline/Unlit")));
            marker.Initialize(material);
            var health = unit.GetComponent<Health>();
            yield return null;

            health.TakeDamage(health.Max);
            yield return null;

            world.Track(marker.LastMarker);
            Assert.That(marker.LastMarker.GetComponent<Renderer>().sharedMaterial, Is.SameAs(material));
        }
    }
}
