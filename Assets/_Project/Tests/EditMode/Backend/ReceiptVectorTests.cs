using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Blackglass.Backend.Core;
using NUnit.Framework;
using static Blackglass.Tests.ContractVectors;

namespace Blackglass.Tests
{
    /// <summary>
    /// The three receipt-v1 vectors (Backend/contracts/manifest-v1/receipt) read with ReceiptV1.TryRead and compared with their
    /// input values. Retention vectors are never read by Unity (owner ruling D4: retention is the backend's, with a salt the
    /// mission process never sees).
    /// </summary>
    public sealed class ReceiptVectorTests
    {
        static string Folder => Path.Combine(ProjectRoot, "Backend", "contracts", "manifest-v1", "receipt");

        [TestCase("01-success")]
        [TestCase("02-insured-defeat")]
        [TestCase("03-void")]
        public void Each_receipt_reads_as_its_input_says(string name)
        {
            Assert.That(ReceiptV1.TryRead(File.ReadAllBytes(Path.Combine(Folder, name + ".receipt.json")), out var r), Is.True);
            var input = Load("manifest-v1", "receipt", name + ".input.json");
            Assert.That(r.ReceiptVersion, Is.EqualTo(1));
            Assert.That(r.AttemptId, Is.EqualTo(Str(input["attemptId"])));
            Assert.That(r.AccountId, Is.EqualTo(Str(input["accountId"])));
            Assert.That(r.Outcome, Is.EqualTo(Str(input["outcome"])));
            Assert.That(r.SettledAt, Is.EqualTo(Str(input["settledAt"])));
            Assert.That(r.CatalogueVersion, Is.EqualTo(Str(input["catalogueVersion"])));
            Assert.That(r.InventoryVersion, Is.EqualTo((long)input["inventoryVersion"]));
            var energy = Obj(input["energy"]);
            Assert.That(r.Energy.Cost, Is.EqualTo(Int(energy["cost"])));
            Assert.That(r.Energy.Refunded, Is.EqualTo(Int(energy["refunded"])));
            Assert.That(r.Energy.State, Is.EqualTo(Str(energy["state"])));
            Assert.That(r.Consumed.Select(c => c.OperativeId + "/" + c.DefinitionId + "/" + c.Quantity),
                Is.EquivalentTo(Arr(input["consumed"]).Select(Obj).Select(c => Str(c["operativeId"]) + "/" + Str(c["definitionId"]) + "/" + Int(c["quantity"]))));
            Assert.That(r.Granted.Select(g => g.OperativeId + "/" + g.DefinitionId + "/" + g.Quantity + "/" + string.Join(",", g.ItemIds.OrderBy(i => i))),
                Is.EquivalentTo(Arr(input["granted"]).Select(Obj).Select(g => Str(g["operativeId"]) + "/" + Str(g["definitionId"]) + "/" + Int(g["quantity"]) + "/" +
                    string.Join(",", Arr(g["itemIds"]).Select(Str).OrderBy(i => i)))));
            Assert.That(r.Xp.Select(x => x.OperativeId + "/" + x.Amount),
                Is.EquivalentTo(Arr(input["xp"]).Select(Obj).Select(x => Str(x["operativeId"]) + "/" + Int(x["amount"]))));
            Assert.That(r.Operatives.Select(o => o.OperativeId + "/" + (o.Alive.HasValue ? o.Alive.Value.ToString() : "null")),
                Is.EquivalentTo(Arr(input["operatives"]).Select(Obj).Select(o => Str(o["operativeId"]) + "/" + (o["alive"] == null ? "null" : Bool(o["alive"]).ToString()))));
            var insurance = Obj(input["insurance"]);
            Assert.That(r.Insurance.Covered, Is.EqualTo(Bool(insurance["covered"])));
            if (r.Insurance.Covered)
            {
                Assert.That(r.Insurance.Applied, Is.EqualTo(Bool(insurance["applied"])));
                Assert.That(r.Insurance.PolicyId, Is.EqualTo(Str(insurance["policyId"])));
                Assert.That(r.Insurance.PolicyVersion, Is.EqualTo(Int(insurance["policyVersion"])));
                Assert.That(r.Insurance.Refunds.Select(x => x.DefinitionId + "/" + x.Quantity),
                    Is.EquivalentTo(Arr(insurance["refunds"]).Select(Obj).Select(x => Str(x["definitionId"]) + "/" + Int(x["quantity"]))));
                Assert.That(r.Insurance.Retained.Select(x => x.DefinitionId + "/" + x.Quantity + "/" + string.Join(",", x.ItemIds)),
                    Is.EquivalentTo(Arr(insurance["retained"]).Select(Obj).Select(x => Str(x["definitionId"]) + "/" + Int(x["quantity"]) + "/" + string.Join(",", Arr(x["itemIds"]).Select(Str)))));
                Assert.That(r.Insurance.Xp.Select(x => x.OperativeId + "/" + x.Amount),
                    Is.EquivalentTo(Arr(insurance["xp"]).Select(Obj).Select(x => Str(x["operativeId"]) + "/" + Int(x["amount"]))));
            }
        }

        [Test]
        public void The_void_reads_alive_as_null_and_the_insured_defeat_as_applied()
        {
            ReceiptV1.TryRead(File.ReadAllBytes(Path.Combine(Folder, "03-void.receipt.json")), out var v);
            Assert.That(v.Outcome, Is.EqualTo("infra_void"));
            Assert.That(v.Operatives.All(o => !o.Alive.HasValue), Is.True);
            Assert.That(v.Insurance.Covered, Is.False);
            ReceiptV1.TryRead(File.ReadAllBytes(Path.Combine(Folder, "02-insured-defeat.receipt.json")), out var d);
            Assert.That(d.Insurance.Applied, Is.True);
            Assert.That(d.Insurance.Retained.Count, Is.EqualTo(2));
        }

        static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

        [Test]
        public void Refuses_another_version_a_missing_member_and_bad_json()
        {
            var text = File.ReadAllText(Path.Combine(Folder, "03-void.receipt.json"));
            Assert.That(ReceiptV1.TryRead(Utf8(text.Replace("\"receiptVersion\":1", "\"receiptVersion\":2")), out _), Is.False);
            Assert.That(ReceiptV1.TryRead(Utf8(text.Replace("\"outcome\":\"infra_void\",", "")), out _), Is.False);
            Assert.That(ReceiptV1.TryRead(Utf8(text.Substring(1)), out _), Is.False);
            Assert.That(ReceiptV1.TryRead(Utf8(text.Replace("\"insurance\":{\"covered\":false}", "\"insurance\":{\"covered\":true}")), out _), Is.False);
        }

        [Test]
        public void Tolerates_an_unknown_member()
        {
            var text = File.ReadAllText(Path.Combine(Folder, "03-void.receipt.json"));
            Assert.That(ReceiptV1.TryRead(Utf8(text.Replace("{\"accountId\"", "{\"addedLater\":[1,{\"x\":null}],\"accountId\"")), out var r), Is.True);
            Assert.That(r.AccountId, Is.EqualTo("JG"));
        }

        [Test]
        public void Unity_reads_no_retention_vector()
        {
            var sources = Directory.GetFiles(Path.Combine(ProjectRoot, "Assets", "_Project"), "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.EndsWith("ReceiptVectorTests.cs") && File.ReadAllText(p).Contains("retention"));
            foreach (var path in sources)
                StringAssert.DoesNotContain("manifest-v1\", \"retention", File.ReadAllText(path), path);
        }
    }
}
