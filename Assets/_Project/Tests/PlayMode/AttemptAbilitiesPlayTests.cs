#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Linq;
using Blackglass.Backend.Core;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// Stage 12 Task 8 on the real server scene: -attemptFixture starts an authoritative attempt from a fixture (Editor and
    /// fixture builds only). The snapshot operatives spawn with their gear and abilities scaled by their picks; Recon Scan and
    /// Aimed Shot resolve on the server with their cooldowns; basic attacks kill with no client and hostile AI acts on its own;
    /// a friendly death shows in the manifest with its held items as frozen.
    /// </summary>
    public class AttemptAbilitiesPlayTests
    {
        const string ScenePath = "Assets/_Project/Scenes/ServerMission.unity";
        Scene scene;
        ServerBootstrap bootstrap;
        MissionDirector director;
        string fixturePath;
        int exitCode;

        static string Fixtures => Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Backend", "contracts", "attempt-v1");

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            exitCode = -1;
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(ScenePath);
            var roots = scene.GetRootGameObjects();
            bootstrap = roots.Single(g => g.name == "Server").GetComponent<ServerBootstrap>();
            director = roots.Single(g => g.name == "Mission").GetComponent<MissionDirector>();
            bootstrap.Exit = code => exitCode = code;
            // The scene starts its own fixture mission (no -attemptFixture in the Editor's arguments); wait for it, then clear it.
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 60f);
            director.Clear();
            yield return null;
            // Fixture 03 (seed 1), with C at rank 3 and two picks, so the picks' scaling is visible.
            var text = File.ReadAllText(Path.Combine(Fixtures, "03-seeded-collect-and-use.json"));
            text = text.Replace("\"choices\":[],\"definitionId\":\"a93e5c17-0d48-4f2b-b6a3-7e1c8d4f9a02\",\"equipment\"", "\"choices\":[\"ability\",\"ability\"],\"definitionId\":\"a93e5c17-0d48-4f2b-b6a3-7e1c8d4f9a02\",\"equipment\"")
                .Replace("\"experience\":0,\"operativeId\":\"C\",\"rank\":1", "\"experience\":260,\"operativeId\":\"C\",\"rank\":3");
            Assert.That(text, Does.Contain("\"rank\":3"), "the fixture edit applied");
            Assert.That(text, Does.Contain("[\"ability\",\"ability\"]"), "the fixture edit applied");
            fixturePath = Path.Combine(Application.temporaryCachePath, "attempt-fixture-picks.json");
            File.WriteAllText(fixturePath, text);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Time.timeScale = 1f;
            if (director != null)
                director.Clear();
            yield return null;
            if (scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(scene);
            if (fixturePath != null && File.Exists(fixturePath))
                File.Delete(fixturePath);
        }

        IEnumerator StartAttempt()
        {
            bootstrap.Begin(new[] { "-attemptFixture", fixturePath });
            yield return TestWorld.WaitUntil(() => bootstrap.Session != null || bootstrap.HasExited, 60f);
            Assert.That(bootstrap.HasExited, Is.False, bootstrap.ExitReason);
            Assert.That(bootstrap.Session, Is.Not.Null);
        }

        static CommandableUnit Unit(MissionDirector d, string operativeId) =>
            d.Friendlies.Single(u => u.GetComponent<UnitIdentity>().OperativeId == operativeId);

        static AbilityDefinition Ability(CommandableUnit unit, string id)
        {
            var abilities = unit.GetComponent<UnitAbilities>();
            for (var i = 0; i < abilities.Count; i++)
                if (abilities.Definition(i).Id == id)
                    return abilities.Definition(i);
            Assert.Fail(unit.name + " has no " + id);
            return null;
        }

        [UnityTest]
        public IEnumerator The_snapshot_operatives_spawn_with_their_gear_and_abilities_scaled_by_their_picks()
        {
            yield return StartAttempt();
            Assert.That(director.Attempt.AttemptId, Is.EqualTo("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e03"));
            Assert.That(director.Friendlies.Select(u => u.GetComponent<UnitIdentity>().OperativeId), Is.EquivalentTo(new[] { "C", "D" }));
            var c = Unit(director, "C");
            var identity = c.GetComponent<UnitIdentity>();
            Assert.That(identity.Definition.Id, Is.EqualTo("a93e5c17-0d48-4f2b-b6a3-7e1c8d4f9a02"), "Kestrel");
            Assert.That(identity.State.Experience, Is.EqualTo(260));
            Assert.That(identity.State.ChoiceIds, Is.EqualTo(new[] { "ability", "ability" }));
            Assert.That(identity.Effective.Rank, Is.EqualTo(3));
            Assert.That(identity.Effective.HasWeapon, Is.True);
            Assert.That(identity.Effective.WeaponName, Does.Contain("Marksman"), "the equipped marksman rifle 2k");
            var abilities = c.GetComponent<UnitAbilities>();
            Assert.That(abilities.Count, Is.EqualTo(identity.Definition.Abilities.Length));
            Assert.That(abilities.PowerMultiplier, Is.EqualTo(identity.Effective.AbilityPower));
            Assert.That(abilities.CooldownMultiplier, Is.EqualTo(identity.Effective.AbilityCooldownMultiplier));
            var unpicked = Unit(director, "D").GetComponent<UnitIdentity>().Effective;
            Assert.That(identity.Effective.AbilityPower * identity.Effective.AbilityCooldownMultiplier,
                Is.Not.EqualTo(unpicked.AbilityPower * unpicked.AbilityCooldownMultiplier), "the two ability picks change C's abilities");
            Assert.That(c.GetComponent<Health>().Max, Is.EqualTo(identity.Effective.MaxHealth));
            var bag = bootstrap.Inventory.WorkingLoadout("C").Bag;
            Assert.That(bag.Find("2k").DefinitionId, Is.EqualTo("item.marksman-rifle"));
            Assert.That(bag.Find("2n").DefinitionId, Is.EqualTo("item.light-vest"));
            Assert.That(bag.CountOf("item.medkit"), Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator Recon_scan_and_aimed_shot_resolve_on_the_server_with_their_cooldowns()
        {
            yield return StartAttempt();
            var c = Unit(director, "C");
            var abilities = c.GetComponent<UnitAbilities>();
            var scan = Ability(c, "ability.recon-scan");
            Assert.That(abilities.TryUse(AbilityCommand.AtGround(scan, c.transform.position + Vector3.forward * 2f)), Is.True, abilities.LastFailure.ToString());
            Assert.That(abilities.IsReady(abilities.IndexOf(scan)), Is.False);
            Assert.That(abilities.TryUse(AbilityCommand.AtGround(scan, c.transform.position)), Is.False, "inside the cooldown");
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OnCooldown));

            // The hostile brought between C and D (who see each other in the spawn room), so fog of war observes it: Aimed Shot
            // then resolves through AbilityRules and UnitAbilities.
            var hostile = director.Hostiles[0];
            var d = Unit(director, "D");
            var toD = d.transform.position - c.transform.position;
            toD.y = 0f;
            Assert.That(hostile.GetComponent<NavMeshAgent>().Warp(c.transform.position + toD.normalized * Mathf.Min(2.5f, toD.magnitude * 0.6f)), Is.True);
            var intelligence = scene.GetRootGameObjects().Single(g => g.name == "Systems").GetComponent<IntelligenceService>();
            yield return TestWorld.WaitUntil(() => intelligence.CanTarget(hostile.GetComponent<Health>()), 10f);
            Assert.That(intelligence.CanTarget(hostile.GetComponent<Health>()), Is.True, "precondition: the hostile is observed");
            var aimed = Ability(c, "ability.aimed-shot");
            hostile.GetComponent<Health>().SetMax(100000); // it must survive the first shot, so the second meets the cooldown, not a dead target
            var shots = 0;
            abilities.ShotResolved += _ => shots++;
            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile.GetComponent<Health>())), Is.True, abilities.LastFailure.ToString());
            Assert.That(shots, Is.EqualTo(1), "the shot resolved on the server");
            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile.GetComponent<Health>())), Is.False);
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OnCooldown));
            Assert.That(abilities.UsedCount, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator Combat_resolves_with_no_client_and_a_friendly_death_shows_in_the_manifest()
        {
            yield return StartAttempt();
            var c = Unit(director, "C");
            var d = Unit(director, "D");
            var hostile = director.Hostiles[0];
            var dHealth = d.GetComponent<Health>();
            var hostileHealth = hostile.GetComponent<Health>();
            var damageToD = 0;
            dHealth.Damaged += amount => damageToD += amount;
            Assert.That(hostile.GetComponent<NavMeshAgent>().Warp(d.transform.position + d.transform.forward * 4f), Is.True);
            Time.timeScale = 4f;
            // Hostile AI acts on its own: it engages D without any order.
            yield return TestWorld.WaitUntil(() => damageToD > 0 || !dHealth.IsAlive, 60f);
            Assert.That(damageToD, Is.GreaterThan(0), "the hostile attacked D by itself");
            // D dies (the scripted part of the scenario), C's basic attack kills the hostile: an ordered attack, no client.
            dHealth.TakeDamage(100000);
            c.Issue(new AttackCommand(hostileHealth));
            yield return TestWorld.WaitUntil(() => !hostileHealth.IsAlive, 60f);
            Assert.That(hostileHealth.IsAlive, Is.False, "the server resolved C's attacks to a kill");
            // C extracts: the mission ends in success with D dead.
            c.GetComponent<NavMeshAgent>().Warp(director.Current.ExtractionZone.position);
            yield return TestWorld.WaitUntil(() => bootstrap.Session.IsFrozen, 30f);
            Time.timeScale = 1f;
            var session = bootstrap.Session;
            Assert.That(session.Outcome.Kind, Is.EqualTo(ManifestBuildKind.Normal), string.Join(",", session.Outcome.Diagnostics));
            var manifest = session.Outcome.Manifest.Manifest;
            Assert.That(manifest.Outcome, Is.EqualTo(ManifestOutcome.Success));
            Assert.That(manifest.Integrity, Is.EqualTo(ManifestIntegrity.Ok), string.Join(",", session.Facts.FailureCodes));
            Assert.That(manifest.OutcomeSequence, Is.EqualTo(session.Sequence.Committed + 1), "the terminal event follows every committed event");
            Assert.That(session.Sequence.Committed, Is.GreaterThanOrEqualTo(4), "at least D's death, the hostile's death and two objective changes");
            var committedAtFreeze = session.Sequence.Committed;
            var dead = manifest.Operatives.Single(o => o.Slot == 1);
            Assert.That(dead.Alive, Is.False, "D died");
            Assert.That(dead.HeldItems, Is.EqualTo(new[] { Base58Id.TryParse("2m", out var rifle) ? rifle : 0 }), "D's rifle, as frozen");
            Assert.That(dead.HeldStacks.Single().Quantity, Is.EqualTo(1));
            Assert.That(manifest.Operatives.Single(o => o.Slot == 0).Alive, Is.True);

            // A kill after the end changes nothing: C dies after the freeze, the manifest still has C alive.
            var bytes = session.Outcome.Manifest.CanonicalBytes;
            c.GetComponent<Health>().TakeDamage(100000);
            yield return null;
            Assert.That(session.Outcome.Manifest.CanonicalBytes, Is.EqualTo(bytes));
            Assert.That(session.Outcome.Manifest.Manifest.Operatives.Single(o => o.Slot == 0).Alive, Is.True);
            Assert.That(session.Sequence.Committed, Is.EqualTo(committedAtFreeze), "nothing after the freeze advances the sequence");
            Assert.That(session.Outcome.Manifest.Manifest.OutcomeSequence, Is.EqualTo(committedAtFreeze + 1));
        }

        sealed class OtherTemplateSource : IAttemptSource
        {
            readonly IAttemptSource inner;
            readonly string id;
            readonly int version;

            public OtherTemplateSource(IAttemptSource inner, string id, int version)
            {
                this.inner = inner;
                this.id = id;
                this.version = version;
            }

            public bool TryLoad(out string attemptId, out SessionRegisterResponse response, out string problem)
            {
                var ok = inner.TryLoad(out attemptId, out response, out problem);
                if (ok) response.missionTemplate = new MissionTemplateDto { id = id, version = version };
                return ok;
            }
        }

        [UnityTest]
        public IEnumerator An_unsupported_template_is_refused_before_any_generation([Values("procedural-standard:2", "mission.other:1")] string template)
        {
            var parts = template.Split(':');
            bootstrap.AttemptSourceFactory = path => new OtherTemplateSource(new FixtureAttemptSource(path), parts[0], int.Parse(parts[1]));
            bootstrap.Begin(new[] { "-attemptFixture", fixturePath });
            yield return null;
            Assert.That(bootstrap.HasExited, Is.True);
            Assert.That(exitCode, Is.EqualTo(ServerExitCodes.StartupFailed));
            StringAssert.Contains("unsupported_template", bootstrap.ExitReason);
            Assert.That(director.Attempt, Is.Null, "no generation");
            Assert.That(bootstrap.Session, Is.Null, "no ready report, no gameplay start");
        }

        [UnityTest]
        public IEnumerator Only_committed_gameplay_events_advance_the_sequence()
        {
            yield return StartAttempt();
            var session = bootstrap.Session;
            var c = Unit(director, "C");
            var abilities = c.GetComponent<UnitAbilities>();
            var items = c.GetComponent<UnitItems>();
            var scan = Ability(c, "ability.recon-scan");

            var before = session.Sequence.Committed;
            var kit = bootstrap.Inventory.WorkingLoadout("C").Bag.Entries.First(e => e.DefinitionId == "item.medkit").InstanceId;
            Assert.That(items.TryUse(kit), Is.False, "full health: no effect");
            Assert.That(session.Sequence.Committed, Is.EqualTo(before), "a no-effect request is not an event");

            Assert.That(abilities.TryUse(AbilityCommand.AtGround(scan, c.transform.position)), Is.True, abilities.LastFailure.ToString());
            Assert.That(session.Sequence.Committed, Is.EqualTo(before + 1), "an accepted ability use is one event");
            Assert.That(abilities.TryUse(AbilityCommand.AtGround(scan, c.transform.position)), Is.False, "on cooldown");
            Assert.That(session.Sequence.Committed, Is.EqualTo(before + 1), "a refused request is not an event");

            c.GetComponent<Health>().TakeDamage(30);
            Assert.That(session.Sequence.Committed, Is.EqualTo(before + 2), "damage taken is one state change");
            Assert.That(items.TryUse(kit), Is.True);
            Assert.That(session.Sequence.Committed, Is.EqualTo(before + 4), "an accepted medkit use: the item use and the healing it caused");
            c.GetComponent<Health>().TakeDamage(0);
            Assert.That(session.Sequence.Committed, Is.EqualTo(before + 4), "no change, no event");
            Assert.That(session.CommittedEvents, Is.EqualTo(session.Sequence.Committed));
        }

        [UnityTest]
        public IEnumerator An_attempt_fixture_option_without_a_path_ends_startup()
        {
            bootstrap.Begin(new[] { "-attemptFixture" });
            yield return null;
            Assert.That(bootstrap.HasExited, Is.True, "never the plain mission by accident");
            Assert.That(exitCode, Is.EqualTo(ServerExitCodes.StartupFailed));
            StringAssert.Contains("-attemptFixture", bootstrap.ExitReason);
        }

        [UnityTest]
        public IEnumerator The_fixture_attempt_is_refused_unless_the_fixture_gate_is_on()
        {
            bootstrap.AttemptFixtureAllowed = () => false;
            bootstrap.Begin(new[] { "-attemptFixture", fixturePath });
            yield return null;
            Assert.That(bootstrap.HasExited, Is.True);
            Assert.That(exitCode, Is.EqualTo(ServerExitCodes.StartupFailed));
            StringAssert.Contains("release build", bootstrap.ExitReason);
            Assert.That(director.Attempt, Is.Null, "nothing was generated");
        }

        [UnityTest]
        public IEnumerator A_fixture_the_attempt_rules_refuse_ends_startup()
        {
            File.WriteAllText(fixturePath, File.ReadAllText(fixturePath).Replace("\"slot\":1", "\"slot\":5"));
            bootstrap.Begin(new[] { "-attemptFixture", fixturePath });
            yield return null;
            Assert.That(bootstrap.HasExited, Is.True);
            Assert.That(exitCode, Is.EqualTo(ServerExitCodes.StartupFailed));
            StringAssert.Contains("bad_slots", bootstrap.ExitReason);
        }
    }
}
#endif
