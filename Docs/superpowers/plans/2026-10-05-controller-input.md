# Phase 6.5 Native Controller Input Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Xbox, PlayStation, Nintendo and generic gamepads drive every existing control state (direct control, tactical pause, selection, orders, queues, cover) through semantic Input Actions, with automatic keyboard/mouse ↔ controller hot-swapping, a tactical cursor and a debug HUD line showing the active input family. Keyboard/mouse is unchanged.

**Architecture:** `BlackglassControls.inputactions` gains binding groups (`KeyboardMouse`, `Xbox`, `PlayStation`, `Nintendo`, `Gamepad`) and new semantic actions. A new `ActiveInputDevice` component detects the active family from Input System events and applies it with `InputActionAsset.bindingMask` (+ `devices` for one controller at a time). A screen-space `TacticalCursor` resolves a typed `PointerTarget` that feeds the same `Act(...)` path the mouse click uses in `PlayerCommandInput`; orders still go `CommandResolver` → `GroupOrders` → `CommandableUnit`. No gameplay system (units, combat, cover, AI, command queues) is modified.

**Tech Stack:** Unity 6.3 LTS (6000.3.25f1), C#, `com.unity.inputsystem` 1.20.0 (already installed; no new packages), NUnit + Unity Test Framework + `InputTestFixture`.

**Spec:** `docs/superpowers/specs/2026-10-05-controller-input-design.md` (approved 2026-10-05). Read it before starting; the "Deviations" section below lists where this plan intentionally refines it.

## Global Constraints

- Input System package stays at **1.20.0**; add no packages (CLAUDE.md: "unnecessary third-party packages" are out).
- Existing keyboard/mouse **bindings and behaviour stay identical**; every existing test must pass unchanged except the action-name strings renamed in Task 3.
- `Gamepad.current`, `Keyboard.current`, `Mouse.current`, face-button names (`buttonSouth`…) and device types appear **only** under `Assets/_Project/Scripts/Input/` (enforced by a test in Task 14). Gameplay and input components read semantic `InputActionReference`s.
- `TacticalPause` stays the only writer of `Time.timeScale`. New runtime code that must work while paused (family detection, cursor, camera look) uses `Time.unscaledDeltaTime` or input events, never scaled time.
- No singletons, no static mutable state, no `FindObjectOfType` at runtime; components are wired through serialized fields and `internal Initialize(...)` (decisions 009/010).
- Never use `??`, `?.` or `is null` on `UnityEngine.Object` references (fake-null); use `== null` / `!= null`.
- New or changed files under `Scripts/` follow the surrounding style: file-scoped to namespace `Blackglass`, 4-space indent, `///` summaries, no commented-out code.
- Commits end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Commit after each task. Unity creates `.meta` files for every new file on the next import: **`git add` the directory/path, not individual files, so the `.meta` files go in too.**
- No physical controller is available to Claude. Never write "tested on hardware" for anything. The owner has a **PS5 DualSense (USB, VID 054C / PID 0CE6)** for the manual check in Task 14.
- Unity batch runs need the Editor closed. Run tests with `Tools/run-tests.sh EditMode|PlayMode ["<filter>"]` from the repo root (EXIT=0 passed, 1 compile error, 2 test failures). Unity creates `Library/`, `Temp/` and `Logs/` (git-ignored).
- **Baseline measured 2026-10-05 on `main`:** EditMode **337** passed, PlayMode **303** passed, 0 failed. Re-measure before trusting any total here (see Task 0).
- Scene `Prototype.unity` is edited **only** through a temporary editor builder that is created, run and deleted inside Task 13 and never committed (the pattern of earlier phases).

## Deviations from the spec (decided while planning; the owner approved the spec, not these refinements)

1. **No separate `Tactical` map.** The new tactical actions (`Confirm`, `Attack`, `NextTarget`, `PreviousTarget`, `CursorMove`) join the existing `Commands` map, which already holds `Command`, `Stop`, `Modifier`, `ClearSelection`, `TogglePause`. Fewer maps, no extra enable/disable plumbing. `Camera` gains `Look` and `CameraModifier`; `Character` gains `PreviousCharacter`.
2. **More renames than the spec listed**, all name-only (action IDs are unchanged, so scene references survive; Task 3 verifies): `Commands/TogglePause`→`ToggleTacticalPause`, `Commands/Modifier`→`QueueModifier`, `Commands/ClearSelection`→`Cancel`, `Character/Takeover`→`ToggleCharacterControl`, `Character/CycleCharacter`→`NextCharacter`. The spec's other semantic names are new actions.
3. **Gamepad bindings use `<Gamepad>` paths in every family group; prompt text comes from a label table** (`GamepadLabels`), not from layout display names. Reason: the binding group (selected by `ActiveInputDevice`), not the layout, decides the family, so an Xbox pad on a platform whose layout is not `XInputController` still works. Nintendo differs by *binding* (Confirm = east), not by code.
4. **Stick "hysteresis" is edge-triggered engagement** and buttons are edge-triggered too (`before < 0.5 && after ≥ 0.5`). A stick that is simply held deflected, or a button that is held while the pad keeps sending reports, never re-claims the family from keyboard/mouse. This is what "stick drift must not flip the family" needs in practice.
5. **The binding mask is applied on the next `Update`, not inside the event callback.** The first meaningful event after a switch therefore only *wakes* the new family (the press that woke it does not also fire an action). Held sticks take effect at once (actions use an initial-state check).
6. **No UI stub reader component.** The `UI` map (`Navigate`, `Submit`, `Cancel`) exists in the asset with gamepad bindings and is covered by asset tests; a component that nothing uses would be speculative.
7. **`SoftTarget` is a property of `TacticalCursor`**, not a separate class. Snap is recomputed every frame (no sticky snap).
8. **Cover snap radius is 0.75 m** (units 1.2 m): at the 1 m cover spacing a 1.2 m radius would turn every ground order near a wall into a cover order. Both are serialized and tunable.
9. **Switch 2:** Input System 1.20.0 has no Switch 2 layout. It is recognised only by Nintendo vendor ID 0x057E and a product ID believed to be 0x2069 (**unverified on hardware**) and would additionally need an HID layout to produce `Gamepad` input at all. It is classified `Nintendo` / `ControllerModel.Switch2` when it does appear as a gamepad; this is not claimed to work.
10. **`Select`, `Confirm` and `ContextAction` are one action, `Confirm`.** Three names for one binding would only invite three bindings; the spec's semantic role is preserved (select a friendly, order on anything else, move into cover).

## Review Focus

Failure modes the spec implies but a feature-by-feature test list would miss, most likely first. Each has a test in the owning task.

1. **A pad is holding a stick or button while the player switches to keyboard/mouse** (and the pad keeps sending reports): the family must stay keyboard/mouse. → Task 4 `HeldStick_...` / `HeldButton_...`.
2. **The active controller is unplugged** (or its device object removed) mid-play: fall back to keyboard/mouse, clear the device restriction, no exception, no stuck input. → Task 4.
3. **Unknown or oddly-identified pads:** a plain `Gamepad`, a pad with Nintendo/Sony vendor but no known layout, and non-gamepad HID joysticks must classify without exceptions (generic or ignored). → Tasks 1 and 4.
4. **Stale or dead targets:** the cursor's snapped hostile/friendly dies or is destroyed between frames; `Attack` with no living hostiles; `NextTarget` with none; `Confirm` while the cursor role is inactive (driving, no RT held); `Cancel` with an empty selection. None may throw or issue an order. → Tasks 9, 10, 11.
5. **Switching family mid-action while driving:** a held left stick or WASD at the instant the family changes must not leave a non-zero move intent or a permanently held modifier. → Task 7 and Task 13.

---

## File Structure

New runtime code, `Assets/_Project/Scripts/Input/` (assembly `Blackglass`):

| File | Responsibility |
|---|---|
| `InputFamily.cs` | `InputFamily`, `ControllerModel` enums; `InputFamilyExtensions` (`IsController`, `BindingGroup`, `DisplayName`). |
| `InputFamilyClassifier.cs` | Device → family/model (pure apart from reading the device). |
| `InputActivityFilter.cs` | Pure thresholds for "meaningful input" (stick/button edges, mouse motion). |
| `ActiveInputDevice.cs` | `MonoBehaviour`: listens to input events, owns `Family`/`Device`, applies mask + device restriction. |
| `GamepadLabels.cs`, `PromptResolver.cs` | Placeholder prompt text per family and "what is shown for action X". |
| `StickResponse.cs`, `StickRole.cs` | Pure stick curve and "who owns the right stick" rule. |
| `PointerTarget.cs`, `PointerTargetResolver.cs` | Typed target + screen point → target (shared by mouse and cursor). |
| `HostileTargets.cs` | Pure best-target and target-cycling rules. |
| `TacticalCursor.cs` | Virtual cursor, snapping, soft target, target cycling. |

New debug UI: `Assets/_Project/Scripts/DebugUI/TacticalCursorView.cs`.

Modified: `Input/BlackglassControls.inputactions` (+ a one-off generator script in the scratchpad, not committed), `Scripts/Controls/PlayerCommandInput.cs`, `Scripts/Controls/DirectControlInput.cs`, `Scripts/CameraControl/TacticalCameraController.cs`, `Scripts/DebugUI/PrototypeHud.cs`, `Scenes/Prototype.unity` (via builder), `Tests/EditMode/Blackglass.Tests.EditMode.asmdef` (+ Input System references), `Tests/PlayMode/TestSupport/TestControls.cs`, five test files (renamed action strings), `Docs/Decisions.md`.

New tests: `Tests/EditMode/`: `InputFamilyClassifierTests`, `InputActivityFilterTests`, `InputAssetTests`, `PromptResolverTests`, `StickResponseTests`, `HostileTargetsTests`, `ControllerHudTests`, `NoDeviceTypesInGameplayTests`; `Tests/PlayMode/`: `ActiveInputDeviceTests`, `CameraGamepadTests`, `DirectControlGamepadTests`, `PointerTargetResolverTests`, `TacticalCursorTests`, `ControllerCommandInputTests`, `RebindingTests`, `PrototypeSceneControllerTests`.

Decision/doc: `Docs/Decisions.md` decision 024 and amendments to 008/012 (Task 14).

## Task execution notes (read once)

- Test fixtures that touch the shared project `InputActionAsset` must reset it in `TearDown` **before** `base.TearDown()`: `TestControls.Reset(actions)` (added in Task 3) clears `bindingMask`, `devices`, binding overrides and disables the actions.
- Tests that need a family call `TestControls.UseGroup(actions, "Xbox")` (or the pad's family) instead of an `ActiveInputDevice`, except Task 4/13 which test `ActiveInputDevice` itself. With no mask every group's bindings are active at once, which is not a state the game is ever in.
- "Wake" helpers in tests: tap `pad.startButton` (unbound, reserved for Menu) to make a pad the active device, tap `keyboard.f12Key` (unbound) for keyboard/mouse.
- Quick compile check without tests: `Tools/run-tests.sh EditMode "Blackglass.Tests.NoSuchFixture"` returns EXIT=1 with `error CS...` lines on a compile error, otherwise 0 tests run.

---

### Task 0: Branch and baseline

**Files:** none.

- [ ] **Step 1: Create the branch**

```bash
git switch -c phase-6-5-controller-input
git status --short
```
Expected: `?? .claude/` only.

- [ ] **Step 2: Re-measure the baseline**

```bash
Tools/run-tests.sh EditMode; Tools/run-tests.sh PlayMode
```
Expected: `total="337" passed="337"` and `total="303" passed="303"`, EXIT=0 both. If the numbers differ, use the measured ones everywhere this plan quotes totals.

- [ ] **Step 3: Keep the baseline logs for the final Console comparison (Task 14)**

```bash
cp Logs/TestRun-EditMode.log Logs/phase65-baseline-EditMode.log
cp Logs/TestRun-PlayMode.log Logs/phase65-baseline-PlayMode.log
```

---

### Task 1: Input family types and device classifier

**Files:**
- Create: `Assets/_Project/Scripts/Input/InputFamily.cs`
- Create: `Assets/_Project/Scripts/Input/InputFamilyClassifier.cs`
- Modify: `Assets/_Project/Tests/EditMode/Blackglass.Tests.EditMode.asmdef`
- Test: `Assets/_Project/Tests/EditMode/InputFamilyClassifierTests.cs`

**Interfaces:**
- Produces:
  - `enum InputFamily { KeyboardMouse, Xbox, PlayStation, Nintendo, GenericGamepad }`
  - `enum ControllerModel { None, Unknown, Xbox, DualShock, DualSense, SwitchPro, Switch2 }`
  - `static class InputFamilyExtensions { bool IsController(this InputFamily); string BindingGroup(this InputFamily); string DisplayName(this InputFamily); }` — `BindingGroup` returns `"KeyboardMouse"`, `"Xbox"`, `"PlayStation"`, `"Nintendo"`, `"Gamepad"`; `DisplayName` returns `"Keyboard/Mouse"`, `"Xbox"`, `"PlayStation"`, `"Nintendo"`, `"Generic Gamepad"`.
  - `static class InputFamilyClassifier { bool TryClassify(InputDevice device, out InputFamily family, out ControllerModel model); InputFamily FamilyFromVendor(int vendorId); bool IsSwitch2(int vendorId, int productId); }`

- [ ] **Step 1: Add the Input System references to the EditMode test assembly**

Replace the `references` array in `Assets/_Project/Tests/EditMode/Blackglass.Tests.EditMode.asmdef` with:

```json
    "references": [
        "Blackglass",
        "UnityEngine.TestRunner",
        "UnityEditor.TestRunner",
        "Unity.InputSystem",
        "Unity.InputSystem.TestFramework"
    ],
```

- [ ] **Step 2: Write the failing tests**

`Assets/_Project/Tests/EditMode/InputFamilyClassifierTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.Switch;
using UnityEngine.InputSystem.XInput;

namespace Blackglass.Tests
{
    public class InputFamilyClassifierTests : InputTestFixture
    {
        static void AssertClassified(InputDevice device, InputFamily family, ControllerModel model)
        {
            Assert.That(InputFamilyClassifier.TryClassify(device, out var actualFamily, out var actualModel), Is.True);
            Assert.That(actualFamily, Is.EqualTo(family));
            Assert.That(actualModel, Is.EqualTo(model));
        }

        [Test]
        public void KeyboardAndMouse_AreKeyboardMouse()
        {
            AssertClassified(InputSystem.AddDevice<Keyboard>(), InputFamily.KeyboardMouse, ControllerModel.None);
            AssertClassified(InputSystem.AddDevice<Mouse>(), InputFamily.KeyboardMouse, ControllerModel.None);
        }

        [Test]
        public void XInputController_IsXbox() =>
            AssertClassified(InputSystem.AddDevice<XInputController>(), InputFamily.Xbox, ControllerModel.Xbox);

        [Test]
        public void DualSenseHid_IsPlayStation() =>
            AssertClassified(InputSystem.AddDevice<DualSenseGamepadHID>(), InputFamily.PlayStation, ControllerModel.DualSense);

        [Test]
        public void DualShock4Hid_IsPlayStation() =>
            AssertClassified(InputSystem.AddDevice<DualShock4GamepadHID>(), InputFamily.PlayStation, ControllerModel.DualShock);

        [Test]
        public void SwitchProHid_IsNintendo() =>
            AssertClassified(InputSystem.AddDevice<SwitchProControllerHID>(), InputFamily.Nintendo, ControllerModel.SwitchPro);

        [Test]
        public void PlainGamepad_IsGeneric() =>
            AssertClassified(InputSystem.AddDevice<Gamepad>(), InputFamily.GenericGamepad, ControllerModel.Unknown);

        [Test]
        public void NonGamepadDevices_AreNotClassified()
        {
            Assert.That(InputFamilyClassifier.TryClassify(InputSystem.AddDevice<Joystick>(), out _, out _), Is.False);
            Assert.That(InputFamilyClassifier.TryClassify(InputSystem.AddDevice<Touchscreen>(), out _, out _), Is.False);
        }

        [TestCase(0x054C, InputFamily.PlayStation)]
        [TestCase(0x045E, InputFamily.Xbox)]
        [TestCase(0x057E, InputFamily.Nintendo)]
        [TestCase(0x1234, InputFamily.GenericGamepad)]
        [TestCase(0, InputFamily.GenericGamepad)]
        public void FamilyFromVendor_MapsKnownVendors(int vendorId, InputFamily expected) =>
            Assert.That(InputFamilyClassifier.FamilyFromVendor(vendorId), Is.EqualTo(expected));

        [Test]
        public void IsSwitch2_NeedsTheNintendoVendorAndAKnownProduct()
        {
            Assert.That(InputFamilyClassifier.IsSwitch2(0x057E, 0x2069), Is.True);
            Assert.That(InputFamilyClassifier.IsSwitch2(0x057E, 0x2009), Is.False, "0x2009 is the original Switch Pro Controller");
            Assert.That(InputFamilyClassifier.IsSwitch2(0x054C, 0x2069), Is.False, "Right product, wrong vendor");
        }

        [Test]
        public void FamilyExtensions_GiveGroupsAndNames()
        {
            Assert.That(InputFamily.KeyboardMouse.IsController(), Is.False);
            Assert.That(InputFamily.Xbox.IsController(), Is.True);
            Assert.That(InputFamily.GenericGamepad.BindingGroup(), Is.EqualTo("Gamepad"));
            Assert.That(InputFamily.PlayStation.BindingGroup(), Is.EqualTo("PlayStation"));
            Assert.That(InputFamily.KeyboardMouse.DisplayName(), Is.EqualTo("Keyboard/Mouse"));
            Assert.That(InputFamily.GenericGamepad.DisplayName(), Is.EqualTo("Generic Gamepad"));
        }
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.InputFamilyClassifierTests"`
Expected: EXIT=1 with `error CS0246` / `CS0103` for `InputFamily`, `InputFamilyClassifier`.

- [ ] **Step 4: Implement `InputFamily.cs`**

```csharp
namespace Blackglass
{
    /// <summary>
    /// Which kind of input device the player is using right now. Prompts and the active binding group follow it;
    /// gameplay never reads it.
    /// </summary>
    public enum InputFamily
    {
        KeyboardMouse,
        Xbox,
        PlayStation,
        Nintendo,
        GenericGamepad,
    }

    /// <summary>
    /// A more precise controller identity, kept so a prompt family can be split later (a separate Switch 2 glyph set,
    /// say). Gameplay never reads it.
    /// </summary>
    public enum ControllerModel
    {
        None,
        Unknown,
        Xbox,
        DualShock,
        DualSense,
        SwitchPro,
        Switch2,
    }

    public static class InputFamilyExtensions
    {
        public static bool IsController(this InputFamily family) => family != InputFamily.KeyboardMouse;

        /// <summary>The binding group in BlackglassControls.inputactions that holds this family's bindings.</summary>
        public static string BindingGroup(this InputFamily family)
        {
            switch (family)
            {
                case InputFamily.Xbox:
                    return "Xbox";
                case InputFamily.PlayStation:
                    return "PlayStation";
                case InputFamily.Nintendo:
                    return "Nintendo";
                case InputFamily.GenericGamepad:
                    return "Gamepad";
                default:
                    return "KeyboardMouse";
            }
        }

        public static string DisplayName(this InputFamily family)
        {
            switch (family)
            {
                case InputFamily.Xbox:
                    return "Xbox";
                case InputFamily.PlayStation:
                    return "PlayStation";
                case InputFamily.Nintendo:
                    return "Nintendo";
                case InputFamily.GenericGamepad:
                    return "Generic Gamepad";
                default:
                    return "Keyboard/Mouse";
            }
        }
    }
}
```

- [ ] **Step 5: Implement `InputFamilyClassifier.cs`**

```csharp
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.Switch;
using UnityEngine.InputSystem.XInput;

namespace Blackglass
{
    /// <summary>
    /// Decides which input family a device belongs to. Keyboard and mouse are KeyboardMouse; a gamepad is Xbox,
    /// PlayStation or Nintendo when its layout or USB vendor says so, otherwise GenericGamepad; anything else (joysticks,
    /// touch) is not classified and never changes the active family. Nothing outside Scripts/Input knows device types.
    /// </summary>
    public static class InputFamilyClassifier
    {
        public const int SonyVendorId = 0x054C;
        public const int MicrosoftVendorId = 0x045E;
        public const int NintendoVendorId = 0x057E;

        const int DualSenseProductId = 0x0CE6;
        const int DualSenseEdgeProductId = 0x0DF2;
        // Believed to be the Switch 2 Pro Controller. Not verified on hardware, and Input System 1.20.0 has no Switch 2
        // layout, so such a device only appears as a Gamepad if the platform exposes it as one.
        const int Switch2ProProductId = 0x2069;

        public static bool TryClassify(InputDevice device, out InputFamily family, out ControllerModel model)
        {
            family = InputFamily.KeyboardMouse;
            model = ControllerModel.None;
            if (device is Keyboard || device is Mouse)
                return true;
            if (!(device is Gamepad))
                return false;

            TryReadHidIds(device, out var vendorId, out var productId);

            if (device is XInputController)
            {
                family = InputFamily.Xbox;
                model = ControllerModel.Xbox;
                return true;
            }
            if (device is DualShockGamepad)
            {
                family = InputFamily.PlayStation;
                var isDualSense = device.layout == "DualSenseGamepadHID" || productId == DualSenseProductId
                    || productId == DualSenseEdgeProductId;
                model = isDualSense ? ControllerModel.DualSense : ControllerModel.DualShock;
                return true;
            }
            if (device is SwitchProController)
            {
                family = InputFamily.Nintendo;
                model = IsSwitch2(vendorId, productId) ? ControllerModel.Switch2 : ControllerModel.SwitchPro;
                return true;
            }

            family = FamilyFromVendor(vendorId);
            switch (family)
            {
                case InputFamily.Xbox:
                    model = ControllerModel.Xbox;
                    break;
                case InputFamily.Nintendo:
                    model = IsSwitch2(vendorId, productId) ? ControllerModel.Switch2 : ControllerModel.Unknown;
                    break;
                default:
                    model = ControllerModel.Unknown;
                    break;
            }
            return true;
        }

        public static InputFamily FamilyFromVendor(int vendorId)
        {
            switch (vendorId)
            {
                case SonyVendorId:
                    return InputFamily.PlayStation;
                case MicrosoftVendorId:
                    return InputFamily.Xbox;
                case NintendoVendorId:
                    return InputFamily.Nintendo;
                default:
                    return InputFamily.GenericGamepad;
            }
        }

        public static bool IsSwitch2(int vendorId, int productId) =>
            vendorId == NintendoVendorId && productId == Switch2ProProductId;

        static void TryReadHidIds(InputDevice device, out int vendorId, out int productId)
        {
            vendorId = 0;
            productId = 0;
            var description = device.description;
            if (description.interfaceName != "HID" || string.IsNullOrEmpty(description.capabilities))
                return;
            var hid = HID.HIDDeviceDescriptor.FromJson(description.capabilities);
            vendorId = hid.vendorId;
            productId = hid.productId;
        }
    }
}
```

- [ ] **Step 6: Run to verify it passes**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.InputFamilyClassifierTests"`
Expected: EXIT=0, `total="14" passed="14"` (7 single tests + 5 vendor cases + the Switch 2 and extension tests; count what the run prints and use it).
If `InputSystem.AddDevice<DualSenseGamepadHID>()` throws "layout not found" in the test fixture, add the device with `InputSystem.AddDevice("DualSenseGamepadHID")` (same layout, by name) and report which form was needed.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project/Scripts/Input Assets/_Project/Tests/EditMode
git commit -m "Classify input devices into keyboard/mouse, Xbox, PlayStation, Nintendo and generic families

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Activity filter (what counts as meaningful input)

**Files:**
- Create: `Assets/_Project/Scripts/Input/InputActivityFilter.cs`
- Test: `Assets/_Project/Tests/EditMode/InputActivityFilterTests.cs`

**Interfaces:**
- Produces: `sealed class InputActivityFilter` with
  `InputActivityFilter(float stickEngage = 0.55f, float stickRelease = 0.35f, float buttonPress = 0.5f, float mouseDeltaPixels = 4f)`,
  `bool IsStickEngagement(int stickKey, Vector2 value)` (true once per crossing; re-arms only after the stick falls below `stickRelease`),
  `bool IsButtonPress(float before, float after)`, `bool IsMouseMotion(Vector2 delta)`, `bool IsScroll(Vector2 scroll)`, `void Reset()`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class InputActivityFilterTests
    {
        [Test]
        public void StickBelowEngage_IsNotMeaningful()
        {
            var filter = new InputActivityFilter();
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.5f, 0f)), Is.False);
        }

        [Test]
        public void StickAtEngage_IsMeaningfulOnce()
        {
            var filter = new InputActivityFilter();
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.6f, 0f)), Is.True);
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.6f, 0f)), Is.False, "Same deflection again is not new input");
        }

        [Test]
        public void StickMustFallBelowRelease_BeforeItCanEngageAgain()
        {
            var filter = new InputActivityFilter();
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.9f, 0f)), Is.True);
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.45f, 0f)), Is.False, "Between release and engage: still engaged");
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.9f, 0f)), Is.False, "Never released, so no new engagement");
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.2f, 0f)), Is.False, "Released");
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.9f, 0f)), Is.True, "Pushed again");
        }

        [Test]
        public void SticksAreTrackedIndependently()
        {
            var filter = new InputActivityFilter();
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.9f, 0f)), Is.True);
            Assert.That(filter.IsStickEngagement(2, new Vector2(0.9f, 0f)), Is.True);
        }

        [Test]
        public void DriftNeverEngages()
        {
            var filter = new InputActivityFilter();
            for (var i = 0; i < 200; i++)
            {
                var wobble = new Vector2(Mathf.Sin(i) * 0.3f, Mathf.Cos(i) * 0.3f);
                Assert.That(filter.IsStickEngagement(1, wobble), Is.False);
            }
        }

        [TestCase(0f, 1f, true)]
        [TestCase(1f, 1f, false)]
        [TestCase(0f, 0.4f, false)]
        [TestCase(0.4f, 0.6f, true)]
        [TestCase(1f, 0f, false)]
        public void ButtonPress_IsAnEdgeAcrossThePressPoint(float before, float after, bool expected)
        {
            Assert.That(new InputActivityFilter().IsButtonPress(before, after), Is.EqualTo(expected));
        }

        [Test]
        public void MouseMotion_NeedsAFewPixels()
        {
            var filter = new InputActivityFilter();
            Assert.That(filter.IsMouseMotion(new Vector2(1f, 1f)), Is.False);
            Assert.That(filter.IsMouseMotion(new Vector2(2f, 0f)), Is.False);
            Assert.That(filter.IsMouseMotion(new Vector2(3f, 3f)), Is.True);
        }

        [Test]
        public void AnyScroll_IsMeaningful()
        {
            var filter = new InputActivityFilter();
            Assert.That(filter.IsScroll(Vector2.zero), Is.False);
            Assert.That(filter.IsScroll(new Vector2(0f, -120f)), Is.True);
        }

        [Test]
        public void Reset_ForgetsEngagement()
        {
            var filter = new InputActivityFilter();
            filter.IsStickEngagement(1, new Vector2(0.9f, 0f));
            filter.Reset();
            Assert.That(filter.IsStickEngagement(1, new Vector2(0.9f, 0f)), Is.True);
        }

        [Test]
        public void Constructor_RejectsReleaseAboveEngage()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new InputActivityFilter(stickEngage: 0.4f, stickRelease: 0.5f));
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.InputActivityFilterTests"`
Expected: EXIT=1, `error CS0246: ... InputActivityFilter`.

