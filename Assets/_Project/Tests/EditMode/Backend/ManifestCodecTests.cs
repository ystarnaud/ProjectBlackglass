using System;
using System.IO;
using System.Linq;
using System.Text;
using Blackglass.Backend.Core;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>
    /// Refusals and orderings the golden vectors don't cover. The syntax-level expectations (BOM, comment, lone surrogate,
    /// nesting 64 and 65) are inferred from System.Text.Json; Backend LedgerScenarioTests asserts the same inputs against
    /// ResultManifest.Parse, so both runtimes pin them.
    /// </summary>
    public sealed class ManifestCodecTests
    {
        static string Vector(string name) =>
            File.ReadAllText(Path.Combine(ContractVectors.ProjectRoot, "Backend", "contracts", "manifest-v1", "valid", name + ".canonical.json"), new UTF8Encoding(false));

        static string Full => Vector("02-full");
        static string Minimal => Vector("01-minimal");

        static string Code(string json) => ManifestCodec.Parse(new UTF8Encoding(false).GetBytes(json)).Code;

        static string Swap(string json, string from, string to)
        {
            Assert.That(json.Contains(from), Is.True, "the test's anchor exists: " + from);
            return json.Replace(from, to);
        }

        /// <summary>The minimal manifest with <paramref name="containers"/> open containers in total (the root object counts).</summary>
        internal static string Nested(int containers) =>
            Minimal.Substring(0, Minimal.Length - 1) + ",\"x\":" + new string('[', containers - 1) + new string(']', containers - 1) + "}";

        [Test]
        public void The_minimal_vector_parses() => Assert.That(Code(Minimal), Is.Null);

        [Test]
        public void A_byte_order_mark_is_NotJson()
        {
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Minimal)).ToArray();
            Assert.That(ManifestCodec.Parse(bytes).Code, Is.EqualTo(ManifestCodes.NotJson));
        }

        [Test]
        public void A_comment_is_NotJson() => Assert.That(Code("{/*x*/" + Minimal.Substring(1)), Is.EqualTo(ManifestCodes.NotJson));

        [Test]
        public void A_lone_surrogate_in_a_read_string_is_NotJson() =>
            Assert.That(Code(Swap(Minimal, "\"buildRevision\":\"r1\"", "\"buildRevision\":\"\\ud800\"")), Is.EqualTo(ManifestCodes.NotJson));

        [Test]
        public void A_lone_surrogate_is_refused_only_where_its_member_is_read()
        {
            var json = Swap(Swap(Minimal, "\"outcome\":\"success\"", "\"outcome\":\"\\udc00\""), "\"formatVersion\":1", "\"formatVersion\":2");
            Assert.That(Code(json), Is.EqualTo(ManifestCodes.UnsupportedFormat));
        }

        [Test]
        public void A_surrogate_pair_escape_is_a_valid_string() =>
            Assert.That(Code(Swap(Minimal, "\"buildRevision\":\"r1\"", "\"buildRevision\":\"\\ud83d\\ude00\"")), Is.EqualTo(ManifestCodes.BadValue));

        [Test]
        public void Nesting_64_is_TooDeep_and_65_is_NotJson()
        {
            Assert.That(Code(Nested(6)), Is.EqualTo(ManifestCodes.TooDeep));
            Assert.That(Code(Nested(64)), Is.EqualTo(ManifestCodes.TooDeep));
            Assert.That(Code(Nested(65)), Is.EqualTo(ManifestCodes.NotJson));
        }

        [Test]
        public void An_equipment_slot_is_read_before_its_item_id() =>
            Assert.That(Code(Swap(Full, "{\"itemId\":\"2m\",\"slot\":\"armor\"}", "{\"itemId\":\"0\",\"slot\":\"x\"}")), Is.EqualTo(ManifestCodes.BadEnum));

        [Test]
        public void A_loot_consumption_reads_its_line_before_its_definition() =>
            Assert.That(Code(Swap(Full, "{\"containerIndex\":0,\"definitionId\":\"item.medkit\",\"line\":0,\"quantity\":1,\"source\":\"loot\"}",
                "{\"containerIndex\":0,\"definitionId\":\"Bad\",\"line\":70000,\"quantity\":1,\"source\":\"loot\"}")), Is.EqualTo(ManifestCodes.OutOfBounds));

        [Test]
        public void Held_items_sort_numerically()
        {
            var json = Swap(Minimal, "\"held\":[]", "\"held\":[{\"itemId\":\"21\",\"origin\":\"owned\"},{\"itemId\":\"z\",\"origin\":\"owned\"}]");
            var text = Encoding.UTF8.GetString(ManifestCodec.Parse(Encoding.UTF8.GetBytes(json)).CanonicalBytes);
            StringAssert.Contains("\"held\":[{\"itemId\":\"z\",\"origin\":\"owned\"},{\"itemId\":\"21\",\"origin\":\"owned\"}]", text);
        }

        [Test]
        public void Equipment_sorts_by_its_slot_word()
        {
            var json = Swap(Full, "\"equipment\":[{\"itemId\":\"2m\",\"slot\":\"armor\"},{\"itemId\":\"2k\",\"slot\":\"weapon\"}]",
                "\"equipment\":[{\"itemId\":\"2k\",\"slot\":\"weapon\"},{\"itemId\":\"2m\",\"slot\":\"armor\"}]");
            Assert.That(Encoding.UTF8.GetString(ManifestCodec.Parse(Encoding.UTF8.GetBytes(json)).CanonicalBytes), Is.EqualTo(Full));
        }

        [TestCase("[]", ManifestCodes.WrongType)]
        [TestCase("\"x\"", ManifestCodes.WrongType)]
        [TestCase("", ManifestCodes.NotJson)]
        [TestCase(" ", ManifestCodes.NotJson)]
        [TestCase("{} {}", ManifestCodes.NotJson)]
        [TestCase("{\"a\":1,}", ManifestCodes.NotJson)]
        [TestCase("{\"a\":01}", ManifestCodes.NotJson)]
        [TestCase("{\"a\":\"\t\"}", ManifestCodes.NotJson)]
        [TestCase("{\"a\":\"\\x\"}", ManifestCodes.NotJson)]
        [TestCase("{\"a\":tru}", ManifestCodes.NotJson)]
        [TestCase("{\"a\":-}", ManifestCodes.NotJson)]
        [TestCase("{\"a\":1.}", ManifestCodes.NotJson)]
        [TestCase("{'a':1}", ManifestCodes.NotJson)]
        public void Syntax_and_root_kind(string json, string code) => Assert.That(Code(json), Is.EqualTo(code));

        [Test]
        public void Invalid_utf8_is_NotJson()
        {
            var bytes = Encoding.UTF8.GetBytes(Swap(Minimal, "\"r1\"", "\"r\u00e91\"")).ToList();
            var at = bytes.IndexOf(0xC3);
            bytes[at + 1] = 0x28; // a broken continuation byte
            Assert.That(ManifestCodec.Parse(bytes.ToArray()).Code, Is.EqualTo(ManifestCodes.NotJson));
            var surrogate = Encoding.UTF8.GetBytes(Minimal).ToList();
            surrogate.InsertRange(surrogate.IndexOf((byte)'r') + 1, new byte[] { 0xED, 0xA0, 0x80 }); // an encoded surrogate
            Assert.That(ManifestCodec.Parse(surrogate.ToArray()).Code, Is.EqualTo(ManifestCodes.NotJson));
            var overlong = Encoding.UTF8.GetBytes(Minimal).ToList();
            overlong.InsertRange(overlong.IndexOf((byte)'r') + 1, new byte[] { 0xC0, 0xAF });
            Assert.That(ManifestCodec.Parse(overlong.ToArray()).Code, Is.EqualTo(ManifestCodes.NotJson));
        }

        [Test]
        public void Format_version()
        {
            Assert.That(Code(Swap(Minimal, "\"formatVersion\":1", "\"formatVersion\":2")), Is.EqualTo(ManifestCodes.UnsupportedFormat));
            Assert.That(Code(Swap(Minimal, "\"formatVersion\":1", "\"formatVersion\":\"1\"")), Is.EqualTo(ManifestCodes.WrongType));
        }

        [Test]
        public void Number_bounds()
        {
            Assert.That(Code(Swap(Minimal, "\"outcomeSequence\":8812", "\"outcomeSequence\":10000000000")), Is.EqualTo(ManifestCodes.OutOfBounds));
            Assert.That(Code(Swap(Minimal, "\"serverElapsedMs\":1200000", "\"serverElapsedMs\":86400001")), Is.EqualTo(ManifestCodes.OutOfBounds));
            Assert.That(Code(Swap(Minimal, "\"serverElapsedMs\":1200000", "\"serverElapsedMs\":86400000")), Is.Null);
            Assert.That(Code(Swap(Minimal, "\"lootTableVersion\":1", "\"lootTableVersion\":0")), Is.EqualTo(ManifestCodes.OutOfBounds));
            Assert.That(Code(Swap(Minimal, "\"slot\":0", "\"slot\":8")), Is.EqualTo(ManifestCodes.OutOfBounds));
        }

        [Test]
        public void List_bounds()
        {
            var operatives = Minimal.Substring(Minimal.IndexOf("[{\"alive\"", StringComparison.Ordinal));
            operatives = operatives.Substring(0, operatives.IndexOf("}]", StringComparison.Ordinal) + 2);
            Assert.That(Code(Swap(Minimal, operatives, "[]")), Is.EqualTo(ManifestCodes.OutOfBounds));
            var held = string.Join(",", Enumerable.Range(1, 257).Select(i => "{\"itemId\":\"" + Base58Id.Format(i) + "\",\"origin\":\"owned\"}"));
            Assert.That(Code(Swap(Minimal, "\"held\":[]", "\"held\":[" + held + "]")), Is.EqualTo(ManifestCodes.OutOfBounds));
        }

        [Test]
        public void Strings_are_bounded_in_utf8_bytes()
        {
            Assert.That(Code(Swap(Minimal, "\"r1\"", "\"" + new string('\u00e9', 65) + "\"")), Is.EqualTo(ManifestCodes.OutOfBounds));
            Assert.That(Code(Swap(Minimal, "\"r1\"", "\"" + new string('\u00e9', 64) + "\"")), Is.EqualTo(ManifestCodes.BadValue));
        }

        [Test]
        public void Entry_shapes()
        {
            Assert.That(Code(Swap(Full, "{\"definitionId\":\"item.medkit\",\"origin\":\"owned\",\"quantity\":2}",
                "{\"definitionId\":\"item.medkit\",\"itemId\":\"2\",\"origin\":\"owned\",\"quantity\":2}")), Is.EqualTo(ManifestCodes.UnknownMember));
            Assert.That(Code(Swap(Full, "{\"definitionId\":\"item.medkit\",\"quantity\":1,\"source\":\"owned\"}",
                "{\"definitionId\":\"item.medkit\",\"quantity\":1}")), Is.EqualTo(ManifestCodes.MissingMember));
        }

        [Test]
        public void Duplicates_within_lists()
        {
            Assert.That(Code(Swap(Full, "{\"containerIndex\":2,\"line\":0,\"slot\":\"utility\"}", "{\"containerIndex\":2,\"line\":0,\"slot\":\"weapon\"}")),
                Is.EqualTo(ManifestCodes.DuplicateEntry));
            Assert.That(Code(Swap(Full, "\"operativeId\":\"D\"", "\"operativeId\":\"C\"")), Is.EqualTo(ManifestCodes.DuplicateEntry));
        }

        [Test]
        public void A_model_built_in_code_writes_02_full()
        {
            var c = new OperativeFact(Id("C"), 0, true,
                new[] { new OwnedUse("item.medkit", 1) },
                new[] { new LootUse(0, 0, "item.medkit", 1) },
                new[] { Id("2m"), Id("2k") },
                new[] { new OwnedStackFact("item.medkit", 2) },
                new[] { new LootLotFact(0, 1, 1), new LootLotFact(0, 0, 1) },
                new[] { new LineCount(0, 1, 1), new LineCount(0, 0, 2) },
                new[] { EquipmentFact.Item(EquipSlot.Weapon, Id("2k")), EquipmentFact.Item(EquipSlot.Armor, Id("2m")) });
            var d = new OperativeFact(Id("D"), 1, false, new OwnedUse[0], new LootUse[0], new[] { Id("2n") }, new OwnedStackFact[0],
                new[] { new LootLotFact(2, 0, 1) }, new[] { new LineCount(2, 0, 1) },
                new[] { EquipmentFact.Item(EquipSlot.Weapon, Id("2n")), EquipmentFact.Loot(EquipSlot.Utility, 2, 0) });
            var m = new ResultManifestV1("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", Id("JG"), "0192f1a5-0000-7000-8000-000000000001", "r1",
                "sha256:" + string.Concat(Enumerable.Repeat("5f2c", 16)), ManifestIntegrity.Ok, ManifestOutcome.Success, 8812, 1200000,
                "cf2e14dece9cc2a6", 1, "sha256:" + string.Concat(Enumerable.Repeat("a1b2", 16)),
                new[] { new ObjectiveFact("intel", ObjectiveStateKind.Failed), new ObjectiveFact("extract", ObjectiveStateKind.Completed) },
                new[] { d, c });
            Assert.That(Encoding.UTF8.GetString(ManifestCodec.ToCanonicalBytes(m)), Is.EqualTo(Full));
        }

        [Test]
        public void HashOf_empty_is_the_well_known_digest() =>
            Assert.That(ManifestCodec.HashOf(new byte[0]), Is.EqualTo("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));

        [Test]
        public void Parse_never_throws_on_garbage()
        {
            var random = new Random(7);
            for (var i = 0; i < 2000; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(Full);
                for (var j = 0; j < 1 + random.Next(4); j++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                var result = ManifestCodec.Parse(bytes);
                Assert.That(result.Ok || ManifestCodes.All.Contains(result.Code), Is.True);
            }
        }

        static long Id(string text)
        {
            Assert.That(Base58Id.TryParse(text, out var id), Is.True);
            return id;
        }
    }
}
