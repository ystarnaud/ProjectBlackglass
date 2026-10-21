using System;
using System.Collections.Generic;
using System.Linq;
using Blackglass.Backend.Core;
using NUnit.Framework;
using static Blackglass.Tests.AttemptTestData;

namespace Blackglass.Tests
{
    /// <summary>
    /// Stage 12 Task 2: AttemptStart validates the backend's register answer before any gameplay starts. Each refusal code
    /// has its own case; the first failure in the documented order wins.
    /// </summary>
    public sealed class AttemptStartTests
    {
        static string Refusal(string attemptId, SessionRegisterResponse response, Func<string, bool> known = null)
        {
            var ok = AttemptStart.TryCreate(attemptId, response, GoldenCatalogueVersion, known ?? KnownOperative, GoldenLedgerCatalogue(),
                out var start, out var refusal);
            Assert.That(ok, Is.EqualTo(refusal == null), "true exactly when there is no refusal");
            Assert.That(start == null, Is.EqualTo(!ok), "a refused attempt has no start");
            return refusal;
        }

        [Test]
        public void A_valid_response_gives_the_snapshot_in_slot_order()
        {
            var response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            Assert.That(AttemptStart.TryCreate(AttemptId, response, GoldenCatalogueVersion, KnownOperative, GoldenLedgerCatalogue(), out var start, out var refusal),
                Is.True, refusal);
            Assert.That(start.AttemptId, Is.EqualTo(AttemptId));
            Assert.That(start.ProcessSessionId, Is.EqualTo(SessionId));
            Assert.That(start.Seed, Is.EqualTo(Seed));
            Assert.That(start.TemplateId, Is.EqualTo(TemplateId));
            Assert.That(start.TemplateVersion, Is.EqualTo(1));
            Assert.That(start.CatalogueVersion, Is.EqualTo(GoldenCatalogueVersion));
            Assert.That(start.AccountId, Is.EqualTo("JG"));
            Assert.That(start.Snapshot.Select(o => o.OperativeId), Is.EqualTo(new[] { "C" }));
            Assert.That(start.Snapshot[0].Items.Select(i => i.ItemId), Is.EqualTo(new[] { "2k", "2m", "2n" }));
            Assert.That(start.Snapshot[0].Stacks.Single().Quantity, Is.EqualTo(3));
            Assert.That(start.Snapshot[0].WeaponItemId, Is.EqualTo("2k"));
            Assert.That(start.Snapshot[0].ArmorItemId, Is.Null);
            Assert.That(start.OperativeDefinitionIds, Is.EqualTo(new[] { OperativeDefinition1 }));
            Assert.That(start.Experience, Is.EqualTo(new[] { 0 }));
            Assert.That(start.Ranks, Is.EqualTo(new[] { 1 }));
            Assert.That(start.Choices.Single(), Is.Empty);
        }

        [Test]
        public void Two_operatives_sent_out_of_order_come_back_in_slot_order()
        {
            var snapshot = LedgerSnapshot("05-two-operatives-defeat.json");
            snapshot.operatives = snapshot.operatives.Reverse().ToArray();
            snapshot.operatives[0].experience = 260;
            snapshot.operatives[0].rank = 3;
            snapshot.operatives[0].choices = new[] { "combat", "ability" };
            Assert.That(snapshot.operatives[0].slot, Is.EqualTo(1), "the fixture really is reversed");
            Assert.That(AttemptStart.TryCreate(AttemptId, Response(snapshot), GoldenCatalogueVersion, KnownOperative, GoldenLedgerCatalogue(), out var start, out var refusal),
                Is.True, refusal);
            Assert.That(start.Snapshot.Select(o => o.Slot), Is.EqualTo(new[] { 0, 1 }));
            Assert.That(start.Snapshot.Select(o => o.OperativeId), Is.EqualTo(new[] { "C", "D" }));
            Assert.That(start.Experience, Is.EqualTo(new[] { 0, 260 }));
            Assert.That(start.Ranks, Is.EqualTo(new[] { 1, 3 }));
            Assert.That(start.Choices[1], Is.EqualTo(new[] { "combat", "ability" }));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("0192f1a4-5c2e-4b1d-9a0e-3f6c1d2b8e41")] // version 4
        [TestCase("0192F1A4-5C2E-7B1D-9A0E-3F6C1D2B8E41")] // not lowercase
        [TestCase("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41\n")]
        public void A_non_canonical_attempt_id_is_bad_attempt_id(string attemptId) =>
            Assert.That(Refusal(attemptId, Response(LedgerSnapshot("01-owned-swap-and-use.json"))), Is.EqualTo("bad_attempt_id"));

