#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The Character Test Scene bench shows Darius holding the shared rifle in his WeaponSocket (Phase 12 migration).</summary>
    public class CharacterTestScenePlayModeTests
    {
        const string ScenePath = "Assets/_Project/Scenes/Character Test Scene.unity";

        [UnityTest]
        public IEnumerator Darius_HoldsExactlyOneRifle_InHisWeaponSocket()
        {
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            var scene = SceneManager.GetSceneByPath(ScenePath);
            try
            {
                Assert.That(scene.isLoaded, Is.True);
                yield return null;
                var darius = scene.GetRootGameObjects().Single(g => g.name == "Darius_Visual");
                var socket = darius.GetComponentsInChildren<Transform>(true).Single(t => t.name == UnitWeaponVisual.SocketName);
                var held = socket.Cast<Transform>().ToArray();
                Assert.That(held.Length, Is.EqualTo(1), "exactly one model in the hand");
                Assert.That(held[0].name, Is.EqualTo(UnitWeaponVisual.ManagedName));
                Assert.That(held[0].Find("RifleModel"), Is.Not.Null, "the shared rifle");
                Assert.That(held[0].Find("Muzzle"), Is.Not.Null);
                // The standard rifle's pose under the socket, as UnitWeaponVisual places RifleVisual.
                Assert.That(held[0].localPosition, Is.EqualTo(Vector3.zero));
                Assert.That(Quaternion.Angle(held[0].localRotation, Quaternion.identity), Is.LessThan(0.01f));
                Assert.That(held[0].localScale, Is.EqualTo(Vector3.one));
            }
            finally
            {
                if (scene.isLoaded)
                    SceneManager.UnloadSceneAsync(scene);
            }
            yield return null;
        }
    }
}
#endif
