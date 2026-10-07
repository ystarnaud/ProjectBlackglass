#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Utils;

namespace Blackglass.Tests
{
    public class ObjectiveMarkerPlayModeTests
    {
        // The block hands the colour back through a float round trip: compare with a tolerance, not bit for bit.
        static readonly ColorEqualityComparer Tolerance = new ColorEqualityComparer(0.01f);

        MissionRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        static Color ColourOf(Renderer renderer)
        {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            return block.GetColor("_BaseColor");
        }

        [UnityTest]
        public IEnumerator TheExtractionZone_IsVisibleButNotSolid_AndChangesColourWhenItOpens()
        {
            rig = new MissionRig(new MissionSettings { interactionSeconds = 0f });
            yield return rig.Generate(12345);
            var zone = rig.Director.Current.ExtractionZone;
            var renderers = zone.GetComponentsInChildren<Renderer>();

            Assert.That(renderers, Is.Not.Empty, "a disc and a beacon");
            Assert.That(zone.GetComponentsInChildren<Collider>(), Is.Empty, "clicks and rays pass through the zone");
            Assert.That(Physics.Raycast(zone.position + Vector3.up * 5f, Vector3.down, out var hit, 10f), Is.True);
            Assert.That(hit.collider.transform.IsChildOf(zone), Is.False, "the floor is under the zone, not the marker");
            yield return null;
            var locked = ColourOf(renderers[0]);
            Assert.That(locked, Is.EqualTo(ObjectiveMarker.ZoneColour(ObjectiveState.Inactive)).Using(Tolerance));

            foreach (var hostile in rig.Director.Hostiles)
            {
                hostile.GetComponent<EnemyAI>().enabled = false;
                hostile.GetComponent<Health>().TakeDamage(1000);
            }
            var unit = rig.Director.Friendlies[0];
            var terminal = rig.Director.Current.Terminal;
            unit.GetComponent<UnitMover>().TrySnap(terminal.Position, out var stand);
            unit.GetComponent<UnityEngine.AI.NavMeshAgent>().Warp(stand);
            unit.Issue(new InteractCommand(terminal));
            yield return TestWorld.WaitUntil(() => rig.Director.Phase == MissionPhase.ExtractionOpen, 10f);
            yield return null;

            Assert.That(rig.Director.Phase, Is.EqualTo(MissionPhase.ExtractionOpen));
            Assert.That(ColourOf(renderers[0]), Is.EqualTo(ObjectiveMarker.ZoneColour(ObjectiveState.Active)).Using(Tolerance));
        }

        [Test]
        public void Apply_AlsoTintsTheEmissionColour_SoAnEmissiveDisplayShowsTheState()
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                var renderer = cube.GetComponent<Renderer>();
                var tint = new Color(0.2f, 0.75f, 0.95f);
                cube.AddComponent<ObjectiveMarker>().Bind(() => tint, new[] { renderer });

                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                Assert.That(block.GetColor("_EmissionColor"), Is.EqualTo(tint * 1.5f).Using(Tolerance));
            }
            finally
            {
                Object.DestroyImmediate(cube);
            }
        }

        [UnityTest]
        public IEnumerator TheTerminal_ShowsItsState_AndLeavesNoMaterialInstancesBehind()
        {
            rig = new MissionRig(new MissionSettings { interactionSeconds = 5f });
            yield return rig.Generate(12345);
            var terminal = rig.Director.Current.Terminal;
            var renderer = terminal.GetComponentInChildren<Renderer>();
            yield return null;

            Assert.That(ColourOf(renderer), Is.EqualTo(ObjectiveMarker.TerminalColour(terminal)).Using(Tolerance));
            var available = ColourOf(renderer);

            var unit = rig.Director.Friendlies[0];
            foreach (var hostile in rig.Director.Hostiles)
                hostile.GetComponent<EnemyAI>().enabled = false;
            unit.GetComponent<UnitMover>().TrySnap(terminal.Position, out var stand);
            unit.GetComponent<UnityEngine.AI.NavMeshAgent>().Warp(stand);
            unit.Issue(new InteractCommand(terminal));
            yield return TestWorld.WaitUntil(() => terminal.Progress > 0.1f, 5f);
            yield return null;

            Assert.That(ColourOf(renderer), Is.Not.EqualTo(available).Using(Tolerance), "working looks different from available");
            Assert.That(renderer.sharedMaterial.name, Does.Not.Contain("(Instance)"), "a property block, not a material instance, tints it");
        }
    }
}
#endif
