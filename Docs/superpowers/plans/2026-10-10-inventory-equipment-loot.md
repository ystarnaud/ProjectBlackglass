# Inventory, Equipment & Loot (Phase 12) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove the loop *configure loadout → deploy → find loot → collect → use a medkit → extract → keep secured items in memory → re-equip for the next mission* on top of the existing procedural missions, command queue, fog of war and HUD.

**Architecture:** Plain serializable inventory data (`ItemEntry`, `ItemInventory`, `OperativeLoadout`, `InventoryState`) owned by an `InventorySession` (session state + a cloned working state per mission, settled once by mission instance id) wrapped by a scene-level `SquadInventory`. Equipment feeds Phase 10's `EffectiveConfiguration`. Item use and looting are two new `UnitCommand`s in the existing queue. Loot containers are ordinary `MissionInteractable`s (so click, R, context Confirm, fog and queueing already work) placed by a pure `LootPlacer` from a salted random stream. A scoped modal panel (own canvas, `UI` action map) edits state only through `SquadInventory`.

**Tech Stack:** Unity 6.3 LTS (6000.3.25f1), C#, Input System 1.20, uGUI (legacy `Text`, built from code), NUnit EditMode/PlayMode tests run through `Tools/run-tests.sh`.

**Spec:** `docs/superpowers/specs/2026-10-09-inventory-equipment-loot-design.md` (read it first; this plan implements sections 4–13 of it). Brief: the Phase 12 prompt. Decisions to respect: `docs/Decisions.md` 006, 013, 014, 024, 025, 033, 036, 037, 042.

## Global Constraints

- **No commits, no pushes, no next phase.** Owner directive for this phase: leave all changes in the working tree. Where this plan says *Checkpoint*, run the stated tests and report `git status --short`; reviewers diff against `HEAD`. Pre-existing dirty files (`Assets/Settings/Mobile_RPAsset.asset`, `ProjectSettings/URPProjectSettings.asset`, `.claude/`) are not ours; never stage or revert them.
- Unity batch tests need **exclusive access**: the Editor must be closed and only one Unity process may run at a time. `Tools/run-tests.sh EditMode|PlayMode [filter]` exits 0 pass, 2 test failures, 1 compile error. Measure totals with a real run (Task 0); never trust earlier counts.
- Namespace `Blackglass` for game code, `Blackglass.Tests` for tests. Game code lives in `Assets/_Project/Scripts/<Area>/`, tests in `Assets/_Project/Tests/EditMode` (pure logic, no `Awake/OnEnable/Update`) or `.../PlayMode` (anything needing lifecycle or physics). Internals are visible to the test assemblies.
- Never use `??`, `?.` or `ReferenceEquals` on `UnityEngine.Object` references (fake null). Use `== null` / `TryGetComponent`.
- Simulation components check `SimulationTime.IsRunning`, not `Time.deltaTime`; item use and looting must freeze during tactical pause.
- Definition assets (`ItemDefinition`, `ItemCatalogue`, `LootTable`, `StarterLoadout`, `WeaponVisualAsset`) hold **no mutable state**: no quantities, owners, cooldowns or mission state.
- Ownership keys are stable ids only: operative id (`PersistentOperativeState.OperativeId`), `ItemEntry.InstanceId`, definition `Id`. Never display names, GameObject instance ids, hierarchy order or prefab names.
- Equipment changes (equip, unequip, stash transfers) are refused while a mission working state exists (`InventoryMode.InMission`). Tactical pause is still in-mission. Reason text: `Equipment can't be changed during a mission.`
- No friendly fire, no shops, currency, crafting, rarity/affixes, durability, encumbrance, ammunition, corpse looting, disk save/load, VFX/audio, or Asset Studio changes. No third-party packages.
- Keyboard/mouse and the four pad families keep working. Gameplay code must not name devices or face buttons (an existing test scans for it). Every new action needs bindings in all four pad groups (`Xbox`, `PlayStation`, `Nintendo`, `Gamepad`); every binding needs a `groups` value.
- Do not claim Play Mode, build or physical-controller results that were not run. Report automated results separately from manual checks.

## Review Focus

1. **Two consumers of one entry** (two operatives taking the same loot entry; two queued medkit uses with one left): exactly one succeeds, the other fails cleanly, nothing duplicates or vanishes. Pinned in Tasks 2, 10 and 13.
2. **Collect/use with the world gone** (container destroyed, collector dead, order replaced, bag full, zero room for a non-stackable): no exception, no stuck order, nothing lost; leftovers stay in the container. Pinned in Task 13.
3. **Edits during a mission by any path** (API, panel, stale snapshot): refused with the lock reason, state unchanged. Pinned in Tasks 4 and 16.
4. **Settlement edge cases** (duplicate `MissionFinished`, failed generation, F6/F7 mid-mission, same seed twice, Success then Failure callbacks): the session changes at most once, and only on Success of the active instance. Pinned in Tasks 4 and 8.
5. **Input leak / stuck input** (modal open while a move key, stick or Confirm is held; actions re-enabled by another component while open; closing with held keys): no gameplay order is issued from modal input and no intent sticks after close. Pinned in Tasks 15 and 18.

## File Structure

New (all under `Assets/_Project/`):

| File | Responsibility |
|---|---|
| `Scripts/Items/ItemDefinition.cs` | Immutable item data + `ItemCategory`, `ItemSlot`, validation |
| `Scripts/Items/WeaponVisualAsset.cs` | Prefab + local offset for a weapon model (no state) |
| `Scripts/Items/ItemCatalogue.cs` | Id → definition lookup and validation |
| `Scripts/Items/ItemEntry.cs` | One owned entry (instance id, definition id, quantity) |
| `Scripts/Items/ItemInventory.cs` | Capped list of entries: add/stack/remove/clone |
| `Scripts/Items/ItemTransfer.cs` | Atomic move between inventories; `TransferResult`, `TransferFailure` |
| `Scripts/Items/EditResult.cs` | `{Ok, Reason}` result of an equipment edit |
| `Scripts/Items/OperativeEquipment.cs` | Three slots holding entry instance ids; `EquippedItems` |
| `Scripts/Items/OperativeLoadout.cs` | One operative's bag + equipment |
| `Scripts/Items/InventoryState.cs` | Stash + loadouts; deep `Clone()` |
| `Scripts/Items/StarterLoadout.cs` | Authored starter grants (data) |
| `Scripts/Items/InventorySession.cs` | Session vs working state, begin/abort/settle, edits, seeding (plain class) |
| `Scripts/Items/SquadInventory.cs` | Scene component: owns an `InventorySession`, subscribes to the director |
| `Scripts/Items/UnitItems.cs` | Per-unit item use and collecting (runs in the command queue) |
| `Scripts/Items/UnitWeaponVisual.cs` | Managed weapon child under `WeaponSocket` |
| `Scripts/Items/LootTable.cs` | Weighted loot table (data) |
| `Scripts/Items/LootPlan.cs` | Pure placement result + hash |
| `Scripts/Items/LootPlacer.cs` | Pure placement from layout + plans + loot stream |
| `Scripts/Items/LootContainer.cs` | Scene component: container inventory, searched flag |
| `Scripts/Items/LootKnowledge.cs` | Fog rule for container contents |
| `Scripts/Controls/InputModalGate.cs` | Disable/restore gameplay actions while a modal is open |
| `Scripts/Hud/InventoryView.cs` | Plain data the modal draws |
| `Scripts/Hud/InventoryViewBuilder.cs` | Builds the view from `SquadInventory` (the only reader) |
| `Scripts/Hud/InventoryRequests.cs` | What the modal's buttons ask of gameplay |
| `Scripts/Hud/InventoryModal.cs` | uGUI modal panel + deploy buttons |
| `Scripts/Items/ItemText.cs` | The few words the panel shows about an item (pure) |
| `Editor/InventoryDataBuilder.cs` | Creates item/loot/starter assets, migrates the baked rifles |
| `Editor/InventorySceneBuilder.cs` | Wires `ProceduralMission` |
| `Tests/EditMode/...`, `Tests/PlayMode/...` | One test file per task (named in each task) |
| `Docs/` (repo `docs/`) | `Phase12-ManualTests.md`; decision 043 in `Decisions.md` |

Modified: `Operatives/EffectiveConfiguration.cs`, `Operatives/SquadRoster.cs`, `Operatives/UnitIdentity.cs`, `Units/UnitAttacker.cs`, `Units/CommandableUnit.cs`, `Units/UnitInteractor.cs`, `Commands/UnitCommands.cs`, `Mission/MissionInteractable.cs`, `Mission/MissionDirector.cs`, `Mission/MissionSlots.cs`, `Mission/MissionSpawner.cs`, `Mission/MissionContent.cs`, `Mission/MissionNavigation.cs`, `Mission/SeededRandom.cs`, `Input/BlackglassControls.inputactions`, `Hud/HudSnapshot.cs`, `Hud/HudSnapshotBuilder.cs`, `Hud/OperativePanel.cs`, `Controls/PlayerCommandInput.cs` (prompt label only, if needed), five scene tests (deploy helper).

## Interface Index

Later tasks rely on these exact names. Task numbers say where each is defined.

- **T1** `enum ItemCategory { Weapon, Armor, Utility, Consumable }`, `enum ItemSlot { None, Weapon, Armor, Utility }`; `ItemDefinition` (`Id`, `DisplayName`, `Description`, `Category`, `Slot`, `MaxStack`, `IsStackable`, `Modifiers` (`StatModifiers`), `Weapon` (`CombatArchetype`), `HealAmount`, `Icon`, `WeaponVisual`, `IsValid(out string)`, `internal static Create(string id, string displayName, ItemCategory category, ItemSlot slot, int maxStack = 1, StatModifiers modifiers = default, CombatArchetype weapon = null, int healAmount = 0)`, `internal void SetWeaponVisual(WeaponVisualAsset)`, `internal void SetDescription(string)`); `WeaponVisualAsset` (`Prefab`, `LocalPosition`, `LocalEuler`, `LocalScale`, `internal static Create(GameObject, Vector3, Vector3, Vector3)`); `ItemCatalogue` (`Items`, `Find(string)`, `Validate(List<string>)`, `internal static Create(params ItemDefinition[])`).
- **T2** `ItemEntry` (`InstanceId`, `DefinitionId`, `Quantity`, `Clone()`, `internal SetQuantity(int)`); `ItemInventory` (`Capacity`, `IsUncapped`, `Count`, `Entries`, `FreeEntries`, `Find(string)`, `CountOf(string)`, `RoomFor(ItemDefinition)`, `Add(ItemDefinition, int)` → leftover, `Remove(string, int)` → removed, `Clone()`, `internal TryAddEntry(ItemEntry)`, `internal RemoveEntry(ItemEntry)`); `enum TransferFailure { None, InvalidQuantity, SameContainer, NotFound, UnknownItem, DestinationFull, Equipped, Locked }`; `TransferResult` (`Requested`, `Moved`, `Remaining`, `Failure`, `IsPartial`, `Describe(string itemName)`, `static Failed(TransferFailure, int requested)`); `ItemTransfer.Move(ItemInventory source, ItemInventory destination, string instanceId, int quantity, ItemCatalogue catalogue)`.
- **T3** `EditResult` (`Ok`, `Reason`, `static Success`, `static Fail(string)`); `EquippedItems` (`IsSet`, `Weapon`, `Armor`, `Utility` — all `ItemDefinition`, `Modifiers`); `OperativeEquipment` (`InstanceIdIn(ItemSlot)`, `Contains(string)`, `TryEquip(ItemInventory bag, ItemCatalogue, string instanceId)` → `EditResult`, `Unequip(ItemSlot)` → bool, `Resolve(ItemInventory, ItemCatalogue)` → `EquippedItems`, `Clone()`); `OperativeLoadout` (`OperativeId`, `Bag`, `Equipment`, `Resolve(ItemCatalogue)`, `Clone()`); `InventoryState` (`Stash`, `Loadouts`, `Loadout(string)`, `EnsureLoadout(string, int)`, `MoveToStash(...)`, `MoveToBag(...)`, `Clone()`).
- **T4** `StarterGrant { ItemDefinition item; int quantity; bool equip }`, `StarterOperative { OperativeDefinition operative; StarterGrant[] items }`, `StarterLoadout` (`For(string operativeId)` → `IReadOnlyList<StarterGrant>`, `Stash`); `enum InventoryMode { Loadout, InMission }`; `InventorySession` (`Session`, `Working`, `Active`, `Mode`, `ActiveMissionId`, `Catalogue`, `BagCapacity`, `Changed`, `EnsureOperative`, `BeginMission(string)`, `AbortMission()`, `Settle(string, bool)`, `Equip`, `Unequip`, `ToStash`, `FromStash`, `SeedStarter`, `UnsecuredCount`, `EquippedFor`); `SquadInventory` (`Core`, `Catalogue`, `StartInLoadout`, `WorkingLoadout(string)`, `EquippedFor(string)`, `HandleMissionFinished(MissionPhase)`).
- **T5** `EffectiveConfiguration.Evaluate(def, state, track, EquippedItems)`; new `HasWeapon`, `WeaponName`; `SquadRoster.Evaluate(member, EquippedItems)`.
- **T6** `UnitAttacker.HasWeapon`; `CommandableUnit.LastRefusal` (`CommandRefusal { None, NoWeapon }`), `LastRefusalTime`.
- **T7** Assets: `Data/Items/*.asset`, `Data/Items/ItemCatalogue.asset`, `Data/Items/StarterLoadout.asset`, `Data/Items/Visuals/*.asset`.
- **T8** `MissionDirector.InstanceId`; `MissionSystems.inventory`.
- **T9** `UnitIdentity.Bind(SquadRoster, RosterMember, SquadInventory = null)`, `UnitIdentity.Inventory`; `UnitWeaponVisual` (`Show(WeaponVisualAsset)`, `Current`).
- **T10** `UseItemCommand(string instanceId)`; `ItemUseFailure`; `UnitItems` (`Bind`, `Check`, `TryUse`, `LastUseFailure`).
- **T11** `SeededRandom.ForLoot(int, int)`; `LootTable`; `LootPlan`; `LootPlacer.TryPlace(...)`.
- **T12** `MissionInteractable.Rearm`, `.LastUser`, `.CompletionCount`, `.SetLabel`; `LootContainer`; `MissionContent.AddLootContainers`; `MissionNavigation.ValidateLoot`; `GeneratedMission.LootContainers`.
- **T13** `CollectCommand`; `UnitItems.TryCollect`, `CollectResult`.
- **T15** `InputModalGate`; actions `Inventory/Toggle`, `UI/PreviousTab`, `UI/NextTab`.
- **T16** `ItemText`, `InventoryListKind`, `InventorySelection`, `InventoryView`, `InventoryViewSources`, `InventoryViewBuilder.Build(sources, inspectedId, selection, view)`, `RequestResult`, `InventoryRequests`.
- **T17** `HudSnapshot.ControlledWeapon`.
- **T18** `InventoryModal`, `InventoryCommand`.
- **T19** `InventorySceneBuilder`, `TestWorld.DeployFromLoadout`.

---

## Milestone A — Items, inventories, equipment state

### Task 0: Baseline

**Files:** none changed.

- [ ] **Step 1: Confirm the tree and measure the baseline**

Run (Unity Editor closed):

```bash
cd /f/Programs/ProjectBlackglass && git status --short && git log --oneline -1
Tools/run-tests.sh EditMode
Tools/run-tests.sh PlayMode
```

Expected: `HEAD` is `b02289a`; both runs `EXIT=0`. Write the two totals (`total="…"`) into a scratch note (`.claude/scratchpad/phase12-baseline.txt`). Every later "no regression" check compares against these, never against a remembered number. If either run fails before any change, stop and report.

---

### Task 1: Item definitions, weapon visual asset, catalogue

**Files:**
- Create: `Assets/_Project/Scripts/Items/ItemDefinition.cs`, `WeaponVisualAsset.cs`, `ItemCatalogue.cs`
- Test: `Assets/_Project/Tests/EditMode/ItemDefinitionTests.cs`

**Interfaces:**
- Consumes: `StatModifiers` (`Operatives/StatModifiers.cs`), `CombatArchetype` (`Combat/CombatArchetype.cs`).
- Produces: the T1 names in the Interface Index.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/ItemDefinitionTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class ItemDefinitionTests
    {
        readonly List<Object> made = new List<Object>();

        T Track<T>(T o) where T : Object { made.Add(o); return o; }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) Object.DestroyImmediate(o);
            made.Clear();
        }

        CombatArchetype Archetype() => Track(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));

        [Test]
        public void Weapon_NeedsAnArchetypeAndTheWeaponSlot()
        {
            var ok = Track(ItemDefinition.Create("rifle", "Service Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: Archetype()));
            var noArchetype = Track(ItemDefinition.Create("bad1", "Bad", ItemCategory.Weapon, ItemSlot.Weapon));
            var wrongSlot = Track(ItemDefinition.Create("bad2", "Bad", ItemCategory.Weapon, ItemSlot.Armor, weapon: Archetype()));

            Assert.That(ok.IsValid(out var problem), Is.True, problem);
            Assert.That(noArchetype.IsValid(out problem), Is.False);
            StringAssert.Contains("archetype", problem);
            Assert.That(wrongSlot.IsValid(out problem), Is.False);
            StringAssert.Contains("slot", problem);
        }

        [Test]
        public void Consumable_NeedsHealing_NoSlot_AndMayStack()
        {
            var medkit = Track(ItemDefinition.Create("medkit", "Medkit", ItemCategory.Consumable, ItemSlot.None, maxStack: 3, healAmount: 40));
            var noHeal = Track(ItemDefinition.Create("bad", "Bad", ItemCategory.Consumable, ItemSlot.None, maxStack: 3));
            var slotted = Track(ItemDefinition.Create("bad2", "Bad", ItemCategory.Consumable, ItemSlot.Utility, maxStack: 3, healAmount: 5));

            Assert.That(medkit.IsValid(out var problem), Is.True, problem);
            Assert.That(medkit.IsStackable, Is.True);
            Assert.That(noHeal.IsValid(out problem), Is.False);
            Assert.That(slotted.IsValid(out problem), Is.False);
        }

        [Test]
        public void ArmorAndUtility_MustMatchTheirSlot_AndNotStack()
        {
            var vest = Track(ItemDefinition.Create("vest", "Vest", ItemCategory.Armor, ItemSlot.Armor, modifiers: new StatModifiers { maxHealth = 20 }));
            var stackedVest = Track(ItemDefinition.Create("v2", "Vest", ItemCategory.Armor, ItemSlot.Armor, maxStack: 2));
            var wrong = Track(ItemDefinition.Create("u", "Boots", ItemCategory.Utility, ItemSlot.Armor));

            Assert.That(vest.IsValid(out var problem), Is.True, problem);
            Assert.That(vest.IsStackable, Is.False);
            Assert.That(stackedVest.IsValid(out problem), Is.False);
            Assert.That(wrong.IsValid(out problem), Is.False);
        }

        [Test]
        public void BlankIdOrName_IsInvalid()
        {
            var noId = Track(ItemDefinition.Create(" ", "X", ItemCategory.Armor, ItemSlot.Armor));
            var noName = Track(ItemDefinition.Create("x", "", ItemCategory.Armor, ItemSlot.Armor));

            Assert.That(noId.IsValid(out var problem), Is.False);
            StringAssert.Contains("id", problem);
            Assert.That(noName.IsValid(out problem), Is.False);
            StringAssert.Contains("name", problem);
        }

        [Test]
        public void Catalogue_FindsById_AndReportsDuplicatesAndInvalidEntries()
        {
            var vest = Track(ItemDefinition.Create("vest", "Vest", ItemCategory.Armor, ItemSlot.Armor));
            var twin = Track(ItemDefinition.Create("vest", "Other Vest", ItemCategory.Armor, ItemSlot.Armor));
            var bad = Track(ItemDefinition.Create("bad", "Bad", ItemCategory.Weapon, ItemSlot.Weapon));
            var good = Track(ItemCatalogue.Create(vest));
            var broken = Track(ItemCatalogue.Create(vest, twin, bad, null));

            Assert.That(good.Find("vest") == vest, Is.True);
            Assert.That(good.Find("nope") == null, Is.True);
            Assert.That(good.Find(null) == null, Is.True);
            Assert.That(good.Validate(new List<string>()), Is.True);

            var errors = new List<string>();
            Assert.That(broken.Validate(errors), Is.False);
            Assert.That(errors, Has.Count.EqualTo(3));
            Assert.That(string.Join("\n", errors), Does.Contain("duplicate").And.Contain("bad").And.Contain("empty"));
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.ItemDefinitionTests"`
Expected: `EXIT=1` with `error CS0246` for `ItemDefinition` / `ItemCategory`.

- [ ] **Step 3: Implement**

`Assets/_Project/Scripts/Items/WeaponVisualAsset.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The model a weapon item shows in a unit's hand: a prefab and the local pose of its root under the unit's
    /// WeaponSocket. The socket carries the per-character alignment; this carries the per-weapon difference (identity for
    /// the standard rifle). Shared, immutable data: it holds no state.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Weapon Visual", fileName = "WeaponVisual")]
    public sealed class WeaponVisualAsset : ScriptableObject
    {
        [SerializeField] GameObject prefab;
        [SerializeField] Vector3 localPosition = Vector3.zero;
        [SerializeField] Vector3 localEuler = Vector3.zero;
        [SerializeField] Vector3 localScale = Vector3.one;

        public GameObject Prefab => prefab;
        public Vector3 LocalPosition => localPosition;
        public Vector3 LocalEuler => localEuler;
        public Vector3 LocalScale => localScale;

        internal static WeaponVisualAsset Create(GameObject prefab, Vector3 position, Vector3 euler, Vector3 scale)
        {
            var asset = CreateInstance<WeaponVisualAsset>();
            asset.prefab = prefab;
            asset.localPosition = position;
            asset.localEuler = euler;
            asset.localScale = scale;
            return asset;
        }
    }
}
```

`Assets/_Project/Scripts/Items/ItemDefinition.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    public enum ItemCategory { Weapon, Armor, Utility, Consumable }

    /// <summary>The equipment slot an item fits; None for items that are only carried (consumables).</summary>
    public enum ItemSlot { None, Weapon, Armor, Utility }

    /// <summary>
    /// A kind of item as shared, immutable data: a stable authored id (never the display name), a category, the slot it
    /// equips to, how many fit in one stack, passive stat modifiers, the combat archetype a weapon brings, the healing a
    /// consumable gives, and optional presentation (icon, weapon model). Holds no quantity, owner, cooldown or mission
    /// state: owned items are ItemEntry values in an ItemInventory.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Item", fileName = "Item")]
    public sealed class ItemDefinition : ScriptableObject
    {
        [SerializeField] string id = "";
        [SerializeField] string displayName = "Item";
        [SerializeField, TextArea] string description = "";
        [SerializeField] ItemCategory category;
        [SerializeField] ItemSlot slot;
        [SerializeField, Min(1)] int maxStack = 1;
        [SerializeField] StatModifiers modifiers;
        [SerializeField] CombatArchetype weapon;
        [SerializeField, Min(0)] int healAmount;
        [SerializeField] Sprite icon;
        [SerializeField] WeaponVisualAsset weaponVisual;

        public string Id => id;
        public string DisplayName => displayName;
        public string Description => description;
        public ItemCategory Category => category;
        public ItemSlot Slot => slot;
        public int MaxStack => maxStack;
        public bool IsStackable => maxStack > 1;
        public StatModifiers Modifiers => modifiers;
        public CombatArchetype Weapon => weapon;
        public int HealAmount => healAmount;
        public Sprite Icon => icon;
        public WeaponVisualAsset WeaponVisual => weaponVisual;

        /// <summary>False with the first problem found (what to fix in the asset).</summary>
        public bool IsValid(out string problem)
        {
            if (string.IsNullOrWhiteSpace(id))
                problem = "the id is blank";
            else if (string.IsNullOrWhiteSpace(displayName))
                problem = "the display name is blank";
            else if (maxStack < 1)
                problem = "the maximum stack is below 1";
            else if (category == ItemCategory.Weapon && slot != ItemSlot.Weapon)
                problem = "a weapon must use the Weapon slot";
            else if (category == ItemCategory.Weapon && weapon == null)
                problem = "a weapon needs a combat archetype";
            else if (category == ItemCategory.Armor && slot != ItemSlot.Armor)
                problem = "armor must use the Armor slot";
            else if (category == ItemCategory.Utility && slot != ItemSlot.Utility)
                problem = "a utility item must use the Utility slot";
            else if (category == ItemCategory.Consumable && slot != ItemSlot.None)
                problem = "a consumable has no equipment slot";
            else if (category == ItemCategory.Consumable && healAmount < 1)
                problem = "a consumable needs a heal amount of at least 1";
            else if (category != ItemCategory.Consumable && maxStack != 1)
                problem = "equipment cannot stack";
            else if (category != ItemCategory.Weapon && weapon != null)
                problem = "only a weapon carries a combat archetype";
            else
                problem = null;
            return problem == null;
        }

        internal static ItemDefinition Create(string id, string displayName, ItemCategory category, ItemSlot slot, int maxStack = 1,
            StatModifiers modifiers = default, CombatArchetype weapon = null, int healAmount = 0)
        {
            var item = CreateInstance<ItemDefinition>();
            item.id = id;
            item.displayName = displayName;
            item.category = category;
            item.slot = slot;
            item.maxStack = maxStack;
            item.modifiers = modifiers;
            item.weapon = weapon;
            item.healAmount = healAmount;
            item.name = displayName;
            return item;
        }

        internal void SetWeaponVisual(WeaponVisualAsset visual) => weaponVisual = visual;

        internal void SetDescription(string text) => description = text ?? string.Empty;
    }
}
```

`Assets/_Project/Scripts/Items/ItemCatalogue.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Every item definition the game knows, looked up by stable id. Saved state stores only definition ids, so this is the
    /// one place an id becomes data. Holds no state.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Item Catalogue", fileName = "ItemCatalogue")]
    public sealed class ItemCatalogue : ScriptableObject
    {
        [SerializeField] ItemDefinition[] items = new ItemDefinition[0];

        public IReadOnlyList<ItemDefinition> Items => items;

        /// <summary>The definition with this id, or null (blank id, unknown id).</summary>
        public ItemDefinition Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || items == null)
                return null;
            foreach (var item in items)
            {
                if (item != null && item.Id == id)
                    return item;
            }
            return null;
        }

        /// <summary>
        /// Checks every entry: an empty slot, an invalid definition or a repeated id each add an actionable message that
        /// names the asset. True when there are no errors.
        /// </summary>
        public bool Validate(List<string> errors)
        {
            var start = errors.Count;
            var seen = new Dictionary<string, ItemDefinition>();
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (item == null)
                {
                    errors.Add($"{name}: entry {i} is empty.");
                    continue;
                }
                if (!item.IsValid(out var problem))
                {
                    errors.Add($"{name}: item '{item.DisplayName}' (id '{item.Id}', asset {item.name}) is invalid: {problem}.");
                    continue;
                }
                if (seen.TryGetValue(item.Id, out var other))
                    errors.Add($"{name}: duplicate item id '{item.Id}' on '{other.name}' and '{item.name}'.");
                else
                    seen.Add(item.Id, item);
            }
            return errors.Count == start;
        }

        internal static ItemCatalogue Create(params ItemDefinition[] definitions)
        {
            var catalogue = CreateInstance<ItemCatalogue>();
            catalogue.items = definitions ?? new ItemDefinition[0];
            return catalogue;
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.ItemDefinitionTests"`
Expected: `EXIT=0`, 5 passed.

---

### Task 2: Entries, inventories and atomic transfers

**Files:**
- Create: `Assets/_Project/Scripts/Items/ItemEntry.cs`, `ItemInventory.cs`, `ItemTransfer.cs`
- Test: `Assets/_Project/Tests/EditMode/ItemInventoryTests.cs`

**Interfaces:**
- Consumes: `ItemDefinition`, `ItemCatalogue` (T1).
- Produces: `ItemEntry`, `ItemInventory`, `TransferFailure`, `TransferResult`, `ItemTransfer` (Interface Index T2).

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/ItemInventoryTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class ItemInventoryTests
    {
        readonly List<Object> made = new List<Object>();
        ItemDefinition medkit, rifle, vest;
        ItemCatalogue catalogue;

        [SetUp]
        public void SetUp()
        {
            var archetype = Track(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));
            medkit = Track(ItemDefinition.Create("medkit", "Medkit", ItemCategory.Consumable, ItemSlot.None, maxStack: 3, healAmount: 40));
            rifle = Track(ItemDefinition.Create("rifle", "Service Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: archetype));
            vest = Track(ItemDefinition.Create("vest", "Vest", ItemCategory.Armor, ItemSlot.Armor));
            catalogue = Track(ItemCatalogue.Create(medkit, rifle, vest));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) Object.DestroyImmediate(o);
            made.Clear();
        }

        T Track<T>(T o) where T : Object { made.Add(o); return o; }

        [Test]
        public void Add_StacksUpToTheMaximum_ThenOpensANewEntry()
        {
            var bag = new ItemInventory(4);

            Assert.That(bag.Add(medkit, 2), Is.EqualTo(0));
            Assert.That(bag.Add(medkit, 2), Is.EqualTo(0));

            Assert.That(bag.Count, Is.EqualTo(2));
            Assert.That(bag.Entries[0].Quantity, Is.EqualTo(3));
            Assert.That(bag.Entries[1].Quantity, Is.EqualTo(1));
            Assert.That(bag.CountOf("medkit"), Is.EqualTo(4));
        }

        [Test]
        public void Add_WhenFull_ReturnsTheLeftover_AndChangesNothingElse()
        {
            var bag = new ItemInventory(1);
            bag.Add(medkit, 3);

            var leftover = bag.Add(medkit, 2);

            Assert.That(leftover, Is.EqualTo(2));
            Assert.That(bag.Count, Is.EqualTo(1));
            Assert.That(bag.CountOf("medkit"), Is.EqualTo(3));
        }

        [Test]
        public void NonStackable_TakesOneEntryEach_AndEachHasItsOwnInstanceId()
        {
            var bag = new ItemInventory(0);

            bag.Add(rifle, 2);

            Assert.That(bag.Count, Is.EqualTo(2));
            Assert.That(bag.Entries[0].InstanceId, Is.Not.EqualTo(bag.Entries[1].InstanceId));
            Assert.That(bag.Entries[0].Quantity, Is.EqualTo(1));
        }

        [Test]
        public void RoomFor_CountsOpenStackSpaceAndFreeEntries()
        {
            var bag = new ItemInventory(2);
            bag.Add(medkit, 2);

            Assert.That(bag.RoomFor(medkit), Is.EqualTo(1 + 3));
            Assert.That(bag.RoomFor(rifle), Is.EqualTo(1));
            Assert.That(new ItemInventory(0).RoomFor(rifle), Is.EqualTo(int.MaxValue));
        }

        [Test]
        public void Remove_TakesPartOfAStack_ThenDropsTheEmptyEntry()
        {
            var bag = new ItemInventory(0);
            bag.Add(medkit, 3);
            var id = bag.Entries[0].InstanceId;

            Assert.That(bag.Remove(id, 1), Is.EqualTo(1));
            Assert.That(bag.Entries[0].Quantity, Is.EqualTo(2));
            Assert.That(bag.Remove(id, 5), Is.EqualTo(2));
            Assert.That(bag.Count, Is.EqualTo(0));
            Assert.That(bag.Remove(id, 1), Is.EqualTo(0), "an unknown entry removes nothing");
        }

        [Test]
        public void Clone_IsDeep()
        {
            var bag = new ItemInventory(3);
            bag.Add(medkit, 2);
            var copy = bag.Clone();

            bag.Remove(bag.Entries[0].InstanceId, 2);
            copy.Add(rifle, 1);

            Assert.That(copy.CountOf("medkit"), Is.EqualTo(2));
            Assert.That(bag.Count, Is.EqualTo(0));
            Assert.That(copy.Count, Is.EqualTo(2));
            Assert.That(copy.Capacity, Is.EqualTo(3));
        }

        [Test]
        public void Move_NonStackable_KeepsTheSameInstanceId_AndHasOneLocation()
        {
            var source = new ItemInventory(0);
            var destination = new ItemInventory(2);
            source.Add(rifle, 1);
            var id = source.Entries[0].InstanceId;

            var result = ItemTransfer.Move(source, destination, id, 1, catalogue);

            Assert.That(result.Failure, Is.EqualTo(TransferFailure.None));
            Assert.That(result.Moved, Is.EqualTo(1));
            Assert.That(source.Find(id), Is.Null);
            Assert.That(destination.Find(id), Is.Not.Null);
        }

        [Test]
        public void Move_ToAFullInventory_LeavesTheItemAtTheSource()
        {
            var source = new ItemInventory(0);
            var destination = new ItemInventory(1);
            source.Add(rifle, 1);
            destination.Add(vest, 1);
            var id = source.Entries[0].InstanceId;

            var result = ItemTransfer.Move(source, destination, id, 1, catalogue);

            Assert.That(result.Failure, Is.EqualTo(TransferFailure.DestinationFull));
            Assert.That(result.Moved, Is.EqualTo(0));
            Assert.That(source.Find(id), Is.Not.Null);
            Assert.That(destination.Count, Is.EqualTo(1));
        }

        [Test]
        public void Move_Stack_MovesWhatFits_AndReportsTheLeftoverAtTheSource()
        {
            var source = new ItemInventory(0);
            var destination = new ItemInventory(1);
            source.Add(medkit, 3);
            source.Add(medkit, 3);   // two stacks of 3
            destination.Add(medkit, 2);   // one stack with 1 free
            var id = source.Entries[0].InstanceId;

            var result = ItemTransfer.Move(source, destination, id, 3, catalogue);

            Assert.That(result.Moved, Is.EqualTo(1));
            Assert.That(result.Remaining, Is.EqualTo(2));
            Assert.That(result.IsPartial, Is.True);
            Assert.That(result.Failure, Is.EqualTo(TransferFailure.DestinationFull));
            Assert.That(destination.CountOf("medkit"), Is.EqualTo(3));
            Assert.That(source.CountOf("medkit"), Is.EqualTo(5), "total units are conserved");
        }

        [Test]
        public void Move_ConservesTotals_AcrossARunOfRandomishMoves()
        {
            var a = new ItemInventory(3);
            var b = new ItemInventory(3);
            a.Add(medkit, 3);
            a.Add(medkit, 3);
            a.Add(rifle, 1);
            var total = a.CountOf("medkit") + b.CountOf("medkit");
            var rifles = 1;

            for (var i = 0; i < 20; i++)
            {
                var from = i % 2 == 0 ? a : b;
                var to = i % 2 == 0 ? b : a;
                if (from.Count == 0) continue;
                var entry = from.Entries[i % from.Count];
                ItemTransfer.Move(from, to, entry.InstanceId, 1 + i % 3, catalogue);
            }

            Assert.That(a.CountOf("medkit") + b.CountOf("medkit"), Is.EqualTo(total));
            Assert.That(a.CountOf("rifle") + b.CountOf("rifle"), Is.EqualTo(rifles));
        }

        [Test]
        public void Move_RefusesBadInput_WithoutChangingAnything()
        {
            var source = new ItemInventory(0);
            var destination = new ItemInventory(0);
            source.Add(medkit, 2);
            var id = source.Entries[0].InstanceId;

            Assert.That(ItemTransfer.Move(source, destination, id, 0, catalogue).Failure, Is.EqualTo(TransferFailure.InvalidQuantity));
            Assert.That(ItemTransfer.Move(source, destination, "missing", 1, catalogue).Failure, Is.EqualTo(TransferFailure.NotFound));
            Assert.That(ItemTransfer.Move(source, source, id, 1, catalogue).Failure, Is.EqualTo(TransferFailure.SameContainer));
            var unknown = ItemCatalogue.Create();
            made.Add(unknown);
            Assert.That(ItemTransfer.Move(source, destination, id, 1, unknown).Failure, Is.EqualTo(TransferFailure.UnknownItem));
            Assert.That(source.CountOf("medkit"), Is.EqualTo(2));
            Assert.That(destination.Count, Is.EqualTo(0));
        }

        [Test]
        public void Move_TheSameEntryTwice_SecondCallFindsNothing()
        {
            var source = new ItemInventory(0);
            var one = new ItemInventory(0);
            var two = new ItemInventory(0);
            source.Add(rifle, 1);
            var id = source.Entries[0].InstanceId;

            var first = ItemTransfer.Move(source, one, id, 1, catalogue);
            var second = ItemTransfer.Move(source, two, id, 1, catalogue);

            Assert.That(first.Moved, Is.EqualTo(1));
            Assert.That(second.Failure, Is.EqualTo(TransferFailure.NotFound));
            Assert.That(one.CountOf("rifle") + two.CountOf("rifle"), Is.EqualTo(1));
        }

        [Test]
        public void Describe_ExplainsAPartialMove()
        {
            var text = new TransferResult(3, 1, 2, TransferFailure.DestinationFull).Describe("Medkit");

            StringAssert.Contains("1", text);
            StringAssert.Contains("Medkit", text);
            StringAssert.Contains("2 left", text);
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.ItemInventoryTests"`
Expected: `EXIT=1`, `error CS0246` for `ItemInventory`.

- [ ] **Step 3: Implement**

`Assets/_Project/Scripts/Items/ItemEntry.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// One owned entry: a stable instance id, the id of its definition and how many units it holds. Plain serializable
    /// data with no Unity object references. A non-stackable item is an entry of quantity 1 whose instance id never
    /// changes, wherever it is moved; a stack is one entry too (its id is not kept when the stack is split or merged).
    /// </summary>
    [Serializable]
    public sealed class ItemEntry
    {
        [SerializeField] string instanceId;
        [SerializeField] string definitionId;
        [SerializeField] int quantity;

        public ItemEntry(string definitionId, int quantity) : this(Guid.NewGuid().ToString("N"), definitionId, quantity) { }

        public ItemEntry(string instanceId, string definitionId, int quantity)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
                throw new ArgumentException("An entry needs an instance id.", nameof(instanceId));
            if (string.IsNullOrWhiteSpace(definitionId))
                throw new ArgumentException("An entry needs a definition id.", nameof(definitionId));
            if (quantity < 1)
                throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "An entry holds at least one unit.");
            this.instanceId = instanceId;
            this.definitionId = definitionId;
            this.quantity = quantity;
        }

        // For the serializer only.
        ItemEntry() { }

        public string InstanceId => instanceId;
        public string DefinitionId => definitionId;
        public int Quantity => quantity;

        internal void SetQuantity(int value) => quantity = Math.Max(0, value);

        public ItemEntry Clone() => new ItemEntry(instanceId, definitionId, quantity);
    }
}
```

`Assets/_Project/Scripts/Items/ItemInventory.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A list of entries with an optional entry limit (0 = uncapped). A non-stackable item takes one entry; a stack takes
    /// one entry up to its definition's maximum. No grid, no weight. Mutating methods never lose units: Add returns what
    /// did not fit, Remove returns what was taken.
    /// </summary>
    [Serializable]
    public sealed class ItemInventory
    {
        [SerializeField] int capacity;
        [SerializeField] List<ItemEntry> entries = new List<ItemEntry>();

        public ItemInventory(int capacity = 0)
        {
            if (capacity < 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity cannot be negative.");
            this.capacity = capacity;
        }

        public int Capacity => capacity;
        public bool IsUncapped => capacity == 0;
        public int Count => entries.Count;
        public IReadOnlyList<ItemEntry> Entries => entries;
        public int FreeEntries => IsUncapped ? int.MaxValue : Math.Max(0, capacity - entries.Count);

        public ItemEntry Find(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
                return null;
            foreach (var entry in entries)
            {
                if (entry.InstanceId == instanceId)
                    return entry;
            }
            return null;
        }

        public int CountOf(string definitionId)
        {
            var total = 0;
            foreach (var entry in entries)
            {
                if (entry.DefinitionId == definitionId)
                    total += entry.Quantity;
            }
            return total;
        }

        /// <summary>How many units of this definition could be added right now.</summary>
        public int RoomFor(ItemDefinition definition)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            if (!definition.IsStackable)
                return FreeEntries;
            if (IsUncapped)
                return int.MaxValue;
            long room = (long)FreeEntries * definition.MaxStack;
            foreach (var entry in entries)
            {
                if (entry.DefinitionId == definition.Id)
                    room += Math.Max(0, definition.MaxStack - entry.Quantity);
            }
            return (int)Math.Min(room, int.MaxValue);
        }

        /// <summary>Adds units, filling open stacks first. Returns how many did not fit (0 when everything was added).</summary>
        public int Add(ItemDefinition definition, int quantity)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            if (quantity < 0)
                throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Quantity cannot be negative.");
            var left = quantity;
            if (definition.IsStackable)
            {
                foreach (var entry in entries)
                {
                    if (left == 0)
                        return 0;
                    if (entry.DefinitionId != definition.Id || entry.Quantity >= definition.MaxStack)
                        continue;
                    var take = Math.Min(left, definition.MaxStack - entry.Quantity);
                    entry.SetQuantity(entry.Quantity + take);
                    left -= take;
                }
            }
            while (left > 0 && FreeEntries > 0)
            {
                var take = Math.Min(left, definition.MaxStack);
                entries.Add(new ItemEntry(definition.Id, take));
                left -= take;
            }
            return left;
        }

        /// <summary>Removes up to `quantity` units from the entry. Returns how many were removed (0 for an unknown entry or a quantity below 1).</summary>
        public int Remove(string instanceId, int quantity)
        {
            var entry = Find(instanceId);
            if (entry == null || quantity < 1)
                return 0;
            var take = Math.Min(quantity, entry.Quantity);
            entry.SetQuantity(entry.Quantity - take);
            if (entry.Quantity == 0)
                entries.Remove(entry);
            return take;
        }

        /// <summary>Adds an existing entry as it is (a moved non-stackable keeps its instance id). False when full or the id is already here.</summary>
        internal bool TryAddEntry(ItemEntry entry)
        {
            if (entry == null || FreeEntries < 1 || Find(entry.InstanceId) != null)
                return false;
            entries.Add(entry);
            return true;
        }

        internal bool RemoveEntry(ItemEntry entry) => entries.Remove(entry);

        public ItemInventory Clone()
        {
            var copy = new ItemInventory(capacity);
            foreach (var entry in entries)
                copy.entries.Add(entry.Clone());
            return copy;
        }
    }
}
```

`Assets/_Project/Scripts/Items/ItemTransfer.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    public enum TransferFailure
    {
        None,
        InvalidQuantity,
        SameContainer,
        NotFound,
        UnknownItem,
        DestinationFull,
        /// <summary>The entry is equipped and must be unequipped first.</summary>
        Equipped,
        /// <summary>A mission is running: equipment and stash transfers are closed.</summary>
        Locked,
    }

    /// <summary>
    /// What a transfer did. Failure is None when everything that could be taken from the entry moved; DestinationFull with
    /// Moved above 0 is a partial move (IsPartial); every other failure moved nothing. Remaining is how many units of the
    /// entry are still at the source.
    /// </summary>
    public readonly struct TransferResult
    {
        public TransferResult(int requested, int moved, int remaining, TransferFailure failure)
        {
            Requested = requested;
            Moved = moved;
            Remaining = remaining;
            Failure = failure;
        }

        public int Requested { get; }
        public int Moved { get; }
        public int Remaining { get; }
        public TransferFailure Failure { get; }
        public bool IsPartial => Moved > 0 && Failure != TransferFailure.None;
        public bool Succeeded => Moved > 0 && Failure == TransferFailure.None;

        public static TransferResult Failed(TransferFailure failure, int requested) => new TransferResult(requested, 0, 0, failure);

        public string Describe(string itemName)
        {
            switch (Failure)
            {
                case TransferFailure.None:
                    return $"Moved {Moved} {itemName}.";
                case TransferFailure.DestinationFull:
                    return Moved > 0
                        ? $"Moved {Moved} of {Requested} {itemName}; {Remaining} left (no room)."
                        : $"No room for {itemName}.";
                case TransferFailure.Equipped:
                    return $"{itemName} is equipped. Unequip it first.";
                case TransferFailure.Locked:
                    return "Equipment can't be changed during a mission.";
                case TransferFailure.UnknownItem:
                    return "Unknown item; it cannot be moved.";
                case TransferFailure.NotFound:
                    return $"{itemName} is no longer there.";
                case TransferFailure.SameContainer:
                    return "Already there.";
                default:
                    return "Nothing to move.";
            }
        }
    }

    /// <summary>
    /// Moves units between two inventories as one step: it removes from the source only what the destination accepted, so a
    /// failure can neither duplicate nor destroy an item. A non-stackable entry moves whole and keeps its instance id.
    /// </summary>
    public static class ItemTransfer
    {
        public static TransferResult Move(ItemInventory source, ItemInventory destination, string instanceId, int quantity,
            ItemCatalogue catalogue)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (quantity < 1)
                return TransferResult.Failed(TransferFailure.InvalidQuantity, quantity);
            if (ReferenceEquals(source, destination))
                return TransferResult.Failed(TransferFailure.SameContainer, quantity);
            var entry = source.Find(instanceId);
            if (entry == null)
                return TransferResult.Failed(TransferFailure.NotFound, quantity);
            var definition = catalogue != null ? catalogue.Find(entry.DefinitionId) : null;
            if (definition == null)
                return TransferResult.Failed(TransferFailure.UnknownItem, quantity);

            var wanted = Math.Min(quantity, entry.Quantity);
            if (!definition.IsStackable)
            {
                if (!destination.TryAddEntry(entry))
                    return TransferResult.Failed(TransferFailure.DestinationFull, quantity);
                source.RemoveEntry(entry);
                return new TransferResult(quantity, 1, 0, TransferFailure.None);
            }

            var moved = Math.Min(wanted, destination.RoomFor(definition));
            if (moved < 1)
                return TransferResult.Failed(TransferFailure.DestinationFull, quantity);
            var notAdded = destination.Add(definition, moved);
            moved -= notAdded;
            source.Remove(instanceId, moved);
            var remaining = source.Find(instanceId) != null ? source.Find(instanceId).Quantity : 0;
            return new TransferResult(quantity, moved, remaining, moved < wanted ? TransferFailure.DestinationFull : TransferFailure.None);
        }
    }
}
```

Note: `ReferenceEquals` here compares two plain C# objects (not `UnityEngine.Object`), which is allowed.

- [ ] **Step 4: Run to verify it passes**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.ItemInventoryTests"`
Expected: `EXIT=0`, 13 passed.

---

### Task 3: Equipment, loadouts, inventory state

**Files:**
- Create: `Assets/_Project/Scripts/Items/EditResult.cs`, `OperativeEquipment.cs`, `OperativeLoadout.cs`, `InventoryState.cs`
- Test: `Assets/_Project/Tests/EditMode/OperativeLoadoutTests.cs`

**Interfaces:**
- Consumes: T1, T2.
- Produces: the T3 names.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/OperativeLoadoutTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class OperativeLoadoutTests
    {
        readonly List<Object> made = new List<Object>();
        ItemDefinition rifle, marksman, vest, medkit;
        ItemCatalogue catalogue;

        [SetUp]
        public void SetUp()
        {
            var ranged = Track(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));
            var mark = Track(CombatArchetype.Create("Marksman", CombatRole.Ranged, 16f, 40, 2.5f));
            rifle = Track(ItemDefinition.Create("rifle", "Service Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: ranged));
            marksman = Track(ItemDefinition.Create("marksman", "Marksman Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: mark));
            vest = Track(ItemDefinition.Create("vest", "Vest", ItemCategory.Armor, ItemSlot.Armor, modifiers: new StatModifiers { maxHealth = 20 }));
            medkit = Track(ItemDefinition.Create("medkit", "Medkit", ItemCategory.Consumable, ItemSlot.None, maxStack: 3, healAmount: 40));
            catalogue = Track(ItemCatalogue.Create(rifle, marksman, vest, medkit));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) Object.DestroyImmediate(o);
            made.Clear();
        }

        T Track<T>(T o) where T : Object { made.Add(o); return o; }

        [Test]
        public void Equip_ReferencesTheBagEntry_AndNeverCreatesACopy()
        {
            var loadout = new OperativeLoadout("op-1", 8);
            loadout.Bag.Add(rifle, 1);
            var id = loadout.Bag.Entries[0].InstanceId;

            var result = loadout.Equipment.TryEquip(loadout.Bag, catalogue, id);

            Assert.That(result.Ok, Is.True, result.Reason);
            Assert.That(loadout.Equipment.InstanceIdIn(ItemSlot.Weapon), Is.EqualTo(id));
            Assert.That(loadout.Bag.Count, Is.EqualTo(1), "equipping adds no entry");
            Assert.That(loadout.Resolve(catalogue).Weapon == rifle, Is.True);
        }

        [Test]
        public void Equip_Refuses_ItemsNotInTheBag_AndItemsWithoutASlot()
        {
            var loadout = new OperativeLoadout("op-1", 8);
            loadout.Bag.Add(medkit, 1);

            Assert.That(loadout.Equipment.TryEquip(loadout.Bag, catalogue, "missing").Ok, Is.False);
            Assert.That(loadout.Equipment.TryEquip(loadout.Bag, catalogue, loadout.Bag.Entries[0].InstanceId).Ok, Is.False);
            Assert.That(loadout.Equipment.InstanceIdIn(ItemSlot.Weapon), Is.Null.Or.Empty);
        }

        [Test]
        public void Equip_ASecondWeapon_ReplacesTheReference_AndKeepsBothInTheBag()
        {
            var loadout = new OperativeLoadout("op-1", 8);
            loadout.Bag.Add(rifle, 1);
            loadout.Bag.Add(marksman, 1);
            var first = loadout.Bag.Entries[0].InstanceId;
            var second = loadout.Bag.Entries[1].InstanceId;

            loadout.Equipment.TryEquip(loadout.Bag, catalogue, first);
            loadout.Equipment.TryEquip(loadout.Bag, catalogue, second);

            Assert.That(loadout.Equipment.InstanceIdIn(ItemSlot.Weapon), Is.EqualTo(second));
            Assert.That(loadout.Equipment.Contains(first), Is.False);
            Assert.That(loadout.Bag.Count, Is.EqualTo(2));
        }

        [Test]
        public void Unequip_ClearsOnlyTheReference()
        {
            var loadout = new OperativeLoadout("op-1", 8);
            loadout.Bag.Add(vest, 1);
            loadout.Equipment.TryEquip(loadout.Bag, catalogue, loadout.Bag.Entries[0].InstanceId);

            Assert.That(loadout.Equipment.Unequip(ItemSlot.Armor), Is.True);
            Assert.That(loadout.Equipment.Unequip(ItemSlot.Armor), Is.False);
            Assert.That(loadout.Bag.Count, Is.EqualTo(1));
            Assert.That(loadout.Resolve(catalogue).Armor == null, Is.True);
        }

        [Test]
        public void Resolve_IsSet_EvenWhenNothingIsEquipped_AndSumsModifiers()
        {
            var loadout = new OperativeLoadout("op-1", 8);
            Assert.That(loadout.Resolve(catalogue).IsSet, Is.True);
            Assert.That(loadout.Resolve(catalogue).Weapon == null, Is.True);

            loadout.Bag.Add(vest, 1);
            loadout.Equipment.TryEquip(loadout.Bag, catalogue, loadout.Bag.Entries[0].InstanceId);

            Assert.That(loadout.Resolve(catalogue).Modifiers.maxHealth, Is.EqualTo(20));
        }

        [Test]
        public void Resolve_IgnoresAReferenceWhoseEntryIsGone()
        {
            var loadout = new OperativeLoadout("op-1", 8);
            loadout.Bag.Add(vest, 1);
            var id = loadout.Bag.Entries[0].InstanceId;
            loadout.Equipment.TryEquip(loadout.Bag, catalogue, id);
            loadout.Bag.Remove(id, 1);   // bypasses the state rules on purpose

            Assert.That(loadout.Resolve(catalogue).Armor == null, Is.True);
        }

        [Test]
        public void State_RefusesToMoveAnEquippedItem_ThenAllowsItAfterUnequip()
        {
            var state = new InventoryState();
            var loadout = state.EnsureLoadout("op-1", 8);
            loadout.Bag.Add(rifle, 1);
            var id = loadout.Bag.Entries[0].InstanceId;
            loadout.Equipment.TryEquip(loadout.Bag, catalogue, id);

            var blocked = state.MoveToStash("op-1", id, 1, catalogue);
            loadout.Equipment.Unequip(ItemSlot.Weapon);
            var moved = state.MoveToStash("op-1", id, 1, catalogue);

            Assert.That(blocked.Failure, Is.EqualTo(TransferFailure.Equipped));
            Assert.That(moved.Moved, Is.EqualTo(1));
            Assert.That(state.Stash.Find(id), Is.Not.Null);
            Assert.That(loadout.Bag.Find(id), Is.Null);
        }

        [Test]
        public void State_MoveToBag_RespectsTheBagLimit_AndKeepsTheRestInTheStash()
        {
            var state = new InventoryState();
            var loadout = state.EnsureLoadout("op-1", 1);
            loadout.Bag.Add(vest, 1);
            state.Stash.Add(rifle, 1);

            var result = state.MoveToBag("op-1", state.Stash.Entries[0].InstanceId, 1, catalogue);

            Assert.That(result.Failure, Is.EqualTo(TransferFailure.DestinationFull));
            Assert.That(state.Stash.Count, Is.EqualTo(1));
        }

        [Test]
        public void State_UnknownOperative_IsNotFound()
        {
            var state = new InventoryState();
            Assert.That(state.MoveToStash("nobody", "x", 1, catalogue).Failure, Is.EqualTo(TransferFailure.NotFound));
        }

        [Test]
        public void EnsureLoadout_IsIdempotent_AndLoadoutsAreKeyedByOperativeId()
        {
            var state = new InventoryState();

            var a = state.EnsureLoadout("op-1", 8);
            var again = state.EnsureLoadout("op-1", 8);
            state.EnsureLoadout("op-2", 8);

            Assert.That(again, Is.SameAs(a));
            Assert.That(state.Loadouts, Has.Count.EqualTo(2));
            Assert.That(state.Loadout("op-2").OperativeId, Is.EqualTo("op-2"));
            Assert.That(state.Loadout("op-3"), Is.Null);
        }

        [Test]
        public void Clone_SharesNoCollection_AndKeepsInstanceIdsAndEquipment()
        {
            var state = new InventoryState();
            var loadout = state.EnsureLoadout("op-1", 8);
            loadout.Bag.Add(rifle, 1);
            var id = loadout.Bag.Entries[0].InstanceId;
            loadout.Equipment.TryEquip(loadout.Bag, catalogue, id);
            state.Stash.Add(medkit, 2);

            var copy = state.Clone();
            copy.Loadout("op-1").Equipment.Unequip(ItemSlot.Weapon);
            copy.Loadout("op-1").Bag.Add(vest, 1);
            copy.Stash.Remove(copy.Stash.Entries[0].InstanceId, 2);

            Assert.That(copy.Loadout("op-1").Bag.Find(id), Is.Not.Null, "same item, same instance id");
            Assert.That(loadout.Equipment.InstanceIdIn(ItemSlot.Weapon), Is.EqualTo(id));
            Assert.That(loadout.Bag.Count, Is.EqualTo(1));
            Assert.That(state.Stash.CountOf("medkit"), Is.EqualTo(2));
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.OperativeLoadoutTests"`
Expected: `EXIT=1`, `error CS0246` for `OperativeLoadout`.

- [ ] **Step 3: Implement**

`Assets/_Project/Scripts/Items/EditResult.cs`:

```csharp
namespace Blackglass
{
    /// <summary>The outcome of an equipment edit: Ok, or the reason text to show.</summary>
    public readonly struct EditResult
    {
        EditResult(bool ok, string reason)
        {
            Ok = ok;
            Reason = reason ?? string.Empty;
        }

        public bool Ok { get; }
        public string Reason { get; }

        public static EditResult Success => new EditResult(true, string.Empty);

        public static EditResult Fail(string reason) => new EditResult(false, reason);
    }
}
```

`Assets/_Project/Scripts/Items/OperativeEquipment.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The definitions an operative has equipped right now, resolved from its loadout. IsSet is true for any resolved
    /// loadout (even with every slot empty) and false for the default value, which means "this unit has no equipment
    /// system" and leaves a unit's configuration exactly as the definition and its archetype give it.
    /// </summary>
    public readonly struct EquippedItems
    {
        public EquippedItems(ItemDefinition weapon, ItemDefinition armor, ItemDefinition utility)
        {
            IsSet = true;
            Weapon = weapon;
            Armor = armor;
            Utility = utility;
        }

        public bool IsSet { get; }
        public ItemDefinition Weapon { get; }
        public ItemDefinition Armor { get; }
        public ItemDefinition Utility { get; }

        /// <summary>The passive modifiers of everything equipped, summed.</summary>
        public StatModifiers Modifiers
        {
            get
            {
                var total = default(StatModifiers);
                if (Weapon != null)
                    total = StatModifiers.Combine(total, Weapon.Modifiers);
                if (Armor != null)
                    total = StatModifiers.Combine(total, Armor.Modifiers);
                if (Utility != null)
                    total = StatModifiers.Combine(total, Utility.Modifiers);
                return total;
            }
        }
    }

    /// <summary>
    /// Three equipment slots, each holding the instance id of an entry in the owner's bag. Equipping never copies an item:
    /// the entry stays in the bag and the slot points at it. A slot whose entry is gone resolves to empty.
    /// </summary>
    [Serializable]
    public sealed class OperativeEquipment
    {
        [SerializeField] string weapon = "";
        [SerializeField] string armor = "";
        [SerializeField] string utility = "";

        public string InstanceIdIn(ItemSlot slot)
        {
            switch (slot)
            {
                case ItemSlot.Weapon: return weapon;
                case ItemSlot.Armor: return armor;
                case ItemSlot.Utility: return utility;
                default: return string.Empty;
            }
        }

        public bool Contains(string instanceId) =>
            !string.IsNullOrEmpty(instanceId) && (weapon == instanceId || armor == instanceId || utility == instanceId);

        public EditResult TryEquip(ItemInventory bag, ItemCatalogue catalogue, string instanceId)
        {
            var entry = bag != null ? bag.Find(instanceId) : null;
            if (entry == null)
                return EditResult.Fail("That item is not in this bag.");
            var definition = catalogue != null ? catalogue.Find(entry.DefinitionId) : null;
            if (definition == null)
                return EditResult.Fail($"Unknown item '{entry.DefinitionId}' cannot be equipped.");
            if (definition.Slot == ItemSlot.None)
                return EditResult.Fail($"{definition.DisplayName} can't be equipped.");
            Set(definition.Slot, instanceId);
            return EditResult.Success;
        }

        public bool Unequip(ItemSlot slot)
        {
            if (string.IsNullOrEmpty(InstanceIdIn(slot)))
                return false;
            Set(slot, string.Empty);
            return true;
        }

        public EquippedItems Resolve(ItemInventory bag, ItemCatalogue catalogue) =>
            new EquippedItems(Lookup(bag, catalogue, weapon), Lookup(bag, catalogue, armor), Lookup(bag, catalogue, utility));

        static ItemDefinition Lookup(ItemInventory bag, ItemCatalogue catalogue, string instanceId)
        {
            var entry = bag != null ? bag.Find(instanceId) : null;
            return entry != null && catalogue != null ? catalogue.Find(entry.DefinitionId) : null;
        }

        void Set(ItemSlot slot, string instanceId)
        {
            switch (slot)
            {
                case ItemSlot.Weapon: weapon = instanceId; break;
                case ItemSlot.Armor: armor = instanceId; break;
                case ItemSlot.Utility: utility = instanceId; break;
            }
        }

        public OperativeEquipment Clone() => new OperativeEquipment { weapon = weapon, armor = armor, utility = utility };
    }
}
```

`Assets/_Project/Scripts/Items/OperativeLoadout.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>One operative's carried items and what is equipped from them, keyed by the stable operative id.</summary>
    [Serializable]
    public sealed class OperativeLoadout
    {
        [SerializeField] string operativeId;
        [SerializeField] ItemInventory bag;
        [SerializeField] OperativeEquipment equipment = new OperativeEquipment();

        public OperativeLoadout(string operativeId, int bagCapacity)
        {
            if (string.IsNullOrWhiteSpace(operativeId))
                throw new ArgumentException("A loadout needs an operative id.", nameof(operativeId));
            this.operativeId = operativeId;
            bag = new ItemInventory(bagCapacity);
        }

        OperativeLoadout() { }

        public string OperativeId => operativeId;
        public ItemInventory Bag => bag;
        public OperativeEquipment Equipment => equipment;

        public EquippedItems Resolve(ItemCatalogue catalogue) => equipment.Resolve(bag, catalogue);

        public OperativeLoadout Clone() => new OperativeLoadout { operativeId = operativeId, bag = bag.Clone(), equipment = equipment.Clone() };
    }
}
```

`Assets/_Project/Scripts/Items/InventoryState.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// All owned items: the shared stash (uncapped) and one loadout per operative id. Plain serializable data with no
    /// Unity object references (a future save stores it as it is). Clone() is a deep copy that keeps every instance id: the
    /// mission's working state is the same items in separate collections.
    /// </summary>
    [Serializable]
    public sealed class InventoryState
    {
        [SerializeField] ItemInventory stash = new ItemInventory(0);
        [SerializeField] List<OperativeLoadout> loadouts = new List<OperativeLoadout>();

        public ItemInventory Stash => stash;
        public IReadOnlyList<OperativeLoadout> Loadouts => loadouts;

        public OperativeLoadout Loadout(string operativeId)
        {
            if (string.IsNullOrEmpty(operativeId))
                return null;
            foreach (var loadout in loadouts)
            {
                if (loadout.OperativeId == operativeId)
                    return loadout;
            }
            return null;
        }

        /// <summary>The operative's loadout, created empty with this bag size when it does not exist yet.</summary>
        public OperativeLoadout EnsureLoadout(string operativeId, int bagCapacity)
        {
            var existing = Loadout(operativeId);
            if (existing != null)
                return existing;
            var made = new OperativeLoadout(operativeId, bagCapacity);
            loadouts.Add(made);
            return made;
        }

        public TransferResult MoveToStash(string operativeId, string instanceId, int quantity, ItemCatalogue catalogue)
        {
            var loadout = Loadout(operativeId);
            if (loadout == null)
                return TransferResult.Failed(TransferFailure.NotFound, quantity);
            if (loadout.Equipment.Contains(instanceId))
                return TransferResult.Failed(TransferFailure.Equipped, quantity);
            return ItemTransfer.Move(loadout.Bag, stash, instanceId, quantity, catalogue);
        }

        public TransferResult MoveToBag(string operativeId, string instanceId, int quantity, ItemCatalogue catalogue)
        {
            var loadout = Loadout(operativeId);
            if (loadout == null)
                return TransferResult.Failed(TransferFailure.NotFound, quantity);
            return ItemTransfer.Move(stash, loadout.Bag, instanceId, quantity, catalogue);
        }

        public InventoryState Clone()
        {
            var copy = new InventoryState { stash = stash.Clone() };
            foreach (var loadout in loadouts)
                copy.loadouts.Add(loadout.Clone());
            return copy;
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.OperativeLoadoutTests"`
Expected: `EXIT=0`, 11 passed.

---

### Task 4: Session, working state, settlement, starter seeding, scene component

**Files:**
- Create: `Assets/_Project/Scripts/Items/StarterLoadout.cs`, `InventorySession.cs`, `SquadInventory.cs`
- Test: `Assets/_Project/Tests/EditMode/InventorySessionTests.cs`

**Interfaces:**
- Consumes: T1–T3; `OperativeDefinition.Id`, `SquadRoster.Members`, `MissionDirector.MissionFinished`, `MissionPhase`.
- Produces: the T4 names. `MissionDirector.InstanceId` is added in Task 8; `SquadInventory` reads it there, so in this task `SquadInventory.HandleMissionFinished(MissionPhase)` takes the id from a `Func<string>` set by `Initialize` (the director's `InstanceId` is wired in Task 8).

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/InventorySessionTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class InventorySessionTests
    {
        readonly List<Object> made = new List<Object>();
        ItemDefinition rifle, marksman, vest, medkit;
        ItemCatalogue catalogue;

        [SetUp]
        public void SetUp()
        {
            var ranged = Track(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));
            var mark = Track(CombatArchetype.Create("Marksman", CombatRole.Ranged, 16f, 40, 2.5f));
            rifle = Track(ItemDefinition.Create("rifle", "Service Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: ranged));
            marksman = Track(ItemDefinition.Create("marksman", "Marksman Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: mark));
            vest = Track(ItemDefinition.Create("vest", "Vest", ItemCategory.Armor, ItemSlot.Armor));
            medkit = Track(ItemDefinition.Create("medkit", "Medkit", ItemCategory.Consumable, ItemSlot.None, maxStack: 3, healAmount: 40));
            catalogue = Track(ItemCatalogue.Create(rifle, marksman, vest, medkit));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) Object.DestroyImmediate(o);
            made.Clear();
        }

        T Track<T>(T o) where T : Object { made.Add(o); return o; }

        InventorySession NewSession()
        {
            var session = new InventorySession(catalogue, 8);
            session.EnsureOperative("a");
            session.EnsureOperative("b");
            session.Session.Loadout("a").Bag.Add(rifle, 1);
            session.Session.Loadout("a").Bag.Add(medkit, 2);
            session.Session.Stash.Add(marksman, 1);
            return session;
        }

        [Test]
        public void Equip_AndStashTransfers_WorkInLoadoutMode_AndRaiseChanged()
        {
            var session = NewSession();
            var changes = 0;
            session.Changed += () => changes++;
            var rifleId = session.Session.Loadout("a").Bag.Entries[0].InstanceId;

            Assert.That(session.Mode, Is.EqualTo(InventoryMode.Loadout));
            Assert.That(session.Equip("a", rifleId).Ok, Is.True);
            Assert.That(session.EquippedFor(session.Session, "a").Weapon == rifle, Is.True);
            Assert.That(session.ToStash("a", rifleId, 1).Failure, Is.EqualTo(TransferFailure.Equipped));
            Assert.That(session.Unequip("a", ItemSlot.Weapon).Ok, Is.True);
            Assert.That(session.ToStash("a", rifleId, 1).Moved, Is.EqualTo(1));
            Assert.That(changes, Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void Edits_AreRefusedWhileAMissionRuns_AndStateIsUnchanged()
        {
            var session = NewSession();
            var rifleId = session.Session.Loadout("a").Bag.Entries[0].InstanceId;
            session.BeginMission("m1");

            var equip = session.Equip("a", rifleId);
            var unequip = session.Unequip("a", ItemSlot.Weapon);
            var move = session.ToStash("a", rifleId, 1);
            var back = session.FromStash("a", session.Session.Stash.Entries[0].InstanceId, 1);

            Assert.That(session.Mode, Is.EqualTo(InventoryMode.InMission));
            Assert.That(equip.Ok, Is.False);
            StringAssert.Contains("during a mission", equip.Reason);
            Assert.That(unequip.Ok, Is.False);
            Assert.That(move.Failure, Is.EqualTo(TransferFailure.Locked));
            Assert.That(back.Failure, Is.EqualTo(TransferFailure.Locked));
            Assert.That(session.Session.Loadout("a").Bag.Count, Is.EqualTo(2));
            Assert.That(session.Session.Loadout("a").Equipment.InstanceIdIn(ItemSlot.Weapon), Is.Null.Or.Empty);
        }

        [Test]
        public void Working_IsADeepCopy_PickupsDoNotTouchTheSessionBeforeSettlement()
        {
            var session = NewSession();
            session.BeginMission("m1");

            session.Working.Loadout("b").Bag.Add(vest, 1);
            session.Working.Stash.Add(medkit, 1);
            session.Working.Loadout("a").Bag.Remove(session.Working.Loadout("a").Bag.Entries[1].InstanceId, 2);

            Assert.That(session.Session.Loadout("b").Bag.Count, Is.EqualTo(0));
            Assert.That(session.Session.Stash.CountOf("medkit"), Is.EqualTo(0));
            Assert.That(session.Session.Loadout("a").Bag.CountOf("medkit"), Is.EqualTo(2));
            Assert.That(session.Session, Is.Not.SameAs(session.Working));
        }

        [Test]
        public void Settle_Success_CommitsTheWorkingStateExactlyOnce()
        {
            var session = NewSession();
            session.BeginMission("m1");
            session.Working.Loadout("b").Bag.Add(vest, 1);

            Assert.That(session.Settle("m1", true), Is.True);
            Assert.That(session.Session.Loadout("b").Bag.CountOf("vest"), Is.EqualTo(1));
            Assert.That(session.Mode, Is.EqualTo(InventoryMode.Loadout));
            Assert.That(session.Working, Is.Null);

            // A duplicate callback, and a later failure for the same id, change nothing.
            Assert.That(session.Settle("m1", true), Is.False);
            Assert.That(session.Settle("m1", false), Is.False);
            Assert.That(session.Session.Loadout("b").Bag.CountOf("vest"), Is.EqualTo(1));
        }

        [Test]
        public void Settle_Failure_OrAbort_DiscardsTheWorkingState()
        {
            var session = NewSession();
            session.BeginMission("m1");
            session.Working.Loadout("b").Bag.Add(vest, 1);
            Assert.That(session.Settle("m1", false), Is.False);
            Assert.That(session.Session.Loadout("b").Bag.Count, Is.EqualTo(0));

            session.BeginMission("m2");
            session.Working.Loadout("b").Bag.Add(vest, 1);
            session.AbortMission();
            Assert.That(session.Session.Loadout("b").Bag.Count, Is.EqualTo(0));
            Assert.That(session.Mode, Is.EqualTo(InventoryMode.Loadout));
        }

        [Test]
        public void Settle_WithAnotherMissionsId_IsIgnored()
        {
            var session = NewSession();
            session.BeginMission("m2");
            session.Working.Loadout("b").Bag.Add(vest, 1);

            Assert.That(session.Settle("m1", true), Is.False);
            Assert.That(session.Mode, Is.EqualTo(InventoryMode.InMission));
            Assert.That(session.Session.Loadout("b").Bag.Count, Is.EqualTo(0));
        }

        [Test]
        public void BeginMission_WhileOneRuns_DiscardsTheOldWorkingStateAndStartsFresh()
        {
            var session = NewSession();
            session.BeginMission("m1");
            session.Working.Loadout("b").Bag.Add(vest, 1);

            session.BeginMission("m2");

            Assert.That(session.ActiveMissionId, Is.EqualTo("m2"));
            Assert.That(session.Working.Loadout("b").Bag.Count, Is.EqualTo(0));
            Assert.That(session.Settle("m1", true), Is.False);
        }

        [Test]
        public void UnsecuredCount_IsWhatTheMissionAddedBeyondTheStart()
        {
            var session = NewSession();
            session.BeginMission("m1");
            session.Working.Loadout("a").Bag.Add(medkit, 1);
            session.Working.Loadout("a").Bag.Add(vest, 1);

            Assert.That(session.UnsecuredCount("a", "medkit"), Is.EqualTo(1));
            Assert.That(session.UnsecuredCount("a", "vest"), Is.EqualTo(1));
            Assert.That(session.UnsecuredCount("a", "rifle"), Is.EqualTo(0));
            Assert.That(session.UnsecuredCount("b", "vest"), Is.EqualTo(0));
        }

        [Test]
        public void UsingAConsumable_InTheMission_DoesNotLowerUnsecuredBelowZero()
        {
            var session = NewSession();
            session.BeginMission("m1");
            session.Working.Loadout("a").Bag.Remove(session.Working.Loadout("a").Bag.Entries[1].InstanceId, 1);

            Assert.That(session.UnsecuredCount("a", "medkit"), Is.EqualTo(0));
        }

        [Test]
        public void SeedStarter_GrantsAndEquipsOnce_AndNeverRegrants()
        {
            var session = new InventorySession(catalogue, 8);
            var starter = StarterLoadout.Create(
                new Dictionary<string, StarterGrant[]>
                {
                    ["a"] = new[]
                    {
                        new StarterGrant { item = rifle, quantity = 1, equip = true },
                        new StarterGrant { item = medkit, quantity = 2, equip = false },
                    },
                },
                new[] { new StarterGrant { item = vest, quantity = 1, equip = false } });
            made.Add(starter);

            var problems = new List<string>();
            session.SeedStarter(starter, new[] { "a", "b" }, problems);
            var bagAfterFirst = session.Session.Loadout("a").Bag.Count;
            session.SeedStarter(starter, new[] { "a", "b" }, problems);

            Assert.That(problems, Is.Empty);
            Assert.That(session.Session.Loadout("a").Bag.Count, Is.EqualTo(bagAfterFirst));
            Assert.That(session.Session.Loadout("a").Bag.CountOf("rifle"), Is.EqualTo(1));
            Assert.That(session.EquippedFor(session.Session, "a").Weapon == rifle, Is.True);
            Assert.That(session.Session.Loadout("b"), Is.Not.Null);
            Assert.That(session.Session.Stash.CountOf("vest"), Is.EqualTo(1));
        }

        [Test]
        public void SeedStarter_ReportsAGrantThatDoesNotFit_WithoutThrowing()
        {
            var session = new InventorySession(catalogue, 1);
            var starter = StarterLoadout.Create(
                new Dictionary<string, StarterGrant[]>
                {
                    ["a"] = new[]
                    {
                        new StarterGrant { item = rifle, quantity = 1, equip = true },
                        new StarterGrant { item = vest, quantity = 1, equip = false },
                    },
                },
                new StarterGrant[0]);
            made.Add(starter);

            var problems = new List<string>();
            session.SeedStarter(starter, new[] { "a" }, problems);

            Assert.That(problems, Has.Count.EqualTo(1));
            StringAssert.Contains("vest", problems[0].ToLowerInvariant());
        }

        [Test]
        public void SquadInventory_SettlesOnTheDirectorsMissionFinished_UsingTheGivenInstanceId()
        {
            var host = new GameObject("inv");
            made.Add(host);
            var inventory = host.AddComponent<SquadInventory>();
            var current = "m1";
            inventory.Initialize(catalogue, null, null, 8, () => current);
            inventory.Core.EnsureOperative("a");
            inventory.Core.BeginMission("m1");
            inventory.Core.Working.Loadout("a").Bag.Add(vest, 1);

            inventory.HandleMissionFinished(MissionPhase.Success);
            inventory.HandleMissionFinished(MissionPhase.Success);

            Assert.That(inventory.Core.Session.Loadout("a").Bag.CountOf("vest"), Is.EqualTo(1));
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.Loadout));
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.InventorySessionTests"`
Expected: `EXIT=1`, `error CS0246` for `InventorySession`.

- [ ] **Step 3: Implement**

`Assets/_Project/Scripts/Items/StarterLoadout.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>One starter grant: an item, how many, and whether to equip it (the first matching entry of the bag).</summary>
    [Serializable]
    public struct StarterGrant
    {
        public ItemDefinition item;
        [Min(1)] public int quantity;
        public bool equip;
    }

    /// <summary>One operative's starter grants, matched by the operative definition's stable id.</summary>
    [Serializable]
    public sealed class StarterOperative
    {
        public OperativeDefinition operative;
        public StarterGrant[] items = new StarterGrant[0];
    }

    /// <summary>
    /// The items a fresh prototype session starts with: per-operative grants and the shared stash. Authored data; seeding
    /// is done once by InventorySession.SeedStarter and never repeated for an operative that already received it.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Starter Loadout", fileName = "StarterLoadout")]
    public sealed class StarterLoadout : ScriptableObject
    {
        [SerializeField] StarterOperative[] operatives = new StarterOperative[0];
        [SerializeField] StarterGrant[] stash = new StarterGrant[0];

        // Test-only source: grants keyed by operative id, used when the asset has no operative definitions.
        Dictionary<string, StarterGrant[]> byId;

        public IReadOnlyList<StarterGrant> Stash => stash;

        public IReadOnlyList<StarterGrant> For(string operativeId)
        {
            if (byId != null && byId.TryGetValue(operativeId, out var direct))
                return direct;
            if (operatives != null)
            {
                foreach (var entry in operatives)
                {
                    if (entry != null && entry.operative != null && entry.operative.Id == operativeId)
                        return entry.items ?? new StarterGrant[0];
                }
            }
            return Array.Empty<StarterGrant>();
        }

        internal static StarterLoadout Create(Dictionary<string, StarterGrant[]> grantsById, StarterGrant[] stash)
        {
            var asset = CreateInstance<StarterLoadout>();
            asset.byId = grantsById;
            asset.stash = stash ?? new StarterGrant[0];
            return asset;
        }

        internal static StarterLoadout Create(StarterOperative[] operatives, StarterGrant[] stash)
        {
            var asset = CreateInstance<StarterLoadout>();
            asset.operatives = operatives ?? new StarterOperative[0];
            asset.stash = stash ?? new StarterGrant[0];
            return asset;
        }
    }
}
```

`Assets/_Project/Scripts/Items/InventorySession.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Blackglass
{
    public enum InventoryMode
    {
        /// <summary>No mission is running: equipment and the stash can be edited.</summary>
        Loadout,
        /// <summary>A mission working state exists: pickups and item use change it; equipment is locked.</summary>
        InMission,
    }

    /// <summary>
    /// The inventory rules, with no scene dependency. Holds the persistent session state and, while a mission runs, a deep
    /// copy of it (the working state). Pickups and consumable use change only the working state; a Success settlement
    /// replaces the session with it exactly once, and a failure, an abort or a restart discards it. Equipment and stash
    /// edits act on the session and are refused while a mission runs. Nothing here touches XP or progression.
    /// </summary>
    public sealed class InventorySession
    {
        public const string LockedReason = "Equipment can't be changed during a mission.";

        readonly ItemCatalogue catalogue;
        readonly int bagCapacity;
        readonly HashSet<string> seededOperatives = new HashSet<string>();
        readonly Dictionary<string, Dictionary<string, int>> startCounts = new Dictionary<string, Dictionary<string, int>>();
        bool stashSeeded;

        public InventorySession(ItemCatalogue catalogue, int bagCapacity)
        {
            if (bagCapacity < 0)
                throw new ArgumentOutOfRangeException(nameof(bagCapacity), bagCapacity, "A bag cannot have a negative limit.");
            this.catalogue = catalogue;
            this.bagCapacity = bagCapacity;
            Session = new InventoryState();
        }

        public ItemCatalogue Catalogue => catalogue;
        public int BagCapacity => bagCapacity;
        /// <summary>The persistent state. Replaced (never mutated by pickups) when a mission settles.</summary>
        public InventoryState Session { get; private set; }
        /// <summary>The running mission's copy, or null outside a mission.</summary>
        public InventoryState Working { get; private set; }
        /// <summary>The working state during a mission, else the session: what the player is looking at.</summary>
        public InventoryState Active => Working ?? Session;
        public InventoryMode Mode => Working != null ? InventoryMode.InMission : InventoryMode.Loadout;
        public string ActiveMissionId { get; private set; }

        /// <summary>Raised after any change to the session or the working state made through this class.</summary>
        public event Action Changed;

        public void NotifyChanged() => Changed?.Invoke();

        public void EnsureOperative(string operativeId)
        {
            if (Session.Loadout(operativeId) == null)
            {
                Session.EnsureLoadout(operativeId, bagCapacity);
                Changed?.Invoke();
            }
        }

        // ---- mission boundary ----

        /// <summary>Starts a mission: the working state is a deep copy of the session. A previous unsettled mission is discarded.</summary>
        public bool BeginMission(string missionId)
        {
            if (string.IsNullOrWhiteSpace(missionId))
                throw new ArgumentException("A mission needs an id.", nameof(missionId));
            Working = Session.Clone();
            ActiveMissionId = missionId;
            RecordStart();
            Changed?.Invoke();
            return true;
        }

        /// <summary>Drops the working state (generation failed, restart, clear). The session is untouched.</summary>
        public void AbortMission()
        {
            if (Working == null)
                return;
            Working = null;
            ActiveMissionId = null;
            startCounts.Clear();
            Changed?.Invoke();
        }

        /// <summary>
        /// Ends the mission with this id. Success replaces the session with the working state; anything else discards it.
        /// Returns true only when items were committed. A call for any other id, or a second call, does nothing.
        /// </summary>
        public bool Settle(string missionId, bool success)
        {
            if (Working == null || string.IsNullOrEmpty(missionId) || missionId != ActiveMissionId)
                return false;
            if (success)
                Session = Working;
            Working = null;
            ActiveMissionId = null;
            startCounts.Clear();
            Changed?.Invoke();
            return success;
        }

        void RecordStart()
        {
            startCounts.Clear();
            foreach (var loadout in Working.Loadouts)
            {
                var counts = new Dictionary<string, int>();
                foreach (var entry in loadout.Bag.Entries)
                {
                    counts.TryGetValue(entry.DefinitionId, out var current);
                    counts[entry.DefinitionId] = current + entry.Quantity;
                }
                startCounts[loadout.OperativeId] = counts;
            }
        }

        /// <summary>Units of this definition the operative carries beyond what it started the mission with (not yet secured). 0 outside a mission.</summary>
        public int UnsecuredCount(string operativeId, string definitionId)
        {
            if (Working == null)
                return 0;
            var loadout = Working.Loadout(operativeId);
            if (loadout == null)
                return 0;
            var start = 0;
            if (startCounts.TryGetValue(operativeId, out var counts))
                counts.TryGetValue(definitionId, out start);
            return Math.Max(0, loadout.Bag.CountOf(definitionId) - start);
        }

        // ---- loadout edits (session only, closed during a mission) ----

        public EditResult Equip(string operativeId, string instanceId)
        {
            if (Mode == InventoryMode.InMission)
                return EditResult.Fail(LockedReason);
            var loadout = Session.Loadout(operativeId);
            if (loadout == null)
                return EditResult.Fail("Unknown operative.");
            var result = loadout.Equipment.TryEquip(loadout.Bag, catalogue, instanceId);
            if (result.Ok)
                Changed?.Invoke();
            return result;
        }

        public EditResult Unequip(string operativeId, ItemSlot slot)
        {
            if (Mode == InventoryMode.InMission)
                return EditResult.Fail(LockedReason);
            var loadout = Session.Loadout(operativeId);
            if (loadout == null)
                return EditResult.Fail("Unknown operative.");
            if (!loadout.Equipment.Unequip(slot))
                return EditResult.Fail("Nothing is equipped there.");
            Changed?.Invoke();
            return EditResult.Success;
        }

        public TransferResult ToStash(string operativeId, string instanceId, int quantity)
        {
            if (Mode == InventoryMode.InMission)
                return TransferResult.Failed(TransferFailure.Locked, quantity);
            var result = Session.MoveToStash(operativeId, instanceId, quantity, catalogue);
            if (result.Moved > 0)
                Changed?.Invoke();
            return result;
        }

        public TransferResult FromStash(string operativeId, string instanceId, int quantity)
        {
            if (Mode == InventoryMode.InMission)
                return TransferResult.Failed(TransferFailure.Locked, quantity);
            var result = Session.MoveToBag(operativeId, instanceId, quantity, catalogue);
            if (result.Moved > 0)
                Changed?.Invoke();
            return result;
        }

        public EquippedItems EquippedFor(InventoryState state, string operativeId)
        {
            var loadout = state != null ? state.Loadout(operativeId) : null;
            return loadout != null ? loadout.Resolve(catalogue) : default;
        }

        // ---- starter seeding ----

        /// <summary>
        /// Grants the starter items once per operative id (and the stash once): calling it again, or after a mission,
        /// never grants anything twice. A grant that does not fit, or an item that is missing, adds a problem message.
        /// </summary>
        public void SeedStarter(StarterLoadout starter, IEnumerable<string> operativeIds, List<string> problems)
        {
            if (starter == null)
                return;
            foreach (var id in operativeIds)
            {
                if (string.IsNullOrWhiteSpace(id) || !seededOperatives.Add(id))
                    continue;
                var loadout = Session.EnsureLoadout(id, bagCapacity);
                foreach (var grant in starter.For(id))
                {
                    if (grant.item == null)
                    {
                        problems.Add($"Starter loadout for {id}: a grant has no item.");
                        continue;
                    }
                    var leftover = loadout.Bag.Add(grant.item, Math.Max(1, grant.quantity));
                    if (leftover > 0)
                        problems.Add($"Starter loadout for {id}: no room for {leftover} x {grant.item.DisplayName} ({grant.item.Id}).");
                    if (!grant.equip)
                        continue;
                    var entry = FirstOf(loadout.Bag, grant.item.Id);
                    if (entry == null)
                    {
                        problems.Add($"Starter loadout for {id}: could not equip {grant.item.DisplayName} ({grant.item.Id}).");
                        continue;
                    }
                    var equipped = loadout.Equipment.TryEquip(loadout.Bag, catalogue, entry.InstanceId);
                    if (!equipped.Ok)
                        problems.Add($"Starter loadout for {id}: {equipped.Reason}");
                }
            }
            if (!stashSeeded)
            {
                stashSeeded = true;
                foreach (var grant in starter.Stash)
                {
                    if (grant.item == null)
                    {
                        problems.Add("Starter loadout: a stash grant has no item.");
                        continue;
                    }
                    Session.Stash.Add(grant.item, Math.Max(1, grant.quantity));
                }
            }
            Changed?.Invoke();
        }

        static ItemEntry FirstOf(ItemInventory bag, string definitionId)
        {
            foreach (var entry in bag.Entries)
            {
                if (entry.DefinitionId == definitionId)
                    return entry;
            }
            return null;
        }
    }
}
```

`Assets/_Project/Scripts/Items/SquadInventory.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The scene-level owner of the squad's items (decision 043): wraps an InventorySession, builds it from the roster,
    /// seeds the starter items once, and settles the running mission when the director reports its result. Lives next to
    /// the SquadRoster so it outlasts every generated mission. In memory only: nothing is written to disk.
    /// </summary>
    [DefaultExecutionOrder(5)]   // after SquadRoster.Awake (order 0) has built its members
    public sealed class SquadInventory : MonoBehaviour
    {
        [SerializeField] ItemCatalogue catalogue;
        [SerializeField] StarterLoadout starter;
        [SerializeField] SquadRoster roster;
        [SerializeField] MissionDirector director;
        [SerializeField, Min(1)] int bagCapacity = 8;
        // The scene opens on the loadout panel and waits for Deploy instead of generating a mission at once.
        [SerializeField] bool startInLoadout = true;

        InventorySession core;
        Func<string> missionIdSource;
        bool subscribed;

        public ItemCatalogue Catalogue => catalogue;
        public bool StartInLoadout => startInLoadout;
        public SquadRoster Roster => roster;

        /// <summary>The rules and state. Built on first use.</summary>
        public InventorySession Core
        {
            get
            {
                if (core == null)
                    Build();
                return core;
            }
        }

        void Awake()
        {
            if (core == null)
                Build();
        }

        void OnEnable() => Subscribe();

        void OnDisable() => Unsubscribe();

        internal void Initialize(ItemCatalogue itemCatalogue, StarterLoadout starterLoadout, SquadRoster squad, int bag,
            Func<string> currentMissionId, MissionDirector missionDirector = null)
        {
            Unsubscribe();
            catalogue = itemCatalogue;
            starter = starterLoadout;
            roster = squad;
            director = missionDirector;
            bagCapacity = Mathf.Max(1, bag);
            missionIdSource = currentMissionId;
            core = null;
            Build();
            if (isActiveAndEnabled)
                Subscribe();
        }

        void Build()
        {
            var errors = new List<string>();
            if (catalogue == null)
                errors.Add($"{name}: the squad inventory has no item catalogue.");
            else
                catalogue.Validate(errors);
            core = new InventorySession(catalogue, bagCapacity);
            if (roster != null)
            {
                var ids = new List<string>();
                foreach (var member in roster.Members)
                    ids.Add(member.Id);
                foreach (var id in ids)
                    core.EnsureOperative(id);
                core.SeedStarter(starter, ids, errors);
            }
            foreach (var error in errors)
                Debug.LogError(error, this);
        }

        void Subscribe()
        {
            if (subscribed || director == null)
                return;
            director.MissionFinished += HandleMissionFinished;
            subscribed = true;
        }

        void Unsubscribe()
        {
            if (!subscribed)
                return;
            if (director != null)
                director.MissionFinished -= HandleMissionFinished;
            subscribed = false;
        }

        /// <summary>Starts a mission for this instance id: makes sure every roster member has a loadout, then clones the session.</summary>
        public void BeginMission(string missionId)
        {
            if (roster != null)
            {
                foreach (var member in roster.Members)
                    Core.EnsureOperative(member.Id);
            }
            Core.BeginMission(missionId);
        }

        public void AbortMission() => Core.AbortMission();

        internal void HandleMissionFinished(MissionPhase phase)
        {
            var id = missionIdSource != null ? missionIdSource() : director != null ? director.InstanceId : null;
            Core.Settle(id, phase == MissionPhase.Success);
        }

        /// <summary>The operative's working loadout during a mission, else null.</summary>
        public OperativeLoadout WorkingLoadout(string operativeId) =>
            core != null && core.Working != null ? core.Working.Loadout(operativeId) : null;

        /// <summary>What the operative has equipped in the active state (working during a mission, else the session); default when unknown.</summary>
        public EquippedItems EquippedFor(string operativeId) => Core.EquippedFor(Core.Active, operativeId);
    }
}
```

`director.InstanceId` does not exist until Task 8, so this does not compile yet. Make the director reference compile-safe now: temporarily keep only the `missionIdSource` path and add the director fallback in Task 8. Replace the line in `HandleMissionFinished` with:

```csharp
            var id = missionIdSource != null ? missionIdSource() : null;
```

and remove the `director.InstanceId` fallback; Task 8 Step 3 restores it. (`Subscribe` still compiles because `MissionDirector.MissionFinished` already exists.)

- [ ] **Step 4: Run to verify it passes**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.InventorySessionTests"`
Expected: `EXIT=0`, 12 passed.

- [ ] **Step 5: Milestone-A checkpoint**

Run `Tools/run-tests.sh EditMode`; expected `EXIT=0` and the total equals the Task 0 baseline plus 5 + 13 + 11 + 12 new tests. Report `git status --short`.


---

## Milestone B — Equipment in the unit's configuration, weapon visuals

### Task 5: Equipped items in `EffectiveConfiguration`

**Files:**
- Modify: `Assets/_Project/Scripts/Operatives/EffectiveConfiguration.cs` (replace the file), `Assets/_Project/Scripts/Operatives/SquadRoster.cs:130-131`
- Test: `Assets/_Project/Tests/EditMode/EffectiveConfigurationEquipmentTests.cs`

**Interfaces:**
- Consumes: `EquippedItems` (T3), `ItemDefinition.Weapon/Modifiers` (T1).
- Produces: `EffectiveConfiguration.Evaluate(def, state, track, EquippedItems)`, `HasWeapon`, `WeaponName`; `SquadRoster.Evaluate(RosterMember, EquippedItems)`. The three-argument `Evaluate` and `Base` behave exactly as before (`HasWeapon == true`).

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/EffectiveConfigurationEquipmentTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class EffectiveConfigurationEquipmentTests
    {
        readonly List<Object> made = new List<Object>();
        OperativeDefinition darius;
        ProgressionTrack track;
        ItemDefinition rifle, marksman, blade, vest, boots;

        T Track<T>(T o) where T : Object { made.Add(o); return o; }

        [SetUp]
        public void SetUp()
        {
            var combat = Track(AdvancementChoice.Create("combat", "Combat Training", "", new StatModifiers { attackDamage = 0.15f }));
            track = Track(ProgressionTrack.Create(new[] { 0, 100, 250 }, new[] { combat }, 150, 50));
            var assault = Track(OperativeRole.Create("Assault", "", new StatModifiers { attackDamage = 0.2f }));
            var ranged = Track(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));
            var mark = Track(CombatArchetype.Create("Marksman", CombatRole.Ranged, 16f, 40, 2.5f));
            var melee = Track(CombatArchetype.Create("Melee", CombatRole.Melee, 2f, 25, 1f));
            var prefab = Track(new GameObject("UnitPrefab"));
            darius = Track(OperativeDefinition.Create("darius-id", "Darius", assault, prefab, 130, 5f, ranged, new AbilityDefinition[0]));
            rifle = Track(ItemDefinition.Create("rifle", "Service Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: ranged));
            marksman = Track(ItemDefinition.Create("marksman", "Marksman Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: mark));
            blade = Track(ItemDefinition.Create("blade", "Combat Blade", ItemCategory.Weapon, ItemSlot.Weapon, weapon: melee));
            vest = Track(ItemDefinition.Create("vest", "Vest", ItemCategory.Armor, ItemSlot.Armor, modifiers: new StatModifiers { maxHealth = 20 }));
            boots = Track(ItemDefinition.Create("boots", "Servo Boots", ItemCategory.Utility, ItemSlot.Utility, modifiers: new StatModifiers { moveSpeed = 0.5f }));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) Object.DestroyImmediate(o);
            made.Clear();
        }

        static PersistentOperativeState State() => new PersistentOperativeState("darius-id");

        [Test]
        public void DefaultEquipment_MeansNoEquipmentSystem_AndMatchesTheOldResult()
        {
            var before = EffectiveConfiguration.Evaluate(darius, State(), track);
            var withDefault = EffectiveConfiguration.Evaluate(darius, State(), track, default);

            Assert.That(withDefault.HasWeapon, Is.True);
            Assert.That(withDefault.AttackRange, Is.EqualTo(before.AttackRange));
            Assert.That(withDefault.AttackDamage, Is.EqualTo(before.AttackDamage));
            Assert.That(withDefault.MaxHealth, Is.EqualTo(before.MaxHealth));
            Assert.That(withDefault.WeaponName, Is.EqualTo("Ranged"));
        }

        [Test]
        public void EquippedWeapon_ReplacesTheDefinitionsArchetype_NeverAddsToIt()
        {
            var config = EffectiveConfiguration.Evaluate(darius, State(), track, new EquippedItems(marksman, null, null));

            Assert.That(config.HasWeapon, Is.True);
            Assert.That(config.WeaponName, Is.EqualTo("Marksman Rifle"));
            Assert.That(config.AttackRange, Is.EqualTo(16f), "the marksman's range, not the default's plus the marksman's");
            Assert.That(config.AttackInterval, Is.EqualTo(2.5f));
            Assert.That(config.AttackRole, Is.EqualTo(CombatRole.Ranged));
            // 40 damage, +20% from the Assault role.
            Assert.That(config.AttackDamage, Is.EqualTo(48));
        }

        [Test]
        public void AMeleeWeaponItem_GivesAMeleeUnit()
        {
            var config = EffectiveConfiguration.Evaluate(darius, State(), track, new EquippedItems(blade, null, null));

            Assert.That(config.AttackRole, Is.EqualTo(CombatRole.Melee));
            Assert.That(config.AttackRange, Is.EqualTo(2f));
            Assert.That(config.AttackDamage, Is.EqualTo(30));
        }

        [Test]
        public void ArmorAndUtility_AddTheirModifiers_ToHealthAndSpeed()
        {
            var plain = EffectiveConfiguration.Evaluate(darius, State(), track, new EquippedItems(rifle, null, null));
            var kitted = EffectiveConfiguration.Evaluate(darius, State(), track, new EquippedItems(rifle, vest, boots));

            Assert.That(kitted.MaxHealth, Is.EqualTo(plain.MaxHealth + 20));
            Assert.That(kitted.MoveSpeed, Is.EqualTo(plain.MoveSpeed + 0.5f).Within(1e-4f));
            Assert.That(kitted.AttackDamage, Is.EqualTo(plain.AttackDamage));
        }

        [Test]
        public void EmptyWeaponSlot_MeansNoWeapon_NotTheDefaultArchetype()
        {
            var config = EffectiveConfiguration.Evaluate(darius, State(), track, new EquippedItems(null, vest, null));

            Assert.That(config.HasWeapon, Is.False);
            Assert.That(config.AttackDamage, Is.EqualTo(0));
            Assert.That(config.WeaponName, Is.Empty);
            Assert.That(config.MaxHealth, Is.EqualTo(130 + 20), "armor still applies");
        }

        [Test]
        public void EvaluatingAgain_GivesTheSameResult_AndUnequippingReturnsToBase()
        {
            var equipped = new EquippedItems(rifle, vest, boots);
            var first = EffectiveConfiguration.Evaluate(darius, State(), track, equipped);
            for (var i = 0; i < 5; i++)
                EffectiveConfiguration.Evaluate(darius, State(), track, equipped);
            var again = EffectiveConfiguration.Evaluate(darius, State(), track, equipped);
            var bare = EffectiveConfiguration.Evaluate(darius, State(), track, new EquippedItems(rifle, null, null));

            Assert.That(again.MaxHealth, Is.EqualTo(first.MaxHealth));
            Assert.That(again.MoveSpeed, Is.EqualTo(first.MoveSpeed));
            Assert.That(bare.MaxHealth, Is.EqualTo(130));
            Assert.That(bare.MoveSpeed, Is.EqualTo(5f));
        }

        [Test]
        public void Progression_StillStacksOnTheEquippedWeapon()
        {
            var state = State();
            state.AddExperience(100);
            Assert.That(state.TryPickChoice(track, "combat"), Is.True);

            var config = EffectiveConfiguration.Evaluate(darius, state, track, new EquippedItems(rifle, null, null));

            // 15 base damage, +20% role +15% pick = +35%, rounded away from zero: 20.25 -> 20.
            Assert.That(config.AttackDamage, Is.EqualTo(20));
            Assert.That(config.Rank, Is.EqualTo(2));
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.EffectiveConfigurationEquipmentTests"`
Expected: `EXIT=1` (`Evaluate` has no four-argument overload; `HasWeapon` missing).

- [ ] **Step 3: Replace `EffectiveConfiguration.cs`**

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// What an operative is worth right now: its definition, plus its role's bonus, plus the modifiers of every choice it
    /// has picked, plus (when the unit has an equipment system) the passive modifiers of its equipped items and its
    /// equipped weapon in place of the definition's archetype. A plain value computed on demand from authoritative inputs
    /// and never stored back into any asset, so repeating the calculation can never accumulate a bonus. Combat stays in
    /// the combat components; they receive these numbers and do the rest.
    /// </summary>
    public readonly struct EffectiveConfiguration
    {
        // A cooldown can be cut by at most this much, so abilities never become free.
        const float MaxCooldownReduction = 0.75f;
        const float MinMoveSpeed = 0.5f;

        EffectiveConfiguration(int rank, int maxHealth, float moveSpeed, CombatRole attackRole, float attackRange,
            int attackDamage, float attackInterval, float abilityPower, float abilityCooldownMultiplier, bool hasWeapon,
            string weaponName)
        {
            Rank = rank;
            MaxHealth = maxHealth;
            MoveSpeed = moveSpeed;
            AttackRole = attackRole;
            AttackRange = attackRange;
            AttackDamage = attackDamage;
            AttackInterval = attackInterval;
            AbilityPower = abilityPower;
            AbilityCooldownMultiplier = abilityCooldownMultiplier;
            HasWeapon = hasWeapon;
            WeaponName = weaponName ?? string.Empty;
        }

        public int Rank { get; }
        public int MaxHealth { get; }
        public float MoveSpeed { get; }
        public CombatRole AttackRole { get; }
        public float AttackRange { get; }
        public int AttackDamage { get; }
        public float AttackInterval { get; }
        /// <summary>Multiplier on an ability's damage or healing; 1 leaves it as authored.</summary>
        public float AbilityPower { get; }
        /// <summary>Multiplier on an ability's cooldown; 1 leaves it as authored, never below 0.25.</summary>
        public float AbilityCooldownMultiplier { get; }
        /// <summary>False only for a unit with an equipment system and an empty Weapon slot: its ordinary attack is disabled.</summary>
        public bool HasWeapon { get; }
        /// <summary>The weapon's display name (the archetype's without equipment), empty with no weapon.</summary>
        public string WeaponName { get; }

        /// <summary>The definition and its archetype alone: no role bonus, no picks, rank 1.</summary>
        public static EffectiveConfiguration Base(OperativeDefinition definition) => Build(definition, 1, default, default);

        /// <summary>
        /// Base + role bonus + the modifiers of the state's picks. A null state or track means no picks (rank 1); a pick
        /// whose id the track no longer knows is skipped; a null role gives no bonus.
        /// </summary>
        public static EffectiveConfiguration Evaluate(OperativeDefinition definition, PersistentOperativeState state, ProgressionTrack track) =>
            Evaluate(definition, state, track, default);

        /// <summary>
        /// As above, plus equipment. `equipped.IsSet` false (the default) means no equipment system and gives the result
        /// above unchanged; true replaces the definition's archetype with the equipped weapon (none = no weapon) and adds
        /// the passive modifiers of the weapon, armor and utility items.
        /// </summary>
        public static EffectiveConfiguration Evaluate(OperativeDefinition definition, PersistentOperativeState state,
            ProgressionTrack track, EquippedItems equipped)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            var total = definition.Role != null ? definition.Role.Bonus : default;
            var rank = 1;
            if (state != null && track != null)
            {
                rank = state.Rank(track);
                foreach (var choiceId in state.ChoiceIds)
                {
                    var choice = track.FindChoice(choiceId);
                    if (choice != null)
                        total = StatModifiers.Combine(total, choice.Modifiers);
                }
            }
            return Build(definition, rank, total, equipped);
        }

        static EffectiveConfiguration Build(OperativeDefinition definition, int rank, StatModifiers total, EquippedItems equipped)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            CombatArchetype weapon;
            string weaponName;
            if (equipped.IsSet)
            {
                total = StatModifiers.Combine(total, equipped.Modifiers);
                weapon = equipped.Weapon != null ? equipped.Weapon.Weapon : null;
                weaponName = equipped.Weapon != null ? equipped.Weapon.DisplayName : string.Empty;
            }
            else
            {
                weapon = definition.Archetype;
                if (weapon == null)
                    throw new ArgumentException($"{definition.name} has no combat archetype.", nameof(definition));
                weaponName = weapon.DisplayName;
            }
            var armed = weapon != null;

            return new EffectiveConfiguration(
                rank,
                Math.Max(1, definition.BaseMaxHealth + total.maxHealth),
                Math.Max(MinMoveSpeed, definition.BaseMoveSpeed + total.moveSpeed),
                armed ? weapon.Role : CombatRole.Melee,
                armed ? weapon.Range : 0f,
                armed ? ScaleRounded(weapon.Damage, total.attackDamage) : 0,
                armed ? weapon.AttackInterval : 0f,
                Math.Max(0f, 1f + total.abilityPower),
                1f - Mathf.Clamp(total.abilityCooldownReduction, 0f, MaxCooldownReduction),
                armed,
                armed ? weaponName : string.Empty);
        }

        // Rounded away from zero in double precision so a .5 result never depends on float noise; never below 0.
        static int ScaleRounded(int value, float fraction) =>
            Math.Max(0, (int)Math.Round(value * (1.0 + fraction), MidpointRounding.AwayFromZero));
    }
}
```

In `SquadRoster.cs` replace lines 130–131 with:

```csharp
        public EffectiveConfiguration Evaluate(RosterMember member) =>
            EffectiveConfiguration.Evaluate(member.Definition, member.State, track);

        /// <summary>The member's configuration with its equipment applied (see EffectiveConfiguration.Evaluate).</summary>
        public EffectiveConfiguration Evaluate(RosterMember member, EquippedItems equipped) =>
            EffectiveConfiguration.Evaluate(member.Definition, member.State, track, equipped);
```

- [ ] **Step 4: Run to verify it passes**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.EffectiveConfiguration"`
Expected: `EXIT=0` — the new 7 and the existing `EffectiveConfigurationTests` all pass (the prefix filter runs both classes).

---

### Task 6: A unit with no weapon cannot make the ordinary attack

**Files:**
- Modify: `Assets/_Project/Scripts/Units/UnitAttacker.cs`, `Assets/_Project/Scripts/Units/CommandableUnit.cs`, `Assets/_Project/Scripts/AI/CompanionAI.cs:309`
- Test: `Assets/_Project/Tests/PlayMode/NoWeaponPlayModeTests.cs`

**Interfaces:**
- Consumes: `EffectiveConfiguration.HasWeapon` (T5; applied to units in T9).
- Produces: `UnitAttacker.HasWeapon`, `UnitAttacker.ApplyEffective(role, range, damage, interval, bool weaponPresent = true)`; `enum CommandRefusal { None, NoWeapon }`; `CommandableUnit.LastRefusal`, `CommandableUnit.LastRefusalTime` (unscaled seconds).

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/PlayMode/NoWeaponPlayModeTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class NoWeaponPlayModeTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
        }

        [TearDown]
        public void TearDown() => world.Dispose();

        static void Disarm(CommandableUnit unit) =>
            unit.GetComponent<UnitAttacker>().ApplyEffective(CombatRole.Melee, 0f, 0, 0f, weaponPresent: false);

        [UnityTest]
        public IEnumerator AttackOrder_IsRefusedWithTheReason_AndTheUnitKeepsItsOrders()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, 0f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(new Vector3(3f, 0f, 0f));
            yield return null;
            Assert.That(unit.Issue(new MoveCommand(new Vector3(-4f, 0f, 0f))), Is.True);
            Disarm(unit);

            var accepted = unit.Issue(new AttackCommand(dummy));

            Assert.That(accepted, Is.False);
            Assert.That(unit.LastRefusal, Is.EqualTo(CommandRefusal.NoWeapon));
            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>(), "orders are unchanged");
        }

        [UnityTest]
        public IEnumerator QueuedAttack_IsRefused_Too()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, 0f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(new Vector3(3f, 0f, 0f));
            yield return null;
            unit.Issue(new MoveCommand(new Vector3(-4f, 0f, 0f)));
            Disarm(unit);

            Assert.That(unit.Issue(new AttackCommand(dummy), IssueMode.Append), Is.False);
            Assert.That(unit.PendingCommands, Is.Empty);
        }

        [UnityTest]
        public IEnumerator TryAttack_NeverFires_WithoutAWeapon()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, 0f), role: CombatRole.Melee, range: 2f);
            var dummy = world.CreateDummy(new Vector3(1f, 0f, 0f));
            yield return null;
            var attacker = unit.GetComponent<UnitAttacker>();
            Disarm(unit);

            Assert.That(attacker.HasWeapon, Is.False);
            Assert.That(attacker.TryAttack(dummy), Is.False);
            Assert.That(dummy.Current, Is.EqualTo(dummy.Max));
        }

        [UnityTest]
        public IEnumerator Rearming_RestoresTheAttack()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, 0f), role: CombatRole.Melee, range: 2f);
            var dummy = world.CreateDummy(new Vector3(1f, 0f, 0f));
            yield return null;
            var attacker = unit.GetComponent<UnitAttacker>();
            Disarm(unit);

            attacker.ApplyEffective(CombatRole.Melee, 2f, 25, 1f);

            Assert.That(attacker.HasWeapon, Is.True);
            Assert.That(unit.Issue(new AttackCommand(dummy)), Is.True);
        }

        [UnityTest]
        public IEnumerator AutoRetaliate_FromAnUnarmedUnit_DoesNothingAndDoesNotThrow()
        {
            var unit = world.CreateFighter(new Vector3(0f, 0f, 0f), role: CombatRole.Melee, range: 2f);
            var enemy = world.CreateFighter(new Vector3(1f, 0f, 0f));
            yield return null;
            Disarm(unit);

            unit.GetComponent<Health>().TakeDamage(5, enemy.GetComponent<Health>());
            yield return null;

            Assert.That(unit.CurrentCommand, Is.Null);
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.NoWeaponPlayModeTests"`
Expected: `EXIT=1` (`weaponPresent`, `HasWeapon`, `LastRefusal` missing).

- [ ] **Step 3: Implement**

`UnitAttacker.cs`: add the field and property near the other fields:

```csharp
        // False only when equipment left the Weapon slot empty (ApplyEffective with weaponPresent false): no ordinary attack.
        bool hasWeapon = true;

        /// <summary>False when this unit has an equipment system and nothing in its Weapon slot.</summary>
        public bool HasWeapon => hasWeapon;
```

In `ApplyArchetype` add `hasWeapon = true;` after `hasEffective = false;`. Replace `ApplyEffective`:

```csharp
        internal void ApplyEffective(CombatRole combatRole, float attackRange, int attackDamage, float attackInterval, bool weaponPresent = true)
        {
            role = combatRole;
            range = Mathf.Max(0.1f, attackRange);
            damage = Mathf.Max(0, attackDamage);
            cooldown = Mathf.Max(0f, attackInterval);
            hasWeapon = weaponPresent;
            hasEffective = true;
        }
```

Replace `CanAttackFrom`:

```csharp
        public bool CanAttackFrom(Vector3 pivot, Health target) =>
            hasWeapon && IsInRangeFrom(pivot, target) && (!NeedsLineOfSight || HasLineOfSightFrom(pivot, target));
```

`CommandableUnit.cs`: add above the class (next to `AttackPhase`):

```csharp
    /// <summary>A reason an order was refused that the HUD explains in words.</summary>
    public enum CommandRefusal
    {
        None,
        /// <summary>The unit has no weapon equipped, so it cannot make the ordinary attack.</summary>
        NoWeapon,
    }
```

Add after the `StopCount` property:

```csharp
        /// <summary>Why the last order was refused for a reason the player should be told (None before any).</summary>
        public CommandRefusal LastRefusal { get; private set; }

        /// <summary>Unscaled time of the last refusal, so the HUD can show it for a few seconds even while paused.</summary>
        public float LastRefusalTime { get; private set; }
```

In `Issue`, directly after `if (!IsAlive) return false;` insert:

```csharp
            if (command is AttackCommand && !Attacker.HasWeapon)
            {
                Refuse(CommandRefusal.NoWeapon);
                return false;
            }
```

In `CanStart`, replace the attack case:

```csharp
                case AttackCommand attack:
                    if (!Attacker.HasWeapon)
                    {
                        Refuse(CommandRefusal.NoWeapon);
                        return false;
                    }
                    return IsAttackable(attack.Target);
```

In `UpdateAttack`, replace the first guard with:

```csharp
            if (!Attacker.HasWeapon || !IsAttackable(target))
            {
                Finish();
                return;
            }
```

Add the helper near `FaceTowards`:

```csharp
        void Refuse(CommandRefusal reason)
        {
            LastRefusal = reason;
            LastRefusalTime = Time.unscaledTime;
        }
```

`CompanionAI.cs` (the line `var target = ChooseAssistTarget();` in `Think`): change to

```csharp
            var target = Attacker.HasWeapon ? ChooseAssistTarget() : null;
```

so an unarmed companion falls through to follow instead of retrying a refused attack every think tick.

- [ ] **Step 4: Run to verify it passes**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.NoWeaponPlayModeTests"` → `EXIT=0`, 5 passed. Then `Tools/run-tests.sh PlayMode "Blackglass.Tests.CommandableUnit"` and `"Blackglass.Tests.CompanionAI"` → `EXIT=0`.

---

### Task 7: Item and starter assets (editor builder)

`LootTable` is defined in Task 11, which extends this builder with the loot table. This task creates items, weapon visuals, the catalogue and the starter loadout.

**Files:**
- Create: `Assets/_Project/Editor/InventoryDataBuilder.cs`
- Generated: `Assets/_Project/Data/Items/*.asset`, `.../Visuals/*.asset`, `Assets/Art/Weapons/Placeholders/BladePlaceholder.prefab` and its material
- Test: `Assets/_Project/Tests/EditMode/InventoryAssetTests.cs`

**Interfaces:**
- Consumes: T1, T4 (`StarterLoadout`), the shipped `Data/Archetypes/*.asset`, `Assets/Art/Weapons/Rifle/Prefabs/Rifle.prefab`, the three `Data/Operatives/Definitions/*.asset`, `Data/Abilities/Mend.asset`.
- Produces: the asset paths below; the stable ids `item.service-rifle`, `item.marksman-rifle`, `item.combat-blade`, `item.light-vest`, `item.servo-boots`, `item.medkit`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/InventoryAssetTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    public class InventoryAssetTests
    {
        const string Root = "Assets/_Project/Data/Items/";

        static T Load<T>(string path) where T : Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            Assert.That(asset != null, Is.True, "missing asset " + path);
            return asset;
        }

        [Test]
        public void Catalogue_HoldsTheSixPrototypeItems_AllValid_WithStableIds()
        {
            var catalogue = Load<ItemCatalogue>(Root + "ItemCatalogue.asset");

            var errors = new List<string>();
            Assert.That(catalogue.Validate(errors), Is.True, string.Join("\n", errors));
            foreach (var id in new[] { "item.service-rifle", "item.marksman-rifle", "item.combat-blade", "item.light-vest", "item.servo-boots", "item.medkit" })
                Assert.That(catalogue.Find(id) != null, Is.True, id);
            Assert.That(catalogue.Items, Has.Count.EqualTo(6));
        }

        [Test]
        public void Weapons_ReuseTheExistingArchetypes_AndTradeOffRangeForSpeed()
        {
            var catalogue = Load<ItemCatalogue>(Root + "ItemCatalogue.asset");
            var ranged = Load<CombatArchetype>("Assets/_Project/Data/Archetypes/Ranged.asset");
            var marksman = Load<CombatArchetype>("Assets/_Project/Data/Archetypes/Marksman.asset");
            var melee = Load<CombatArchetype>("Assets/_Project/Data/Archetypes/Melee.asset");

            Assert.That(catalogue.Find("item.service-rifle").Weapon == ranged, Is.True);
            Assert.That(catalogue.Find("item.marksman-rifle").Weapon == marksman, Is.True);
            Assert.That(catalogue.Find("item.combat-blade").Weapon == melee, Is.True);
            Assert.That(marksman.Range, Is.GreaterThan(ranged.Range));
            Assert.That(marksman.AttackInterval, Is.GreaterThan(ranged.AttackInterval), "the longer weapon attacks more slowly");
        }

        [Test]
        public void ArmorUtilityAndMedkit_AreModest_AndTheMedkitMatchesMend()
        {
            var catalogue = Load<ItemCatalogue>(Root + "ItemCatalogue.asset");
            var mend = Load<AbilityDefinition>("Assets/_Project/Data/Abilities/Mend.asset");

            Assert.That(catalogue.Find("item.light-vest").Modifiers.maxHealth, Is.EqualTo(20));
            Assert.That(catalogue.Find("item.servo-boots").Modifiers.moveSpeed, Is.EqualTo(0.5f));
            var medkit = catalogue.Find("item.medkit");
            Assert.That(medkit.IsStackable, Is.True);
            Assert.That(medkit.MaxStack, Is.EqualTo(3));
            Assert.That(medkit.HealAmount, Is.EqualTo(mend.Amount));
        }

        [Test]
        public void EveryWeapon_HasAVisual_WithAPrefab()
        {
            var catalogue = Load<ItemCatalogue>(Root + "ItemCatalogue.asset");
            foreach (var item in catalogue.Items)
            {
                if (item.Category != ItemCategory.Weapon)
                    continue;
                Assert.That(item.WeaponVisual != null, Is.True, item.Id);
                Assert.That(item.WeaponVisual.Prefab != null, Is.True, item.Id);
            }
        }

        [Test]
        public void Starter_SeedsTheThreeOperatives_WithoutProblems_AndKeepsTheirCurrentWeapons()
        {
            var catalogue = Load<ItemCatalogue>(Root + "ItemCatalogue.asset");
            var starter = Load<StarterLoadout>(Root + "StarterLoadout.asset");
            var session = new InventorySession(catalogue, 8);
            var ids = new List<string>();
            foreach (var name in new[] { "Darius", "Kestrel", "Sable" })
            {
                var definition = Load<OperativeDefinition>($"Assets/_Project/Data/Operatives/Definitions/{name}.asset");
                ids.Add(definition.Id);
                var problems = new List<string>();
                session.SeedStarter(starter, new[] { definition.Id }, problems);
                Assert.That(problems, Is.Empty, name);
                var weapon = session.EquippedFor(session.Session, definition.Id).Weapon;
                Assert.That(weapon != null && weapon.Weapon == definition.Archetype, Is.True,
                    $"{name} starts with the weapon matching its archetype, so existing behaviour is kept");
            }

            Assert.That(session.Session.Stash.CountOf("item.medkit"), Is.EqualTo(4));
            Assert.That(session.Session.Stash.CountOf("item.combat-blade"), Is.EqualTo(1));
            Assert.That(session.Session.Loadout(ids[0]).Bag.CountOf("item.medkit"), Is.EqualTo(2));
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.InventoryAssetTests"`
Expected: `EXIT=2` — all five fail with `missing asset …` (the types exist since T1/T4; the assets do not).

- [ ] **Step 3: Write the builder**

`Assets/_Project/Editor/InventoryDataBuilder.cs` (same conventions as `OperativeDataBuilder`: `#if UNITY_EDITOR`, namespace `Blackglass.EditorTools`, existing assets kept):

```csharp
#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Creates the prototype item data: six item definitions, their weapon visuals, the catalogue and the starter loadout.
    /// Existing assets are kept (hand tuning is never overwritten). Ids are authored here once and never change.
    /// </summary>
    public static class InventoryDataBuilder
    {
        public const string Root = "Assets/_Project/Data/Items";
        const string VisualDir = Root + "/Visuals";
        const string ArchetypeDir = "Assets/_Project/Data/Archetypes";
        const string DefinitionDir = "Assets/_Project/Data/Operatives/Definitions";
        const string RiflePrefab = "Assets/Art/Weapons/Rifle/Prefabs/Rifle.prefab";
        const string PlaceholderDir = "Assets/Art/Weapons/Placeholders";
        public const string CataloguePath = Root + "/ItemCatalogue.asset";
        public const string StarterPath = Root + "/StarterLoadout.asset";

        [MenuItem("Blackglass/Inventory/Create Prototype Items (keeps existing assets)")]
        public static void CreateAssetsMenu() => CreateAssets();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildFromCommandLine()
        {
            CreateAssets();
            AssetDatabase.SaveAssets();
        }

        public static void CreateAssets()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(VisualDir);
            Directory.CreateDirectory(PlaceholderDir);

            var rifleVisual = EnsureVisual("RifleVisual", Load<GameObject>(RiflePrefab), Vector3.zero, Vector3.zero, Vector3.one);
            // Placeholder: the same model, 25% longer. Documented in decision 043; replace with real art later.
            var marksmanVisual = EnsureVisual("MarksmanRifleVisual", Load<GameObject>(RiflePrefab), Vector3.zero, Vector3.zero, new Vector3(1f, 1f, 1.25f));
            var bladeVisual = EnsureVisual("BladeVisual", EnsureBladePrefab(), Vector3.zero, Vector3.zero, Vector3.one);

            var rifle = Ensure("ServiceRifle", "item.service-rifle", "Service Rifle", ItemCategory.Weapon, ItemSlot.Weapon, 1, default,
                Load<CombatArchetype>(ArchetypeDir + "/Ranged.asset"), 0, rifleVisual, "Standard issue. Reliable at medium range.");
            var marksman = Ensure("MarksmanRifle", "item.marksman-rifle", "Marksman Rifle", ItemCategory.Weapon, ItemSlot.Weapon, 1, default,
                Load<CombatArchetype>(ArchetypeDir + "/Marksman.asset"), 0, marksmanVisual, "Long range and heavy hits, but slow to fire.");
            var blade = Ensure("CombatBlade", "item.combat-blade", "Combat Blade", ItemCategory.Weapon, ItemSlot.Weapon, 1, default,
                Load<CombatArchetype>(ArchetypeDir + "/Melee.asset"), 0, bladeVisual, "Close combat only. Ignores cover and sight.");
            var vest = Ensure("LightVest", "item.light-vest", "Light Vest", ItemCategory.Armor, ItemSlot.Armor, 1,
                new StatModifiers { maxHealth = 20 }, null, 0, null, "+20 maximum health.");
            var boots = Ensure("ServoBoots", "item.servo-boots", "Servo Boots", ItemCategory.Utility, ItemSlot.Utility, 1,
                new StatModifiers { moveSpeed = 0.5f }, null, 0, null, "+0.5 m/s movement speed.");
            var medkit = Ensure("Medkit", "item.medkit", "Medkit", ItemCategory.Consumable, ItemSlot.None, 3, default, null,
                Load<AbilityDefinition>("Assets/_Project/Data/Abilities/Mend.asset").Amount, null, "Restores health to the user. Not used at full health.");

            var catalogue = EnsureCatalogue(new[] { rifle, marksman, blade, vest, boots, medkit });
            EnsureStarter(rifle, marksman, vest, boots, blade, medkit);
            EditorUtility.SetDirty(catalogue);
        }

        static ItemDefinition Ensure(string file, string id, string displayName, ItemCategory category, ItemSlot slot, int maxStack,
            StatModifiers modifiers, CombatArchetype weapon, int heal, WeaponVisualAsset visual, string description)
        {
            var path = $"{Root}/{file}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);
            if (existing != null)
                return existing;
            var item = ItemDefinition.Create(id, displayName, category, slot, maxStack, modifiers, weapon, heal);
            item.SetWeaponVisual(visual);
            item.SetDescription(description);
            if (!item.IsValid(out var problem))
                throw new InvalidOperationException($"{file}: {problem}");
            AssetDatabase.CreateAsset(item, path);
            return item;
        }

        static WeaponVisualAsset EnsureVisual(string file, GameObject prefab, Vector3 position, Vector3 euler, Vector3 scale)
        {
            var path = $"{VisualDir}/{file}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<WeaponVisualAsset>(path);
            if (existing != null)
                return existing;
            var visual = WeaponVisualAsset.Create(prefab, position, euler, scale);
            AssetDatabase.CreateAsset(visual, path);
            return visual;
        }

        // A thin grey box, 0.45 m long, pointing along +Z: the stand-in blade. No collider.
        static GameObject EnsureBladePrefab()
        {
            var path = PlaceholderDir + "/BladePlaceholder.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null)
                return existing;
            var materialPath = PlaceholderDir + "/BladePlaceholder.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                material = new Material(shader) { name = "BladePlaceholder", color = new Color(0.6f, 0.62f, 0.66f) };
                AssetDatabase.CreateAsset(material, materialPath);
            }
            var root = new GameObject("BladePlaceholder");
            var blade = GameObject.CreatePrimitive(PrimitiveType.Cube);
            blade.name = "Blade";
            UnityEngine.Object.DestroyImmediate(blade.GetComponent<Collider>());
            blade.transform.SetParent(root.transform, false);
            blade.transform.localPosition = new Vector3(0f, 0f, 0.25f);
            blade.transform.localScale = new Vector3(0.04f, 0.1f, 0.45f);
            blade.GetComponent<Renderer>().sharedMaterial = material;
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            return prefab;
        }

        static ItemCatalogue EnsureCatalogue(ItemDefinition[] items)
        {
            var catalogue = AssetDatabase.LoadAssetAtPath<ItemCatalogue>(CataloguePath);
            if (catalogue == null)
            {
                catalogue = ScriptableObject.CreateInstance<ItemCatalogue>();
                AssetDatabase.CreateAsset(catalogue, CataloguePath);
            }
            var serialized = new SerializedObject(catalogue);
            var list = serialized.FindProperty("items");
            list.arraySize = items.Length;
            for (var i = 0; i < items.Length; i++)
                list.GetArrayElementAtIndex(i).objectReferenceValue = items[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return catalogue;
        }

        static void EnsureStarter(ItemDefinition rifle, ItemDefinition marksman, ItemDefinition vest, ItemDefinition boots,
            ItemDefinition blade, ItemDefinition medkit)
        {
            if (AssetDatabase.LoadAssetAtPath<StarterLoadout>(StarterPath) != null)
                return;
            var starter = StarterLoadout.Create(new[]
            {
                Operative("Darius", Grant(rifle, 1, true), Grant(medkit, 2, false)),
                Operative("Kestrel", Grant(marksman, 1, true), Grant(medkit, 2, false)),
                Operative("Sable", Grant(rifle, 1, true), Grant(medkit, 2, false)),
            }, new[]
            {
                Grant(blade, 1, false), Grant(rifle, 1, false), Grant(vest, 1, false), Grant(boots, 1, false), Grant(medkit, 4, false),
            });
            AssetDatabase.CreateAsset(starter, StarterPath);
        }

        static StarterGrant Grant(ItemDefinition item, int quantity, bool equip) => new StarterGrant { item = item, quantity = quantity, equip = equip };

        static StarterOperative Operative(string name, params StarterGrant[] items) => new StarterOperative
        {
            operative = Load<OperativeDefinition>($"{DefinitionDir}/{name}.asset"),
            items = items,
        };

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidOperationException("Missing asset " + path);
            return asset;
        }
    }
}
#endif
```

`Assets/_Project/Editor` compiles into `Assembly-CSharp-Editor`, which already uses internals (`OperativeDataBuilder` calls `OperativeDefinition.Create`), so `ItemDefinition.Create`, `StarterLoadout.Create` and `WeaponVisualAsset.Create` are reachable. If the editor assembly cannot see them, add the three asset-creating factories to the `InternalsVisibleTo` list exactly as the existing ones are.

- [ ] **Step 4: Run the builder, then the tests**

```bash
cd /f/Programs/ProjectBlackglass
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -quit -projectPath "$(pwd -W)" \
  -executeMethod Blackglass.EditorTools.InventoryDataBuilder.BuildFromCommandLine -logFile Logs/InventoryDataBuilder.log; echo EXIT=$?
Tools/run-tests.sh EditMode "Blackglass.Tests.InventoryAssetTests"
```

Expected: builder `EXIT=0` (read `Logs/InventoryDataBuilder.log` for exceptions if not), tests `EXIT=0`, 5 passed. Run the builder a second time: `git status --short` must show nothing new (idempotent).

---

### Task 8: Mission instance id and the inventory lifecycle in the director

**Files:**
- Modify: `Assets/_Project/Scripts/Mission/MissionDirector.cs`, `Assets/_Project/Scripts/Mission/MissionSlots.cs`, `Assets/_Project/Scripts/Items/SquadInventory.cs`, `Assets/_Project/Tests/PlayMode/TestSupport/MissionRig.cs`
- Test: `Assets/_Project/Tests/PlayMode/InventoryLifecyclePlayModeTests.cs`

**Interfaces:**
- Consumes: `SquadInventory.BeginMission/AbortMission/HandleMissionFinished` (T4).
- Produces: `MissionDirector.InstanceId` (a new GUID string for every generation); `MissionSystems.inventory`; `MissionRig.AddInventory(...)`.

- [ ] **Step 1: Extend the test rig**

In `MissionRig.cs` add after `AddRoster`:

```csharp
        /// <summary>
        /// Adds the squad inventory to the persistent systems. Call after AddRoster and before the first Generate. The
        /// director's mission-finished event settles the inventory, as in the scene.
        /// </summary>
        public SquadInventory AddInventory(ItemCatalogue catalogue, StarterLoadout starter = null, SquadRoster roster = null, int bagCapacity = 8)
        {
            var host = World.Track(new GameObject("Inventory"));
            host.SetActive(false);
            var inventory = host.AddComponent<SquadInventory>();
            inventory.Initialize(catalogue, starter, roster, bagCapacity, null, Director);
            host.SetActive(true);
            systems.inventory = inventory;
            return inventory;
        }
```

- [ ] **Step 2: Write the failing tests**

`Assets/_Project/Tests/PlayMode/InventoryLifecyclePlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class InventoryLifecyclePlayModeTests
    {
        const string Items = "Assets/_Project/Data/Items/";
        MissionRig rig;
        SquadInventory inventory;
        ItemDefinition vest;

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        [SetUp]
        public void SetUp()
        {
            rig = new MissionRig();
            var catalogue = Load<ItemCatalogue>(Items + "ItemCatalogue.asset");
            vest = catalogue.Find("item.light-vest");
            inventory = rig.AddInventory(catalogue);
            inventory.Core.EnsureOperative("a");
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
            rig = null;
        }

        [UnityTest]
        public IEnumerator Generate_StartsAMissionInstance_AndTheWorkingStateIsACopy()
        {
            inventory.Core.Session.Loadout("a").Bag.Add(vest, 1);

            yield return rig.Generate(12345);

            Assert.That(rig.Director.InstanceId, Is.Not.Null.And.Not.Empty);
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.InMission));
            Assert.That(inventory.Core.ActiveMissionId, Is.EqualTo(rig.Director.InstanceId));
            Assert.That(inventory.Core.Working.Loadout("a").Bag.CountOf("item.light-vest"), Is.EqualTo(1));
            Assert.That(inventory.Core.Working, Is.Not.SameAs(inventory.Core.Session));
        }

        [UnityTest]
        public IEnumerator SameSeedTwice_IsTwoDistinctMissionInstances_AndTheOldWorkingStateIsGone()
        {
            yield return rig.Generate(12345);
            var first = rig.Director.InstanceId;
            inventory.Core.Working.Loadout("a").Bag.Add(vest, 1);

            yield return rig.Generate(12345);

            Assert.That(rig.Director.InstanceId, Is.Not.EqualTo(first));
            Assert.That(inventory.Core.ActiveMissionId, Is.EqualTo(rig.Director.InstanceId));
            Assert.That(inventory.Core.Working.Loadout("a").Bag.Count, Is.EqualTo(0), "the abandoned mission's pickup is discarded");
            Assert.That(inventory.Core.Session.Loadout("a").Bag.Count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Clear_DiscardsTheWorkingState()
        {
            yield return rig.Generate(12345);
            inventory.Core.Working.Loadout("a").Bag.Add(vest, 1);

            rig.Director.Clear();

            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.Loadout));
            Assert.That(inventory.Core.Session.Loadout("a").Bag.Count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Success_CommitsOnce_EvenWhenTheEventIsRepeated()
        {
            yield return rig.Generate(12345);
            inventory.Core.Working.Loadout("a").Bag.Add(vest, 1);

            inventory.HandleMissionFinished(MissionPhase.Success);
            inventory.HandleMissionFinished(MissionPhase.Success);

            Assert.That(inventory.Core.Session.Loadout("a").Bag.CountOf("item.light-vest"), Is.EqualTo(1));
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.Loadout));
        }

        [UnityTest]
        public IEnumerator Failure_ThroughTheRealRuntime_DiscardsThePickups()
        {
            yield return rig.Generate(12345);
            inventory.Core.Working.Loadout("a").Bag.Add(vest, 1);

            foreach (var unit in rig.Director.Friendlies)
                unit.GetComponent<Health>().TakeDamage(10000);
            yield return TestWorld.WaitUntil(() => rig.Director.Phase == MissionPhase.Failure, 5f);

            Assert.That(rig.Director.Phase, Is.EqualTo(MissionPhase.Failure));
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.Loadout));
            Assert.That(inventory.Core.Session.Loadout("a").Bag.Count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator AFailedGeneration_LeavesNoWorkingState()
        {
            rig.Director.Settings.maxAttempts = 1;
            rig.SetFriendlySlots(new FriendlySlot[0]);   // no slots and no roster: every spawn fails

            LogAssert.ignoreFailingMessages = true;
            yield return rig.Generate(12345);
            LogAssert.ignoreFailingMessages = false;

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Failed));
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.Loadout));
        }
    }
}
#endif
```

- [ ] **Step 3: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.InventoryLifecyclePlayModeTests"` → `EXIT=1` (`InstanceId`, `MissionSystems.inventory` missing).

- [ ] **Step 4: Implement**

`MissionSlots.cs`: add `public SquadInventory inventory;` to `MissionSystems` after `intelligence`.

`MissionDirector.cs`:

1. Next to `Phase`:

```csharp
        /// <summary>A new id for every generation (also a repeat of the same seed); the inventory settles against it exactly once.</summary>
        public string InstanceId { get; private set; } = string.Empty;
```

2. In `Start()`, after the `generateOnStart` guard:

```csharp
            if (systems.inventory != null && systems.inventory.StartInLoadout)
                return;   // the loadout panel's Deploy button generates the first mission
```

3. In `Run()`, directly after `yield return null;   // let Destroy finish …`:

```csharp
            InstanceId = Guid.NewGuid().ToString("N");
            if (systems.inventory != null)
                systems.inventory.BeginMission(InstanceId);
```

4. In `Run()`'s exhausted-attempts branch, before `SetState(MissionState.Failed)`:

```csharp
            if (systems.inventory != null)
                systems.inventory.AbortMission();
```

5. In `Teardown()`, right after `DetachRuntime();`:

```csharp
            if (systems.inventory != null)
                systems.inventory.AbortMission();   // an unsettled mission (restart, clear) keeps nothing
```

Do **not** put the abort in `ResetSystems()`: that also runs between failed attempts of one generation, which must keep the working state.

`SquadInventory.cs` `HandleMissionFinished`: restore the director fallback:

```csharp
            var id = missionIdSource != null ? missionIdSource() : director != null ? director.InstanceId : null;
```

- [ ] **Step 5: Run to verify it passes**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.InventoryLifecyclePlayModeTests"` → `EXIT=0`, 6 passed. Then `Tools/run-tests.sh PlayMode "Blackglass.Tests.MissionDirector"` → `EXIT=0`.

---

### Task 9: Bind the unit to its loadout; the managed weapon visual

**Files:**
- Create: `Assets/_Project/Scripts/Items/UnitWeaponVisual.cs`
- Modify: `Assets/_Project/Scripts/Operatives/UnitIdentity.cs`, `Assets/_Project/Scripts/Mission/MissionSpawner.cs` (the `Bind` call), `Assets/_Project/Editor/InventoryDataBuilder.cs` (prefab migration), prefabs `Darius_Visual`, `EnemyUnit_Visual`, `Darius_Player`, `EnemyUnit_Friendly`, `EnemyUnit_Hostile`, and the existing asset tests that assert the baked rifle
- Test: `Assets/_Project/Tests/PlayMode/EquipmentSpawnPlayModeTests.cs`, `Assets/_Project/Tests/PlayMode/WeaponVisualPlayModeTests.cs`

**Interfaces:**
- Consumes: T4 (`SquadInventory.EquippedFor`), T5, T6, T7 assets, T8 (`MissionRig.AddInventory`).
- Produces: `UnitIdentity.Bind(SquadRoster, RosterMember, SquadInventory = null)`, `UnitIdentity.Inventory`; `UnitWeaponVisual.Show(WeaponVisualAsset)`, `.Current`, constants `SocketName = "WeaponSocket"`, `ManagedName = "EquippedWeapon"`.

- [ ] **Step 1: Write the failing spawn tests**

`Assets/_Project/Tests/PlayMode/EquipmentSpawnPlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class EquipmentSpawnPlayModeTests
    {
        const string Items = "Assets/_Project/Data/Items/";
        const string Ops = "Assets/_Project/Data/Operatives/";
        MissionRig rig;
        SquadRoster roster;
        SquadInventory inventory;
        ItemCatalogue catalogue;

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        [SetUp]
        public void SetUp()
        {
            rig = new MissionRig();
            var squad = new[] { "Darius", "Kestrel", "Sable" }
                .Select(n => Load<OperativeDefinition>($"{Ops}Definitions/{n}.asset")).ToArray();
            roster = rig.AddRoster(squad, Load<ProgressionTrack>(Ops + "Progression.asset"));
            catalogue = Load<ItemCatalogue>(Items + "ItemCatalogue.asset");
            inventory = rig.AddInventory(catalogue, Load<StarterLoadout>(Items + "StarterLoadout.asset"), roster);
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
            rig = null;
        }

        UnitAttacker AttackerOf(int index) => rig.Director.Friendlies[index].GetComponent<UnitAttacker>();

        [UnityTest]
        public IEnumerator Starter_KeepsEveryOperativesCurrentWeaponNumbers()
        {
            yield return rig.Generate(12345);

            var darius = AttackerOf(0);
            var kestrel = AttackerOf(1);
            Assert.That(darius.HasWeapon, Is.True);
            Assert.That(darius.Range, Is.EqualTo(8f));
            Assert.That(kestrel.Range, Is.EqualTo(16f), "Kestrel starts with the marksman rifle");
            Assert.That(kestrel.Cooldown, Is.EqualTo(2.5f));
        }

        [UnityTest]
        public IEnumerator DifferentWeapons_OnDifferentOperatives_ChangeOnlyThoseOperatives()
        {
            var dariusId = roster.Members[0].Id;
            var blade = inventory.Core.Session.Stash.Entries.First(e => e.DefinitionId == "item.combat-blade");
            Assert.That(inventory.Core.FromStash(dariusId, blade.InstanceId, 1).Moved, Is.EqualTo(1));
            Assert.That(inventory.Core.Equip(dariusId, blade.InstanceId).Ok, Is.True);

            yield return rig.Generate(12345);

            Assert.That(AttackerOf(0).Role, Is.EqualTo(CombatRole.Melee));
            Assert.That(AttackerOf(0).Range, Is.EqualTo(2f));
            Assert.That(AttackerOf(1).Range, Is.EqualTo(16f), "Kestrel is unchanged");
            Assert.That(AttackerOf(2).Range, Is.EqualTo(8f), "Sable shares the rifle definition and is unchanged");
            var archetype = Load<CombatArchetype>("Assets/_Project/Data/Archetypes/Melee.asset");
            Assert.That(archetype.Range, Is.EqualTo(2f), "the shared asset was not modified");
        }

        [UnityTest]
        public IEnumerator ArmorAndBoots_RaiseHealthAndSpeed_AtSpawn_WithoutHealingLater()
        {
            var id = roster.Members[0].Id;
            foreach (var definitionId in new[] { "item.light-vest", "item.servo-boots" })
            {
                var entry = inventory.Core.Session.Stash.Entries.First(e => e.DefinitionId == definitionId);
                inventory.Core.FromStash(id, entry.InstanceId, 1);
                Assert.That(inventory.Core.Equip(id, entry.InstanceId).Ok, Is.True);
            }
            var baseHealth = roster.Evaluate(roster.Members[0]).MaxHealth;

            yield return rig.Generate(12345);

            var unit = rig.Director.Friendlies[0];
            var health = unit.GetComponent<Health>();
            Assert.That(health.Max, Is.EqualTo(baseHealth + 20));
            Assert.That(health.Current, Is.EqualTo(health.Max), "a mission starts at full effective health");
            health.TakeDamage(30);
            roster.AwardExperience(id, 10);   // a roster change re-applies the configuration
            yield return null;
            Assert.That(health.Max, Is.EqualTo(baseHealth + 20), "re-applying does not add the bonus again");
            Assert.That(health.Current, Is.EqualTo(health.Max - 30), "re-applying does not heal");
        }

        [UnityTest]
        public IEnumerator EmptyWeaponSlot_SpawnsAnUnarmedUnit_ThatSaysSo()
        {
            var id = roster.Members[0].Id;
            Assert.That(inventory.Core.Unequip(id, ItemSlot.Weapon).Ok, Is.True);

            yield return rig.Generate(12345);

            var unit = rig.Director.Friendlies[0];
            Assert.That(AttackerOf(0).HasWeapon, Is.False);
            Assert.That(unit.Issue(new AttackCommand(rig.Director.Hostiles[0].GetComponent<Health>())), Is.False);
            Assert.That(unit.LastRefusal, Is.EqualTo(CommandRefusal.NoWeapon));
            Assert.That(unit.TryGetComponent<UnitAbilities>(out var abilities) && abilities.Count > 0, Is.True, "abilities are unaffected");
        }

        [UnityTest]
        public IEnumerator Regeneration_DoesNotRegrantStarterItems()
        {
            var id = roster.Members[0].Id;
            var before = inventory.Core.Session.Loadout(id).Bag.Count;

            yield return rig.Generate(12345);
            yield return rig.Generate(777);
            yield return rig.Generate(12345);

            Assert.That(inventory.Core.Session.Loadout(id).Bag.Count, Is.EqualTo(before));
            Assert.That(inventory.Core.Session.Stash.CountOf("item.medkit"), Is.EqualTo(4));
        }

        [UnityTest]
        public IEnumerator UnitIdentity_ReportsTheInventory_AndNoRosterMeansNoEquipmentSystem()
        {
            yield return rig.Generate(12345);

            Assert.That(rig.Director.Friendlies[0].GetComponent<UnitIdentity>().Inventory == inventory, Is.True);

            var plain = new MissionRig();
            yield return plain.Generate(12345);
            Assert.That(plain.Director.Friendlies[0].GetComponent<UnitAttacker>().HasWeapon, Is.True);
            plain.Dispose();
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.EquipmentSpawnPlayModeTests"` → `EXIT=1` (`UnitIdentity.Inventory`).

- [ ] **Step 3: Implement the binding**

`UnitIdentity.cs`: add `SquadInventory inventory;` and `public SquadInventory Inventory => inventory;`. Change `Bind`:

```csharp
        internal void Bind(SquadRoster squad, RosterMember operative, SquadInventory items = null)
        {
            Unsubscribe();
            roster = squad;
            member = operative;
            inventory = items;
            Reapply();
            if (isActiveAndEnabled)
                Subscribe();
        }
```

Replace `Reapply` and `Apply`:

```csharp
        void Reapply()
        {
            if (roster == null || member == null)
                return;
            // Recomputed from the roster state and the unit's equipment every time; never added to the last result.
            var equipped = inventory != null ? inventory.EquippedFor(member.Id) : default;
            Effective = roster.Evaluate(member, equipped);
            HasConfiguration = true;
            Apply(Effective, equipped);
        }

        void Apply(EffectiveConfiguration config, EquippedItems equipped)
        {
            if (TryGetComponent<Health>(out var health))
                health.SetMax(config.MaxHealth);
            if (TryGetComponent<UnitMover>(out var mover))
                mover.SetSpeed(config.MoveSpeed);
            if (TryGetComponent<UnitAttacker>(out var attacker))
                attacker.ApplyEffective(config.AttackRole, config.AttackRange, config.AttackDamage, config.AttackInterval, config.HasWeapon);
            if (TryGetComponent<UnitAbilities>(out var abilities))
                abilities.SetModifiers(config.AbilityPower, config.AbilityCooldownMultiplier);
            if (equipped.IsSet && TryGetComponent<UnitWeaponVisual>(out var visual))
                visual.Show(equipped.Weapon != null ? equipped.Weapon.WeaponVisual : null);
        }
```

`MissionSpawner.cs`: change the bind line to

```csharp
                if (member != null)
                    unit.gameObject.AddComponent<UnitIdentity>().Bind(roster, member, systems.inventory);   // after archetype and abilities: it scales them
```

- [ ] **Step 4: Measure the baked rifles, then write the failing weapon-visual tests**

Before any prefab change, capture the current geometry of the baked rifles. Create a temporary `Assets/_Project/Editor/RifleMeasure.cs` with a menu item that, for `Darius_Visual.prefab` and `EnemyUnit_Visual.prefab`, instantiates the prefab, finds the baked rifle (`WeaponSocket/Darius_Rifle`, `WeaponSocket/Rifle`), and logs for its first `Renderer`: `renderer.bounds.center - hand.position` and `renderer.bounds.size`, each expressed in the **unit root's local space** (`root.InverseTransformVector(...)`; the hand is the socket's parent). Run it in batch mode and copy the numbers into the constants below. Then delete the temporary script and its `.meta`.

`Assets/_Project/Tests/PlayMode/WeaponVisualPlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class WeaponVisualPlayModeTests
    {
        const string Friendly = "Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Friendly.prefab";
        const string Hostile = "Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Hostile.prefab";
        const string Darius = "Assets/Art/Characters/Darius/Prefabs/Darius_Player.prefab";
        const string Visuals = "Assets/_Project/Data/Items/Visuals/";

        // Measured on the repository before the baked rifles were replaced (Step 4): rifle renderer bounds in the unit
        // root's local space, relative to the right hand. Paste the measured values; a zero size means no measurement.
        static readonly Vector3 EnemyCenter = Vector3.zero;   // REPLACE with the measurement
        static readonly Vector3 EnemySize = Vector3.zero;     // REPLACE with the measurement
        static readonly Vector3 DariusCenter = Vector3.zero;  // REPLACE with the measurement
        static readonly Vector3 DariusSize = Vector3.zero;    // REPLACE with the measurement

        TestWorld world;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown() => world.Dispose();

        GameObject Spawn(string path)
        {
            var instance = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            return world.Track(instance);
        }

        static Transform Socket(GameObject unit) =>
            unit.GetComponentsInChildren<Transform>(true).First(t => t.name == UnitWeaponVisual.SocketName);

        static Transform[] Managed(GameObject unit) =>
            Socket(unit).Cast<Transform>().Where(t => t.name == UnitWeaponVisual.ManagedName).ToArray();

        static WeaponVisualAsset Visual(string name) => AssetDatabase.LoadAssetAtPath<WeaponVisualAsset>(Visuals + name + ".asset");

        [UnityTest]
        public IEnumerator Units_WithoutALoadout_KeepTheirDefaultRifle_ExactlyOneManagedChild()
        {
            foreach (var path in new[] { Friendly, Hostile, Darius })
            {
                var unit = Spawn(path);
                yield return null;
                Assert.That(Managed(unit), Has.Length.EqualTo(1), path);
                Assert.That(Socket(unit).Cast<Transform>().Count(), Is.EqualTo(1), $"{path}: no baked rifle remains beside the managed one");
            }
        }

        [UnityTest]
        public IEnumerator Show_ReplacesOnlyTheManagedChild_AndLeavesTheRootAlone()
        {
            var unit = Spawn(Friendly);
            yield return null;
            var rootScale = unit.transform.lossyScale;
            var rootPosition = unit.transform.position;
            var visual = unit.GetComponent<UnitWeaponVisual>();
            var first = visual.Current;

            visual.Show(Visual("MarksmanRifleVisual"));

            Assert.That(Managed(unit), Has.Length.EqualTo(1));
            Assert.That(visual.Current != first, Is.True);
            Assert.That(visual.Current.transform.localScale.z, Is.EqualTo(1.25f).Within(1e-4f));
            Assert.That(unit.transform.lossyScale, Is.EqualTo(rootScale));
            Assert.That(unit.transform.position, Is.EqualTo(rootPosition));
            Assert.That(unit.GetComponent<Health>() != null && unit.GetComponent<CommandableUnit>() != null, Is.True);
        }

        [UnityTest]
        public IEnumerator Show_Null_RemovesTheWeapon_AndShowingTheSameVisualTwiceKeepsOneChild()
        {
            var unit = Spawn(Friendly);
            yield return null;
            var visual = unit.GetComponent<UnitWeaponVisual>();

            visual.Show(null);
            Assert.That(Managed(unit), Is.Empty);
            Assert.That(visual.Current == null, Is.True);

            visual.Show(Visual("RifleVisual"));
            visual.Show(Visual("RifleVisual"));
            Assert.That(Managed(unit), Has.Length.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ManagedChild_HasNoCollider_AndTheGameplayRootKeepsItsOwn()
        {
            var unit = Spawn(Friendly);
            yield return null;

            Assert.That(unit.GetComponent<UnitWeaponVisual>().Current.GetComponentsInChildren<Collider>(true), Is.Empty);
            Assert.That(unit.GetComponent<Collider>() != null, Is.True);
        }

        [UnityTest]
        public IEnumerator Capsule_WithTheComponentButNoSocket_DoesNothingAndDoesNotThrow()
        {
            var unit = world.CreateFighter(Vector3.zero);
            var visual = unit.gameObject.AddComponent<UnitWeaponVisual>();

            Assert.DoesNotThrow(() => visual.Show(Visual("RifleVisual")));
            yield return null;
            Assert.That(visual.Current == null, Is.True);
        }

        [UnityTest]
        public IEnumerator TheReplacedRifle_SitsWhereTheBakedOneDid()
        {
            Assert.That(EnemySize.magnitude, Is.GreaterThan(0.1f), "the baked-rifle measurement was not pasted in");
            yield return AssertGeometry(Friendly, EnemyCenter, EnemySize);
            yield return AssertGeometry(Darius, DariusCenter, DariusSize);
        }

        IEnumerator AssertGeometry(string path, Vector3 center, Vector3 size)
        {
            var unit = Spawn(path);
            yield return null;
            var renderer = unit.GetComponent<UnitWeaponVisual>().Current.GetComponentInChildren<Renderer>();
            var hand = Socket(unit).parent;
            var measuredCenter = unit.transform.InverseTransformVector(renderer.bounds.center - hand.position);
            var measuredSize = unit.transform.InverseTransformVector(renderer.bounds.size);
            Assert.That(Vector3.Distance(measuredCenter, center), Is.LessThan(0.02f), $"{path} centre {measuredCenter} vs {center}");
            Assert.That(Vector3.Distance(measuredSize, size), Is.LessThan(0.02f), $"{path} size {measuredSize} vs {size}");
        }
    }
}
#endif
```

- [ ] **Step 5: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.WeaponVisualPlayModeTests"` → `EXIT=1` (`UnitWeaponVisual` missing).

- [ ] **Step 6: Implement `UnitWeaponVisual`**

`Assets/_Project/Scripts/Items/UnitWeaponVisual.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Presentation only: keeps exactly one managed weapon model as a child of the unit's WeaponSocket (the right-hand
    /// attachment the character prefabs already carry). Show replaces only that child; the gameplay root, its scale and
    /// the unit's identity are never touched. The socket's own local transform is the per-character alignment; the weapon
    /// asset adds the per-weapon pose. A unit with no WeaponSocket (the capsule units) shows nothing and logs nothing.
    /// A prefab-assigned default shows until the first explicit Show (units with no loadout, hostiles).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UnitWeaponVisual : MonoBehaviour
    {
        public const string SocketName = "WeaponSocket";
        public const string ManagedName = "EquippedWeapon";

        [SerializeField] WeaponVisualAsset defaultVisual;

        Transform socket;
        GameObject current;
        WeaponVisualAsset shown;
        bool explicitlySet;

        /// <summary>The managed model currently in the hand, or null.</summary>
        public GameObject Current => current;

        void Awake()
        {
            if (!explicitlySet && defaultVisual != null)
                Place(defaultVisual);
        }

        /// <summary>Shows this weapon (null shows none). Showing the one already shown changes nothing.</summary>
        public void Show(WeaponVisualAsset asset)
        {
            explicitlySet = true;
            if (asset == shown && (current != null || asset == null || asset.Prefab == null))
                return;
            Place(asset);
        }

        void Place(WeaponVisualAsset asset)
        {
            if (current != null)
            {
                current.transform.SetParent(null);   // leaves the socket at once; Destroy only runs at the end of the frame
                if (Application.isPlaying)
                    Destroy(current);
                else
                    DestroyImmediate(current);
                current = null;
            }
            shown = asset;
            if (asset == null || asset.Prefab == null)
                return;
            var hand = Socket();
            if (hand == null)
                return;
            current = Instantiate(asset.Prefab, hand);
            current.name = ManagedName;
            current.transform.localPosition = asset.LocalPosition;
            current.transform.localRotation = Quaternion.Euler(asset.LocalEuler);
            current.transform.localScale = asset.LocalScale;
            EnvironmentVisualBuilder.StripColliders(current);
        }

        Transform Socket()
        {
            if (socket != null)
                return socket;
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == SocketName)
                    return socket = t;
            }
            return null;
        }
    }
}
```

- [ ] **Step 7: Migrate the prefabs (builder extension)**

Add to `InventoryDataBuilder`:

```csharp
        const string DariusVisualPrefab = "Assets/Art/Characters/Darius/Prefabs/Darius_Visual.prefab";
        const string EnemyVisualPrefab = "Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Visual.prefab";
        static readonly string[] UnitPrefabs =
        {
            "Assets/Art/Characters/Darius/Prefabs/Darius_Player.prefab",
            "Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Friendly.prefab",
            "Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Hostile.prefab",
        };

        [MenuItem("Blackglass/Inventory/Migrate Baked Rifles To Managed Weapon Visual")]
        public static void MigrateRiflesMenu() => MigrateRifles();

        public static void MigrateRiflesFromCommandLine() => MigrateRifles();

        /// <summary>
        /// Replaces the rifle baked into each character visual with the managed child: the old child's pose moves to the
        /// WeaponSocket (the per-character alignment), the baked child is removed, and every unit prefab gets a
        /// UnitWeaponVisual whose default is the standard rifle. Idempotent: a prefab with no baked rifle is left alone.
        /// </summary>
        public static void MigrateRifles()
        {
            var rifle = Load<WeaponVisualAsset>(VisualDir + "/RifleVisual.asset");
            MigrateVisual(EnemyVisualPrefab, "Rifle", viaFbxInstance: false);
            MigrateVisual(DariusVisualPrefab, "Darius_Rifle", viaFbxInstance: true);
            foreach (var path in UnitPrefabs)
            {
                var contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var component = contents.GetComponent<UnitWeaponVisual>();
                    if (component == null)
                        component = contents.AddComponent<UnitWeaponVisual>();
                    var serialized = new SerializedObject(component);
                    serialized.FindProperty("defaultVisual").objectReferenceValue = rifle;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    PrefabUtility.SaveAsPrefabAsset(contents, path);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }
            AssetDatabase.SaveAssets();
        }

        static void MigrateVisual(string path, string bakedName, bool viaFbxInstance)
        {
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Transform socket = null;
                foreach (var t in contents.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == UnitWeaponVisual.SocketName)
                    {
                        socket = t;
                        break;
                    }
                }
                var baked = socket != null ? socket.Find(bakedName) : null;
                if (baked == null)
                    return;   // already migrated

                if (!viaFbxInstance)
                {
                    // The baked rifle is a Rifle.prefab instance: its pose is exactly what the socket should carry.
                    socket.localPosition = baked.localPosition;
                    socket.localRotation = baked.localRotation;
                    socket.localScale = baked.localScale;
                }
                else
                {
                    // The baked rifle is the FBX instance. Place a Rifle.prefab instance so that its RifleModel lands
                    // exactly where the baked rifle's root was, and give the socket that pose.
                    var probe = (GameObject)PrefabUtility.InstantiatePrefab(Load<GameObject>(RiflePrefab), socket);
                    var model = probe.transform.Find("RifleModel");
                    var rotation = baked.rotation * Quaternion.Inverse(model.localRotation);
                    probe.transform.rotation = rotation;
                    probe.transform.position = baked.position - rotation * Vector3.Scale(model.localPosition, probe.transform.lossyScale);
                    socket.position = probe.transform.position;
                    socket.rotation = probe.transform.rotation;
                    UnityEngine.Object.DestroyImmediate(probe);
                }
                UnityEngine.Object.DestroyImmediate(baked.gameObject);
                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }
```

The socket must have no other children (the test `no baked rifle remains beside the managed one` enforces this). The FBX-instance branch assumes uniform scale on the parents; the geometry test is the judge: if Darius is off by more than 0.02 m, fix the arithmetic, never the tolerance.

- [ ] **Step 8: Migrate, then run the tests**

```bash
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -quit -projectPath "$(pwd -W)" \
  -executeMethod Blackglass.EditorTools.InventoryDataBuilder.MigrateRiflesFromCommandLine -logFile Logs/MigrateRifles.log; echo EXIT=$?
Tools/run-tests.sh PlayMode "Blackglass.Tests.WeaponVisualPlayModeTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.EquipmentSpawnPlayModeTests"
Tools/run-tests.sh EditMode "Blackglass.Tests.WeaponAssetTests"
Tools/run-tests.sh EditMode "Blackglass.Tests.EnemyUnitAssetTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.DariusAssetTests"
```

Expected: all `EXIT=0`, except that the three existing asset tests that assert the *baked* rifle (`WeaponAssetTests` ~lines 96–101, `EnemyUnitAssetTests` ~85–101, `DariusAssetTests` ~70) must be updated in place to look for the `WeaponSocket` plus a `UnitWeaponVisual` with a non-null default (and, where they measured the baked rifle's pose, the managed child after one frame). Keep their intent (a rifle exists and the grip is right) and report each edit.

- [ ] **Step 9: Milestone-B checkpoint**

Run both full suites. Expected: every test that was green at Task 0 is still green, plus all new ones. Report the two totals and `git status --short`. The rifle grip by eye goes on the manual-test sheet (Task 18).

---

## Milestone C — Consumables, loot containers, placement, fog

### Task 10: `UseItemCommand` and `UnitItems`

**Files:**
- Create: `Assets/_Project/Scripts/Items/UnitItems.cs`, `Assets/_Project/Tests/PlayMode/TestSupport/InventoryKit.cs`
- Modify: `Assets/_Project/Scripts/Commands/UnitCommands.cs`, `Assets/_Project/Scripts/Units/CommandableUnit.cs`, `Assets/_Project/Scripts/Mission/MissionSpawner.cs`
- Test: `Assets/_Project/Tests/PlayMode/UseItemPlayModeTests.cs`

**Interfaces:**
- Consumes: `SquadInventory.WorkingLoadout/Catalogue/Core.NotifyChanged` (T4), `Health.Heal/Current/Max`.
- Produces: `UseItemCommand(string instanceId)` with `InstanceId`; `enum ItemUseFailure { None, Dead, NoLoadout, NotOwned, NotUsable, NoEffect }`; `UnitItems` with `Bind(SquadInventory, string operativeId)`, `OperativeId`, `Check(string instanceId, bool full = true)`, `TryUse(string)`, `Record(ItemUseFailure)`, `LastUseFailure`, `LastUseFailureTime`, `UsedCount`, events `Used(ItemDefinition)` and `UseFailed(ItemUseFailure)`; test helper `InventoryKit`.

- [ ] **Step 1: Write the test helper**

`Assets/_Project/Tests/PlayMode/TestSupport/InventoryKit.cs`:

```csharp
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>Test items, a catalogue and a SquadInventory with a mission already begun, for tests that need no director.</summary>
    internal sealed class InventoryKit
    {
        public readonly ItemDefinition Medkit;
        public readonly ItemDefinition Rifle;
        public readonly ItemDefinition Vest;
        public readonly ItemCatalogue Catalogue;
        public readonly SquadInventory Inventory;
        readonly TestWorld world;

        public InventoryKit(TestWorld world, int bagCapacity = 8, params string[] operativeIds)
        {
            this.world = world;
            var archetype = world.Track(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));
            Medkit = world.Track(ItemDefinition.Create("medkit", "Medkit", ItemCategory.Consumable, ItemSlot.None, 3, default, null, 40));
            Rifle = world.Track(ItemDefinition.Create("rifle", "Service Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: archetype));
            Vest = world.Track(ItemDefinition.Create("vest", "Vest", ItemCategory.Armor, ItemSlot.Armor));
            Catalogue = world.Track(ItemCatalogue.Create(Medkit, Rifle, Vest));
            var host = world.Track(new GameObject("Inventory"));
            Inventory = host.AddComponent<SquadInventory>();
            Inventory.Initialize(Catalogue, null, null, bagCapacity, null);
            foreach (var id in operativeIds)
                Inventory.Core.EnsureOperative(id);
        }

        /// <summary>Starts the mission working state (a copy of the session), as the director does before spawning.</summary>
        public void BeginMission(string missionId = "kit-mission") => Inventory.BeginMission(missionId);

        /// <summary>The operative's bag in the working state during a mission, else in the session.</summary>
        public ItemInventory Bag(string operativeId)
        {
            var state = Inventory.Core.Working ?? Inventory.Core.Session;
            return state.Loadout(operativeId).Bag;
        }

        /// <summary>Adds a UnitItems to the unit, bound to this inventory and operative.</summary>
        public UnitItems Equip(CommandableUnit unit, string operativeId)
        {
            var items = unit.gameObject.AddComponent<UnitItems>();
            items.Bind(Inventory, operativeId);
            return items;
        }

        /// <summary>A searched (or not) loot container with these contents, at ground level; no collider, so it carves nothing.</summary>
        public LootContainer CreateContainer(Vector3 ground, bool searched, params (ItemDefinition item, int quantity)[] items)
        {
            var host = world.Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            host.name = "TestLootContainer";
            host.transform.position = ground + Vector3.up * 0.4f;
            Object.DestroyImmediate(host.GetComponent<Collider>());
            var container = host.AddComponent<LootContainer>();
            var contents = new ItemInventory(0);
            foreach (var (item, quantity) in items)
                contents.Add(item, quantity);
            container.InitializeWith(contents, searched);
            return container;
        }
    }
}
```

(`LootContainer.InitializeWith` is defined in Task 12; this helper compiles from Task 12 on. Until then keep `CreateContainer` out of the file and add it in Task 12 Step 1.)

For this task, create the file **without** the `CreateContainer` method.

- [ ] **Step 2: Write the failing tests**

`Assets/_Project/Tests/PlayMode/UseItemPlayModeTests.cs`:

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UseItemPlayModeTests
    {
        const string Op = "op-1";
        TestWorld world;
        InventoryKit kit;
        TacticalPause pause;
        CommandableUnit unit;
        UnitItems items;
        Health health;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            kit = new InventoryKit(world, 8, Op);
            kit.BeginMission();
            unit = world.CreateFighter(Vector3.zero);
            items = kit.Equip(unit, Op);
            health = unit.GetComponent<Health>();
            kit.Bag(Op).Add(kit.Medkit, 2);
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        string MedkitId => kit.Bag(Op).Entries[0].InstanceId;
        int Medkits => kit.Bag(Op).CountOf("medkit");

        [UnityTest]
        public IEnumerator Use_Heals_AndConsumesExactlyOne()
        {
            health.TakeDamage(60);
            yield return null;

            Assert.That(unit.Issue(new UseItemCommand(MedkitId)), Is.True);
            yield return null;

            Assert.That(health.Current, Is.EqualTo(80));
            Assert.That(Medkits, Is.EqualTo(1));
            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(items.UsedCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Use_AtFullHealth_IsRejected_AndNothingIsConsumed()
        {
            yield return null;

            var accepted = unit.Issue(new UseItemCommand(MedkitId));

            Assert.That(accepted, Is.False);
            Assert.That(items.LastUseFailure, Is.EqualTo(ItemUseFailure.NoEffect));
            Assert.That(Medkits, Is.EqualTo(2));
            Assert.That(health.Current, Is.EqualTo(health.Max));
        }

        [UnityTest]
        public IEnumerator Checking_NeverConsumes()
        {
            health.TakeDamage(60);
            yield return null;

            for (var i = 0; i < 5; i++)
                Assert.That(items.Check(MedkitId), Is.EqualTo(ItemUseFailure.None));

            Assert.That(Medkits, Is.EqualTo(2));
            Assert.That(health.Current, Is.EqualTo(40));
        }

        [UnityTest]
        public IEnumerator Paused_TheOrderWaits_AndRunsOnResume()
        {
            health.TakeDamage(60);
            yield return null;
            pause.Pause();

            Assert.That(unit.Issue(new UseItemCommand(MedkitId)), Is.True);
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(health.Current, Is.EqualTo(40), "nothing heals while paused");
            Assert.That(Medkits, Is.EqualTo(2));

            pause.Resume();
            yield return null;
            yield return null;
            Assert.That(health.Current, Is.EqualTo(80));
            Assert.That(Medkits, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Paused_ThenStop_ConsumesNothing()
        {
            health.TakeDamage(60);
            yield return null;
            pause.Pause();
            unit.Issue(new UseItemCommand(MedkitId));

            unit.Issue(new StopCommand());
            pause.Resume();
            yield return null;
            yield return null;

            Assert.That(Medkits, Is.EqualTo(2));
            Assert.That(health.Current, Is.EqualTo(40));
        }

        [UnityTest]
        public IEnumerator TwoQueuedUses_WithOneMedkit_FirstSucceeds_SecondFailsCleanly()
        {
            kit.Bag(Op).Remove(MedkitId, 1);   // one medkit left
            health.TakeDamage(90);
            yield return null;
            Assert.That(unit.Issue(new MoveCommand(new Vector3(4f, 0f, 0f))), Is.True);
            var id = MedkitId;

            Assert.That(unit.Issue(new UseItemCommand(id), IssueMode.Append), Is.True, "both are accepted: the item is owned now");
            Assert.That(unit.Issue(new UseItemCommand(id), IssueMode.Append), Is.True);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);

            Assert.That(health.Current, Is.EqualTo(50), "one use healed 40");
            Assert.That(Medkits, Is.EqualTo(0));
            Assert.That(items.LastUseFailure, Is.EqualTo(ItemUseFailure.NotOwned));
            Assert.That(unit.PendingCommands, Is.Empty, "the queue moved on, nothing is stuck");
        }

        [UnityTest]
        public IEnumerator LosingTheItemBeforeItsTurn_FailsCleanly_WithoutConsumingAnything()
        {
            health.TakeDamage(60);
            yield return null;
            unit.Issue(new MoveCommand(new Vector3(4f, 0f, 0f)));
            var id = MedkitId;
            Assert.That(unit.Issue(new UseItemCommand(id), IssueMode.Append), Is.True);

            kit.Bag(Op).Remove(id, 2);   // another route took them
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);

            Assert.That(items.LastUseFailure, Is.EqualTo(ItemUseFailure.NotOwned));
            Assert.That(health.Current, Is.EqualTo(40));
            Assert.That(items.UsedCount, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator HealthChangingBeforeItsTurn_IsJudgedAtExecution()
        {
            health.TakeDamage(30);
            yield return null;
            unit.Issue(new MoveCommand(new Vector3(4f, 0f, 0f)));
            Assert.That(unit.Issue(new UseItemCommand(MedkitId), IssueMode.Append), Is.True);

            health.Heal(100);   // healed some other way while walking
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);

            Assert.That(Medkits, Is.EqualTo(2), "full health at execution: not consumed");
            Assert.That(items.LastUseFailure, Is.EqualTo(ItemUseFailure.NoEffect));
        }

        [UnityTest]
        public IEnumerator AHeldMoveKey_DoesNotDropAUsableItemOrder()
        {
            health.TakeDamage(60);
            yield return null;
            unit.SetMoveIntent(Vector3.forward);

            Assert.That(unit.Issue(new UseItemCommand(MedkitId)), Is.True);
            yield return null;
            yield return null;
            unit.SetMoveIntent(Vector3.zero);

            Assert.That(Medkits, Is.EqualTo(1));
            Assert.That(health.Current, Is.EqualTo(80));
        }

        [UnityTest]
        public IEnumerator ADeadUnit_CannotUse()
        {
            yield return null;
            health.TakeDamage(1000);
            var id = MedkitId;

            Assert.That(items.Check(id), Is.EqualTo(ItemUseFailure.Dead));
            Assert.That(unit.Issue(new UseItemCommand(id)), Is.False);
            Assert.That(Medkits, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator NonConsumables_AndUnknownEntries_AreNotUsable()
        {
            kit.Bag(Op).Add(kit.Rifle, 1);
            health.TakeDamage(60);
            yield return null;
            var rifleId = kit.Bag(Op).Entries[1].InstanceId;

            Assert.That(items.Check(rifleId), Is.EqualTo(ItemUseFailure.NotUsable));
            Assert.That(items.Check("nope"), Is.EqualTo(ItemUseFailure.NotOwned));
            Assert.That(unit.Issue(new UseItemCommand(rifleId)), Is.False);
        }

        [UnityTest]
        public IEnumerator UsingAfterTheMissionEnded_FailsWithNoLoadout()
        {
            health.TakeDamage(60);
            yield return null;
            var id = MedkitId;
            kit.Inventory.Core.AbortMission();

            Assert.That(items.Check(id), Is.EqualTo(ItemUseFailure.NoLoadout));
            Assert.That(unit.Issue(new UseItemCommand(id)), Is.False);
        }

        [Test]
        public void Command_NeedsAnId()
        {
            Assert.Throws<System.ArgumentException>(() => new UseItemCommand(" "));
        }
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.UseItemPlayModeTests"` → `EXIT=1` (`UseItemCommand`, `UnitItems` missing).

- [ ] **Step 4: Implement**

`UnitCommands.cs` — add before `StopCommand`:

```csharp
    /// <summary>
    /// Use an owned consumable on the unit itself. Plain data like every order: the unit checks it when it is issued and
    /// again when it runs (UnitItems), and a failure ends the order without consuming anything. Instant; it names the
    /// item by its entry's instance id, never by its name.
    /// </summary>
    public sealed class UseItemCommand : UnitCommand
    {
        public UseItemCommand(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
                throw new ArgumentException("An item order needs the entry's instance id.", nameof(instanceId));
            InstanceId = instanceId;
        }

        public string InstanceId { get; }
    }
```

Also update the class comment's list of commands ("Move, Attack, MoveToCover, Ability, Interact, UseItem, Collect and Stop").

`Assets/_Project/Scripts/Items/UnitItems.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    public enum ItemUseFailure
    {
        None,
        Dead,
        /// <summary>The unit has no working inventory (no mission is running, or it was never bound).</summary>
        NoLoadout,
        /// <summary>The entry is not in this operative's bag (it was used up or taken).</summary>
        NotOwned,
        /// <summary>The item cannot be used this way (not a consumable).</summary>
        NotUsable,
        /// <summary>It would do nothing now (healing at full health).</summary>
        NoEffect,
    }

    /// <summary>
    /// A unit's item capability: the rules for using a consumable from its own working bag, and for collecting loot into
    /// it (Collect is added in a later task). CommandableUnit runs the orders; this component only answers "may I" and
    /// does the use: validate, heal through Health (the one healing implementation), then consume one unit. Selecting an
    /// item or asking Check never changes anything. Added to the friendly units at spawn, like UnitAbilities.
    /// </summary>
    public sealed class UnitItems : MonoBehaviour
    {
        SquadInventory inventory;
        string operativeId;
        Health health;

        public string OperativeId => operativeId;
        public ItemUseFailure LastUseFailure { get; private set; }
        /// <summary>Unscaled time of the last failure, so the panel can show it for a few seconds even while paused.</summary>
        public float LastUseFailureTime { get; private set; }
        public int UsedCount { get; private set; }

        public event Action<ItemDefinition> Used;
        public event Action<ItemUseFailure> UseFailed;

        Health OwnHealth => health != null ? health : health = GetComponent<Health>();

        internal void Bind(SquadInventory items, string operative)
        {
            inventory = items;
            operativeId = operative;
        }

        OperativeLoadout Loadout => inventory != null ? inventory.WorkingLoadout(operativeId) : null;

        /// <summary>
        /// Whether the entry can be used now. `full` false skips the check that depends on where the unit will be and how
        /// hurt it is by then (used for an order queued behind others); execution always checks in full.
        /// </summary>
        public ItemUseFailure Check(string instanceId, bool full = true)
        {
            var own = OwnHealth;
            if (own == null)
                return ItemUseFailure.NotUsable;
            if (!own.IsAlive)
                return ItemUseFailure.Dead;
            var loadout = Loadout;
            if (loadout == null)
                return ItemUseFailure.NoLoadout;
            var entry = loadout.Bag.Find(instanceId);
            if (entry == null)
                return ItemUseFailure.NotOwned;
            var definition = inventory.Catalogue != null ? inventory.Catalogue.Find(entry.DefinitionId) : null;
            if (definition == null || definition.Category != ItemCategory.Consumable || definition.HealAmount < 1)
                return ItemUseFailure.NotUsable;
            if (full && own.Current >= own.Max)
                return ItemUseFailure.NoEffect;
            return ItemUseFailure.None;
        }

        /// <summary>Validates, heals, then consumes one unit. False (with the reason in LastUseFailure) when it cannot, consuming nothing.</summary>
        public bool TryUse(string instanceId)
        {
            var failure = Check(instanceId);
            if (failure != ItemUseFailure.None)
            {
                Record(failure);
                return false;
            }
            var loadout = Loadout;
            var entry = loadout.Bag.Find(instanceId);
            var definition = inventory.Catalogue.Find(entry.DefinitionId);
            var restored = OwnHealth.Heal(definition.HealAmount);
            if (restored <= 0)
            {
                Record(ItemUseFailure.NoEffect);
                return false;
            }
            loadout.Bag.Remove(instanceId, 1);
            UsedCount++;
            Used?.Invoke(definition);
            inventory.Core.NotifyChanged();
            return true;
        }

        internal void Record(ItemUseFailure failure)
        {
            LastUseFailure = failure;
            LastUseFailureTime = Time.unscaledTime;
            UseFailed?.Invoke(failure);
        }
    }
}
```

`CommandableUnit.cs`:

1. Field/accessor next to the other lazy accessors: `UnitItems items;` and `UnitItems Items => items != null ? items : items = GetComponent<UnitItems>();`
2. In `Issue`'s accepted-type switch add `case UseItemCommand _:` to the list with `InteractCommand`.
3. After the `startingInteract` check in `Issue` add:

```csharp
            if (command is UseItemCommand startingUse && !CanOrderUse(startingUse))
                return false;
```

4. In `CanStart` add:

```csharp
                case UseItemCommand use:
                    return Items != null && CheckUse(use, full: false);
```

5. In `TryStartCommand` add (next to the ability case):

```csharp
                case UseItemCommand _:
                    Mover.Stop();
                    Cover.ReleaseReservation();
                    ResetAttack();
                    return true;
```

6. Add the helpers (near `CanOrderInteract`):

```csharp
        // An item order that would start now: the unit has the item and it would do something.
        bool CanOrderUse(UseItemCommand order) => Items != null && CheckUse(order, full: true);

        bool CheckUse(UseItemCommand order, bool full)
        {
            var failure = Items.Check(order.InstanceId, full);
            if (failure == ItemUseFailure.None)
                return true;
            Items.Record(failure);
            return false;
        }
```

7. Replace `RunAbilities` with this version (the ability branch is unchanged; item orders run first on the same frame, before steering is considered, so a held move key cannot drop a usable item order):

```csharp
        AbilityFailure RunAbilities()
        {
            var guard = queue.Pending.Count + 1;
            while (guard-- > 0)
            {
                if (queue.Current is UseItemCommand use)
                {
                    if (Items != null)
                        Items.TryUse(use.InstanceId);
                    Finish();
                    continue;
                }
                if (!(queue.Current is AbilityCommand ability))
                    break;
                var owned = Abilities;
                if (owned != null)
                {
                    var failure = owned.CheckOrder(ability).Failure;
                    if (AbilityRules.IsApproachable(failure))
                        return failure;
                    owned.TryUse(ability);
                }
                Finish();
            }
            return AbilityFailure.None;
        }
```

`MissionSpawner.cs`: after the `UnitIdentity` line add

```csharp
                if (member != null && systems.inventory != null)
                    unit.gameObject.AddComponent<UnitItems>().Bind(systems.inventory, member.Id);
```

- [ ] **Step 5: Run to verify it passes**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.UseItemPlayModeTests"` → `EXIT=0`, 12 passed. Then `Tools/run-tests.sh PlayMode "Blackglass.Tests.CommandableUnit"` and `"Blackglass.Tests.CommandableUnitAbility"` → `EXIT=0` (the unified loop did not change ability behaviour).

---

### Task 11: Loot table, plan and placer (pure)

**Files:**
- Create: `Assets/_Project/Scripts/Items/LootTable.cs`, `LootPlan.cs`, `LootPlacer.cs`
- Modify: `Assets/_Project/Scripts/Mission/SeededRandom.cs`, `Assets/_Project/Editor/InventoryDataBuilder.cs`
- Test: `Assets/_Project/Tests/EditMode/LootPlacerTests.cs`

**Interfaces:**
- Consumes: `MissionLayout`, `ObjectivePlan`, `SecurityPlan`, `ObjectivePlacer.FreeTiles/ObstacleMask/Shrink` (internal), `MissionGenerator.TryAttempt`, `SecurityPlacer.TryPlace`, `ItemDefinition`.
- Produces: `SeededRandom.ForLoot(int seed, int attempt)` (container tiles) and `SeededRandom.ForLootContents(int seed, int attempt, int container)` (items); `LootEntry`, `LootTable` (`Version`, `ContainerCount`, `MinItems`, `MaxItems`, `Entries`, `IsValid(out string)`, `internal static Create(...)`); `LootItemPlan(DefinitionId, Quantity)`, `LootContainerPlan(Tile, Room, Items)`, `LootPlan` (`Containers`, `Requested`, `Placed`, `Shortfall`, `TableVersion`, `Hash`, `static Empty`); `LootPlacer.Place(layout, objectives, security, table, float avoidDistance = AvoidDistance)`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/EditMode/LootPlacerTests.cs`:

```csharp
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
        public void EveryContainer_IsOnAFreeBlock_InsideItsRoom_ClearOfEverythingElse_Across60Seeds()
        {
            var full = 0;
            var built = 0;
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
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var x = container.Tile.x + dx;
                            var y = container.Tile.y + dy;
                            Assert.That(layout.IsFloor(x, y), Is.True, $"seed {seed}: floor around {container.Tile}");
                            Assert.That(blocked[y * layout.Width + x], Is.False, $"seed {seed}: free around {container.Tile}");
                        }
                    var room = layout.Rooms[container.Room].Rect;
                    var inner = ObjectivePlacer.Shrink(room, LootPlacer.InsetFromRoom);
                    Assert.That(container.Tile.x >= inner.xMin && container.Tile.x < inner.xMax && container.Tile.y >= inner.yMin && container.Tile.y < inner.yMax,
                        Is.True, $"seed {seed}: {container.Tile} is inside the inset room");
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
        }

        [Test]
        public void ItemsComeOnlyFromTheTable_WithinTheirQuantityRange()
        {
            Assert.That(Build(12345, out var layout, out var plan, out var security, out _), Is.True);
            var loot = LootPlacer.Place(layout, plan, security, Table());

            foreach (var container in loot.Containers)
                foreach (var item in container.Items)
                {
                    Assert.That(item.DefinitionId, Is.AnyOf("medkit", "rifle"));
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.LootPlacerTests"` → `EXIT=1` (`LootTable` etc. missing).

- [ ] **Step 3: Implement**

`SeededRandom.cs` — add after `ForSecurity`:

```csharp
        /// <summary>
        /// The loot placer's stream for one attempt of one seed: container tiles only. Independent of the layout,
        /// objective and security streams (so loot never moves a wall or an objective) and of what the items are.
        /// </summary>
        public static SeededRandom ForLoot(int seed, int attempt) => ForStream(seed, attempt, 0x10075EEDC0FFEE5BUL);

        /// <summary>
        /// What is in one container: its own stream per container index, so changing the loot table never moves a
        /// container, and a container's contents do not depend on how many containers come before it.
        /// </summary>
        public static SeededRandom ForLootContents(int seed, int attempt, int container) =>
            ForStream(seed, unchecked(attempt * 4099 + container + 1), 0x7C0DEDC0117AB1E5UL);
```

`Assets/_Project/Scripts/Items/LootTable.cs`:

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>One line of a loot table: an item, a quantity range and a weight (higher is more likely).</summary>
    [Serializable]
    public struct LootEntry
    {
        public ItemDefinition item;
        [Min(1)] public int minQuantity;
        [Min(1)] public int maxQuantity;
        [Min(1)] public int weight;
    }

    /// <summary>
    /// What generated containers hold: how many containers a mission asks for, how many items each rolls, and the weighted
    /// entries they roll from. Authored data. `Version` is part of the loot plan's hash, so a table change is visible in a
    /// reproduced seed; it never affects the mission layout or where containers stand.
    /// </summary>
    [CreateAssetMenu(menuName = "Blackglass/Loot Table", fileName = "LootTable")]
    public sealed class LootTable : ScriptableObject
    {
        public const int MaxContainers = 6;

        [SerializeField, Min(1)] int version = 1;
        [SerializeField, Range(0, MaxContainers)] int containerCount = 3;
        [SerializeField, Min(1)] int minItems = 1;
        [SerializeField, Min(1)] int maxItems = 2;
        [SerializeField] LootEntry[] entries = new LootEntry[0];

        public int Version => version;
        public int ContainerCount => containerCount;
        public int MinItems => minItems;
        public int MaxItems => maxItems;
        public LootEntry[] Entries => entries;

        public bool IsValid(out string problem)
        {
            if (containerCount < 0 || containerCount > MaxContainers)
                problem = $"the container count must be 0 to {MaxContainers}";
            else if (minItems < 1 || maxItems < minItems)
                problem = "the items per container range is empty";
            else if (entries == null || entries.Length == 0)
                problem = "there are no entries";
            else
            {
                problem = null;
                for (var i = 0; i < entries.Length && problem == null; i++)
                {
                    var entry = entries[i];
                    if (entry.item == null)
                        problem = $"entry {i} has no item";
                    else if (entry.weight < 1)
                        problem = $"entry {i} ({entry.item.DisplayName}) has a weight below 1";
                    else if (entry.minQuantity < 1 || entry.maxQuantity < entry.minQuantity)
                        problem = $"entry {i} ({entry.item.DisplayName}) has an empty quantity range";
                }
            }
            return problem == null;
        }

        internal static LootTable Create(int version, int containerCount, int minItems, int maxItems, LootEntry[] entries)
        {
            var table = CreateInstance<LootTable>();
            table.version = version;
            table.containerCount = containerCount;
            table.minItems = minItems;
            table.maxItems = maxItems;
            table.entries = entries ?? new LootEntry[0];
            return table;
        }
    }
}
```

`Assets/_Project/Scripts/Items/LootPlan.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    public readonly struct LootItemPlan
    {
        public LootItemPlan(string definitionId, int quantity)
        {
            DefinitionId = definitionId;
            Quantity = quantity;
        }

        public string DefinitionId { get; }
        public int Quantity { get; }
    }

    /// <summary>One container to build: its tile, its room and what it holds (definition ids and quantities, no instance ids).</summary>
    public sealed class LootContainerPlan
    {
        public LootContainerPlan(Vector2Int tile, int room, IReadOnlyList<LootItemPlan> items)
        {
            Tile = tile;
            Room = room;
            Items = items;
        }

        public Vector2Int Tile { get; }
        public int Room { get; }
        public IReadOnlyList<LootItemPlan> Items { get; }
    }

    /// <summary>
    /// Where the mission's loot containers go and what they hold, as pure data. Hash is FNV-1a over the table version, the
    /// request and every container, so tests can pin and compare placements. Instance ids are not part of it: they are
    /// created when the containers are built and differ between deployments.
    /// </summary>
    public sealed class LootPlan
    {
        public static readonly LootPlan Empty = new LootPlan(0, 0, new List<LootContainerPlan>(), string.Empty);

        public LootPlan(int tableVersion, int requested, List<LootContainerPlan> containers, string shortfall)
        {
            TableVersion = tableVersion;
            Requested = requested;
            Containers = containers.AsReadOnly();
            Shortfall = shortfall ?? string.Empty;
            Hash = ComputeHash();
        }

        public int TableVersion { get; }
        public int Requested { get; }
        public IReadOnlyList<LootContainerPlan> Containers { get; }
        public int Placed => Containers.Count;
        /// <summary>Empty when every requested container was placed, else why some were not.</summary>
        public string Shortfall { get; }
        public ulong Hash { get; }

        ulong ComputeHash()
        {
            var h = 14695981039346656037UL;
            void Add(int value)
            {
                unchecked
                {
                    h ^= (uint)value;
                    h *= 1099511628211UL;
                }
            }
            Add(TableVersion);
            Add(Requested);
            Add(Containers.Count);
            foreach (var container in Containers)
            {
                Add(container.Room);
                Add(container.Tile.x);
                Add(container.Tile.y);
                Add(container.Items.Count);
                foreach (var item in container.Items)
                {
                    Add(item.DefinitionId.Length);
                    foreach (var c in item.DefinitionId)
                        Add(c);
                    Add(item.Quantity);
                }
            }
            return h;
        }
    }
}
```

`Assets/_Project/Scripts/Items/LootPlacer.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Chooses where loot containers go and what they hold, from the generated layout and the objective and security plans
    /// alone: pure integer tile work, deterministic per seed, attempt and table, with its own random streams
    /// (SeededRandom.ForLoot for tiles, ForLootContents per container for items), so loot never changes the layout and a
    /// change to the table's items never moves a container. Rules: a free 3x3 block (as the terminal), at least
    /// InsetFromRoom tiles inside its room (never in a doorway), AvoidDistance tiles from every spawn, guard, terminal,
    /// camera terminal, extraction tile and earlier container, and outside the squad's starting room when another room
    /// exists. Loot is optional: a container that finds no tile is reported in Shortfall, the attempt does not fail.
    /// </summary>
    public static class LootPlacer
    {
        public const float AvoidDistance = 3f;
        public const int InsetFromRoom = 2;

        public static LootPlan Place(MissionLayout layout, ObjectivePlan objectives, SecurityPlan security, LootTable table,
            float avoidDistance = AvoidDistance)
        {
            if (table == null || table.ContainerCount < 1)
                return table == null ? LootPlan.Empty : new LootPlan(table.Version, 0, new List<LootContainerPlan>(), string.Empty);

            var rng = SeededRandom.ForLoot(layout.Seed, layout.Attempt);
            var blocked = ObjectivePlacer.ObstacleMask(layout);
            var avoid = new List<Vector2Int>(layout.FriendlySpawns);
            avoid.AddRange(layout.HostileSpawns);
            if (objectives != null)
            {
                avoid.AddRange(objectives.GuardTiles);
                avoid.Add(objectives.TerminalTile);
                avoid.Add(objectives.ExtractionTile);
            }
            if (security != null && security.HasTerminal)
                avoid.Add(security.TerminalTile);

            // Rooms in a seeded order, the squad's own room last (only used when it is the only room).
            var order = new List<int>();
            for (var r = 0; r < layout.Rooms.Count; r++)
            {
                if (r != layout.FriendlyRoom || layout.Rooms.Count == 1)
                    order.Add(r);
            }
            rng.Shuffle(order);

            var containers = new List<LootContainerPlan>();
            for (var i = 0; i < table.ContainerCount; i++)
            {
                for (var step = 0; step < order.Count; step++)
                {
                    var room = order[(i + step) % order.Count];
                    var region = ObjectivePlacer.Shrink(layout.Rooms[room].Rect, InsetFromRoom);
                    var tiles = ObjectivePlacer.FreeTiles(layout, blocked, region, avoid, avoidDistance);
                    if (tiles.Count == 0)
                        continue;
                    var tile = tiles[rng.NextInt(tiles.Count)];
                    containers.Add(new LootContainerPlan(tile, room, Roll(table, SeededRandom.ForLootContents(layout.Seed, layout.Attempt, i))));
                    avoid.Add(tile);
                    break;
                }
            }
            var shortfall = containers.Count < table.ContainerCount
                ? $"placed {containers.Count} of {table.ContainerCount} loot containers: no free tile for the rest"
                : string.Empty;
            return new LootPlan(table.Version, table.ContainerCount, containers, shortfall);
        }

        static List<LootItemPlan> Roll(LootTable table, SeededRandom rng)
        {
            var total = 0;
            foreach (var entry in table.Entries)
                total += entry.weight;
            var count = rng.NextInt(table.MinItems, table.MaxItems + 1);
            var items = new List<LootItemPlan>(count);
            for (var n = 0; n < count; n++)
            {
                var pick = rng.NextInt(total);
                foreach (var entry in table.Entries)
                {
                    if (pick < entry.weight)
                    {
                        items.Add(new LootItemPlan(entry.item.Id, rng.NextInt(entry.minQuantity, entry.maxQuantity + 1)));
                        break;
                    }
                    pick -= entry.weight;
                }
            }
            return items;
        }
    }
}
```

`InventoryDataBuilder.cs` — add the loot table (called at the end of `CreateAssets`, with the six item references in scope):

```csharp
        public const string LootTablePath = Root + "/LootTable.asset";

        static void EnsureLootTable(ItemDefinition rifle, ItemDefinition marksman, ItemDefinition blade, ItemDefinition vest,
            ItemDefinition boots, ItemDefinition medkit)
        {
            if (AssetDatabase.LoadAssetAtPath<LootTable>(LootTablePath) != null)
                return;
            var table = LootTable.Create(1, 3, 1, 2, new[]
            {
                Entry(medkit, 1, 2, 4), Entry(rifle, 1, 1, 1), Entry(marksman, 1, 1, 1),
                Entry(blade, 1, 1, 1), Entry(vest, 1, 1, 2), Entry(boots, 1, 1, 2),
            });
            AssetDatabase.CreateAsset(table, LootTablePath);
        }

        static LootEntry Entry(ItemDefinition item, int min, int max, int weight) =>
            new LootEntry { item = item, minQuantity = min, maxQuantity = max, weight = weight };
```

and in `CreateAssets()` after `EnsureStarter(...)`: `EnsureLootTable(rifle, marksman, blade, vest, boots, medkit);`. Run the builder again (command in Task 7 Step 4) so `LootTable.asset` exists; add one assertion to `InventoryAssetTests`:

```csharp
        [Test]
        public void LootTable_IsValid_AndUsesOnlyCatalogueItems()
        {
            var catalogue = Load<ItemCatalogue>(Root + "ItemCatalogue.asset");
            var table = Load<LootTable>(Root + "LootTable.asset");

            Assert.That(table.IsValid(out var problem), Is.True, problem);
            foreach (var entry in table.Entries)
                Assert.That(catalogue.Find(entry.item.Id) == entry.item, Is.True, entry.item.Id);
        }
```

- [ ] **Step 4: Run to verify it passes**

Run the builder (Task 7 Step 4 command), then `Tools/run-tests.sh EditMode "Blackglass.Tests.LootPlacerTests"` → `EXIT=0`, 11 passed. The 60-seed rule test prints how many layouts placed every container; if it is below 90% do **not** loosen the rule silently: record the measured figure and report it as a ruling (as decision 033 did for the free-block size). Then `Tools/run-tests.sh EditMode "Blackglass.Tests.InventoryAssetTests"` → `EXIT=0`, 6 passed. Re-run `Tools/run-tests.sh EditMode "Blackglass.Tests.MissionGenerator"` → unchanged (golden layout hashes untouched).

---

### Task 12: Loot containers in the mission

**Files:**
- Create: `Assets/_Project/Scripts/Items/LootContainer.cs`, `Assets/_Project/Scripts/Items/LootKnowledge.cs`
- Modify: `Assets/_Project/Scripts/Mission/MissionInteractable.cs`, `Assets/_Project/Scripts/Units/UnitInteractor.cs`, `Assets/_Project/Scripts/Mission/MissionContent.cs`, `Assets/_Project/Scripts/Mission/MissionNavigation.cs`, `Assets/_Project/Scripts/Mission/MissionBuilder.cs`, `Assets/_Project/Scripts/Mission/MissionDirector.cs`, `Assets/_Project/Tests/PlayMode/TestSupport/MissionRig.cs`, `Assets/_Project/Tests/PlayMode/TestSupport/InventoryKit.cs`
- Test: `Assets/_Project/Tests/PlayMode/LootContainerPlayModeTests.cs`

**Interfaces:**
- Consumes: T4 (`SquadInventory.Catalogue`), T11, `MissionContent.InteractionRange`, `MissionNavigation.SnapRadius/PathExists`, `UnitInteractor`.
- Produces: `MissionInteractable.SetRepeatable(float)`, `.CompletionCount`, `.LastUser`, `.SetLabel(string)`; `LootContainer` (`Contents`, `IsSearched`, `IsEmpty`, `Interactable`, `Position`, `OpenRequested(CommandableUnit)`, `Changed`, `internal Initialize(IEnumerable<LootItemPlan>, ItemCatalogue, float searchSeconds)`, `internal InitializeWith(ItemInventory, bool searched)`, `internal NotifyChanged()`); `LootKnowledge.CanSeeContents(LootContainer)` and `.CanSeeLocation(IntelligenceService, LootContainer)`; `MissionContent.AddLootContainers`; `MissionNavigation.ValidateLoot`; `GeneratedMission.Loot`, `.LootContainers`; `MissionReport.LootRequested/LootPlaced/LootHash`; `MissionDirector.SetLootTable(LootTable)`; `MissionRig.AddLoot(LootTable)`.

- [ ] **Step 1: Rig and kit additions**

`MissionRig.cs`: add `public void AddLoot(LootTable table) => Director.SetLootTable(table);`.

`InventoryKit.cs`: add the `CreateContainer` method from Task 10's listing (it compiles now that `InitializeWith` exists).

- [ ] **Step 2: Write the failing tests**

`Assets/_Project/Tests/PlayMode/LootContainerPlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class LootContainerPlayModeTests
    {
        const string Items = "Assets/_Project/Data/Items/";
        MissionRig rig;
        SquadInventory inventory;
        LootTable table;

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        [SetUp]
        public void SetUp()
        {
            rig = new MissionRig();
            inventory = rig.AddInventory(Load<ItemCatalogue>(Items + "ItemCatalogue.asset"));
            table = Load<LootTable>(Items + "LootTable.asset");
            rig.AddLoot(table);
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
            rig = null;
        }

        [UnityTest]
        public IEnumerator Containers_AreBuilt_Registered_AndUnsearched_WithThePlannedContents()
        {
            yield return rig.Generate(12345);
            var mission = rig.Director.Current;

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
            Assert.That(mission.LootContainers.Count, Is.EqualTo(mission.Loot.Placed));
            Assert.That(mission.LootContainers.Count, Is.GreaterThan(0));
            Assert.That(rig.Director.Report.LootPlaced, Is.EqualTo(mission.Loot.Placed));
            for (var i = 0; i < mission.LootContainers.Count; i++)
            {
                var container = mission.LootContainers[i];
                Assert.That(container.IsSearched, Is.False);
                Assert.That(rig.Interactables.Items.Contains(container.Interactable), Is.True, "found like any interactable");
                Assert.That(container.transform.IsChildOf(mission.Geometry), Is.True);
                var planned = mission.Loot.Containers[i].Items.Sum(item => item.Quantity);
                Assert.That(container.Contents.Entries.Sum(e => e.Quantity), Is.EqualTo(planned));
            }
        }

        [UnityTest]
        public IEnumerator SameSeed_RepeatsPlacementAndContents_ButNotInstanceIds()
        {
            yield return rig.Generate(12345);
            var first = rig.Director.Current.LootContainers
                .Select(c => (pos: c.Position, items: c.Contents.Entries.OrderBy(e => e.DefinitionId).Select(e => (e.DefinitionId, e.Quantity)).ToArray(),
                              ids: c.Contents.Entries.Select(e => e.InstanceId).ToArray())).ToArray();
            var hash = rig.Director.Report.LootHash;
            var layoutHash = rig.Director.Report.LayoutHash;

            yield return rig.Generate(12345);
            var second = rig.Director.Current.LootContainers
                .Select(c => (pos: c.Position, items: c.Contents.Entries.OrderBy(e => e.DefinitionId).Select(e => (e.DefinitionId, e.Quantity)).ToArray(),
                              ids: c.Contents.Entries.Select(e => e.InstanceId).ToArray())).ToArray();

            Assert.That(rig.Director.Report.LootHash, Is.EqualTo(hash));
            Assert.That(rig.Director.Report.LayoutHash, Is.EqualTo(layoutHash));
            Assert.That(second.Length, Is.EqualTo(first.Length));
            for (var i = 0; i < first.Length; i++)
            {
                Assert.That(second[i].pos, Is.EqualTo(first[i].pos));
                Assert.That(second[i].items, Is.EqualTo(first[i].items));
                Assert.That(second[i].ids.Intersect(first[i].ids), Is.Empty, "new ownership ids every deployment");
            }
        }

        [UnityTest]
        public IEnumerator TheLayoutAndObjectives_AreTheSame_WithOrWithoutLoot()
        {
            yield return rig.Generate(12345);
            var withLoot = (rig.Director.Report.LayoutHash, rig.Director.Report.ObjectiveHash);

            rig.AddLoot(null);
            yield return rig.Generate(12345);

            Assert.That((rig.Director.Report.LayoutHash, rig.Director.Report.ObjectiveHash), Is.EqualTo(withLoot));
            Assert.That(rig.Director.Current.LootContainers, Is.Empty);
        }

        [UnityTest]
        public IEnumerator Containers_AreReachable_AndNeverBlockTheGoals()
        {
            foreach (var seed in new[] { 12345, 1, 2, 3, 4 })
            {
                yield return rig.Generate(seed);
                Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), $"seed {seed}");
                var start = rig.Director.Friendlies[0].transform.position - Vector3.up;
                foreach (var container in rig.Director.Current.LootContainers)
                {
                    var path = new NavMeshPath();
                    Assert.That(NavMesh.SamplePosition(container.Position, out var near, 2f, NavMesh.AllAreas), Is.True, $"seed {seed}");
                    Assert.That(NavMesh.CalculatePath(start, near.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete, Is.True, $"seed {seed}");
                    Assert.That(TestWorld.HorizontalDistance(near.position, container.Position), Is.LessThanOrEqualTo(MissionContent.InteractionRange - 0.2f));
                }
            }
        }

        [UnityTest]
        public IEnumerator Search_TakesTime_ThenOpens_AndLaterOpensAreInstant_AndTheContainerStaysAvailable()
        {
            yield return rig.Generate(12345);
            var container = rig.Director.Current.LootContainers[0];
            var unit = rig.Director.Friendlies[0];
            var opened = new System.Collections.Generic.List<CommandableUnit>();
            container.OpenRequested += u => opened.Add(u);
            Assert.That(NavMesh.SamplePosition(container.Position, out var stand, 2f, NavMesh.AllAreas), Is.True);
            unit.GetComponent<NavMeshAgent>().Warp(stand.position);
            yield return null;

            Assert.That(unit.Issue(new InteractCommand(container.Interactable)), Is.True);
            yield return null;
            Assert.That(container.IsSearched, Is.False, "searching takes time");
            yield return TestWorld.WaitUntil(() => container.IsSearched, 6f);

            Assert.That(container.IsSearched, Is.True);
            Assert.That(opened, Is.EqualTo(new[] { unit }), "the searching unit is told");
            Assert.That(container.Interactable.IsAvailable, Is.True, "still interactable");
            Assert.That(container.Interactable.DisplayName, Is.EqualTo(LootContainer.OpenLabel));

            Assert.That(unit.Issue(new InteractCommand(container.Interactable)), Is.True);
            yield return TestWorld.WaitUntil(() => opened.Count == 2, 3f);
            Assert.That(opened.Count, Is.EqualTo(2), "an opened container opens at once");
            Assert.That(unit.CurrentCommand, Is.Null.Or.Not.TypeOf<InteractCommand>());
        }

        [UnityTest]
        public IEnumerator Regeneration_RemovesTheOldContainers_AndTheRegistryHoldsOnlyTheNewOnes()
        {
            yield return rig.Generate(12345);
            var old = rig.Director.Current.LootContainers.ToArray();

            yield return rig.Generate(777);
            yield return null;

            foreach (var container in old)
                Assert.That(container == null, Is.True, "destroyed with the mission");
            var current = rig.Director.Current.LootContainers.Select(c => c.Interactable).ToArray();
            foreach (var item in rig.Interactables.Items)
                if (item != null && item.DisplayName.Contains("container"))
                    Assert.That(current.Contains(item), Is.True);
        }

        [UnityTest]
        public IEnumerator InvalidTable_IsReported_AndTheMissionStillGenerates()
        {
            var bad = ScriptableObject.CreateInstance<LootTable>();   // no entries
            rig.AddLoot(bad);

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("loot table"));
            yield return rig.Generate(12345);

            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready));
            Assert.That(rig.Director.Current.LootContainers, Is.Empty);
            Object.DestroyImmediate(bad);
        }

        [Test]
        public void KnowledgeHelper_SeesContentsOnlyOnceSearched()
        {
            var world = new TestWorld();
            try
            {
                var kit = new InventoryKit(world, 8, "a");
                var container = kit.CreateContainer(Vector3.zero, searched: false, (kit.Medkit, 1));
                Assert.That(LootKnowledge.CanSeeContents(container), Is.False);
                container.InitializeWith(container.Contents, searched: true);
                Assert.That(LootKnowledge.CanSeeContents(container), Is.True);
                Assert.That(LootKnowledge.CanSeeContents(null), Is.False);
                Assert.That(LootKnowledge.CanSeeLocation(null, container), Is.True, "no intelligence service means everything is known");
            }
            finally
            {
                world.Dispose();
            }
        }
    }
}
#endif
```

- [ ] **Step 3: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.LootContainerPlayModeTests"` → `EXIT=1` (types missing).

- [ ] **Step 4: Implement `MissionInteractable` / `UnitInteractor` changes**

`MissionInteractable.cs`: add fields and members:

```csharp
        // After a completion the interactable becomes available again with this duration (a negative value: stay completed).
        float repeatDuration = -1f;
        int completionCount;
        CommandableUnit lastUser;

        /// <summary>How many times the work has finished (a repeatable interactable counts each one).</summary>
        public int CompletionCount => completionCount;
        /// <summary>The unit that finished the work most recently.</summary>
        public CommandableUnit LastUser => lastUser;

        public void SetLabel(string label) => displayName = label;

        /// <summary>
        /// After each completion re-arm with this duration instead of staying completed (0 = instant next time). A loot
        /// container searches once and then opens at once; the terminal never calls this.
        /// </summary>
        public void SetRepeatable(float nextDuration) => repeatDuration = Mathf.Max(0f, nextDuration);
```

In `Advance`, replace the completion block:

```csharp
            if (progress >= duration)
            {
                progress = duration;
                completed = true;
                lastUser = user;
                user = null;
                completionCount++;
                Completed?.Invoke(this);
                if (repeatDuration >= 0f)
                {
                    completed = false;
                    progress = 0f;
                    duration = repeatDuration;
                }
            }
            return true;
```

`UnitInteractor.cs` `Advance`: replace the tail with

```csharp
            var before = target.CompletionCount;
            if (!target.Advance(Unit, deltaTime))
            {
                target.Release(Unit);
                current = null;
                return InteractionStep.Lost;
            }
            if (target.IsCompleted || target.CompletionCount != before)
            {
                current = null;
                return InteractionStep.Completed;
            }
            return InteractionStep.Working;
```

(`var before` must be declared before the `if (!target.Advance…)`; keep the existing `var target = current;` line above it.)

- [ ] **Step 5: Implement `LootContainer` and `LootKnowledge`**

`Assets/_Project/Scripts/Items/LootContainer.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A generated loot container: a solid prop with an ordinary MissionInteractable, so clicking it, the Interact key, the
    /// pad's context Confirm, queueing during pause, cursor snapping and the fog rules all work with no special input
    /// code. The first interaction searches it (SearchSeconds of scaled time); afterwards it stays interactable with a
    /// duration of 0, and every completed interaction raises OpenRequested with the unit that did it, which is how the
    /// loot panel learns what to show. Its contents are an uncapped ItemInventory; nothing can read them as "known" until
    /// IsSearched (LootKnowledge).
    /// </summary>
    [RequireComponent(typeof(MissionInteractable))]
    public sealed class LootContainer : MonoBehaviour
    {
        public const float SearchSeconds = 1.5f;
        public const string SearchLabel = "Search container";
        public const string OpenLabel = "Open container";

        ItemInventory contents = new ItemInventory(0);
        MissionInteractable interactable;
        bool searched;

        public ItemInventory Contents => contents;
        public bool IsSearched => searched;
        public bool IsEmpty => contents.Count == 0;
        public Vector3 Position => transform.position;

        public MissionInteractable Interactable
        {
            get
            {
                if (interactable == null)
                    interactable = GetComponent<MissionInteractable>();
                return interactable;
            }
        }

        /// <summary>Raised after every completed interaction (the search and each later open), with the unit that did it.</summary>
        public event Action<CommandableUnit> OpenRequested;

        /// <summary>Raised when the contents change (a take).</summary>
        public event Action Changed;

        internal void Initialize(IEnumerable<LootItemPlan> plan, ItemCatalogue catalogue, float searchSeconds = SearchSeconds)
        {
            contents = new ItemInventory(0);
            foreach (var item in plan)
            {
                var definition = catalogue != null ? catalogue.Find(item.DefinitionId) : null;
                if (definition == null)
                {
                    Debug.LogError($"{name}: loot refers to an unknown item '{item.DefinitionId}'.", this);
                    continue;
                }
                contents.Add(definition, item.Quantity);
            }
            Arm(searchSeconds);
        }

        /// <summary>Test and tool entry: use these contents as they are, optionally already searched.</summary>
        internal void InitializeWith(ItemInventory ready, bool searched)
        {
            contents = ready;
            Arm(SearchSeconds);
            if (searched)
            {
                this.searched = true;
                Interactable.SetLabel(OpenLabel);
                Interactable.Initialize(MissionContent.InteractionRange, 0f, OpenLabel);
            }
        }

        void Arm(float searchSeconds)
        {
            var item = Interactable;
            item.Initialize(MissionContent.InteractionRange, searchSeconds, SearchLabel);
            item.SetRepeatable(0f);
            item.SetAvailable(true);
            item.Completed -= OnCompleted;
            item.Completed += OnCompleted;
        }

        void OnCompleted(MissionInteractable _)
        {
            if (!searched)
            {
                searched = true;
                Interactable.SetLabel(OpenLabel);
            }
            OpenRequested?.Invoke(Interactable.LastUser);
        }

        internal void NotifyChanged() => Changed?.Invoke();

        void OnDestroy()
        {
            if (interactable != null)
                interactable.Completed -= OnCompleted;
        }
    }
}
```

`Assets/_Project/Scripts/Items/LootKnowledge.cs`:

```csharp
namespace Blackglass
{
    /// <summary>
    /// What the player may know about loot (decision 037's rules extended). A container's location follows the existing
    /// static-object rule through Knowledge.CanInteract (its region is not unknown). Its contents are known only once
    /// the squad searched it: the generator knowing what is inside never makes the HUD, a panel or a log show it.
    /// </summary>
    public static class LootKnowledge
    {
        public static bool CanSeeContents(LootContainer container) => container != null && container.IsSearched;

        public static bool CanSeeLocation(IntelligenceService intelligence, LootContainer container) =>
            container != null && Knowledge.CanInteract(intelligence, container.Interactable);
    }
}
```

- [ ] **Step 6: Mission content, validation, builder and director**

`MissionContent.cs` — add:

```csharp
        public const float LootSize = 0.9f;
        public const float LootHeight = 0.7f;
        static readonly Color LootColour = new Color(0.85f, 0.6f, 0.15f);

        /// <summary>
        /// Adds the planned loot containers under `geometry` (call from the builder's pre-bake hook). Each is a solid box with
        /// a Not Walkable modifier (agents route around it, and the bake includes it), an interactable, the LootContainer and
        /// a collider-free placeholder visual. Not a CoverSurface: it offers no cover.
        /// </summary>
        public static List<LootContainer> AddLootContainers(Transform geometry, LootPlan plan, MissionLayout layout,
            ItemCatalogue catalogue, Material material)
        {
            var containers = new List<LootContainer>();
            for (var i = 0; i < plan.Containers.Count; i++)
            {
                var planned = plan.Containers[i];
                var crate = new GameObject($"LootContainer_{i + 1}");
                crate.transform.SetParent(geometry, false);
                crate.transform.position = layout.TileCenter(planned.Tile) + Vector3.up * (LootHeight * 0.5f);
                var box = crate.AddComponent<BoxCollider>();
                box.size = new Vector3(LootSize, LootHeight, LootSize);
                var modifier = crate.AddComponent<NavMeshModifier>();
                modifier.overrideArea = true;
                modifier.area = NotWalkableArea;
                var container = crate.AddComponent<LootContainer>();
                container.Initialize(planned.Items, catalogue);

                var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                visual.name = "Visual";
                Object.DestroyImmediate(visual.GetComponent<Collider>());
                visual.transform.SetParent(crate.transform, false);
                visual.transform.localScale = new Vector3(LootSize, LootHeight, LootSize);
                var renderer = visual.GetComponent<Renderer>();
                if (material != null)
                    renderer.sharedMaterial = material;
                var block = new MaterialPropertyBlock();
                block.SetColor("_BaseColor", LootColour);
                renderer.SetPropertyBlock(block);
                containers.Add(container);
            }
            return containers;
        }
```

`MissionNavigation.cs` — add after `ValidateSecurity`:

```csharp
        /// <summary>
        /// Every loot container must be usable like the terminal: somewhere to stand within the interaction range and a
        /// complete path from the first friendly spawn. A plan without containers is always valid.
        /// </summary>
        public static bool ValidateLoot(MissionLayout layout, LootPlan plan, out string reason)
        {
            reason = null;
            if (plan == null || plan.Placed == 0)
                return true;
            if (!NavMesh.SamplePosition(layout.TileCenter(layout.FriendlySpawns[0]), out var start, 1f, NavMesh.AllAreas))
            {
                reason = "the first friendly spawn is not on the NavMesh";
                return false;
            }
            var path = new NavMeshPath();
            foreach (var container in plan.Containers)
            {
                var position = layout.TileCenter(container.Tile);
                if (!NavMesh.SamplePosition(position, out var stand, SnapRadius, NavMesh.AllAreas)
                    || Vector3.Distance(new Vector3(stand.position.x, 0f, stand.position.z), position) > MissionContent.InteractionRange - 0.2f)
                {
                    reason = "a loot container has no standing place within reach";
                    return false;
                }
                if (!PathExists(start.position, position, path, out reason, "a loot container"))
                    return false;
            }
            return true;
        }
```

`MissionBuilder.cs` — in `GeneratedMission` add:

```csharp
        public LootPlan Loot { get; internal set; } = LootPlan.Empty;
        public IReadOnlyList<LootContainer> LootContainers { get; internal set; } = Array.Empty<LootContainer>();
```

(add `using System.Collections.Generic;` if missing).

`MissionDirector.cs`:

1. `[SerializeField] LootTable lootTable;` next to the other serialized fields; `LootTable activeLoot;` next to `Coroutine running;`; and

```csharp
        internal void SetLootTable(LootTable table) => lootTable = table;
```

2. `MissionReport`: add `public int LootRequested; public int LootPlaced; public ulong LootHash;`.
3. `Run()`: after `InstanceId`/`BeginMission` lines add

```csharp
            activeLoot = null;
            if (lootTable != null)
            {
                if (lootTable.IsValid(out var lootProblem))
                    activeLoot = lootTable;
                else
                    Debug.LogError($"{name}: the loot table '{lootTable.name}' is invalid ({lootProblem}); this mission has no loot.", this);
            }
```

4. `TryAttempt`: after the `SecurityPlacer` block add

```csharp
            var loot = LootPlan.Empty;
            if (activeLoot != null && systems.inventory != null && systems.inventory.Catalogue != null)
                loot = LootPlacer.Place(layout, plan, security, activeLoot);
            List<LootContainer> containers = null;
```

   replace the `if (request.hackTerminal || security.HasTerminal)` block with

```csharp
            if (request.hackTerminal || security.HasTerminal || loot.Placed > 0)
            {
                addContent = geometry =>
                {
                    if (request.hackTerminal)
                        terminal = MissionContent.AddTerminal(geometry, plan, layout, request, obstacleMaterial, environmentTheme);
                    if (security.HasTerminal)
                        cameraTerminal = MissionContent.AddCameraTerminal(geometry, security, layout, request, obstacleMaterial, environmentTheme);
                    if (loot.Placed > 0)
                        containers = MissionContent.AddLootContainers(geometry, loot, layout, systems.inventory.Catalogue, obstacleMaterial);
                };
            }
```

   after the `ValidateSecurity` block add

```csharp
            if (!MissionNavigation.ValidateLoot(layout, loot, out reason))
            {
                Report.Failures.Add($"attempt {attempt}: loot: {reason}");
                DestroyMission(mission, true);
                return false;
            }
```

   after `mission.CameraTerminal = cameraTerminal;` add `mission.Loot = loot; mission.LootContainers = containers ?? new List<LootContainer>();`; in the `interactables` list building add

```csharp
                if (containers != null)
                    foreach (var container in containers)
                        interactables.Add(container.Interactable);
```

   and after `Fill(Report, layout, navigation, plan);` add

```csharp
            Report.LootRequested = loot.Requested;
            Report.LootPlaced = loot.Placed;
            Report.LootHash = loot.Hash;
            if (loot.Shortfall.Length > 0)
                Debug.LogWarning($"{name}: seed {request.seed}: {loot.Shortfall}.", this);
```

- [ ] **Step 7: Run to verify it passes**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.LootContainerPlayModeTests"` → `EXIT=0`, 8 passed. Then `Tools/run-tests.sh PlayMode "Blackglass.Tests.CommandableUnitInteract"` and `"Blackglass.Tests.MissionObjectives"` and `"Blackglass.Tests.MissionRuntime"` → `EXIT=0` (the terminal still completes once and stays completed).

---

### Task 13: `CollectCommand` and taking loot

**Files:**
- Modify: `Assets/_Project/Scripts/Commands/UnitCommands.cs`, `Assets/_Project/Scripts/Items/UnitItems.cs`, `Assets/_Project/Scripts/Units/CommandableUnit.cs`
- Test: `Assets/_Project/Tests/PlayMode/CollectPlayModeTests.cs`

**Interfaces:**
- Consumes: T2 (`ItemTransfer.Move`), T10 (`UnitItems`), T12 (`LootContainer`, `InventoryKit.CreateContainer`).
- Produces: `CollectCommand(LootContainer container, string instanceId = null)` with `Container`, `InstanceId`, `TakeAll`; `enum CollectFailure { None, Dead, NoLoadout, NoContainer, NotSearched, OutOfRange, NothingThere, BagFull }`; `readonly struct CollectResult { Taken, Left, Failure, Describe() }`; on `UnitItems`: `CheckCollect(LootContainer, bool requireRange)`, `InRange(LootContainer, Vector3)`, `TryCollect(LootContainer, string instanceId)`, `RecordCollect(CollectFailure)`, `LastCollect`, `LastCollectTime`, event `Collected(CollectResult)`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/PlayMode/CollectPlayModeTests.cs`:

```csharp
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CollectPlayModeTests
    {
        TestWorld world;
        TacticalPause pause;
        InventoryKit kit;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            kit = new InventoryKit(world, 2, "a", "b");
            kit.BeginMission();
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        (CommandableUnit unit, UnitItems items) Collector(string id, Vector3 ground)
        {
            var unit = world.CreateFighter(ground);
            return (unit, kit.Equip(unit, id));
        }

        LootContainer Container(Vector3 ground, bool searched = true, params (ItemDefinition, int)[] items) =>
            kit.CreateContainer(ground, searched, items);

        [UnityTest]
        public IEnumerator TakeOneEntry_WalksToTheContainer_AndMovesJustThatEntry()
        {
            var container = Container(new Vector3(8f, 0f, 0f), true, (kit.Rifle, 1), (kit.Vest, 1));
            var (unit, items) = Collector("a", new Vector3(0f, 0f, 0f));
            yield return null;
            var rifleId = container.Contents.Entries.First(e => e.DefinitionId == "rifle").InstanceId;

            Assert.That(unit.Issue(new CollectCommand(container, rifleId)), Is.True);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 15f);

            Assert.That(kit.Bag("a").CountOf("rifle"), Is.EqualTo(1));
            Assert.That(container.Contents.CountOf("rifle"), Is.EqualTo(0));
            Assert.That(container.Contents.CountOf("vest"), Is.EqualTo(1), "only the chosen entry moved");
            Assert.That(items.LastCollect.Failure, Is.EqualTo(CollectFailure.None));
            Assert.That(items.LastCollect.Taken, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator TakeWhatFits_WithAFullBag_LeavesTheRestInTheContainer()
        {
            var container = Container(new Vector3(2f, 0f, 0f), true, (kit.Rifle, 1), (kit.Vest, 1), (kit.Medkit, 2));
            var (unit, items) = Collector("a", Vector3.zero);   // the bag holds 2 entries
            yield return null;

            Assert.That(unit.Issue(new CollectCommand(container)), Is.True);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);

            Assert.That(kit.Bag("a").Count, Is.EqualTo(2));
            Assert.That(container.Contents.Count, Is.EqualTo(1), "one entry did not fit and stayed");
            Assert.That(items.LastCollect.Failure, Is.EqualTo(CollectFailure.BagFull));
            Assert.That(items.LastCollect.Left, Is.GreaterThan(0));
            Assert.That(items.LastCollect.Taken, Is.GreaterThan(0));
            var total = kit.Bag("a").Entries.Sum(e => e.Quantity) + container.Contents.Entries.Sum(e => e.Quantity);
            Assert.That(total, Is.EqualTo(4), "nothing was destroyed or duplicated");
        }

        [UnityTest]
        public IEnumerator ABagWithNoRoomForAnItem_TakesNothing_AndSaysSo()
        {
            kit.Bag("a").Add(kit.Rifle, 2);
            var container = Container(new Vector3(2f, 0f, 0f), true, (kit.Vest, 1));
            var (unit, items) = Collector("a", Vector3.zero);
            yield return null;

            unit.Issue(new CollectCommand(container));
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);

            Assert.That(container.Contents.CountOf("vest"), Is.EqualTo(1));
            Assert.That(items.LastCollect.Taken, Is.EqualTo(0));
            Assert.That(items.LastCollect.Failure, Is.EqualTo(CollectFailure.BagFull));
        }

        [UnityTest]
        public IEnumerator TwoOperativesTakingTheSameEntry_ExactlyOneGetsIt()
        {
            var container = Container(new Vector3(2f, 0f, 0f), true, (kit.Rifle, 1));
            var (first, _) = Collector("a", new Vector3(0f, 0f, 1f));
            var (second, _) = Collector("b", new Vector3(0f, 0f, -1f));
            yield return null;
            var id = container.Contents.Entries[0].InstanceId;

            first.Issue(new CollectCommand(container, id));
            second.Issue(new CollectCommand(container, id));
            yield return TestWorld.WaitUntil(() => first.CurrentCommand == null && second.CurrentCommand == null, 10f);

            Assert.That(kit.Bag("a").CountOf("rifle") + kit.Bag("b").CountOf("rifle"), Is.EqualTo(1));
            Assert.That(container.Contents.Count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator TheContainerDisappearingMidWalk_EndsTheOrder_WithoutErrors()
        {
            var container = Container(new Vector3(12f, 0f, 0f), true, (kit.Rifle, 1));
            var (unit, items) = Collector("a", Vector3.zero);
            yield return null;
            unit.Issue(new CollectCommand(container));
            yield return null;

            Object.Destroy(container.gameObject);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 5f);

            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(items.LastCollect.Failure, Is.EqualTo(CollectFailure.NoContainer));
            Assert.That(kit.Bag("a").Count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator TheCollectorDyingMidWalk_TakesNothing_AndLeavesTheContainerAlone()
        {
            var container = Container(new Vector3(12f, 0f, 0f), true, (kit.Rifle, 1));
            var (unit, _) = Collector("a", Vector3.zero);
            yield return null;
            unit.Issue(new CollectCommand(container));
            yield return null;

            unit.GetComponent<Health>().TakeDamage(1000);
            yield return null;

            Assert.That(container.Contents.CountOf("rifle"), Is.EqualTo(1));
            Assert.That(kit.Bag("a").Count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator AnUnsearchedContainer_CannotBeTakenFrom()
        {
            var container = Container(new Vector3(2f, 0f, 0f), false, (kit.Rifle, 1));
            var (unit, items) = Collector("a", Vector3.zero);
            yield return null;

            Assert.That(unit.Issue(new CollectCommand(container)), Is.False);
            Assert.That(items.CheckCollect(container, false), Is.EqualTo(CollectFailure.NotSearched));
            Assert.That(container.Contents.CountOf("rifle"), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Paused_NothingMoves_UntilResume()
        {
            var container = Container(new Vector3(2f, 0f, 0f), true, (kit.Rifle, 1));
            var (unit, _) = Collector("a", Vector3.zero);
            yield return null;
            pause.Pause();

            Assert.That(unit.Issue(new CollectCommand(container)), Is.True);
            for (var i = 0; i < 5; i++)
                yield return null;
            Assert.That(container.Contents.CountOf("rifle"), Is.EqualTo(1), "no world loot transfers while paused");
            Assert.That(kit.Bag("a").Count, Is.EqualTo(0));

            pause.Resume();
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);
            Assert.That(kit.Bag("a").CountOf("rifle"), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ReplacedOrStopped_BeforeItRuns_TakesNothing()
        {
            var container = Container(new Vector3(12f, 0f, 0f), true, (kit.Rifle, 1));
            var (unit, _) = Collector("a", Vector3.zero);
            yield return null;
            unit.Issue(new CollectCommand(container));
            yield return null;

            unit.Issue(new StopCommand());
            yield return null;
            yield return null;

            Assert.That(container.Contents.CountOf("rifle"), Is.EqualTo(1));
            Assert.That(unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator Sequence_Collect_ThenUse_ThenMove_RunsInOrder_AndRevalidatesEach()
        {
            var container = Container(new Vector3(3f, 0f, 0f), true, (kit.Medkit, 1));
            var (unit, items) = Collector("a", Vector3.zero);
            var health = unit.GetComponent<Health>();
            health.TakeDamage(70);
            yield return null;
            // The use order names an entry the unit does not own yet, so take first, then queue the use.
            Assert.That(unit.Issue(new CollectCommand(container)), Is.True);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);
            var owned = kit.Bag("a").Entries[0].InstanceId;

            Assert.That(unit.Issue(new UseItemCommand(owned)), Is.True);
            Assert.That(unit.Issue(new MoveCommand(new Vector3(-4f, 0f, 0f)), IssueMode.Append), Is.True);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);

            Assert.That(health.Current, Is.EqualTo(70));
            Assert.That(kit.Bag("a").CountOf("medkit"), Is.EqualTo(0));
            Assert.That(unit.transform.position.x, Is.LessThan(-3f));
            Assert.That(items.UsedCount, Is.EqualTo(1));
        }

        [Test]
        public void Command_NeedsAContainer()
        {
            Assert.Throws<System.ArgumentNullException>(() => new CollectCommand(null));
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.CollectPlayModeTests"` → `EXIT=1` (`CollectCommand`, `CollectFailure` missing).

- [ ] **Step 3: Implement**

`UnitCommands.cs` — add after `UseItemCommand`:

```csharp
    /// <summary>
    /// Walk to a searched loot container and move items from it into the unit's own bag: one entry, or everything that fits
    /// (no instance id). Plain data like every order: the unit checks it when it is issued and again when it runs
    /// (UnitItems), so another unit taking the entry first, a full bag or a vanished container end the order without losing
    /// anything. Leftovers stay in the container.
    /// </summary>
    public sealed class CollectCommand : UnitCommand
    {
        public CollectCommand(LootContainer container, string instanceId = null)
        {
            if (container == null)
                throw new ArgumentNullException(nameof(container));
            Container = container;
            InstanceId = instanceId;
        }

        public LootContainer Container { get; }

        /// <summary>The container entry to take, or null/empty for everything that fits.</summary>
        public string InstanceId { get; }

        public bool TakeAll => string.IsNullOrEmpty(InstanceId);
    }
```

`UnitItems.cs` — add the enums/struct at the top of the namespace and the members in the class:

```csharp
    public enum CollectFailure
    {
        None,
        Dead,
        NoLoadout,
        /// <summary>The container is gone (destroyed with its mission).</summary>
        NoContainer,
        NotSearched,
        OutOfRange,
        /// <summary>The entry (or the whole container) is already empty.</summary>
        NothingThere,
        /// <summary>Some or all items did not fit in the bag and stayed in the container.</summary>
        BagFull,
    }

    /// <summary>What a take did: units moved into the bag, units still in the container among those asked for, and why not all moved.</summary>
    public readonly struct CollectResult
    {
        public CollectResult(int taken, int left, CollectFailure failure)
        {
            Taken = taken;
            Left = left;
            Failure = failure;
        }

        public int Taken { get; }
        public int Left { get; }
        public CollectFailure Failure { get; }

        public string Describe()
        {
            switch (Failure)
            {
                case CollectFailure.None: return $"Took {Taken}.";
                case CollectFailure.BagFull: return Taken > 0 ? $"Bag full: took {Taken}, {Left} left in the container." : "Bag full: nothing taken.";
                case CollectFailure.NothingThere: return "Nothing left to take.";
                case CollectFailure.NotSearched: return "Search the container first.";
                case CollectFailure.OutOfRange: return "Too far from the container.";
                case CollectFailure.NoContainer: return "The container is gone.";
                case CollectFailure.Dead: return "Down: cannot take items.";
                default: return "Cannot take items now.";
            }
        }
    }
```

Class members:

```csharp
        public CollectResult LastCollect { get; private set; }
        public float LastCollectTime { get; private set; }
        public event Action<CollectResult> Collected;

        /// <summary>Whether this unit may take from the container now; `requireRange` false is for an order that will walk first.</summary>
        public CollectFailure CheckCollect(LootContainer container, bool requireRange)
        {
            if (container == null)
                return CollectFailure.NoContainer;
            var own = OwnHealth;
            if (own == null || !own.IsAlive)
                return CollectFailure.Dead;
            if (Loadout == null)
                return CollectFailure.NoLoadout;
            if (!container.IsSearched)
                return CollectFailure.NotSearched;
            if (container.IsEmpty)
                return CollectFailure.NothingThere;
            if (requireRange && !InRange(container, transform.position))
                return CollectFailure.OutOfRange;
            return CollectFailure.None;
        }

        public bool InRange(LootContainer container, Vector3 from) =>
            container != null && container.Interactable != null && CoverRules.FlatDistance(from, container.Position) <= container.Interactable.Range;

        /// <summary>
        /// Moves one entry, or every entry that fits, from the container into this unit's bag. Each move re-reads the source
        /// (ItemTransfer), so a second collector cannot duplicate an entry. Never throws for a vanished container, a dead
        /// unit or a full bag: it returns the reason and changes nothing it cannot finish.
        /// </summary>
        public CollectResult TryCollect(LootContainer container, string instanceId)
        {
            var failure = CheckCollect(container, requireRange: true);
            if (failure != CollectFailure.None)
                return RecordCollect(new CollectResult(0, 0, failure));

            var bag = Loadout.Bag;
            var wanted = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(instanceId))
            {
                foreach (var entry in container.Contents.Entries)
                    wanted.Add(entry.InstanceId);
            }
            else
            {
                wanted.Add(instanceId);
            }

            var taken = 0;
            var missing = 0;
            foreach (var id in wanted)
            {
                var source = container.Contents.Find(id);
                if (source == null)
                {
                    missing++;
                    continue;
                }
                taken += ItemTransfer.Move(container.Contents, bag, id, source.Quantity, inventory.Catalogue).Moved;
            }
            // What is still there is re-read from the container, so the figure is what really remains.
            var left = 0;
            foreach (var id in wanted)
            {
                var still = container.Contents.Find(id);
                if (still != null)
                    left += still.Quantity;
            }
            var outcome = left > 0 ? CollectFailure.BagFull
                : taken == 0 && missing > 0 ? CollectFailure.NothingThere
                : CollectFailure.None;
            if (taken > 0)
            {
                container.NotifyChanged();
                inventory.Core.NotifyChanged();
            }
            return RecordCollect(new CollectResult(taken, left, outcome));
        }

        internal void RecordCollect(CollectFailure failure) => RecordCollect(new CollectResult(0, 0, failure));

        CollectResult RecordCollect(CollectResult result)
        {
            LastCollect = result;
            LastCollectTime = Time.unscaledTime;
            Collected?.Invoke(result);
            return result;
        }
```

`CommandableUnit.cs`:

1. In `Issue`'s accepted-type switch add `case CollectCommand _:` to the list.
2. After `startingUse` in `Issue` add:

```csharp
            if (command is CollectCommand startingCollect && !CanOrderCollect(startingCollect))
                return false;
```

3. In `CanStart`:

```csharp
                case CollectCommand collect:
                    // Queued: only what cannot change by the time it runs. The walk and the bag are checked then.
                    return Items != null && Items.CheckCollect(collect.Container, requireRange: false) == CollectFailure.None;
```

4. In `TryStartCommand` (next to the interact case):

```csharp
                case CollectCommand collect:
                {
                    var holder = Items;
                    var container = collect.Container;
                    if (holder == null || holder.CheckCollect(container, requireRange: false) != CollectFailure.None)
                        return false;
                    var inRange = holder.InRange(container, transform.position);
                    if (!inRange && !Mover.CanMoveTo(container.Position))
                        return false;
                    if (inRange)
                        Mover.Stop();
                    else if (!WalkTo(container.Position))
                        return false;
                    walkProgressTime = Time.time;
                    Cover.ReleaseReservation();
                    ResetAttack();
                    return true;
                }
```

5. In `Update`'s `switch (queue.Current)` add `case CollectCommand collect: UpdateCollect(collect); break;`.
6. Helpers (near `CanOrderInteract` / `UpdateInteract`):

```csharp
        // A collect order that would start now: a searched container the unit can take from, in reach or reachable.
        bool CanOrderCollect(CollectCommand order)
        {
            var holder = Items;
            if (holder == null)
                return false;
            var failure = holder.CheckCollect(order.Container, requireRange: false);
            if (failure != CollectFailure.None)
            {
                holder.RecordCollect(failure);
                return false;
            }
            return holder.InRange(order.Container, transform.position) || Mover.CanReach(order.Container.Position);
        }

        // Re-validates every running frame. In reach the unit stops, faces the container and takes (the transfer re-reads the
        // source); out of reach it walks; a vanished container, a full stop or a walk that makes no progress ends the order
        // so the queue moves on. Only simulation time reaches this, so a pause freezes it.
        void UpdateCollect(CollectCommand order)
        {
            var holder = Items;
            if (holder == null)
            {
                Finish();
                return;
            }
            var failure = holder.CheckCollect(order.Container, requireRange: false);
            if (failure != CollectFailure.None)
            {
                holder.RecordCollect(failure);
                Finish();
                return;
            }
            if (holder.InRange(order.Container, transform.position))
            {
                Mover.Stop();
                FaceTowards(order.Container.Position);
                holder.TryCollect(order.Container, order.InstanceId);
                Finish();
                return;
            }
            if (Mover.HasArrived || WalkStalled())
            {
                holder.RecordCollect(CollectFailure.OutOfRange);
                Finish();
            }
        }
```

(Unity-null: `order.Container` destroyed → `CheckCollect` sees `container == null` true for a destroyed Unity object → `NoContainer`.)

- [ ] **Step 4: Run to verify it passes**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.CollectPlayModeTests"` → `EXIT=0`, 11 passed. Then `Tools/run-tests.sh PlayMode "Blackglass.Tests.UseItemPlayModeTests"` and `"Blackglass.Tests.CommandableUnit"` → `EXIT=0`.

---

### Task 14: Loot and the fog of war

**Files:**
- Test: `Assets/_Project/Tests/PlayMode/LootFogPlayModeTests.cs`
- Modify (only if a test fails): the consumer that leaks (named by the failing assertion)

**Interfaces:**
- Consumes: `LootKnowledge` (T12), `IntelligenceService.CanInteract`, `Knowledge.CanInteract`, `PlayerCommandInput.NearbyInteractable`, `InteractableRegistry.NearestAvailable(point, radius, accept)`, `TacticalCursor` snap, `MissionRig.AddIntelligence`.
- Produces: tests that pin the information boundary for containers.

- [ ] **Step 1: Write the tests**

`Assets/_Project/Tests/PlayMode/LootFogPlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class LootFogPlayModeTests
    {
        const string Items = "Assets/_Project/Data/Items/";
        MissionRig rig;
        IntelligenceService intelligence;

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        [SetUp]
        public void SetUp()
        {
            rig = new MissionRig();
            rig.AddInventory(Load<ItemCatalogue>(Items + "ItemCatalogue.asset"));
            rig.AddLoot(Load<LootTable>(Items + "LootTable.asset"));
            intelligence = rig.AddIntelligence(IntelligenceSettings.Blind());
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
            rig = null;
        }

        LootContainer FarContainer()
        {
            var squad = rig.Director.Friendlies[0].transform.position;
            return rig.Director.Current.LootContainers
                .OrderByDescending(c => TestWorld.HorizontalDistance(c.Position, squad)).First();
        }

        [UnityTest]
        public IEnumerator AnUnknownContainer_IsNotInteractable_NotSnappable_AndNotNearestAvailable()
        {
            yield return rig.Generate(12345);
            yield return null;
            var container = FarContainer();

            Assert.That(intelligence.CanInteract(container.Interactable), Is.False, "its region is unknown");
            Assert.That(LootKnowledge.CanSeeLocation(intelligence, container), Is.False);
            var nearest = rig.Interactables.NearestAvailable(container.Position, 2f, item => Knowledge.CanInteract(intelligence, item));
            Assert.That(nearest == container.Interactable, Is.False, "the cursor snap and proximity filters skip it");
            Assert.That(LootKnowledge.CanSeeContents(container), Is.False);
        }

        [UnityTest]
        public IEnumerator ADiscoveredContainer_IsKnownByLocation_ButItsContentsStayHiddenUntilSearched()
        {
            yield return rig.Generate(12345);
            var container = FarContainer();

            intelligence.Model.RevealArea(container.Position, 3f);   // the region becomes known
            yield return null;

            Assert.That(LootKnowledge.CanSeeLocation(intelligence, container), Is.True);
            Assert.That(LootKnowledge.CanSeeContents(container), Is.False, "knowing where it is does not reveal what is in it");
            Assert.That(container.Contents.Count, Is.GreaterThan(0), "the generator did create contents");
        }

        [UnityTest]
        public IEnumerator ReconScan_DiscoversTheLocation_NeverTheContents()
        {
            yield return rig.Generate(12345);
            var container = FarContainer();

            intelligence.Scan(container.Position, 6f, 5f);
            yield return null;

            Assert.That(LootKnowledge.CanSeeLocation(intelligence, container), Is.True);
            Assert.That(LootKnowledge.CanSeeContents(container), Is.False);
        }

        [UnityTest]
        public IEnumerator SearchingIt_MakesTheContentsKnown()
        {
            yield return rig.Generate(12345);
            var container = FarContainer();
            var unit = rig.Director.Friendlies[0];
            intelligence.Model.RevealArea(container.Position, 3f);
            Assert.That(NavMesh.SamplePosition(container.Position, out var stand, 2f, NavMesh.AllAreas), Is.True);
            unit.GetComponent<NavMeshAgent>().Warp(stand.position);
            yield return null;

            unit.Issue(new InteractCommand(container.Interactable));
            yield return TestWorld.WaitUntil(() => container.IsSearched, 6f);

            Assert.That(LootKnowledge.CanSeeContents(container), Is.True);
        }
    }
}
#endif
```

`intelligence.Model.RevealArea(Vector3, float)` is the reveal API named in decision 037 (`RevealRegion`, `RevealArea`…). Check its real signature in `MissionIntelligence.cs` when writing this test and adapt the call (the assertion is what matters); if the area reveal needs a region index, use `RevealRegion(layoutRegionIndexAt(container.Position))` via `intelligence.Map`.

- [ ] **Step 2: Run**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.LootFogPlayModeTests"`. Expected: `EXIT=0`, 4 passed — containers are interactables, so the Phase 11 rules already cover location. If the first test fails because a consumer of `InteractableRegistry.Items` ignores `Knowledge.CanInteract` (`PlayerCommandInput.TryFindReachableTerminal`, the cursor snap, the HUD prompt), fix that consumer to apply the same filter the terminal path uses and name the file in the report.

- [ ] **Step 3: Milestone-C checkpoint**

Run both full suites. Expected: all previously-green tests still green plus all new ones. Report both totals and `git status --short`.

---

## Milestone D — Panel, input, deploy flow, regression

### Task 15: Input actions and the input modal gate

**Files:**
- Modify: `Assets/_Project/Input/BlackglassControls.inputactions` (via the patch script below), `Assets/_Project/Tests/EditMode/InputAssetTests.cs`
- Create: `Assets/_Project/Scripts/Controls/InputModalGate.cs`
- Test: `Assets/_Project/Tests/EditMode/InventoryInputAssetTests.cs`, `Assets/_Project/Tests/EditMode/InputModalGateTests.cs`, `Assets/_Project/Tests/PlayMode/InputModalGatePlayModeTests.cs`

**Interfaces:**
- Consumes: the asset layout of decision 024 (binding groups `KeyboardMouse`, `Xbox`, `PlayStation`, `Nintendo`, `Gamepad`).
- Produces: actions `Inventory/Toggle` (keyboard `I`; every pad family: the pad's Select/View/Create button), `UI/PreviousTab`, `UI/NextTab` (keyboard `Q`/`E`; every pad family: left/right shoulder), keyboard bindings for `UI/Navigate` (arrow keys), `UI/Submit` (Enter), `UI/Cancel` (Escape); `InputModalGate(InputActionAsset asset, params string[] modalMaps)` with `IsOpen`, `Open()`, `Enforce()`, `Close()`.

Keyboard keys in use (checked): `1-4 a d e esc f f1 f5-f12 m q r s space tab v w x`, shifts. `I` is free. `Q`/`E`/`Esc` are used by gameplay but only while the modal is closed: the gate disables every gameplay action while the modal is open, so the UI bindings may reuse them.

- [ ] **Step 1: Write the failing asset tests**

`Assets/_Project/Tests/EditMode/InventoryInputAssetTests.cs`:

```csharp
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    public class InventoryInputAssetTests
    {
        const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";
        static readonly string[] PadGroups = { "Xbox", "PlayStation", "Nintendo", "Gamepad" };

        static InputActionAsset Load() => AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);

        static string[] GroupsOf(InputBinding b) =>
            b.groups.Split(new[] { InputBinding.Separator }, System.StringSplitOptions.RemoveEmptyEntries);

        [Test]
        public void InventoryToggle_IsOnI_AndOnTheSelectButtonOfEveryPadFamily()
        {
            var toggle = Load().FindAction("Inventory/Toggle", throwIfNotFound: true);

            var keyboard = toggle.bindings.Where(b => GroupsOf(b).Contains("KeyboardMouse")).Select(b => b.path).ToArray();
            Assert.That(keyboard, Is.EqualTo(new[] { "<Keyboard>/i" }));
            foreach (var group in PadGroups)
            {
                var pad = toggle.bindings.Where(b => GroupsOf(b).Contains(group)).Select(b => b.path).ToArray();
                Assert.That(pad, Is.EqualTo(new[] { "<Gamepad>/select" }), group);
            }
        }

        [Test]
        public void TheSelectButton_IsBoundOnlyToTheInventoryToggle()
        {
            var users = Load().bindings.Where(b => b.path == "<Gamepad>/select").Select(b => b.action).Distinct().ToArray();
            Assert.That(users, Is.EqualTo(new[] { "Toggle" }));
        }

        [Test]
        public void TheKeyI_IsUsedByNothingElse()
        {
            var users = Load().bindings.Where(b => b.path == "<Keyboard>/i").ToArray();
            Assert.That(users, Has.Length.EqualTo(1));
        }

        [Test]
        public void TabActions_AndKeyboardNavigation_ExistInTheUiMap()
        {
            var asset = Load();
            foreach (var name in new[] { "UI/PreviousTab", "UI/NextTab" })
            {
                var action = asset.FindAction(name, throwIfNotFound: true);
                foreach (var group in PadGroups.Concat(new[] { "KeyboardMouse" }))
                    Assert.That(action.bindings.Any(b => GroupsOf(b).Contains(group)), Is.True, $"{name} has no {group} binding");
            }
            foreach (var name in new[] { "UI/Navigate", "UI/Submit", "UI/Cancel" })
                Assert.That(asset.FindAction(name, throwIfNotFound: true).bindings.Any(b => GroupsOf(b).Contains("KeyboardMouse")), Is.True, name);
        }

        [Test]
        public void EveryNewBinding_HasAGroup_AndAUniqueId()
        {
            var asset = Load();
            foreach (var path in new[] { "Inventory/Toggle", "UI/PreviousTab", "UI/NextTab" })
                foreach (var binding in asset.FindAction(path).bindings)
                    Assert.That(binding.groups, Is.Not.Empty, $"{path} {binding.path}");
            var ids = asset.bindings.Select(b => b.id).ToList();
            Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count));
        }
    }
}
```

Also edit `InputAssetTests.cs`: add `"Inventory/Toggle", "UI/PreviousTab", "UI/NextTab"` to the `PadActions` array, and replace the test `SelectButton_IsReserved_AndUnbound` with:

```csharp
        [Test]
        public void SelectButton_IsOnlyTheInventoryToggle()
        {
            var users = Load().bindings.Where(b => b.path == "<Gamepad>/select").Select(b => b.action).Distinct();
            Assert.That(users, Is.EqualTo(new[] { "Toggle" }), "owner ruling, Phase 12: the reserved button opens the inventory (decision 043)");
        }
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.InventoryInputAssetTests"` → `EXIT=2` (the actions do not exist).

- [ ] **Step 3: Patch the asset**

Create `C:\...\scratchpad\patch_controls.py` (scratchpad, not the project) and run it once with `python`:

```python
import re, sys, uuid

path = r"F:\Programs\ProjectBlackglass\Assets\_Project\Input\BlackglassControls.inputactions"
text = open(path, encoding="utf-8").read()
if '"name": "Inventory"' in text:
    sys.exit("already patched")

def gid():
    return str(uuid.uuid4())

PADS = ["Xbox", "PlayStation", "Nintendo", "Gamepad"]

def action(name, kind="Button", control="Button", check="false"):
    return ('{ "name": "%s", "type": "%s", "id": "%s", "expectedControlType": "%s", "processors": "", "interactions": "", '
            '"initialStateCheck": %s }' % (name, kind, gid(), control, check))

def binding(path, group, act, name="", composite=False, part=False):
    return ('{ "name": "%s", "id": "%s", "path": "%s", "interactions": "", "processors": "", "groups": "%s", "action": "%s", '
            '"isComposite": %s, "isPartOfComposite": %s }' % (name, gid(), path, group, act, str(composite).lower(), str(part).lower()))

ind = "                "

# 1. New UI actions after the existing Cancel action line.
cancel = re.search(r'\{ "name": "Cancel", "type": "Button", "id": "d5dd9773-0e88-4a1c-9cad-3d9eec76ad28"[^\n]*\}', text)
text = text.replace(cancel.group(0), cancel.group(0) + ",\n" + ind + action("PreviousTab") + ",\n" + ind + action("NextTab"))

# 2. New UI bindings, appended after the last UI binding (the Gamepad Cancel one).
last = re.search(r'\{[^\n]*"id": "9163f38b-8e0b-43f7-ab67-bf04d32230f3"[^\n]*\}', text)
extra = []
extra.append(binding("2DVector", "KeyboardMouse", "Navigate", "arrows", composite=True))
for part, key in (("up", "upArrow"), ("down", "downArrow"), ("left", "leftArrow"), ("right", "rightArrow")):
    extra.append(binding("<Keyboard>/" + key, "KeyboardMouse", "Navigate", part, part=True))
extra.append(binding("<Keyboard>/enter", "KeyboardMouse", "Submit"))
extra.append(binding("<Keyboard>/escape", "KeyboardMouse", "Cancel"))
extra.append(binding("<Keyboard>/q", "KeyboardMouse", "PreviousTab"))
extra.append(binding("<Keyboard>/e", "KeyboardMouse", "NextTab"))
for group in PADS:
    extra.append(binding("<Gamepad>/leftShoulder", group, "PreviousTab"))
    extra.append(binding("<Gamepad>/rightShoulder", group, "NextTab"))
text = text.replace(last.group(0), last.group(0) + ",\n" + ",\n".join(ind + e for e in extra))

# 3. The new Inventory map, before the Developer map.
inventory_map = (
    '        {\n'
    '            "name": "Inventory",\n'
    '            "id": "%s",\n'
    '            "actions": [\n'
    '                %s\n'
    '            ],\n'
    '            "bindings": [\n%s\n            ]\n'
    '        },\n'
) % (gid(), action("Toggle"),
     ",\n".join([ind + binding("<Keyboard>/i", "KeyboardMouse", "Toggle")] +
                [ind + binding("<Gamepad>/select", g, "Toggle") for g in PADS]))
marker = '        {\n            "name": "Developer",'
assert marker in text
text = text.replace(marker, inventory_map + marker)
open(path, "w", encoding="utf-8", newline="\n").write(text)
print("patched")
```

Run `python <script>`; then `git diff --stat Assets/_Project/Input/BlackglassControls.inputactions` must show additions only (no reformatting of existing lines). The file is LF (`.gitattributes` forces `eol=lf`), which the script writes..

- [ ] **Step 4: Run to verify the asset tests pass**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.InventoryInputAssetTests"` and `"Blackglass.Tests.InputAssetTests"` and `"Blackglass.Tests.HudInputAssetTests"` and `"Blackglass.Tests.AbilityInputAssetTests"` → `EXIT=0`. If `InputAssetTests.ExistingKeyboardMouseBindings_AreUnchanged` or the "gameplay code names no device" scan fails, the patch touched something it should not: fix the patch, not the test.

- [ ] **Step 5: Write the gate tests**

`Assets/_Project/Tests/EditMode/InputModalGateTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    public class InputModalGateTests
    {
        InputActionAsset asset;
        InputAction fire, move, navigate, toggle;

        [SetUp]
        public void SetUp()
        {
            asset = ScriptableObject.CreateInstance<InputActionAsset>();
            var game = asset.AddActionMap("Game");
            fire = game.AddAction("Fire", InputActionType.Button, "<Keyboard>/space");
            move = game.AddAction("Move", InputActionType.Value, "<Keyboard>/w");
            var ui = asset.AddActionMap("UI");
            navigate = ui.AddAction("Navigate", InputActionType.Value, "<Keyboard>/upArrow");
            var inventory = asset.AddActionMap("Inventory");
            toggle = inventory.AddAction("Toggle", InputActionType.Button, "<Keyboard>/i");
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(asset);

        [Test]
        public void Open_DisablesGameplayActions_AndEnablesTheModalMaps()
        {
            fire.Enable();
            move.Enable();
            var gate = new InputModalGate(asset, "UI", "Inventory");

            gate.Open();

            Assert.That(gate.IsOpen, Is.True);
            Assert.That(fire.enabled, Is.False);
            Assert.That(move.enabled, Is.False);
            Assert.That(navigate.enabled, Is.True);
            Assert.That(toggle.enabled, Is.True);
        }

        [Test]
        public void Close_RestoresEachActionsPreviousState_NotJustEnabled()
        {
            fire.Enable();                 // was on
            toggle.Enable();               // was on
            var gate = new InputModalGate(asset, "UI", "Inventory");
            gate.Open();

            gate.Close();

            Assert.That(gate.IsOpen, Is.False);
            Assert.That(fire.enabled, Is.True);
            Assert.That(move.enabled, Is.False, "it was off before and is off again");
            Assert.That(navigate.enabled, Is.False, "the UI map is off outside the modal");
            Assert.That(toggle.enabled, Is.True);
        }

        [Test]
        public void Enforce_DisablesAnActionAnotherComponentSwitchedOnWhileOpen_AndRestoresItOnClose()
        {
            var gate = new InputModalGate(asset, "UI", "Inventory");
            gate.Open();

            move.Enable();                 // a component enabled itself during the modal
            gate.Enforce();
            Assert.That(move.enabled, Is.False, "no gameplay input leaks through");

            gate.Close();
            Assert.That(move.enabled, Is.True, "it wanted to be on");
        }

        [Test]
        public void Enforce_ReEnablesAModalActionSomeoneSwitchedOff()
        {
            var gate = new InputModalGate(asset, "UI", "Inventory");
            gate.Open();

            navigate.Disable();
            gate.Enforce();

            Assert.That(navigate.enabled, Is.True);
        }

        [Test]
        public void OpenTwice_AndCloseWithoutOpen_AreHarmless()
        {
            fire.Enable();
            var gate = new InputModalGate(asset, "UI", "Inventory");

            gate.Close();
            Assert.That(fire.enabled, Is.True);

            gate.Open();
            gate.Open();
            gate.Close();
            Assert.That(fire.enabled, Is.True, "the second Open did not record the disabled state as the original");
        }
    }
}
```

- [ ] **Step 6: Run to verify it fails, then implement**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.InputModalGateTests"` → `EXIT=1`.

`Assets/_Project/Scripts/Controls/InputModalGate.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// What "a modal panel owns the input" means: while open, every action outside the named modal maps is disabled (so a
    /// weapon, a movement key or a command cannot fire from input meant for the panel) and the modal maps' actions are
    /// enabled. Close puts every action back exactly as it was, whether it was on or off. Enforce, called every frame while
    /// open, switches off any gameplay action a component re-enabled meanwhile and remembers that it wanted to be on, so
    /// the restore is still right. Time scale and tactical pause are never touched.
    /// </summary>
    public sealed class InputModalGate
    {
        readonly InputActionAsset asset;
        readonly HashSet<string> modalMaps;
        readonly Dictionary<InputAction, bool> wasEnabled = new Dictionary<InputAction, bool>();

        public InputModalGate(InputActionAsset asset, params string[] modalMaps)
        {
            this.asset = asset;
            this.modalMaps = new HashSet<string>(modalMaps);
        }

        public bool IsOpen { get; private set; }

        public void Open()
        {
            if (IsOpen || asset == null)
                return;
            wasEnabled.Clear();
            foreach (var map in asset.actionMaps)
            {
                var modal = modalMaps.Contains(map.name);
                foreach (var action in map.actions)
                {
                    wasEnabled[action] = action.enabled;
                    if (modal)
                        action.Enable();
                    else
                        action.Disable();
                }
            }
            IsOpen = true;
        }

        public void Enforce()
        {
            if (!IsOpen || asset == null)
                return;
            foreach (var map in asset.actionMaps)
            {
                var modal = modalMaps.Contains(map.name);
                foreach (var action in map.actions)
                {
                    if (modal && !action.enabled)
                        action.Enable();
                    else if (!modal && action.enabled)
                    {
                        wasEnabled[action] = true;
                        action.Disable();
                    }
                }
            }
        }

        public void Close()
        {
            if (!IsOpen)
                return;
            foreach (var pair in wasEnabled)
            {
                if (pair.Value)
                    pair.Key.Enable();
                else
                    pair.Key.Disable();
            }
            wasEnabled.Clear();
            IsOpen = false;
        }
    }
}
```

Run `Tools/run-tests.sh EditMode "Blackglass.Tests.InputModalGateTests"` → `EXIT=0`, 5 passed.

- [ ] **Step 7: PlayMode: no leak, no stuck input**

`Assets/_Project/Tests/PlayMode/InputModalGatePlayModeTests.cs` (follow `DirectControlInputTests`' fixture: `InputTestFixture`, `TestControls.Load()`, `TestControls.Ref`):

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class InputModalGatePlayModeTests : InputTestFixture
    {
        Keyboard keyboard;
        TestWorld world;
        InputActionAsset actions;
        CommandableUnit unit;
        ActiveCharacter active;
        DirectControlInput input;
        InputModalGate gate;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            world = new TestWorld();
            world.CreateEnvironment();
            unit = world.CreateFriendly(Vector3.zero).Unit;
            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            var viewCamera = cameraObject.AddComponent<Camera>();
            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            var pause = systems.AddComponent<TacticalPause>();
            var selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(unit.GetComponent<SelectableUnit>());
            active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(unit, pause, selection);
            actions = TestControls.Load();
            input = systems.AddComponent<DirectControlInput>();
            input.Initialize(active, viewCamera, TestControls.Ref(actions, "Character/Move"),
                TestControls.Ref(actions, "Character/ToggleCharacterControl"), selection);
            systems.SetActive(true);
            active.SetTakeover(true);
            gate = new InputModalGate(actions, "UI", "Inventory");
        }

        public override void TearDown()
        {
            gate.Close();
            world.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        [UnityTest]
        public IEnumerator AHeldMoveKey_StopsSteering_WhileTheModalIsOpen_AndSteeringResumesOnlyIfStillHeld()
        {
            Press(keyboard.dKey);
            yield return null;
            yield return null;
            Assert.That(unit.MoveIntent, Is.Not.EqualTo(Vector3.zero), "precondition: the key drives the unit");

            gate.Open();
            yield return null;
            yield return null;
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero), "no gameplay input while the modal is open");

            Release(keyboard.dKey);              // released while the modal was open
            yield return null;
            gate.Close();
            yield return null;
            yield return null;
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero), "a key released during the modal leaves no stuck intent");
        }

        [UnityTest]
        public IEnumerator AKeyStillHeldAtClose_LeavesNoStuckIntentOnceItIsReleased()
        {
            Press(keyboard.dKey);
            yield return null;
            gate.Open();
            yield return null;
            gate.Close();
            yield return null;
            yield return null;

            Release(keyboard.dKey);
            yield return null;
            yield return null;

            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero));
        }

        [UnityTest]
        public IEnumerator PressingAGameplayKey_WhileOpen_DoesNothing()
        {
            gate.Open();
            var before = actions.FindAction("Commands/ToggleTacticalPause").enabled;

            Press(keyboard.spaceKey);
            yield return null;
            Release(keyboard.spaceKey);
            yield return null;

            Assert.That(before, Is.False);
            Assert.That(actions.FindAction("Commands/ToggleTacticalPause").WasPressedThisFrame(), Is.False);
        }

        [UnityTest]
        public IEnumerator InventoryToggle_StillWorks_WhileOpen_SoTheModalCanBeClosed()
        {
            gate.Open();
            var toggle = actions.FindAction("Inventory/Toggle");

            Press(keyboard.iKey);
            yield return null;

            Assert.That(toggle.WasPressedThisFrame(), Is.True);
            Release(keyboard.iKey);
            yield return null;
        }
    }
}
#endif
```

`TestControls.Load()`/`Ref`/`Reset` exist (see `DirectControlInputTests`). Check what `TestControls.Reset` does and adapt if it re-enables or clones actions.

- [ ] **Step 8: Run**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.InputModalGatePlayModeTests"` → `EXIT=0`, 4 passed.

---

### Task 16: The inventory view, its builder and its requests

**Files:**
- Create: `Assets/_Project/Scripts/Hud/InventoryView.cs`, `InventoryViewBuilder.cs`, `InventoryRequests.cs`, `Assets/_Project/Scripts/Items/ItemText.cs`
- Test: `Assets/_Project/Tests/EditMode/ItemTextTests.cs`, `Assets/_Project/Tests/PlayMode/InventoryViewBuilderPlayModeTests.cs`

**Interfaces:**
- Consumes: T3, T4, T10, T12, T13; `UnitIdentity.OperativeId`; `MissionDirector.Friendlies/GenerateNew/RegenerateSame/State`; `LootKnowledge`.
- Produces: `ItemText.Describe(ItemDefinition)`, `ItemText.Compare(ItemDefinition candidate, ItemDefinition current)`, `ItemText.Modifiers(StatModifiers)`; `InventoryListKind`, `InventorySelection`, `InventoryTab`, `InventorySlotView`, `InventoryRow`, `InventoryAction`, `InventoryView`, `InventoryViewSources`, `InventoryViewBuilder.Build(InventoryViewSources, string inspectedOperativeId, InventorySelection, InventoryView)`; `RequestResult`, `InventoryRequests` (`Equip`, `Unequip`, `ToStash`, `FromStash`, `Use`, `Take`, `TakeAll`, `DeployNew`, `DeploySame`).

- [ ] **Step 1: Write the failing text tests**

`Assets/_Project/Tests/EditMode/ItemTextTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class ItemTextTests
    {
        readonly List<Object> made = new List<Object>();
        ItemDefinition rifle, marksman, vest, boots, medkit;

        T Track<T>(T o) where T : Object { made.Add(o); return o; }

        [SetUp]
        public void SetUp()
        {
            var ranged = Track(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));
            var mark = Track(CombatArchetype.Create("Marksman", CombatRole.Ranged, 16f, 40, 2.5f));
            rifle = Track(ItemDefinition.Create("rifle", "Service Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: ranged));
            marksman = Track(ItemDefinition.Create("marksman", "Marksman Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: mark));
            vest = Track(ItemDefinition.Create("vest", "Vest", ItemCategory.Armor, ItemSlot.Armor, modifiers: new StatModifiers { maxHealth = 20 }));
            boots = Track(ItemDefinition.Create("boots", "Boots", ItemCategory.Utility, ItemSlot.Utility, modifiers: new StatModifiers { moveSpeed = 0.5f }));
            medkit = Track(ItemDefinition.Create("medkit", "Medkit", ItemCategory.Consumable, ItemSlot.None, 3, default, null, 40));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) Object.DestroyImmediate(o);
            made.Clear();
        }

        [Test]
        public void Describe_ShowsTheNumbersThatMatter()
        {
            StringAssert.Contains("Damage 15", ItemText.Describe(rifle));
            StringAssert.Contains("Range 8", ItemText.Describe(rifle));
            StringAssert.Contains("+20 max health", ItemText.Describe(vest));
            StringAssert.Contains("+0.5 m/s speed", ItemText.Describe(boots));
            StringAssert.Contains("Restores 40", ItemText.Describe(medkit));
        }

        [Test]
        public void Compare_ShowsBeforeAndAfter_ForWeapons()
        {
            var text = ItemText.Compare(marksman, rifle);

            StringAssert.Contains("Damage 15 -> 40", text);
            StringAssert.Contains("Range 8 -> 16", text);
            StringAssert.Contains("Interval 1 -> 2.5", text);
        }

        [Test]
        public void Compare_AgainstNothing_ShowsNone_AndForArmorShowsTheModifier()
        {
            StringAssert.Contains("none", ItemText.Compare(rifle, null));
            StringAssert.Contains("+20 max health", ItemText.Compare(vest, null));
        }

        [Test]
        public void Compare_OfAConsumable_IsEmpty()
        {
            Assert.That(ItemText.Compare(medkit, rifle), Is.Empty);
        }

        [Test]
        public void Modifiers_ListsOnlyWhatIsNonZero_WithSigns()
        {
            var text = ItemText.Modifiers(new StatModifiers { attackDamage = 0.15f, abilityCooldownReduction = 0.1f });

            StringAssert.Contains("+15% attack damage", text);
            StringAssert.Contains("-10% ability cooldown", text);
            Assert.That(ItemText.Modifiers(default), Is.Empty);
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails, then implement `ItemText`**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.ItemTextTests"` → `EXIT=1`.

`Assets/_Project/Scripts/Items/ItemText.cs`:

```csharp
using System.Collections.Generic;
using System.Globalization;

namespace Blackglass
{
    /// <summary>The few words the inventory panel shows about an item, as pure functions of the definition (placeholder text, no art).</summary>
    public static class ItemText
    {
        static string N(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        public static string Describe(ItemDefinition item)
        {
            if (item == null)
                return string.Empty;
            switch (item.Category)
            {
                case ItemCategory.Weapon:
                    var w = item.Weapon;
                    return $"{w.Role} weapon\nDamage {w.Damage}   Range {N(w.Range)} m   Interval {N(w.AttackInterval)} s";
                case ItemCategory.Consumable:
                    return $"Restores {item.HealAmount} health to the user.\nNot used at full health.";
                default:
                    return Modifiers(item.Modifiers);
            }
        }

        /// <summary>What swapping to `candidate` would change, against what is in the same slot now (null = nothing). Empty for items that do not equip.</summary>
        public static string Compare(ItemDefinition candidate, ItemDefinition current)
        {
            if (candidate == null || candidate.Slot == ItemSlot.None)
                return string.Empty;
            if (candidate.Category == ItemCategory.Weapon)
            {
                var a = current != null ? current.Weapon : null;
                var b = candidate.Weapon;
                return $"vs {(current != null ? current.DisplayName : "none")}\n" +
                       $"Damage {(a != null ? a.Damage.ToString(CultureInfo.InvariantCulture) : "-")} -> {b.Damage}\n" +
                       $"Range {(a != null ? N(a.Range) : "-")} -> {N(b.Range)} m\n" +
                       $"Interval {(a != null ? N(a.AttackInterval) : "-")} -> {N(b.AttackInterval)} s";
            }
            var now = current != null ? Modifiers(current.Modifiers) : string.Empty;
            return $"{Modifiers(candidate.Modifiers)}\nnow: {(current != null ? current.DisplayName : "none")}{(now.Length > 0 ? " (" + now.Replace("\n", ", ") + ")" : string.Empty)}";
        }

        public static string Modifiers(StatModifiers m)
        {
            var lines = new List<string>();
            if (m.maxHealth != 0)
                lines.Add($"{Sign(m.maxHealth)}{m.maxHealth} max health");
            if (m.moveSpeed != 0f)
                lines.Add($"{Sign(m.moveSpeed)}{N(m.moveSpeed)} m/s speed");
            if (m.attackDamage != 0f)
                lines.Add($"{Sign(m.attackDamage)}{N(m.attackDamage * 100f)}% attack damage");
            if (m.abilityPower != 0f)
                lines.Add($"{Sign(m.abilityPower)}{N(m.abilityPower * 100f)}% ability power");
            if (m.abilityCooldownReduction != 0f)
                lines.Add($"-{N(m.abilityCooldownReduction * 100f)}% ability cooldown");
            return string.Join("\n", lines);
        }

        static string Sign(float value) => value >= 0f ? "+" : string.Empty;
    }
}
```

`Compare` for a negative `Modifiers` value prints `-N` since `N(...)` of a negative number keeps its sign and `Sign` returns empty. Run `Tools/run-tests.sh EditMode "Blackglass.Tests.ItemTextTests"` → `EXIT=0`, 5 passed.

- [ ] **Step 3: Write the failing builder/requests tests**

`Assets/_Project/Tests/PlayMode/InventoryViewBuilderPlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class InventoryViewBuilderPlayModeTests
    {
        const string Items = "Assets/_Project/Data/Items/";
        const string Ops = "Assets/_Project/Data/Operatives/";
        MissionRig rig;
        SquadRoster roster;
        SquadInventory inventory;
        InventoryRequests requests;
        InventoryViewSources sources;
        readonly InventoryView view = new InventoryView();

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        [SetUp]
        public void SetUp()
        {
            rig = new MissionRig();
            var squad = new[] { "Darius", "Kestrel", "Sable" }.Select(n => Load<OperativeDefinition>($"{Ops}Definitions/{n}.asset")).ToArray();
            roster = rig.AddRoster(squad, Load<ProgressionTrack>(Ops + "Progression.asset"));
            inventory = rig.AddInventory(Load<ItemCatalogue>(Items + "ItemCatalogue.asset"), Load<StarterLoadout>(Items + "StarterLoadout.asset"), roster);
            rig.AddLoot(Load<LootTable>(Items + "LootTable.asset"));
            requests = new InventoryRequests(inventory, rig.Director);
            sources = new InventoryViewSources { inventory = inventory, roster = roster, director = rig.Director };
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
            rig = null;
        }

        string Id(int index) => roster.Members[index].Id;

        [Test]
        public void Loadout_ShowsTheStash_TheBag_AndTheEquipmentOfTheInspectedOperative()
        {
            InventoryViewBuilder.Build(sources, Id(1), default, view);

            Assert.That(view.Mode, Is.EqualTo(InventoryMode.Loadout));
            Assert.That(view.InspectedId, Is.EqualTo(Id(1)));
            Assert.That(view.InspectedName, Is.EqualTo("Kestrel"));
            Assert.That(view.Tabs, Has.Count.EqualTo(3));
            Assert.That(view.Slots[0].ItemLabel, Does.Contain("Marksman"));
            Assert.That(view.Stash.Count, Is.GreaterThan(0));
            Assert.That(view.Bag.Any(r => r.IsEquipped && r.Label.Contains("Marksman")), Is.True);
        }

        [Test]
        public void AnUnknownInspectedId_FallsBackToTheFirstOperative()
        {
            InventoryViewBuilder.Build(sources, "nobody", default, view);
            Assert.That(view.InspectedId, Is.EqualTo(Id(0)));
        }

        [Test]
        public void Loadout_Equip_IsOfferedForAnUnequippedWeapon_AndAppliesToTheInspectedOperativeOnly()
        {
            var blade = inventory.Core.Session.Stash.Entries.First(e => e.DefinitionId == "item.combat-blade");
            Assert.That(requests.FromStash(Id(2), blade.InstanceId).Succeeded, Is.True);
            var bladeId = inventory.Core.Session.Loadout(Id(2)).Bag.Entries.First(e => e.DefinitionId == "item.combat-blade").InstanceId;

            InventoryViewBuilder.Build(sources, Id(2), new InventorySelection(InventoryListKind.Bag, bladeId), view);
            Assert.That(view.Equip.Visible && view.Equip.Enabled, Is.True);
            Assert.That(view.DetailTitle, Does.Contain("Combat Blade"));
            Assert.That(view.DetailBody, Does.Contain("Damage"));

            var result = requests.Equip(Id(2), bladeId);
            Assert.That(result.Ok, Is.True, result.Message);
            Assert.That(inventory.EquippedFor(Id(2)).Weapon.Id, Is.EqualTo("item.combat-blade"));
            Assert.That(inventory.EquippedFor(Id(0)).Weapon.Id, Is.EqualTo("item.service-rifle"), "another operative is unchanged");
        }

        [Test]
        public void Loadout_MoveToBag_ShowsWhyWhenTheBagIsFull()
        {
            var bag = inventory.Core.Session.Loadout(Id(0)).Bag;
            while (bag.FreeEntries > 0)
                bag.Add(inventory.Catalogue.Find("item.light-vest"), 1);
            var stashEntry = inventory.Core.Session.Stash.Entries.First(e => e.DefinitionId == "item.servo-boots");

            InventoryViewBuilder.Build(sources, Id(0), new InventorySelection(InventoryListKind.Stash, stashEntry.InstanceId), view);

            Assert.That(view.ToBag.Visible, Is.True);
            Assert.That(view.ToBag.Enabled, Is.False);
            Assert.That(view.ToBag.Reason, Does.Contain("full"));
        }

        [UnityTest]
        public IEnumerator InMission_EquipmentIsLocked_WithTheReason_AndUseIsOffered()
        {
            yield return rig.Generate(12345);

            var medkit = inventory.Core.Working.Loadout(Id(0)).Bag.Entries.First(e => e.DefinitionId == "item.medkit");
            InventoryViewBuilder.Build(sources, Id(0), new InventorySelection(InventoryListKind.Bag, medkit.InstanceId), view);

            Assert.That(view.Mode, Is.EqualTo(InventoryMode.InMission));
            Assert.That(view.Equip.Visible, Is.False, "a consumable does not equip");
            Assert.That(view.Use.Visible, Is.True);
            Assert.That(view.Use.Enabled, Is.False, "full health");
            Assert.That(view.Use.Reason, Does.Contain("full health"));

            var weapon = inventory.Core.Working.Loadout(Id(0)).Bag.Entries.First(e => e.DefinitionId == "item.service-rifle");
            InventoryViewBuilder.Build(sources, Id(0), new InventorySelection(InventoryListKind.Bag, weapon.InstanceId), view);
            Assert.That(view.Unequip.Visible && !view.Unequip.Enabled, Is.True, "an equipped weapon shows Unequip, locked");
            Assert.That(view.Unequip.Reason, Does.Contain("during a mission"));
        }

        [UnityTest]
        public IEnumerator Use_AppliesToTheInspectedOperative_NotTheControlledOne()
        {
            yield return rig.Generate(12345);
            var inspected = rig.Director.Friendlies[1];   // Kestrel; Darius is the controlled character
            var controlled = rig.Director.Friendlies[0];
            Assert.That(rig.Active.Unit, Is.EqualTo(controlled));
            inspected.GetComponent<Health>().TakeDamage(40);
            var medkit = inventory.Core.Working.Loadout(Id(1)).Bag.Entries.First(e => e.DefinitionId == "item.medkit");

            var result = requests.Use(Id(1), medkit.InstanceId, queue: false);
            yield return null;
            yield return null;

            Assert.That(result.Ok, Is.True, result.Message);
            var health = inspected.GetComponent<Health>();
            Assert.That(health.Current, Is.EqualTo(health.Max), "the inspected operative was healed");
            Assert.That(inventory.Core.Working.Loadout(Id(1)).Bag.CountOf("item.medkit"), Is.EqualTo(1));
            Assert.That(inventory.Core.Working.Loadout(Id(0)).Bag.CountOf("item.medkit"), Is.EqualTo(2), "the controlled operative's medkits are untouched");
        }

        [UnityTest]
        public IEnumerator Use_AtFullHealth_IsRefusedWithAMessage_AndConsumesNothing()
        {
            yield return rig.Generate(12345);
            var medkit = inventory.Core.Working.Loadout(Id(0)).Bag.Entries.First(e => e.DefinitionId == "item.medkit");

            var result = requests.Use(Id(0), medkit.InstanceId, queue: false);

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Message, Does.Contain("full health"));
            Assert.That(inventory.Core.Working.Loadout(Id(0)).Bag.CountOf("item.medkit"), Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator Container_ContentsAreListedOnlyOnceSearched()
        {
            yield return rig.Generate(12345);
            var container = rig.Director.Current.LootContainers[0];
            sources.container = container;

            InventoryViewBuilder.Build(sources, Id(0), default, view);
            Assert.That(view.HasContainer, Is.False, "unsearched: no contents, not even a count");
            Assert.That(view.Container, Is.Empty);

            container.InitializeWith(container.Contents, searched: true);
            InventoryViewBuilder.Build(sources, Id(0), default, view);
            Assert.That(view.HasContainer, Is.True);
            Assert.That(view.Container.Count, Is.EqualTo(container.Contents.Count));
            Assert.That(view.TakeAll.Visible, Is.True);
        }

        [UnityTest]
        public IEnumerator Take_And_TakeAll_IssueOrdersForTheInspectedOperative()
        {
            yield return rig.Generate(12345);
            var container = rig.Director.Current.LootContainers[0];
            container.InitializeWith(container.Contents, searched: true);
            var unit = rig.Director.Friendlies[2];
            UnityEngine.AI.NavMesh.SamplePosition(container.Position, out var stand, 2f, UnityEngine.AI.NavMesh.AllAreas);
            unit.GetComponent<UnityEngine.AI.NavMeshAgent>().Warp(stand.position);
            yield return null;
            var before = container.Contents.Entries.Sum(e => e.Quantity);

            var result = requests.TakeAll(Id(2), container, queue: false);
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);

            Assert.That(result.Ok, Is.True, result.Message);
            var after = container.Contents.Entries.Sum(e => e.Quantity);
            Assert.That(after, Is.LessThan(before));
            Assert.That(rig.Director.Friendlies[0].GetComponent<UnitItems>().LastCollect.Taken, Is.EqualTo(0), "nobody else took anything");
        }

        [Test]
        public void Deploy_IsRefusedWhileGenerating()
        {
            rig.Director.Generate(12345);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("already being generated"));

            var result = requests.DeployNew();

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Message, Does.Contain("generat"));
        }
    }
}
#endif
```

- [ ] **Step 4: Run to verify it fails, then implement**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.InventoryViewBuilderPlayModeTests"` → `EXIT=1`.

`Assets/_Project/Scripts/Hud/InventoryView.cs`:

```csharp
using System.Collections.Generic;

namespace Blackglass
{
    public enum InventoryListKind { None, Bag, Stash, Slot, Container }

    /// <summary>What the panel has selected: a list and an entry's instance id (for a Slot, the equipped entry's id).</summary>
    public readonly struct InventorySelection
    {
        public InventorySelection(InventoryListKind kind, string instanceId)
        {
            Kind = kind;
            InstanceId = instanceId;
        }

        public InventoryListKind Kind { get; }
        public string InstanceId { get; }
        public bool IsNone => Kind == InventoryListKind.None || string.IsNullOrEmpty(InstanceId);
    }

    public struct InventoryTab { public string OperativeId, Name; public bool IsInspected, IsDown; }

    public struct InventorySlotView { public ItemSlot Slot; public string Title, ItemLabel, InstanceId; }

    public struct InventoryRow
    {
        public string InstanceId, Label, Tag;   // Tag: "", "NEW +n" (unsecured pickup)
        public int Quantity;
        public bool IsEquipped;
    }

    /// <summary>Whether an action is offered, usable, and if offered but not usable, why (shown as text).</summary>
    public struct InventoryAction
    {
        public bool Visible, Enabled;
        public string Reason;

        public static InventoryAction Hidden => default;
        public static InventoryAction Usable => new InventoryAction { Visible = true, Enabled = true, Reason = string.Empty };
        public static InventoryAction Blocked(string reason) => new InventoryAction { Visible = true, Enabled = false, Reason = reason };
    }

    /// <summary>
    /// Everything the inventory panel shows, as plain data rebuilt by InventoryViewBuilder. The panel draws it and never
    /// queries gameplay; every request carries an operative id and an instance id, never "whoever is controlled".
    /// </summary>
    public sealed class InventoryView
    {
        public readonly List<InventoryTab> Tabs = new List<InventoryTab>();
        public readonly InventorySlotView[] Slots = new InventorySlotView[3];
        public readonly List<InventoryRow> Bag = new List<InventoryRow>();
        public readonly List<InventoryRow> Stash = new List<InventoryRow>();
        public readonly List<InventoryRow> Container = new List<InventoryRow>();

        public bool HasInventory;
        public InventoryMode Mode;
        public string Header = string.Empty, InspectedId = string.Empty, InspectedName = string.Empty;
        public int BagCapacity;
        public bool HasContainer;
        public InventorySelection Selected;
        public string DetailTitle = string.Empty, DetailBody = string.Empty;
        public InventoryAction Equip, Unequip, ToStash, ToBag, Use, QueueUse, Take, QueueTake, TakeAll, QueueTakeAll;

        public void Clear()
        {
            Tabs.Clear();
            Bag.Clear();
            Stash.Clear();
            Container.Clear();
            for (var i = 0; i < Slots.Length; i++)
                Slots[i] = default;
            HasInventory = false;
            Mode = InventoryMode.Loadout;
            Header = string.Empty;
            InspectedId = string.Empty;
            InspectedName = string.Empty;
            BagCapacity = 0;
            HasContainer = false;
            Selected = default;
            DetailTitle = string.Empty;
            DetailBody = string.Empty;
            Equip = Unequip = ToStash = ToBag = Use = QueueUse = Take = QueueTake = TakeAll = QueueTakeAll = default;
        }
    }
}
```

`Assets/_Project/Scripts/Hud/InventoryViewBuilder.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>The optional gameplay references the panel's view reads; a missing one hides the part it feeds.</summary>
    public sealed class InventoryViewSources
    {
        public SquadInventory inventory;
        public SquadRoster roster;
        public MissionDirector director;
        public IntelligenceService intelligence;
        public LootContainer container;
    }

    /// <summary>
    /// The only code that reads gameplay for the inventory panel (the HUD's snapshot boundary, decision 042, for the modal).
    /// It decides what is shown and which actions are offered, with the reason when one is not. Container contents pass
    /// LootKnowledge (searched only) and the container's location passes the intelligence rule, so the panel is never a
    /// bypass around the fog of war.
    /// </summary>
    public static class InventoryViewBuilder
    {
        public static void Build(InventoryViewSources s, string inspectedId, InventorySelection selection, InventoryView into)
        {
            into.Clear();
            var inventory = s != null ? s.inventory : null;
            if (inventory == null)
                return;
            var core = inventory.Core;
            var state = core.Active;
            var catalogue = core.Catalogue;
            into.HasInventory = true;
            into.Mode = core.Mode;
            into.Header = core.Mode == InventoryMode.Loadout ? "LOADOUT" : "INVENTORY - MISSION";
            into.BagCapacity = core.BagCapacity;

            BuildTabs(s, state, inspectedId, into);
            if (string.IsNullOrEmpty(into.InspectedId))
                return;
            var loadout = state.Loadout(into.InspectedId);
            if (loadout == null)
                return;
            var equipped = loadout.Resolve(catalogue);

            for (var i = 0; i < 3; i++)
            {
                var slot = (ItemSlot)(i + 1);
                var id = loadout.Equipment.InstanceIdIn(slot);
                var entry = loadout.Bag.Find(id);
                into.Slots[i] = new InventorySlotView
                {
                    Slot = slot, Title = slot.ToString().ToUpperInvariant(),
                    ItemLabel = entry != null ? LabelOf(catalogue, entry.DefinitionId) : "(empty)", InstanceId = entry != null ? id : string.Empty,
                };
            }
            var firstOfDefinition = new System.Collections.Generic.HashSet<string>();
            foreach (var entry in loadout.Bag.Entries)
            {
                var tag = string.Empty;
                if (core.Mode == InventoryMode.InMission && firstOfDefinition.Add(entry.DefinitionId))
                {
                    var unsecured = core.UnsecuredCount(into.InspectedId, entry.DefinitionId);
                    if (unsecured > 0)
                        tag = "NEW +" + unsecured;
                }
                into.Bag.Add(new InventoryRow
                {
                    InstanceId = entry.InstanceId, Label = LabelOf(catalogue, entry.DefinitionId), Quantity = entry.Quantity,
                    IsEquipped = loadout.Equipment.Contains(entry.InstanceId), Tag = tag,
                });
            }
            if (core.Mode == InventoryMode.Loadout)
            {
                foreach (var entry in state.Stash.Entries)
                    into.Stash.Add(new InventoryRow { InstanceId = entry.InstanceId, Label = LabelOf(catalogue, entry.DefinitionId), Quantity = entry.Quantity });
            }

            var container = s.container;
            if (container != null && LootKnowledge.CanSeeLocation(s.intelligence, container) && LootKnowledge.CanSeeContents(container))
            {
                into.HasContainer = true;
                foreach (var entry in container.Contents.Entries)
                    into.Container.Add(new InventoryRow { InstanceId = entry.InstanceId, Label = LabelOf(catalogue, entry.DefinitionId), Quantity = entry.Quantity });
            }

            ResolveSelection(selection, loadout, state, container, into);
            BuildActions(s, core, loadout, equipped, into);
        }

        static void BuildTabs(InventoryViewSources s, InventoryState state, string inspectedId, InventoryView into)
        {
            var firstId = string.Empty;
            foreach (var loadout in state.Loadouts)
            {
                var id = loadout.OperativeId;
                var member = s.roster != null ? s.roster.Find(id) : null;
                var name = member != null ? member.Definition.DisplayName : id;
                var unit = FindUnit(s.director, id);
                if (firstId.Length == 0)
                    firstId = id;
                into.Tabs.Add(new InventoryTab { OperativeId = id, Name = name, IsDown = unit != null && !unit.IsAlive });
            }
            var chosen = into.Tabs.FindIndex(t => t.OperativeId == inspectedId);
            if (chosen < 0 && into.Tabs.Count > 0)
                chosen = 0;
            for (var i = 0; i < into.Tabs.Count; i++)
            {
                var tab = into.Tabs[i];
                tab.IsInspected = i == chosen;
                into.Tabs[i] = tab;
            }
            if (chosen >= 0)
            {
                into.InspectedId = into.Tabs[chosen].OperativeId;
                into.InspectedName = into.Tabs[chosen].Name;
            }
        }

        static void ResolveSelection(InventorySelection selection, OperativeLoadout loadout, InventoryState state, LootContainer container,
            InventoryView into)
        {
            if (selection.IsNone)
                return;
            ItemEntry entry = null;
            switch (selection.Kind)
            {
                case InventoryListKind.Bag:
                case InventoryListKind.Slot:
                    entry = loadout.Bag.Find(selection.InstanceId);
                    break;
                case InventoryListKind.Stash:
                    entry = into.Mode == InventoryMode.Loadout ? state.Stash.Find(selection.InstanceId) : null;
                    break;
                case InventoryListKind.Container:
                    entry = into.HasContainer ? container.Contents.Find(selection.InstanceId) : null;
                    break;
            }
            if (entry == null)
                return;
            into.Selected = selection;
        }

        static void BuildActions(InventoryViewSources s, InventorySession core, OperativeLoadout loadout, EquippedItems equipped, InventoryView into)
        {
            var inMission = core.Mode == InventoryMode.InMission;
            var catalogue = core.Catalogue;
            var unit = FindUnit(s.director, into.InspectedId);
            var items = unit != null ? unit.GetComponent<UnitItems>() : null;
            var alive = unit != null && unit.IsAlive;

            if (into.HasContainer)
            {
                var canTake = inMission && alive;
                var why = !inMission ? "The mission is over: this loot is out of reach." : !alive ? into.InspectedName + " is down." : string.Empty;
                into.TakeAll = canTake ? InventoryAction.Usable : InventoryAction.Blocked(why);
                into.QueueTakeAll = into.TakeAll;
            }

            if (into.Selected.IsNone)
                return;
            var entry = FindEntry(into.Selected, loadout, core.Active, s.container);
            var definition = entry != null && catalogue != null ? catalogue.Find(entry.DefinitionId) : null;
            if (definition == null)
            {
                into.DetailTitle = entry != null ? $"Unknown item ({entry.DefinitionId})" : string.Empty;
                into.DetailBody = "This item's definition is missing. It cannot be used or equipped.";
                return;
            }
            var current = definition.Slot != ItemSlot.None ? SlotItem(equipped, definition.Slot) : null;
            into.DetailTitle = definition.DisplayName + (entry.Quantity > 1 ? " x" + entry.Quantity : string.Empty);
            into.DetailBody = ItemText.Describe(definition)
                + (definition.Slot != ItemSlot.None && (current == null || current != definition) ? "\n\n" + ItemText.Compare(definition, current) : string.Empty)
                + (definition.Description.Length > 0 ? "\n\n" + definition.Description : string.Empty);

            var locked = inMission ? InventorySession.LockedReason : string.Empty;
            switch (into.Selected.Kind)
            {
                case InventoryListKind.Bag:
                case InventoryListKind.Slot:
                {
                    var isEquipped = loadout.Equipment.Contains(entry.InstanceId);
                    if (definition.Slot != ItemSlot.None)
                    {
                        if (isEquipped)
                            into.Unequip = inMission ? InventoryAction.Blocked(locked) : InventoryAction.Usable;
                        else
                            into.Equip = inMission ? InventoryAction.Blocked(locked) : InventoryAction.Usable;
                    }
                    if (!isEquipped)
                        into.ToStash = inMission ? InventoryAction.Blocked(locked) : InventoryAction.Usable;
                    else if (!inMission)
                        into.ToStash = InventoryAction.Blocked(definition.DisplayName + " is equipped. Unequip it first.");
                    else
                        into.ToStash = InventoryAction.Blocked(locked);
                    if (definition.Category == ItemCategory.Consumable)
                    {
                        if (!inMission)
                        {
                            into.Use = InventoryAction.Blocked("Consumables are used during a mission.");
                            into.QueueUse = into.Use;
                        }
                        else
                        {
                            var now = items != null ? items.Check(entry.InstanceId, true) : ItemUseFailure.NoLoadout;
                            var queued = items != null ? items.Check(entry.InstanceId, false) : ItemUseFailure.NoLoadout;
                            into.Use = now == ItemUseFailure.None ? InventoryAction.Usable : InventoryAction.Blocked(UseReason(now));
                            into.QueueUse = queued == ItemUseFailure.None ? InventoryAction.Usable : InventoryAction.Blocked(UseReason(queued));
                        }
                    }
                    break;
                }
                case InventoryListKind.Stash:
                    into.ToBag = loadout.Bag.RoomFor(definition) > 0 ? InventoryAction.Usable : InventoryAction.Blocked(into.InspectedName + "'s bag is full.");
                    break;
                case InventoryListKind.Container:
                {
                    var canTake = inMission && alive;
                    var why = !inMission ? "The mission is over: this loot is out of reach." : !alive ? into.InspectedName + " is down." : string.Empty;
                    into.Take = canTake ? InventoryAction.Usable : InventoryAction.Blocked(why);
                    into.QueueTake = into.Take;
                    break;
                }
            }
        }

        static ItemEntry FindEntry(InventorySelection selection, OperativeLoadout loadout, InventoryState state, LootContainer container)
        {
            switch (selection.Kind)
            {
                case InventoryListKind.Bag:
                case InventoryListKind.Slot: return loadout.Bag.Find(selection.InstanceId);
                case InventoryListKind.Stash: return state.Stash.Find(selection.InstanceId);
                case InventoryListKind.Container: return container != null ? container.Contents.Find(selection.InstanceId) : null;
                default: return null;
            }
        }

        static ItemDefinition SlotItem(EquippedItems equipped, ItemSlot slot)
        {
            switch (slot)
            {
                case ItemSlot.Weapon: return equipped.Weapon;
                case ItemSlot.Armor: return equipped.Armor;
                case ItemSlot.Utility: return equipped.Utility;
                default: return null;
            }
        }

        static string LabelOf(ItemCatalogue catalogue, string definitionId)
        {
            var definition = catalogue != null ? catalogue.Find(definitionId) : null;
            return definition != null ? definition.DisplayName : $"Unknown item ({definitionId})";
        }

        internal static string UseReason(ItemUseFailure failure)
        {
            switch (failure)
            {
                case ItemUseFailure.Dead: return "Down: cannot use items.";
                case ItemUseFailure.NoLoadout: return "No mission is running.";
                case ItemUseFailure.NotOwned: return "That item is gone.";
                case ItemUseFailure.NotUsable: return "That cannot be used.";
                case ItemUseFailure.NoEffect: return "Already at full health.";
                default: return string.Empty;
            }
        }

        /// <summary>The squad member's unit for this operative id, dead units included (they stay in the list), else null.</summary>
        internal static CommandableUnit FindUnit(MissionDirector director, string operativeId)
        {
            if (director == null || string.IsNullOrEmpty(operativeId))
                return null;
            foreach (var unit in director.Friendlies)
            {
                if (unit != null && unit.TryGetComponent<UnitIdentity>(out var identity) && identity.OperativeId == operativeId)
                    return unit;
            }
            return null;
        }
    }
}
```

The test `Use … Does.Contain("full health")` reads `UseReason(NoEffect)` = "Already at full health." ✓ (contains "full health"). `Does.Contain("generat")` in the deploy test reads the request message below.

`Assets/_Project/Scripts/Hud/InventoryRequests.cs`:

```csharp
namespace Blackglass
{
    /// <summary>The outcome of a panel request: whether it was accepted, and the line to show.</summary>
    public readonly struct RequestResult
    {
        public RequestResult(bool ok, string message)
        {
            Ok = ok;
            Message = message ?? string.Empty;
        }

        public bool Ok { get; }
        public string Message { get; }
    }

    /// <summary>
    /// What the inventory panel's buttons ask of gameplay. Each request names the operative it affects, and goes through the
    /// same APIs the rest of the game uses (SquadInventory for loadout edits, CommandableUnit.Issue for item orders, the
    /// director for deployment), so the panel holds no rules and no state. A unit that died or vanished since the last view
    /// makes the request a safe refusal with a message.
    /// </summary>
    public sealed class InventoryRequests
    {
        readonly SquadInventory inventory;
        readonly MissionDirector director;

        public InventoryRequests(SquadInventory inventory, MissionDirector director)
        {
            this.inventory = inventory;
            this.director = director;
        }

        public RequestResult Equip(string operativeId, string instanceId)
        {
            var result = inventory.Core.Equip(operativeId, instanceId);
            return new RequestResult(result.Ok, result.Ok ? "Equipped." : result.Reason);
        }

        public RequestResult Unequip(string operativeId, ItemSlot slot)
        {
            var result = inventory.Core.Unequip(operativeId, slot);
            return new RequestResult(result.Ok, result.Ok ? "Unequipped." : result.Reason);
        }

        public TransferResult ToStash(string operativeId, string instanceId) =>
            inventory.Core.ToStash(operativeId, instanceId, int.MaxValue);

        public TransferResult FromStash(string operativeId, string instanceId) =>
            inventory.Core.FromStash(operativeId, instanceId, int.MaxValue);

        public RequestResult Use(string operativeId, string instanceId, bool queue)
        {
            var unit = Unit(operativeId, out var failure);
            if (unit == null)
                return new RequestResult(false, failure);
            var accepted = unit.Issue(new UseItemCommand(instanceId), queue ? IssueMode.Append : IssueMode.Replace);
            if (accepted)
                return new RequestResult(true, queue ? "Use queued." : "Using.");
            var items = unit.GetComponent<UnitItems>();
            return new RequestResult(false, items != null ? InventoryViewBuilder.UseReason(items.LastUseFailure) : "Cannot use items.");
        }

        public RequestResult Take(string operativeId, LootContainer container, string instanceId, bool queue)
        {
            var unit = Unit(operativeId, out var failure);
            if (unit == null)
                return new RequestResult(false, failure);
            if (container == null)
                return new RequestResult(false, "The container is gone.");
            var accepted = unit.Issue(new CollectCommand(container, instanceId), queue ? IssueMode.Append : IssueMode.Replace);
            if (accepted)
                return new RequestResult(true, queue ? "Take queued." : "Taking.");
            var items = unit.GetComponent<UnitItems>();
            return new RequestResult(false, items != null ? items.LastCollect.Describe() : "Cannot take items.");
        }

        public RequestResult TakeAll(string operativeId, LootContainer container, bool queue) => Take(operativeId, container, null, queue);

        public RequestResult DeployNew() => Deploy(director != null && director.GenerateNew());

        public RequestResult DeploySame() => Deploy(director != null && director.RegenerateSame());

        RequestResult Deploy(bool started) =>
            started ? new RequestResult(true, "Deploying.") : new RequestResult(false, "A mission is being generated. Try again in a moment.");

        CommandableUnit Unit(string operativeId, out string failure)
        {
            var unit = InventoryViewBuilder.FindUnit(director, operativeId);
            failure = unit == null ? "That operative is not on the mission." : !unit.IsAlive ? "That operative is down." : string.Empty;
            return failure.Length == 0 ? unit : null;
        }
    }
}
```

`MissionDirector.GenerateNew()`/`RegenerateSame()` already return false (with a warning) while generating; the test for `Deploy_IsRefusedWhileGenerating` expects a log warning: add `LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("already being generated"));` to that test.

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.InventoryViewBuilderPlayModeTests"` → `EXIT=0`, 9 passed.

---

### Task 17: "No weapon equipped" in the tactical HUD

**Files:**
- Modify: `Assets/_Project/Scripts/Hud/HudSnapshot.cs`, `HudSnapshotBuilder.cs`, `OperativePanel.cs`
- Test: `Assets/_Project/Tests/PlayMode/HudNoWeaponPlayModeTests.cs`

**Interfaces:**
- Consumes: `UnitAttacker.HasWeapon`, `UnitIdentity.Effective.WeaponName` (T5/T9).
- Produces: `HudSnapshot.ControlledWeapon` (`"No weapon equipped"` when the controlled unit has no weapon, else the weapon's name for a unit bound to an operative, else empty); the OperativePanel's cover line reads `"<cover>  |  <weapon>"` when `ControlledWeapon` is not empty.

- [ ] **Step 1: Write the failing test**

`Assets/_Project/Tests/PlayMode/HudNoWeaponPlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class HudNoWeaponPlayModeTests
    {
        const string Items = "Assets/_Project/Data/Items/";
        const string Ops = "Assets/_Project/Data/Operatives/";
        MissionRig rig;
        SquadRoster roster;
        readonly HudSnapshotBuilder builder = new HudSnapshotBuilder();
        readonly HudSnapshot snapshot = new HudSnapshot();

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        IEnumerator Start(bool unequipWeapon)
        {
            rig = new MissionRig();
            var squad = new[] { "Darius", "Kestrel", "Sable" }.Select(n => Load<OperativeDefinition>($"{Ops}Definitions/{n}.asset")).ToArray();
            roster = rig.AddRoster(squad, Load<ProgressionTrack>(Ops + "Progression.asset"));
            var inventory = rig.AddInventory(Load<ItemCatalogue>(Items + "ItemCatalogue.asset"), Load<StarterLoadout>(Items + "StarterLoadout.asset"), roster);
            if (unequipWeapon)
                Assert.That(inventory.Core.Unequip(roster.Members[0].Id, ItemSlot.Weapon).Ok, Is.True);
            yield return rig.Generate(12345);
        }

        HudSources Sources() => new HudSources
        {
            activeCharacter = rig.Active, selection = rig.Selection, tacticalPause = rig.Pause, encounter = rig.Encounter,
            director = rig.Director, roster = roster,
        };

        [UnityTest]
        public IEnumerator AnUnarmedControlledUnit_SaysSo_OnTheOperativePanel()
        {
            yield return Start(unequipWeapon: true);

            builder.Build(Sources(), snapshot, 0f);
            Assert.That(snapshot.ControlledWeapon, Is.EqualTo("No weapon equipped"));

            var root = (RectTransform)new GameObject("Root", typeof(RectTransform)).transform;
            var panel = new OperativePanel(root);
            panel.Apply(snapshot);
            Assert.That(panel.CoverLabel.text, Does.Contain("No weapon equipped"));
            Object.DestroyImmediate(root.gameObject);
        }

        [UnityTest]
        public IEnumerator AnArmedControlledUnit_ShowsTheWeaponName()
        {
            yield return Start(unequipWeapon: false);

            builder.Build(Sources(), snapshot, 0f);

            Assert.That(snapshot.ControlledWeapon, Is.EqualTo("Service Rifle"));
        }

        [UnityTest]
        public IEnumerator AUnitWithoutAnOperative_ShowsNoWeaponLine()
        {
            rig = new MissionRig();
            yield return rig.Generate(12345);

            builder.Build(new HudSources { activeCharacter = rig.Active, selection = rig.Selection, director = rig.Director }, snapshot, 0f);

            Assert.That(snapshot.ControlledWeapon, Is.Empty);
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify it fails, then implement**

Run → `EXIT=1` (`ControlledWeapon`).

`HudSnapshot.cs`: add `public string ControlledWeapon = string.Empty;` to the controlled-unit field line and `ControlledWeapon = string.Empty;` to `Clear()`.

`HudSnapshotBuilder.BuildControlled`, after the `into.ControlledMaxHealth = max;` line add:

```csharp
            into.ControlledWeapon = WeaponLine(controlled);
```

and add the helper beside `Identify`:

```csharp
        // "No weapon equipped" for a unit whose Weapon slot is empty; the weapon's name for an operative's unit; else nothing.
        static string WeaponLine(CommandableUnit unit)
        {
            if (unit.TryGetComponent<UnitAttacker>(out var attacker) && !attacker.HasWeapon)
                return "No weapon equipped";
            if (unit.TryGetComponent<UnitIdentity>(out var identity) && identity.HasConfiguration)
                return identity.Effective.WeaponName;
            return string.Empty;
        }
```

`OperativePanel.cs`: widen the cover label (`Line("Cover", info, …, 320f, 41f, 420f, 20f)`) and replace its `SetText` line with

```csharp
            HudFactory.SetText(coverLabel, s.ControlledWeapon.Length > 0 ? s.ControlledCover + "  |  " + s.ControlledWeapon : s.ControlledCover);
```

(the existing `OperativePanel` caches nothing for this label; `SetText` already writes only on change).

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.HudNoWeaponPlayModeTests"` → `EXIT=0`, 3 passed. Then `Tools/run-tests.sh PlayMode "Blackglass.Tests.Hud"` → `EXIT=0` (existing HUD tests unchanged: no operative means an empty weapon line).

---

### Task 18: The inventory modal panel

**Files:**
- Create: `Assets/_Project/Scripts/Hud/InventoryModal.cs`
- Test: `Assets/_Project/Tests/PlayMode/InventoryModalPlayModeTests.cs`

**Interfaces:**
- Consumes: T4, T12, T13, T15 (`InputModalGate`, actions), T16 (`InventoryView`, `InventoryViewBuilder`, `InventoryRequests`), `PointerOnlyInputModule.StripNavigation`, `HudFactory`, `HudTheme`, `PromptResolver`, `ActiveInputDevice.Family`.
- Produces: `InventoryModal` (MonoBehaviour): `IsOpen`, `Open()`, `Close()`, `Toggle()`, `OpenContainer(LootContainer, CommandableUnit)`, `InspectedOperativeId`, `internal Initialize(...)`, `internal Select(InventoryListKind, string)`, `internal Invoke(InventoryCommand)`, `internal Canvas Canvas`, `internal string Message`; `enum InventoryCommand { Equip, Unequip, ToStash, ToBag, Use, QueueUse, Take, QueueTake, TakeAll, QueueTakeAll, DeployNew, DeploySame, Close }`.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/PlayMode/InventoryModalPlayModeTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Blackglass.Tests
{
    public class InventoryModalPlayModeTests : InputTestFixture
    {
        const string Items = "Assets/_Project/Data/Items/";
        const string Ops = "Assets/_Project/Data/Operatives/";
        Keyboard keyboard;
        InputActionAsset actions;
        MissionRig rig;
        SquadRoster roster;
        SquadInventory inventory;
        InventoryModal modal;

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            actions = TestControls.Load();
            rig = new MissionRig();
            var squad = new[] { "Darius", "Kestrel", "Sable" }.Select(n => Load<OperativeDefinition>($"{Ops}Definitions/{n}.asset")).ToArray();
            roster = rig.AddRoster(squad, Load<ProgressionTrack>(Ops + "Progression.asset"));
            inventory = rig.AddInventory(Load<ItemCatalogue>(Items + "ItemCatalogue.asset"), Load<StarterLoadout>(Items + "StarterLoadout.asset"), roster);
            rig.AddLoot(Load<LootTable>(Items + "LootTable.asset"));
            var host = rig.World.Track(new GameObject("Modal"));
            host.SetActive(false);
            modal = host.AddComponent<InventoryModal>();
            modal.Initialize(inventory, roster, rig.Director, null, actions, TestControls.Ref(actions, "Inventory/Toggle"), null, openOnStart: false);
            host.SetActive(true);
        }

        public override void TearDown()
        {
            modal.Close();
            rig.Dispose();
            TestControls.Reset(actions);
            base.TearDown();
        }

        string Id(int i) => roster.Members[i].Id;

        IEnumerator Tap(UnityEngine.InputSystem.Controls.KeyControl key)
        {
            Press(key);
            yield return null;
            Release(key);
            yield return null;
        }

        [UnityTest]
        public IEnumerator TheToggleKey_OpensAndCloses_WithoutTouchingTimeOrPause()
        {
            rig.Pause.Pause();
            var scaleBefore = Time.timeScale;
            yield return null;

            yield return Tap(keyboard.iKey);
            Assert.That(modal.IsOpen, Is.True);
            Assert.That(rig.Pause.IsPaused, Is.True, "opening does not change the pause state");
            Assert.That(Time.timeScale, Is.EqualTo(scaleBefore));

            yield return Tap(keyboard.iKey);
            Assert.That(modal.IsOpen, Is.False);
            Assert.That(rig.Pause.IsPaused, Is.True);
            Assert.That(Time.timeScale, Is.EqualTo(scaleBefore));
        }

        [UnityTest]
        public IEnumerator Open_DisablesGameplayInput_AndCloseRestoresExactly()
        {
            var before = actions.actionMaps.SelectMany(m => m.actions).ToDictionary(a => a, a => a.enabled);

            modal.Open();
            yield return null;
            Assert.That(actions.FindAction("Commands/Command").enabled, Is.False);
            Assert.That(actions.FindAction("Character/Move").enabled, Is.False);
            Assert.That(actions.FindAction("UI/Submit").enabled, Is.True);

            modal.Close();
            yield return null;

            foreach (var pair in before)
                Assert.That(pair.Key.enabled, Is.EqualTo(pair.Value), pair.Key.name);
        }

        [UnityTest]
        public IEnumerator DisablingTheModal_WhileOpen_RestoresTheInput()
        {
            modal.Open();
            var wasOn = actions.FindAction("Inventory/Toggle").enabled;

            modal.enabled = false;
            yield return null;

            Assert.That(modal.IsOpen, Is.False);
            Assert.That(actions.FindAction("UI/Submit").enabled, Is.False);
            Assert.That(wasOn, Is.True);
            modal.enabled = true;
        }

        [UnityTest]
        public IEnumerator InMission_EquipIsRefusedWithAMessage_AndNothingChanges()
        {
            yield return rig.Generate(12345);
            var weapon = inventory.Core.Working.Loadout(Id(0)).Bag.Entries.First(e => e.DefinitionId == "item.service-rifle").InstanceId;
            modal.Open();
            modal.Select(InventoryListKind.Bag, weapon);
            yield return null;

            modal.Invoke(InventoryCommand.Unequip);
            yield return null;

            Assert.That(modal.Message, Does.Contain("during a mission"));
            Assert.That(inventory.EquippedFor(Id(0)).Weapon != null, Is.True);
        }

        [UnityTest]
        public IEnumerator InLoadout_EquipAndUnequip_ActOnTheInspectedOperative_AndRepeatingDoesNotAccumulate()
        {
            modal.Open();
            modal.InspectOperative(Id(2));
            var stash = inventory.Core.Session.Stash.Entries.First(e => e.DefinitionId == "item.light-vest").InstanceId;
            modal.Select(InventoryListKind.Stash, stash);
            yield return null;
            modal.Invoke(InventoryCommand.ToBag);
            var vest = inventory.Core.Session.Loadout(Id(2)).Bag.Entries.First(e => e.DefinitionId == "item.light-vest").InstanceId;
            var baseHealth = roster.Evaluate(roster.Members[2]).MaxHealth;

            for (var i = 0; i < 4; i++)
            {
                modal.Select(InventoryListKind.Bag, vest);
                yield return null;
                modal.Invoke(InventoryCommand.Equip);
                modal.Select(InventoryListKind.Slot, vest);
                yield return null;
                modal.Invoke(InventoryCommand.Unequip);
            }
            modal.Select(InventoryListKind.Bag, vest);
            yield return null;
            modal.Invoke(InventoryCommand.Equip);

            Assert.That(inventory.EquippedFor(Id(2)).Armor.Id, Is.EqualTo("item.light-vest"));
            Assert.That(inventory.EquippedFor(Id(0)).Armor == null, Is.True, "another operative is unchanged");
            var kit = roster.Evaluate(roster.Members[2], inventory.EquippedFor(Id(2)));
            Assert.That(kit.MaxHealth, Is.EqualTo(baseHealth + 20), "four equip/unequip cycles added nothing extra");
        }

        [UnityTest]
        public IEnumerator UnsearchedContainerContents_NeverAppearInAnyText_UntilSearched()
        {
            yield return rig.Generate(12345);
            var container = rig.Director.Current.LootContainers[0];
            modal.Open(container, rig.Director.Friendlies[0]);
            yield return null;
            yield return null;

            Assert.That(modal.ContainerRowCount, Is.EqualTo(0), "unsearched: no contents are listed, not even a count");
            Assert.That(modal.ContainerRowTexts, Is.Empty);

            container.InitializeWith(container.Contents, searched: true);
            yield return null;
            yield return null;
            Assert.That(modal.ContainerRowCount, Is.EqualTo(container.Contents.Count));
            var names = container.Contents.Entries.Select(e => inventory.Catalogue.Find(e.DefinitionId).DisplayName).ToArray();
            foreach (var row in modal.ContainerRowTexts.Zip(names, (text, name) => (text, name)))
                Assert.That(row.text, Does.Contain(row.name));
        }

        [UnityTest]
        public IEnumerator SearchingAContainer_OpensTheLootView_ForTheSearchingUnit()
        {
            yield return rig.Generate(12345);
            yield return null;
            var container = rig.Director.Current.LootContainers[0];
            var unit = rig.Director.Friendlies[1];
            UnityEngine.AI.NavMesh.SamplePosition(container.Position, out var stand, 2f, UnityEngine.AI.NavMesh.AllAreas);
            unit.GetComponent<UnityEngine.AI.NavMeshAgent>().Warp(stand.position);
            yield return null;

            unit.Issue(new InteractCommand(container.Interactable));
            yield return TestWorld.WaitUntil(() => modal.IsOpen, 6f);

            Assert.That(modal.IsOpen, Is.True);
            Assert.That(modal.InspectedOperativeId, Is.EqualTo(Id(1)), "the loot view shows, and acts for, the operative who searched");
        }

        [UnityTest]
        public IEnumerator TheMissionEnding_OpensTheLoadout_WithTheResult()
        {
            yield return rig.Generate(12345);
            foreach (var unit in rig.Director.Friendlies)
                unit.GetComponent<Health>().TakeDamage(10000);
            yield return TestWorld.WaitUntil(() => modal.IsOpen, 5f);

            Assert.That(modal.IsOpen, Is.True);
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.Loadout));
            Assert.That(modal.ResultLine, Does.Contain("FAILURE"));
        }

        [UnityTest]
        public IEnumerator Deploy_StartsAMission_AndClosesThePanel()
        {
            modal.Open();
            yield return null;

            modal.Invoke(InventoryCommand.DeployNew);
            yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);

            Assert.That(modal.IsOpen, Is.False);
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.InMission));
        }

        [UnityTest]
        public IEnumerator EveryActionButton_IsAUiButton_WiredToItsCommand()
        {
            modal.Open();
            yield return null;

            foreach (InventoryCommand command in System.Enum.GetValues(typeof(InventoryCommand)))
                Assert.That(modal.ButtonFor(command) != null, Is.True, command.ToString());
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.InventoryModalPlayModeTests"` → `EXIT=1` (`InventoryModal` missing).

- [ ] **Step 3: Implement `InventoryModal`**

`Assets/_Project/Scripts/Hud/InventoryModal.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Blackglass
{
    public enum InventoryCommand
    {
        Equip, Unequip, ToStash, ToBag, Use, QueueUse, Take, QueueTake, TakeAll, QueueTakeAll, DeployNew, DeploySame, Close,
    }

    /// <summary>
    /// The inventory and loadout panel: the one place the game has controller focus (decision 043 amends 042 for this panel
    /// only). It owns a screen-space canvas above the HUD, opens with the Inventory/Toggle action or when a loot container
    /// opens or a mission ends, and while it is open the InputModalGate disables every gameplay action and the event system
    /// navigates with the UI map. It draws an InventoryView and sends InventoryRequests; it keeps no item state, and every
    /// request names the inspected operative. Opening never changes the time scale or the tactical pause.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InventoryModal : MonoBehaviour
    {
        const int SortingOrder = 20;
        const int MaxRows = 14;
        const float RowHeight = 34f;
        const float RefreshSeconds = 0.25f;

        [SerializeField] SquadInventory inventory;
        [SerializeField] SquadRoster roster;
        [SerializeField] MissionDirector director;
        [SerializeField] IntelligenceService intelligence;
        [SerializeField] InputActionAsset controls;
        [SerializeField] InputActionReference toggle;
        [SerializeField] ActiveInputDevice inputDevice;
        [SerializeField] bool openOnStart = true;

        readonly InventoryView view = new InventoryView();
        readonly InventoryViewSources sources = new InventoryViewSources();
        readonly Dictionary<GameObject, (InventoryListKind kind, int index)> rowOf = new Dictionary<GameObject, (InventoryListKind, int)>();
        readonly Dictionary<InventoryCommand, Button> commandButtons = new Dictionary<InventoryCommand, Button>();
        readonly List<Button> tabButtons = new List<Button>();
        readonly List<Text> tabLabels = new List<Text>();
        readonly List<GameObject> navigationOrder = new List<GameObject>();
        Canvas canvas;
        InputModalGate gate;
        InventoryRequests requests;
        RowList bagRows, stashRows, containerRows;
        Button[] slotButtons;
        Text[] slotLabels;
        Text titleLabel, resultLabel, detailTitle, detailBody, messageLabel, hintLabel, bagHeader, sideHeader, reasonLabel;
        string inspectedId = string.Empty;
        InventorySelection selection;
        LootContainer container;
        GeneratedMission trackedMission;
        readonly Dictionary<LootContainer, Action<CommandableUnit>> handlers = new Dictionary<LootContainer, Action<CommandableUnit>>();
        MissionPhase lastPhase;
        string resultLine = string.Empty;
        string message = string.Empty;
        UnitItems watchedItems;
        int watchedUses;
        float watchedSince;
        float nextRefresh;
        bool dirty = true;
        bool built;
        bool started;
        InputActionReference navigateRef, submitRef, cancelRef;

        public bool IsOpen { get; private set; }
        public string InspectedOperativeId => inspectedId;
        internal Canvas Canvas => canvas;
        internal string Message => message;
        internal string ResultLine => resultLine;
        internal int ContainerRowCount => containerRows != null ? containerRows.Count : 0;
        internal IEnumerable<string> ContainerRowTexts => containerRows != null ? containerRows.Texts() : new string[0];
        internal Button ButtonFor(InventoryCommand command) => commandButtons.TryGetValue(command, out var b) ? b : null;

        internal void Initialize(SquadInventory squadInventory, SquadRoster squad, MissionDirector missionDirector,
            IntelligenceService intel, InputActionAsset actions, InputActionReference toggleAction, ActiveInputDevice device,
            bool openOnStart)
        {
            inventory = squadInventory;
            roster = squad;
            director = missionDirector;
            intelligence = intel;
            controls = actions;
            toggle = toggleAction;
            inputDevice = device;
            this.openOnStart = openOnStart;
        }

        // ---- lifecycle ----

        void OnEnable()
        {
            Build();
            InputActionUtility.SetEnabled(true, toggle);
            if (inventory != null)
                inventory.Core.Changed += MarkDirty;
            requests = new InventoryRequests(inventory, director);
            sources.inventory = inventory;
            sources.roster = roster;
            sources.director = director;
            sources.intelligence = intelligence;
        }

        void OnDisable()
        {
            if (inventory != null && inventory.Core != null)
                inventory.Core.Changed -= MarkDirty;
            Unsubscribe();
            Close();
        }

        void MarkDirty() => dirty = true;

        void Update()
        {
            if (!built || inventory == null)
                return;
            if (!started)
            {
                started = true;
                lastPhase = director != null ? director.Phase : MissionPhase.Inactive;
                if (openOnStart && inventory.StartInLoadout && director != null && director.State == MissionState.Idle)
                    Open();
            }
            TrackMission();
            if (toggle != null && toggle.action != null && toggle.action.WasPressedThisFrame())
                Toggle();
            if (!IsOpen)
                return;
            gate.Enforce();
            ReadUiInput();
            if (!IsOpen)
                return;
            FollowFocus();
            WatchOutcome();
            if (dirty || Time.unscaledTime >= nextRefresh)
                Refresh();
        }

        // ---- opening and closing ----

        public void Toggle()
        {
            if (IsOpen)
                Close();
            else
                Open();
        }

        public void Open()
        {
            if (IsOpen || !built || inventory == null)
                return;
            gate.Open();
            EnableNavigation(true);
            canvas.gameObject.SetActive(true);
            IsOpen = true;
            dirty = true;
            Refresh();
            FocusFirst();
        }

        /// <summary>Opens the panel on a searched container, for (and inspecting) the operative who searched it.</summary>
        public void Open(LootContainer opened, CommandableUnit by)
        {
            container = opened;
            if (by != null && by.TryGetComponent<UnitIdentity>(out var identity) && !string.IsNullOrEmpty(identity.OperativeId))
                inspectedId = identity.OperativeId;
            selection = default;
            Open();
            dirty = true;
        }

        public void Close()
        {
            if (!IsOpen)
                return;
            EnableNavigation(false);
            gate.Close();
            if (canvas != null)
                canvas.gameObject.SetActive(false);
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);
            IsOpen = false;
        }

        internal void InspectOperative(string operativeId)
        {
            inspectedId = operativeId;
            selection = default;
            message = string.Empty;
            dirty = true;
        }

        // ---- mission tracking ----

        void TrackMission()
        {
            if (director == null)
                return;
            var mission = director.Current;
            if (mission != trackedMission)
            {
                Unsubscribe();
                trackedMission = mission;
                container = null;
                if (mission != null)
                {
                    foreach (var loot in mission.LootContainers)
                    {
                        var opened = loot;
                        Action<CommandableUnit> handler = unit => OnContainerOpened(opened, unit);
                        handlers[opened] = handler;
                        opened.OpenRequested += handler;
                    }
                }
                dirty = true;
            }
            var phase = director.Phase;
            if (phase != lastPhase)
            {
                lastPhase = phase;
                if (phase == MissionPhase.Success || phase == MissionPhase.Failure)
                {
                    resultLine = phase == MissionPhase.Success ? "MISSION SUCCESS - loot secured" : "MISSION FAILURE - pickups lost";
                    container = null;
                    selection = default;
                    Open();
                    dirty = true;
                }
                else if (phase == MissionPhase.Active)
                {
                    resultLine = string.Empty;
                }
            }
        }

        void Unsubscribe()
        {
            foreach (var pair in handlers)
            {
                if (pair.Key != null)
                    pair.Key.OpenRequested -= pair.Value;
            }
            handlers.Clear();
            trackedMission = null;
        }

        void OnContainerOpened(LootContainer opened, CommandableUnit by)
        {
            if (opened != null && by != null)
                Open(opened, by);
        }

        // ---- UI input (polled actions; navigation itself is the event system's) ----

        void ReadUiInput()
        {
            if (controls == null)
                return;
            if (controls.FindAction("UI/Cancel").WasPressedThisFrame())
            {
                Close();
                return;
            }
            if (controls.FindAction("UI/PreviousTab").WasPressedThisFrame())
                StepTab(-1);
            else if (controls.FindAction("UI/NextTab").WasPressedThisFrame())
                StepTab(1);
        }

        void StepTab(int direction)
        {
            if (view.Tabs.Count == 0)
                return;
            var current = view.Tabs.FindIndex(t => t.IsInspected);
            var next = (current + direction + view.Tabs.Count) % view.Tabs.Count;
            InspectOperative(view.Tabs[next].OperativeId);
        }

        void EnableNavigation(bool on)
        {
            var system = EventSystem.current;
            if (system == null || !system.TryGetComponent<InputSystemUIInputModule>(out var module) || controls == null)
                return;
            if (on)
            {
                navigateRef = InputActionReference.Create(controls.FindAction("UI/Navigate"));
                submitRef = InputActionReference.Create(controls.FindAction("UI/Submit"));
                cancelRef = InputActionReference.Create(controls.FindAction("UI/Cancel"));
                module.move = navigateRef;
                module.submit = submitRef;
                module.cancel = cancelRef;
            }
            else
            {
                PointerOnlyInputModule.StripNavigation(module);
                foreach (var reference in new[] { navigateRef, submitRef, cancelRef })
                {
                    if (reference != null)
                        Destroy(reference);
                }
                navigateRef = submitRef = cancelRef = null;
            }
        }

        // The selection follows the focused row, so a pad moves through a list and then across to the action buttons.
        void FollowFocus()
        {
            var system = EventSystem.current;
            if (system == null)
                return;
            var focused = system.currentSelectedGameObject;
            if (focused == null || !focused.activeInHierarchy)
            {
                FocusFirst();
                return;
            }
            if (rowOf.TryGetValue(focused, out var row))
            {
                var id = IdAt(row.kind, row.index);
                if (!string.IsNullOrEmpty(id) && (selection.Kind != row.kind || selection.InstanceId != id))
                    Select(row.kind, id);
            }
        }

        void FocusFirst()
        {
            var system = EventSystem.current;
            if (system == null)
                return;
            foreach (var go in navigationOrder)
            {
                if (go != null && go.activeInHierarchy && go.TryGetComponent<Button>(out var button) && button.interactable)
                {
                    system.SetSelectedGameObject(go);
                    return;
                }
            }
        }

        // ---- selection and commands ----

        internal void Select(InventoryListKind kind, string instanceId)
        {
            selection = new InventorySelection(kind, instanceId);
            message = string.Empty;
            dirty = true;
        }

        string IdAt(InventoryListKind kind, int index)
        {
            switch (kind)
            {
                case InventoryListKind.Bag: return index < view.Bag.Count ? view.Bag[index].InstanceId : null;
                case InventoryListKind.Stash: return index < view.Stash.Count ? view.Stash[index].InstanceId : null;
                case InventoryListKind.Container: return index < view.Container.Count ? view.Container[index].InstanceId : null;
                case InventoryListKind.Slot: return index < view.Slots.Length && !string.IsNullOrEmpty(view.Slots[index].InstanceId) ? view.Slots[index].InstanceId : null;
                default: return null;
            }
        }

        internal void Invoke(InventoryCommand command)
        {
            if (inventory == null)
                return;
            Refresh();   // act on what is on screen now, never on a stale view
            var id = view.InspectedId;
            var picked = view.Selected.InstanceId;
            switch (command)
            {
                case InventoryCommand.Close:
                    Close();
                    return;
                case InventoryCommand.DeployNew:
                case InventoryCommand.DeploySame:
                {
                    var result = command == InventoryCommand.DeployNew ? requests.DeployNew() : requests.DeploySame();
                    message = result.Message;
                    if (result.Ok)
                        Close();
                    break;
                }
                case InventoryCommand.Equip:
                    message = requests.Equip(id, picked).Message;
                    break;
                case InventoryCommand.Unequip:
                    message = view.Selected.Kind == InventoryListKind.None ? "Select an item." : RequestUnequip(id, picked);
                    break;
                case InventoryCommand.ToStash:
                    message = requests.ToStash(id, picked).Describe(view.DetailTitle);
                    break;
                case InventoryCommand.ToBag:
                    message = requests.FromStash(id, picked).Describe(view.DetailTitle);
                    break;
                case InventoryCommand.Use:
                case InventoryCommand.QueueUse:
                {
                    var result = requests.Use(id, picked, queue: command == InventoryCommand.QueueUse);
                    message = result.Message;
                    if (result.Ok)
                        Watch(id);
                    break;
                }
                case InventoryCommand.Take:
                case InventoryCommand.QueueTake:
                {
                    var result = requests.Take(id, container, picked, queue: command == InventoryCommand.QueueTake);
                    message = result.Message;
                    if (result.Ok)
                        Watch(id);
                    break;
                }
                case InventoryCommand.TakeAll:
                case InventoryCommand.QueueTakeAll:
                {
                    var result = requests.TakeAll(id, container, queue: command == InventoryCommand.QueueTakeAll);
                    message = result.Message;
                    if (result.Ok)
                        Watch(id);
                    break;
                }
            }
            dirty = true;
        }

        string RequestUnequip(string operativeId, string instanceId)
        {
            foreach (var slot in view.Slots)
            {
                if (slot.InstanceId == instanceId && !string.IsNullOrEmpty(instanceId))
                    return requests.Unequip(operativeId, slot.Slot).Message;
            }
            return "Select an equipped item.";
        }

        // After an accepted order, the result of the running order is read back here (it runs on simulation time).
        void Watch(string operativeId)
        {
            var unit = InventoryViewBuilder.FindUnit(director, operativeId);
            watchedItems = unit != null ? unit.GetComponent<UnitItems>() : null;
            watchedSince = Time.unscaledTime;
            watchedUses = watchedItems != null ? watchedItems.UsedCount : 0;
        }

        void WatchOutcome()
        {
            if (watchedItems == null)
                return;
            if (watchedItems.LastCollectTime > watchedSince)
            {
                message = watchedItems.LastCollect.Describe();
                watchedItems = null;
                dirty = true;
            }
            else if (watchedItems.UsedCount > watchedUses)
            {
                message = "Medkit used.";
                watchedItems = null;
                dirty = true;
            }
            else if (watchedItems.LastUseFailureTime > watchedSince)
            {
                message = InventoryViewBuilder.UseReason(watchedItems.LastUseFailure);
                watchedItems = null;
                dirty = true;
            }
        }

        // ---- drawing ----

        void Refresh()
        {
            dirty = false;
            nextRefresh = Time.unscaledTime + RefreshSeconds;
            sources.container = container;
            InventoryViewBuilder.Build(sources, inspectedId, selection, view);
            if (!view.HasInventory)
                return;
            inspectedId = view.InspectedId;
            if (view.Selected.IsNone)
                selection = default;

            HudFactory.SetText(titleLabel, view.Header + (view.InspectedName.Length > 0 ? "   -   " + view.InspectedName.ToUpperInvariant() : string.Empty));
            HudFactory.SetText(resultLabel, resultLine);
            ApplyTabs();
            for (var i = 0; i < slotButtons.Length; i++)
            {
                HudFactory.SetText(slotLabels[i], view.Slots[i].Title + ": " + view.Slots[i].ItemLabel);
                Tint(slotButtons[i], selection.Kind == InventoryListKind.Slot && !string.IsNullOrEmpty(view.Slots[i].InstanceId) && selection.InstanceId == view.Slots[i].InstanceId);
                slotButtons[i].interactable = !string.IsNullOrEmpty(view.Slots[i].InstanceId);
            }
            HudFactory.SetText(bagHeader, $"BAG  {view.Bag.Count}/{view.BagCapacity}");
            bagRows.Apply(view.Bag, selection, InventoryListKind.Bag);
            var showStash = view.Mode == InventoryMode.Loadout;
            var showContainer = view.HasContainer;
            HudFactory.SetText(sideHeader, showContainer ? "CONTAINER" : showStash ? "STASH" : string.Empty);
            stashRows.Apply(showStash && !showContainer ? view.Stash : EmptyRows, selection, InventoryListKind.Stash);
            containerRows.Apply(showContainer ? view.Container : EmptyRows, selection, InventoryListKind.Container);
            HudFactory.SetText(detailTitle, view.DetailTitle);
            HudFactory.SetText(detailBody, view.DetailBody);

            ApplyCommand(InventoryCommand.Equip, view.Equip, "Equip");
            ApplyCommand(InventoryCommand.Unequip, view.Unequip, "Unequip");
            ApplyCommand(InventoryCommand.ToStash, view.ToStash, "Move to stash");
            ApplyCommand(InventoryCommand.ToBag, view.ToBag, "Move to bag");
            ApplyCommand(InventoryCommand.Use, view.Use, "Use now");
            ApplyCommand(InventoryCommand.QueueUse, view.QueueUse, "Queue use");
            ApplyCommand(InventoryCommand.Take, view.Take, "Take now");
            ApplyCommand(InventoryCommand.QueueTake, view.QueueTake, "Queue take");
            ApplyCommand(InventoryCommand.TakeAll, view.TakeAll, "Take what fits");
            ApplyCommand(InventoryCommand.QueueTakeAll, view.QueueTakeAll, "Queue take what fits");
            var loadoutMode = view.Mode == InventoryMode.Loadout;
            ShowCommand(InventoryCommand.DeployNew, loadoutMode);
            ShowCommand(InventoryCommand.DeploySame, loadoutMode);
            HudFactory.SetText(reasonLabel, FirstReason());
            HudFactory.SetText(messageLabel, message);
            HudFactory.SetText(hintLabel, Hint());
        }

        static readonly List<InventoryRow> EmptyRows = new List<InventoryRow>();

        void ApplyTabs()
        {
            for (var i = 0; i < tabButtons.Count; i++)
            {
                var on = i < view.Tabs.Count;
                HudFactory.SetActive(tabButtons[i].gameObject, on);
                if (!on)
                    continue;
                var tab = view.Tabs[i];
                HudFactory.SetText(tabLabels[i], tab.Name + (tab.IsDown ? " (down)" : string.Empty));
                Tint(tabButtons[i], tab.IsInspected);
            }
        }

        void ApplyCommand(InventoryCommand command, InventoryAction action, string label)
        {
            var button = commandButtons[command];
            HudFactory.SetActive(button.gameObject, action.Visible);
            if (!action.Visible)
                return;
            button.interactable = action.Enabled;
            HudFactory.SetText(button.GetComponentInChildren<Text>(), label);
        }

        void ShowCommand(InventoryCommand command, bool visible)
        {
            var button = commandButtons[command];
            HudFactory.SetActive(button.gameObject, visible);
            if (visible)
                button.interactable = director == null || director.State != MissionState.Generating;
        }

        string FirstReason()
        {
            foreach (var action in new[] { view.Equip, view.Unequip, view.ToStash, view.ToBag, view.Use, view.Take, view.TakeAll })
            {
                if (action.Visible && !action.Enabled && action.Reason.Length > 0)
                    return action.Reason;
            }
            return string.Empty;
        }

        string Hint()
        {
            if (controls == null)
                return string.Empty;
            var family = inputDevice != null ? inputDevice.Family : InputFamily.KeyboardMouse;
            return $"Close: {PromptResolver.GetPrompt(toggle != null ? toggle.action : null, family)} / {PromptResolver.GetPrompt(controls.FindAction("UI/Cancel"), family)}"
                + $"    Operative: {PromptResolver.GetPrompt(controls.FindAction("UI/PreviousTab"), family)} / {PromptResolver.GetPrompt(controls.FindAction("UI/NextTab"), family)}"
                + $"    Choose: {PromptResolver.GetPrompt(controls.FindAction("UI/Submit"), family)}";
        }

        static void Tint(Button button, bool selected)
        {
            var image = button.targetGraphic as Image;
            if (image != null)
                HudFactory.SetColor(image, selected ? new Color(HudTheme.Accent.r, HudTheme.Accent.g, HudTheme.Accent.b, 0.55f) : HudTheme.Panel);
        }

        // ---- construction ----

        void Build()
        {
            if (built)
                return;
            gate = new InputModalGate(controls, "UI", "Inventory");
            var canvasObject = new GameObject("InventoryCanvas", typeof(RectTransform));
            canvasObject.transform.SetParent(transform, false);
            canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            canvasObject.AddComponent<GraphicRaycaster>();
            var root = (RectTransform)canvasObject.transform;

            HudFactory.Box("Dim", root, new Color(0f, 0f, 0f, 0.6f), true);
            var panel = HudFactory.Box("Panel", root, new Color(HudTheme.Panel.r, HudTheme.Panel.g, HudTheme.Panel.b, 0.96f), true);
            HudFactory.Place(panel, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1500f, 900f));

            titleLabel = Label(panel, "Title", HudTheme.FontBanner, HudTheme.Text, 24f, 16f, 1000f, 44f);
            resultLabel = Label(panel, "Result", HudTheme.FontLarge, HudTheme.Warn, 24f, 62f, 1000f, 30f);
            for (var i = 0; i < 6; i++)
            {
                var button = MakeButton(panel, "Tab" + i, 24f + i * 230f, 100f, 220f, 40f, out var text);
                tabButtons.Add(button);
                tabLabels.Add(text);
                var index = i;
                button.onClick.AddListener(() =>
                {
                    if (index < view.Tabs.Count)
                        InspectOperative(view.Tabs[index].OperativeId);
                });
                navigationOrder.Add(button.gameObject);
            }

            slotButtons = new Button[3];
            slotLabels = new Text[3];
            for (var i = 0; i < 3; i++)
            {
                slotButtons[i] = MakeButton(panel, "Slot" + i, 24f, 160f + i * 44f, 420f, 38f, out slotLabels[i]);
                var index = i;
                slotButtons[i].onClick.AddListener(() => Select(InventoryListKind.Slot, IdAt(InventoryListKind.Slot, index) ?? string.Empty));
                rowOf[slotButtons[i].gameObject] = (InventoryListKind.Slot, i);
                navigationOrder.Add(slotButtons[i].gameObject);
            }
            bagHeader = Label(panel, "BagHeader", HudTheme.FontBody, HudTheme.TextDim, 24f, 300f, 420f, 24f);
            bagRows = new RowList(this, panel, "Bag", InventoryListKind.Bag, 24f, 330f, 420f);
            sideHeader = Label(panel, "SideHeader", HudTheme.FontBody, HudTheme.TextDim, 470f, 160f, 420f, 24f);
            stashRows = new RowList(this, panel, "Stash", InventoryListKind.Stash, 470f, 190f, 420f);
            containerRows = new RowList(this, panel, "Container", InventoryListKind.Container, 470f, 190f, 420f);

            detailTitle = Label(panel, "DetailTitle", HudTheme.FontLarge, HudTheme.Text, 920f, 160f, 560f, 32f);
            detailBody = Label(panel, "DetailBody", HudTheme.FontBody, HudTheme.Text, 920f, 198f, 560f, 210f);
            detailBody.verticalOverflow = VerticalWrapMode.Truncate;
            reasonLabel = Label(panel, "Reason", HudTheme.FontBody, HudTheme.Warn, 920f, 414f, 560f, 40f);

            var order = new[]
            {
                InventoryCommand.Equip, InventoryCommand.Unequip, InventoryCommand.ToStash, InventoryCommand.ToBag,
                InventoryCommand.Use, InventoryCommand.QueueUse, InventoryCommand.Take, InventoryCommand.QueueTake,
                InventoryCommand.TakeAll, InventoryCommand.QueueTakeAll,
            };
            for (var i = 0; i < order.Length; i++)
                AddCommand(panel, order[i], 920f + (i % 2) * 285f, 462f + (i / 2) * 46f, 275f, 40f);
            AddCommand(panel, InventoryCommand.DeployNew, 24f, 800f, 300f, 48f);
            AddCommand(panel, InventoryCommand.DeploySame, 334f, 800f, 300f, 48f);
            AddCommand(panel, InventoryCommand.Close, 1176f, 800f, 300f, 48f);
            HudFactory.SetText(commandButtons[InventoryCommand.DeployNew].GetComponentInChildren<Text>(), "Deploy (new seed)");
            HudFactory.SetText(commandButtons[InventoryCommand.DeploySame].GetComponentInChildren<Text>(), "Deploy (same seed)");
            HudFactory.SetText(commandButtons[InventoryCommand.Close].GetComponentInChildren<Text>(), "Close");

            messageLabel = Label(panel, "Message", HudTheme.FontLarge, HudTheme.Good, 24f, 750f, 1450f, 34f);
            hintLabel = Label(panel, "Hint", HudTheme.FontSmall, HudTheme.TextDim, 24f, 860f, 1450f, 24f);
            canvas.gameObject.SetActive(false);
            built = true;
        }

        void AddCommand(RectTransform parent, InventoryCommand command, float x, float y, float width, float height)
        {
            var button = MakeButton(parent, command.ToString(), x, y, width, height, out _);
            commandButtons[command] = button;
            button.onClick.AddListener(() => Invoke(command));
            navigationOrder.Add(button.gameObject);
        }

        Button MakeButton(RectTransform parent, string name, float x, float y, float width, float height, out Text label)
        {
            var rect = HudFactory.Box(name, parent, HudTheme.Panel, true);
            HudFactory.Place(rect, new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(width, height));
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = rect.GetComponent<Image>();
            label = HudFactory.Label("Label", rect, HudTheme.FontBody, HudTheme.Text, TextAnchor.MiddleLeft);
            HudFactory.Place(label.rectTransform, new Vector2(0f, 0.5f), new Vector2(10f, 0f), new Vector2(width - 20f, height));
            return button;
        }

        static Text Label(RectTransform parent, string name, int size, Color color, float x, float y, float width, float height)
        {
            var label = HudFactory.Label(name, parent, size, color, TextAnchor.UpperLeft);
            HudFactory.Place(label.rectTransform, new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(width, height));
            return label;
        }

        /// <summary>A pooled column of row buttons; a row shows "[E] name xN  tag", and the selected row is tinted.</summary>
        sealed class RowList
        {
            readonly InventoryModal owner;
            readonly Button[] buttons = new Button[MaxRows];
            readonly Text[] labels = new Text[MaxRows];

            public RowList(InventoryModal owner, RectTransform parent, string name, InventoryListKind kind, float x, float y, float width)
            {
                this.owner = owner;
                for (var i = 0; i < MaxRows; i++)
                {
                    buttons[i] = owner.MakeButton(parent, name + "Row" + i, x, y + i * RowHeight, width, RowHeight - 2f, out labels[i]);
                    var index = i;
                    var listKind = kind;
                    buttons[i].onClick.AddListener(() =>
                    {
                        var id = owner.IdAt(listKind, index);
                        if (!string.IsNullOrEmpty(id))
                            owner.Select(listKind, id);
                    });
                    owner.rowOf[buttons[i].gameObject] = (kind, i);
                    owner.navigationOrder.Add(buttons[i].gameObject);
                    buttons[i].gameObject.SetActive(false);
                }
            }

            public int Count { get; private set; }

            public IEnumerable<string> Texts()
            {
                for (var i = 0; i < Count; i++)
                    yield return labels[i].text;
            }

            public void Apply(List<InventoryRow> rows, InventorySelection selection, InventoryListKind kind)
            {
                Count = Mathf.Min(rows.Count, MaxRows);
                for (var i = 0; i < MaxRows; i++)
                {
                    var on = i < Count;
                    HudFactory.SetActive(buttons[i].gameObject, on);
                    if (!on)
                        continue;
                    var row = rows[i];
                    var text = (row.IsEquipped ? "[E] " : string.Empty) + row.Label + (row.Quantity > 1 ? " x" + row.Quantity : string.Empty)
                        + (row.Tag.Length > 0 ? "   " + row.Tag : string.Empty);
                    HudFactory.SetText(labels[i], text);
                    Tint(buttons[i], selection.Kind == kind && selection.InstanceId == row.InstanceId);
                }
            }
        }
    }
}
```

Implementation notes for the engineer: `modal.Open(container, unit)` is an overload of `Open()`; the `Open(LootContainer, CommandableUnit)` test call and the `OnContainerOpened` handler use it. `OnContainerOpened` receives only the unit; `FindOpened` picks the container that has just completed (highest `CompletionCount` with a `LastUser`); when two containers could match, prefer the one within `Interactable.Range` of the unit — add that tie-break if the test with several containers flakes. `PointerOnlyInputModule.StripNavigation` takes an `InputSystemUIInputModule`; the `EventSystem` in tests may have no module (then `EnableNavigation` returns early and keyboard-free tests still pass).

- [ ] **Step 4: Run to verify it passes**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.InventoryModalPlayModeTests"` → `EXIT=0`, 10 passed. Then `Tools/run-tests.sh PlayMode "Blackglass.Tests.Hud"` and `"Blackglass.Tests.InputModalGatePlayModeTests"` → `EXIT=0`.

---

### Task 19: Scene wiring and the deploy flow

**Files:**
- Create: `Assets/_Project/Editor/InventorySceneBuilder.cs`
- Modify: `Assets/_Project/Scenes/ProceduralMission.unity` (by the builder), `Assets/_Project/Tests/PlayMode/TestSupport/TestWorld.cs` (deploy helper), the five scene tests that load `ProceduralMission`
- Test: `Assets/_Project/Tests/PlayMode/InventorySceneTests.cs`

**Interfaces:**
- Consumes: T4 (`SquadInventory`), T12 (`MissionDirector.lootTable`, `MissionSystems.inventory`), T18 (`InventoryModal`), the existing scene objects (`MissionDirector`, `SquadRoster`, `IntelligenceService`, `ActiveInputDevice`, the `TacticalHud` sources' controls asset).
- Produces: `Blackglass.EditorTools.InventorySceneBuilder.Wire()` / `WireFromCommandLine()`; `TestWorld.DeployFromLoadout(MissionDirector)`.

- [ ] **Step 1: Write the failing scene test**

`Assets/_Project/Tests/PlayMode/InventorySceneTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class InventorySceneTests
    {
        MissionDirector director;
        SquadInventory inventory;
        InventoryModal modal;

        IEnumerator Load()
        {
            var loading = SceneManager.LoadSceneAsync("ProceduralMission", LoadSceneMode.Single);
            while (!loading.isDone)
                yield return null;
            yield return null;
            director = Object.FindFirstObjectByType<MissionDirector>();
            inventory = Object.FindFirstObjectByType<SquadInventory>();
            modal = Object.FindFirstObjectByType<InventoryModal>();
        }

        [UnityTest]
        public IEnumerator TheScene_OpensOnTheLoadout_WithoutGeneratingAMission()
        {
            yield return Load();
            yield return new WaitForSecondsRealtime(1f);

            Assert.That(inventory != null && modal != null, Is.True, "the scene has the inventory and the panel");
            Assert.That(director.State, Is.EqualTo(MissionState.Idle), "no mission until Deploy");
            Assert.That(modal.IsOpen, Is.True);
            Assert.That(inventory.StartInLoadout, Is.True);
        }

        [UnityTest]
        public IEnumerator Deploy_GeneratesAMission_WithEquippedUnits_ItemsAndContainers()
        {
            yield return Load();
            yield return null;

            modal.Invoke(InventoryCommand.DeployNew);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 30f);

            Assert.That(director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", director.Report.Failures));
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.InMission));
            foreach (var unit in director.Friendlies)
            {
                Assert.That(unit.TryGetComponent<UnitItems>(out _), Is.True, unit.name);
                Assert.That(unit.GetComponent<UnitIdentity>().Inventory == inventory, Is.True);
                Assert.That(unit.GetComponent<UnitAttacker>().HasWeapon, Is.True, "starter weapons are equipped");
            }
            Assert.That(director.Report.LootRequested, Is.GreaterThan(0));
            Assert.That(modal.IsOpen, Is.False);
        }

        [UnityTest]
        public IEnumerator Regeneration_KeepsTheStarterItemsOnce()
        {
            yield return Load();
            yield return null;
            var medkits = inventory.Core.Session.Stash.CountOf("item.medkit");

            modal.Invoke(InventoryCommand.DeployNew);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready, 30f);
            modal.Invoke(InventoryCommand.Close);
            director.RegenerateSame();
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready, 30f);

            Assert.That(inventory.Core.Session.Stash.CountOf("item.medkit"), Is.EqualTo(medkits));
        }
    }
}
#endif
```

- [ ] **Step 2: Add the deploy helper and update the five scene tests**

`TestWorld.cs` — add:

```csharp
        /// <summary>
        /// The ProceduralMission scene opens on the loadout panel and waits for Deploy (decision 043). Scene tests that need
        /// a mission call this after loading: it generates a new-seed mission if the director is still waiting.
        /// </summary>
        public static IEnumerator DeployFromLoadout(MissionDirector director)
        {
            yield return null;
            yield return null;
            if (director != null && director.State == MissionState.Idle)
                director.GenerateNew();
        }
```

In `ProceduralMissionSceneTests`, `HudSceneTests`, `HudFogSweepTests`, `IntelligenceSceneTests`, `MissionObjectivesSceneTests`: after each `LoadSceneAsync("ProceduralMission", …)` completes and the director is found, and **before** the existing wait for `Ready`, add `yield return TestWorld.DeployFromLoadout(director);` (use the variable name each test uses for the director). Where a test already calls `director.Generate(seed)` itself, the helper's `GenerateNew` is harmless but wasteful: skip the helper there. Do not change any assertion.

- [ ] **Step 3: Write the scene builder**

`Assets/_Project/Editor/InventorySceneBuilder.cs`:

```csharp
#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Wires the inventory into the ProceduralMission scene: the SquadInventory (catalogue, starter loadout, roster, director),
    /// the loot table on the director, MissionSystems.inventory, and the InventoryModal with its toggle action and the scene's
    /// input device and intelligence service. Idempotent: objects already there are reused and re-pointed. The Prototype scene
    /// is left alone (it keeps its capsule units and the old flow).
    /// </summary>
    public static class InventorySceneBuilder
    {
        const string ScenePath = "Assets/_Project/Scenes/ProceduralMission.unity";
        const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        [MenuItem("Blackglass/Inventory/Wire ProceduralMission Scene")]
        public static void WireMenu() => Wire();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void WireFromCommandLine() => Wire();

        public static void Wire()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = FindOne<MissionDirector>();
            var roster = FindOne<SquadRoster>();
            var catalogue = Load<ItemCatalogue>(InventoryDataBuilder.CataloguePath);
            var starter = Load<StarterLoadout>(InventoryDataBuilder.StarterPath);
            var loot = Load<LootTable>(InventoryDataBuilder.LootTablePath);
            var controls = Load<InputActionAsset>(ControlsPath);
            var intelligence = UnityEngine.Object.FindFirstObjectByType<IntelligenceService>();
            var device = UnityEngine.Object.FindFirstObjectByType<ActiveInputDevice>();

            var inventory = FindOrAdd<SquadInventory>("Inventory");
            var inventoryObject = new SerializedObject(inventory);
            Set(inventoryObject, "catalogue", catalogue);
            Set(inventoryObject, "starter", starter);
            Set(inventoryObject, "roster", roster);
            Set(inventoryObject, "director", director);
            inventoryObject.FindProperty("bagCapacity").intValue = 8;
            inventoryObject.FindProperty("startInLoadout").boolValue = true;
            inventoryObject.ApplyModifiedPropertiesWithoutUndo();

            var directorObject = new SerializedObject(director);
            Set(directorObject, "systems.inventory", inventory);
            Set(directorObject, "lootTable", loot);
            directorObject.ApplyModifiedPropertiesWithoutUndo();

            var modal = FindOrAdd<InventoryModal>("InventoryModal");
            var modalObject = new SerializedObject(modal);
            Set(modalObject, "inventory", inventory);
            Set(modalObject, "roster", roster);
            Set(modalObject, "director", director);
            Set(modalObject, "intelligence", intelligence);
            Set(modalObject, "controls", controls);
            Set(modalObject, "toggle", ActionReference("Inventory", "Toggle"));
            Set(modalObject, "inputDevice", device);
            modalObject.FindProperty("openOnStart").boolValue = true;
            modalObject.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        static T FindOne<T>() where T : Component
        {
            var found = UnityEngine.Object.FindFirstObjectByType<T>();
            if (found == null)
                throw new InvalidOperationException($"ProceduralMission needs a {typeof(T).Name}.");
            return found;
        }

        static T FindOrAdd<T>(string objectName) where T : Component
        {
            var existing = UnityEngine.Object.FindFirstObjectByType<T>();
            return existing != null ? existing : new GameObject(objectName).AddComponent<T>();
        }

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidOperationException("Missing asset " + path + ". Run Blackglass/Inventory/Create Prototype Items first.");
            return asset;
        }

        static void Set(SerializedObject serialized, string path, UnityEngine.Object value)
        {
            var property = serialized.FindProperty(path);
            if (property == null)
                throw new InvalidOperationException($"No serialized field '{path}' on {serialized.targetObject.name}.");
            property.objectReferenceValue = value;
        }

        static InputActionReference ActionReference(string map, string action)
        {
            var reference = AssetDatabase.LoadAllAssetsAtPath(ControlsPath).OfType<InputActionReference>()
                .FirstOrDefault(r => r.action != null && r.action.actionMap.name == map && r.action.name == action);
            if (reference == null)
                throw new InvalidOperationException($"No InputActionReference for '{map}/{action}' in {ControlsPath}.");
            return reference;
        }
    }
}
#endif
```

`InventoryDataBuilder.CataloguePath`, `StarterPath` and `LootTablePath` are `public const` (Tasks 7 and 11). If the Input Actions importer did not generate an `InputActionReference` sub-asset for the new map after the patch, open the asset once in batch mode via `AssetDatabase.ImportAsset(ControlsPath)` in `Wire()` before looking it up.

- [ ] **Step 4: Run the builder and the tests**

```bash
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -quit -projectPath "$(pwd -W)" \
  -executeMethod Blackglass.EditorTools.InventorySceneBuilder.WireFromCommandLine -logFile Logs/InventorySceneBuilder.log; echo EXIT=$?
Tools/run-tests.sh PlayMode "Blackglass.Tests.InventorySceneTests"
```

Expected: builder `EXIT=0`, tests `EXIT=0`, 3 passed. Run the builder a second time: `git diff --stat Assets/_Project/Scenes/ProceduralMission.unity` must not change (idempotent). Then run the five scene test classes (`ProceduralMissionSceneTests`, `HudSceneTests`, `HudFogSweepTests`, `IntelligenceSceneTests`, `MissionObjectivesSceneTests`) → `EXIT=0`.

---

### Task 20: Regression, documentation, final report

**Files:**
- Modify: `docs/Decisions.md` (append decision 043, amend 024/042/033/036 with one-line pointers), `CLAUDE.md` is **not** touched
- Create: `docs/Phase12-ManualTests.md`
- Test: `Assets/_Project/Tests/PlayMode/HudFogSweepTests.cs` (extend)

- [ ] **Step 1: Extend the HUD fog sweep**

In `HudFogSweepTests` add one test, in that file's style, which generates a Blind-preset mission with the inventory and loot table, opens the modal on an **unsearched** container (`modal.Open(container, unit)`), and asserts that no `Text` under both the HUD canvas and the modal canvas contains any of the container's item display names or any `DefinitionId`, with the truth view on and off; then marks the container searched and asserts the names now appear in the modal only (never in the HUD canvas). Reuse the file's helper that gathers all HUD text.

- [ ] **Step 2: Write the decision record**

Append to `docs/Decisions.md` (same format as 042; fill every bullet from what was built, keeping these points):

```markdown
## 043 — Inventory, equipment and loot: session and working state, equipment-driven configuration, containers as interactables, a scoped modal

- **Decided (Phase 12, 2026-10-10):**
  - **Data.** `ItemDefinition` (ScriptableObject, immutable: stable id, category, slot, stack size, `StatModifiers`, a `CombatArchetype` for weapons, a heal amount) is separate from the owned `ItemEntry` (instance id, definition id, quantity), which lives in an `ItemInventory` (entry limit; bag 8, stash uncapped). An operative's `OperativeLoadout` is a bag plus three slots (Weapon, Armor, Utility) that hold the **instance id** of a bag entry; equipping creates no copy. `InventoryState` (stash + loadouts keyed by operative id) is plain serializable data. `ItemTransfer.Move` is the only way units change inventory: it removes from the source only what the destination accepted, so a failure never duplicates or destroys.
  - **Configuration.** `EffectiveConfiguration.Evaluate(definition, state, track, equipped)` recomputes from role, picks and equipped items every time. The equipped weapon's archetype replaces the definition's archetype (amends 036/025: with a loadout the weapon drives combat; `OperativeDefinition.Archetype` seeds the starter weapon and keeps units without a loadout unchanged). An empty Weapon slot disables the ordinary attack (`UnitAttacker.HasWeapon`, refused in `Issue` with `CommandRefusal.NoWeapon`); abilities are unaffected. Armor +20 max health and boots +0.5 m/s are modifiers, not an armor system.
  - **Session and working state.** `InventorySession` holds the persistent state and a deep copy per mission. `MissionDirector.InstanceId` is new for every generation (also the same seed). Success commits the working copy once; failure, abort, a failed generation and any restart discard it (a deliberately forgiving **prototype** policy, not the final extraction economy). Equipment edits and stash transfers are refused while a working state exists; tactical pause is in-mission. XP is untouched.
  - **Commands.** `UseItemCommand` and `CollectCommand` ride the existing queue like `AbilityCommand`/`InteractCommand` (validated on issue, again on execution, instant or walking, frozen by pause). No use interval: quantity is the limit. Collecting re-reads the source per entry, so two collectors cannot duplicate; leftovers stay in the container.
  - **Loot.** Containers are ordinary `MissionInteractable`s (so click, R, context Confirm, queueing, cursor snap and `Knowledge.CanInteract` apply unchanged) that search once and then open instantly (`SetRepeatable`). `LootPlacer` is pure, uses `SeededRandom.ForLoot` (tiles) and `ForLootContents` (items per container), so neither the layout nor container positions depend on the table's items. Placement shortfalls are reported, not fatal. Containers are solid and baked into the NavMesh like the terminal and checked by `ValidateLoot`.
  - **Fog.** A container's location follows the Phase 11 rule; its contents are visible only once searched (`LootKnowledge`).
  - **Modal.** `InventoryModal` is the one place with controller focus (amends 042 for this panel; binds the reserved Select/View/Create button, amends 024). `InputModalGate` disables every gameplay action while it is open and restores each exactly; the `UI` map and `PointerOnlyInputModule` navigation exist only for its lifetime. Opening never changes the time scale or the tactical pause. The scene now opens on the loadout panel and waits for Deploy; the panel reopens after Success or Failure.
- **Why / Rejected / Implications / Known limitations:** write these from the implementation: rejected a squad-wide mission pool, a server-style database, subclassing items per category, a use interval, remote transfers during a mission, and extending the HUD snapshot for the modal; implications for Phase 12.5 (combat presentation can read `UnitAttacker.HasWeapon` and `UnitWeaponVisual`) and Phase 14 (serialize `InventoryState` keyed by operative id and definition id); limitations: in memory only, Blade and Marksman visuals are placeholders, the modal is the only pad focus mode, the failure policy is not the final economy, containers are not themed, the Recon Scan reveals a container's location but never its contents, transfers between operatives happen only in loadout mode, the panel opens by itself when a mission ends (even if units are still alive).
```

Add one-line pointers: in decisions 024, 033, 036 and 042 append `Amended by 043 (Phase 12): …` with the specific change.

- [ ] **Step 3: Write the manual-test sheet**

`docs/Phase12-ManualTests.md`: one numbered list per the brief's section 18 checklist (equip different weapons on different operatives; shared definitions unchanged; repeated equip/unequip; empty Weapon slot; starter items not duplicating on F6/F7; weapon attachment and visual replacement; stack limits and a full bag; partial collection; two operatives taking the same item; medkit use, full-health rejection, cancel, insufficient quantity; queueing item use during tactical pause; inventory navigation without input leaking; loot under fog of war; same-seed reproducibility; extraction retaining loot once; failure/abort policy; duplicate completion; a second mission with retained equipment; runtime death not deleting identity or pre-owned equipment; keyboard/mouse and each physical controller family; regression of combat, abilities, progression, cover, objectives, extraction and HUD). Each item states the exact steps (keys: `I` inventory, `R` interact, `F6/F7` regenerate, `Space` pause) and the expected result, with a column "Done by" left empty. Controllers are listed as **not tested on hardware** except the DualSense layout the owner saw in Phase 6.5; the modal has been exercised with simulated devices only.

- [ ] **Step 4: Final verification**

```bash
Tools/run-tests.sh EditMode
Tools/run-tests.sh PlayMode
git status --short
```

Expected: both `EXIT=0`; totals equal the Task 0 baseline plus all tests added (count them from the run, do not guess); every test green at Task 0 is still green. Report: the two totals and how they were obtained; every existing test that had to be edited and why (the three baked-rifle asset tests, `SelectButton_IsReserved…`, the five scene tests' deploy call); the files changed (`git status --short`); and the manual checks still required, separated from the automated results.

- [ ] **Step 5: Completion report to the owner**

Report, in the order of the brief's section 18: files changed, data/ownership model, inventory and equipment rules, prototype items, combat/configuration integration, weapon visual integration, consumable behaviour, procedural loot strategy, mission settlement policy, HUD/input integration, fog safeguards, tests performed (automated vs. manual still required), remaining limitations, and concerns before Phase 12.5. State plainly that nothing was committed or pushed and that Play Mode, build and physical-controller checks that were not run are listed as manual.

---

## Self-Review (done while writing)

**Spec coverage** (spec section → task): 4 data → T1–T4; equipment slots/test content → T7; 5 configuration → T5, T6, T9; weapon visuals → T9; 6 loadout timing/lock → T4, T16, T18; consumables → T10; 9 commands/pause → T10, T13; 10 world loot → T12, T13; 11 determinism → T11, T12; 12 settlement → T4, T8, T20; 13 UI → T16–T18; 14 input → T15, T18; 15 fog → T12, T14, T16, T20; 16 cleanup → T8, T12, T19; 18 validation → every task plus T20. The HUD "no weapon" notice → T17. Deploy flow → T19.

**Placeholders:** the only deliberate open items are measured values the engineer must paste (T9 rifle geometry constants, the T11 full-placement ratio). Both come with a test that fails if left unmeasured or below the stated bar.

**Type consistency:** `InventorySession.Equip/Unequip/ToStash/FromStash` (T4) are what `InventoryRequests` calls (T16); `UnitItems.Check/TryUse/Record` (T10) and `CheckCollect/TryCollect/RecordCollect/InRange` (T13) are what `CommandableUnit` and the builder call; `LootContainer.InitializeWith(ItemInventory, bool searched)` (T12) is what the kit and tests call; `InventoryView`/`InventoryViewBuilder.Build(sources, inspectedId, selection, view)` (T16) is what the modal calls (T18); `MissionRig.AddInventory/AddLoot` (T8, T12) are used by T9–T19 tests.

**Review Focus coverage:** (1) T2, T10, T13; (2) T13; (3) T4, T16, T18; (4) T4, T8; (5) T15, T18.
