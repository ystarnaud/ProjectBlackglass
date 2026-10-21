using System.Collections.Generic;
using System.IO;
using System.Linq;
using Blackglass.Backend.Core;
using Blackglass.BackendTools;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// Stage 12 Task 7: Backend/contracts/attempt-v1 is exactly what a fresh export of Unity's generation writes (the backend's
    /// AttemptFixtureTests judges those files), like the catalogue export's staleness test. Re-export with
    /// Blackglass > Backend > Export Attempt Fixtures after a deliberate change.
    /// </summary>
    public sealed class AttemptFixtureExportTests
    {
        static SortedDictionary<string, byte[]> Fresh()
        {
            var errors = new List<string>();
            var files = AttemptFixtureExport.Build(errors);
            Assert.That(errors, Is.Empty);
            Assert.That(files, Is.Not.Null);
            return files;
        }

        [Test]
        public void The_committed_fixtures_equal_a_fresh_export()
        {
            var fresh = Fresh();
            Assert.That(Directory.Exists(AttemptFixtureExport.Folder), Is.True, "run Blackglass > Backend > Export Attempt Fixtures");
            var committed = Directory.GetFiles(AttemptFixtureExport.Folder, "*.json").Select(Path.GetFileName).OrderBy(n => n, System.StringComparer.Ordinal).ToList();
            Assert.That(committed, Is.EqualTo(fresh.Keys.ToList()), "the same files");
            foreach (var file in fresh)
                Assert.That(File.ReadAllBytes(Path.Combine(AttemptFixtureExport.Folder, file.Key)), Is.EqualTo(file.Value), file.Key + " is stale: re-export");
        }

        [Test]
        public void An_export_is_deterministic()
        {
            var a = Fresh();
            var b = Fresh();
            Assert.That(b.Keys, Is.EqualTo(a.Keys));
            foreach (var file in a)
                Assert.That(b[file.Key], Is.EqualTo(file.Value), file.Key);
        }

        [Test]
        public void The_forced_duplicate_medkits_are_two_lines_in_the_ready_report()
        {
            var ready = JsonUtility.FromJson<ReadyRequest>(System.Text.Encoding.UTF8.GetString(Fresh()["01-forced-duplicate-medkits.ready.json"]));
            Assert.That(ready.lootPool.Select(l => (l.containerIndex, l.line, l.definitionId, l.quantity)),
                Is.EqualTo(new[] { (0, 0, "item.medkit", 1), (0, 1, "item.medkit", 2) }));
            Assert.That(ready.lootHash, Is.EqualTo(LootPoolHash.Compute(1, new[] { new PoolLine(0, 0, "item.medkit", 1), new PoolLine(0, 1, "item.medkit", 2) })));
        }

        [Test]
        public void Every_valid_manifest_is_canonical_format_v1()
        {
            foreach (var file in Fresh().Where(f => f.Key.EndsWith(".manifest.json") && !f.Key.Contains("-invalid-")))
            {
                var parsed = ManifestCodec.Parse(file.Value);
                Assert.That(parsed.Ok, Is.True, file.Key + ": " + parsed.Code);
                Assert.That(parsed.CanonicalBytes, Is.EqualTo(file.Value), file.Key);
            }
        }
    }
}
