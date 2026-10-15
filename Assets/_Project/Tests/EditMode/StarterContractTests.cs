using System;
using System.Collections.Generic;
using System.Linq;
using Blackglass.BackendTools;
using NUnit.Framework;
using static Blackglass.Tests.ContractVectors;

namespace Blackglass.Tests
{
    /// <summary>
    /// Runs the shared starter vector (Backend/contracts/starter) against InventorySession.SeedStarter. The backend's xUnit
    /// tests run the same file against its catalogue validator and StarterPlanner. Refusals that Unity only enforces in the
    /// editor's CatalogueBuilder (a second equip in one slot, an item outside the catalogue) are checked there.
    /// </summary>
    public class StarterContractTests
    {
        static readonly string[] RefusalClasses = { "second-equip-in-slot", "equip-without-slot", "unknown-item", "bag-capacity" };

        GoldenAssets a;

        [SetUp]
        public void SetUp() => a = new GoldenAssets();

        [TearDown]
        public void TearDown() => a.Dispose();

        /// <summary>One vector case as Unity assets: the catalogue (without any ghost item), starter, and the ids in authored order.</summary>
        sealed class Built
        {
            public int BagCapacity;
            public ItemCatalogue Catalogue;
            public StarterLoadout Starter;
            public List<string> OperativeIds = new List<string>();
        }

        Built Build(Dictionary<string, object> c)
        {
            var built = new Built { BagCapacity = Int(c["bagCapacity"]) };
            var items = new Dictionary<string, ItemDefinition>(StringComparer.Ordinal);
            foreach (var entry in Arr(c["items"]).Select(Obj))
            {
                var id = Str(entry["id"]);
                var category = (ItemCategory)Enum.Parse(typeof(ItemCategory), Str(entry["category"]), true);
                var slot = (ItemSlot)Enum.Parse(typeof(ItemSlot), Str(entry["slot"]), true);
                var maxStack = Int(entry["maxStack"]);
                Assert.That(Bool(entry["stackable"]), Is.EqualTo(maxStack > 1), id + ": the vector's stackable flag and its maxStack disagree");
                items.Add(id, a.Item(id, category, slot, maxStack, Int(entry["healAmount"]), category == ItemCategory.Weapon ? a.Archetype : null));
            }

            // An id the vector does not list stands for an item that exists as an asset but is not in the catalogue.
            ItemDefinition Resolve(string itemId)
            {
                if (!items.TryGetValue(itemId, out var found))
                {
                    found = a.Item(itemId, ItemCategory.Armor, ItemSlot.Armor);
                    items.Add(itemId, found);
                    ghosts.Add(found);
                }
                return found;
            }

            ghosts.Clear();
            var operatives = new List<StarterOperative>();
            a.Operatives.Clear();
            foreach (var op in Arr(c["operatives"]).Select(Obj))
            {
                var id = Str(op["id"]);
                var definition = a.Operative(id, a.Alpha);
                a.Operatives.Add(definition);
                built.OperativeIds.Add(id);
                var grants = Arr(op["grants"]).Select(Obj)
                    .Select(g => GoldenAssets.Grant(Resolve(Str(g["itemId"])), Int(g["quantity"]), Bool(g["equip"]))).ToArray();
                operatives.Add(new StarterOperative { operative = definition, items = grants });
            }
            var stash = Arr(c["stash"]).Select(Obj).Select(g => GoldenAssets.Grant(Resolve(Str(g["itemId"])), Int(g["quantity"]))).ToArray();

            var listed = items.Values.Where(i => !ghosts.Contains(i)).OrderBy(i => i.Id, StringComparer.Ordinal).ToArray();
            built.Catalogue = a.Keep(ItemCatalogue.Create(listed));
            built.Starter = a.Keep(StarterLoadout.Create(operatives.ToArray(), stash));
            a.Items = built.Catalogue;
            a.Rules = a.Keep(InventoryRules.Create(built.BagCapacity));
            a.Loot = a.Keep(LootTable.Create(7, 2, 1, 3, new[] { GoldenAssets.Entry(listed[0], 1, 1, 1) }));
            a.Starter = built.Starter;
            return built;
        }

        readonly List<ItemDefinition> ghosts = new List<ItemDefinition>();

        static string RefusalClass(string caseName, string error)
        {
            if (error.Contains("a second item to equip")) return "second-equip-in-slot";
            if (error.Contains("cannot be equipped")) return "equip-without-slot";
            if (error.Contains("not in the item catalogue")) return "unknown-item";
            if (error.Contains("exceed the bag capacity")) return "bag-capacity";
            throw new AssertionException($"{caseName}: the builder error maps to no refusal class: {error}");
        }

        static List<string> Entries(IEnumerable<object> list) =>
            list.Select(e => Arr(e)).Select(e => $"{Str(e[0])} x{Int(e[1])}").ToList();

        static List<string> Entries(ItemInventory inventory) =>
            inventory.Entries.Select(e => $"{e.DefinitionId} x{e.Quantity}").ToList();

