using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class LootPlacerTests
    {
        readonly List<Object> made = new List<Object>();
        ItemDefinition medkit, rifle, vest;

        T Track<T>(T o) where T : Object { made.Add(o); return o; }

        [SetUp]
        public void SetUp()
        {
            var archetype = Track(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));
            medkit = Track(ItemDefinition.Create("medkit", "Medkit", ItemCategory.Consumable, ItemSlot.None, 3, default, null, 40));
            rifle = Track(ItemDefinition.Create("rifle", "Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: archetype));
            vest = Track(ItemDefinition.Create("vest", "Vest", ItemCategory.Armor, ItemSlot.Armor));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) Object.DestroyImmediate(o);
            made.Clear();
        }

        LootTable Table(int version = 1, int count = 3, params LootEntry[] entries)
        {
            if (entries.Length == 0)
                entries = new[]
                {
                    new LootEntry { item = medkit, minQuantity = 1, maxQuantity = 2, weight = 4 },
                    new LootEntry { item = rifle, minQuantity = 1, maxQuantity = 1, weight = 1 },
                };
            return Track(LootTable.Create(version, count, 1, 2, entries));
        }

        static bool Build(int seed, out MissionLayout layout, out ObjectivePlan plan, out SecurityPlan security, out MissionSettings settings)
        {
            settings = new MissionSettings { seed = seed }.Validated();
            for (var attempt = 1; attempt <= settings.maxAttempts; attempt++)
            {
                if (!MissionGenerator.TryAttempt(settings, attempt, out layout, out _))
                    continue;
                if (!ObjectivePlacer.TryPlace(layout, settings, out plan, out _))
                    continue;
                if (!SecurityPlacer.TryPlace(layout, plan, settings, out security, out _))
                    continue;
                return true;
            }
            layout = null;
            plan = null;
            security = null;
            return false;
        }

        [Test]
        public void SameInputs_GiveTheSamePlanAndHash()
        {
            Assert.That(Build(12345, out var layout, out var plan, out var security, out _), Is.True);
            var table = Table();

            var a = LootPlacer.Place(layout, plan, security, table);
            var b = LootPlacer.Place(layout, plan, security, table);

            Assert.That(a.Placed, Is.GreaterThan(0));
            Assert.That(b.Hash, Is.EqualTo(a.Hash));
            for (var i = 0; i < a.Containers.Count; i++)
            {
                Assert.That(b.Containers[i].Tile, Is.EqualTo(a.Containers[i].Tile));
                Assert.That(b.Containers[i].Items.Count, Is.EqualTo(a.Containers[i].Items.Count));
            }
        }

        [Test]
        public void ChangingTheTableContents_NeverMovesAContainer_OrTheLayout()
        {
            Assert.That(Build(12345, out var layout, out var plan, out var security, out _), Is.True);
            var layoutHash = layout.Hash;
            var first = LootPlacer.Place(layout, plan, security, Table(1));
            var other = LootPlacer.Place(layout, plan, security, Table(2, 3,
                new LootEntry { item = vest, minQuantity = 1, maxQuantity = 1, weight = 1 }));

            Assert.That(layout.Hash, Is.EqualTo(layoutHash), "placement never touches the layout");
            Assert.That(other.Placed, Is.EqualTo(first.Placed));
            for (var i = 0; i < first.Placed; i++)
                Assert.That(other.Containers[i].Tile, Is.EqualTo(first.Containers[i].Tile), "same tiles with different items");
            Assert.That(other.Hash, Is.Not.EqualTo(first.Hash));
        }

        [Test]
        public void AskingForMoreContainers_KeepsTheEarlierOnesWhereTheyWere()
        {
            Assert.That(Build(12345, out var layout, out var plan, out var security, out _), Is.True);
            var two = LootPlacer.Place(layout, plan, security, Table(1, 2));
            var four = LootPlacer.Place(layout, plan, security, Table(1, 4));

            for (var i = 0; i < two.Placed; i++)
                Assert.That(four.Containers[i].Tile, Is.EqualTo(two.Containers[i].Tile));
        }

        [Test]
        public void DifferentSeedsGiveDifferentPlans_AndTheTableVersionIsInTheHash()
        {
            Assert.That(Build(12345, out var l1, out var p1, out var s1, out _), Is.True);
            Assert.That(Build(777, out var l2, out var p2, out var s2, out _), Is.True);

            var a = LootPlacer.Place(l1, p1, s1, Table());
            var b = LootPlacer.Place(l2, p2, s2, Table());
            var v2 = LootPlacer.Place(l1, p1, s1, Table(2));

            Assert.That(b.Hash, Is.Not.EqualTo(a.Hash));
            Assert.That(v2.Hash, Is.Not.EqualTo(a.Hash));
            Assert.That(v2.TableVersion, Is.EqualTo(2));
        }

        [Test]
        public void EveryContainer_StandsAgainstAWall_BesideNoDoorway_ClearOfEverythingElse_Across60Seeds()
        {
            var full = 0;
            var built = 0;
            var onWall = 0;
            var total = 0;
            for (var seed = 1; seed <= 60; seed++)
            {
                if (!Build(seed, out var layout, out var plan, out var security, out var settings))
                    continue;
                built++;
                var loot = LootPlacer.Place(layout, plan, security, Table());
                if (loot.Placed == loot.Requested)
                    full++;
                var blocked = ObjectivePlacer.ObstacleMask(layout);
                var others = new List<Vector2Int>(layout.FriendlySpawns);
                others.AddRange(layout.HostileSpawns);
                others.AddRange(plan.GuardTiles);
                others.Add(plan.TerminalTile);
                others.Add(plan.ExtractionTile);
                if (security.HasTerminal)
                    others.Add(security.TerminalTile);
                var placedTiles = new List<Vector2Int>();
                foreach (var container in loot.Containers)
                {
                    total++;
                    var room = layout.Rooms[container.Room].Rect;
                    var ring = container.Tile.x == room.xMin || container.Tile.x == room.xMax - 1
                        || container.Tile.y == room.yMin || container.Tile.y == room.yMax - 1;
                    if (ring)
                    {
                        onWall++;
                        // The lane two tiles in front of the wall is clear too, so no one-tile strip is left beside an obstacle.
                        var inward = new Vector2Int(container.Tile.x == room.xMin ? 1 : container.Tile.x == room.xMax - 1 ? -1 : 0,
                            container.Tile.y == room.yMin ? 1 : container.Tile.y == room.yMax - 1 ? -1 : 0);
                        foreach (var direction in new[] { new Vector2Int(inward.x, 0), new Vector2Int(0, inward.y) })
                        {
                            if (direction == Vector2Int.zero)
                                continue;
                            for (var side = -1; side <= 1; side++)
                            {
                                var lane = container.Tile + direction * 2 + new Vector2Int(direction.y != 0 ? side : 0, direction.x != 0 ? side : 0);
                                if (lane.x >= room.xMin && lane.x < room.xMax && lane.y >= room.yMin && lane.y < room.yMax)
                                    Assert.That(blocked[lane.y * layout.Width + lane.x], Is.False, $"seed {seed}: {container.Tile} has an obstacle in its lane at {lane}");
                            }
                        }
                    }
                    else
                    {
                        Assert.That(Mathf.Min(container.Tile.x - room.xMin, room.xMax - 1 - container.Tile.x, container.Tile.y - room.yMin, room.yMax - 1 - container.Tile.y),
                            Is.GreaterThanOrEqualTo(LootPlacer.FallbackInset), $"seed {seed}: a fallback crate keeps a lane of two tiles or more to the wall");
                    }
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var x = container.Tile.x + dx;
                            var y = container.Tile.y + dy;
                            var inside = x >= room.xMin && x < room.xMax && y >= room.yMin && y < room.yMax;
                            if (inside)
                            {
                                Assert.That(layout.IsFloor(x, y), Is.True, $"seed {seed}: floor around {container.Tile}");
                                Assert.That(blocked[y * layout.Width + x], Is.False, $"seed {seed}: free around {container.Tile}");
                            }
                            else if (ring)
                                Assert.That(layout.IsFloor(x, y), Is.False, $"seed {seed}: {container.Tile} is beside a doorway or corridor at {x},{y}");
                        }
                    foreach (var other in others)
                        Assert.That(Vector2.Distance(container.Tile, other), Is.GreaterThanOrEqualTo(LootPlacer.AvoidDistance), $"seed {seed}");
                    foreach (var earlier in placedTiles)
                        Assert.That(Vector2.Distance(container.Tile, earlier), Is.GreaterThanOrEqualTo(LootPlacer.AvoidDistance), $"seed {seed}");
                    placedTiles.Add(container.Tile);
                    if (layout.Rooms.Count > 1)
                        Assert.That(container.Room, Is.Not.EqualTo(layout.FriendlyRoom), $"seed {seed}: not in the spawn room");
                    Assert.That(container.Items.Count, Is.InRange(1, 2));
                }
            }
            Assert.That(built, Is.GreaterThan(50));
            Assert.That(full / (float)built, Is.GreaterThanOrEqualTo(0.9f), $"{full} of {built} layouts placed every container");
            Assert.That(onWall / (float)total, Is.GreaterThanOrEqualTo(0.95f), $"{onWall} of {total} containers stand against a wall (the rest used the inset fallback)");
        }

        [Test]
        public void ItemsComeOnlyFromTheTable_WithinTheirQuantityRange()
        {
            Assert.That(Build(12345, out var layout, out var plan, out var security, out _), Is.True);
            var loot = LootPlacer.Place(layout, plan, security, Table());

            foreach (var container in loot.Containers)
                foreach (var item in container.Items)
                {
                    Assert.That(item.DefinitionId, Is.EqualTo("medkit").Or.EqualTo("rifle"));
                    Assert.That(item.Quantity, Is.InRange(1, item.DefinitionId == "medkit" ? 2 : 1));
                }
        }

        [Test]
        public void WhenNothingFits_TheShortfallIsReported_NotHidden()
        {
            Assert.That(Build(12345, out var layout, out var plan, out var security, out _), Is.True);

            var loot = LootPlacer.Place(layout, plan, security, Table(1, 3), avoidDistance: 1000f);

            Assert.That(loot.Placed, Is.EqualTo(0));
            Assert.That(loot.Requested, Is.EqualTo(3));
            Assert.That(loot.Shortfall, Is.Not.Empty);
        }

        [Test]
        public void ANullTableOrZeroContainers_GivesAnEmptyPlan()
        {
            Assert.That(Build(12345, out var layout, out var plan, out var security, out _), Is.True);

            Assert.That(LootPlacer.Place(layout, plan, security, null).Placed, Is.EqualTo(0));
            Assert.That(LootPlacer.Place(layout, plan, security, Table(1, 0)).Requested, Is.EqualTo(0));
        }

        [Test]
        public void Table_ValidationNamesTheProblem()
        {
            Assert.That(Table().IsValid(out var problem), Is.True, problem);
            var noEntries = Track(LootTable.Create(1, 3, 1, 2, new LootEntry[0]));
            var noItem = Track(LootTable.Create(1, 3, 1, 2, new[] { new LootEntry { item = null, minQuantity = 1, maxQuantity = 1, weight = 1 } }));
            var badWeight = Track(LootTable.Create(1, 3, 1, 2, new[] { new LootEntry { item = medkit, minQuantity = 1, maxQuantity = 1, weight = 0 } }));
            var badRange = Track(LootTable.Create(1, 3, 1, 2, new[] { new LootEntry { item = medkit, minQuantity = 3, maxQuantity = 1, weight = 1 } }));

            Assert.That(noEntries.IsValid(out problem), Is.False);
            StringAssert.Contains("entries", problem);
            Assert.That(noItem.IsValid(out problem), Is.False);
            StringAssert.Contains("item", problem);
            Assert.That(badWeight.IsValid(out problem), Is.False);
            StringAssert.Contains("weight", problem);
            Assert.That(badRange.IsValid(out problem), Is.False);
            StringAssert.Contains("quantity", problem);
        }

        [Test]
        public void LootStreams_AreIndependentOfTheOtherStreams()
        {
            var loot = SeededRandom.ForLoot(12345, 1).NextULong();
            Assert.That(loot, Is.Not.EqualTo(SeededRandom.ForAttempt(12345, 1).NextULong()));
            Assert.That(loot, Is.Not.EqualTo(SeededRandom.ForObjectives(12345, 1).NextULong()));
            Assert.That(loot, Is.Not.EqualTo(SeededRandom.ForSecurity(12345, 1).NextULong()));
            Assert.That(SeededRandom.ForLootContents(12345, 1, 0).NextULong(), Is.Not.EqualTo(SeededRandom.ForLootContents(12345, 1, 1).NextULong()));
        }
    }
}
