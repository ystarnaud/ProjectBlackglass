using System.IO;
using System.Text.RegularExpressions;
using Blackglass.Backend.Core;
using Blackglass.BackendTools;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>
    /// Stage 12 Task 8: the server's fixture attempt source (Editor and fixture builds only) reads an attempt-v1 description into
    /// the register answer the backend would send; -attemptFixture selects it; the server scene knows the build's catalogue version.
    /// </summary>
    public sealed class AttemptFixtureSourceTests
    {
        static string Fixture(string name) => Path.Combine(AttemptFixtureExport.Folder, name);

        [Test]
        public void A_seeded_fixture_reads_as_the_register_answer_of_one_participant()
        {
            var source = new FixtureAttemptSource(Fixture("03-seeded-collect-and-use.json"));
            Assert.That(source.TryLoad(out var attemptId, out var response, out var problem), Is.True, problem);
            Assert.That(attemptId, Is.EqualTo("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e03"));
            Assert.That(response.processSessionId, Is.EqualTo("0192f1a5-0000-7000-8000-000000000003"));
            Assert.That(response.seed, Is.EqualTo(1));
            Assert.That(response.missionTemplate.id, Is.EqualTo("procedural-standard"));
            Assert.That(response.participants, Has.Length.EqualTo(1));
            var snapshot = response.participants[0].loadout;
            Assert.That(response.participants[0].accountId, Is.EqualTo("JG"));
            Assert.That(snapshot.operatives[0].operativeId, Is.EqualTo("C"));
            Assert.That(snapshot.operatives[0].equipment.Weapon, Is.EqualTo("2k"));
            Assert.That(snapshot.operatives[0].equipment.Armor, Is.Null, "JSON null reads as an empty slot");
            Assert.That(snapshot.catalogueVersion, Is.EqualTo(response.catalogueVersion));
        }

        [Test]
        public void A_missing_or_malformed_fixture_is_refused_with_a_reason()
        {
            Assert.That(new FixtureAttemptSource(Fixture("no-such-fixture.json")).TryLoad(out _, out _, out var problem), Is.False);
            StringAssert.Contains("not found", problem);
            var path = Path.Combine(Path.GetTempPath(), "blackglass-bad-fixture.json");
            File.WriteAllText(path, "{ not json");
            try
            {
                Assert.That(new FixtureAttemptSource(path).TryLoad(out _, out _, out problem), Is.False);
                StringAssert.Contains("unreadable", problem);
                File.WriteAllText(path, "{\"attemptId\":\"x\"}");
                Assert.That(new FixtureAttemptSource(path).TryLoad(out _, out _, out problem), Is.False, "no snapshot");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void The_option_selects_the_fixture_attempt()
        {
            var options = ServerOptions.Parse(new[] { "-attemptFixture", "a/b.json", "-seed", "5" }, out var warnings);
            Assert.That(options.AttemptFixture, Is.EqualTo("a/b.json"));
            Assert.That(warnings, Is.Empty);
            Assert.That(ServerOptions.Parse(new string[0], out _).AttemptFixture, Is.Null);
            // A missing path still selects the attempt, which then fails startup: never the plain mission by accident.
            Assert.That(ServerOptions.Parse(new[] { "-attemptFixture" }, out warnings).AttemptFixture, Is.EqualTo(string.Empty));
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(ServerOptions.Parse(new[] { "-attemptFixture", "-net" }, out _).AttemptFixture, Is.EqualTo(string.Empty));
        }

        [Test]
        public void A_forced_fixture_is_refused_because_it_has_no_seed_to_generate()
        {
            Assert.That(new FixtureAttemptSource(Fixture("01-forced-duplicate-medkits.json")).TryLoad(out _, out _, out var problem), Is.False);
            StringAssert.Contains("no seed", problem);
        }

        // The release guard of S12-D4, pinned in source: a release build has no fixture source and the gate is closed.
        [Test]
        public void The_fixture_attempt_is_compiled_only_into_Editor_and_fixture_builds()
        {
            var server = Path.Combine(ContractVectors.ProjectRoot, "Assets", "_Project", "Scripts", "Server");
            var source = File.ReadAllText(Path.Combine(server, "FixtureAttemptSource.cs"));
            StringAssert.StartsWith("#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE", source.TrimStart('﻿'));
            StringAssert.EndsWith("#endif", source.TrimEnd());
            Assert.That(Regex.Matches(source, @"#if|#else|#elif|#endif").Count, Is.EqualTo(2), "one guard around the whole file");
            var bootstrap = File.ReadAllText(Path.Combine(server, "ServerBootstrap.cs")).Replace("\r\n", "\n");
            StringAssert.Contains("#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE\n        const bool FixtureBuild = true;\n#else\n        const bool FixtureBuild = false;\n#endif", bootstrap);
            StringAssert.Contains("#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE\n            return new FixtureAttemptSource(path);\n#else\n            return null;\n#endif", bootstrap);
            Assert.That(Regex.Matches(bootstrap, @"new FixtureAttemptSource\(").Count, Is.EqualTo(1), "the only construction is the guarded one");
            StringAssert.Contains("internal Func<bool> AttemptFixtureAllowed = () => FixtureBuild;", bootstrap);
        }

        // The loopback fixture checkpoint: the operator aid (where the hostile starts, where extraction is) and the logged manifest
        // exist only in Editor and fixture builds, and only in the server's log.
        [TestCase("FIXTURE OPERATOR AID")]
        [TestCase("attempt manifest ")]
        public void The_fixture_operator_aid_is_compiled_only_into_Editor_and_fixture_builds(string marker)
        {
            var bootstrap = File.ReadAllText(Path.Combine(ContractVectors.ProjectRoot, "Assets", "_Project", "Scripts", "Server", "ServerBootstrap.cs"));
            var at = bootstrap.IndexOf(marker, System.StringComparison.Ordinal);
            Assert.That(at, Is.GreaterThan(0), "the marker is in the bootstrap");
            Assert.That(bootstrap.IndexOf(marker, at + 1, System.StringComparison.Ordinal), Is.EqualTo(-1), "written in one place");
            var opened = bootstrap.LastIndexOf("#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE", at, System.StringComparison.Ordinal);
            var closed = bootstrap.LastIndexOf("#endif", at, System.StringComparison.Ordinal);
            var otherwise = bootstrap.LastIndexOf("#else", at, System.StringComparison.Ordinal);
            Assert.That(opened, Is.GreaterThan(closed), "inside the fixture guard");
            Assert.That(opened, Is.GreaterThan(otherwise), "not in an #else branch");
            foreach (Match call in Regex.Matches(bootstrap, @"\bLogFixture\w*\("))
            {
                var callOpened = bootstrap.LastIndexOf("#if UNITY_EDITOR || BLACKGLASS_NET_FIXTURE", call.Index, System.StringComparison.Ordinal);
                var callClosed = bootstrap.LastIndexOf("#endif", call.Index, System.StringComparison.Ordinal);
                Assert.That(callOpened, Is.GreaterThan(callClosed), "every use of " + call.Value + " is guarded too");
            }
        }

        [Test]
        public void The_server_scene_holds_the_builds_catalogue_version()
        {
            var errors = new System.Collections.Generic.List<string>();
            var version = CanonicalCatalogueWriter.VersionOf(CatalogueExporter.Export(CatalogueSources.LoadProject(), errors));
            Assert.That(errors, Is.Empty);
            var scene = File.ReadAllText(Path.Combine(ContractVectors.ProjectRoot, "Assets", "_Project", "Scenes", "ServerMission.unity"));
            var stored = Regex.Match(scene, @"catalogueVersion: (sha256:[0-9a-f]{64})");
            Assert.That(stored.Success, Is.True, "rebuild the server scene (Blackglass > Server > Build Server Scene)");
            Assert.That(stored.Groups[1].Value, Is.EqualTo(version), "the scene is stale: rebuild it after a catalogue change");
        }
    }
}
