#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// Stage 12 Task 7: every seeded attempt-v1 fixture is what the real MissionDirector generates for that seed. Its committed
    /// ready report (the bytes the backend judges) equals, byte for byte, the ready report of GenerateForAttempt with the same
    /// seed, session id and squad size. The export's generation steps are the server's.
    /// </summary>
    public class AttemptFixturePlayTests
    {
        [Serializable]
        sealed class Description
        {
            public string name;
            public string processSessionId;
            public int seed;
            public string readyFile;
        }

        static string Folder => Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Backend", "contracts", "attempt-v1");

        static IEnumerable<string> Seeded() => Directory.GetFiles(Folder, "0*-seeded-*.json")
            .Where(p => !p.EndsWith(".ready.json") && !p.EndsWith(".manifest.json")).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal);

        [Test]
        public void There_are_four_seeded_fixtures() => Assert.That(Seeded().Count(), Is.EqualTo(4));

        [UnityTest]
        public IEnumerator Each_seeded_fixture_is_the_directors_generation([ValueSource(nameof(Seeded))] string name)
        {
            var d = JsonUtility.FromJson<Description>(File.ReadAllText(Path.Combine(Folder, name)));
            var world = new AttemptWorld();
            try
            {
                var attempt = world.Start(d.seed, d.processSessionId);
                world.UseSnapshot(attempt);
                Assert.That(world.Director.GenerateForAttempt(attempt), Is.True);
                yield return TestWorld.WaitUntil(() => world.Director.State == MissionState.Ready || world.Director.State == MissionState.Failed, 60f);
                Assert.That(world.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", world.Director.Report.Failures));
                var ready = AttemptReady.Build(attempt, world.Director, AttemptReady.Pool(world.Director));
                Assert.That(JsonUtility.ToJson(ready), Is.EqualTo(File.ReadAllText(Path.Combine(Folder, d.readyFile))), name);
            }
            finally
            {
                world.Dispose();
            }
            yield return null;
        }
    }
}
#endif
