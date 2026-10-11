#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// Decision 037: an unobserved hostile is fully hidden. The rifle its UnitWeaponVisual places in Awake (after the spawner
    /// bound the HostilePresenter) is hidden and shown with the rest of the unit.
    /// </summary>
    public class HostileWeaponFogPlayModeTests
    {
        const string HostilePrefab = "Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Hostile.prefab";
        const string MarksmanVisual = "Assets/_Project/Data/Items/Visuals/MarksmanRifleVisual.asset";
        MissionRig rig;

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
            rig = null;
        }

        [UnityTest]
        public IEnumerator AnUnobservedHostile_DrawsNoRifle_AndItsRifleIsDrawnOnceObserved()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Blind());
            var slots = MissionRig.HostileSlots();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HostilePrefab);
            foreach (var slot in slots)
                slot.prefab = prefab;   // the rifle-carrying hostiles of the ProceduralMission scene
            rig.SetHostileSlots(slots);

            yield return rig.Generate(12345);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
            yield return null;   // one presenter pass

            var presenter = rig.Director.Hostiles.Select(h => h.GetComponent<HostilePresenter>()).FirstOrDefault(p => p != null && p.IsHidden);
            Assert.That(presenter != null, Is.True, "Precondition: a blind mission starts with an unobserved hostile");
            var weapon = presenter.GetComponent<UnitWeaponVisual>();
            Assert.That(weapon != null && weapon.Current != null, Is.True, "Precondition: the hostile carries its default rifle");
            var rifle = weapon.Current.GetComponentsInChildren<Renderer>(true);
            Assert.That(rifle, Is.Not.Empty);
            Assert.That(rifle.All(r => r.forceRenderingOff), Is.True, "the rifle of an unobserved hostile is not drawn");
            Assert.That(presenter.GetComponentsInChildren<Renderer>(true).All(r => r.forceRenderingOff), Is.True, "nothing under the hostile is drawn");

            // A model placed while the hostile is hidden is hidden at once, not only at the next change of state.
            weapon.Show(AssetDatabase.LoadAssetAtPath<WeaponVisualAsset>(MarksmanVisual));
            var replaced = weapon.Current.GetComponentsInChildren<Renderer>(true);
            Assert.That(replaced, Is.Not.Empty);
            Assert.That(replaced.All(r => r.forceRenderingOff), Is.True, "a weapon shown on a hidden hostile is hidden too");

            service.Scan(Vector3.zero, 200f, 100f);   // learn the whole map and every enemy
            yield return null;
            Assert.That(presenter.IsHidden, Is.False);
            Assert.That(replaced.All(r => !r.forceRenderingOff), Is.True, "observed: its rifle is drawn again");
            Assert.That(presenter.GetComponentsInChildren<Renderer>(true).All(r => !r.forceRenderingOff), Is.True);
        }
    }
}
#endif
