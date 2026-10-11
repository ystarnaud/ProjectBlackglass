using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>Guards the presentation layer's rules by reading its source: time, randomness, and who may touch gameplay state.</summary>
    public class PresentationSourceAuditTests
    {
        const string Folder = "Assets/_Project/Scripts/Presentation";

        static string[] Sources => Directory.GetFiles(Folder, "*.cs");

        static void AssertNone(string token, params string[] allowedFiles)
        {
            foreach (var file in Sources)
            {
                var name = Path.GetFileName(file);
                if (allowedFiles.Contains(name))
                    continue;
                Assert.That(File.ReadAllText(file), Does.Not.Contain(token), name + " must not contain '" + token + "'");
            }
        }

        [Test]
        public void NoPresentationScript_WritesTheTimeScale_OrUsesWallClockTime()
        {
            AssertNone("timeScale =");
            AssertNone("timeScale=");
            AssertNone("useUnscaledTime = true");
            AssertNone("realtimeSinceStartup");
            AssertNone("unscaledTime");
            AssertNone("WaitForSecondsRealtime");
        }

        [Test]
        public void OnlyTheAudioClock_ReadsUnscaledDeltaTime()
        {
            AssertNone("unscaledDeltaTime", "CombatAudio.cs");
        }

        [Test]
        public void PresentationNeverDrawsFromTheGameplayRandomStream()
        {
            AssertNone("UnityEngine.Random");
            AssertNone("Random.value");
            AssertNone("Random.Range(");
            AssertNone("Random.InitState");
            AssertNone("insideUnitSphere");
        }

        [Test]
        public void OnlyThePreviewPanel_MayChangeHealth_OrIssueOrders()
        {
            AssertNone(".TakeDamage(", "PresentationPreviewPanel.cs");
            AssertNone(".Heal(", "PresentationPreviewPanel.cs");
            AssertNone(".Issue(", "PresentationPreviewPanel.cs");
            AssertNone(".TryUse(", "PresentationPreviewPanel.cs");
            AssertNone(".TryAttack(", "PresentationPreviewPanel.cs");
            AssertNone("NavMeshAgent");
            AssertNone("Physics.SyncTransforms");
        }

        [Test]
        public void TheAuditActuallyFindsTheSources()
        {
            Assert.That(Sources.Length, Is.GreaterThan(10));
            Assert.That(Sources.Select(Path.GetFileName), Does.Contain("CombatAudio.cs"));
        }
    }
}
