#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>Each avatar this client was sent gets the character visual with no collider; the animation is driven from replicated state.</summary>
    public class NetPlayAvatarsPlayTests
    {
        const string VisualPath = "Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Visual.prefab";
        NetLoopbackRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
            rig.BeginNet(quiet: true);
        }

        [UnityTearDown]
        public IEnumerator TearDown() => rig.Stop();

        [Test]
        public void Speed_IsFlatMetresPerSecond_AndAWarpReadsAsStanding()
        {
            Assert.That(NetAvatarView.Speed(Vector3.zero, new Vector3(0.05f, 3f, 0f), 0.01f), Is.EqualTo(5f).Within(1e-4f));
            Assert.That(NetAvatarView.Speed(Vector3.zero, new Vector3(10f, 0f, 0f), 0.01f), Is.Zero);
            Assert.That(NetAvatarView.Speed(Vector3.zero, Vector3.one, 0f), Is.Zero);
        }

        [UnityTest]
        public IEnumerator Every_own_avatar_gets_the_character_visual_without_a_collider()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var avatars = rig.Client.gameObject.AddComponent<NetPlayAvatars>();
            avatars.Initialize(rig.Client, _ => false, NetPlayKit.Load<GameObject>(VisualPath),
                NetPlayKit.Load<WeaponVisualAsset>("Assets/_Project/Data/Items/Visuals/RifleVisual.asset"),
                NetPlayKit.Load<GameObject>("Assets/_Project/Presentation/Prefabs/TracerRifle.prefab"),
                NetPlayKit.Load<Material>("Assets/_Project/Materials/SelectionRing.mat"), NetPlayKit.Load<Material>("Assets/_Project/Materials/IntelMarker.mat"));
            yield return TestWorld.WaitUntil(() => rig.Client.OwnedCount > 0 && NetClient.VisibleStates().Count(s => avatars.ViewOf(s.UnitId) != null) == rig.Client.OwnedCount, 10f);
            foreach (var state in NetClient.VisibleStates())
            {
                var view = avatars.ViewOf(state.UnitId);
                Assert.That(view.GetComponentInChildren<Animator>(), Is.Not.Null, "#" + state.UnitId + " has the animated model");
                Assert.That(view.GetComponentsInChildren<Collider>(true), Is.Empty, "#" + state.UnitId + " has no collider");
                Assert.That(view.GetComponentInChildren<UnitWeaponVisual>().Current, Is.Not.Null, "#" + state.UnitId + " holds the rifle");
            }
            Assert.That(NetClient.VisibleStates().Any(s => s.Hostile), Is.False, "the quiet hostile is not observed from spawn");
        }

        NetPlayAvatars AddAvatars(System.Func<int, bool> selected)
        {
            var avatars = rig.Client.gameObject.AddComponent<NetPlayAvatars>();
            avatars.Initialize(rig.Client, selected, NetPlayKit.Load<GameObject>(VisualPath),
                NetPlayKit.Load<WeaponVisualAsset>("Assets/_Project/Data/Items/Visuals/RifleVisual.asset"),
                NetPlayKit.Load<GameObject>("Assets/_Project/Presentation/Prefabs/TracerRifle.prefab"),
                NetPlayKit.Load<Material>("Assets/_Project/Materials/SelectionRing.mat"), NetPlayKit.Load<Material>("Assets/_Project/Materials/IntelMarker.mat"));
            return avatars;
        }

        static void AssertColour(Color actual, Color expected) =>
            Assert.That(((Vector4)actual - (Vector4)expected).magnitude, Is.LessThan(1e-3f), actual + " is not " + expected);

        static Color RingColour(Renderer ring)
        {
            var block = new MaterialPropertyBlock();
            ring.GetPropertyBlock(block);
            return block.GetColor("_BaseColor");
        }

        // Owner ruling D5: friendly, hostile and selected states are visibly distinct.
        [UnityTest]
        public IEnumerator Own_hostile_and_selected_units_are_visibly_distinct()
        {
            Assert.That(NetAvatarView.OwnColour, Is.Not.EqualTo(NetAvatarView.HostileColour));
            Assert.That(NetAvatarView.SelectedColour, Is.Not.EqualTo(NetAvatarView.OwnColour));
            Assert.That(NetAvatarView.SelectedColour, Is.Not.EqualTo(NetAvatarView.HostileColour));
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var first = rig.Client.FirstOwnedId;
            var avatars = AddAvatars(id => id == first);
            yield return TestWorld.WaitUntil(() => rig.Client.OwnedCount > 1 && avatars.ViewOf(first) != null && avatars.ViewOf(first + 1) != null, 10f);
            yield return null;
            var selected = avatars.ViewOf(first);
            var other = avatars.ViewOf(first + 1);
            AssertColour(RingColour(selected.SideRing), NetAvatarView.OwnColour);
            Assert.That(selected.SelectionRing.enabled, Is.True, "the selected operative shows the selection ring");
            AssertColour(RingColour(selected.SelectionRing), NetAvatarView.SelectedColour);
            Assert.That(other.SelectionRing.enabled, Is.False, "an unselected operative shows none");

            // Bring the hostile into the squad's sight; its avatar gets the hostile ring.
            var hostile = rig.Director.Hostiles[0];
            var friendly = rig.Director.Friendlies[0];
            var agent = friendly.GetComponent<UnityEngine.AI.NavMeshAgent>();
            var hostileId = rig.Server.Units.IdOf(hostile);
            var seen = false;
            foreach (var radius in new[] { 2.5f, 4f, 6f })
            {
                for (var step = 0; step < 8 && !seen; step++)
                {
                    var angle = step * Mathf.PI / 4f;
                    var candidate = hostile.transform.position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                    if (!UnityEngine.AI.NavMesh.SamplePosition(candidate, out var hit, 1.5f, UnityEngine.AI.NavMesh.AllAreas) || !agent.Warp(hit.position))
                        continue;
                    var until = Time.realtimeSinceStartup + 1.5f;
                    while (!seen && Time.realtimeSinceStartup < until)
                    {
                        yield return null;
                        seen = avatars.ViewOf(hostileId) != null;
                    }
                }
            }
            Assert.That(seen, Is.True, "the hostile came into sight");
            yield return null;
            AssertColour(RingColour(avatars.ViewOf(hostileId).SideRing), NetAvatarView.HostileColour);
        }

        // Checkpoint 1 review M11: the client does not trust shot ids; a shot naming an avatar it does not hold draws nothing.
        [UnityTest]
        public IEnumerator A_shot_naming_an_avatar_this_client_does_not_hold_draws_nothing()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var first = rig.Client.FirstOwnedId;
            var avatars = AddAvatars(_ => false);
            yield return TestWorld.WaitUntil(() => avatars.ViewOf(first) != null, 10f);
            var floaters = 0;
            avatars.Floater += (id, text) => floaters++;
            var tracersBefore = Object.FindObjectsByType<TracerView>(FindObjectsSortMode.None).Length;
            avatars.OnShot(new NetShot(99, first, false));
            avatars.OnShot(new NetShot(first, 99, false));
            Assert.That(floaters, Is.Zero);
            Assert.That(Object.FindObjectsByType<TracerView>(FindObjectsSortMode.None).Length, Is.EqualTo(tracersBefore));
        }
    }
}
#endif
