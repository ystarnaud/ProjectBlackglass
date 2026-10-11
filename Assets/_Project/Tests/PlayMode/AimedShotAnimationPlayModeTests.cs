#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class AimedShotAnimationPlayModeTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator AimedShot_PlaysTheFireClip_AndTheGameplayResultIsTheSame()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(MissionRig.DariusPlayerPath);
            var unit = world.Track(Object.Instantiate(prefab, new Vector3(0f, 1f, -6f), Quaternion.identity));
            var encounter = world.CreateEncounter();
            var target = world.CreateDummy(new Vector3(0f, 0f, 2f));
            var health = unit.GetComponent<Health>();
            encounter.Initialize(new[] { health }, new[] { target });
            var aimed = world.CreateAimedShot();
            var abilities = unit.AddComponent<UnitAbilities>();
            abilities.Initialize(encounter, aimed);
            var animator = unit.GetComponent<UnitAnimationDriver>().Animator;
            yield return null;

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, target)), Is.True);
            yield return TestWorld.WaitUntil(() => DariusMeasure.InState(animator, "Fire"), 3f);
            Assert.That(DariusMeasure.InState(animator, "Fire"), Is.True);
            Assert.That(target.Current, Is.EqualTo(target.Max - 45), "the ability's damage is unchanged");
        }
    }
}
#endif
