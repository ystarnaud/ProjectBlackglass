#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UnitAnimationDriverPlayModeTests
    {
        static readonly int SpeedId = Animator.StringToHash("Speed");
        static readonly int CrouchedId = Animator.StringToHash("Crouched");

        TestWorld world;
        GameObject environment;
        GameObject actors;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 6f), new Vector3(4f, 0.9f, 0.5f)));
            actors = world.Track(new GameObject("Actors"));
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        GameObject SpawnDarius(Vector3 ground)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(MissionRig.DariusPlayerPath);
            var unit = Object.Instantiate(prefab, ground + Vector3.up, Quaternion.identity, actors.transform);
            Assert.That(unit.GetComponent<NavMeshAgent>().isOnNavMesh, Is.True);
            return unit;
        }

        static Animator AnimatorOf(GameObject unit) => unit.GetComponent<UnitAnimationDriver>().Animator;

        static IEnumerator WaitState(Animator animator, string state, float timeout = 3f) =>
            TestWorld.WaitUntil(() => DariusMeasure.InState(animator, state), timeout);

        [UnityTest]
        public IEnumerator StandingStill_HasNoSpeed_AndIsInTheStandingState()
        {
            var unit = SpawnDarius(new Vector3(-8f, 0f, -8f));
            yield return new WaitForSeconds(0.4f);
            var animator = AnimatorOf(unit);
            Assert.That(animator.GetFloat(SpeedId), Is.LessThan(0.05f));
            Assert.That(animator.GetBool(CrouchedId), Is.False);
            Assert.That(DariusMeasure.InState(animator, "Standing"), Is.True);
        }

        [UnityTest]
        public IEnumerator Moving_RaisesSpeedAboveTheWalkThreshold()
        {
            var unit = SpawnDarius(new Vector3(-8f, 0f, -8f));
            var animator = AnimatorOf(unit);
            Assert.That(unit.GetComponent<CommandableUnit>().Issue(new MoveCommand(new Vector3(8f, 0f, -8f))), Is.True);
            yield return TestWorld.WaitUntil(() => animator.GetFloat(SpeedId) > 1f, 3f);
            Assert.That(animator.GetFloat(SpeedId), Is.GreaterThan(1f), "faster than the walk clip's own speed");
            Assert.That(unit.transform.position.x, Is.GreaterThan(-8f));
        }

        [UnityTest]
        public IEnumerator Crouched_FollowsLowCover_NotTallCover()
        {
            var obstacle = TestWorld.ObstacleCollider(environment);
            var low = world.CreateCoverPoint(new Vector3(0f, 0f, 5f), Vector3.forward, obstacle, height: CoverHeight.Low);
            var tall = world.CreateCoverPoint(new Vector3(6f, 0f, 5f), Vector3.forward, obstacle, height: CoverHeight.Tall);
            var unit = SpawnDarius(low.Position);
            var cover = unit.GetComponent<UnitCover>();
            var animator = AnimatorOf(unit);

            Assert.That(cover.TryReserve(low), Is.True);
            Assert.That(cover.TryOccupy(), Is.True);
            yield return TestWorld.WaitUntil(() => animator.GetBool(CrouchedId), 2f);
            Assert.That(animator.GetBool(CrouchedId), Is.True, "occupying low cover");
            yield return WaitState(animator, "Crouching");
            Assert.That(DariusMeasure.InState(animator, "Crouching"), Is.True);

            cover.Release();
            unit.transform.position = tall.Position + Vector3.up;
            unit.GetComponent<NavMeshAgent>().Warp(tall.Position);
            Assert.That(cover.TryReserve(tall), Is.True);
            Assert.That(cover.TryOccupy(), Is.True);
            yield return TestWorld.WaitUntil(() => !animator.GetBool(CrouchedId), 2f);
            Assert.That(animator.GetBool(CrouchedId), Is.False, "tall cover is stood behind");
        }

        [UnityTest]
        public IEnumerator Shooting_PlaysTheFireState()
        {
            var unit = SpawnDarius(new Vector3(-8f, 0f, -8f));
            var target = world.CreateDummy(new Vector3(-8f, 0f, -7f));
            var animator = AnimatorOf(unit);
            yield return null;

            Assert.That(unit.GetComponent<UnitAttacker>().TryAttack(target), Is.True);
            yield return WaitState(animator, "Fire");
            Assert.That(DariusMeasure.InState(animator, "Fire"), Is.True);
        }

        [UnityTest]
        public IEnumerator Damage_PlaysTheHitState_WhileAlive()
        {
            var unit = SpawnDarius(new Vector3(-8f, 0f, -8f));
            var animator = AnimatorOf(unit);
            yield return null;

            unit.GetComponent<Health>().TakeDamage(5);
            yield return WaitState(animator, "Hit");
            Assert.That(DariusMeasure.InState(animator, "Hit"), Is.True);
            Assert.That(unit.GetComponent<Health>().IsAlive, Is.True);
        }

        [UnityTest]
        public IEnumerator Reload_PlaysTheReloadState()
        {
            var unit = SpawnDarius(new Vector3(-8f, 0f, -8f));
            var animator = AnimatorOf(unit);
            yield return null;

            unit.GetComponent<UnitAnimationDriver>().PlayReload();
            yield return WaitState(animator, "Reload");
            Assert.That(DariusMeasure.InState(animator, "Reload"), Is.True);
        }

        [UnityTest]
        public IEnumerator Death_ReleasesTheVisualToTheParent_AndPlaysTheDeathAnimation_UntilTheParentGoes()
        {
            var unit = SpawnDarius(new Vector3(-8f, 0f, -8f));
            var driver = unit.GetComponent<UnitAnimationDriver>();
            var visual = driver.Animator.gameObject;
            var animator = driver.Animator;
            yield return null;

            unit.GetComponent<Health>().TakeDamage(1000);
            Assert.That(unit.activeSelf, Is.False, "Health deactivated the unit");
            Assert.That(visual.transform.parent, Is.SameAs(actors.transform), "the visual left the unit");
            Assert.That(visual.activeInHierarchy, Is.True);
            yield return WaitState(animator, "Death");
            Assert.That(DariusMeasure.InState(animator, "Death"), Is.True);

            // Lying down, not hovering: after the clip the body is within reach of the floor.
            yield return new WaitForSeconds(3.2f);
            var bounds = DariusMeasure.PosedBodyBounds(visual);
            var floor = unit.transform.position.y - 1f;
            Debug.Log($"DARIUS death pose bounds min={bounds.min} max={bounds.max} floor={floor}");
            Assert.That(bounds.min.y - floor, Is.InRange(-0.2f, 0.2f), "the corpse lies on the ground (the death pose sinks about 0.15 m at the 1.88 m scale, 0.146 m at the model size)");
            Assert.That(bounds.size.y, Is.LessThan(1.0f), "and is lying, not standing");

            Object.Destroy(actors);
            yield return null;
            Assert.That(visual == null, Is.True, "nothing of the corpse is left after the mission root goes");
        }

        [UnityTest]
        public IEnumerator DeathInCover_PlaysTheCrouchDeathAnimation()
        {
            var obstacle = TestWorld.ObstacleCollider(environment);
            var low = world.CreateCoverPoint(new Vector3(0f, 0f, 5f), Vector3.forward, obstacle, height: CoverHeight.Low);
            var unit = SpawnDarius(low.Position);
            var cover = unit.GetComponent<UnitCover>();
            var animator = AnimatorOf(unit);
            cover.TryReserve(low);
            cover.TryOccupy();
            yield return WaitState(animator, "Crouching");

            unit.GetComponent<Health>().TakeDamage(1000);
            yield return WaitState(animator, "CrouchDeath");
            Assert.That(DariusMeasure.InState(animator, "CrouchDeath"), Is.True);
        }

        [UnityTest]
        public IEnumerator Pause_FreezesTheSpeedUpdate()
        {
            var unit = SpawnDarius(new Vector3(-8f, 0f, -8f));
            var animator = AnimatorOf(unit);
            unit.GetComponent<CommandableUnit>().Issue(new MoveCommand(new Vector3(8f, 0f, -8f)));
            yield return TestWorld.WaitUntil(() => animator.GetFloat(SpeedId) > 0.5f, 3f);

            Time.timeScale = 0f;
            yield return null;
            var frozen = animator.GetFloat(SpeedId);
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(animator.GetFloat(SpeedId), Is.EqualTo(frozen), "no parameter changes while paused");
            Assert.That(animator.GetBool(CrouchedId), Is.False);
        }
    }
}
#endif