        [Test]
        public void A_missing_response_is_bad_response() => Assert.That(Refusal(AttemptId, null), Is.EqualTo("bad_response"));

        [Test]
        public void A_negative_seed_is_bad_response()
        {
            var response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.seed = -1; // the backend stores check (seed >= 0)
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_response"));
            response.seed = 0;
            Assert.That(Refusal(AttemptId, response), Is.Null);
        }

        [TestCase(null)]
        [TestCase("0192f1a5-0000-7000-8000-00000000000")]
        [TestCase("0192F1A5-0000-7000-8000-000000000001")]
        public void A_non_canonical_session_id_is_bad_session_id(string sessionId)
        {
            var response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.processSessionId = sessionId;
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_session_id"));
        }

        [Test]
        public void A_missing_or_malformed_template_is_bad_template()
        {
            var response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.missionTemplate = null;
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_template"));
            response.missionTemplate = new MissionTemplateDto { id = "Mission Standard", version = 1 };
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_template"));
            response.missionTemplate = new MissionTemplateDto { id = TemplateId, version = 0 };
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_template"));
        }

        // Owner ruling (Track L final rulings §2): the only supported template is procedural-standard:1, 1 to 3 operatives.
        [Test]
        public void The_only_supported_template_is_procedural_standard_version_1()
        {
            var t = MissionTemplates.Supported.Single();
            Assert.That((t.Id, t.Version, t.MinOperatives, t.MaxOperatives), Is.EqualTo(("procedural-standard", 1, 1, 3)));
        }

        [Test]
        public void A_template_spec_refuses_impossible_values()
        {
            Assert.Throws<ArgumentException>(() => new MissionTemplateSpec("", 1, 1, 3));
            Assert.Throws<ArgumentException>(() => new MissionTemplateSpec("procedural-standard", 0, 1, 3));
            Assert.Throws<ArgumentException>(() => new MissionTemplateSpec("procedural-standard", 1, 0, 3));
            Assert.Throws<ArgumentException>(() => new MissionTemplateSpec("procedural-standard", 1, 3, 2));
            Assert.Throws<ArgumentException>(() => new MissionTemplateSpec("procedural-standard", 1, 1, AttemptStart.MaxOperatives + 1));
        }

        [TestCase("mission.standard", 1)]
        [TestCase("procedural-standard", 2)]
        [TestCase("procedural-standard-x", 1)]
        public void An_unsupported_template_id_or_version_is_unsupported_template(string id, int version)
        {
            var response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.missionTemplate = new MissionTemplateDto { id = id, version = version };
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("unsupported_template"));
        }

        [Test]
        public void A_malformed_template_is_still_bad_template_first()
        {
            var response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.missionTemplate = new MissionTemplateDto { id = "Not A Template", version = 1 };
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_template"));
        }

