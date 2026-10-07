#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>Darius as slot 0 of a generated mission: he is the active character and behaves like any unit.</summary>
    public class DariusMissionPlayModeTests
    {
        MissionRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        IEnumerator Generate(int seed)
        {
            rig = new MissionRig();
            rig.SetFriendlySlots(MissionRig.FriendlySlotsWithDarius());
            yield return rig.Generate(seed);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
        }

        static Animator VisualAnimator(CommandableUnit unit) => unit.GetComponent<UnitAnimationDriver>().Animator;

        [UnityTest]
        public IEnumerator Slot0_IsDarius_TheActiveCharacter_OnTheNavMesh_AndSelectable()
        {
            yield return Generate(12345);
            var darius = rig.Director.Friendlies[0];

            Assert.That(darius.GetComponent<UnitAnimationDriver>(), Is.Not.Null);
            Assert.That(darius.GetComponent<UnitAttacker>().Archetype.Role, Is.EqualTo(CombatRole.Ranged), "a rifle carrier is a ranged unit");
            Assert.That(darius.transform.Find("Visual").GetComponent<Animator>().isHuman, Is.True);
            Assert.That(rig.Active.Unit, Is.SameAs(darius));
            Assert.That(darius.GetComponent<NavMeshAgent>().isOnNavMesh, Is.True);
            Assert.That(rig.Director.Friendlies[1].GetComponent<UnitAnimationDriver>(), Is.Null, "slots 1 and 2 stay capsules");
            Assert.That(rig.Director.Friendlies[2].GetComponent<UnitAnimationDriver>(), Is.Null);

            yield return null;
            Physics.SyncTransforms();
            var origin = darius.transform.position + new Vector3(0.6f, 6f, -0.6f);
            Assert.That(Physics.Raycast(origin, (darius.transform.position - origin).normalized, out var hit, 20f), Is.True);
            Assert.That(hit.collider.GetComponentInParent<SelectableUnit>(), Is.Not.Null.And.SameAs(darius.GetComponent<SelectableUnit>()),
                "a click on Darius selects him");
        }

        [UnityTest]
        public IEnumerator Darius_MovesToADestination()
        {
            yield return Generate(12345);
            var darius = rig.Director.Friendlies[0];
            var start = darius.transform.position - Vector3.up * darius.GetComponent<NavMeshAgent>().baseOffset;
            Vector3 goal = default;
            var found = false;
            foreach (var dir in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right })
            {
                if (!NavMesh.SamplePosition(start + dir * 3f, out var hit, 0.5f, NavMesh.AllAreas))
                    continue;
                var path = new NavMeshPath();
                if (NavMesh.CalculatePath(start, hit.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete)
                {
                    goal = hit.position;
                    found = true;
                    break;
                }
            }
            Assert.That(found, Is.True, "a reachable point 3 m away");

            Assert.That(darius.Issue(new MoveCommand(goal)), Is.True);
            yield return TestWorld.WaitUntil(() => darius.CurrentCommand == null, 10f);
            Assert.That(TestWorld.HorizontalDistance(darius.transform.position, goal), Is.LessThan(0.6f));
        }

        [UnityTest]
        public IEnumerator Darius_StandsWithHisFeetOnTheGround_At188Metres()
        {
            yield return Generate(12345);
            var darius = rig.Director.Friendlies[0];
            yield return new WaitForSeconds(0.3f);

            var bounds = DariusMeasure.PosedBodyBounds(darius.transform.Find("Visual").gameObject);
            var floor = darius.transform.position.y - 1f;
            Debug.Log($"DARIUS standing bounds min={bounds.min} max={bounds.max} height={bounds.size.y} floor={floor}");
            Assert.That(floor, Is.EqualTo(0f).Within(0.05f), "the NavMesh floor is y=0");
            Assert.That(bounds.min.y - floor, Is.EqualTo(0f).Within(0.15f), "feet on the ground");
            Assert.That(bounds.size.y, Is.EqualTo(1.88f).Within(0.03f), "Darius is 1.88 m tall");
        }

        [UnityTest]
        public IEnumerator Regeneration_LeavesNothingOfDarius_EvenAfterHeDied()
        {
            yield return Generate(12345);
            var darius = rig.Director.Friendlies[0];
            var oldVisual = VisualAnimator(darius).gameObject;
            darius.GetComponent<Health>().TakeDamage(1000);
            yield return new WaitForSeconds(0.3f);
            Assert.That(oldVisual != null, Is.True, "the corpse is still there");

            yield return rig.Generate(777);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready));
            yield return null;
            Assert.That(oldVisual == null, Is.True, "the old corpse went with the old mission");
            var animators = Object.FindObjectsByType<Animator>(FindObjectsSortMode.None);
            Assert.That(animators.Length, Is.EqualTo(1), "only the new Darius animates: " + string.Join(", ", animators.Select(a => a.name)));
            Assert.That(animators[0].transform.IsChildOf(rig.Director.Friendlies[0].transform), Is.True);
        }
    }
}
#endif
