using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using static Blackglass.Tests.ContractVectors;

namespace Blackglass.Tests
{
    /// <summary>
    /// Runs the shared progression vector (Backend/contracts/progression) against PersistentOperativeState and
    /// ProgressionTrack. The backend's xUnit tests run the same file against its own pick rule, so the two sides cannot drift.
    /// </summary>
    public class ProgressionContractTests
    {
        readonly List<UnityEngine.Object> created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var asset in created)
            {
                if (asset != null)
                    UnityEngine.Object.DestroyImmediate(asset);
            }
            created.Clear();
        }

        ProgressionTrack BuildTrack(Dictionary<string, object> vector)
        {
            var track = Obj(vector["track"]);
            var thresholds = Arr(track["xpThresholds"]).Select(Int).ToArray();
            var choices = Arr(track["choiceIds"]).Select(id =>
            {
                var choice = AdvancementChoice.Create(Str(id), Str(id), "", default);
                created.Add(choice);
                return choice;
            }).ToArray();
            var built = ProgressionTrack.Create(thresholds, choices, 150, 50);
            created.Add(built);
            return built;
        }

        /// <summary>
        /// The public API where it can reach the case (AddExperience, then picks through TryPickChoice, every one of which must
        /// succeed while a pick is pending). It cannot reach negative XP or more picks than the rank allows, so those
        /// states are read through the serializer, which is how a stored state comes back from disk.
        /// </summary>
        static PersistentOperativeState StateFor(ProgressionTrack track, string name, int xp, int picksMade, out bool usedPublicApi)
        {
            usedPublicApi = xp >= 0 && picksMade <= Math.Max(0, track.RankFor(xp) - 1);
            if (!usedPublicApi)
            {
                var picks = string.Join(",", Enumerable.Repeat("\"combat\"", picksMade));
                return JsonUtility.FromJson<PersistentOperativeState>(
                    "{\"operativeId\":\"" + name + "\",\"experience\":" + xp + ",\"choiceIds\":[" + picks + "]}");
            }
            var state = new PersistentOperativeState(name);
            state.AddExperience(xp);
            for (var i = 0; i < picksMade; i++)
                Assert.That(state.TryPickChoice(track, "combat"), Is.True, $"{name}: setup pick {i + 1} of {picksMade} should succeed");
            return state;
        }

        [Test]
        public void Every_case_matches_PersistentOperativeState()
        {
            var vector = Load("progression", "vectors.json");
            var track = BuildTrack(vector);
            var cases = Arr(vector["cases"]).Select(Obj).ToList();
            Assert.That(cases.Count, Is.GreaterThanOrEqualTo(30), "the vector file lost cases");

            var names = new HashSet<string>();
            var outcomes = new HashSet<string>();
            var viaApi = 0;
            var viaSerializer = 0;
            foreach (var c in cases)
            {
                var name = Str(c["name"]);
                Assert.That(names.Add(name), Is.True, "duplicate case name " + name);
                var xp = Int(c["xp"]);
                var picksMade = Int(c["picksMade"]);
                var choiceId = c["choiceId"] == null ? null : Str(c["choiceId"]);
                var expectedRank = Int(c["rank"]);
                var expectedPending = Int(c["pending"]);
                var outcome = Str(c["outcome"]);
                outcomes.Add(outcome);

                // Negative XP cannot be added through the public API: the track itself must still call it rank 1.
                if (xp < 0)
                {
                    Assert.That(track.RankFor(xp), Is.EqualTo(expectedRank), name + ": RankFor on the track");
                    Assert.Throws<ArgumentOutOfRangeException>(() => new PersistentOperativeState("x").AddExperience(xp), name);
                }

                var state = StateFor(track, "operative-" + name, xp, picksMade, out var usedPublicApi);
                if (usedPublicApi) viaApi++; else viaSerializer++;
                Assert.That(state.Experience, Is.EqualTo(xp), name + ": experience");
                Assert.That(state.ChoiceIds.Count, Is.EqualTo(picksMade), name + ": picks made");

                Assert.That(state.Rank(track), Is.EqualTo(expectedRank), name + ": rank");
                Assert.That(state.PendingPicks(track), Is.EqualTo(expectedPending), name + ": pending picks before the attempt");

                var picked = state.TryPickChoice(track, choiceId);
                var actual = picked ? "ok" : (track.FindChoice(choiceId) == null ? "UnknownChoice" : "NoPendingPick");
                Assert.That(actual, Is.EqualTo(outcome), name + ": outcome of picking '" + (choiceId ?? "<null>") + "'");
                Assert.That(state.ChoiceIds.Count, Is.EqualTo(picked ? picksMade + 1 : picksMade), name + ": picks after the attempt");
                if (picked)
                    Assert.That(state.ChoiceIds[state.ChoiceIds.Count - 1], Is.EqualTo(choiceId), name + ": the stored choice id");
                Assert.That(state.PendingPicks(track), Is.EqualTo(Math.Max(0, expectedPending - (picked ? 1 : 0))), name + ": pending picks after the attempt");
            }

            Assert.That(outcomes, Is.EquivalentTo(new[] { "ok", "UnknownChoice", "NoPendingPick" }), "the vector must cover every outcome");
            Assert.That(viaApi, Is.GreaterThan(0));
            Assert.That(viaSerializer, Is.GreaterThan(0), "the vector no longer needs the serializer path (negative XP, picks above the rank)");
        }
    }
}