        [Test]
        public void A_roster_outside_the_templates_capacity_is_unsupported_squad_size()
        {
            var response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            var one = response.participants[0].loadout.operatives[0];
            response.participants[0].loadout.operatives = Enumerable.Range(0, 4).Select(i => new SnapshotOperativeDto
            {
                slot = i, operativeId = Base58Id.Format(100 + i), definitionId = one.definitionId, experience = 0, rank = 1, choices = new string[0],
                bag = new SnapshotBagDto { instances = new SnapshotInstanceDto[0], stacks = new SnapshotStackDto[0] }, equipment = new EquipmentSlotsDto(),
            }).ToArray();
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("unsupported_squad_size"), "4 operatives; the template takes 1 to 3");
            response.participants[0].loadout.operatives = response.participants[0].loadout.operatives.Take(3).ToArray();
            Assert.That(Refusal(AttemptId, response), Is.Null, "3 fit");
        }

        [Test]
        public void Another_catalogue_in_the_response_or_the_snapshot_is_catalogue_mismatch()
        {
            const string other = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
            var response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.catalogueVersion = other;
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("catalogue_mismatch"));
            response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.participants[0].loadout.catalogueVersion = other;
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("catalogue_mismatch"));
        }

        [Test]
        public void No_participant_or_two_are_unsupported_participants()
        {
            var response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.participants = new RegisterParticipantDto[0];
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("unsupported_participants"));
            response.participants = null;
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("unsupported_participants"));
            var second = Response(LedgerSnapshot("05-two-operatives-defeat.json")).participants[0];
            response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.participants = new[] { response.participants[0], second };
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("unsupported_participants"));
        }

        [Test]
        public void A_snapshot_for_another_account_is_bad_snapshot()
        {
            var response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.participants[0].accountId = "JH";
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_snapshot"));
            response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.participants[0].accountId = "0JG"; // not canonical Base58
            response.participants[0].loadout.accountId = "0JG";
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_snapshot"));
            response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.participants[0].loadout = null;
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_snapshot"));
        }

        [Test]
        public void A_snapshot_the_dto_boundary_refuses_is_bad_snapshot()
        {
            var response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.participants[0].loadout.operatives[0].operativeId = null; // OperativeSnapshot.FromDto throws
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_snapshot"));
            response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.participants[0].loadout.operatives[0] = null;
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_snapshot"));
            response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.participants[0].loadout.operatives[0].experience = -1;
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_snapshot"));
            response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.participants[0].loadout.operatives[0].rank = 0;
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_snapshot"));
            response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.participants[0].loadout.operatives[0].choices = new[] { "combat", null };
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_snapshot"));
        }

        [Test]
        public void A_snapshot_the_ledger_refuses_is_bad_snapshot()
        {
            var response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.participants[0].loadout.operatives[0].bag.instances[0].definitionId = "item.unknown";
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_snapshot"), "an unknown item definition");
            response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.participants[0].loadout.operatives[0].bag.instances[1].itemId = "2k";
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_snapshot"), "an item id repeats");
            response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.participants[0].loadout.operatives[0].equipment.armor = "2k";
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_snapshot"), "a rifle in the armor slot");
            response = Response(LedgerSnapshot("05-two-operatives-defeat.json"));
            response.participants[0].loadout.operatives[1].operativeId = response.participants[0].loadout.operatives[0].operativeId;
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_snapshot"), "an operative id repeats");
        }

        [Test]
        public void An_operative_definition_this_build_lacks_is_unknown_operative_definition()
        {
            var response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            Assert.That(Refusal(AttemptId, response, id => false), Is.EqualTo("unknown_operative_definition"));
            response.participants[0].loadout.operatives[0].definitionId = null;
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("unknown_operative_definition"));
        }

        [Test]
        public void Slots_that_are_not_zero_to_n_minus_one_are_bad_slots()
        {
            var response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.participants[0].loadout.operatives[0].slot = 1;
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_slots"), "a gap at slot 0");
            response = Response(LedgerSnapshot("05-two-operatives-defeat.json"));
            response.participants[0].loadout.operatives[1].slot = 0;
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_slots"), "a repeated slot");
            response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.participants[0].loadout.operatives = new SnapshotOperativeDto[0];
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_slots"), "no operative");
        }

        [Test]
        public void Nine_operatives_are_bad_slots()
        {
            var response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.participants[0].loadout.operatives = Enumerable.Range(0, 9).Select(i => new SnapshotOperativeDto
            {
                slot = i, operativeId = Base58Id.Format(100 + i), definitionId = OperativeDefinition1, experience = 0, rank = 1, choices = new string[0],
                bag = new SnapshotBagDto { instances = new SnapshotInstanceDto[0], stacks = new SnapshotStackDto[0] }, equipment = new EquipmentSlotsDto(),
            }).ToArray();
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_slots"));
        }

        [Test]
        public void The_first_failure_in_order_wins()
        {
            var response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            response.processSessionId = "x";
            response.catalogueVersion = "y";
            response.participants = new RegisterParticipantDto[0];
            Assert.That(Refusal("z", response), Is.EqualTo("bad_attempt_id"));
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("bad_session_id"));
            response.processSessionId = SessionId;
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("catalogue_mismatch"));
            response.catalogueVersion = GoldenCatalogueVersion;
            Assert.That(Refusal(AttemptId, response), Is.EqualTo("unsupported_participants"));
        }

        [Test]
        public void The_start_keeps_its_own_copies()
        {
            var response = Response(LedgerSnapshot("01-owned-swap-and-use.json"));
            Assert.That(AttemptStart.TryCreate(AttemptId, response, GoldenCatalogueVersion, KnownOperative, GoldenLedgerCatalogue(), out var start, out _), Is.True);
            response.participants[0].loadout.operatives[0].choices = new[] { "combat" };
            response.participants[0].loadout.operatives[0].experience = 999;
            response.seed = 1;
            Assert.That(start.Choices[0], Is.Empty);
            Assert.That(start.Experience[0], Is.EqualTo(0));
            Assert.That(start.Seed, Is.EqualTo(Seed));
            Assert.That(start.Choices, Is.InstanceOf<IReadOnlyList<IReadOnlyList<string>>>());
        }
    }
}
