using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Blackglass.Backend.Core
{
    /// <summary>The outcome of ManifestCodec.Parse: a manifest with its canonical bytes and hash, or the first refusal code.</summary>
    public sealed class ManifestParseResult
    {
        ManifestParseResult(ResultManifestV1 manifest, byte[] canonical, string hash, string code)
        {
            Manifest = manifest;
            CanonicalBytes = canonical;
            ManifestHash = hash;
            Code = code;
        }

        internal static ManifestParseResult Parsed(ResultManifestV1 manifest, byte[] canonical) =>
            new ManifestParseResult(manifest, canonical, ManifestCodec.HashOf(canonical), null);

        internal static ManifestParseResult Refused(string code) => new ManifestParseResult(null, null, null, code);

        public bool Ok => Code == null;
        public ResultManifestV1 Manifest { get; }
        public byte[] CanonicalBytes { get; }
        public string ManifestHash { get; }
        public string Code { get; }
    }

    /// <summary>
    /// Format v1's one reader and one writer, a statement-by-statement port of Backend ResultManifest.Parse and
    /// ToCanonicalBytes: the first refusal met, in the backend's exact order (size, UTF-8 and JSON syntax, depth, then the
    /// structure: per object duplicate keys, unknown members, missing members; formatVersion first, then each root member in
    /// ordinal key order; inside an entry, members in the backend's constructor order).
    ///
    /// **Format validation only.** A successful Parse proves the bytes are a format-v1 manifest and gives its canonical form.
    /// It says nothing about the admission snapshot, the loot pool, conservation, the objectives or any other settlement rule
    /// (V4-V15): only the backend's ManifestValidator judges those, and no Unity code may treat a parse as acceptance.
    /// </summary>
    public static class ManifestCodec
    {
        static readonly string[] RootMembers =
        {
            "accountId", "attemptId", "buildRevision", "catalogueVersion", "formatVersion", "integrity", "layoutHash", "lootHash", "lootTableVersion",
            "objectives", "operatives", "outcome", "outcomeSequence", "processSessionId", "serverElapsedMs",
        };

        sealed class Refusal : Exception
        {
            public Refusal(string code) : base(code) => Code = code;

            public string Code { get; }
        }

        static Refusal Refuse(string code) => new Refusal(code);

        /// <summary>Never throws on bad input.</summary>
        public static ManifestParseResult Parse(byte[] raw)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));
            if (raw.Length > ResultManifestV1.MaxBytes) return ManifestParseResult.Refused(ManifestCodes.TooLarge);
            if (!StrictJsonReader.TryRead(raw, out var root)) return ManifestParseResult.Refused(ManifestCodes.NotJson);
            try
            {
                CheckDepth(root, 0);
                var manifest = ReadRoot(root);
                return ManifestParseResult.Parsed(manifest, ToCanonicalBytes(manifest));
            }
            catch (Refusal r) { return ManifestParseResult.Refused(r.Code); }
        }

        /// <summary>"sha256:" and the lowercase hex SHA-256 of <paramref name="bytes"/>.</summary>
        public static string HashOf(byte[] bytes)
        {
            byte[] digest;
            using (var sha = SHA256.Create()) digest = sha.ComputeHash(bytes);
            var text = new StringBuilder("sha256:", 71);
            foreach (var b in digest) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        static void CheckDepth(JsonNode e, int depth)
        {
            if (e.Kind != JsonKind.Object && e.Kind != JsonKind.Array) return;
            if (depth > ResultManifestV1.MaxDepthBelowRoot) throw Refuse(ManifestCodes.TooDeep);
            if (e is JsonObject o)
                foreach (var m in o.Members) CheckDepth(m.Value, depth + 1);
            else
                foreach (var item in ((JsonArray)e).Items) CheckDepth(item, depth + 1);
        }

        static ResultManifestV1 ReadRoot(JsonNode root)
        {
            var m = Members(root, RootMembers);
            if (Int(m["formatVersion"], 0, int.MaxValue) != ResultManifestV1.FormatVersion) throw Refuse(ManifestCodes.UnsupportedFormat);
            var account = Base58(m["accountId"]);
            var attempt = Str(m["attemptId"]);
            if (!CanonicalIds.IsAttemptId(attempt)) throw Refuse(ManifestCodes.NonCanonicalId);
            var build = Str(m["buildRevision"]);
            if (!CanonicalIds.IsBuildRevision(build)) throw Refuse(ManifestCodes.BadValue);
            var catalogue = Hash(m["catalogueVersion"]);
            var integrityWord = OneOf(m["integrity"], "ok", "ledgerMismatch");
            var layout = Str(m["layoutHash"]);
            if (!CanonicalIds.IsLayoutHash(layout)) throw Refuse(ManifestCodes.NonCanonicalId);
            var lootHash = Hash(m["lootHash"]);
            var tableVersion = Int(m["lootTableVersion"], 1, int.MaxValue);
            var objectives = Distinct(ListOf(m["objectives"], 0, ResultManifestV1.MaxObjectives, ReadObjective), o => o.ObjectiveId);
            var operatives = ListOf(m["operatives"], 1, ResultManifestV1.MaxOperatives, ReadOperative);
            Distinct(operatives, o => o.Slot);
            Distinct(operatives, o => o.OperativeId);
            var outcomeText = Str(m["outcome"]);
            if (outcomeText == ManifestWords.InfraVoid) throw Refuse(ManifestCodes.BackendOnlyOutcome);
            if (!ManifestWords.TryOutcome(outcomeText, out var outcome)) throw Refuse(ManifestCodes.BadEnum);
            var sequence = Int(m["outcomeSequence"], 0, int.MaxValue);
            var session = Str(m["processSessionId"]);
            if (!CanonicalIds.IsUuid(session)) throw Refuse(ManifestCodes.NonCanonicalId);
            var elapsed = Int(m["serverElapsedMs"], 0, ResultManifestV1.MaxElapsedMs);
            ManifestWords.TryIntegrity(integrityWord, out var integrity);
            return new ResultManifestV1(attempt, account, session, build, catalogue, integrity, outcome, sequence, elapsed, layout, tableVersion, lootHash,
                objectives, operatives);
        }

        static ObjectiveFact ReadObjective(JsonNode e)
        {
            var o = Members(e, "objectiveId", "state");
            var id = Str(o["objectiveId"]);
            if (!CanonicalIds.IsWord(id, ResultManifestV1.MaxObjectiveIdLength)) throw Refuse(ManifestCodes.BadValue);
            ManifestWords.TryState(OneOf(o["state"], "active", "completed", "failed"), out var state);
            return new ObjectiveFact(id, state);
        }

        static OperativeFact ReadOperative(JsonNode e)
        {
            var o = Members(e, "alive", "collected", "consumed", "equipment", "held", "operativeId", "slot");
            var alive = Bool(o["alive"]);
            var collected = Distinct(ListOf(o["collected"], 0, ResultManifestV1.MaxEntries, c =>
            {
                var l = Members(c, "containerIndex", "line", "quantity");
                var ci = Index(l["containerIndex"]);
                var line = Index(l["line"]);
                return new LineCount(ci, line, Quantity(l["quantity"]));
            }), c => (c.ContainerIndex, c.Line));
            var consumedOwned = new List<OwnedUse>();
            var consumedLoot = new List<LootUse>();
            foreach (var item in ListOf(o["consumed"], 0, ResultManifestV1.MaxEntries, c => c))
            {
                var raw = RawMembers(item);
                var source = OneOf(Required(raw, "source"), "loot", "owned");
                if (source == "loot")
                {
                    Exact(raw, "containerIndex", "definitionId", "line", "quantity", "source");
                    var ci = Index(raw["containerIndex"]);
                    var line = Index(raw["line"]);
                    var definition = Definition(raw["definitionId"]);
                    consumedLoot.Add(new LootUse(ci, line, definition, Quantity(raw["quantity"])));
                }
                else
                {
                    Exact(raw, "definitionId", "quantity", "source");
                    var definition = Definition(raw["definitionId"]);
                    consumedOwned.Add(new OwnedUse(definition, Quantity(raw["quantity"])));
                }
            }
            Distinct(consumedLoot, c => (c.ContainerIndex, c.Line));
            Distinct(consumedOwned, c => c.DefinitionId);
            var equipment = Distinct(ListOf(o["equipment"], 0, ResultManifestV1.MaxEntries, c =>
            {
                var raw = RawMembers(c);
                if (raw.ContainsKey("itemId"))
                {
                    Exact(raw, "itemId", "slot");
                    var itemSlot = Slot(raw["slot"]);
                    return EquipmentFact.Item(itemSlot, Base58(raw["itemId"]));
                }
                Exact(raw, "containerIndex", "line", "slot");
                var lootSlot = Slot(raw["slot"]);
                var ci = Index(raw["containerIndex"]);
                return EquipmentFact.Loot(lootSlot, ci, Index(raw["line"]));
            }), r => r.Slot);
            var heldItems = new List<long>();
            var heldStacks = new List<OwnedStackFact>();
            var heldLoot = new List<LootLotFact>();
            foreach (var item in ListOf(o["held"], 0, ResultManifestV1.MaxEntries, c => c))
            {
                var raw = RawMembers(item);
                var origin = OneOf(Required(raw, "origin"), "loot", "owned");
                if (origin == "loot")
                {
                    Exact(raw, "containerIndex", "line", "origin", "quantity");
                    var ci = Index(raw["containerIndex"]);
                    var line = Index(raw["line"]);
                    heldLoot.Add(new LootLotFact(ci, line, Quantity(raw["quantity"])));
                }
                else if (raw.ContainsKey("itemId"))
                {
                    Exact(raw, "itemId", "origin");
                    heldItems.Add(Base58(raw["itemId"]));
                }
                else
                {
                    Exact(raw, "definitionId", "origin", "quantity");
                    var definition = Definition(raw["definitionId"]);
                    heldStacks.Add(new OwnedStackFact(definition, Quantity(raw["quantity"])));
                }
            }
            Distinct(heldLoot, h => (h.ContainerIndex, h.Line));
            Distinct(heldItems, h => h);
            Distinct(heldStacks, h => h.DefinitionId);
            var operativeId = Base58(o["operativeId"]);
            var slot = Int(o["slot"], 0, ResultManifestV1.MaxOperatives - 1);
            return new OperativeFact(operativeId, slot, alive, consumedOwned, consumedLoot, heldItems, heldStacks, heldLoot, collected, equipment);
        }

        // ---- the canonical writer ----

        /// <summary>The one canonical encoding: keys in ordinal order, every list in its stated order, no whitespace.</summary>
        public static byte[] ToCanonicalBytes(ResultManifestV1 m)
        {
            if (m == null) throw new ArgumentNullException(nameof(m));
            return CanonicalJsonWriter.ToBytes(JsonObject.Of(
                ("accountId", S(Base58Id.Format(m.AccountId))),
                ("attemptId", S(m.AttemptId)),
                ("buildRevision", S(m.BuildRevision)),
                ("catalogueVersion", S(m.CatalogueVersion)),
                ("formatVersion", N(ResultManifestV1.FormatVersion)),
                ("integrity", S(ManifestWords.Of(m.Integrity))),
                ("layoutHash", S(m.LayoutHash)),
                ("lootHash", S(m.LootHash)),
                ("lootTableVersion", N(m.LootTableVersion)),
                ("objectives", new JsonArray(m.Objectives.OrderBy(o => o.ObjectiveId, StringComparer.Ordinal)
                    .Select(o => (JsonNode)JsonObject.Of(("objectiveId", S(o.ObjectiveId)), ("state", S(ManifestWords.Of(o.State))))))),
                ("operatives", new JsonArray(m.Operatives.OrderBy(o => o.Slot).Select(o => (JsonNode)WriteOperative(o)))),
                ("outcome", S(ManifestWords.Of(m.Outcome))),
                ("outcomeSequence", N(m.OutcomeSequence)),
                ("processSessionId", S(m.ProcessSessionId)),
                ("serverElapsedMs", N(m.ServerElapsedMs))));
        }

        static JsonObject WriteOperative(OperativeFact o)
        {
            var consumed = o.ConsumedLoot.OrderBy(c => c.ContainerIndex).ThenBy(c => c.Line)
                .Select(c => (JsonNode)JsonObject.Of(("containerIndex", N(c.ContainerIndex)), ("definitionId", S(c.DefinitionId)), ("line", N(c.Line)),
                    ("quantity", N(c.Quantity)), ("source", S("loot"))))
                .Concat(o.ConsumedOwned.OrderBy(c => c.DefinitionId, StringComparer.Ordinal)
                    .Select(c => (JsonNode)JsonObject.Of(("definitionId", S(c.DefinitionId)), ("quantity", N(c.Quantity)), ("source", S("owned")))));
            var equipment = o.Equipment.OrderBy(e => ManifestWords.Of(e.Slot), StringComparer.Ordinal)
                .Select(e => (JsonNode)(e.ItemId.HasValue
                    ? JsonObject.Of(("itemId", S(Base58Id.Format(e.ItemId.Value))), ("slot", S(ManifestWords.Of(e.Slot))))
                    : JsonObject.Of(("containerIndex", N(e.ContainerIndex.Value)), ("line", N(e.Line.Value)), ("slot", S(ManifestWords.Of(e.Slot))))));
            var held = o.HeldLoot.OrderBy(h => h.ContainerIndex).ThenBy(h => h.Line)
                .Select(h => (JsonNode)JsonObject.Of(("containerIndex", N(h.ContainerIndex)), ("line", N(h.Line)), ("origin", S("loot")), ("quantity", N(h.Quantity))))
                .Concat(o.HeldItems.OrderBy(id => id).Select(id => (JsonNode)JsonObject.Of(("itemId", S(Base58Id.Format(id))), ("origin", S("owned")))))
                .Concat(o.HeldStacks.OrderBy(h => h.DefinitionId, StringComparer.Ordinal)
                    .Select(h => (JsonNode)JsonObject.Of(("definitionId", S(h.DefinitionId)), ("origin", S("owned")), ("quantity", N(h.Quantity)))));
            return JsonObject.Of(
                ("alive", JsonBool.Of(o.Alive)),
                ("collected", new JsonArray(o.Collected.OrderBy(c => c.ContainerIndex).ThenBy(c => c.Line)
                    .Select(c => (JsonNode)JsonObject.Of(("containerIndex", N(c.ContainerIndex)), ("line", N(c.Line)), ("quantity", N(c.Quantity)))))),
                ("consumed", new JsonArray(consumed)),
                ("equipment", new JsonArray(equipment)),
                ("held", new JsonArray(held)),
                ("operativeId", S(Base58Id.Format(o.OperativeId))),
                ("slot", N(o.Slot)));
        }

        static JsonNode S(string value) => new JsonString(value);

        static JsonNode N(long value) => JsonNumber.FromInt(value);

        // ---- strict readers; each refusal carries its code ----

        static Dictionary<string, JsonNode> RawMembers(JsonNode e)
        {
            if (!(e is JsonObject o)) throw Refuse(ManifestCodes.WrongType);
            var found = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            foreach (var p in o.Members)
            {
                if (p.NameIsInvalidUtf16) throw Refuse(ManifestCodes.NotJson); // where the backend's JsonProperty.Name throws
                if (found.ContainsKey(p.Name)) throw Refuse(ManifestCodes.DuplicateKey);
                found.Add(p.Name, p.Value);
            }
            return found;
        }

        static void Exact(Dictionary<string, JsonNode> found, params string[] names)
        {
            if (found.Keys.Any(k => Array.IndexOf(names, k) < 0)) throw Refuse(ManifestCodes.UnknownMember);
            if (names.Any(n => !found.ContainsKey(n))) throw Refuse(ManifestCodes.MissingMember);
        }

        static Dictionary<string, JsonNode> Members(JsonNode e, params string[] names)
        {
            var found = RawMembers(e);
            Exact(found, names);
            return found;
        }

        static JsonNode Required(Dictionary<string, JsonNode> found, string name) =>
            found.TryGetValue(name, out var value) ? value : throw Refuse(ManifestCodes.MissingMember);

        static string Str(JsonNode e)
        {
            if (!(e is JsonString s)) throw Refuse(ManifestCodes.WrongType);
            if (s.IsInvalidUtf16) throw Refuse(ManifestCodes.NotJson); // where the backend's GetString() throws
            return StrictJsonReader.Utf8ByteCount(s.Value) <= ResultManifestV1.MaxStringBytes ? s.Value : throw Refuse(ManifestCodes.OutOfBounds);
        }

        static int Int(JsonNode e, int min, int max)
        {
            if (!(e is JsonNumber n)) throw Refuse(ManifestCodes.WrongType);
            var raw = n.RawText;
            if (!IsIntegerLiteral(raw)) throw Refuse(ManifestCodes.NotInteger);
            if (raw.Length > 10 || !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
                throw Refuse(ManifestCodes.OutOfBounds);
            return (int)v;
        }

        /// <summary>^(?:0|[1-9][0-9]*)\z</summary>
        static bool IsIntegerLiteral(string raw)
        {
            if (raw.Length == 0) return false;
            if (raw == "0") return true;
            if (raw[0] < '1' || raw[0] > '9') return false;
            foreach (var c in raw)
                if (c < '0' || c > '9') return false;
            return true;
        }

        static int Index(JsonNode e) => Int(e, 0, ResultManifestV1.MaxContainerOrLine);

        static int Quantity(JsonNode e) => Int(e, 1, int.MaxValue);

        static bool Bool(JsonNode e) => e is JsonBool b ? b.Value : throw Refuse(ManifestCodes.WrongType);

        static string OneOf(JsonNode e, params string[] allowed)
        {
            var s = Str(e);
            return Array.IndexOf(allowed, s) >= 0 ? s : throw Refuse(ManifestCodes.BadEnum);
        }

        static EquipSlot Slot(JsonNode e)
        {
            ManifestWords.TrySlot(OneOf(e, "armor", "utility", "weapon"), out var slot);
            return slot;
        }

        static string Hash(JsonNode e)
        {
            var s = Str(e);
            return CanonicalIds.IsSha256(s) ? s : throw Refuse(ManifestCodes.NonCanonicalId);
        }

        static long Base58(JsonNode e) => Base58Id.TryParse(Str(e), out var id) ? id : throw Refuse(ManifestCodes.NonCanonicalId);

        static string Definition(JsonNode e)
        {
            var s = Str(e);
            return CanonicalIds.IsDefinitionId(s) ? s : throw Refuse(ManifestCodes.BadValue);
        }

        static List<T> ListOf<T>(JsonNode e, int min, int max, Func<JsonNode, T> read)
        {
            if (!(e is JsonArray a)) throw Refuse(ManifestCodes.WrongType);
            if (a.Items.Count < min || a.Items.Count > max) throw Refuse(ManifestCodes.OutOfBounds);
            var list = new List<T>(a.Items.Count);
            foreach (var item in a.Items) list.Add(read(item));
            return list;
        }

        static List<T> Distinct<T, TKey>(List<T> list, Func<T, TKey> key)
        {
            var seen = new HashSet<TKey>();
            foreach (var item in list)
                if (!seen.Add(key(item))) throw Refuse(ManifestCodes.DuplicateEntry);
            return list;
        }
    }
}
