#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// Plays a representative burst in the preview scene for a fixed time and writes the frame times with their conditions to
    /// .claude/scratchpad/phase125/performance.txt. It asserts only that frames were measured: no frame-rate target is claimed.
    /// </summary>
    [Category("Perf")]
    public class PresentationPerformanceReportTests
    {
        [UnityTest]
        public IEnumerator Measure_ABurstOfShotsAndBlasts_InThePreviewScene()
        {
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(PresentationPreviewScenePlayModeTests.ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            var scene = SceneManager.GetSceneByPath(PresentationPreviewScenePlayModeTests.ScenePath);
            var panel = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<PresentationPreviewPanel>(true)).First();
            var presenter = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<CombatPresenter>(true)).First();
            yield return new WaitForSeconds(1f);   // warm up

            var times = new System.Collections.Generic.List<float>();
            var peakEffects = 0;
            var start = Time.realtimeSinceStartup;
            var next = 0f;
            while (Time.realtimeSinceStartup - start < 8f)
            {
                if (Time.realtimeSinceStartup - start > next)
                {
                    panel.Stress();
                    panel.CastBlast();
                    next += 0.5f;
                }
                peakEffects = Mathf.Max(peakEffects, presenter.ActiveEffects);
                times.Add(Time.unscaledDeltaTime);
                yield return null;
            }

            times.Sort();
            float Percentile(float p) => times[Mathf.Min(times.Count - 1, (int)(times.Count * p))];
            var report = $"Frames {times.Count} in 8 s; mean {times.Average() * 1000f:F2} ms; median {Percentile(0.5f) * 1000f:F2} ms; " +
                         $"p95 {Percentile(0.95f) * 1000f:F2} ms; p99 {Percentile(0.99f) * 1000f:F2} ms; worst {times.Last() * 1000f:F2} ms; " +
                         $"peak active effects {peakEffects}\n" +
                         $"Conditions: Unity {Application.unityVersion}, {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}), " +
                         $"{Screen.width}x{Screen.height}, quality '{QualitySettings.names[QualitySettings.GetQualityLevel()]}', " +
                         $"{SystemInfo.processorType}, Editor Play Mode (batch: {Application.isBatchMode}); Stress button (80 shots) + Blast every 0.5 s.\n";
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".claude", "scratchpad", "phase125", "performance.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.AppendAllText(path, report);
            Debug.Log(report);
            Assert.That(times.Count, Is.GreaterThan(10));
            yield return SceneManager.UnloadSceneAsync(scene);
        }
    }
}
#endif