- [ ] **Step 3: Implement**

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// What counts as "the player used this device". Sticks count only when they cross a high engage threshold (well above
    /// the dead zone) and only once per crossing, so drift, tiny analog values and a stick left deflected never claim the
    /// active family. Buttons count on the press edge, so a button that stays held while its pad keeps reporting does not
    /// either. Mouse jitter below a few pixels is ignored. Pure logic, no Input System types.
    /// </summary>
    public sealed class InputActivityFilter
    {
        public const float DefaultStickEngage = 0.55f;
        public const float DefaultStickRelease = 0.35f;
        public const float DefaultButtonPress = 0.5f;
        public const float DefaultMouseDeltaPixels = 4f;

        readonly float stickEngage;
        readonly float stickRelease;
        readonly float buttonPress;
        readonly float mouseDeltaPixels;
        readonly Dictionary<int, bool> engagedSticks = new Dictionary<int, bool>();

        public InputActivityFilter(float stickEngage = DefaultStickEngage, float stickRelease = DefaultStickRelease,
            float buttonPress = DefaultButtonPress, float mouseDeltaPixels = DefaultMouseDeltaPixels)
        {
            if (stickRelease > stickEngage)
                throw new ArgumentOutOfRangeException(nameof(stickRelease), stickRelease, "Release must not exceed engage.");
            this.stickEngage = stickEngage;
            this.stickRelease = stickRelease;
            this.buttonPress = buttonPress;
            this.mouseDeltaPixels = mouseDeltaPixels;
        }

        /// <summary>
        /// True once when the stick crosses the engage threshold from a released state. It stays engaged (and returns
        /// false) until the stick falls below the release threshold.
        /// </summary>
        public bool IsStickEngagement(int stickKey, Vector2 value)
        {
            var magnitude = value.magnitude;
            engagedSticks.TryGetValue(stickKey, out var engaged);
            if (engaged)
            {
                if (magnitude < stickRelease)
                    engagedSticks[stickKey] = false;
                return false;
            }
            if (magnitude < stickEngage)
                return false;
            engagedSticks[stickKey] = true;
            return true;
        }

        /// <summary>True when a button's value crosses the press point upward in this event.</summary>
        public bool IsButtonPress(float before, float after) => before < buttonPress && after >= buttonPress;

        public bool IsMouseMotion(Vector2 delta) => delta.sqrMagnitude >= mouseDeltaPixels * mouseDeltaPixels;

        public bool IsScroll(Vector2 scroll) => scroll != Vector2.zero;

        public void Reset() => engagedSticks.Clear();
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.InputActivityFilterTests"`
Expected: EXIT=0, `passed="14"` (10 methods, 5 button cases).

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Input Assets/_Project/Tests/EditMode
git commit -m "Add the input activity filter that ignores drift and held controls

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Input Actions asset — groups, renames, semantic actions, controller bindings

**Files:**
- Modify: `Assets/_Project/Input/BlackglassControls.inputactions` (via a one-off generator script in the scratchpad; the script is **not** committed)
- Modify: `Assets/_Project/Tests/PlayMode/TestSupport/TestControls.cs`
- Modify (rename strings only): `Tests/PlayMode/PlayerCommandInputTests.cs`, `DirectControlInputTests.cs`, `ActiveCharacterDeathPlayModeTests.cs`
- Test: `Assets/_Project/Tests/EditMode/InputAssetTests.cs`

**Interfaces:**
- Produces (asset): maps `Camera` (`Pan`, `Rotate`, `RotateDrag`, `PointerPosition`, `Zoom`, **`Look`**, **`CameraModifier`**), `Commands` (`Command`, `PointerPosition`, **`ToggleTacticalPause`**, **`QueueModifier`**, `Stop`, **`Cancel`**, **`Confirm`**, **`Attack`**, **`NextTarget`**, **`PreviousTarget`**, **`CursorMove`**), `Character` (`Move`, **`ToggleCharacterControl`**, **`NextCharacter`**, `CycleReverse`, `ToggleFollow`, **`PreviousCharacter`**), **`UI`** (`Navigate`, `Submit`, `Cancel`); binding groups `KeyboardMouse`, `Xbox`, `PlayStation`, `Nintendo`, `Gamepad`.
- Produces (test support): `TestControls.UseGroup(InputActionAsset, string group)`, `TestControls.Reset(InputActionAsset)`.

- [ ] **Step 1: Write the failing asset tests**

`Assets/_Project/Tests/EditMode/InputAssetTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    public class InputAssetTests
    {
        const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";
        static readonly string[] PadGroups = { "Xbox", "PlayStation", "Nintendo", "Gamepad" };
        static readonly string[] KnownGroups = { "KeyboardMouse", "Xbox", "PlayStation", "Nintendo", "Gamepad" };

        // Every action a controller must be able to drive, as "Map/Action".
        static readonly string[] PadActions =
        {
            "Camera/Pan", "Camera/Look", "Camera/CameraModifier", "Camera/Zoom",
            "Commands/CursorMove", "Commands/Confirm", "Commands/Cancel", "Commands/Attack", "Commands/QueueModifier",
            "Commands/ToggleTacticalPause", "Commands/Stop", "Commands/NextTarget", "Commands/PreviousTarget",
            "Character/Move", "Character/ToggleCharacterControl", "Character/NextCharacter", "Character/PreviousCharacter",
            "Character/ToggleFollow", "UI/Navigate", "UI/Submit", "UI/Cancel",
        };

        static InputActionAsset Load()
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            Assert.That(asset, Is.Not.Null, $"Input actions not found at {AssetPath}");
            return asset;
        }

        static string[] GroupsOf(InputBinding binding) =>
            binding.groups.Split(new[] { InputBinding.Separator }, System.StringSplitOptions.RemoveEmptyEntries);

        static IEnumerable<InputBinding> BindingsOf(string actionPath, string group) =>
            Load().FindAction(actionPath, throwIfNotFound: true).bindings
                .Where(b => !b.isComposite && !b.isPartOfComposite && GroupsOf(b).Contains(group));

        static string[] PathsOf(string actionPath, string group) =>
            BindingsOf(actionPath, group).Select(b => b.path).OrderBy(p => p).ToArray();

        [Test]
        public void EveryBindingBelongsToExactlyOneKnownGroup()
        {
            foreach (var binding in Load().bindings)
            {
                var groups = GroupsOf(binding);
                Assert.That(groups, Has.Length.EqualTo(1), $"{binding.action}: {binding.path} must be in exactly one group");
                Assert.That(KnownGroups, Does.Contain(groups[0]), $"{binding.action}: {binding.path}");
            }
        }

        [Test]
        public void KeyboardMouseGroup_HoldsOnlyKeyboardMouseAndPointerPaths_AndPadGroupsOnlyGamepadPaths()
        {
            foreach (var binding in Load().bindings.Where(b => !b.isComposite))
            {
                var group = GroupsOf(binding)[0];
                var isPad = binding.path.StartsWith("<Gamepad>");
                Assert.That(isPad, Is.EqualTo(group != "KeyboardMouse"), $"{binding.action}: {binding.path} is in {group}");
            }
        }

        [Test]
        public void BindingAndActionIds_AreUnique()
        {
            var asset = Load();
            var bindingIds = asset.bindings.Select(b => b.id).ToList();
            Assert.That(bindingIds.Distinct().Count(), Is.EqualTo(bindingIds.Count), "Duplicate binding ID");
            var actionIds = asset.actionMaps.SelectMany(m => m.actions).Select(a => a.id).ToList();
            Assert.That(actionIds.Distinct().Count(), Is.EqualTo(actionIds.Count), "Duplicate action ID");
        }

        [Test]
        public void RenamedActionsExist_AndTheOldNamesAreGone()
        {
            var asset = Load();
            foreach (var renamed in new[]
                     {
                         "Commands/ToggleTacticalPause", "Commands/QueueModifier", "Commands/Cancel",
                         "Character/ToggleCharacterControl", "Character/NextCharacter",
                     })
                Assert.That(asset.FindAction(renamed), Is.Not.Null, renamed);
            foreach (var old in new[]
                     {
                         "Commands/TogglePause", "Commands/Modifier", "Commands/ClearSelection",
                         "Character/Takeover", "Character/CycleCharacter",
                     })
                Assert.That(asset.FindAction(old), Is.Null, old);
        }

        [Test]
        public void ExistingKeyboardMouseBindings_AreUnchanged()
        {
            var expected = new Dictionary<string, string[]>
            {
                ["Camera/Pan"] = new[] { "<Keyboard>/a", "<Keyboard>/d", "<Keyboard>/s", "<Keyboard>/w" },
                ["Camera/Rotate"] = new[] { "<Keyboard>/e", "<Keyboard>/q" },
                ["Camera/RotateDrag"] = new[] { "<Mouse>/rightButton" },
                ["Camera/PointerPosition"] = new[] { "<Pointer>/position" },
                ["Camera/Zoom"] = new[] { "<Mouse>/scroll/y" },
                ["Commands/Command"] = new[] { "<Mouse>/leftButton" },
                ["Commands/PointerPosition"] = new[] { "<Pointer>/position" },
                ["Commands/ToggleTacticalPause"] = new[] { "<Keyboard>/space" },
                ["Commands/QueueModifier"] = new[] { "<Keyboard>/leftShift", "<Keyboard>/rightShift" },
                ["Commands/Stop"] = new[] { "<Keyboard>/x" },
                ["Commands/Cancel"] = new[] { "<Keyboard>/escape" },
                ["Character/Move"] = new[] { "<Keyboard>/a", "<Keyboard>/d", "<Keyboard>/s", "<Keyboard>/w" },
                ["Character/ToggleCharacterControl"] = new[] { "<Keyboard>/v" },
                ["Character/NextCharacter"] = new[] { "<Keyboard>/tab" },
                ["Character/CycleReverse"] = new[] { "<Keyboard>/leftShift", "<Keyboard>/rightShift" },
                ["Character/ToggleFollow"] = new[] { "<Keyboard>/f" },
            };
            foreach (var pair in expected)
            {
                var actual = BindingsOf(pair.Key, "KeyboardMouse").Concat(
                    Load().FindAction(pair.Key).bindings.Where(b => b.isPartOfComposite && GroupsOf(b).Contains("KeyboardMouse")))
                    .Select(b => b.path).OrderBy(p => p).ToArray();
                Assert.That(actual, Is.EqualTo(pair.Value), pair.Key);
            }
        }

        [Test]
        public void EveryPadFamily_BindsEverySemanticAction()
        {
            var asset = Load();
            foreach (var group in PadGroups)
            {
                foreach (var path in PadActions)
                {
                    var bound = asset.FindAction(path, throwIfNotFound: true).bindings.Any(b => GroupsOf(b).Contains(group));
                    Assert.That(bound, Is.True, $"{path} has no {group} binding");
                }
            }
        }

        [Test]
        public void Nintendo_SwapsConfirmAndCancel_OtherFamiliesKeepSouthEast()
        {
            foreach (var action in new[] { "Commands/Confirm", "UI/Submit" })
            {
                Assert.That(PathsOf(action, "Nintendo"), Is.EqualTo(new[] { "<Gamepad>/buttonEast" }), action);
                foreach (var group in new[] { "Xbox", "PlayStation", "Gamepad" })
                    Assert.That(PathsOf(action, group), Is.EqualTo(new[] { "<Gamepad>/buttonSouth" }), $"{action} {group}");
            }
            foreach (var action in new[] { "Commands/Cancel", "UI/Cancel" })
            {
                Assert.That(PathsOf(action, "Nintendo"), Is.EqualTo(new[] { "<Gamepad>/buttonSouth" }), action);
                foreach (var group in new[] { "Xbox", "PlayStation", "Gamepad" })
                    Assert.That(PathsOf(action, group), Is.EqualTo(new[] { "<Gamepad>/buttonEast" }), $"{action} {group}");
            }
        }

        [Test]
        public void ProvisionalLayout_MatchesTheSpec_InEveryFamilyExceptTheNintendoSwap()
        {
            var layout = new Dictionary<string, string>
            {
                ["Commands/Attack"] = "<Gamepad>/buttonWest",
                ["Character/ToggleCharacterControl"] = "<Gamepad>/buttonNorth",
                ["Character/NextCharacter"] = "<Gamepad>/rightShoulder",
                ["Character/PreviousCharacter"] = "<Gamepad>/leftShoulder",
                ["Commands/QueueModifier"] = "<Gamepad>/leftTrigger",
                ["Camera/CameraModifier"] = "<Gamepad>/rightTrigger",
                ["Commands/ToggleTacticalPause"] = "<Gamepad>/select",
                ["Character/ToggleFollow"] = "<Gamepad>/dpad/up",
                ["Commands/Stop"] = "<Gamepad>/dpad/down",
                ["Commands/PreviousTarget"] = "<Gamepad>/dpad/left",
                ["Commands/NextTarget"] = "<Gamepad>/dpad/right",
                ["Character/Move"] = "<Gamepad>/leftStick",
                ["Camera/Pan"] = "<Gamepad>/leftStick",
                ["Camera/Look"] = "<Gamepad>/rightStick",
                ["Commands/CursorMove"] = "<Gamepad>/rightStick",
            };
            foreach (var group in PadGroups)
            {
                foreach (var pair in layout)
                    Assert.That(PathsOf(pair.Key, group), Is.EqualTo(new[] { pair.Value }), $"{pair.Key} {group}");
            }
        }

        [Test]
        public void MenuButton_IsReserved_AndUnbound()
        {
            Assert.That(Load().bindings.Any(b => b.path == "<Gamepad>/start"), Is.False);
        }

        [Test]
        public void StickBindings_CarryTheDeadzoneProcessor()
        {
            var sticks = Load().bindings.Where(b => b.path == "<Gamepad>/leftStick" || b.path == "<Gamepad>/rightStick").ToList();
            Assert.That(sticks, Is.Not.Empty);
            foreach (var binding in sticks)
                Assert.That(binding.processors, Does.Contain("stickDeadzone"), $"{binding.action} {binding.groups}");
        }

        [Test]
        public void Zoom_HasStickClickBindingsInEveryPadFamily()
        {
            var asset = Load();
            foreach (var group in PadGroups)
            {
                var paths = asset.FindAction("Camera/Zoom").bindings
                    .Where(b => b.isPartOfComposite && GroupsOf(b).Contains(group)).Select(b => b.path).OrderBy(p => p).ToArray();
                Assert.That(paths, Is.EqualTo(new[] { "<Gamepad>/leftStickPress", "<Gamepad>/rightStickPress" }), group);
            }
        }

        [Test]
        public void ControlSchemesExistForEveryGroup()
        {
            var schemes = Load().controlSchemes.Select(s => s.bindingGroup).OrderBy(s => s).ToArray();
            Assert.That(schemes, Is.EqualTo(KnownGroups.OrderBy(s => s).ToArray()));
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.InputAssetTests"`
Expected: EXIT=2, failures (groups empty, actions missing).

- [ ] **Step 3: Write the generator script (scratchpad, not committed)**

Create `<scratchpad>/generate_controls.py` (the scratchpad path is in the session's environment notes; run it from the repo root):

```python
import json
import sys
import uuid

PATH = "Assets/_Project/Input/BlackglassControls.inputactions"
KBM = "KeyboardMouse"
PAD_GROUPS = ["Xbox", "PlayStation", "Nintendo", "Gamepad"]
DEADZONE = "stickDeadzone(min=0.2,max=0.925)"

src = json.load(open(PATH, encoding="utf-8"))
maps = {m["name"]: m for m in src["maps"]}
assert list(maps) == ["Camera", "Commands", "Character"], list(maps)
assert not src["controlSchemes"], "generator already ran"


def new_id():
    return str(uuid.uuid4())


# 1. Every existing binding is keyboard/mouse. A binding with no group is not matched by a group mask.
for m in maps.values():
    for b in m["bindings"]:
        b["groups"] = KBM


# 2. Semantic renames (name only: action IDs stay, so scene InputActionReferences keep resolving).
def rename(map_name, old, new):
    m = maps[map_name]
    hit = [a for a in m["actions"] if a["name"] == old]
    assert len(hit) == 1, (map_name, old)
    hit[0]["name"] = new
    for b in m["bindings"]:
        if b["action"] == old:
            b["action"] = new


rename("Commands", "TogglePause", "ToggleTacticalPause")
rename("Commands", "Modifier", "QueueModifier")
rename("Commands", "ClearSelection", "Cancel")
rename("Character", "Takeover", "ToggleCharacterControl")
rename("Character", "CycleCharacter", "NextCharacter")


def add_action(map_name, name, typ, control, initial=False):
    maps[map_name]["actions"].append({
        "name": name, "type": typ, "id": new_id(), "expectedControlType": control,
        "processors": "", "interactions": "", "initialStateCheck": initial})


def bind(map_name, action, path, group, processors="", name="", composite=False, part=False):
    maps[map_name]["bindings"].append({
        "name": name, "id": new_id(), "path": path, "interactions": "", "processors": processors,
        "groups": group, "action": action, "isComposite": composite, "isPartOfComposite": part})


# 3. New semantic actions.
add_action("Camera", "Look", "Value", "Vector2", True)
add_action("Camera", "CameraModifier", "Button", "Button")
add_action("Commands", "Confirm", "Button", "Button")
add_action("Commands", "Attack", "Button", "Button")
add_action("Commands", "NextTarget", "Button", "Button")
add_action("Commands", "PreviousTarget", "Button", "Button")
add_action("Commands", "CursorMove", "Value", "Vector2", True)
add_action("Character", "PreviousCharacter", "Button", "Button")
maps["UI"] = {"name": "UI", "id": new_id(), "actions": [], "bindings": []}
add_action("UI", "Navigate", "Value", "Vector2", True)
add_action("UI", "Submit", "Button", "Button")
add_action("UI", "Cancel", "Button", "Button")


# 4. Controller bindings: one per family group. Nintendo swaps the south/east pair for confirm and cancel.
def pad(map_name, action, path, nintendo_path=None, processors=""):
    for group in PAD_GROUPS:
        chosen = nintendo_path if (group == "Nintendo" and nintendo_path) else path
        bind(map_name, action, chosen, group, processors)


pad("Camera", "Pan", "<Gamepad>/leftStick", processors=DEADZONE)
pad("Camera", "Look", "<Gamepad>/rightStick", processors=DEADZONE)
pad("Camera", "CameraModifier", "<Gamepad>/rightTrigger")
for group in PAD_GROUPS:
    bind("Camera", "Zoom", "1DAxis", group, name="L3R3", composite=True)
    bind("Camera", "Zoom", "<Gamepad>/leftStickPress", group, name="negative", part=True)
    bind("Camera", "Zoom", "<Gamepad>/rightStickPress", group, name="positive", part=True)

pad("Commands", "CursorMove", "<Gamepad>/rightStick", processors=DEADZONE)
pad("Commands", "Confirm", "<Gamepad>/buttonSouth", "<Gamepad>/buttonEast")
pad("Commands", "Cancel", "<Gamepad>/buttonEast", "<Gamepad>/buttonSouth")
pad("Commands", "Attack", "<Gamepad>/buttonWest")
pad("Commands", "QueueModifier", "<Gamepad>/leftTrigger")
pad("Commands", "ToggleTacticalPause", "<Gamepad>/select")
pad("Commands", "Stop", "<Gamepad>/dpad/down")
pad("Commands", "NextTarget", "<Gamepad>/dpad/right")
pad("Commands", "PreviousTarget", "<Gamepad>/dpad/left")

pad("Character", "Move", "<Gamepad>/leftStick", processors=DEADZONE)
pad("Character", "ToggleCharacterControl", "<Gamepad>/buttonNorth")
pad("Character", "NextCharacter", "<Gamepad>/rightShoulder")
pad("Character", "PreviousCharacter", "<Gamepad>/leftShoulder")
pad("Character", "ToggleFollow", "<Gamepad>/dpad/up")

pad("UI", "Navigate", "<Gamepad>/dpad")
pad("UI", "Navigate", "<Gamepad>/leftStick", processors=DEADZONE)
pad("UI", "Submit", "<Gamepad>/buttonSouth", "<Gamepad>/buttonEast")
pad("UI", "Cancel", "<Gamepad>/buttonEast", "<Gamepad>/buttonSouth")

# 5. Control schemes, one per binding group.
def scheme(name, group, devices):
    return {"name": name, "bindingGroup": group,
            "devices": [{"devicePath": d, "isOptional": False, "isOR": False} for d in devices]}


src["controlSchemes"] = [scheme("KeyboardMouse", KBM, ["<Keyboard>", "<Mouse>"])] + \
    [scheme(g, g, ["<Gamepad>"]) for g in PAD_GROUPS]

# 6. Write in the file's existing one-object-per-line style.
ACTION_KEYS = ["name", "type", "id", "expectedControlType", "processors", "interactions", "initialStateCheck"]
BINDING_KEYS = ["name", "id", "path", "interactions", "processors", "groups", "action", "isComposite", "isPartOfComposite"]


def one(obj, keys):
    return "{ " + ", ".join('"%s": %s' % (k, json.dumps(obj[k])) for k in keys) + " }"


order = [maps["Camera"], maps["Commands"], maps["Character"], maps["UI"]]
out = ['{', '    "version": 1,', '    "name": "BlackglassControls",', '    "maps": [']
for i, m in enumerate(order):
    out.append('        {')
    out.append('            "name": %s,' % json.dumps(m["name"]))
    out.append('            "id": %s,' % json.dumps(m["id"]))
    out.append('            "actions": [')
    out.append(",\n".join("                " + one(a, ACTION_KEYS) for a in m["actions"]))
    out.append('            ],')
    out.append('            "bindings": [')
    out.append(",\n".join("                " + one(b, BINDING_KEYS) for b in m["bindings"]))
    out.append('            ]')
    out.append('        }' + ("," if i < len(order) - 1 else ""))
out.append('    ],')
out.append('    "controlSchemes": [')
out.append(",\n".join("        " + json.dumps(s) for s in src["controlSchemes"]))
out.append('    ]')
out.append('}')
with open(PATH, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(out) + "\n")
print("wrote", PATH)
```

- [ ] **Step 4: Run it and sanity-check the result**

```bash
python "<scratchpad>/generate_controls.py"
python -c "import json; d=json.load(open('Assets/_Project/Input/BlackglassControls.inputactions')); print([m['name'] for m in d['maps']], len(d['controlSchemes']))"
git diff --stat Assets/_Project/Input
```
Expected: `['Camera', 'Commands', 'Character', 'UI'] 5`; the diff touches only the `.inputactions` file. The original action IDs (`...9e02` etc.) must still appear: `grep -c "6b0e3c1a-5d2f-4a8b-9e10-1c3a5e7b9e02" Assets/_Project/Input/BlackglassControls.inputactions` → 1.

- [ ] **Step 5: Add the test-support helpers**

In `Assets/_Project/Tests/PlayMode/TestSupport/TestControls.cs` add inside the class:

```csharp
        /// <summary>Activates one binding group only, like ActiveInputDevice does in the game.</summary>
        public static void UseGroup(InputActionAsset asset, string group) =>
            asset.bindingMask = InputBinding.MaskByGroup(group);

        /// <summary>Puts the shared project asset back to a clean state; call it from TearDown before base.TearDown().</summary>
        public static void Reset(InputActionAsset asset)
        {
            asset.bindingMask = null;
            asset.devices = null;
            asset.RemoveAllBindingOverrides();
            asset.Disable();
        }
```

- [ ] **Step 6: Update the renamed action names in the existing tests**

```bash
cd Assets/_Project/Tests
sed -i 's#"Commands/TogglePause"#"Commands/ToggleTacticalPause"#; s#"Commands/Modifier"#"Commands/QueueModifier"#; s#"Commands/ClearSelection"#"Commands/Cancel"#; s#"Character/Takeover"#"Character/ToggleCharacterControl"#; s#"Character/CycleCharacter"#"Character/NextCharacter"#' PlayMode/PlayerCommandInputTests.cs PlayMode/DirectControlInputTests.cs PlayMode/ActiveCharacterDeathPlayModeTests.cs
cd ../../..
grep -rn "Commands/TogglePause\|Commands/Modifier\|Commands/ClearSelection\|Character/Takeover\|Character/CycleCharacter" Assets/_Project/Tests Assets/_Project/Scripts
```
Expected: the grep prints nothing.

- [ ] **Step 7: Run the asset tests, then every existing suite (the scene must still resolve its action references)**

```bash
Tools/run-tests.sh EditMode "Blackglass.Tests.InputAssetTests"
Tools/run-tests.sh EditMode
Tools/run-tests.sh PlayMode
```
Expected: asset tests EXIT=0; EditMode total = 337 + 14 (Task 1) + 14 (Task 2) + 12 (this task) = 377 passed; PlayMode **303 passed** (unchanged). If `PrototypeSceneTests` fail with null `InputActionReference` errors or "Action was not found", the renames changed the sub-asset IDs: stop and report (the fix is a scene rewire; do not rename back silently).

- [ ] **Step 8: Commit**

```bash
git add Assets/_Project/Input Assets/_Project/Tests
git commit -m "Group the input bindings by device family, add the semantic actions and controller bindings

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: `ActiveInputDevice` — hot-swapping and applying the family

**Files:**
- Create: `Assets/_Project/Scripts/Input/ActiveInputDevice.cs`
- Test: `Assets/_Project/Tests/PlayMode/ActiveInputDeviceTests.cs`

**Interfaces:**
- Consumes: `InputFamily`, `InputFamilyExtensions`, `InputFamilyClassifier.TryClassify`, `InputActivityFilter` (Tasks 1–2); the asset's binding groups (Task 3); `TestControls.Reset` (Task 3).
- Produces: `sealed class ActiveInputDevice : MonoBehaviour` with `InputFamily Family`, `ControllerModel Model`, `InputDevice Device` (the active controller; null for keyboard/mouse), `event Action<InputFamily> Changed` (raised whenever the family **or** the active device changes), `internal void Initialize(InputActionAsset actions)`. Serialized field name: `controls` (the scene builder in Task 13 sets it).

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/PlayMode/ActiveInputDeviceTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Switch;
using UnityEngine.InputSystem.XInput;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class ActiveInputDeviceTests : InputTestFixture
    {
        Keyboard keyboard;
        Mouse mouse;
        InputActionAsset actions;
        GameObject host;
        ActiveInputDevice active;
        int changes;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
            host = new GameObject("Systems");
            host.SetActive(false);
            active = host.AddComponent<ActiveInputDevice>();
            active.Initialize(actions);
            active.Changed += _ => changes++;
            host.SetActive(true);
        }

        public override void TearDown()
        {
            Object.DestroyImmediate(host);
            TestControls.Reset(actions);
            base.TearDown();
        }

        // Tapping Menu (start) is the harmless "wake" input: it is unbound, so it only announces the device.
        IEnumerator Wake(Gamepad pad)
        {
            Press(pad.startButton);
            yield return null;
            Release(pad.startButton);
            yield return null;
        }

        IEnumerator WakeKeyboard()
        {
            Press(keyboard.f12Key);
            yield return null;
            Release(keyboard.f12Key);
            yield return null;
        }

        static InputBinding Mask(string group) => InputBinding.MaskByGroup(group);

        IEnumerator AssertWakes<T>(InputFamily family, ControllerModel model, string group) where T : Gamepad
        {
            var pad = InputSystem.AddDevice<T>();
            yield return Wake(pad);
            Assert.That(active.Family, Is.EqualTo(family));
            Assert.That(active.Model, Is.EqualTo(model));
            Assert.That(active.Device, Is.SameAs(pad));
            Assert.That(actions.bindingMask, Is.EqualTo((InputBinding?)Mask(group)));
            Assert.That(actions.devices.HasValue, Is.True);
            Assert.That(actions.devices.Value, Has.Count.EqualTo(1));
            Assert.That(actions.devices.Value[0], Is.SameAs(pad));
        }

        [Test]
        public void StartsAsKeyboardMouse_AndAppliesTheKeyboardMouseMask()
        {
            Assert.That(active.Family, Is.EqualTo(InputFamily.KeyboardMouse));
            Assert.That(active.Device, Is.Null);
            Assert.That(actions.bindingMask, Is.EqualTo((InputBinding?)Mask("KeyboardMouse")));
            Assert.That(actions.devices.HasValue, Is.False);
        }

        [UnityTest]
        public IEnumerator XboxController_SwitchesToXbox()
        {
            yield return AssertWakes<XInputController>(InputFamily.Xbox, ControllerModel.Xbox, "Xbox");
        }

        [UnityTest]
        public IEnumerator DualSense_SwitchesToPlayStation()
        {
            yield return AssertWakes<DualSenseGamepadHID>(InputFamily.PlayStation, ControllerModel.DualSense, "PlayStation");
        }

        [UnityTest]
        public IEnumerator SwitchPro_SwitchesToNintendo()
        {
            yield return AssertWakes<SwitchProControllerHID>(InputFamily.Nintendo, ControllerModel.SwitchPro, "Nintendo");
        }

        [UnityTest]
        public IEnumerator UnknownGamepad_FallsBackToTheGenericGroup()
        {
            yield return AssertWakes<Gamepad>(InputFamily.GenericGamepad, ControllerModel.Unknown, "Gamepad");
        }

        [UnityTest]
        public IEnumerator KeyboardAfterController_ReturnsToKeyboardMouse()
        {
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);
            yield return WakeKeyboard();

            Assert.That(active.Family, Is.EqualTo(InputFamily.KeyboardMouse));
            Assert.That(active.Device, Is.Null);
            Assert.That(actions.bindingMask, Is.EqualTo((InputBinding?)Mask("KeyboardMouse")));
            Assert.That(actions.devices.HasValue, Is.False, "Keyboard/mouse is not restricted to one device");
        }

        [UnityTest]
        public IEnumerator MouseMovement_ReturnsToKeyboardMouse()
        {
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);
            Move(mouse.position, new Vector2(300f, 200f));
            yield return null;

            Assert.That(active.Family, Is.EqualTo(InputFamily.KeyboardMouse));
        }

        [UnityTest]
        public IEnumerator TinyMouseJitter_DoesNotTakeTheFamilyBackFromAController()
        {
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);
            Set(mouse.delta, new Vector2(1f, 1f));
            yield return null;

            Assert.That(active.Family, Is.EqualTo(InputFamily.Xbox));
        }

        [UnityTest]
        public IEnumerator StickDrift_NeverSwitches_ButAFirmDeflectionDoes()
        {
            var pad = InputSystem.AddDevice<XInputController>();
            foreach (var drift in new[] { new Vector2(0.1f, 0f), new Vector2(0.3f, 0.2f), new Vector2(0.4f, 0.3f), new Vector2(0.5f, 0.1f) })
            {
                Set(pad.leftStick, drift);
                yield return null;
                Set(pad.rightStick, -drift);
                yield return null;
                Assert.That(active.Family, Is.EqualTo(InputFamily.KeyboardMouse), $"Drift {drift} switched the family");
            }
            Assert.That(changes, Is.EqualTo(0));

            Set(pad.leftStick, new Vector2(0.9f, 0f));
            yield return null;
            Assert.That(active.Family, Is.EqualTo(InputFamily.Xbox));
        }

        [UnityTest]
        public IEnumerator HeldStick_DoesNotTakeTheFamilyBackAfterKeyboardUse()
        {
            var pad = InputSystem.AddDevice<Gamepad>();
            InputSystem.QueueStateEvent(pad, new GamepadState { leftStick = new Vector2(0.9f, 0f) });
            yield return null;
            Assert.That(active.Family, Is.EqualTo(InputFamily.GenericGamepad));

            yield return WakeKeyboard();
            Assert.That(active.Family, Is.EqualTo(InputFamily.KeyboardMouse));

            // The pad keeps reporting the deflected stick (full-state reports, as a real pad sends).
            for (var i = 0; i < 5; i++)
            {
                InputSystem.QueueStateEvent(pad, new GamepadState { leftStick = new Vector2(0.95f, 0.02f * i) });
                yield return null;
            }
            Assert.That(active.Family, Is.EqualTo(InputFamily.KeyboardMouse), "A held stick re-claimed the family");

            InputSystem.QueueStateEvent(pad, new GamepadState());
            yield return null;
            InputSystem.QueueStateEvent(pad, new GamepadState { leftStick = new Vector2(0.9f, 0f) });
            yield return null;
            Assert.That(active.Family, Is.EqualTo(InputFamily.GenericGamepad), "Releasing and pushing again is new input");
        }

        [UnityTest]
        public IEnumerator HeldButton_DoesNotTakeTheFamilyBackAfterKeyboardUse()
        {
            var pad = InputSystem.AddDevice<Gamepad>();
            InputSystem.QueueStateEvent(pad, new GamepadState(GamepadButton.South));
            yield return null;
            Assert.That(active.Family, Is.EqualTo(InputFamily.GenericGamepad));

            yield return WakeKeyboard();
            for (var i = 0; i < 5; i++)
            {
                InputSystem.QueueStateEvent(pad, new GamepadState(GamepadButton.South) { leftStick = new Vector2(0.01f * i, 0f) });
                yield return null;
            }
            Assert.That(active.Family, Is.EqualTo(InputFamily.KeyboardMouse), "A held button re-claimed the family");
        }

        [UnityTest]
        public IEnumerator TwoControllers_TheLastWithMeaningfulInputWins_AndOnlyItIsBound()
        {
            var first = InputSystem.AddDevice<XInputController>();
            var second = InputSystem.AddDevice<XInputController>();
            yield return Wake(first);
            Assert.That(actions.devices.Value[0], Is.SameAs(first));

            yield return Wake(second);
            Assert.That(active.Device, Is.SameAs(second));
            Assert.That(actions.devices.Value, Has.Count.EqualTo(1));
            Assert.That(actions.devices.Value[0], Is.SameAs(second));
            Assert.That(active.Family, Is.EqualTo(InputFamily.Xbox));
        }

        [UnityTest]
        public IEnumerator RemovingTheActiveController_FallsBackToKeyboardMouse()
        {
            var pad = InputSystem.AddDevice<DualSenseGamepadHID>();
            yield return Wake(pad);
            InputSystem.RemoveDevice(pad);
            yield return null;

            Assert.That(active.Family, Is.EqualTo(InputFamily.KeyboardMouse));
            Assert.That(active.Device, Is.Null);
            Assert.That(actions.bindingMask, Is.EqualTo((InputBinding?)Mask("KeyboardMouse")));
            Assert.That(actions.devices.HasValue, Is.False);
        }

        [UnityTest]
        public IEnumerator RemovingAnotherController_ChangesNothing()
        {
            var active1 = InputSystem.AddDevice<XInputController>();
            var other = InputSystem.AddDevice<XInputController>();
            yield return Wake(active1);
            InputSystem.RemoveDevice(other);
            yield return null;

            Assert.That(active.Family, Is.EqualTo(InputFamily.Xbox));
            Assert.That(active.Device, Is.SameAs(active1));
        }

        [UnityTest]
        public IEnumerator Changed_FiresOncePerChange_NotPerEvent()
        {
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);
            Assert.That(changes, Is.EqualTo(1));
            yield return Wake(pad);
            Assert.That(changes, Is.EqualTo(1), "The same controller again is not a change");
            yield return WakeKeyboard();
            Assert.That(changes, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator RepeatedSwitching_LeavesNoStuckInput_AndLogsNoErrors()
        {
            var xbox = InputSystem.AddDevice<XInputController>();
            var ps = InputSystem.AddDevice<DualSenseGamepadHID>();
            var move = actions.FindAction("Character/Move", true);
            move.Enable();

            for (var i = 0; i < 12; i++)
            {
                yield return Wake(i % 2 == 0 ? (Gamepad)xbox : ps);
                Set((i % 2 == 0 ? xbox : (Gamepad)ps).leftStick, new Vector2(0f, 0.9f));
                yield return null;
                yield return WakeKeyboard();
                Set(xbox.leftStick, Vector2.zero);
                Set(ps.leftStick, Vector2.zero);
                yield return null;
            }

            Assert.That(active.Family, Is.EqualTo(InputFamily.KeyboardMouse));
            Assert.That(move.ReadValue<Vector2>(), Is.EqualTo(Vector2.zero), "Move is stuck after repeated switching");
            Assert.That(actions.bindingMask, Is.EqualTo((InputBinding?)Mask("KeyboardMouse")));
        }

        [UnityTest]
        public IEnumerator SwitchingToAController_WhileAKeyIsHeld_ReleasesTheKeyCleanly()
        {
            var move = actions.FindAction("Character/Move", true);
            move.Enable();
            Press(keyboard.wKey);
            yield return null;
            Assert.That(move.ReadValue<Vector2>().y, Is.GreaterThan(0.5f));

            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);
            yield return null;
            Assert.That(move.ReadValue<Vector2>(), Is.EqualTo(Vector2.zero), "The held W still drives Move after the switch");

            Release(keyboard.wKey);
            yield return WakeKeyboard();
            Press(keyboard.wKey);
            yield return null;
            Assert.That(move.ReadValue<Vector2>().y, Is.GreaterThan(0.5f), "Keyboard control did not come back");
            Release(keyboard.wKey);
        }

        [UnityTest]
        public IEnumerator AfterWaking_TheControllerDrivesActions()
        {
            var pad = InputSystem.AddDevice<XInputController>();
            var confirm = actions.FindAction("Commands/Confirm", true);
            confirm.Enable();
            var performed = 0;
            confirm.performed += _ => performed++;

            yield return Wake(pad);
            var before = performed;
            Press(pad.buttonSouth);
            yield return null;
            Release(pad.buttonSouth);
            yield return null;

            Assert.That(performed - before, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Nintendo_ConfirmIsTheEastButton_NotSouth()
        {
            var pad = InputSystem.AddDevice<SwitchProControllerHID>();
            var confirm = actions.FindAction("Commands/Confirm", true);
            var cancel = actions.FindAction("Commands/Cancel", true);
            confirm.Enable();
            cancel.Enable();
            var confirmed = 0;
            var cancelled = 0;
            confirm.performed += _ => confirmed++;
            cancel.performed += _ => cancelled++;

            yield return Wake(pad);
            Press(pad.buttonEast);
            yield return null;
            Release(pad.buttonEast);
            yield return null;
            Press(pad.buttonSouth);
            yield return null;
            Release(pad.buttonSouth);
            yield return null;

            Assert.That(confirmed, Is.EqualTo(1), "East confirms on a Nintendo controller");
            Assert.That(cancelled, Is.EqualTo(1), "South cancels on a Nintendo controller");
        }

        [UnityTest]
        public IEnumerator Disabling_ClearsTheMaskAndTheDeviceRestriction()
        {
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);
            host.SetActive(false);
            yield return null;

            Assert.That(actions.bindingMask.HasValue, Is.False);
            Assert.That(actions.devices.HasValue, Is.False);
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.ActiveInputDeviceTests"`
Expected: EXIT=1, `error CS0246: ... ActiveInputDevice`.

- [ ] **Step 3: Implement `ActiveInputDevice.cs`**

```csharp
using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace Blackglass
{
    /// <summary>
    /// Tracks which input family the player is using (keyboard/mouse, Xbox, PlayStation, Nintendo or a generic
    /// gamepad) and makes the input actions follow it: the asset's binding mask selects that family's binding group, and
    /// for a controller the asset is restricted to that one device, so a second connected pad cannot also drive the game.
    /// Switching needs meaningful input (see InputActivityFilter), so stick drift, mouse jitter and held controls never
    /// flip it. The change is applied on the next Update, so the input that woke a family does not also fire an action.
    /// Runs on input events, not scaled time, so it works while tactically paused. Lives on Systems.
    /// </summary>
    [DefaultExecutionOrder(-300)] // before the input components read their actions this frame
    public sealed class ActiveInputDevice : MonoBehaviour
    {
        [SerializeField] InputActionAsset controls;

        readonly InputActivityFilter filter = new InputActivityFilter();
        bool bindingsDirty;

        public InputFamily Family { get; private set; } = InputFamily.KeyboardMouse;

        /// <summary>The more precise controller identity (None for keyboard/mouse). Gameplay never reads it.</summary>
        public ControllerModel Model { get; private set; } = ControllerModel.None;

        /// <summary>The active controller, or null while keyboard/mouse is active.</summary>
        public InputDevice Device { get; private set; }

        /// <summary>Raised with the new family whenever the family or the active controller changes.</summary>
        public event Action<InputFamily> Changed;

        internal void Initialize(InputActionAsset actions) => controls = actions;

        void OnEnable()
        {
            Family = InputFamily.KeyboardMouse;
            Model = ControllerModel.None;
            Device = null;
            filter.Reset();
            ApplyBindings();
            InputSystem.onEvent += OnInputEvent;
            InputSystem.onDeviceChange += OnDeviceChange;
        }

        void OnDisable()
        {
            InputSystem.onEvent -= OnInputEvent;
            InputSystem.onDeviceChange -= OnDeviceChange;
            bindingsDirty = false;
            if (controls == null)
                return;
            controls.bindingMask = null;
            controls.devices = null;
        }

        void Update()
        {
            if (bindingsDirty)
                ApplyBindings();
        }

        void OnInputEvent(InputEventPtr eventPtr, InputDevice device)
        {
            if (!eventPtr.IsA<StateEvent>() && !eventPtr.IsA<DeltaStateEvent>())
                return;
            if (!InputFamilyClassifier.TryClassify(device, out var family, out var model))
                return;
            if (!IsMeaningful(eventPtr, device))
                return;
            if (family == Family && (!family.IsController() || device == Device))
                return;
            SwitchTo(family, device, model);
        }

        void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            var gone = change == InputDeviceChange.Removed || change == InputDeviceChange.Disconnected;
            if (gone && Family.IsController() && device == Device)
                SwitchTo(InputFamily.KeyboardMouse, null, ControllerModel.None);
        }

        void SwitchTo(InputFamily family, InputDevice device, ControllerModel model)
        {
            Family = family;
            Model = model;
            Device = family.IsController() ? device : null;
            bindingsDirty = true;
            Changed?.Invoke(family);
        }

        void ApplyBindings()
        {
            bindingsDirty = false;
            if (controls == null)
                return;
            controls.bindingMask = InputBinding.MaskByGroup(Family.BindingGroup());
            if (Family.IsController() && Device != null)
                controls.devices = new InputDevice[] { Device };
            else
                controls.devices = null;
        }

        // Evaluates every stick first so the filter always sees them, whatever else in the event already qualified.
        bool IsMeaningful(InputEventPtr eventPtr, InputDevice device)
        {
            var meaningful = false;
            switch (device)
            {
                case Gamepad gamepad:
                    meaningful |= StickEngaged(gamepad.leftStick, device.deviceId * 4, eventPtr);
                    meaningful |= StickEngaged(gamepad.rightStick, device.deviceId * 4 + 1, eventPtr);
                    break;
                case Mouse mouse:
                    meaningful |= mouse.delta.ReadValueFromEvent(eventPtr, out var delta) && filter.IsMouseMotion(delta);
                    meaningful |= mouse.scroll.ReadValueFromEvent(eventPtr, out var scroll) && filter.IsScroll(scroll);
                    break;
            }
            return meaningful || AnyButtonPressed(device, eventPtr);
        }

        bool StickEngaged(StickControl stick, int key, InputEventPtr eventPtr) =>
            stick.ReadValueFromEvent(eventPtr, out var value) && filter.IsStickEngagement(key, value);

        // A button counts on its press edge: the event value is past the press point and the device's current
        // (pre-event) value is not. Stick direction buttons are derived from the stick and are judged as the stick.
        bool AnyButtonPressed(InputDevice device, InputEventPtr eventPtr)
        {
            foreach (var control in device.allControls)
            {
                if (!(control is ButtonControl button) || button.synthetic || button.noisy || button.parent is StickControl)
                    continue;
                if (button.ReadValueFromEvent(eventPtr, out var after) && filter.IsButtonPress(button.ReadValue(), after))
                    return true;
            }
            return false;
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.ActiveInputDeviceTests"`
Expected: EXIT=0, `passed="20"`.
If a switch test fails because the first wake press also fires an action or the mask is not applied yet, check that every test `yield return null`s after queueing input (the mask is applied in `Update`). If `InputSystem.AddDevice<DualSenseGamepadHID>()` or `<SwitchProControllerHID>()` fails to create a device in the fixture, use the by-name overload (`InputSystem.AddDevice("DualSenseGamepadHID")` cast to `Gamepad`) and report it.
If `ReadValueFromEvent` is `false` for a button in a delta event (so `Press(pad.startButton)` is not detected), report it with the failing test: the fallback is to read the button from `device.currentStatePtr` after `onAfterUpdate`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project/Scripts/Input Assets/_Project/Tests/PlayMode
git commit -m "Detect the active input family from input events and apply its binding group

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Prompt resolver (what to show for action X on the active family)

**Files:**
- Create: `Assets/_Project/Scripts/Input/GamepadLabels.cs`
- Create: `Assets/_Project/Scripts/Input/PromptResolver.cs`
- Test: `Assets/_Project/Tests/EditMode/PromptResolverTests.cs`

**Interfaces:**
- Consumes: `InputFamily`, `BindingGroup()` (Task 1); the asset (Task 3).
- Produces: `static string GamepadLabels.Label(string controlName, InputFamily family)`; `static string PromptResolver.GetPrompt(InputAction action, InputFamily family)` → the family's binding text, `" / "`-joined when several, `"-"` when the action has none (or is null). Gamepad text is placeholder (no glyph art); keyboard/mouse text is the Input System's human-readable path.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    public class PromptResolverTests
    {
        const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        static InputActionAsset Load() => AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);

        static string Prompt(string actionPath, InputFamily family) =>
            PromptResolver.GetPrompt(Load().FindAction(actionPath, throwIfNotFound: true), family);

        [TestCase("Commands/Confirm", InputFamily.Xbox, "A")]
        [TestCase("Commands/Confirm", InputFamily.PlayStation, "Cross")]
        [TestCase("Commands/Confirm", InputFamily.Nintendo, "A")]
        [TestCase("Commands/Confirm", InputFamily.GenericGamepad, "South")]
        [TestCase("Commands/Cancel", InputFamily.Xbox, "B")]
        [TestCase("Commands/Cancel", InputFamily.PlayStation, "Circle")]
        [TestCase("Commands/Cancel", InputFamily.Nintendo, "B")]
        [TestCase("Commands/Cancel", InputFamily.GenericGamepad, "East")]
        [TestCase("Commands/Attack", InputFamily.Xbox, "X")]
        [TestCase("Commands/Attack", InputFamily.PlayStation, "Square")]
        [TestCase("Commands/Attack", InputFamily.Nintendo, "Y")]
        [TestCase("Character/ToggleCharacterControl", InputFamily.Xbox, "Y")]
        [TestCase("Character/ToggleCharacterControl", InputFamily.PlayStation, "Triangle")]
        [TestCase("Character/ToggleCharacterControl", InputFamily.Nintendo, "X")]
        [TestCase("Character/NextCharacter", InputFamily.Xbox, "RB")]
        [TestCase("Character/NextCharacter", InputFamily.PlayStation, "R1")]
        [TestCase("Character/NextCharacter", InputFamily.Nintendo, "R")]
        [TestCase("Commands/QueueModifier", InputFamily.Xbox, "LT")]
        [TestCase("Commands/QueueModifier", InputFamily.PlayStation, "L2")]
        [TestCase("Commands/QueueModifier", InputFamily.Nintendo, "ZL")]
        [TestCase("Camera/CameraModifier", InputFamily.Nintendo, "ZR")]
        [TestCase("Commands/ToggleTacticalPause", InputFamily.Xbox, "View")]
        [TestCase("Commands/ToggleTacticalPause", InputFamily.PlayStation, "Create")]
        [TestCase("Commands/ToggleTacticalPause", InputFamily.Nintendo, "-")]
        [TestCase("Character/ToggleFollow", InputFamily.Xbox, "D-pad Up")]
        [TestCase("Character/Move", InputFamily.Xbox, "Left Stick")]
        [TestCase("Commands/CursorMove", InputFamily.PlayStation, "Right Stick")]
        public void GetPrompt_UsesTheFamilysOwnLabels(string actionPath, InputFamily family, string expected)
        {
            Assert.That(Prompt(actionPath, family), Is.EqualTo(expected));
        }

        [Test]
        public void KeyboardMouse_ShowsTheHumanReadableBinding()
        {
            Assert.That(Prompt("Commands/Command", InputFamily.KeyboardMouse), Does.Contain("Left Button"));
            Assert.That(Prompt("Commands/ToggleTacticalPause", InputFamily.KeyboardMouse), Does.Contain("Space"));
            Assert.That(Prompt("Commands/QueueModifier", InputFamily.KeyboardMouse), Does.Contain("Shift"));
            Assert.That(Prompt("Character/Move", InputFamily.KeyboardMouse), Is.EqualTo("WASD"));
        }

        [Test]
        public void ActionWithoutABindingInTheFamily_ShowsADash()
        {
            Assert.That(Prompt("Commands/Command", InputFamily.Xbox), Is.EqualTo("-"));
            Assert.That(Prompt("Commands/Confirm", InputFamily.KeyboardMouse), Is.EqualTo("-"));
            Assert.That(PromptResolver.GetPrompt(null, InputFamily.Xbox), Is.EqualTo("-"));
        }

        [Test]
        public void UnknownControlName_FallsBackToTheName()
        {
            Assert.That(GamepadLabels.Label("buttonFoo", InputFamily.Xbox), Is.EqualTo("buttonFoo"));
        }

        [Test]
        public void ARebind_ChangesThePrompt_ForThatFamilyOnly()
        {
            var copy = InputActionAsset.FromJson(Load().ToJson());
            try
            {
                var attack = copy.FindAction("Commands/Attack", throwIfNotFound: true);
                var xboxIndex = Enumerable.Range(0, attack.bindings.Count).First(i => attack.bindings[i].groups == "Xbox");
                attack.ApplyBindingOverride(xboxIndex, "<Gamepad>/buttonNorth");

                Assert.That(PromptResolver.GetPrompt(attack, InputFamily.Xbox), Is.EqualTo("Y"));
                Assert.That(PromptResolver.GetPrompt(attack, InputFamily.PlayStation), Is.EqualTo("Square"));
            }
            finally
            {
                Object.DestroyImmediate(copy);
            }
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.PromptResolverTests"`
Expected: EXIT=1, `error CS0103: ... PromptResolver`.

- [ ] **Step 3: Implement `GamepadLabels.cs`**

```csharp
namespace Blackglass
{
    /// <summary>
    /// Placeholder prompt text for a gamepad control, per input family. Nintendo's face buttons are labelled by their
    /// printed letters, which are not in Xbox's positions (south is B, east is A, north is X, west is Y). Text only: no
    /// glyph artwork. Unknown control names come back unchanged, so a new binding never throws.
    /// </summary>
    public static class GamepadLabels
    {
        public static string Label(string controlName, InputFamily family)
        {
            switch (controlName)
            {
                case "buttonSouth": return Pick(family, "A", "Cross", "B", "South");
                case "buttonEast": return Pick(family, "B", "Circle", "A", "East");
                case "buttonWest": return Pick(family, "X", "Square", "Y", "West");
                case "buttonNorth": return Pick(family, "Y", "Triangle", "X", "North");
                case "leftShoulder": return Pick(family, "LB", "L1", "L", "Left Shoulder");
                case "rightShoulder": return Pick(family, "RB", "R1", "R", "Right Shoulder");
                case "leftTrigger": return Pick(family, "LT", "L2", "ZL", "Left Trigger");
                case "rightTrigger": return Pick(family, "RT", "R2", "ZR", "Right Trigger");
                case "select": return Pick(family, "View", "Create", "-", "Select");
                case "start": return Pick(family, "Menu", "Options", "+", "Start");
                case "leftStickPress": return Pick(family, "LS Click", "L3", "L Stick Click", "Left Stick Press");
                case "rightStickPress": return Pick(family, "RS Click", "R3", "R Stick Click", "Right Stick Press");
                case "leftStick": return "Left Stick";
                case "rightStick": return "Right Stick";
                case "dpad": return "D-pad";
                case "dpad/up": return "D-pad Up";
                case "dpad/down": return "D-pad Down";
                case "dpad/left": return "D-pad Left";
                case "dpad/right": return "D-pad Right";
                default: return controlName;
            }
        }

        static string Pick(InputFamily family, string xbox, string playStation, string nintendo, string generic)
        {
            switch (family)
            {
                case InputFamily.Xbox: return xbox;
                case InputFamily.PlayStation: return playStation;
                case InputFamily.Nintendo: return nintendo;
                default: return generic;
            }
        }
    }
}
```

- [ ] **Step 4: Implement `PromptResolver.cs`**

```csharp
using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// Answers "what should be shown for this action on this input family?" from the action's current bindings, so a
    /// rebind changes the prompt and no code hard-codes "Press A". Keyboard/mouse text is the Input System's readable
    /// path; gamepad text comes from GamepadLabels. Placeholder text only.
    /// </summary>
    public static class PromptResolver
    {
        const string None = "-";

        public static string GetPrompt(InputAction action, InputFamily family)
        {
            if (action == null)
                return None;
            var group = family.BindingGroup();
            var prompts = new List<string>();
            var bindings = action.bindings;
            for (var i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];
                if (binding.isPartOfComposite || !InGroup(binding, group))
                    continue;
                prompts.Add(Describe(binding, family));
            }
            return prompts.Count == 0 ? None : string.Join(" / ", prompts);
        }

        static bool InGroup(InputBinding binding, string group)
        {
            if (string.IsNullOrEmpty(binding.groups))
                return false;
            foreach (var candidate in binding.groups.Split(InputBinding.Separator))
            {
                if (candidate == group)
                    return true;
            }
            return false;
        }

        static string Describe(InputBinding binding, InputFamily family)
        {
            if (binding.isComposite)
                return binding.name;
            var path = binding.effectivePath;
            if (family == InputFamily.KeyboardMouse)
                return InputControlPath.ToHumanReadableString(path, InputControlPath.HumanReadableStringOptions.OmitDevice);
            var slash = path.IndexOf('/');
            return GamepadLabels.Label(slash < 0 ? path : path.Substring(slash + 1), family);
        }
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.PromptResolverTests"`
Expected: EXIT=0, `passed="31"` (27 cases + 4 methods).
If a keyboard/mouse assertion fails on exact wording (`InputControlPath.ToHumanReadableString` may print `Left Button` as `Left Button`/`LMB`), loosen only that `Does.Contain` to the printed text and say so in the commit message.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/Input Assets/_Project/Tests/EditMode
git commit -m "Resolve placeholder prompts for an action on the active input family

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Stick helpers and controller camera control

**Files:**
- Create: `Assets/_Project/Scripts/Input/StickResponse.cs`
- Create: `Assets/_Project/Scripts/Input/StickRole.cs`
- Modify: `Assets/_Project/Scripts/CameraControl/TacticalCameraController.cs`
- Test: `Assets/_Project/Tests/EditMode/StickResponseTests.cs`, `Assets/_Project/Tests/PlayMode/CameraGamepadTests.cs`

**Interfaces:**
- Consumes: asset actions `Camera/Look`, `Camera/CameraModifier`, `Camera/Zoom`, `Camera/Pan` with pad bindings (Task 3); `TestControls.UseGroup/Reset`.
- Produces:
  - `static Vector2 StickResponse.Curve(Vector2 value, float exponent)` — direction kept, magnitude clamped to 1 then raised to `max(1, exponent)`.
  - `static bool StickRole.CameraOwnsRightStick(bool isDriving, bool modifierHeld)` → `isDriving != modifierHeld`.
  - `TacticalCameraController.Initialize(Camera camera, InputActionReference pan, InputActionReference rotate, InputActionReference rotateDrag, InputActionReference pointerPosition, InputActionReference zoom, ActiveCharacter active = null, InputActionReference look = null, InputActionReference cameraModifier = null)`; serialized fields `lookAction`, `cameraModifierAction`.

- [ ] **Step 1: Write the failing pure tests**

`Assets/_Project/Tests/EditMode/StickResponseTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class StickResponseTests
    {
        [Test]
        public void Curve_OfZero_IsZero() => Assert.That(StickResponse.Curve(Vector2.zero, 2f), Is.EqualTo(Vector2.zero));

        [Test]
        public void Curve_KeepsTheDirection_AndShapesTheMagnitude()
        {
            var shaped = StickResponse.Curve(new Vector2(0.6f, 0.8f) * 0.5f, 2f);
            Assert.That(shaped.normalized.x, Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(shaped.magnitude, Is.EqualTo(0.25f).Within(1e-4f));
        }

        [Test]
        public void Curve_ClampsMagnitudeToOne() =>
            Assert.That(StickResponse.Curve(new Vector2(3f, 0f), 1.5f).magnitude, Is.EqualTo(1f).Within(1e-4f));

        [Test]
        public void Curve_NeverBelowLinear_ForAnExponentUnderOne() =>
            Assert.That(StickResponse.Curve(new Vector2(0.5f, 0f), 0.2f).magnitude, Is.EqualTo(0.5f).Within(1e-4f));

        [TestCase(false, false, false)]
        [TestCase(true, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, true, false)]
        public void RightStickRole_IsDrivingXorModifier(bool driving, bool modifier, bool cameraOwns) =>
            Assert.That(StickRole.CameraOwnsRightStick(driving, modifier), Is.EqualTo(cameraOwns));
    }
}
```

- [ ] **Step 2: Write the failing camera tests**

`Assets/_Project/Tests/PlayMode/CameraGamepadTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CameraGamepadTests : InputTestFixture
    {
        Gamepad pad;
        TestWorld world;
        InputActionAsset actions;
        TacticalCameraController controller;
        TacticalPause pause;
        ActiveCharacter activeCharacter;

        public override void Setup()
        {
            base.Setup();
            pad = InputSystem.AddDevice<Gamepad>();
            world = new TestWorld();
            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Gamepad");

            activeCharacter = world.Track(new GameObject("Player")).AddComponent<ActiveCharacter>();
            var rig = world.Track(new GameObject("CameraRig"));
            rig.SetActive(false);
            var cameraObject = new GameObject("Camera");
            cameraObject.transform.SetParent(rig.transform, false);
            var viewCamera = cameraObject.AddComponent<Camera>();
            controller = rig.AddComponent<TacticalCameraController>();
            controller.Initialize(viewCamera,
                TestControls.Ref(actions, "Camera/Pan"),
                TestControls.Ref(actions, "Camera/Rotate"),
                TestControls.Ref(actions, "Camera/RotateDrag"),
                TestControls.Ref(actions, "Camera/PointerPosition"),
                TestControls.Ref(actions, "Camera/Zoom"),
                activeCharacter,
                TestControls.Ref(actions, "Camera/Look"),
                TestControls.Ref(actions, "Camera/CameraModifier"));
            rig.SetActive(true);
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        void Drive()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(Vector3.zero);
            activeCharacter.Initialize(unit, pause);
            activeCharacter.SetTakeover(true);
        }

        IEnumerator HoldStick(Vector2 value, float seconds)
        {
            Set(pad.rightStick, value);
            yield return new WaitForSecondsRealtime(seconds);
            Set(pad.rightStick, Vector2.zero);
            yield return null;
        }

        [UnityTest]
        public IEnumerator RightStickX_WhileDriving_RotatesTheCamera()
        {
            Drive();
            var start = controller.Yaw;
            yield return HoldStick(new Vector2(1f, 0f), 0.3f);

            Assert.That(Mathf.Abs(Mathf.DeltaAngle(start, controller.Yaw)), Is.GreaterThan(15f));
        }

        [UnityTest]
        public IEnumerator RightStickUp_WhileDriving_TiltsTowardTheHorizon()
        {
            Drive();
            var start = controller.Pitch;
            yield return HoldStick(new Vector2(0f, 1f), 0.3f);

            Assert.That(controller.Pitch, Is.LessThan(start - 5f));
        }

        [UnityTest]
        public IEnumerator RightStick_InTacticalMode_LeavesTheCameraAlone()
        {
            var start = controller.Yaw;
            var startPitch = controller.Pitch;
            yield return HoldStick(new Vector2(1f, 1f), 0.3f);

            Assert.That(controller.Yaw, Is.EqualTo(start).Within(0.01f));
            Assert.That(controller.Pitch, Is.EqualTo(startPitch).Within(0.01f));
        }

        [UnityTest]
        public IEnumerator RightTriggerHeld_InTacticalMode_LetsTheRightStickRotateTheCamera()
        {
            var start = controller.Yaw;
            Press(pad.rightTrigger);
            yield return null;
            yield return HoldStick(new Vector2(1f, 0f), 0.3f);
            Release(pad.rightTrigger);
            yield return null;

            Assert.That(Mathf.Abs(Mathf.DeltaAngle(start, controller.Yaw)), Is.GreaterThan(15f));
        }

        [UnityTest]
        public IEnumerator RightTriggerHeld_WhilePaused_StillRotates_OnUnscaledTime()
        {
            Drive();
            pause.Pause();
            var start = controller.Yaw;
            Press(pad.rightTrigger);
            yield return null;
            yield return HoldStick(new Vector2(1f, 0f), 0.3f);
            Release(pad.rightTrigger);
            yield return null;

            Assert.That(Mathf.Abs(Mathf.DeltaAngle(start, controller.Yaw)), Is.GreaterThan(15f));
        }

        [UnityTest]
        public IEnumerator RightTriggerHeld_WhileDriving_HandsTheStickToTheCursor_SoTheCameraStays()
        {
            Drive();
            var start = controller.Yaw;
            Press(pad.rightTrigger);
            yield return null;
            yield return HoldStick(new Vector2(1f, 0f), 0.3f);
            Release(pad.rightTrigger);
            yield return null;

            Assert.That(controller.Yaw, Is.EqualTo(start).Within(0.01f));
        }

        [UnityTest]
        public IEnumerator StickDrift_BelowTheDeadZone_DoesNotRotateOrTilt()
        {
            Drive();
            var start = controller.Yaw;
            var startPitch = controller.Pitch;
            yield return HoldStick(new Vector2(0.12f, 0.1f), 0.4f);

            Assert.That(controller.Yaw, Is.EqualTo(start).Within(0.01f));
            Assert.That(controller.Pitch, Is.EqualTo(startPitch).Within(0.01f));
        }

        [UnityTest]
        public IEnumerator LeftStick_PansTheCamera_InProportionToTheDeflection()
        {
            var start = controller.transform.position;
            Set(pad.leftStick, new Vector2(0f, 1f));
            yield return new WaitForSecondsRealtime(0.25f);
            Set(pad.leftStick, Vector2.zero);
            yield return null;
            var full = controller.transform.position.z - start.z;

            var mid = controller.transform.position;
            Set(pad.leftStick, new Vector2(0f, 0.6f));
            yield return new WaitForSecondsRealtime(0.25f);
            Set(pad.leftStick, Vector2.zero);
            yield return null;
            var partial = controller.transform.position.z - mid.z;

            Assert.That(full, Is.GreaterThan(0.5f));
            Assert.That(partial, Is.GreaterThan(0.1f));
            Assert.That(partial, Is.LessThan(full * 0.8f), "Half deflection should pan clearly slower than full");
        }

        [UnityTest]
        public IEnumerator LeftStick_WhileDriving_DoesNotPan()
        {
            Drive();
            yield return new WaitForSecondsRealtime(0.6f);
            var start = controller.transform.position;
            Set(pad.leftStick, new Vector2(1f, 0f));
            yield return new WaitForSecondsRealtime(0.3f);
            Set(pad.leftStick, Vector2.zero);
            yield return null;

            Assert.That(TestWorld.HorizontalDistance(controller.transform.position, start), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator StickClicks_ZoomOutAndIn()
        {
            var start = controller.Distance;
            Press(pad.leftStickButton);
            yield return null;
            Release(pad.leftStickButton);
            yield return null;
            var zoomedOut = controller.Distance;
            Press(pad.rightStickButton);
            yield return null;
            Release(pad.rightStickButton);
            yield return null;

            Assert.That(zoomedOut, Is.GreaterThan(start), "L3 zooms out");
            Assert.That(controller.Distance, Is.LessThan(zoomedOut), "R3 zooms in");
        }
    }
}
#endif
```

- [ ] **Step 3: Run to verify both fail**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.StickResponseTests"` — Expected: EXIT=1 (`StickResponse` missing).

- [ ] **Step 4: Implement the helpers**

`Assets/_Project/Scripts/Input/StickResponse.cs`:

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Response curve for an analog stick after its dead zone: keeps the direction, clamps the magnitude to 1 and raises
    /// it to a power, so small deflections are fine and full deflection is full speed. Pure.
    /// </summary>
    public static class StickResponse
    {
        public static Vector2 Curve(Vector2 value, float exponent)
        {
            var magnitude = Mathf.Min(1f, value.magnitude);
            if (magnitude <= 0f)
                return Vector2.zero;
            return value.normalized * Mathf.Pow(magnitude, Mathf.Max(1f, exponent));
        }
    }
}
```

`Assets/_Project/Scripts/Input/StickRole.cs`:

```csharp
namespace Blackglass
{
    /// <summary>
    /// Who owns the right stick. Driving the active character, it looks around (camera); in tactical mode it moves the
    /// cursor. Holding the camera modifier swaps the two, so a controller can rotate the camera while planning and
    /// point while driving. The same "the mode decides" rule WASD follows (decision 008).
    /// </summary>
    public static class StickRole
    {
        public static bool CameraOwnsRightStick(bool isDriving, bool modifierHeld) => isDriving != modifierHeld;
    }
}
```

- [ ] **Step 5: Modify `TacticalCameraController`**

Add two fields after `zoomAction`:

```csharp
        [SerializeField] InputActionReference lookAction;
        [SerializeField] InputActionReference cameraModifierAction;
```

Add tuning fields after `dragTiltDegreesPerPixel`:

```csharp
        // Right-stick look (controller): degrees per second at full deflection, after the response curve.
        [SerializeField, Min(0f)] float lookYawDegreesPerSecond = 120f;
        [SerializeField, Min(0f)] float lookTiltDegreesPerSecond = 60f;
        [SerializeField, Min(1f)] float lookResponseExponent = 1.5f;
```

Change `Initialize` to:

```csharp
        internal void Initialize(Camera camera, InputActionReference pan, InputActionReference rotate,
            InputActionReference rotateDrag, InputActionReference pointerPosition, InputActionReference zoom,
            ActiveCharacter active = null, InputActionReference look = null, InputActionReference cameraModifier = null)
        {
            viewCamera = camera;
            panAction = pan;
            rotateAction = rotate;
            rotateDragAction = rotateDrag;
            pointerPositionAction = pointerPosition;
            zoomAction = zoom;
            activeCharacter = active;
            lookAction = look;
            cameraModifierAction = cameraModifier;
            lastSeenUnit = CurrentUnit;
        }
```

In `OnEnable` and `OnDisable` add `lookAction, cameraModifierAction` to both `InputActionUtility.SetEnabled(...)` argument lists (after `zoomAction`).

In `Update`, directly after the `var yaw = ...` line and **before** `if (dragDetector.IsPressed)`, insert:

```csharp
            // Right stick: look around while driving, or while the camera modifier hands it over in tactical mode.
            var cameraOwnsStick = StickRole.CameraOwnsRightStick(activeCharacter != null && activeCharacter.IsDriving,
                InputActionUtility.IsPressed(cameraModifierAction));
            if (cameraOwnsStick)
            {
                var look = StickResponse.Curve(InputActionUtility.Read<Vector2>(lookAction), lookResponseExponent);
                yaw += look.x * lookYawDegreesPerSecond * deltaTime;
                pitch = Mathf.Clamp(pitch - look.y * lookTiltDegreesPerSecond * deltaTime, minPitch, maxPitch);
            }
```

Update the class summary: add the sentence "A controller's right stick looks around while driving (and, with the camera modifier held, while planning); the left stick pans; the stick clicks zoom."

- [ ] **Step 6: Run both suites plus the existing camera tests**

```bash
Tools/run-tests.sh EditMode "Blackglass.Tests.StickResponseTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.CameraGamepadTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.TacticalCameraControllerTests"
```
Expected: EditMode `passed="8"` (4 curve tests + 4 role cases); camera gamepad `passed="10"`; existing camera tests all pass unchanged.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project Docs
git commit -m "Let the right stick look around and the stick clicks zoom the tactical camera

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Direct control from a controller (analog move, character switching, toggles)

**Files:**
- Modify: `Assets/_Project/Scripts/Controls/DirectControlInput.cs`
- Test: `Assets/_Project/Tests/PlayMode/DirectControlGamepadTests.cs`

**Interfaces:**
- Consumes: asset actions `Character/Move`, `ToggleCharacterControl`, `NextCharacter`, `PreviousCharacter`, `CycleReverse`, `ToggleFollow` (Task 3).
- Produces: `DirectControlInput.Initialize(ActiveCharacter active, Camera camera, InputActionReference move, InputActionReference takeover, UnitSelection unitSelection = null, InputActionReference cycle = null, InputActionReference reverse = null, InputActionReference toggleFollow = null, InputActionReference previous = null)`; serialized field `previousAction`. Behaviour: `PreviousCharacter` cycles backward exactly like Shift+Tab; `NextCharacter` still honours the `CycleReverse` modifier.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/PlayMode/DirectControlGamepadTests.cs`:

```csharp
#if UNITY_EDITOR
using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.Switch;
using UnityEngine.InputSystem.XInput;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class DirectControlGamepadTests : InputTestFixture
    {
        Keyboard keyboard;
        Gamepad pad;
        InputActionAsset actions;
        TestWorld world;
        CommandableUnit first;
        CommandableUnit second;
        CommandableUnit third;
        UnitSelection selection;
        TacticalPause pause;
        ActiveCharacter active;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            pad = InputSystem.AddDevice<XInputController>();
            world = new TestWorld();
            world.CreateEnvironment();
            first = world.CreateFriendlyFighter(new Vector3(-6f, 0f, -6f)).Unit;
            second = world.CreateFriendlyFighter(new Vector3(6f, 0f, -6f)).Unit;
            third = world.CreateFriendlyFighter(new Vector3(0f, 0f, -12f)).Unit;

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            var viewCamera = cameraObject.AddComponent<Camera>();

            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Xbox");
            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(first.GetComponent<SelectableUnit>(), second.GetComponent<SelectableUnit>(),
                third.GetComponent<SelectableUnit>());
            active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(first, pause, selection);
            var input = systems.AddComponent<DirectControlInput>();
            input.Initialize(active, viewCamera,
                TestControls.Ref(actions, "Character/Move"),
                TestControls.Ref(actions, "Character/ToggleCharacterControl"),
                selection,
                TestControls.Ref(actions, "Character/NextCharacter"),
                TestControls.Ref(actions, "Character/CycleReverse"),
                TestControls.Ref(actions, "Character/ToggleFollow"),
                TestControls.Ref(actions, "Character/PreviousCharacter"));
            systems.SetActive(true);
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator Tap(UnityEngine.InputSystem.Controls.ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        [UnityTest]
        public IEnumerator LeftStick_IsAnalog_HalfDeflectionIsSlowerThanFull()
        {
            active.SetTakeover(true);
            yield return null;

            Set(pad.leftStick, new Vector2(0f, 0.6f));
            yield return null;
            var half = first.MoveIntent;
            Set(pad.leftStick, new Vector2(0f, 1f));
            yield return null;
            var full = first.MoveIntent;

            Assert.That(half.z, Is.GreaterThan(0.3f).And.LessThan(0.8f), $"Half deflection gave {half}");
            Assert.That(full.magnitude, Is.EqualTo(1f).Within(0.02f));
            Assert.That(half.magnitude, Is.LessThan(full.magnitude), "Stick magnitude must not be flattened to 1");
        }

        [UnityTest]
        public IEnumerator LeftStick_Diagonal_KeepsItsDirectionAndNeverExceedsOne()
        {
            active.SetTakeover(true);
            yield return null;
            Set(pad.leftStick, new Vector2(1f, 1f));
            yield return null;

            Assert.That(first.MoveIntent.magnitude, Is.LessThanOrEqualTo(1.0001f));
            Assert.That(first.MoveIntent.x, Is.GreaterThan(0.3f));
            Assert.That(first.MoveIntent.z, Is.GreaterThan(0.3f));
        }

        [UnityTest]
        public IEnumerator StickDrift_DoesNotMoveTheCharacter()
        {
            active.SetTakeover(true);
            yield return null;
            Set(pad.leftStick, new Vector2(0.12f, 0.1f));
            yield return new WaitForSecondsRealtime(0.2f);

            Assert.That(first.MoveIntent, Is.EqualTo(Vector3.zero));
            Assert.That(first.transform.position.x, Is.EqualTo(-6f).Within(0.05f));
        }

        [UnityTest]
        public IEnumerator NextAndPreviousCharacter_CycleLikeTabAndShiftTab_AndWrap()
        {
            yield return Tap(pad.rightShoulder);
            Assert.That(active.Unit, Is.SameAs(second));
            Assert.That(selection.Selected, Has.Count.EqualTo(1));
            Assert.That(selection.Selected[0].Unit, Is.SameAs(second), "Switching selects the new active character");

            yield return Tap(pad.leftShoulder);
            Assert.That(active.Unit, Is.SameAs(first));
            yield return Tap(pad.leftShoulder);
            Assert.That(active.Unit, Is.SameAs(third), "Previous wraps from the first to the last");
            yield return Tap(pad.rightShoulder);
            Assert.That(active.Unit, Is.SameAs(first), "Next wraps from the last to the first");
        }

        [UnityTest]
        public IEnumerator NextCharacter_SkipsADeadCharacter()
        {
            second.GetComponent<Health>().TakeDamage(1000);
            yield return null;
            yield return Tap(pad.rightShoulder);

            Assert.That(active.Unit, Is.SameAs(third));
        }

        [UnityTest]
        public IEnumerator SwitchingCharacter_KeepsTheOldCharactersQueuedOrders()
        {
            var order = new MoveCommand(new Vector3(-6f, 0f, 2f));
            Assert.That(first.Issue(order), Is.True);
            yield return Tap(pad.rightShoulder);

            Assert.That(first.CurrentCommand, Is.SameAs(order));
        }

        [UnityTest]
        public IEnumerator ToggleCharacterControl_AndToggleFollow_WorkWhilePaused()
        {
            pause.Pause();
            Assert.That(active.IsTakeoverOn, Is.False);
            yield return Tap(pad.buttonNorth);
            Assert.That(active.IsTakeoverOn, Is.True);

            var follow = active.IsFollowOn;
            yield return Tap(pad.dpad.up);
            Assert.That(active.IsFollowOn, Is.EqualTo(!follow));
            Assert.That(pause.IsPaused, Is.True, "Controller toggles must not resume the game");
        }

        [UnityTest]
        public IEnumerator EveryFamily_TogglesControlAndFollow_FromItsOwnBindingGroup()
        {
            var families = new (Func<Gamepad> create, string group)[]
            {
                (() => InputSystem.AddDevice<DualSenseGamepadHID>(), "PlayStation"),
                (() => InputSystem.AddDevice<SwitchProControllerHID>(), "Nintendo"),
                (() => InputSystem.AddDevice<Gamepad>(), "Gamepad"),
            };
            foreach (var (create, group) in families)
            {
                var other = create();
                TestControls.UseGroup(actions, group);
                actions.devices = new InputDevice[] { other };
                yield return null;

                var control = active.IsTakeoverOn;
                yield return Tap(other.buttonNorth);
                Assert.That(active.IsTakeoverOn, Is.EqualTo(!control), $"{group}: north toggles character control");

                var follow = active.IsFollowOn;
                yield return Tap(other.dpad.up);
                Assert.That(active.IsFollowOn, Is.EqualTo(!follow), $"{group}: D-pad up toggles follow");

                var before = active.Unit;
                yield return Tap(other.rightShoulder);
                Assert.That(active.Unit, Is.Not.SameAs(before), $"{group}: right shoulder switches character");
            }
        }

        [UnityTest]
        public IEnumerator SwitchingToKeyboardMouseWhileTheStickIsHeld_LeavesNoMoveIntent_AndWasdWorks()
        {
            active.SetTakeover(true);
            yield return null;
            Set(pad.leftStick, new Vector2(0f, 1f));
            yield return null;
            Assert.That(first.MoveIntent.magnitude, Is.GreaterThan(0.9f));

            TestControls.UseGroup(actions, "KeyboardMouse");
            actions.devices = null;
            yield return null;
            yield return null;
            Assert.That(first.MoveIntent, Is.EqualTo(Vector3.zero), "The held stick still steers after the family changed");

            Set(pad.leftStick, Vector2.zero);
            yield return null;
            Press(keyboard.wKey);
            yield return null;
            yield return null;
            Assert.That(first.MoveIntent.magnitude, Is.GreaterThan(0.9f), "WASD does not drive after the switch");
            Release(keyboard.wKey);
            yield return null;
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.DirectControlGamepadTests"`
Expected: EXIT=1 (`Initialize` has no overload taking the 9th argument).

- [ ] **Step 3: Modify `DirectControlInput`**

Add the field after `toggleFollowAction`:

```csharp
        // Plain press: the previous character (the gamepad's counterpart of Shift+Tab).
        [SerializeField] InputActionReference previousAction;
```

Extend `Initialize` with a final parameter and assignment:

```csharp
        internal void Initialize(ActiveCharacter active, Camera camera, InputActionReference move,
            InputActionReference takeover, UnitSelection unitSelection = null, InputActionReference cycle = null,
            InputActionReference reverse = null, InputActionReference toggleFollow = null,
            InputActionReference previous = null)
        {
            activeCharacter = active;
            viewCamera = camera;
            moveAction = move;
            takeoverAction = takeover;
            selection = unitSelection;
            cycleAction = cycle;
            reverseAction = reverse;
            toggleFollowAction = toggleFollow;
            previousAction = previous;
        }
```

In `OnEnable` add `previousAction` to the `SetEnabled(true, ...)` list and, after the `cycleAction` subscription:

```csharp
            if (previousAction != null)
                previousAction.action.performed += OnPrevious;
```

In `OnDisable` add the matching unsubscription (`previousAction.action.performed -= OnPrevious;`) and `previousAction` to the `SetEnabled(false, ...)` list.

Replace `OnCycle` with:

```csharp
        void OnCycle(InputAction.CallbackContext context) =>
            CycleCharacter(InputActionUtility.IsPressed(reverseAction) ? -1 : 1);

        void OnPrevious(InputAction.CallbackContext context) => CycleCharacter(-1);

        void CycleCharacter(int direction)
        {
            if (activeCharacter == null)
                return;
            activeCharacter.Cycle(direction);
            var unit = activeCharacter.Unit;
            if (selection != null && unit != null && unit.TryGetComponent<SelectableUnit>(out var selectable)
                && ActiveCharacter.IsEligible(selectable))
                selection.Select(selectable);
        }
```

Update the class summary's first sentences to: "Direct-control input for the active character. V (or the controller's control toggle) toggles takeover. Tab / RB makes the next eligible friendly the active character and selects it; Shift+Tab / LB the previous one. While driving, WASD or the left stick (analog magnitude kept) becomes a camera-relative move intent ... F / D-pad up toggles the follow flag". Keep the rest of the summary.

- [ ] **Step 4: Run the new fixture and the existing direct-control suite**

```bash
Tools/run-tests.sh PlayMode "Blackglass.Tests.DirectControlGamepadTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.DirectControlInputTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.ActiveCharacterDeathPlayModeTests"
```
Expected: EXIT=0; the new fixture `passed="9"`; the existing suites unchanged.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project
git commit -m "Drive and switch characters from a controller, with analog movement

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Typed pointer target shared by the mouse and the cursor

A refactor with no behaviour change: the click handler's raycast + classification moves into `PointerTargetResolver`, and "do what the player pointed at" becomes `PlayerCommandInput.Act(PointerTarget)`, which Task 11's controller path will call too. The existing `PlayerCommandInputTests` are the safety net.

**Files:**
- Create: `Assets/_Project/Scripts/Input/PointerTarget.cs`
- Create: `Assets/_Project/Scripts/Input/PointerTargetResolver.cs`
- Modify: `Assets/_Project/Scripts/Controls/PlayerCommandInput.cs`
- Test: `Assets/_Project/Tests/PlayMode/PointerTargetResolverTests.cs`

**Interfaces:**
- Produces:
  - `enum PointerTargetKind { None, Friendly, Hostile, Cover, Ground }`
  - `readonly struct PointerTarget` — `Kind`, `Point` (world hit point), `Friendly` (`SelectableUnit`), `Hostile` (`Health`, living, not a selectable friendly), `Cover` (`CoverLocation`); `static PointerTarget None`, `OnFriendly(SelectableUnit, Vector3)`, `OnHostile(Health, Vector3)`, `OnCover(CoverLocation, Vector3)`, `OnGround(Vector3)`.
  - `static PointerTarget PointerTargetResolver.Resolve(Camera camera, Vector2 screenPoint, float maxDistance, LayerMask layers, CoverRegistry registry, float coverRadius)` — pass a null registry (or radius 0) for no cover lookup.
  - `PlayerCommandInput.Act(PointerTarget target)` (internal): friendly → select (or toggle with the queue modifier); anything else → `CommandResolver.Resolve` → `GroupOrders.Issue` (Append with the modifier, else Replace) to `OrderedUnits()`; `None` → nothing.

- [ ] **Step 1: Write the failing resolver tests**

`Assets/_Project/Tests/PlayMode/PointerTargetResolverTests.cs`:

```csharp
#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class PointerTargetResolverTests
    {
        static readonly Vector3 GroundPoint = new Vector3(2f, 0f, 2f);
        static readonly Vector3 CoverGroundPoint = new Vector3(-6f, 0f, 2f);

        TestWorld world;
        Camera viewCamera;
        SelectableUnit friendly;
        Health dummy;
        CoverLocation cover;
        CoverRegistry registry;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            friendly = world.CreateFriendly(new Vector3(-8f, 0f, -6f));
            dummy = world.CreateDummy(new Vector3(6f, 0f, 6f));
            var wall = world.CreateObstacle(CoverGroundPoint + new Vector3(0f, 0.45f, 1f), new Vector3(2f, 0.9f, 0.5f));
            cover = world.CreateCoverPoint(CoverGroundPoint, Vector3.forward, wall.GetComponent<Collider>());
            registry = world.CreateRegistry(cover);
            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();
        }

        [TearDown]
        public void TearDown() => world.Dispose();

        Vector2 ScreenPointOf(Vector3 worldPoint) => viewCamera.WorldToScreenPoint(worldPoint);

        PointerTarget Resolve(Vector3 worldPoint, CoverRegistry coverRegistry = null, float coverRadius = 0.5f,
            float maxDistance = 500f) =>
            PointerTargetResolver.Resolve(viewCamera, ScreenPointOf(worldPoint), maxDistance, ~0, coverRegistry, coverRadius);

        [Test]
        public void OverAFriendly_IsFriendly()
        {
            var target = Resolve(friendly.transform.position);
            Assert.That(target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            Assert.That(target.Friendly, Is.SameAs(friendly));
        }

        [Test]
        public void OverALivingHealth_IsHostile()
        {
            var target = Resolve(dummy.transform.position);
            Assert.That(target.Kind, Is.EqualTo(PointerTargetKind.Hostile));
            Assert.That(target.Hostile, Is.SameAs(dummy));
        }

        [Test]
        public void KilledTarget_ResolvesToGround()
        {
            dummy.TakeDamage(1000);
            Physics.SyncTransforms();
            var target = Resolve(new Vector3(6f, 0f, 6f));
            Assert.That(target.Kind, Is.EqualTo(PointerTargetKind.Ground));
        }

        [Test]
        public void NearACoverPoint_IsCover_WhenTheRegistryIsGiven()
        {
            var target = Resolve(CoverGroundPoint + new Vector3(0.3f, 0f, 0f), registry);
            Assert.That(target.Kind, Is.EqualTo(PointerTargetKind.Cover));
            Assert.That(target.Cover, Is.SameAs(cover));
        }

        [Test]
        public void NearACoverPoint_IsPlainGround_WithoutARegistryOrWithRadiusZero()
        {
            Assert.That(Resolve(CoverGroundPoint, null).Kind, Is.EqualTo(PointerTargetKind.Ground));
            Assert.That(Resolve(CoverGroundPoint + new Vector3(0.3f, 0f, 0f), registry, 0f).Kind, Is.EqualTo(PointerTargetKind.Ground));
        }

        [Test]
        public void OnOpenGround_IsGround_WithTheHitPoint()
        {
            var target = Resolve(GroundPoint, registry);
            Assert.That(target.Kind, Is.EqualTo(PointerTargetKind.Ground));
            Assert.That(Vector3.Distance(target.Point, GroundPoint), Is.LessThan(0.1f));
        }

        [Test]
        public void WhenTheRayHitsNothing_IsNone()
        {
            Assert.That(Resolve(GroundPoint, registry, 0.5f, maxDistance: 1f).Kind, Is.EqualTo(PointerTargetKind.None));
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.PointerTargetResolverTests"`
Expected: EXIT=1 (`PointerTarget` missing).

- [ ] **Step 3: Implement `PointerTarget.cs`**

```csharp
using UnityEngine;

namespace Blackglass
{
    public enum PointerTargetKind
    {
        None,
        Friendly,
        Hostile,
        Cover,
        Ground,
    }

    /// <summary>
    /// What a pointer (the mouse, or the controller's tactical cursor) is on: nothing, a friendly unit, a living
    /// hostile, a cover location or plain ground, plus the world point. Plain data; input turns it into a selection
    /// change or an order and never decides anything else.
    /// </summary>
    public readonly struct PointerTarget
    {
        PointerTarget(PointerTargetKind kind, Vector3 point, SelectableUnit friendly, Health hostile, CoverLocation cover)
        {
            Kind = kind;
            Point = point;
            Friendly = friendly;
            Hostile = hostile;
            Cover = cover;
        }

        public PointerTargetKind Kind { get; }
        public Vector3 Point { get; }
        public SelectableUnit Friendly { get; }
        public Health Hostile { get; }
        public CoverLocation Cover { get; }

        public static PointerTarget None => default;

        public static PointerTarget OnFriendly(SelectableUnit unit, Vector3 point) =>
            new PointerTarget(PointerTargetKind.Friendly, point, unit, null, null);

        public static PointerTarget OnHostile(Health hostile, Vector3 point) =>
            new PointerTarget(PointerTargetKind.Hostile, point, null, hostile, null);

        public static PointerTarget OnCover(CoverLocation cover, Vector3 point) =>
            new PointerTarget(PointerTargetKind.Cover, point, null, null, cover);

        public static PointerTarget OnGround(Vector3 point) =>
            new PointerTarget(PointerTargetKind.Ground, point, null, null, null);
    }
}
```

- [ ] **Step 4: Implement `PointerTargetResolver.cs`**

```csharp
using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Turns a screen point into a PointerTarget: a friendly unit, a living hostile, a cover location within
    /// coverRadius of the hit point, or plain ground. The one place a pointer is classified, so the mouse click and the
    /// controller cursor agree. Raycasts the physics scene; while paused no physics step runs, so moved transforms are
    /// pushed to physics first.
    /// </summary>
    public static class PointerTargetResolver
    {
        static readonly Func<CoverLocation, bool> acceptAny = _ => true;

        public static PointerTarget Resolve(Camera camera, Vector2 screenPoint, float maxDistance, LayerMask layers,
            CoverRegistry registry, float coverRadius)
        {
            if (camera == null)
                return PointerTarget.None;

            Physics.SyncTransforms();
            var ray = camera.ScreenPointToRay(screenPoint);
            if (!Physics.Raycast(ray, out var hit, maxDistance, layers, QueryTriggerInteraction.Ignore))
                return PointerTarget.None;

            var friendly = hit.collider.GetComponentInParent<SelectableUnit>();
            if (friendly != null)
                return PointerTarget.OnFriendly(friendly, hit.point);

            var health = hit.collider.GetComponentInParent<Health>();
            if (health != null && health.IsAlive)
                return PointerTarget.OnHostile(health, hit.point);

            if (registry != null && coverRadius > 0f
                && CoverRules.TryChooseNearest(registry.Points, hit.point, coverRadius, acceptAny, out var cover))
                return PointerTarget.OnCover(cover, hit.point);

            return PointerTarget.OnGround(hit.point);
        }
    }
}
```

- [ ] **Step 5: Refactor `PlayerCommandInput` to use it**

Remove `using System;` and the `static readonly Func<CoverLocation, bool> acceptAny = _ => true;` field. Replace `HandleClick` with:

```csharp
        void HandleClick(Vector2 screenPoint)
        {
            if (viewCamera == null)
                return;
            Act(PointerTargetResolver.Resolve(viewCamera, screenPoint, maxClickDistance, clickableLayers, coverRegistry,
                coverClickRadius));
        }

        // The one "do what the player pointed at" path: a mouse click and the controller cursor both end here. A friendly
        // unit is selected (the queue modifier adds or removes it); anything else is an order, queued with the modifier.
        internal void Act(PointerTarget target)
        {
            switch (target.Kind)
            {
                case PointerTargetKind.None:
                    return;
                case PointerTargetKind.Friendly:
                    if (selection == null)
                        return;
                    if (ModifierHeld)
                        selection.Toggle(target.Friendly);
                    else
                        selection.Select(target.Friendly);
                    return;
                default:
                    var command = CommandResolver.Resolve(target.Hostile, target.Point, target.Cover);
                    GroupOrders.Issue(OrderedUnits(), command, ModifierHeld ? IssueMode.Append : IssueMode.Replace, groupSpacing);
                    return;
            }
        }
```

Leave every other member (fields, `Initialize`, drag handling, `OrderedUnits`, `SelectedUnits`) unchanged.

- [ ] **Step 6: Run the resolver tests and every suite that exercises clicks**

```bash
Tools/run-tests.sh PlayMode "Blackglass.Tests.PointerTargetResolverTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.PlayerCommandInputTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.PrototypeSceneTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.CoverOrderPlayModeTests"
```
Expected: EXIT=0 for all; resolver `passed="7"`; the others pass unchanged (this is the no-regression proof for the refactor).

- [ ] **Step 7: Commit**

```bash
git add Assets/_Project
git commit -m "Share one typed pointer target between mouse clicks and the controller cursor

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Hostile target rules (best target and cycling)

**Files:**
- Create: `Assets/_Project/Scripts/Input/HostileTargets.cs`
- Test: `Assets/_Project/Tests/EditMode/HostileTargetsTests.cs`

**Interfaces:**
- Consumes: `Health` (`IsAlive`), `ControlCycle.NextIndex` (existing).
- Produces: `static class HostileTargets { bool IsValid(Health h); Health Best(IReadOnlyList<Health> hostiles, Vector3 origin, Vector3 facing); Health Cycle(IReadOnlyList<Health> hostiles, Vector3 origin, Health current, int direction); }`
  - `IsValid`: not null/destroyed, alive, active in the hierarchy.
  - `Best`: lowest `flatDistance * (1 + 2 * angleFromFacing / 180°)`; a zero `facing` means nearest; null when none are valid.
  - `Cycle`: valid hostiles sorted by flat distance from `origin` (ties keep list order); the next (+1) or previous (-1) after `current`, wrapping; a `current` that is null or no longer valid starts before the first going forward and after the last going backward; null when none are valid.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class HostileTargetsTests
    {
        readonly List<GameObject> created = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created)
                Object.DestroyImmediate(go);
            created.Clear();
        }

        Health Hostile(float x, float z)
        {
            var go = new GameObject("Hostile");
            go.transform.position = new Vector3(x, 0f, z);
            var health = go.AddComponent<Health>();
            health.Initialize(100);
            created.Add(go);
            return health;
        }

        static readonly Vector3 Origin = Vector3.zero;

        [Test]
        public void Best_WithNoFacing_IsTheNearest()
        {
            var far = Hostile(0f, 9f);
            var near = Hostile(0f, -3f);
            Assert.That(HostileTargets.Best(new[] { far, near }, Origin, Vector3.zero), Is.SameAs(near));
        }

        [Test]
        public void Best_PrefersTheOneAheadOverANearerOneBehind_UpToAPoint()
        {
            var behind = Hostile(0f, -5f);
            var ahead = Hostile(0f, 8f);
            Assert.That(HostileTargets.Best(new[] { behind, ahead }, Origin, Vector3.forward), Is.SameAs(ahead));

            var veryNearBehind = Hostile(0f, -2f);
            var farAhead = Hostile(0f, 20f);
            Assert.That(HostileTargets.Best(new[] { veryNearBehind, farAhead }, Origin, Vector3.forward), Is.SameAs(veryNearBehind));
        }

        [Test]
        public void Best_IgnoresHeight_WhenFacing()
        {
            var near = Hostile(0f, 4f);
            near.transform.position += Vector3.up * 5f;
            var far = Hostile(0f, 9f);
            Assert.That(HostileTargets.Best(new[] { far, near }, Origin, new Vector3(0f, 3f, 1f)), Is.SameAs(near));
        }

        [Test]
        public void Best_SkipsDeadInactiveAndNullEntries()
        {
            var dead = Hostile(0f, 1f);
            dead.TakeDamage(1000);
            var hidden = Hostile(0f, 2f);
            hidden.gameObject.SetActive(false);
            var alive = Hostile(0f, 10f);
            Assert.That(HostileTargets.Best(new[] { dead, null, hidden, alive }, Origin, Vector3.zero), Is.SameAs(alive));
        }

        [Test]
        public void Best_OfNothing_IsNull()
        {
            Assert.That(HostileTargets.Best(new Health[0], Origin, Vector3.forward), Is.Null);
            var dead = Hostile(0f, 1f);
            dead.TakeDamage(1000);
            Assert.That(HostileTargets.Best(new[] { dead }, Origin, Vector3.forward), Is.Null);
        }

        [Test]
        public void Cycle_GoesByDistance_AndWrapsBothWays()
        {
            var far = Hostile(9f, 0f);
            var near = Hostile(2f, 0f);
            var mid = Hostile(0f, 5f);
            var list = new[] { far, near, mid };

            Assert.That(HostileTargets.Cycle(list, Origin, null, 1), Is.SameAs(near), "Forward from nothing: nearest");
            Assert.That(HostileTargets.Cycle(list, Origin, near, 1), Is.SameAs(mid));
            Assert.That(HostileTargets.Cycle(list, Origin, mid, 1), Is.SameAs(far));
            Assert.That(HostileTargets.Cycle(list, Origin, far, 1), Is.SameAs(near), "Wraps forward");
            Assert.That(HostileTargets.Cycle(list, Origin, null, -1), Is.SameAs(far), "Backward from nothing: farthest");
            Assert.That(HostileTargets.Cycle(list, Origin, near, -1), Is.SameAs(far), "Wraps backward");
        }

        [Test]
        public void Cycle_SkipsDeadEntries_AndRecoversFromADeadCurrent()
        {
            var near = Hostile(2f, 0f);
            var mid = Hostile(0f, 5f);
            var far = Hostile(9f, 0f);
            var list = new[] { near, mid, far };
            mid.TakeDamage(1000);

            Assert.That(HostileTargets.Cycle(list, Origin, near, 1), Is.SameAs(far));
            Assert.That(HostileTargets.Cycle(list, Origin, mid, 1), Is.SameAs(near), "A dead current starts from the beginning");
        }

        [Test]
        public void Cycle_WithOneOrNone()
        {
            var only = Hostile(3f, 0f);
            Assert.That(HostileTargets.Cycle(new[] { only }, Origin, only, 1), Is.SameAs(only));
            Assert.That(HostileTargets.Cycle(new Health[0], Origin, null, 1), Is.Null);
            only.TakeDamage(1000);
            Assert.That(HostileTargets.Cycle(new[] { only }, Origin, null, -1), Is.Null);
        }

        [Test]
        public void Cycle_KeepsListOrderForEqualDistances()
        {
            var a = Hostile(3f, 0f);
            var b = Hostile(-3f, 0f);
            Assert.That(HostileTargets.Cycle(new[] { a, b }, Origin, null, 1), Is.SameAs(a));
            Assert.That(HostileTargets.Cycle(new[] { a, b }, Origin, a, 1), Is.SameAs(b));
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.HostileTargetsTests"`
Expected: EXIT=1 (`HostileTargets` missing).

- [ ] **Step 3: Implement**

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Pure target rules for the controller: which living hostile an Attack should pick (nearest, favouring the one the
    /// player is facing) and how NextTarget / PreviousTarget walk the living hostiles. Not a lock-on system: it keeps
    /// no state, and a hostile that dies or is destroyed simply stops being a candidate.
    /// </summary>
    public static class HostileTargets
    {
        // How strongly "behind me" counts against a candidate: a target directly behind scores (1 + this) times its distance.
        const float BehindPenalty = 2f;

        public static bool IsValid(Health hostile) =>
            hostile != null && hostile.IsAlive && hostile.gameObject.activeInHierarchy;

        public static Health Best(IReadOnlyList<Health> hostiles, Vector3 origin, Vector3 facing)
        {
            facing.y = 0f;
            var hasFacing = facing.sqrMagnitude > 1e-6f;
            Health best = null;
            var bestScore = float.PositiveInfinity;
            for (var i = 0; i < hostiles.Count; i++)
            {
                var candidate = hostiles[i];
                if (!IsValid(candidate))
                    continue;
                var offset = candidate.transform.position - origin;
                offset.y = 0f;
                var distance = offset.magnitude;
                var turn = hasFacing && distance > 1e-4f ? Vector3.Angle(facing, offset) / 180f : 0f;
                var score = distance * (1f + BehindPenalty * turn);
                if (score < bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }
            return best;
        }

        public static Health Cycle(IReadOnlyList<Health> hostiles, Vector3 origin, Health current, int direction)
        {
            var sorted = new List<(float distance, int order, Health health)>();
            for (var i = 0; i < hostiles.Count; i++)
            {
                if (IsValid(hostiles[i]))
                    sorted.Add((CoverRules.FlatDistance(origin, hostiles[i].transform.position), i, hostiles[i]));
            }
            if (sorted.Count == 0)
                return null;
            sorted.Sort((a, b) => a.distance != b.distance ? a.distance.CompareTo(b.distance) : a.order.CompareTo(b.order));

            var currentIndex = -1;
            for (var i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].health == current)
                {
                    currentIndex = i;
                    break;
                }
            }
            var next = ControlCycle.NextIndex(sorted.Count, currentIndex, direction, _ => true);
            return next < 0 ? null : sorted[next].health;
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.HostileTargetsTests"`
Expected: EXIT=0, `passed="9"`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project
git commit -m "Add pure best-target and target-cycling rules for the controller

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 10: `TacticalCursor`

**Files:**
- Create: `Assets/_Project/Scripts/Input/TacticalCursor.cs`
- Test: `Assets/_Project/Tests/PlayMode/TacticalCursorTests.cs`

**Interfaces:**
- Consumes: `PointerTarget`, `PointerTargetResolver` (Task 8); `HostileTargets` (Task 9); `StickResponse`, `StickRole` (Task 6); `CoverRules.TryChooseNearest/FlatDistance`; `ActiveCharacter.IsDriving/HasUnit/Unit`; `UnitSelection.Roster`; `Encounter.Hostiles`; `CoverRegistry.Points`; `ActiveInputDevice.Family` (Task 4); asset actions `Commands/CursorMove`, `Camera/CameraModifier`, `Commands/NextTarget`, `Commands/PreviousTarget`.
- Produces: `sealed class TacticalCursor : MonoBehaviour`:
  - `Vector2 ScreenPosition`, `PointerTarget Target`, `bool IsActive` (the family is a controller — or there is no `ActiveInputDevice` — and the camera does not own the right stick), `Health SoftTarget` (null once dead/destroyed/inactive).
  - `internal PointerTarget Refresh()` — re-resolve at the current position now (null-safe; `None` while inactive); `internal void SetScreenPosition(Vector2)` — clamp, store and re-resolve; `internal void CycleTarget(int direction)`; `Health PickAttackTarget(Vector3 origin, Vector3 facing)` — the cursor's hostile if the cursor is active and on one, else the soft target, else `HostileTargets.Best` (which also becomes the soft target); null when there is none.
  - `internal void Initialize(Camera camera, ActiveCharacter active, UnitSelection unitSelection, Encounter currentEncounter, CoverRegistry registry, ActiveInputDevice device, InputActionReference cursorMove, InputActionReference cameraModifier, InputActionReference nextTarget, InputActionReference previousTarget)`.
  - Serialized field names (the scene builder in Task 13 sets them): `viewCamera`, `activeCharacter`, `selection`, `encounter`, `coverRegistry`, `inputDevice`, `cursorMoveAction`, `cameraModifierAction`, `nextTargetAction`, `previousTargetAction`.
  - Behaviour: moves on `Time.unscaledDeltaTime`, snaps to friendly (1.2 m) then hostile (1.2 m) then cover (0.75 m) around the ground point under the cursor, never to a unit that is dead or inactive. A raycast that already hits a unit uses it directly. `NextTarget`/`PreviousTarget` set the soft target and, if the cursor is active, move the cursor onto it.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/PlayMode/TacticalCursorTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XInput;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class TacticalCursorTests : InputTestFixture
    {
        static readonly Vector3 FriendlyGround = new Vector3(-4f, 0f, -6f);
        static readonly Vector3 NearHostileGround = new Vector3(-6f, 0f, -6f);
        static readonly Vector3 FarHostileGround = new Vector3(6f, 0f, 6f);
        static readonly Vector3 CoverGroundPoint = new Vector3(-6f, 0f, 2f);
        static readonly Vector3 OpenGround = new Vector3(2f, 0f, 2f);

        Gamepad pad;
        InputActionAsset actions;
        TestWorld world;
        Camera viewCamera;
        SelectableUnit friendly;
        Health nearHostile;
        Health farHostile;
        CoverLocation cover;
        TacticalPause pause;
        ActiveCharacter active;
        TacticalCursor cursor;

        public override void Setup()
        {
            base.Setup();
            pad = InputSystem.AddDevice<XInputController>();
            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Xbox");
            world = new TestWorld();
            world.CreateEnvironment();
            friendly = world.CreateFriendly(FriendlyGround);
            nearHostile = world.CreateDummy(NearHostileGround);
            farHostile = world.CreateDummy(FarHostileGround);
            var wall = world.CreateObstacle(CoverGroundPoint + new Vector3(0f, 0.45f, 1f), new Vector3(2f, 0.9f, 0.5f));
            cover = world.CreateCoverPoint(CoverGroundPoint, Vector3.forward, wall.GetComponent<Collider>());
            var registry = world.CreateRegistry(cover);
            var encounter = world.CreateEncounter();
            // Far first in the list, so the distance ordering (not list order) is what the tests see.
            encounter.Initialize(new Health[0], new[] { farHostile, nearHostile });

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            var selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(friendly);
            active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(friendly.Unit, pause, selection);
            cursor = systems.AddComponent<TacticalCursor>();
            cursor.Initialize(viewCamera, active, selection, encounter, registry, null,
                TestControls.Ref(actions, "Commands/CursorMove"),
                TestControls.Ref(actions, "Camera/CameraModifier"),
                TestControls.Ref(actions, "Commands/NextTarget"),
                TestControls.Ref(actions, "Commands/PreviousTarget"));
            systems.SetActive(true);
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        Vector2 ScreenPointOf(Vector3 worldPoint) => viewCamera.WorldToScreenPoint(worldPoint);

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Stick_MovesTheCursor_OnUnscaledTime_WhilePaused()
        {
            pause.Pause();
            cursor.SetScreenPosition(new Vector2(50f, 200f));
            yield return null;
            var start = cursor.ScreenPosition;

            Set(pad.rightStick, new Vector2(1f, 0f));
            yield return new WaitForSecondsRealtime(0.2f);
            Set(pad.rightStick, Vector2.zero);
            yield return null;

            Assert.That(cursor.ScreenPosition.x - start.x, Is.GreaterThan(60f));
            Assert.That(cursor.ScreenPosition.y, Is.EqualTo(start.y).Within(1f));
            Assert.That(pause.IsPaused, Is.True);
        }

        [UnityTest]
        public IEnumerator StickDrift_DoesNotMoveTheCursor()
        {
            cursor.SetScreenPosition(new Vector2(200f, 200f));
            yield return null;
            var start = cursor.ScreenPosition;
            Set(pad.rightStick, new Vector2(0.12f, 0.1f));
            yield return new WaitForSecondsRealtime(0.3f);
            Set(pad.rightStick, Vector2.zero);

            Assert.That(cursor.ScreenPosition, Is.EqualTo(start));
        }

        [UnityTest]
        public IEnumerator Cursor_StaysOnTheScreen()
        {
            cursor.SetScreenPosition(new Vector2(viewCamera.pixelWidth * 0.5f, viewCamera.pixelHeight * 0.5f));
            Set(pad.rightStick, new Vector2(1f, 1f));
            yield return new WaitForSecondsRealtime(1.5f);
            Set(pad.rightStick, Vector2.zero);
            yield return null;

            Assert.That(cursor.ScreenPosition.x, Is.InRange(0f, viewCamera.pixelWidth));
            Assert.That(cursor.ScreenPosition.y, Is.InRange(0f, viewCamera.pixelHeight));
        }

        [UnityTest]
        public IEnumerator IsActive_FollowsTheRightStickRole()
        {
            yield return null;
            Assert.That(cursor.IsActive, Is.True, "Tactical mode: the cursor owns the right stick");

            active.SetTakeover(true);
            yield return null;
            Assert.That(cursor.IsActive, Is.False, "Driving: the camera owns it");
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.None), "An inactive cursor points at nothing");

            Press(pad.rightTrigger);
            yield return null;
            Assert.That(cursor.IsActive, Is.True, "RT hands the stick to the cursor while driving");
            Release(pad.rightTrigger);
            yield return null;
            Assert.That(cursor.IsActive, Is.False);

            pause.Pause();
            yield return null;
            Assert.That(cursor.IsActive, Is.True, "Paused is tactical mode");
        }

        [UnityTest]
        public IEnumerator Resolves_Friendly_Hostile_AndGround()
        {
            cursor.SetScreenPosition(ScreenPointOf(friendly.transform.position));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            Assert.That(cursor.Target.Friendly, Is.SameAs(friendly));

            cursor.SetScreenPosition(ScreenPointOf(farHostile.transform.position));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Hostile));
            Assert.That(cursor.Target.Hostile, Is.SameAs(farHostile));

            cursor.SetScreenPosition(ScreenPointOf(OpenGround));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Ground));
            yield return null;
        }

        [UnityTest]
        public IEnumerator SnapsToCoverWithin075Metres_ButNotBeyond()
        {
            cursor.SetScreenPosition(ScreenPointOf(CoverGroundPoint + new Vector3(0.4f, 0f, 0f)));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Cover));
            Assert.That(cursor.Target.Cover, Is.SameAs(cover));

            cursor.SetScreenPosition(ScreenPointOf(CoverGroundPoint + new Vector3(0.7f, 0f, 0f)));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Cover));

            cursor.SetScreenPosition(ScreenPointOf(CoverGroundPoint + new Vector3(1.4f, 0f, 0f)));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Ground));
            yield return null;
        }

        [UnityTest]
        public IEnumerator SnapsToUnitsWithin12Metres_ButNotBeyond()
        {
            cursor.SetScreenPosition(ScreenPointOf(FriendlyGround + new Vector3(0.8f, 0f, 0f)));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            Assert.That(cursor.Target.Friendly, Is.SameAs(friendly));

            cursor.SetScreenPosition(ScreenPointOf(FriendlyGround + new Vector3(2.5f, 0f, 0f)));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Ground));

            cursor.SetScreenPosition(ScreenPointOf(FarHostileGround + new Vector3(0.8f, 0f, 0f)));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Hostile));
            Assert.That(cursor.Target.Hostile, Is.SameAs(farHostile));

            cursor.SetScreenPosition(ScreenPointOf(FarHostileGround + new Vector3(3.5f, 0f, 0f)));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Ground));
            yield return null;
        }

        [UnityTest]
        public IEnumerator FriendlyBeatsHostile_WhenBothAreInSnapRange()
        {
            // Midway between the friendly (x = -4) and the near hostile (x = -6): one metre from each.
            cursor.SetScreenPosition(ScreenPointOf(new Vector3(-5f, 0f, -6f)));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            yield return null;
        }

        [UnityTest]
        public IEnumerator NextTarget_CyclesLivingHostilesByDistance_AndSnapsTheCursorOnto()
        {
            yield return Tap(pad.dpad.right);
            Assert.That(cursor.SoftTarget, Is.SameAs(nearHostile), "Nearest to the active character first");
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Hostile));
            Assert.That(cursor.Target.Hostile, Is.SameAs(nearHostile));

            yield return Tap(pad.dpad.right);
            Assert.That(cursor.SoftTarget, Is.SameAs(farHostile));
            yield return Tap(pad.dpad.right);
            Assert.That(cursor.SoftTarget, Is.SameAs(nearHostile), "Wraps");
            yield return Tap(pad.dpad.left);
            Assert.That(cursor.SoftTarget, Is.SameAs(farHostile), "Previous goes back");
        }

        [UnityTest]
        public IEnumerator TargetCycling_SkipsADeadHostile()
        {
            nearHostile.TakeDamage(1000);
            yield return Tap(pad.dpad.right);
            Assert.That(cursor.SoftTarget, Is.SameAs(farHostile));
            yield return Tap(pad.dpad.right);
            Assert.That(cursor.SoftTarget, Is.SameAs(farHostile), "The only living hostile");
        }

        [UnityTest]
        public IEnumerator TargetCycling_WithNoLivingHostiles_DoesNothing()
        {
            nearHostile.TakeDamage(1000);
            farHostile.TakeDamage(1000);
            yield return Tap(pad.dpad.right);
            yield return Tap(pad.dpad.left);

            Assert.That(cursor.SoftTarget, Is.Null);
        }

        [UnityTest]
        public IEnumerator SoftTarget_ClearsWhenItDiesOrIsDestroyed()
        {
            yield return Tap(pad.dpad.right);
            Assert.That(cursor.SoftTarget, Is.SameAs(nearHostile));
            nearHostile.TakeDamage(1000);
            Assert.That(cursor.SoftTarget, Is.Null, "Dead");

            yield return Tap(pad.dpad.right);
            Assert.That(cursor.SoftTarget, Is.SameAs(farHostile));
            Object.DestroyImmediate(farHostile.gameObject);
            Assert.That(cursor.SoftTarget, Is.Null, "Destroyed");
        }

        [UnityTest]
        public IEnumerator PickAttackTarget_PrefersTheCursorHostile_ThenTheSoftTarget_ThenTheBest()
        {
            var origin = friendly.transform.position;
            cursor.SetScreenPosition(ScreenPointOf(farHostile.transform.position));
            Assert.That(cursor.PickAttackTarget(origin, Vector3.forward), Is.SameAs(farHostile), "The cursor's hostile");

            cursor.SetScreenPosition(ScreenPointOf(OpenGround));
            yield return null;
            Assert.That(cursor.PickAttackTarget(origin, Vector3.forward), Is.SameAs(farHostile), "The soft target stays");

            farHostile.TakeDamage(1000);
            Assert.That(cursor.PickAttackTarget(origin, Vector3.forward), Is.SameAs(nearHostile), "Falls back to the best living hostile");

            nearHostile.TakeDamage(1000);
            Assert.That(cursor.PickAttackTarget(origin, Vector3.forward), Is.Null, "Nothing left to attack");
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.TacticalCursorTests"`
Expected: EXIT=1 (`TacticalCursor` missing).

- [ ] **Step 3: Implement `TacticalCursor.cs`**

```csharp
using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// The controller's tactical cursor: a screen-space pointer moved by the right stick (on unscaled time, so it works
    /// while paused). Each frame it resolves what it is on into a PointerTarget, snapping a little: to a friendly, then a
    /// hostile, then a cover location near the ground point under the cursor. Confirm and Attack read that target in
    /// PlayerCommandInput; the cursor itself issues no orders. It is active whenever a controller is in use and the
    /// camera does not own the right stick (see StickRole). Also holds the "soft target" that NextTarget and
    /// PreviousTarget cycle through living hostiles, and picks the hostile an Attack should use. Lives on Systems.
    /// </summary>
    public sealed class TacticalCursor : MonoBehaviour
    {
        static readonly Func<CoverLocation, bool> acceptAny = _ => true;

        [SerializeField] Camera viewCamera;
        [SerializeField] ActiveCharacter activeCharacter;
        [SerializeField] UnitSelection selection;
        [SerializeField] Encounter encounter;
        [SerializeField] CoverRegistry coverRegistry;
        // Optional: without it the cursor behaves as if a controller were in use (tests, scenes without detection).
        [SerializeField] ActiveInputDevice inputDevice;

        [Header("Input")]
        [SerializeField] InputActionReference cursorMoveAction;
        // Owned by TacticalCameraController (which disables it); this component only enables and reads it.
        [SerializeField] InputActionReference cameraModifierAction;
        [SerializeField] InputActionReference nextTargetAction;
        [SerializeField] InputActionReference previousTargetAction;

        [Header("Tuning")]
        [SerializeField, Min(0f)] float speedPixelsPerSecond = 900f;
        [SerializeField, Min(1f)] float responseExponent = 1.5f;
        // Within this distance of the ground point under the cursor, a unit is snapped to.
        [SerializeField, Min(0f)] float unitSnapRadius = 1.2f;
        // Smaller than the unit radius: cover points are 1 m apart, so a larger one would make every ground order
        // beside a wall a cover order.
        [SerializeField, Min(0f)] float coverSnapRadius = 0.75f;
        [SerializeField, Min(1f)] float maxDistance = 500f;
        [SerializeField] LayerMask clickableLayers = ~0;

        Vector2 screenPosition;
        bool hasPosition;
        PointerTarget target;
        Health softTarget;

        public Vector2 ScreenPosition => screenPosition;

        /// <summary>What the cursor is on as of the last update; None while the cursor is inactive.</summary>
        public PointerTarget Target => target;

        /// <summary>The hostile that NextTarget / PreviousTarget or a snap last chose, while it is still a valid target.</summary>
        public Health SoftTarget => HostileTargets.IsValid(softTarget) ? softTarget : null;

        public bool IsActive =>
            (inputDevice == null || inputDevice.Family.IsController())
            && !StickRole.CameraOwnsRightStick(activeCharacter != null && activeCharacter.IsDriving,
                InputActionUtility.IsPressed(cameraModifierAction));

        internal void Initialize(Camera camera, ActiveCharacter active, UnitSelection unitSelection,
            Encounter currentEncounter, CoverRegistry registry, ActiveInputDevice device,
            InputActionReference cursorMove, InputActionReference cameraModifier,
            InputActionReference nextTarget, InputActionReference previousTarget)
        {
            viewCamera = camera;
            activeCharacter = active;
            selection = unitSelection;
            encounter = currentEncounter;
            coverRegistry = registry;
            inputDevice = device;
            cursorMoveAction = cursorMove;
            cameraModifierAction = cameraModifier;
            nextTargetAction = nextTarget;
            previousTargetAction = previousTarget;
        }

        void OnEnable()
        {
            InputActionUtility.SetEnabled(true, cursorMoveAction, cameraModifierAction, nextTargetAction, previousTargetAction);
            if (nextTargetAction != null)
                nextTargetAction.action.performed += OnNextTarget;
            if (previousTargetAction != null)
                previousTargetAction.action.performed += OnPreviousTarget;
        }

        void OnDisable()
        {
            if (nextTargetAction != null)
                nextTargetAction.action.performed -= OnNextTarget;
            if (previousTargetAction != null)
                previousTargetAction.action.performed -= OnPreviousTarget;
            InputActionUtility.SetEnabled(false, cursorMoveAction, nextTargetAction, previousTargetAction);
        }

        void Update()
        {
            EnsurePosition();
            if (!IsActive)
            {
                target = PointerTarget.None;
                return;
            }
            var move = StickResponse.Curve(InputActionUtility.Read<Vector2>(cursorMoveAction), responseExponent);
            if (move != Vector2.zero)
                screenPosition = ClampToScreen(screenPosition + move * (speedPixelsPerSecond * Time.unscaledDeltaTime));
            Refresh();
        }

        /// <summary>Re-resolves what the cursor is on right now (None while inactive) and returns it.</summary>
        internal PointerTarget Refresh()
        {
            EnsurePosition();
            target = IsActive ? Resolve(screenPosition) : PointerTarget.None;
            if (target.Kind == PointerTargetKind.Hostile)
                softTarget = target.Hostile;
            return target;
        }

        internal void SetScreenPosition(Vector2 position)
        {
            screenPosition = ClampToScreen(position);
            hasPosition = true;
            Refresh();
        }

        /// <summary>
        /// The hostile an Attack should use: the one under the cursor (cursor active), else the soft target, else the
        /// best living hostile for someone at `origin` facing `facing`. That fallback also becomes the soft target so the
        /// view highlights it. Null when no living hostile exists.
        /// </summary>
        public Health PickAttackTarget(Vector3 origin, Vector3 facing)
        {
            if (IsActive && target.Kind == PointerTargetKind.Hostile && HostileTargets.IsValid(target.Hostile))
                return target.Hostile;
            var soft = SoftTarget;
            if (soft != null)
                return soft;
            if (encounter == null)
                return null;
            var best = HostileTargets.Best(encounter.Hostiles, origin, facing);
            softTarget = best;
            return best;
        }

        void OnNextTarget(InputAction.CallbackContext context) => CycleTarget(1);

        void OnPreviousTarget(InputAction.CallbackContext context) => CycleTarget(-1);

        internal void CycleTarget(int direction)
        {
            if (encounter == null)
                return;
            var next = HostileTargets.Cycle(encounter.Hostiles, CycleOrigin(), SoftTarget, direction);
            if (next == null)
                return;
            softTarget = next;
            if (!IsActive || viewCamera == null)
                return;
            var screen = viewCamera.WorldToScreenPoint(next.transform.position);
            if (screen.z > 0f)
                SetScreenPosition(new Vector2(screen.x, screen.y));
        }

        // Targets are ordered by distance from the character being played, or from the camera when there is none.
        Vector3 CycleOrigin()
        {
            if (activeCharacter != null && activeCharacter.HasUnit)
                return activeCharacter.Unit.transform.position;
            return viewCamera != null ? viewCamera.transform.position : Vector3.zero;
        }

        PointerTarget Resolve(Vector2 point)
        {
            // Raw: units and ground exactly under the cursor. Snapping is added below, around the ground point.
            var raw = PointerTargetResolver.Resolve(viewCamera, point, maxDistance, clickableLayers, null, 0f);
            if (raw.Kind != PointerTargetKind.Ground)
                return raw;

            var ground = raw.Point;
            var friendly = NearestFriendly(ground);
            if (friendly != null)
                return PointerTarget.OnFriendly(friendly, ground);
            var hostile = NearestHostile(ground);
            if (hostile != null)
                return PointerTarget.OnHostile(hostile, ground);
            if (coverRegistry != null && CoverRules.TryChooseNearest(coverRegistry.Points, ground, coverSnapRadius, acceptAny, out var cover))
                return PointerTarget.OnCover(cover, ground);
            return raw;
        }

        SelectableUnit NearestFriendly(Vector3 point)
        {
            if (selection == null)
                return null;
            SelectableUnit best = null;
            var bestDistance = unitSnapRadius;
            foreach (var unit in selection.Roster)
            {
                if (unit == null || !unit.isActiveAndEnabled)
                    continue;
                var distance = CoverRules.FlatDistance(point, unit.transform.position);
                if (distance > bestDistance)
                    continue;
                best = unit;
                bestDistance = distance;
            }
            return best;
        }

        Health NearestHostile(Vector3 point)
        {
            if (encounter == null)
                return null;
            Health best = null;
            var bestDistance = unitSnapRadius;
            foreach (var hostile in encounter.Hostiles)
            {
                if (!HostileTargets.IsValid(hostile))
                    continue;
                var distance = CoverRules.FlatDistance(point, hostile.transform.position);
                if (distance > bestDistance)
                    continue;
                best = hostile;
                bestDistance = distance;
            }
            return best;
        }

        void EnsurePosition()
        {
            if (hasPosition || viewCamera == null)
                return;
            screenPosition = new Vector2(viewCamera.pixelWidth * 0.5f, viewCamera.pixelHeight * 0.5f);
            hasPosition = true;
        }

        Vector2 ClampToScreen(Vector2 position)
        {
            if (viewCamera == null)
                return position;
            return new Vector2(Mathf.Clamp(position.x, 0f, viewCamera.pixelWidth), Mathf.Clamp(position.y, 0f, viewCamera.pixelHeight));
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.TacticalCursorTests"`
Expected: EXIT=0, `passed="13"`.
Notes for fixing failures without weakening tests: a snap test that lands on a friendly/hostile *capsule* instead of ground means the offset is inside the body (capsule radius 0.5 m) — move the probe point, not the assertion. If a raycast to a ground point is occluded by an obstacle the environment made, report it (do not delete the obstacle).

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project
git commit -m "Add the controller's tactical cursor with snapping and target cycling

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 11: Controller commands through `PlayerCommandInput` (Confirm, Attack, Cancel, Stop, pause)

**Files:**
- Modify: `Assets/_Project/Scripts/Controls/PlayerCommandInput.cs`
- Test: `Assets/_Project/Tests/PlayMode/ControllerCommandInputTests.cs`

**Interfaces:**
- Consumes: `PlayerCommandInput.Act(PointerTarget)` (Task 8), `TacticalCursor.Refresh/IsActive/PickAttackTarget` (Task 10), asset actions `Commands/Confirm`, `Commands/Attack`, plus the existing `Command`, `PointerPosition`, `ToggleTacticalPause`, `QueueModifier`, `Stop`, `Cancel`.
- Produces: `PlayerCommandInput.Initialize(Camera camera, UnitSelection unitSelection, TacticalPause pause, InputActionReference command, InputActionReference pointerPosition, InputActionReference togglePause, InputActionReference modifier, InputActionReference stop, InputActionReference clearSelection, ActiveCharacter active = null, CoverRegistry registry = null, TacticalCursor tacticalCursor = null, InputActionReference confirm = null, InputActionReference attack = null)`; serialized fields `cursor`, `confirmAction`, `attackAction`. Behaviour: Confirm → `Act(cursor.Refresh())` only while the cursor is active; Attack → an `AttackCommand` on `cursor.PickAttackTarget(anchor position, facing)` to `OrderedUnits()`, queued with the modifier.

- [ ] **Step 1: Write the failing tests**

`Assets/_Project/Tests/PlayMode/ControllerCommandInputTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Switch;
using UnityEngine.InputSystem.XInput;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class ControllerCommandInputTests : InputTestFixture
    {
        static readonly Vector3 GroundPoint = new Vector3(2f, 0f, 2f);
        static readonly Vector3 OtherGroundPoint = new Vector3(-2f, 0f, 2f);
        static readonly Vector3 CoverGroundPoint = new Vector3(-6f, 0f, 2f);

        Gamepad pad;
        InputActionAsset actions;
        TestWorld world;
        Camera viewCamera;
        SelectableUnit unitA;
        SelectableUnit unitB;
        Health nearDummy;
        Health farDummy;
        CoverLocation coverPoint;
        TacticalPause pause;
        UnitSelection selection;
        ActiveCharacter active;
        TacticalCursor cursor;

        public override void Setup()
        {
            base.Setup();
            pad = InputSystem.AddDevice<XInputController>();
            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Xbox");
            world = new TestWorld();
            world.CreateEnvironment();
            unitA = world.CreateFriendly(new Vector3(-8f, 0f, -6f));
            unitB = world.CreateFriendly(new Vector3(4f, 0f, -6f));
            nearDummy = world.CreateDummy(new Vector3(-10f, 0f, -6f));
            farDummy = world.CreateDummy(new Vector3(6f, 0f, 6f));
            var wall = world.CreateObstacle(CoverGroundPoint + new Vector3(0f, 0.45f, 1f), new Vector3(2f, 0.9f, 0.5f));
            coverPoint = world.CreateCoverPoint(CoverGroundPoint, Vector3.forward, wall.GetComponent<Collider>());
            var registry = world.CreateRegistry(coverPoint);
            var encounter = world.CreateEncounter();
            encounter.Initialize(new Health[0], new[] { farDummy, nearDummy });

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(unitA, unitB);
            active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(null, pause, selection);
            cursor = systems.AddComponent<TacticalCursor>();
            cursor.Initialize(viewCamera, active, selection, encounter, registry, null,
                TestControls.Ref(actions, "Commands/CursorMove"),
                TestControls.Ref(actions, "Camera/CameraModifier"),
                TestControls.Ref(actions, "Commands/NextTarget"),
                TestControls.Ref(actions, "Commands/PreviousTarget"));
            var input = systems.AddComponent<PlayerCommandInput>();
            input.Initialize(viewCamera, selection, pause,
                TestControls.Ref(actions, "Commands/Command"),
                TestControls.Ref(actions, "Commands/PointerPosition"),
                TestControls.Ref(actions, "Commands/ToggleTacticalPause"),
                TestControls.Ref(actions, "Commands/QueueModifier"),
                TestControls.Ref(actions, "Commands/Stop"),
                TestControls.Ref(actions, "Commands/Cancel"),
                active, registry, cursor,
                TestControls.Ref(actions, "Commands/Confirm"),
                TestControls.Ref(actions, "Commands/Attack"));
            systems.SetActive(true);
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        Vector2 ScreenPointOf(Vector3 worldPoint) => viewCamera.WorldToScreenPoint(worldPoint);

        IEnumerator CursorOn(Vector3 worldPoint)
        {
            cursor.SetScreenPosition(ScreenPointOf(worldPoint));
            yield return null;
        }

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        void DriveUnitA()
        {
            active.Initialize(unitA.Unit, pause, selection);
            active.SetTakeover(true);
        }

        [UnityTest]
        public IEnumerator Confirm_OnAFriendly_SelectsIt()
        {
            yield return CursorOn(unitB.transform.position);
            yield return Tap(pad.buttonSouth);

            Assert.That(selection.Selected, Has.Count.EqualTo(1));
            Assert.That(selection.Selected[0], Is.SameAs(unitB));
        }

        [UnityTest]
        public IEnumerator QueueModifierPlusConfirm_BuildsAMultiSelection_AndTogglesOneOut()
        {
            yield return CursorOn(unitA.transform.position);
            yield return Tap(pad.buttonSouth);
            Press(pad.leftTrigger);
            yield return null;
            yield return CursorOn(unitB.transform.position);
            yield return Tap(pad.buttonSouth);
            Assert.That(selection.Selected, Is.EquivalentTo(new[] { unitA, unitB }));

            yield return CursorOn(unitA.transform.position);
            yield return Tap(pad.buttonSouth);
            Release(pad.leftTrigger);
            yield return null;
            Assert.That(selection.Selected, Is.EquivalentTo(new[] { unitB }));
        }

        [UnityTest]
        public IEnumerator Confirm_OnGround_WhilePaused_OrdersTheSelectedUnits_AndSimulationStaysPaused()
        {
            selection.Select(unitA);
            pause.Pause();
            yield return CursorOn(GroundPoint);
            yield return Tap(pad.buttonSouth);

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            Assert.That(Vector3.Distance(((MoveCommand)unitA.Unit.CurrentCommand).Destination, GroundPoint), Is.LessThan(0.5f));
            Assert.That(pause.IsPaused, Is.True);
            Assert.That(unitB.Unit.CurrentCommand, Is.Null, "Only the selection is ordered");
        }

        [UnityTest]
        public IEnumerator Confirm_WithTheQueueModifier_AppendsInsteadOfReplacing()
        {
            selection.Select(unitA);
            pause.Pause();
            yield return CursorOn(GroundPoint);
            yield return Tap(pad.buttonSouth);
            var first = unitA.Unit.CurrentCommand;

            Press(pad.leftTrigger);
            yield return null;
            yield return CursorOn(OtherGroundPoint);
            yield return Tap(pad.buttonSouth);
            Release(pad.leftTrigger);
            yield return null;

            Assert.That(unitA.Unit.CurrentCommand, Is.SameAs(first));
            Assert.That(unitA.Unit.PendingCommands, Has.Count.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Confirm_OnAHostile_IssuesAnAttack()
        {
            selection.Select(unitA);
            pause.Pause();
            yield return CursorOn(farDummy.transform.position);
            yield return Tap(pad.buttonSouth);

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)unitA.Unit.CurrentCommand).Target, Is.SameAs(farDummy));
        }

        [UnityTest]
        public IEnumerator Confirm_OnCover_IssuesMoveToCover_ThenAQueuedCommandFollows()
        {
            selection.Select(unitA);
            pause.Pause();
            yield return CursorOn(CoverGroundPoint);
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Cover));
            yield return Tap(pad.buttonSouth);
            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<MoveToCoverCommand>());
            Assert.That(((MoveToCoverCommand)unitA.Unit.CurrentCommand).Point, Is.SameAs(coverPoint));

            Press(pad.leftTrigger);
            yield return null;
            yield return CursorOn(GroundPoint);
            yield return Tap(pad.buttonSouth);
            Release(pad.leftTrigger);
            yield return null;
            Assert.That(unitA.Unit.PendingCommands, Has.Count.EqualTo(1));
            Assert.That(unitA.Unit.PendingCommands[0], Is.TypeOf<MoveCommand>());
        }

        [UnityTest]
        public IEnumerator Cancel_ClearsTheSelection_AndIsHarmlessWhenEmpty()
        {
            selection.Select(unitA);
            pause.Pause();
            yield return Tap(pad.buttonEast);
            Assert.That(selection.Selected, Is.Empty);
            yield return Tap(pad.buttonEast);
            Assert.That(selection.Selected, Is.Empty);
        }

        [UnityTest]
        public IEnumerator Stop_StopsTheSelectedUnits()
        {
            selection.Select(unitA);
            Assert.That(unitA.Unit.Issue(new MoveCommand(GroundPoint)), Is.True);
            yield return Tap(pad.dpad.down);

            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator ToggleTacticalPause_PausesAndResumes_FromTheController()
        {
            yield return Tap(pad.selectButton);
            Assert.That(pause.IsPaused, Is.True);
            yield return Tap(pad.selectButton);
            Assert.That(pause.IsPaused, Is.False);
        }

        [UnityTest]
        public IEnumerator Confirm_WhileTheCameraOwnsTheStick_DoesNothing()
        {
            DriveUnitA();
            selection.Select(unitB);
            yield return CursorOn(GroundPoint);
            yield return Tap(pad.buttonSouth);

            Assert.That(unitB.Unit.CurrentCommand, Is.Null);
            Assert.That(selection.Selected, Is.EquivalentTo(new[] { unitB }));
        }

        [UnityTest]
        public IEnumerator HoldingRightTrigger_WhileDriving_LetsConfirmOrderTheSelection()
        {
            DriveUnitA();
            selection.Select(unitB);
            Press(pad.rightTrigger);
            yield return null;
            yield return CursorOn(GroundPoint);
            yield return Tap(pad.buttonSouth);
            Release(pad.rightTrigger);
            yield return null;

            Assert.That(unitB.Unit.CurrentCommand, Is.TypeOf<MoveCommand>());
        }

        [UnityTest]
        public IEnumerator Attack_AtTheCursorHostile_OrdersTheSelection()
        {
            selection.Select(unitA);
            pause.Pause();
            yield return CursorOn(farDummy.transform.position);
            yield return Tap(pad.buttonWest);

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)unitA.Unit.CurrentCommand).Target, Is.SameAs(farDummy));
        }

        [UnityTest]
        public IEnumerator Attack_WhileDriving_PicksTheBestHostileForTheActiveCharacter()
        {
            DriveUnitA();
            yield return null;
            yield return Tap(pad.buttonWest);

            Assert.That(unitA.Unit.CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)unitA.Unit.CurrentCommand).Target, Is.SameAs(nearDummy), "The nearer one, two metres away");
        }

        [UnityTest]
        public IEnumerator Attack_UsesTheSoftTargetChosenWithTheDPad()
        {
            DriveUnitA();
            yield return null;
            yield return Tap(pad.dpad.right);
            yield return Tap(pad.dpad.right);
            Assert.That(cursor.SoftTarget, Is.SameAs(farDummy));
            yield return Tap(pad.buttonWest);

            Assert.That(((AttackCommand)unitA.Unit.CurrentCommand).Target, Is.SameAs(farDummy));
        }

        [UnityTest]
        public IEnumerator Attack_WithTheQueueModifier_Appends()
        {
            selection.Select(unitA);
            Assert.That(unitA.Unit.Issue(new MoveCommand(GroundPoint)), Is.True);
            var move = unitA.Unit.CurrentCommand;
            Press(pad.leftTrigger);
            yield return null;
            yield return Tap(pad.buttonWest);
            Release(pad.leftTrigger);
            yield return null;

            Assert.That(unitA.Unit.CurrentCommand, Is.SameAs(move));
            Assert.That(unitA.Unit.PendingCommands, Has.Count.EqualTo(1));
            Assert.That(unitA.Unit.PendingCommands[0], Is.TypeOf<AttackCommand>());
        }

        [UnityTest]
        public IEnumerator Attack_WithNoLivingHostiles_DoesNothing()
        {
            selection.Select(unitA);
            nearDummy.TakeDamage(1000);
            farDummy.TakeDamage(1000);
            yield return Tap(pad.buttonWest);

            Assert.That(unitA.Unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator Nintendo_ConfirmIsEast_CancelIsSouth()
        {
            InputSystem.RemoveDevice(pad);
            pad = InputSystem.AddDevice<SwitchProControllerHID>();
            TestControls.UseGroup(actions, "Nintendo");
            yield return null;

            yield return CursorOn(unitB.transform.position);
            yield return Tap(pad.buttonSouth);
            Assert.That(selection.Selected, Is.Empty, "South is Cancel on a Nintendo controller: it must not select");

            yield return Tap(pad.buttonEast);
            Assert.That(selection.Selected, Is.EquivalentTo(new[] { unitB }), "East confirms");

            yield return Tap(pad.buttonSouth);
            Assert.That(selection.Selected, Is.Empty, "South cancels");
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.ControllerCommandInputTests"`
Expected: EXIT=1 (`Initialize` has no overload with the cursor/confirm/attack arguments).

- [ ] **Step 3: Modify `PlayerCommandInput`**

Add fields in the `[Header("Input")]` group after `clearSelectionAction`:

```csharp
        [SerializeField] InputActionReference confirmAction;
        [SerializeField] InputActionReference attackAction;
```

and, after `coverRegistry`:

```csharp
        // The controller's pointer. Without it (or while it is inactive) Confirm does nothing; mouse clicks never use it.
        [SerializeField] TacticalCursor cursor;
```

Extend `Initialize`:

```csharp
        internal void Initialize(Camera camera, UnitSelection unitSelection, TacticalPause pause,
            InputActionReference command, InputActionReference pointerPosition, InputActionReference togglePause,
            InputActionReference modifier, InputActionReference stop, InputActionReference clearSelection,
            ActiveCharacter active = null, CoverRegistry registry = null, TacticalCursor tacticalCursor = null,
            InputActionReference confirm = null, InputActionReference attack = null)
        {
            viewCamera = camera;
            selection = unitSelection;
            tacticalPause = pause;
            commandAction = command;
            pointerPositionAction = pointerPosition;
            togglePauseAction = togglePause;
            modifierAction = modifier;
            stopAction = stop;
            clearSelectionAction = clearSelection;
            activeCharacter = active;
            coverRegistry = registry;
            cursor = tacticalCursor;
            confirmAction = confirm;
            attackAction = attack;
        }
```

In `OnEnable` add `confirmAction, attackAction` to the `SetEnabled(true, ...)` list and, after the `clearSelectionAction` subscription:

```csharp
            if (confirmAction != null)
                confirmAction.action.performed += OnConfirm;
            if (attackAction != null)
                attackAction.action.performed += OnAttack;
```

In `OnDisable` add the two matching `-=` lines and the two actions to the `SetEnabled(false, ...)` list.

Add these methods next to `OnStop`:

```csharp
        // Controller counterpart of a left click: acts on what the tactical cursor is on, through the same Act path.
        void OnConfirm(InputAction.CallbackContext context)
        {
            if (cursor == null || !cursor.IsActive)
                return;
            Act(cursor.Refresh());
        }

        // Attack the cursor's hostile, else the chosen soft target, else the best hostile ahead of whoever is ordered.
        void OnAttack(InputAction.CallbackContext context)
        {
            if (cursor == null)
                return;
            var units = OrderedUnits();
            var anchor = AttackAnchor(units);
            if (anchor == null)
                return;
            var hostile = cursor.PickAttackTarget(anchor.transform.position, AttackFacing(anchor));
            if (hostile == null)
                return;
            GroupOrders.Issue(units, new AttackCommand(hostile), ModifierHeld ? IssueMode.Append : IssueMode.Replace, groupSpacing);
        }

        // Whose position "nearest" is measured from: the character being played, else the first unit being ordered.
        CommandableUnit AttackAnchor(List<CommandableUnit> units)
        {
            if (activeCharacter != null && activeCharacter.HasUnit)
                return activeCharacter.Unit;
            return units.Count > 0 ? units[0] : null;
        }

        // "Ahead": the direction the unit is being steered, else where the camera looks.
        Vector3 AttackFacing(CommandableUnit anchor)
        {
            var facing = anchor.MoveIntent;
            if (facing == Vector3.zero && viewCamera != null)
                facing = viewCamera.transform.forward;
            facing.y = 0f;
            return facing;
        }
```

Update the class summary: add "A controller does the same through the tactical cursor: Confirm acts on what it is on (the same Act path), Attack orders an attack on the cursor's, the chosen or the best hostile, the queue modifier (Shift / LT) queues, Cancel (Esc / east) clears the selection."

- [ ] **Step 4: Run the new fixture and the existing click suites**

```bash
Tools/run-tests.sh PlayMode "Blackglass.Tests.ControllerCommandInputTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.PlayerCommandInputTests"
```
Expected: EXIT=0; new fixture `passed="17"`; the existing click suite unchanged.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Project
git commit -m "Order, attack, queue, cancel and pause from a controller through the tactical cursor

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Cursor view and the debug HUD (input family and prompts)

**Files:**
- Create: `Assets/_Project/Scripts/DebugUI/TacticalCursorView.cs`
- Modify: `Assets/_Project/Scripts/DebugUI/PrototypeHud.cs`
- Test: `Assets/_Project/Tests/EditMode/ControllerHudTests.cs`

**Interfaces:**
- Consumes: `TacticalCursor` (`IsActive`, `ScreenPosition`, `Target`, `SoftTarget`), `PointerTarget`, `ActiveInputDevice.Family`, `PromptResolver.GetPrompt`, `InputFamilyExtensions.DisplayName/IsController` (Tasks 1, 4, 5, 10).
- Produces:
  - `TacticalCursorView` — `[SerializeField] TacticalCursor cursor; [SerializeField] Camera viewCamera;` `internal static string Describe(PointerTarget target)`: `""` for None, `"Ground"`, `"Friendly <name>"`, `"Hostile <name>"`, `"Cover <location name>"`.
  - `PrototypeHud`: new `[SerializeField] ActiveInputDevice inputDevice; [SerializeField] InputActionAsset controls;`, `internal static string DescribeInput(InputFamily)` → `"Input: <DisplayName>"`, `internal static string DescribePromptLine(params (string label, string prompt)[] entries)` → `"Confirm: A | Cancel: B"` (empty string for no entries), `internal static string DescribePauseBanner(string resumePrompt)` → `"TACTICAL PAUSE - <prompt> to resume"`.

- [ ] **Step 1: Write the failing tests**

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ControllerHudTests
    {
        [TestCase(InputFamily.KeyboardMouse, "Input: Keyboard/Mouse")]
        [TestCase(InputFamily.Xbox, "Input: Xbox")]
        [TestCase(InputFamily.PlayStation, "Input: PlayStation")]
        [TestCase(InputFamily.Nintendo, "Input: Nintendo")]
        [TestCase(InputFamily.GenericGamepad, "Input: Generic Gamepad")]
        public void DescribeInput_NamesTheActiveFamily(InputFamily family, string expected) =>
            Assert.That(PrototypeHud.DescribeInput(family), Is.EqualTo(expected));

        [Test]
        public void DescribePromptLine_JoinsLabelsAndPrompts()
        {
            Assert.That(PrototypeHud.DescribePromptLine(("Confirm", "A"), ("Cancel", "B"), ("Queue", "LT")),
                Is.EqualTo("Confirm: A | Cancel: B | Queue: LT"));
            Assert.That(PrototypeHud.DescribePromptLine(), Is.EqualTo(string.Empty));
        }

        [Test]
        public void DescribePauseBanner_KeepsTheKeyboardWording() =>
            Assert.That(PrototypeHud.DescribePauseBanner("Space"), Is.EqualTo("TACTICAL PAUSE - Space to resume"));

        [Test]
        public void CursorView_DescribesWhatTheCursorIsOn()
        {
            var host = new GameObject("Raider");
            try
            {
                var health = host.AddComponent<Health>();
                Assert.That(TacticalCursorView.Describe(PointerTarget.None), Is.EqualTo(string.Empty));
                Assert.That(TacticalCursorView.Describe(PointerTarget.OnGround(Vector3.zero)), Is.EqualTo("Ground"));
                Assert.That(TacticalCursorView.Describe(PointerTarget.OnHostile(health, Vector3.zero)), Is.EqualTo("Hostile Raider"));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `Tools/run-tests.sh EditMode "Blackglass.Tests.ControllerHudTests"`
Expected: EXIT=1 (`DescribeInput`, `TacticalCursorView` missing).

- [ ] **Step 3: Implement `TacticalCursorView.cs`**

```csharp
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Debug view of the controller's tactical cursor (IMGUI, placeholder look): a crosshair with a label naming what it is
    /// on, a bracket on the cover location or ground point it has chosen, and a bracket on the soft target. Shown only
    /// while the cursor is active. Works while paused. Not production UI.
    /// </summary>
    public sealed class TacticalCursorView : MonoBehaviour
    {
        [SerializeField] TacticalCursor cursor;
        [SerializeField] Camera viewCamera;
        [SerializeField, Min(8f)] float crosshairSize = 28f;

        /// <summary>A short name for the target, e.g. "Hostile HostileUnit_1". Empty for nothing.</summary>
        internal static string Describe(PointerTarget target)
        {
            switch (target.Kind)
            {
                case PointerTargetKind.Friendly:
                    return $"Friendly {target.Friendly.name}";
                case PointerTargetKind.Hostile:
                    return $"Hostile {target.Hostile.name}";
                case PointerTargetKind.Cover:
                    return $"Cover {target.Cover.Name}";
                case PointerTargetKind.Ground:
                    return "Ground";
                default:
                    return string.Empty;
            }
        }

        void OnGUI()
        {
            if (cursor == null || viewCamera == null)
                return;

            var soft = cursor.SoftTarget;
            if (soft != null)
                DrawBracket(viewCamera.WorldToScreenPoint(soft.transform.position), 64f);
            if (!cursor.IsActive)
                return;

            var target = cursor.Target;
            if (target.Kind == PointerTargetKind.Cover)
                DrawBracket(viewCamera.WorldToScreenPoint(target.Cover.Position), 40f);

            var position = cursor.ScreenPosition;
            var x = position.x;
            var y = Screen.height - position.y;
            GUI.Box(new Rect(x - crosshairSize * 0.5f, y - 1f, crosshairSize, 2f), GUIContent.none);
            GUI.Box(new Rect(x - 1f, y - crosshairSize * 0.5f, 2f, crosshairSize), GUIContent.none);
            var text = Describe(target);
            if (text.Length > 0)
                GUI.Label(new Rect(x + 16f, y + 12f, 280f, 22f), text);
        }

        static void DrawBracket(Vector3 screen, float size)
        {
            if (screen.z <= 0f)
                return;
            GUI.Box(new Rect(screen.x - size * 0.5f, Screen.height - screen.y - size * 0.5f, size, size), GUIContent.none);
        }
    }
}
```

- [ ] **Step 4: Extend `PrototypeHud`**

Add `using UnityEngine.InputSystem;` to the usings. Add fields after `activeCharacter`:

```csharp
        [SerializeField] ActiveInputDevice inputDevice;
        // Where the HUD looks up the bindings it shows for the active controller family.
        [SerializeField] InputActionAsset controls;
```

Add the three static helpers next to `DescribeFollow`:

```csharp
        /// <summary>The active input family line, e.g. "Input: Xbox".</summary>
        internal static string DescribeInput(InputFamily family) => $"Input: {family.DisplayName()}";

        /// <summary>One line of "label: prompt" pairs, e.g. "Confirm: A | Cancel: B". Empty without entries.</summary>
        internal static string DescribePromptLine(params (string label, string prompt)[] entries)
        {
            var parts = new string[entries.Length];
            for (var i = 0; i < entries.Length; i++)
                parts[i] = $"{entries[i].label}: {entries[i].prompt}";
            return string.Join(" | ", parts);
        }

        internal static string DescribePauseBanner(string resumePrompt) => $"TACTICAL PAUSE - {resumePrompt} to resume";
```

Add this helper method inside the class (next to `DrawUnitLabels`):

```csharp
        string Prompt(string actionPath, InputFamily family) =>
            controls != null ? PromptResolver.GetPrompt(controls.FindAction(actionPath), family) : "-";

        // The active family and, for a controller, the bindings of the main actions. Debug text, rebuilt each frame.
        void DrawInputInfo()
        {
            if (inputDevice == null)
                return;
            var family = inputDevice.Family;
            var left = Screen.width - 560f;
            GUI.Label(new Rect(left, 10f, 550f, 22f), DescribeInput(family));
            if (!family.IsController() || controls == null)
                return;
            GUI.Label(new Rect(left, 30f, 550f, 22f), DescribePromptLine(
                ("Confirm", Prompt("Commands/Confirm", family)), ("Cancel", Prompt("Commands/Cancel", family)),
                ("Attack", Prompt("Commands/Attack", family)), ("Pause", Prompt("Commands/ToggleTacticalPause", family)),
                ("Control", Prompt("Character/ToggleCharacterControl", family)), ("Follow", Prompt("Character/ToggleFollow", family))));
            GUI.Label(new Rect(left, 50f, 550f, 22f), DescribePromptLine(
                ("Prev/Next", Prompt("Character/PreviousCharacter", family) + " / " + Prompt("Character/NextCharacter", family)),
                ("Queue", Prompt("Commands/QueueModifier", family)), ("Stick swap", Prompt("Camera/CameraModifier", family)),
                ("Target", Prompt("Commands/PreviousTarget", family) + " / " + Prompt("Commands/NextTarget", family)),
                ("Stop", Prompt("Commands/Stop", family))));
        }
```

In `OnGUI`, call `DrawInputInfo();` directly after the `GUI.Label(... ControlHints)` line, and replace the paused-banner label call with:

```csharp
                var resumePrompt = inputDevice != null && controls != null
                    ? Prompt("Commands/ToggleTacticalPause", inputDevice.Family) : "Space";
                GUI.Label(new Rect(0f, 165f, Screen.width, 40f), DescribePauseBanner(resumePrompt), pausedStyle);
```

(For keyboard/mouse this prints exactly `TACTICAL PAUSE - Space to resume` as before: the resolver returns `Space` for the binding.)

- [ ] **Step 5: Run to verify it passes, plus the existing HUD tests**

```bash
Tools/run-tests.sh EditMode "Blackglass.Tests.ControllerHudTests"
Tools/run-tests.sh EditMode "Blackglass.Tests.PrototypeHudTests"
```
Expected: EXIT=0; new `passed="8"` (5 family cases + 3 methods); existing HUD tests unchanged.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project
git commit -m "Show the active input family, controller prompts and the tactical cursor in the debug HUD

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 13: Wire the scene and test the whole thing in the real arena

**Files:**
- Create (temporary, never committed): `Assets/_Project/Editor/ControllerSceneBuilder.cs` (+ its `.meta`, deleted afterwards)
- Modify: `Assets/_Project/Scenes/Prototype.unity`
- Test: `Assets/_Project/Tests/PlayMode/PrototypeSceneControllerTests.cs`

**Interfaces:**
- Consumes: everything above. Serialized field names the builder sets: `ActiveInputDevice.controls`; `TacticalCursor.{viewCamera, activeCharacter, selection, encounter, coverRegistry, inputDevice, cursorMoveAction, cameraModifierAction, nextTargetAction, previousTargetAction}`; `TacticalCursorView.{cursor, viewCamera}`; `PlayerCommandInput.{cursor, confirmAction, attackAction}`; `DirectControlInput.previousAction`; `TacticalCameraController.{lookAction, cameraModifierAction}`; `PrototypeHud.{inputDevice, controls}`.
- Produces: `Prototype.unity` with `ActiveInputDevice`, `TacticalCursor` and `TacticalCursorView` added to the `Systems` object and wired; the scene's own camera and systems untouched otherwise.

- [ ] **Step 1: Write the scene tests first (they fail until the scene is wired)**

`Assets/_Project/Tests/PlayMode/PrototypeSceneControllerTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.Switch;
using UnityEngine.InputSystem.XInput;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PrototypeSceneControllerTests : InputTestFixture
    {
        Keyboard keyboard;
        Mouse mouse;
        InputActionAsset actions;
        CommandableUnit[] squad;
        ActiveInputDevice family;
        TacticalCursor cursor;
        TacticalPause pause;
        UnitSelection selection;
        ActiveCharacter active;
        Camera viewCamera;
        CoverRegistry registry;
        Vector3 orderPoint;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
        }

        public override void TearDown()
        {
            Time.timeScale = 1f;
            PrototypeSceneTests.DestroySceneObjects();
            TestControls.Reset(actions);
            base.TearDown();
        }

        // Loaded from each test, after InputTestFixture has isolated the input system (see PrototypeSceneTests).
        IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
            squad = PrototypeSceneTests.FindSquad();
            family = Object.FindFirstObjectByType<ActiveInputDevice>();
            cursor = Object.FindFirstObjectByType<TacticalCursor>();
            pause = Object.FindFirstObjectByType<TacticalPause>();
            selection = Object.FindFirstObjectByType<UnitSelection>();
            active = Object.FindFirstObjectByType<ActiveCharacter>();
            viewCamera = Object.FindFirstObjectByType<Camera>();
            registry = Object.FindFirstObjectByType<CoverRegistry>();
            Assert.That(family, Is.Not.Null, "The scene has no ActiveInputDevice: run the scene builder");
            Assert.That(cursor, Is.Not.Null, "The scene has no TacticalCursor: run the scene builder");
        }

        IEnumerator Wake(Gamepad pad)
        {
            Press(pad.startButton);
            yield return null;
            Release(pad.startButton);
            yield return null;
        }

        IEnumerator WakeKeyboard()
        {
            Press(keyboard.f12Key);
            yield return null;
            Release(keyboard.f12Key);
            yield return null;
        }

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        Vector2 ScreenPointOf(Vector3 worldPoint) => viewCamera.WorldToScreenPoint(worldPoint);

        IEnumerator CursorOn(Vector3 worldPoint)
        {
            cursor.SetScreenPosition(ScreenPointOf(worldPoint));
            yield return null;
        }

        // Finds open, reachable ground near a unit by trying offsets until the cursor reads Ground there; sets orderPoint.
        IEnumerator FindOrderPoint(CommandableUnit near)
        {
            var origin = near.transform.position;
            origin.y = 0f;
            var mover = near.GetComponent<UnitMover>();
            foreach (var offset in new[]
                     {
                         new Vector3(0f, 0f, 3f), new Vector3(3f, 0f, 0f), new Vector3(-3f, 0f, 0f), new Vector3(0f, 0f, -3f),
                         new Vector3(4f, 0f, 4f), new Vector3(-4f, 0f, 4f), new Vector3(4f, 0f, -4f), new Vector3(-4f, 0f, -4f),
                     })
            {
                yield return CursorOn(origin + offset);
                if (cursor.Target.Kind == PointerTargetKind.Ground && mover.CanMoveTo(cursor.Target.Point))
                {
                    orderPoint = cursor.Target.Point;
                    yield break;
                }
            }
            Assert.Fail("No open ground near " + near.name);
        }

        static ButtonControl Confirm(Gamepad pad, bool nintendo) => nintendo ? pad.buttonEast : pad.buttonSouth;

        static ButtonControl Cancel(Gamepad pad, bool nintendo) => nintendo ? pad.buttonSouth : pad.buttonEast;

        [UnityTest]
        public IEnumerator Scene_StartsKeyboardMouse_AControllerWakesIt_AndKeyboardTakesItBack()
        {
            yield return LoadScene();
            Assert.That(family.Family, Is.EqualTo(InputFamily.KeyboardMouse));
            Assert.That(actions.bindingMask, Is.EqualTo((InputBinding?)InputBinding.MaskByGroup("KeyboardMouse")));

            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);
            Assert.That(family.Family, Is.EqualTo(InputFamily.Xbox));

            yield return WakeKeyboard();
            Assert.That(family.Family, Is.EqualTo(InputFamily.KeyboardMouse));
        }

        [UnityTest]
        public IEnumerator Scene_PadDriving_AnalogMovement_CharacterSwitching_AndControlToggle()
        {
            yield return LoadScene();
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);

            Assert.That(active.IsTakeoverOn, Is.False);
            yield return Tap(pad.buttonNorth);
            Assert.That(active.IsTakeoverOn, Is.True, "North toggles character control");

            var unit = active.Unit;
            var start = unit.transform.position;
            Set(pad.leftStick, new Vector2(0f, 0.6f));
            yield return new WaitForSecondsRealtime(0.5f);
            Set(pad.leftStick, Vector2.zero);
            yield return null;
            var moved = TestWorld.HorizontalDistance(unit.transform.position, start);
            Assert.That(moved, Is.InRange(0.3f, 2.4f), "Half deflection moves, but slower than full speed (5 m/s)");

            yield return Tap(pad.rightShoulder);
            Assert.That(active.Unit, Is.Not.SameAs(unit), "RB switches character");
            yield return Tap(pad.leftShoulder);
            Assert.That(active.Unit, Is.SameAs(unit), "LB switches back");
        }

        [UnityTest]
        public IEnumerator Scene_PadTactical_PauseSelectMoveQueueAndCancel_WithSimulationStillPaused()
        {
            yield return LoadScene();
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);

            yield return Tap(pad.selectButton);
            Assert.That(pause.IsPaused, Is.True, "View pauses");

            var unit = squad[0];
            yield return CursorOn(unit.transform.position);
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            yield return Tap(pad.buttonSouth);
            Assert.That(selection.Selected.Select(s => s.Unit), Is.EquivalentTo(new[] { unit }));

            yield return FindOrderPoint(unit);
            var first = orderPoint;
            yield return Tap(pad.buttonSouth);
            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>());

            Press(pad.leftTrigger);
            yield return null;
            yield return CursorOn(first + new Vector3(1.5f, 0f, 0f));
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Ground));
            yield return Tap(pad.buttonSouth);
            Release(pad.leftTrigger);
            yield return null;
            Assert.That(unit.PendingCommands, Has.Count.EqualTo(1), "LT queues the second order");

            var held = unit.transform.position;
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(unit.transform.position, Is.EqualTo(held), "Simulation must stay paused while the controller plans");
            Assert.That(Time.timeScale, Is.EqualTo(0f));

            yield return Tap(pad.buttonEast);
            Assert.That(selection.Selected, Is.Empty, "East cancels (clears the selection)");
            Assert.That(pause.IsPaused, Is.True);
        }

        [UnityTest]
        public IEnumerator Scene_PadCover_CursorSnapsToGeneratedCover_AndMoveToCoverQueues()
        {
            yield return LoadScene();
            yield return TestWorld.WaitUntil(() => registry.Points.Count > 0, 2f);
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);
            yield return Tap(pad.selectButton);

            var unit = squad[0];
            yield return CursorOn(unit.transform.position);
            yield return Tap(pad.buttonSouth);

            var hostiles = PrototypeSceneTests.FindHostiles();
            CoverLocation chosen = null;
            foreach (var point in registry.Points.Where(p => p.IsValid && p.Height == CoverHeight.Low && p.Obstacle != null
                                                              && p.Obstacle.name.StartsWith("LowWall")))
            {
                var crowded = squad.Any(u => CoverRules.FlatDistance(u.transform.position, point.Position) < 2.5f)
                    || hostiles.Any(h => CoverRules.FlatDistance(h.transform.position, point.Position) < 2.5f);
                if (crowded)
                    continue;
                yield return CursorOn(point.Position);
                if (cursor.Target.Kind == PointerTargetKind.Cover && cursor.Target.Cover == point)
                {
                    chosen = point;
                    break;
                }
            }
            Assert.That(chosen, Is.Not.Null, "The cursor could not snap to any open low-wall cover location");

            yield return Tap(pad.buttonSouth);
            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveToCoverCommand>());

            yield return FindOrderPoint(unit);
            Press(pad.leftTrigger);
            yield return null;
            yield return CursorOn(orderPoint);
            yield return Tap(pad.buttonSouth);
            Release(pad.leftTrigger);
            yield return null;
            Assert.That(unit.PendingCommands, Has.Count.EqualTo(1), "A command can be queued behind MoveToCover");
            Assert.That(pause.IsPaused, Is.True);
        }

        [UnityTest]
        public IEnumerator Scene_RepeatedDeviceSwitching_LeavesNoStuckControlsAndLogsNoErrors()
        {
            yield return LoadScene();
            var xbox = InputSystem.AddDevice<XInputController>();
            var ps = InputSystem.AddDevice<DualSenseGamepadHID>();

            for (var i = 0; i < 10; i++)
            {
                var pad = i % 2 == 0 ? (Gamepad)xbox : ps;
                yield return Wake(pad);
                Set(pad.leftStick, new Vector2(0f, 0.9f));
                yield return null;
                yield return WakeKeyboard();
                Set(pad.leftStick, Vector2.zero);
                yield return null;
            }

            Assert.That(family.Family, Is.EqualTo(InputFamily.KeyboardMouse));
            Assert.That(actions.FindAction("Character/Move", true).ReadValue<Vector2>(), Is.EqualTo(Vector2.zero));
            Assert.That(active.Unit.MoveIntent, Is.EqualTo(Vector3.zero));
            Assert.That(Time.timeScale, Is.EqualTo(1f));
        }

        [UnityTest]
        public IEnumerator Scene_KeyboardMouse_StillWorksAfterControllerUse()
        {
            yield return LoadScene();
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Wake(pad);
            yield return WakeKeyboard();

            Set(mouse.position, ScreenPointOf(squad[1].transform.position));
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
            Assert.That(selection.Selected.Select(s => s.Unit), Is.EquivalentTo(new[] { squad[1] }), "Left-click selects");

            var before = active.Unit;
            yield return Tap(keyboard.tabKey);
            Assert.That(active.Unit, Is.Not.SameAs(before), "Tab switches character");

            yield return Tap(keyboard.vKey);
            Assert.That(active.IsTakeoverOn, Is.True, "V toggles character control");
            var follow = active.IsFollowOn;
            yield return Tap(keyboard.fKey);
            Assert.That(active.IsFollowOn, Is.EqualTo(!follow), "F toggles follow");

            yield return Tap(keyboard.spaceKey);
            Assert.That(pause.IsPaused, Is.True, "Space pauses");
            yield return Tap(keyboard.spaceKey);
            Assert.That(pause.IsPaused, Is.False);
        }

        IEnumerator FamilySmoke<T>(InputFamily expected, bool nintendo) where T : Gamepad
        {
            yield return LoadScene();
            var pad = InputSystem.AddDevice<T>();
            yield return Wake(pad);
            Assert.That(family.Family, Is.EqualTo(expected));

            yield return Tap(pad.selectButton);
            Assert.That(pause.IsPaused, Is.True, "The pause button works on every family");

            var unit = squad[1];
            yield return CursorOn(unit.transform.position);
            yield return Tap(Confirm(pad, nintendo));
            Assert.That(selection.Selected.Select(s => s.Unit), Is.EquivalentTo(new[] { unit }));

            yield return FindOrderPoint(unit);
            yield return Tap(Confirm(pad, nintendo));
            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>());

            yield return Tap(Cancel(pad, nintendo));
            Assert.That(selection.Selected, Is.Empty);
        }

        [UnityTest]
        public IEnumerator Scene_XboxFamily_CanPauseSelectOrderAndCancel()
        {
            yield return FamilySmoke<XInputController>(InputFamily.Xbox, false);
        }

        [UnityTest]
        public IEnumerator Scene_PlayStationFamily_CanPauseSelectOrderAndCancel()
        {
            yield return FamilySmoke<DualSenseGamepadHID>(InputFamily.PlayStation, false);
        }

        [UnityTest]
        public IEnumerator Scene_NintendoFamily_CanPauseSelectOrderAndCancel_WithEastAsConfirm()
        {
            yield return FamilySmoke<SwitchProControllerHID>(InputFamily.Nintendo, true);
        }

        [UnityTest]
        public IEnumerator Scene_GenericGamepadFamily_CanPauseSelectOrderAndCancel()
        {
            yield return FamilySmoke<Gamepad>(InputFamily.GenericGamepad, false);
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify they fail for the right reason**

Run: `Tools/run-tests.sh PlayMode "Blackglass.Tests.PrototypeSceneControllerTests"`
Expected: EXIT=2; every test fails with `The scene has no ActiveInputDevice: run the scene builder`.

- [ ] **Step 3: Create the temporary scene builder**

`Assets/_Project/Editor/ControllerSceneBuilder.cs` (create the `Editor` folder if needed; **delete the file and its `.meta` in Step 5**):

```csharp
using System;
using System.Linq;
using Blackglass;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

public static class ControllerSceneBuilder
{
    const string ScenePath = "Assets/_Project/Scenes/Prototype.unity";
    const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";

    static InputActionReference Action(string actionPath)
    {
        var reference = AssetDatabase.LoadAllAssetsAtPath(ControlsPath).OfType<InputActionReference>()
            .FirstOrDefault(r => r.action != null && r.action.actionMap.name + "/" + r.action.name == actionPath);
        if (reference == null)
            throw new Exception($"No InputActionReference for {actionPath}");
        return reference;
    }

    static T Find<T>() where T : Component
    {
        var found = UnityEngine.Object.FindFirstObjectByType<T>();
        if (found == null)
            throw new Exception($"{typeof(T).Name} not found in the scene");
        return found;
    }

    static T GetOrAdd<T>(GameObject host) where T : Component
    {
        var existing = host.GetComponent<T>();
        return existing != null ? existing : host.AddComponent<T>();
    }

    static void Set(Component component, string field, UnityEngine.Object value)
    {
        var so = new SerializedObject(component);
        var property = so.FindProperty(field);
        if (property == null)
            throw new Exception($"{component.GetType().Name} has no serialized field '{field}'");
        property.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    public static void Build()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var systems = GameObject.Find("Systems");
        if (systems == null)
            throw new Exception("Systems not found");
        var controls = AssetDatabase.LoadAssetAtPath<InputActionAsset>(ControlsPath);
        if (controls == null)
            throw new Exception("Input actions asset not found");
        var cameraRig = Find<TacticalCameraController>();
        var viewCamera = cameraRig.GetComponentInChildren<Camera>();
        if (viewCamera == null)
            throw new Exception("No camera under the camera rig");

        var inputDevice = GetOrAdd<ActiveInputDevice>(systems);
        Set(inputDevice, "controls", controls);

        var cursor = GetOrAdd<TacticalCursor>(systems);
        Set(cursor, "viewCamera", viewCamera);
        Set(cursor, "activeCharacter", Find<ActiveCharacter>());
        Set(cursor, "selection", Find<UnitSelection>());
        Set(cursor, "encounter", Find<Encounter>());
        Set(cursor, "coverRegistry", Find<CoverRegistry>());
        Set(cursor, "inputDevice", inputDevice);
        Set(cursor, "cursorMoveAction", Action("Commands/CursorMove"));
        Set(cursor, "cameraModifierAction", Action("Camera/CameraModifier"));
        Set(cursor, "nextTargetAction", Action("Commands/NextTarget"));
        Set(cursor, "previousTargetAction", Action("Commands/PreviousTarget"));

        var cursorView = GetOrAdd<TacticalCursorView>(systems);
        Set(cursorView, "cursor", cursor);
        Set(cursorView, "viewCamera", viewCamera);

        var commandInput = Find<PlayerCommandInput>();
        Set(commandInput, "cursor", cursor);
        Set(commandInput, "confirmAction", Action("Commands/Confirm"));
        Set(commandInput, "attackAction", Action("Commands/Attack"));

        Set(Find<DirectControlInput>(), "previousAction", Action("Character/PreviousCharacter"));

        Set(cameraRig, "lookAction", Action("Camera/Look"));
        Set(cameraRig, "cameraModifierAction", Action("Camera/CameraModifier"));

        var hud = Find<PrototypeHud>();
        Set(hud, "inputDevice", inputDevice);
        Set(hud, "controls", controls);

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
            throw new Exception("Saving the scene failed");
        Debug.Log($"[ControllerSceneBuilder] Wired ActiveInputDevice, TacticalCursor and TacticalCursorView on {systems.name} in {ScenePath}");
    }
}
```

- [ ] **Step 4: Run the builder (Editor closed) and check the result**

```bash
"/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe" -batchmode -quit -projectPath "$(pwd -W)" -executeMethod ControllerSceneBuilder.Build -logFile "$(pwd -W)/Logs/BuildControllerScene.log"; echo "EXIT=$?"
grep -E '\[ControllerSceneBuilder\]|error CS|Exception' Logs/BuildControllerScene.log | head -20
git diff --stat Assets/_Project/Scenes/Prototype.unity
```
Expected: `EXIT=0`, the `[ControllerSceneBuilder] Wired ...` line, no exception; the scene diff shows three added components and the changed reference lines only (no unrelated churn: no NavMesh asset change, no transform changes). If the scene diff is large, report it before continuing.

- [ ] **Step 5: Delete the builder, run the scene tests, then everything that loads the scene**

```bash
rm Assets/_Project/Editor/ControllerSceneBuilder.cs Assets/_Project/Editor/ControllerSceneBuilder.cs.meta
rmdir Assets/_Project/Editor 2>/dev/null; rm -f Assets/_Project/Editor.meta
Tools/run-tests.sh PlayMode "Blackglass.Tests.PrototypeSceneControllerTests"
Tools/run-tests.sh PlayMode "Blackglass.Tests.PrototypeSceneTests"
```
Expected: EXIT=0, controller scene fixture `passed="10"`, existing scene fixture unchanged. Use `git status --short` to confirm only `Prototype.unity` and the new test file (plus `.meta`) are pending.
If a scene test fails for an environmental reason (open ground not found, a unit already near cover) fix the *probe* (offsets, candidate filters), never the production code; if it fails for a production reason, stop and report.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scenes Assets/_Project/Tests
git commit -m "Wire controller input into the prototype scene and test it end to end

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 14: Rebinding check, boundary check, documentation, full verification

**Files:**
- Test: `Assets/_Project/Tests/PlayMode/RebindingTests.cs`, `Assets/_Project/Tests/EditMode/NoDeviceTypesInGameplayTests.cs`
- Modify: `Docs/Decisions.md` (new decision 024; amendment notes on 008 and 012)

- [ ] **Step 1: Write the rebinding tests (they run on a private copy of the asset, so nothing leaks)**

`Assets/_Project/Tests/PlayMode/RebindingTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XInput;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>Proves the action structure supports runtime rebinding; there is no rebinding UI yet.</summary>
    public class RebindingTests : InputTestFixture
    {
        InputActionAsset copy;
        Gamepad pad;

        public override void Setup()
        {
            base.Setup();
            pad = InputSystem.AddDevice<XInputController>();
            copy = InputActionAsset.FromJson(TestControls.Load().ToJson());
            copy.bindingMask = InputBinding.MaskByGroup("Xbox");
        }

        public override void TearDown()
        {
            copy.Disable();
            Object.DestroyImmediate(copy);
            base.TearDown();
        }

        static int XboxBindingIndex(InputAction action) =>
            Enumerable.Range(0, action.bindings.Count).First(i => action.bindings[i].groups == "Xbox");

        [UnityTest]
        public IEnumerator OverridingAGamepadBinding_ChangesWhichButtonFiresTheAction_AndThePrompt()
        {
            var attack = copy.FindAction("Commands/Attack", throwIfNotFound: true);
            attack.Enable();
            var fired = 0;
            attack.performed += _ => fired++;

            Press(pad.buttonWest);
            yield return null;
            Release(pad.buttonWest);
            yield return null;
            Assert.That(fired, Is.EqualTo(1), "West attacks by default");

            attack.ApplyBindingOverride(XboxBindingIndex(attack), "<Gamepad>/buttonNorth");
            yield return null;
            Press(pad.buttonWest);
            yield return null;
            Release(pad.buttonWest);
            yield return null;
            Assert.That(fired, Is.EqualTo(1), "West no longer attacks after the rebind");

            Press(pad.buttonNorth);
            yield return null;
            Release(pad.buttonNorth);
            yield return null;
            Assert.That(fired, Is.EqualTo(2), "North attacks after the rebind");
            Assert.That(PromptResolver.GetPrompt(attack, InputFamily.Xbox), Is.EqualTo("Y"));
        }

        [UnityTest]
        public IEnumerator InteractiveRebinding_AssignsThePressedButton()
        {
            var attack = copy.FindAction("Commands/Attack", throwIfNotFound: true);
            var index = XboxBindingIndex(attack);
            var completed = false;
            var operation = attack.PerformInteractiveRebinding(index)
                .WithControlsHavingToMatchPath("<Gamepad>")
                .WithCancelingThrough("<Keyboard>/escape")
                .OnComplete(_ => completed = true)
                .Start();
            yield return null;

            Press(pad.buttonSouth);
            yield return TestWorld.WaitUntil(() => completed, 1f);
            Release(pad.buttonSouth);
            yield return null;
            operation.Dispose();

            Assert.That(completed, Is.True, "The interactive rebind never completed");
            Assert.That(attack.bindings[index].overridePath, Is.Not.Null.And.Not.Empty);
            Assert.That(attack.bindings[index].effectivePath, Does.EndWith("buttonSouth"));
        }

        [Test]
        public void Overrides_RoundTripThroughJson()
        {
            var attack = copy.FindAction("Commands/Attack", throwIfNotFound: true);
            attack.ApplyBindingOverride(XboxBindingIndex(attack), "<Gamepad>/buttonNorth");
            var json = copy.SaveBindingOverridesAsJson();

            var fresh = InputActionAsset.FromJson(TestControls.Load().ToJson());
            try
            {
                fresh.LoadBindingOverridesFromJson(json);
                Assert.That(PromptResolver.GetPrompt(fresh.FindAction("Commands/Attack"), InputFamily.Xbox), Is.EqualTo("Y"));
                Assert.That(PromptResolver.GetPrompt(fresh.FindAction("Commands/Attack"), InputFamily.PlayStation), Is.EqualTo("Square"),
                    "Rebinding one family leaves the others alone");
            }
            finally
            {
                Object.DestroyImmediate(fresh);
            }
        }
    }
}
#endif
```

- [ ] **Step 2: Write the boundary test**

`Assets/_Project/Tests/EditMode/NoDeviceTypesInGameplayTests.cs`:

```csharp
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// Gameplay and input components consume semantic actions only: physical devices, face-button names and device types
    /// are allowed in Scripts/Input (and nowhere else), so controller type cannot leak into movement, combat, cover,
    /// command queues or AI.
    /// </summary>
    public class NoDeviceTypesInGameplayTests
    {
        static readonly string[] Forbidden =
        {
            "Gamepad.current", "Keyboard.current", "Mouse.current", "Pointer.current",
            "buttonSouth", "buttonEast", "buttonWest", "buttonNorth",
            "XInputController", "DualShock", "DualSense", "SwitchPro",
        };

        [Test]
        public void OnlyScriptsInput_NamesDevicesOrPhysicalButtons()
        {
            var root = Path.Combine(Application.dataPath, "_Project", "Scripts");
            var checkedFiles = 0;
            foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var normalised = file.Replace('\\', '/');
                if (normalised.Contains("/Scripts/Input/"))
                    continue;
                var text = File.ReadAllText(file);
                foreach (var token in Forbidden)
                    Assert.That(text, Does.Not.Contain(token), $"{normalised} mentions '{token}'");
                checkedFiles++;
            }
            Assert.That(checkedFiles, Is.GreaterThan(30), "The scan found suspiciously few gameplay files");
        }
    }
}
```

- [ ] **Step 3: Run both**

```bash
Tools/run-tests.sh PlayMode "Blackglass.Tests.RebindingTests"
Tools/run-tests.sh EditMode "Blackglass.Tests.NoDeviceTypesInGameplayTests"
```
Expected: EXIT=0 (`passed="3"` and `passed="1"`). If the boundary test names a file, move the offending code into `Scripts/Input` or onto a semantic action; if the interactive rebind completes with a non-`buttonSouth` path, report the printed path and adjust only the `EndWith` assertion.

- [ ] **Step 4: Write decision 024 and the amendment notes in `Docs/Decisions.md`**

Append to the end of `Docs/Decisions.md`:

```markdown

## 024 — Native controller input

- **Decided (Phase 6.5, 2026-10-05):** Xbox, PlayStation (DualSense), Nintendo and generic gamepads drive every control state through the Unity Input System, with no Steam Input dependency; keyboard/mouse is unchanged and the player switches freely. Gameplay consumes **semantic actions** in `BlackglassControls.inputactions`; physical bindings live there, in one **binding group per family** (`KeyboardMouse`, `Xbox`, `PlayStation`, `Nintendo`, `Gamepad` = generic).
  - **Active family:** `ActiveInputDevice` (on `Systems`) listens to `InputSystem.onEvent` and applies the family with `InputActionAsset.bindingMask` (and, for a controller, `devices` = that one pad, so a second pad cannot also act). Only *meaningful* input switches it (`InputActivityFilter`): a button press edge, a stick crossing 0.55 (released below 0.35, once per crossing), a trigger past 0.5, any key, mouse movement of 4 px or more or scroll. Drift, held controls and mouse jitter never do. The mask is applied on the next `Update`, so the input that woke a family only wakes it. An unplugged active controller falls back to keyboard/mouse. `InputFamilyClassifier` maps `XInputController` → Xbox, `DualShockGamepad` → PlayStation, `SwitchProController` → Nintendo, other gamepads by USB vendor (Sony/Microsoft/Nintendo) else Generic; `ControllerModel` (DualSense, Switch Pro, Switch 2 …) is kept for later prompt splits and never read by gameplay.
  - **Nintendo** is bound deliberately, not positionally: Confirm is the east button (printed A) and Cancel the south button (printed B); other positions match Xbox. Prompts show the printed labels (`GamepadLabels`, text only, no glyph art; `PromptResolver.GetPrompt(action, family)` derives them from the action's current bindings, so a rebind changes the prompt).
  - **Actions:** renamed (name only, IDs kept) `ToggleTacticalPause`, `QueueModifier`, `Cancel`, `ToggleCharacterControl`, `NextCharacter`; added `PreviousCharacter`, `Look`, `CameraModifier`, `Confirm`, `Attack`, `NextTarget`, `PreviousTarget`, `CursorMove`, and a `UI` map (`Navigate`, `Submit`, `Cancel`, gamepad only, not yet read by any component).
  - **Provisional layout:** left stick = move (driving) / pan; right stick = camera look (driving) / tactical cursor (tactical); **RT swaps the right stick's role** (`StickRole`); South = Confirm (select / order / cover), East = Cancel (clear selection, as Esc), West = Attack, North = character control, LB/RB = previous/next character, LT = queue / add to selection (as Shift), View = tactical pause, D-pad up/down = follow / stop, D-pad left/right = previous/next target, L3/R3 = zoom out/in, Menu = reserved. Stick dead zones are `stickDeadzone(0.2, 0.925)` processors in the asset, plus a response curve in code.
  - **Tactical cursor** (`TacticalCursor`): a screen-space cursor moved on unscaled time. It resolves a typed `PointerTarget` (`Friendly`, `Hostile`, `Cover`, `Ground`) with the same classification the mouse uses (`PointerTargetResolver`) and snaps to a friendly then a hostile (1.2 m) then a cover location (0.75 m) around the ground point under it. Confirm and Attack call the same `PlayerCommandInput.Act` / `GroupOrders` path as a mouse click; no controller-specific command, combat, movement or cover code exists. Attack picks the cursor's hostile, else the soft target (D-pad cycles living hostiles by distance), else the best hostile ahead (`HostileTargets`). Multi-select is LT + Confirm on a friendly (the Shift+click path); there is no box select and no select-all.
- **Why:** the owner wants controller parity as a first-class requirement and rebindable, device-agnostic gameplay; a group mask per family gives per-family bindings (the Nintendo difference) and one-pad-at-a-time for free without a second input framework, and a virtual cursor reuses the mouse's whole order path.
- **Rejected:** `PlayerInput` with control schemes (it owns the actions; every input component here uses `InputActionReference`s); one positional `Gamepad` group (Nintendo would get Xbox's face-button conventions); mouse emulation (poor for a controller); a world-space ground cursor (drifts from the camera, needs separate height handling); separate controller command code; box select with a stick; a sticky snap and a lock-on system (not needed yet).
- **Implications:** a new action needs bindings in all four pad groups (an asset test enforces this) and every binding needs a group (an asset test enforces that too: an ungrouped binding silently dies under a mask). Gameplay code must not name devices or face buttons (a test scans for it). A held button or stick on a pad that is no longer active is ignored until it is released and pressed again. Switching family cancels in-progress actions; the existing release gates cover it. Rebinding works through the standard Input System API (`ApplyBindingOverride`, `PerformInteractiveRebinding`, JSON save/load; covered by a test); there is no rebinding UI yet.
- **Known limitations:** Input System 1.20.0 has no Switch 2 layout, so a Switch 2 controller only works if the platform exposes it as a gamepad, and the Switch 2 product ID is unverified. Steam running on the player's machine may present a pad as XInput (shown as Xbox). Only the DualSense has been (or can be) exercised on real hardware; every other family is covered by simulated Input System devices only. The mapping is a first guess and will be tuned in play.
```

In the `## 008` section, add as the **last bullet**:

```markdown
- **Amended 2026-10-05 (Phase 6.5, see 024):** actions were renamed by name only (`Takeover` → `ToggleCharacterControl`, `CycleCharacter` → `NextCharacter`, `TogglePause` → `ToggleTacticalPause`, `Modifier` → `QueueModifier`, `ClearSelection` → `Cancel`), every binding now belongs to a binding group (keyboard/mouse bindings are in `KeyboardMouse` and are unchanged), and gamepad bindings, the `UI` map and new tactical actions were added. The keyboard/mouse behaviour described above is unchanged.
```

In the `## 012` section, add as the **last bullet**:

```markdown
- **Amended 2026-10-05 (Phase 6.5, see 024):** a separate `PreviousCharacter` action (LB) now exists for the gamepad; `NextCharacter` (Tab, RB) still goes backward while `CycleReverse` (Shift) is held, so Tab and Shift+Tab are exactly as before and the Shift+Tab composite is still not used.
```

- [ ] **Step 5: Full verification**

```bash
Tools/run-tests.sh EditMode; echo "EXIT=$?"
Tools/run-tests.sh PlayMode; echo "EXIT=$?"
git status --short
grep -rn "Gamepad.current\|Keyboard.current\|Mouse.current" Assets/_Project/Scripts | grep -v "/Scripts/Input/" ; echo "(expect no lines above)"
grep -E "Exception|error CS" Logs/TestRun-PlayMode.log Logs/TestRun-EditMode.log | sort | uniq -c | sort -rn | head -20
```
Expected totals (compute them from the measured baseline; with the baseline 337 / 303):
- EditMode = 337 + 14 + 14 + 12 + 31 + 8 + 9 + 8 + 1 = **434** (classifier, activity filter, asset, prompts, stick, hostile targets, HUD (5 family cases + 3), boundary).
- PlayMode = 303 + 20 + 10 + 9 + 7 + 13 + 17 + 10 + 3 = **392** (active device, camera pad, direct control pad, resolver, cursor, controller commands, scene, rebinding).
Use the numbers the runs actually print and reconcile any difference against the per-task counts before finishing. Expected: both EXIT=0, zero failures, `git status` clean apart from the untracked `.claude/`, the grep prints nothing, and the exception/error grep shows no line that was not already present in the baseline logs saved in Task 0 (compare with `diff <(grep -E "Exception|error CS" Logs/phase65-baseline-PlayMode.log | sort -u) <(grep -E "Exception|error CS" Logs/TestRun-PlayMode.log | sort -u)`).

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Tests Docs
git commit -m "Pin rebinding and the device boundary in tests; record decision 024

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 7: Hand over for the manual DualSense check (do not merge yet)**

Tell the owner the branch `phase-6-5-controller-input` is ready and ask them to run the checklist with the PS5 controller on USB, **Steam closed**, in the Unity Editor with `Prototype.unity` open (Play mode, Game view focused; the HUD's top-right shows `Input: ...`):

1. Plug in the DualSense. The HUD still says `Input: Keyboard/Mouse` until the pad is used. Press any button or push a stick firmly → `Input: PlayStation`, and the prompt lines (top right) show Cross / Circle / Square / Triangle, L1/R1, L2 etc.
2. Move the mouse a little → `Input: Keyboard/Mouse`; use the pad again → back to `Input: PlayStation`. Repeat several times quickly.
3. Let the pad rest on the desk and then hold it still: no flicker of the family line, nothing moves.
4. Triangle toggles character control (HUD `Takeover ON`); left stick moves the character with analog speed (gentle push = slow); right stick rotates and tilts the camera; R1 / L1 switch character; D-pad up toggles Follow.
5. Create (the small button left of the touchpad) pauses (banner: `TACTICAL PAUSE - Create to resume`). While paused: left stick pans, right stick moves the crosshair cursor, hold R2 and the right stick rotates the camera instead; Cross on a friendly selects it; L2 + Cross on another adds it; Cross on ground orders a move; L2 + Cross queues one; Cross on a hostile attacks it; D-pad left/right cycles hostiles (cursor jumps to them); Circle clears the selection; D-pad down stops the selected units. Nothing moves until you resume.
6. While paused with a unit selected, move the cursor next to a cover marker (they show while paused): the label says `Cover ...`; Cross sends the unit there; L2 + Cross queues another order behind it.
7. Resume (Create) and, while driving, press Square: the controlled character attacks the best hostile ahead. Hold R2 while driving: the right stick moves the cursor instead of the camera.
8. Unplug the pad while using it → the HUD returns to `Input: Keyboard/Mouse` and the keyboard works. Replug and use it again.
9. Watch the Console for red errors during all of this.

Record exactly what the owner reports. In the completion report list **only** the DualSense as physically tested, and only the items the owner confirms; Xbox, Nintendo Switch, Switch 2 and generic pads remain "simulated Input System devices only — physical testing still required". After the owner's check, use superpowers:finishing-a-development-branch (merge to `main` and push are the owner's call).

- [ ] **Step 8: Completion report (in chat, when execution is done)**

Report, in this order: files created; files changed; Input Actions changed; semantic actions added; initial controller mapping; tactical cursor behaviour; camera/controller interaction; target-selection approach; multi-selection approach; active-device detection; controller-family detection; glyph/prompt architecture; dead-zone handling; rebinding readiness; keyboard/mouse regressions checked (name the suites); physical hardware tested; physical hardware still requiring testing; known limitations; architecture concerns before Phase 7 (candidates to evaluate honestly: `PlayerCommandInput` now has two entry points; `CameraModifier` is read by two components; the mask-switch cancellation behaviour; per-family duplication of bindings in the asset; the unverified Switch 2 handling). Do not begin Phase 7.
