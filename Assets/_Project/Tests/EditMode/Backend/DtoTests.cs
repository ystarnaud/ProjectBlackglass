using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Blackglass.Backend.Core;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// The backend DTOs read and write with JsonUtility exactly as the backend's JSON is shaped. Samples are written in the
    /// backend's member order from the cited sources (ProcessEndpoints, MissionRequests, LoadoutSnapshot.ToJson,
    /// AuthEndpoints, PlayerDtos, AttemptDtos, AdmissionStore).
    /// </summary>
    public sealed class DtoTests
    {
        const string Snapshot = "{\"accountId\":\"JG\",\"inventoryVersion\":3,\"catalogueVersion\":\"sha256:" + Hex64 + "\",\"operatives\":[" +
            "{\"slot\":0,\"operativeId\":\"C\",\"definitionId\":\"00000000-0000-4000-8000-000000000001\",\"experience\":150,\"rank\":2,\"choices\":[\"choice.a\"]," +
            "\"bag\":{\"instances\":[{\"itemId\":\"2k\",\"definitionId\":\"item.alpha-rifle\"}],\"stacks\":[{\"definitionId\":\"item.gamma-kit\",\"quantity\":2}]}," +
            "\"equipment\":{\"weapon\":\"2k\",\"armor\":null,\"utility\":null}}]}";

        const string Hex64 = "5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c";

        static string[] MemberNames(string json) =>
            Regex.Matches(json, "\"([A-Za-z]+)\":").Cast<Match>().Select(m => m.Groups[1].Value).ToArray();

        [Test]
        public void The_register_response_reads()
        {
            var json = "{\"processSessionId\":\"0192f1a5-0000-7000-8000-000000000001\",\"seed\":1592660532,\"missionTemplate\":{\"id\":\"procedural-standard\",\"version\":1}," +
                "\"catalogueVersion\":\"sha256:" + Hex64 + "\",\"participants\":[{\"accountId\":\"JG\",\"loadout\":" + Snapshot + "}],\"deadlines\":{\"launch\":\"2026-10-19T12:00:00Z\"}}";
            var r = JsonUtility.FromJson<SessionRegisterResponse>(json);
            Assert.That(r.processSessionId, Is.EqualTo("0192f1a5-0000-7000-8000-000000000001"));
            Assert.That(r.seed, Is.EqualTo(1592660532));
            Assert.That(r.missionTemplate.id, Is.EqualTo("procedural-standard"));
            Assert.That(r.missionTemplate.version, Is.EqualTo(1));
            Assert.That(r.participants.Single().accountId, Is.EqualTo("JG"));
            Assert.That(r.participants.Single().loadout.operatives.Single().operativeId, Is.EqualTo("C"));
            Assert.That(r.deadlines.launch, Is.EqualTo("2026-10-19T12:00:00Z"));
        }

        [Test]
        public void The_loadout_snapshot_reads_and_an_empty_slot_is_null()
        {
            var s = JsonUtility.FromJson<LoadoutSnapshotDto>(Snapshot);
            Assert.That(s.accountId, Is.EqualTo("JG"));
            Assert.That(s.inventoryVersion, Is.EqualTo(3));
            var o = s.operatives.Single();
            Assert.That(o.slot, Is.EqualTo(0));
            Assert.That(o.experience, Is.EqualTo(150));
            Assert.That(o.rank, Is.EqualTo(2));
            Assert.That(o.choices, Is.EqualTo(new[] { "choice.a" }));
            Assert.That(o.bag.instances.Single().itemId, Is.EqualTo("2k"));
            Assert.That(o.bag.stacks.Single().quantity, Is.EqualTo(2));
            Assert.That(o.equipment.Weapon, Is.EqualTo("2k"));
            // JsonUtility has no null string: a JSON null arrives as "" (pinned here), which the accessor reports as empty (null).
            Assert.That(o.equipment.armor, Is.EqualTo(""));
            Assert.That(o.equipment.Armor, Is.Null);
            Assert.That(o.equipment.Utility, Is.Null);
        }

        [Test]
        public void Ready_start_redeem_and_state_responses_read()
        {
            Assert.That(JsonUtility.FromJson<ReadyResponse>("{\"joinDeadline\":\"2026-10-19T12:01:00Z\"}").joinDeadline, Is.EqualTo("2026-10-19T12:01:00Z"));
            var start = JsonUtility.FromJson<StartResponse>("{\"started\":true,\"startedAt\":\"2026-10-19T12:02:00Z\"}");
            Assert.That(start.started, Is.True);
            Assert.That(start.startedAt, Is.EqualTo("2026-10-19T12:02:00Z"));
            var redeem = JsonUtility.FromJson<RedeemResponse>("{\"accountId\":\"JG\",\"operativeIds\":[\"C\",\"D\"]}");
            Assert.That(redeem.accountId, Is.EqualTo("JG"));
            Assert.That(redeem.operativeIds, Is.EqualTo(new[] { "C", "D" }));
            Assert.That(JsonUtility.FromJson<StateResponse>("{\"state\":\"recorded\"}").state, Is.EqualTo("recorded"));
        }

        [Test]
        public void Requests_write_exactly_the_backends_members()
        {
            Assert.That(MemberNames(JsonUtility.ToJson(new SessionRegisterRequest { buildRevision = "r1", catalogueVersion = "sha256:" + Hex64 })),
                Is.EquivalentTo(new[] { "buildRevision", "catalogueVersion" }));
            var ready = new ReadyRequest
            {
                processSessionId = "s", layoutHash = "cf2e14dece9cc2a6", lootTableVersion = 7, lootHash = "h",
                objectives = new[] { new ReadyObjectiveDto { objectiveId = "extract", required = true } },
                lootPool = new[] { new PoolLineDto { containerIndex = 0, line = 0, definitionId = "item.gamma-kit", quantity = 2 } },
            };
            var readyJson = JsonUtility.ToJson(ready);
            Assert.That(MemberNames(readyJson).Distinct(),
                Is.EquivalentTo(new[] { "processSessionId", "layoutHash", "lootTableVersion", "lootHash", "objectives", "objectiveId", "required", "lootPool", "containerIndex", "line", "definitionId", "quantity" }));
            Assert.That(MemberNames(JsonUtility.ToJson(new StartRequest { processSessionId = "s" })), Is.EquivalentTo(new[] { "processSessionId" }));
            Assert.That(MemberNames(JsonUtility.ToJson(new RedeemRequest { processSessionId = "s", ticket = "t" })), Is.EquivalentTo(new[] { "processSessionId", "ticket" }));
            var heartbeat = new ProcessHeartbeatRequest { processSessionId = "s", phase = "running", participants = new[] { new ParticipantConnectionDto { accountId = "JG", connected = true } } };
            Assert.That(MemberNames(JsonUtility.ToJson(heartbeat)).Distinct(), Is.EquivalentTo(new[] { "processSessionId", "phase", "participants", "accountId", "connected" }));
            Assert.That(MemberNames(JsonUtility.ToJson(new RecordedRequest { manifestHash = "sha256:" + Hex64 })), Is.EquivalentTo(new[] { "manifestHash" }));
            Assert.That(MemberNames(JsonUtility.ToJson(new FailedRequest { processSessionId = "s", reason = "generation_failed" })), Is.EquivalentTo(new[] { "processSessionId", "reason" }));
            Assert.That(MemberNames(JsonUtility.ToJson(new DevLoginRequest { accountKey = "k" })), Is.EquivalentTo(new[] { "accountKey" }));
            Assert.That(MemberNames(JsonUtility.ToJson(new DeployRequest { missionTemplate = "procedural-standard", operativeIds = new[] { "C" } })),
                Is.EquivalentTo(new[] { "missionTemplate", "operativeIds" }));
        }

        [Test]
        public void Client_responses_read()
        {
            var login = JsonUtility.FromJson<DevLoginResponse>("{\"accessToken\":\"opaque\",\"expiresAt\":\"2026-10-19T13:00:00+00:00\",\"accountId\":\"JG\"}");
            Assert.That(login.accountId, Is.EqualTo("JG"));
            Assert.That(login.expiresAt, Is.EqualTo("2026-10-19T13:00:00+00:00"));
            var me = JsonUtility.FromJson<MeResponse>("{\"accountId\":\"JG\",\"inventoryVersion\":4,\"energy\":{\"balance\":40,\"cap\":50,\"nextRegenAt\":\"2026-10-19T13:00:00Z\"}," +
                "\"activeAttemptId\":\"0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41\",\"insurance\":{\"active\":true,\"policyId\":\"standard\",\"expiresAt\":\"2026-10-20T00:00:00Z\"}}");
            Assert.That(me.inventoryVersion, Is.EqualTo(4));
            Assert.That(me.energy.balance, Is.EqualTo(40));
            Assert.That(me.energy.cap, Is.EqualTo(50));
            Assert.That(me.insurance.active, Is.True);
            Assert.That(me.activeAttemptId, Is.EqualTo("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41"));
            var roster = JsonUtility.FromJson<RosterResponse>("{\"version\":5,\"operatives\":[{\"operativeId\":\"C\",\"definitionId\":\"op\",\"definitionKnown\":true,\"experience\":10,\"rank\":1,\"pendingPicks\":0,\"choices\":[]}]}");
            Assert.That(roster.version, Is.EqualTo(5));
            Assert.That(roster.operatives.Single().definitionKnown, Is.True);
            var inventory = JsonUtility.FromJson<InventoryResponse>("{\"version\":6,\"locked\":false,\"stash\":{\"instances\":[{\"itemId\":\"2k\",\"definitionId\":\"item.alpha-rifle\",\"definitionKnown\":true}]," +
                "\"stacks\":[{\"definitionId\":\"item.gamma-kit\",\"definitionKnown\":true,\"quantity\":3}]},\"operatives\":[{\"operativeId\":\"C\",\"bag\":{\"instances\":[],\"stacks\":[]}," +
                "\"equipment\":{\"weapon\":null,\"armor\":\"2m\",\"utility\":null}}]}");
            Assert.That(inventory.stash.stacks.Single().quantity, Is.EqualTo(3));
            Assert.That(inventory.operatives.Single().equipment.Armor, Is.EqualTo("2m"));
            Assert.That(inventory.operatives.Single().equipment.Weapon, Is.Null);
            var deploy = JsonUtility.FromJson<DeployResponse>("{\"attemptId\":\"0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41\",\"state\":\"admitted\",\"energy\":{\"cost\":10,\"balanceAfter\":40}," +
                "\"insurance\":{\"covered\":true,\"policyId\":\"standard\",\"policyVersion\":1}}");
            Assert.That(deploy.energy.balanceAfter, Is.EqualTo(40));
            Assert.That(deploy.insurance.policyVersion, Is.EqualTo(1));
            var ticket = JsonUtility.FromJson<TicketResponse>("{\"ticket\":\"opaque\",\"ticketExpiresAt\":\"2026-10-19T12:05:00Z\"}");
            Assert.That(ticket.ticketExpiresAt, Is.EqualTo("2026-10-19T12:05:00Z"));
        }

        [Test]
        public void A_null_where_JsonUtility_needs_a_value_reads_as_the_default()
        {
            // Pinned: presence can't be read from these DTOs (Stage 16 reads 409 NotProvisioned instead).
            var me = JsonUtility.FromJson<MeResponse>("{\"accountId\":\"JG\",\"inventoryVersion\":null,\"energy\":null,\"activeAttemptId\":null," +
                "\"insurance\":{\"active\":false,\"policyId\":null,\"expiresAt\":null}}");
            Assert.That(me.accountId, Is.EqualTo("JG"));
            Assert.That(me.inventoryVersion, Is.EqualTo(0));
            Assert.That(me.activeAttemptId ?? "", Is.EqualTo(""));
            Assert.That(me.insurance.active, Is.False);
        }

        [Test]
        public void Only_the_register_response_carries_a_seed_and_nothing_carries_a_salt()
        {
            var assembly = typeof(SessionRegisterResponse).Assembly;
            var offenders = assembly.GetTypes()
                .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name.IndexOf("seed", StringComparison.OrdinalIgnoreCase) >= 0 || m.Name.IndexOf("salt", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(m => t.FullName + "." + m.Name))
                .ToList();
            Assert.That(offenders, Is.EqualTo(new[] { typeof(SessionRegisterResponse).FullName + ".seed" }));
        }

        [Test]
        public void No_dto_overrides_ToString()
        {
            var dtos = typeof(SessionRegisterResponse).Assembly.GetTypes().Where(t => t.Namespace == "Blackglass.Backend.Core" && t.IsSerializable && t.IsClass);
            foreach (var t in dtos)
                Assert.That(t.GetMethod("ToString", Type.EmptyTypes).DeclaringType, Is.EqualTo(typeof(object)), t.FullName);
        }
    }
}
