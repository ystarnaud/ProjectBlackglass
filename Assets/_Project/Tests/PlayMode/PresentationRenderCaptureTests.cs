#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    [Category("Render")]
    public class PresentationRenderCaptureTests
    {
        Scene scene;
        string outputDirectory;

        [UnitySetUp]
        public IEnumerator Load()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("No graphics device: frames cannot be rendered in this environment.");
            outputDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".claude", "scratchpad", "phase125", "renders"));
            Directory.CreateDirectory(outputDirectory);
            Time.captureFramerate = 60;   // deterministic 1/60 s frames
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(PresentationPreviewScenePlayModeTests.ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(PresentationPreviewScenePlayModeTests.ScenePath);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator Unload()
        {
            Time.captureFramerate = 0;
            Time.timeScale = 1f;
            if (scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(scene);
        }

        T Find<T>() where T : Component =>
            scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).First();

        // Renders the scene camera into a PNG and returns the file's non-uniformity (0 = a flat colour).
        float Capture(string name, int width = 1280, int height = 720)
        {
            var camera = Find<Camera>();
            var texture = new RenderTexture(width, height, 24);
            var previous = camera.targetTexture;
            camera.targetTexture = texture;
            camera.Render();
            RenderTexture.active = texture;
            var image = new Texture2D(width, height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            image.Apply();
            RenderTexture.active = null;
            camera.targetTexture = previous;
            File.WriteAllBytes(Path.Combine(outputDirectory, name + ".png"), image.EncodeToPNG());
            var pixels = image.GetPixels32();
            var min = 255;
            var max = 0;
            foreach (var p in pixels)
            {
                var v = (p.r + p.g + p.b) / 3;
                min = Mathf.Min(min, v);
                max = Mathf.Max(max, v);
            }
            Object.Destroy(image);
            texture.Release();
            return max - min;
        }

        // Pose the camera for a close look, bypassing the rig's own placement.
        void CloseUp(Vector3 position, Vector3 lookAt)
        {
            Find<TacticalCameraController>().enabled = false;
            var camera = Find<Camera>();
            camera.transform.position = position;
            camera.transform.LookAt(lookAt);
        }

        [UnityTest]
        public IEnumerator GameplayCamera_RifleShot_AtTheNormalZoom()
        {
            Find<PresentationPreviewPanel>().FireAtCharacter();
            yield return null; yield return null;
            Assert.That(Capture("gameplay_rifle_hit"), Is.GreaterThan(20f), "the frame is not blank");
        }

        [UnityTest]
        public IEnumerator CloseUp_MuzzleFlashAndTracer()
        {
            // From behind the shooter's right shoulder, where the rifle is held.
            CloseUp(new Vector3(0.5f, 2.4f, -8.5f), new Vector3(-2.6f, 1.4f, -4.5f));
            Find<PresentationPreviewPanel>().FireAtCharacter();
            yield return null; yield return null;
            Assert.That(Capture("closeup_muzzle_tracer"), Is.GreaterThan(20f));
            for (var i = 0; i < 18; i++) yield return null;   // 0.3 s later: how the smoke puff develops
            Assert.That(Capture("closeup_muzzle_smoke"), Is.GreaterThan(20f));
        }

        [UnityTest]
        public IEnumerator CloseUp_ConcreteAndMetalImpacts()
        {
            CloseUp(new Vector3(0f, 3f, 3f), new Vector3(0f, 1.2f, 8f));
            var panel = Find<PresentationPreviewPanel>();
            panel.FireAtConcrete();
            panel.FireAtMetal();
            yield return null; yield return null; yield return null;
            Assert.That(Capture("closeup_impacts"), Is.GreaterThan(20f));
            for (var i = 0; i < 12; i++) yield return null;   // 0.25 s after the shots: dust spreading, sparks falling
            Assert.That(Capture("closeup_impacts_late"), Is.GreaterThan(20f));
        }

        // The hit flash on the real skinned models: the tinted hostile (left) and the white Darius (right).
        [UnityTest]
        public IEnumerator CloseUp_HitFlash_OnTintedHostileAndWhiteDarius()
        {
            var presenter = Find<CombatPresenter>();
            var hostilePrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Hostile.prefab");
            var dariusPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(MissionRig.DariusPlayerPath);
            var hostile = Object.Instantiate(hostilePrefab, new Vector3(-1.2f, 1f, 7f), Quaternion.Euler(0f, 180f, 0f));
            var darius = Object.Instantiate(dariusPrefab, new Vector3(1.2f, 1f, 7f), Quaternion.Euler(0f, 180f, 0f));
            try
            {
                hostile.AddComponent<UnitPresentation>().Bind(presenter);
                darius.AddComponent<UnitPresentation>().Bind(presenter);
                Physics.SyncTransforms();
                CloseUp(new Vector3(0f, 1.7f, 3.2f), new Vector3(0f, 1.1f, 7f));
                yield return null; yield return null;
                Capture("flash_units_before");

                hostile.GetComponent<Health>().TakeDamage(5);
                darius.GetComponent<Health>().TakeDamage(5);
                yield return null; yield return null;   // two frames into the flash (0.12 s = about 7 frames at 60 fps)
                Assert.That(Capture("flash_units_during"), Is.GreaterThan(20f));
                for (var i = 0; i < 12; i++) yield return null;
                Capture("flash_units_after");
            }
            finally
            {
                Object.Destroy(hostile);
                Object.Destroy(darius);
            }
        }

        // Damage no shot depicted (a blast, retaliation): the default burst on the plain capsule, facing the camera.
        [UnityTest]
        public IEnumerator CloseUp_DefaultBurst_FromDamageWithNoShot()
        {
            CloseUp(new Vector3(-6f, 2f, 0f), new Vector3(-3f, 1.3f, 4f));
            var target = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Health>(true)).First(h => h.name == "Target");
            yield return null; yield return null;
            Capture("closeup_default_burst_before");
            target.TakeDamage(5);
            yield return null; yield return null; yield return null;
            Assert.That(Capture("closeup_default_burst"), Is.GreaterThan(20f));
            for (var i = 0; i < 9; i++) yield return null;
            Capture("closeup_default_burst_late");
        }

        [UnityTest]
        public IEnumerator CloseUp_CharacterHit()
        {
            CloseUp(new Vector3(-6f, 2f, 0f), new Vector3(-3f, 1.3f, 4f));
            Find<PresentationPreviewPanel>().FireAtCharacter();
            yield return null; yield return null; yield return null;
            Assert.That(Capture("closeup_character_hit"), Is.GreaterThan(20f));
            for (var i = 0; i < 9; i++) yield return null;    // 0.2 s after the shot
            Assert.That(Capture("closeup_character_hit_late"), Is.GreaterThan(20f));
        }

        // The heal pulse on the Healee dummy: from the camera's normal view, close up from the side, and straight down (ring radius check).
        [UnityTest]
        public IEnumerator HealPulse_AtTheNormalZoom_CloseUpAndFromAbove()
        {
            var panel = Find<PresentationPreviewPanel>();
            panel.HealDummy();
            yield return null; yield return null;
            Assert.That(Capture("gameplay_heal_a"), Is.GreaterThan(20f));
            for (var i = 0; i < 18; i++) yield return null;   // 0.3 s
            Capture("gameplay_heal_b");
            for (var i = 0; i < 40; i++) yield return null;
            Capture("gameplay_heal_c");

            CloseUp(new Vector3(3.5f, 1.6f, -6.5f), new Vector3(6f, 0.8f, -3f));
            panel.HealDummy();
            yield return null; yield return null;
            Assert.That(Capture("closeup_heal_a"), Is.GreaterThan(20f));
            for (var i = 0; i < 12; i++) yield return null;   // 0.2 s
            Capture("closeup_heal_b");
            for (var i = 0; i < 18; i++) yield return null;   // 0.5 s
            Capture("closeup_heal_c");
            for (var i = 0; i < 70; i++) yield return null;   // let this pulse finish before the top-down one

            CloseUp(new Vector3(6f, 12f, -3f), new Vector3(6f, 0f, -2.999f));
            panel.HealDummy();
            var frame = 0;
            foreach (var target in new[] { 4, 12, 24, 34 })
            {
                while (frame < target) { yield return null; frame++; }
                Capture("topdown_heal_" + target.ToString("00"));
            }
        }

        [UnityTest]
        public IEnumerator Gameplay_Blast_AtTheNormalZoomAndCloseUp()
        {
            Find<PresentationPreviewPanel>().CastBlast();
            yield return null; yield return null; yield return null;
            Assert.That(Capture("gameplay_blast_a"), Is.GreaterThan(20f));
            for (var i = 0; i < 12; i++) yield return null;
            Assert.That(Capture("gameplay_blast_b"), Is.GreaterThan(20f));
            for (var i = 0; i < 12; i++) yield return null;   // 0.4 s: the ring has reached its full size
            Capture("gameplay_blast_c");
            for (var i = 0; i < 90; i++) yield return null;   // 1.9 s: only the smoke is left
            Capture("gameplay_blast_d");
            Find<TacticalCameraController>().enabled = false;
            var camera = Find<Camera>();
            camera.transform.position = new Vector3(0f, 6f, 0f);
            camera.transform.LookAt(new Vector3(0f, 1f, 8f));
            Find<PresentationPreviewPanel>().CastBlast();
            yield return null; yield return null;
            Assert.That(Capture("closeup_blast"), Is.GreaterThan(20f));
            for (var i = 0; i < 18; i++) yield return null;   // 0.4 s
            Capture("closeup_blast_b");
            for (var i = 0; i < 40; i++) yield return null;   // 1.0 s
            Capture("closeup_blast_c");
            for (var i = 0; i < 80; i++) yield return null;   // 2.3 s
            Capture("closeup_blast_d");
        }

        // Straight down on the blast point (12 m up, 60 degree field: about 52 px per metre at the ground), so the ring radius can be measured.
        [UnityTest]
        public IEnumerator Blast_FromAbove_ShowsWhereTheRingEnds()
        {
            CloseUp(new Vector3(0f, 12f, 8f), new Vector3(0f, 0f, 8.001f));
            Find<PresentationPreviewPanel>().CastBlast();
            var frame = 0;
            foreach (var target in new[] { 6, 12, 18, 24 })
            {
                while (frame < target) { yield return null; frame++; }
                Capture("topdown_blast_" + target.ToString("00"));
            }
        }
    }
}
#endif