        [Test]
        public void Every_case_matches_SeedStarter()
        {
            var cases = Arr(Load("starter", "vectors.json")["cases"]).Select(Obj).ToList();
            Assert.That(cases.Count, Is.GreaterThanOrEqualTo(10), "the vector file lost cases");

            var names = new HashSet<string>();
            var refusals = new HashSet<string>();
            var accepted = 0;
            foreach (var c in cases)
            {
                var name = Str(c["name"]);
                Assert.That(names.Add(name), Is.True, "duplicate case name " + name);
                a.Dispose();
                a = new GoldenAssets();
                var built = Build(c);
                var expected = Obj(c["expected"]);

                var errors = new List<string>();
                var document = CatalogueBuilder.Build(a.Sources(), errors);

                var problems = new List<string>();
                var session = new InventorySession(built.Catalogue, built.BagCapacity);
                session.SeedStarter(built.Starter, built.OperativeIds, problems);

                if (expected.TryGetValue("refused", out var refusedValue))
                {
                    var refused = Str(refusedValue);
                    refusals.Add(refused);
                    Assert.That(document, Is.Null, $"{name}: the builder accepted a case the vector refuses ({refused})");
                    var classes = errors.Select(e => RefusalClass(name, e)).Distinct().ToList();
                    Assert.That(classes, Is.EqualTo(new[] { refused }), $"{name}: builder said\n{string.Join("\n", errors)}");

                    switch (refused)
                    {
                        case "bag-capacity":
                            Assert.That(problems.Exists(p => p.Contains("no room for")), Is.True, $"{name}: SeedStarter problems [{string.Join("; ", problems)}]");
                            break;
                        case "equip-without-slot":
                            Assert.That(problems.Exists(p => p.Contains("can't be equipped")), Is.True, $"{name}: SeedStarter problems [{string.Join("; ", problems)}]");
                            break;
                        case "second-equip-in-slot":
                        case "unknown-item":
                            // Validator-only: SeedStarter overwrites the slot silently, and an item outside the catalogue is
                            // still a plain ItemDefinition to the bag. The builder's refusal above is the assertion.
                            break;
                        default:
                            Assert.Fail($"{name}: unmapped refusal class {refused}");
                            break;
                    }
                    continue;
                }

                accepted++;
                Assert.That(document, Is.Not.Null, $"{name}: the builder refused a case the vector accepts:\n{string.Join("\n", errors)}");
                Assert.That(problems, Is.Empty, $"{name}: SeedStarter reported problems");

                var expectedOperatives = Obj(expected["operatives"]);
                Assert.That(session.Session.Loadouts.Select(l => l.OperativeId), Is.EquivalentTo(expectedOperatives.Keys), $"{name}: operatives");
                foreach (var pair in expectedOperatives)
                {
                    var loadout = session.Session.Loadout(pair.Key);
                    var want = Obj(pair.Value);
                    Assert.That(Entries(loadout.Bag), Is.EqualTo(Entries(Arr(want["bag"]))), $"{name}: bag of {pair.Key}");
                    Assert.That(loadout.Bag.Capacity, Is.EqualTo(built.BagCapacity), $"{name}: bag capacity of {pair.Key}");

                    var equipped = session.EquippedFor(session.Session, pair.Key);
                    var wantEquipped = Obj(want["equipped"]);
                    AssertSlot(name, pair.Key, "weapon", wantEquipped, equipped.Weapon);
                    AssertSlot(name, pair.Key, "armor", wantEquipped, equipped.Armor);
                    AssertSlot(name, pair.Key, "utility", wantEquipped, equipped.Utility);
                }
                Assert.That(Entries(session.Session.Stash), Is.EqualTo(Entries(Arr(expected["stash"]))), $"{name}: stash");
                Assert.That(session.Session.Stash.IsUncapped, Is.True, $"{name}: the stash is uncapped");
            }

            Assert.That(refusals, Is.EquivalentTo(RefusalClasses), "the vector must cover every refusal class");
            Assert.That(accepted, Is.GreaterThanOrEqualTo(8));
        }

        static void AssertSlot(string name, string operativeId, string slot, Dictionary<string, object> want, ItemDefinition got)
        {
            var wanted = want[slot] == null ? null : Str(want[slot]);
            Assert.That(got != null ? got.Id : null, Is.EqualTo(wanted), $"{name}: {operativeId} {slot}");
        }

        [Test]
        public void A_grant_without_an_item_is_a_SeedStarter_problem_and_a_missing_source()
        {
            // The vector's unknown-item class cannot be built in Unity as a null grant: that is its own, nearest failure.
            var id = GoldenAssets.One;
            a.Starter = a.StarterFor(new[] { GoldenAssets.Grant(null, 1) }, new StarterGrant[0], a.OperativeOne);
            var problems = new List<string>();
            new InventorySession(a.Items, 6).SeedStarter(a.Starter, new[] { id }, problems);
            Assert.That(problems.Count, Is.EqualTo(1));
            Assert.That(problems[0], Does.Contain("a grant has no item"));
            Assert.That(CatalogueBuilder.Build(a.Sources(), new List<string>()), Is.Null);
        }

        [Test]
        public void A_second_equip_in_one_slot_is_a_catalogue_rule_not_a_SeedStarter_rule()
        {
            // Documents why second-equip-in-slot is asserted through the builder: TryEquip overwrites the slot silently.
            var carbine = a.Item("item.carbine", ItemCategory.Weapon, ItemSlot.Weapon, 1, 0, a.Archetype);
            var catalogue = a.Keep(ItemCatalogue.Create(a.Rifle, carbine));
            var starter = a.StarterFor(new[] { GoldenAssets.Grant(a.Rifle, 1, true), GoldenAssets.Grant(carbine, 1, true) }, new StarterGrant[0], a.OperativeOne);
            var problems = new List<string>();
            var session = new InventorySession(catalogue, 6);
            session.SeedStarter(starter, new[] { GoldenAssets.One }, problems);
            Assert.That(problems, Is.Empty);
            Assert.That(session.EquippedFor(session.Session, GoldenAssets.One).Weapon.Id, Is.EqualTo("item.carbine"));
        }
    }
}
