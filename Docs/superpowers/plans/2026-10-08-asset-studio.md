# Blackglass Asset Studio v0.1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A standalone WPF tool (Blackglass Asset Studio) that stages FBX/OBJ files, builds a versioned JSON manifest and runs Unity in batch mode, plus an Editor-only Unity importer that configures Humanoid characters (with visual-height normalisation and a visual prefab), Humanoid animation clips, generic props and environment modules, and returns a structured result.

**Architecture:** `Core` (pure .NET: profiles, classification, validation, staging, manifest, Unity launcher, runner) + `App` (thin hand-written MVVM WPF) + a Unity Editor asmdef `Blackglass.AssetPipeline.Editor` that consumes the manifest. The manifest/result DTOs and a few pure rules (height scale, path rules, asset naming) live once as plain C# files in `Assets/_Project/Editor/AssetPipeline/Contract/` and are compiled by both Unity and `Core` (linked files). The app never writes Unity serialized files.

**Tech Stack:** C# / .NET SDK 10 (`net10.0-windows`), WPF, `System.Text.Json`; Unity 6000.3.25f1 Editor APIs (`ModelImporter`, `AssetDatabase`, `PrefabUtility`); xunit (test-only NuGet). No other packages.

**Spec:** `docs/superpowers/specs/2026-10-08-asset-studio-design.md` (read it first; this plan implements it). Branch: `asset-studio`.

## Global Constraints

- Windows desktop; C# and WPF only. No Node, Electron, browser UI, local web server, cloud, telemetry, auth, MVVM/UI packages.
- NuGet packages allowed: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio` in the test project only.
- Contract files (`Assets/_Project/Editor/AssetPipeline/Contract/*.cs`) must compile under both Unity (C# 9, .NET Standard 2.1, no `UnityEngine`) and `Core`: **block-scoped namespaces** (no file-scoped), no records, no `init`, no nullable annotations (`#nullable disable` at the top), public fields, `string`/`float`/`bool`/`int`, nested `[Serializable]` classes, `List<T>` / arrays only. Enums are strings.
- Unity code lives under `Assets/_Project/Editor/AssetPipeline/`, namespace `Blackglass.AssetPipeline`; asmdef `Blackglass.AssetPipeline.Editor` (Editor-only, references `Blackglass`). `Core` namespace `Blackglass.AssetStudio`, `App` namespace `Blackglass.AssetStudio.App`, tests `Blackglass.AssetStudio.Tests`.
- Menu root is `Blackglass/` (existing convention): `Blackglass/Asset Pipeline/Run Manifest...`.
- Manifest/result `schemaVersion` is `1`. Ownership label is `BlackglassStudio`.
- Never modify a source file. Never write machine-specific absolute paths into repository files (settings live under `%AppData%`, runs under `%LocalAppData%`).
- Never auto-commit or push on behalf of the *tool*; the plan's own per-task commits go on branch `asset-studio` only. Do not push, do not merge.
- Do not modify gameplay code, gameplay prefabs (`Darius_Player`, `FriendlyUnit`, ...), scenes or the existing `EnvironmentKitBuilder`. Existing files that may change: `.gitignore`, `Assets/_Project/Tests/EditMode/Blackglass.Tests.EditMode.asmdef` (one reference), `docs/Decisions.md` (append 039).
- Unity allows one instance per project: **the Unity Editor must be closed and only one Unity run may be active at a time** (batch import runs, `Tools/run-tests.sh`). Never run two at once.
- Unity-side scratch imports go to `Assets/_AssetStudioScratch/` and are deleted (folder and its `.meta`) at the end of every test or manual run; never commit anything under it.
- New files under `Assets/` need their `.meta` files committed: Unity generates them the first time it runs (Task 8). Commit the `.meta` of every new asset file and folder you add.
- Commit trailer on every commit: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Never use `??` or `?.` on a `UnityEngine.Object` (fake null); use `x != null`.
- Deviations from the spec made in this plan (record them in decision 039): (1) no `StartAssetEditing` bracket around the batch, because each item must be imported synchronously before it is measured; (2) Validate is local only, and measured values come back from the import. The character block keeps both `rigHumanoid` and `createAvatar`, as the brief lists both.

## Review Focus

Failure modes the spec implies but a plain reading would not test; each has a test in the named task:

1. Paths and names with spaces / non-ASCII (`Darius Walk Gun.fbx`, `Café Prop.fbx`) are staged, sanitised to a valid asset name, and launched without quoting bugs. (Tasks 2, 5, 6)
2. Two list entries that resolve to the same asset path in one run must be rejected before Unity starts, not silently overwrite each other. (Task 4)
3. Unity exits without writing `result.json` (crash, compile error, editor open): the user gets a message plus the log path and the last error lines, never "Import failed". (Task 6)
4. Re-importing the same item updates the Studio-owned assets (same GUIDs, no duplicates, no conflict error) while an unlabelled existing asset is refused unless overwrite is ticked. (Tasks 10, 11, 12)
5. An animation whose shared Avatar is missing, not humanoid or invalid, and a character whose Humanoid mapping fails, fail their item with an actionable message and create no prefab. (Tasks 10, 11)
6. A model with no renderers, or measured height 0, never yields NaN / infinite scale; a manual override still works. (Tasks 1, 10)

## File Structure

```
.gitignore                                              (modify)
Tools/BlackglassAssetStudio/
  BlackglassAssetStudio.sln
  src/Core/BlackglassAssetStudio.Core.csproj
  src/Core/Json/ManifestJson.cs
  src/Core/Profiles/ImportProfile.cs                    profile table + defaults
  src/Core/Items/AssetItem.cs  AssetNames.cs  AssetItemFactory.cs
  src/Core/Classification/AssetClassifier.cs
  src/Core/Settings/AppSettings.cs  SettingsStore.cs  AppPaths.cs
  src/Core/Locating/ProjectLocator.cs  UnityLocator.cs
  src/Core/Validation/ItemValidator.cs  OwnershipProbe.cs
  src/Core/Run/RunStaging.cs  ManifestBuilder.cs  GitSnapshot.cs  LogExcerpt.cs  ResultFormatter.cs
  src/Core/Run/UnityLauncher.cs  ImportRunner.cs
  src/App/BlackglassAssetStudio.App.csproj  App.xaml(.cs)  MainWindow.xaml(.cs)  SettingsWindow.xaml(.cs)
  src/App/ViewModels/ViewModelBase.cs  RelayCommand.cs  MainViewModel.cs  AssetItemViewModel.cs
  tests/Core.Tests/BlackglassAssetStudio.Core.Tests.csproj  Fixtures/manifest-v1.json  *Tests.cs  TestSupport/TempDir.cs
Assets/_Project/Editor/AssetPipeline/
  Blackglass.AssetPipeline.Editor.asmdef
  Contract/ImportManifest.cs  ImportResult.cs  HeightScale.cs  PathRules.cs  AssetNaming.cs
  Entry/AssetPipelineRunner.cs
  Importers/AssetPaths.cs  OwnershipLabel.cs  SourceCopier.cs  ModelMeasure.cs  ModelPrefabWriter.cs
            CharacterImporter.cs  AnimationImporter.cs  PropImporter.cs  EnvironmentModuleImporter.cs  EnvironmentSpecs.cs
Assets/_Project/Tests/EditMode/AssetPipeline/  (Unity EditMode tests, namespace Blackglass.Tests)
docs/AssetStudio.md  docs/Decisions.md (append 039)
```

### Running things (read before the first task)

- .NET tests: `dotnet test Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln` (from the repo root, bash). Expect `Passed!` lines; any `error CS` is a failure.
- Unity EditMode tests: close the Editor, then `Tools/run-tests.sh EditMode "Blackglass.Tests.AssetPipeline"` (the filter is the namespace prefix). The script prints `EXIT=0` on success and the `Logs/TestRun-EditMode.log` path.
- The full existing suites must stay green at the end: `Tools/run-tests.sh EditMode` (baseline 899/846 per project notes; compare against the number recorded at the start of Task 8) and `Tools/run-tests.sh PlayMode`.

---

## Task 1: Solution scaffold, shared contract, height scale and path rules

**Files:**
- Modify: `.gitignore`
- Create: `Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln`, `src/Core/BlackglassAssetStudio.Core.csproj`, `src/Core/Json/ManifestJson.cs`, `tests/Core.Tests/BlackglassAssetStudio.Core.Tests.csproj`, `tests/Core.Tests/Fixtures/manifest-v1.json`, `tests/Core.Tests/ContractTests.cs`, `HeightScaleTests.cs`, `PathRulesTests.cs`, `AssetNamingTests.cs`
- Create (Unity folder, no Unity run yet): `Assets/_Project/Editor/AssetPipeline/Contract/ImportManifest.cs`, `ImportResult.cs`, `HeightScale.cs`, `PathRules.cs`, `AssetNaming.cs`

**Interfaces:**
- Produces (Contract, namespace `Blackglass.AssetPipeline`): `ContractInfo.SchemaVersion/OwnershipLabel`; string-constant classes `ProfileIds`, `AnimationCategories`, `LoopModes`, `ThemeModes`, `ScaleSources`, `MaterialModes`, `EnvironmentElements.All`; DTOs `ImportManifest`, `ImportItem`, `CharacterSettings`, `AnimationSettings`, `PropSettings`, `EnvironmentSettings`, `ImportResult`, `ItemResult`, `Size3`, `AvatarReport`, `ClipReport`; `HeightScale.Compute(float measured, float target, bool normalize, float manualOverride) -> ScaleResult{scale, source, error, Ok}`; `PathRules.Normalize(string)`, `PathRules.ValidateDestination(string) -> string` (null = valid), `PathRules.ValidateAssetName(string) -> string`; `AssetNaming.ModelPath(ImportItem)`, `AssetNaming.PrefabPath(ImportItem) -> string` (null when none).
- Produces (Core): `Blackglass.AssetStudio.ManifestJson.Serialize<T>/Deserialize<T>/Clone<T>`.

- [ ] **Step 1: Ignore rules.** Append to `.gitignore`:

```gitignore

# Blackglass Asset Studio (.NET solution under Tools/; the global ignores above hide csproj/sln files)
!/Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln
!/Tools/BlackglassAssetStudio/src/**/*.csproj
!/Tools/BlackglassAssetStudio/tests/**/*.csproj
/Tools/BlackglassAssetStudio/**/bin/
/Tools/BlackglassAssetStudio/**/obj/
/Tools/BlackglassAssetStudio/publish/
TestResults/
```

- [ ] **Step 2: Scaffold the solution** (bash, repo root):

```bash
mkdir -p Tools/BlackglassAssetStudio/src/Core/Json Tools/BlackglassAssetStudio/tests/Core.Tests/Fixtures Assets/_Project/Editor/AssetPipeline/Contract
cat > Tools/BlackglassAssetStudio/src/Core/BlackglassAssetStudio.Core.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <AssemblyName>BlackglassAssetStudio.Core</AssemblyName>
    <RootNamespace>Blackglass.AssetStudio</RootNamespace>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <!-- One definition of the manifest/result contract: the same files Unity compiles. -->
    <Compile Include="..\..\..\..\Assets\_Project\Editor\AssetPipeline\Contract\*.cs" Link="Contract\%(Filename)%(Extension)" />
  </ItemGroup>
</Project>
EOF
cat > Tools/BlackglassAssetStudio/tests/Core.Tests/BlackglassAssetStudio.Core.Tests.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RootNamespace>Blackglass.AssetStudio.Tests</RootNamespace>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Core\BlackglassAssetStudio.Core.csproj" />
    <None Include="Fixtures\*.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
EOF
cd Tools/BlackglassAssetStudio
dotnet add tests/Core.Tests package Microsoft.NET.Test.Sdk
dotnet add tests/Core.Tests package xunit
dotnet add tests/Core.Tests package xunit.runner.visualstudio
dotnet new sln -n BlackglassAssetStudio --format sln
dotnet sln add src/Core/BlackglassAssetStudio.Core.csproj tests/Core.Tests/BlackglassAssetStudio.Core.Tests.csproj
cd ../..
```
If `dotnet add package` of `xunit` pulls a v3-only package set, keep what NuGet resolves (tests below use only `[Fact]`, `[Theory]`, `Assert.*`, which both versions support).

- [ ] **Step 3: Golden fixture** `tests/Core.Tests/Fixtures/manifest-v1.json`:

```json
{
  "schemaVersion": 1,
  "runId": "20261008-153012-ab12",
  "resultPath": "C:\\Temp\\runs\\20261008-153012-ab12\\result.json",
  "items": [
    {
      "id": "char1",
      "profile": "HumanoidCharacter",
      "name": "Kestrel",
      "sourcePath": "C:\\Temp\\runs\\20261008-153012-ab12\\staged\\char1\\Kestrel Model.fbx",
      "destinationFolder": "Assets/Art/Characters/Kestrel",
      "allowOverwrite": false,
      "character": { "targetHeight": 1.85, "normalizeHeight": true, "scaleOverride": 0.0, "rigHumanoid": true, "createAvatar": true, "materialMode": "Embedded", "generatePrefab": true }
    },
    {
      "id": "anim1",
      "profile": "HumanoidAnimation",
      "name": "Kestrel_Walk",
      "sourcePath": "C:\\Temp\\runs\\20261008-153012-ab12\\staged\\anim1\\Walk.fbx",
      "destinationFolder": "Assets/Art/Animations",
      "allowOverwrite": true,
      "animation": { "category": "Locomotion", "clipName": "Walk", "sharedAvatarPath": "Assets/Art/Characters/Darius/Models/Darius Stand Idle.fbx", "loop": "Yes", "bakeRotation": true, "bakeHeight": true, "bakePositionXZ": true }
    },
    {
      "id": "env1",
      "profile": "EnvironmentModule",
      "name": "WallStraight_B",
      "sourcePath": "C:\\Temp\\runs\\20261008-153012-ab12\\staged\\env1\\wall.fbx",
      "destinationFolder": "Assets/_Project/Environment/Modules",
      "allowOverwrite": false,
      "environment": { "element": "WallStraight", "themePath": "Assets/_Project/Environment/Themes/CorporatePrototype/CorporatePrototype.asset", "themeMode": "Append", "replaceIndex": 0, "scale": 1.0 }
    }
  ]
}
```

- [ ] **Step 4: Write the failing tests.**

`tests/Core.Tests/HeightScaleTests.cs`:

```csharp
using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class HeightScaleTests
{
    [Fact]
    public void Auto_scale_is_target_over_measured()
    {
        var r = HeightScale.Compute(2.4f, 1.85f, true, 0f);
        Assert.True(r.Ok);
        Assert.Equal(ScaleSources.Auto, r.source);
        Assert.InRange(r.scale, 1.85f / 2.4f - 1e-5f, 1.85f / 2.4f + 1e-5f);
    }

    [Fact]
    public void Manual_override_wins_even_when_measurement_is_useless()
    {
        var r = HeightScale.Compute(0f, 1.85f, true, 0.5f);
        Assert.True(r.Ok);
        Assert.Equal(ScaleSources.Manual, r.source);
        Assert.Equal(0.5f, r.scale);
    }

    [Fact]
    public void Normalize_off_means_no_scaling()
    {
        var r = HeightScale.Compute(2.4f, 1.85f, false, 0f);
        Assert.True(r.Ok);
        Assert.Equal(ScaleSources.None, r.source);
        Assert.Equal(1f, r.scale);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.005f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Unusable_measured_height_is_an_error_not_a_nan_scale(float measured)
    {
        var r = HeightScale.Compute(measured, 1.85f, true, 0f);
        Assert.False(r.Ok);
        Assert.Contains("manual", r.error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void Non_positive_target_is_an_error(float target)
    {
        Assert.False(HeightScale.Compute(2.4f, target, true, 0f).Ok);
    }

    [Theory]
    [InlineData(-0.5f)]
    [InlineData(float.NaN)]
    public void Invalid_manual_override_is_an_error(float manual)
    {
        Assert.False(HeightScale.Compute(2.4f, 1.85f, true, manual).Ok);
    }
}
```

`tests/Core.Tests/PathRulesTests.cs`:

```csharp
using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class PathRulesTests
{
    [Theory]
    [InlineData("Assets/Art/Characters/Kestrel")]
    [InlineData("Assets/My Folder/Sub Folder")]
    [InlineData("Assets\\Art\\Props")]
    public void Valid_destinations(string folder) => Assert.Null(PathRules.ValidateDestination(folder));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Assets")]
    [InlineData("assets/x")]
    [InlineData("Packages/com.x/y")]
    [InlineData("Assets/../Library")]
    [InlineData("Assets//x")]
    [InlineData("C:/x/y")]
    [InlineData("Assets/a:b")]
    [InlineData("Assets/a?b")]
    [InlineData("Assets/trailing./x")]
    [InlineData("Assets/x /y")]
    public void Invalid_destinations(string folder) => Assert.NotNull(PathRules.ValidateDestination(folder));

    [Theory]
    [InlineData("Kestrel")]
    [InlineData("Darius_Walk_Gun")]
    [InlineData("Café Prop")]
    public void Valid_names(string name) => Assert.Null(PathRules.ValidateAssetName(name));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData(".hidden")]
    [InlineData("name.")]
    public void Invalid_names(string name) => Assert.NotNull(PathRules.ValidateAssetName(name));

    [Fact]
    public void Normalize_uses_forward_slashes_and_no_trailing_slash() =>
        Assert.Equal("Assets/A/B", PathRules.Normalize("Assets\\A\\B/ "));
}
```

`tests/Core.Tests/AssetNamingTests.cs`:

```csharp
using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class AssetNamingTests
{
    static ImportItem Item(string profile, string name = "Kestrel", string source = "C:\\x\\a.FBX") =>
        new ImportItem { profile = profile, name = name, sourcePath = source, destinationFolder = "Assets/Art/Characters/Kestrel" };

    [Fact]
    public void Model_path_uses_name_and_lowercase_source_extension() =>
        Assert.Equal("Assets/Art/Characters/Kestrel/Kestrel.fbx", AssetNaming.ModelPath(Item(ProfileIds.HumanoidCharacter)));

    [Fact]
    public void Character_prefab_has_visual_suffix_and_follows_generate_flag()
    {
        var item = Item(ProfileIds.HumanoidCharacter);
        Assert.Equal("Assets/Art/Characters/Kestrel/Kestrel_Visual.prefab", AssetNaming.PrefabPath(item));
        item.character.generatePrefab = false;
        Assert.Null(AssetNaming.PrefabPath(item));
    }

    [Fact]
    public void Animation_has_no_prefab() => Assert.Null(AssetNaming.PrefabPath(Item(ProfileIds.HumanoidAnimation)));

    [Fact]
    public void Prop_prefab_follows_generate_flag_and_env_always_has_one()
    {
        var prop = Item(ProfileIds.GenericProp, "Crate");
        Assert.Equal("Assets/Art/Characters/Kestrel/Crate.prefab", AssetNaming.PrefabPath(prop));
        prop.prop.generatePrefab = false;
        Assert.Null(AssetNaming.PrefabPath(prop));
        Assert.NotNull(AssetNaming.PrefabPath(Item(ProfileIds.EnvironmentModule, "Wall")));
    }
}
```

`tests/Core.Tests/ContractTests.cs`:

```csharp
using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class ContractTests
{
    static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "manifest-v1.json"));

    [Fact]
    public void Golden_fixture_deserialises()
    {
        var m = ManifestJson.Deserialize<ImportManifest>(Fixture());
        Assert.Equal(1, m.schemaVersion);
        Assert.Equal(3, m.items.Length);
        Assert.Equal(ProfileIds.HumanoidCharacter, m.items[0].profile);
        Assert.Equal(1.85f, m.items[0].character.targetHeight);
        Assert.Contains(" ", m.items[0].sourcePath);
        Assert.Equal("Walk", m.items[1].animation.clipName);
        Assert.True(m.items[1].allowOverwrite);
        Assert.Equal(AnimationCategories.Locomotion, m.items[1].animation.category);
        Assert.Equal("WallStraight", m.items[2].environment.element);
        Assert.Equal(ThemeModes.Append, m.items[2].environment.themeMode);
    }

    [Fact]
    public void Round_trip_is_stable()
    {
        var first = ManifestJson.Serialize(ManifestJson.Deserialize<ImportManifest>(Fixture()));
        var second = ManifestJson.Serialize(ManifestJson.Deserialize<ImportManifest>(first));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Blocks_missing_from_json_keep_usable_defaults()
    {
        var m = ManifestJson.Deserialize<ImportManifest>("{\"schemaVersion\":1,\"items\":[{\"id\":\"a\"}]}");
        Assert.NotNull(m.items[0].character);
        Assert.NotNull(m.items[0].animation);
        Assert.Equal(1f, m.items[0].prop.scale);
        Assert.Equal(1f, m.items[0].environment.scale);
    }

    [Fact]
    public void Result_round_trips()
    {
        var r = new ImportResult { runId = "r", success = true, unityVersion = "6000.3.25f1" };
        var item = new ItemResult { id = "i", success = true, measuredHeight = 2.4f, appliedScale = 0.77f, scaleSource = ScaleSources.Auto };
        item.importedAssets.Add("Assets/x.fbx");
        item.clips.Add(new ClipReport { name = "Walk", duration = 1.03f, loop = true });
        r.items.Add(item);
        var back = ManifestJson.Deserialize<ImportResult>(ManifestJson.Serialize(r));
        Assert.Equal("Assets/x.fbx", back.items[0].importedAssets[0]);
        Assert.Equal("Walk", back.items[0].clips[0].name);
        Assert.Equal(ScaleSources.Auto, back.items[0].scaleSource);
    }

    [Fact]
    public void Environment_element_list_has_the_twelve_known_elements()
    {
        Assert.Equal(12, EnvironmentElements.All.Length);
        Assert.Contains("WallStraight", EnvironmentElements.All);
    }
}
```

- [ ] **Step 5: Run to verify failure.** `dotnet test Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln` → FAIL (`error CS0246` / `The type or namespace name 'Blackglass'`/`ManifestJson` not found).

- [ ] **Step 6: Write the contract.** `Assets/_Project/Editor/AssetPipeline/Contract/ImportManifest.cs`:

```csharp
#nullable disable
using System;

namespace Blackglass.AssetPipeline
{
    /// <summary>Shared by Asset Studio (.NET) and the Unity importer. Compiled by both: keep it plain (see the plan's Global Constraints).</summary>
    public static class ContractInfo
    {
        public const int SchemaVersion = 1;
        /// <summary>AssetDatabase label put on every asset the pipeline creates; its presence allows silent updates.</summary>
        public const string OwnershipLabel = "BlackglassStudio";
    }

    public static class ProfileIds
    {
        public const string HumanoidCharacter = "HumanoidCharacter";
        public const string HumanoidAnimation = "HumanoidAnimation";
        public const string GenericProp = "GenericProp";
        public const string EnvironmentModule = "EnvironmentModule";
    }

    public static class AnimationCategories
    {
        public const string Locomotion = "Locomotion";
        public const string Combat = "Combat";
        public const string Reaction = "Reaction";
        public const string Death = "Death";
    }

    public static class LoopModes
    {
        public const string Auto = "Auto";
        public const string Yes = "Yes";
        public const string No = "No";
    }

    public static class ThemeModes
    {
        public const string None = "None";
        public const string Append = "Append";
        public const string Replace = "Replace";
    }

    public static class ScaleSources
    {
        public const string None = "None";
        public const string Auto = "Auto";
        public const string Manual = "Manual";
    }

    public static class MaterialModes
    {
        public const string Embedded = "Embedded";
        public const string External = "External";
    }

    /// <summary>Names of Blackglass.EnvironmentElement. A Unity EditMode test pins this list to the enum.</summary>
    public static class EnvironmentElements
    {
        public static readonly string[] All =
        {
            "Floor", "WallStraight", "WallCorner", "WallEnd", "WallJunction", "DoorFrame",
            "LowCover", "LowCoverLong", "Pillar", "Crate", "Terminal", "LightFixture",
        };
    }

    [Serializable]
    public class ImportManifest
    {
        public int schemaVersion = ContractInfo.SchemaVersion;
        public string runId = "";
        public string resultPath = "";
        public ImportItem[] items = new ImportItem[0];
    }

    /// <summary>One asset to import. Only the block matching <see cref="profile"/> is read; the others are ignored.</summary>
    [Serializable]
    public class ImportItem
    {
        public string id = "";
        public string profile = ProfileIds.GenericProp;
        public string name = "";
        public string sourcePath = "";
        public string destinationFolder = "";
        public bool allowOverwrite;
        public CharacterSettings character = new CharacterSettings();
        public AnimationSettings animation = new AnimationSettings();
        public PropSettings prop = new PropSettings();
        public EnvironmentSettings environment = new EnvironmentSettings();
    }

    [Serializable]
    public class CharacterSettings
    {
        public float targetHeight = 1.85f;
        public bool normalizeHeight = true;
        /// <summary>0 = none. A positive value is the visual scale to use, whatever was measured.</summary>
        public float scaleOverride;
        public bool rigHumanoid = true;
        public bool createAvatar = true;
        public string materialMode = MaterialModes.Embedded;
        public bool generatePrefab = true;
    }

    [Serializable]
    public class AnimationSettings
    {
        public string category = AnimationCategories.Locomotion;
        public string clipName = "";
        /// <summary>Project-relative path of a Humanoid model whose Avatar the clip copies.</summary>
        public string sharedAvatarPath = "";
        public string loop = LoopModes.Auto;
        public bool bakeRotation;
        public bool bakeHeight;
        public bool bakePositionXZ;
    }

    [Serializable]
    public class PropSettings
    {
        public bool generatePrefab = true;
        public float scale = 1f;
    }

    [Serializable]
    public class EnvironmentSettings
    {
        public string element = "";
        public string themePath = "";
        public string themeMode = ThemeModes.None;
        public int replaceIndex;
        /// <summary>Explicit user scale correction; 1 = none. Never chosen automatically.</summary>
        public float scale = 1f;
    }
}
```

`ImportResult.cs`:

```csharp
#nullable disable
using System;
using System.Collections.Generic;

namespace Blackglass.AssetPipeline
{
    [Serializable]
    public class ImportResult
    {
        public int schemaVersion = ContractInfo.SchemaVersion;
        public string runId = "";
        public string unityVersion = "";
        public bool success;
        public List<string> errors = new List<string>();
        public List<string> warnings = new List<string>();
        public List<ItemResult> items = new List<ItemResult>();
    }

    [Serializable]
    public class ItemResult
    {
        public string id = "";
        public bool success;
        public List<string> errors = new List<string>();
        public List<string> warnings = new List<string>();
        public List<string> importedAssets = new List<string>();
        public List<string> createdPrefabs = new List<string>();
        public List<string> changedAssets = new List<string>();
        public Size3 dimensions = new Size3();
        public float measuredHeight;
        public float targetHeight;
        public float appliedScale = 1f;
        public string scaleSource = ScaleSources.None;
        public AvatarReport avatar = new AvatarReport();
        public List<ClipReport> clips = new List<ClipReport>();
        public string registeredInTheme = "";
    }

    [Serializable]
    public class Size3
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    public class AvatarReport
    {
        public bool valid;
        public bool isHuman;
        public string message = "";
    }

    [Serializable]
    public class ClipReport
    {
        public string name = "";
        public float duration;
        public bool loop;
        public bool bakeRotation;
        public bool bakeHeight;
        public bool bakePositionXZ;
    }
}
```

`HeightScale.cs`:

```csharp
#nullable disable
using System;

namespace Blackglass.AssetPipeline
{
    public struct ScaleResult
    {
        public float scale;
        public string source;
        public string error;
        public bool Ok { get { return string.IsNullOrEmpty(error); } }
    }

    /// <summary>The one rule for the visual scale of a character. Applied to the visual hierarchy only, never to a gameplay root.</summary>
    public static class HeightScale
    {
        /// <summary>Below this a measurement is treated as "no geometry" and cannot drive a scale.</summary>
        public const float MinMeasuredHeight = 0.01f;

        public static ScaleResult Compute(float measuredHeight, float targetHeight, bool normalize, float manualOverride)
        {
            if (float.IsNaN(manualOverride) || float.IsInfinity(manualOverride) || manualOverride < 0f)
                return Fail("The manual scale must be a positive number (or 0 for none).");
            if (manualOverride > 0f)
                return new ScaleResult { scale = manualOverride, source = ScaleSources.Manual };
            if (!normalize)
                return new ScaleResult { scale = 1f, source = ScaleSources.None };
            if (float.IsNaN(targetHeight) || float.IsInfinity(targetHeight) || targetHeight <= 0f)
                return Fail("The target height must be greater than 0.");
            if (float.IsNaN(measuredHeight) || float.IsInfinity(measuredHeight) || measuredHeight < MinMeasuredHeight)
                return Fail("The model's measured height is unusable (" + measuredHeight + " m): it may have no meshes. Set a manual scale instead.");
            return new ScaleResult { scale = targetHeight / measuredHeight, source = ScaleSources.Auto };
        }

        static ScaleResult Fail(string message)
        {
            return new ScaleResult { scale = 1f, source = ScaleSources.None, error = message };
        }
    }
}
```

`PathRules.cs`:

```csharp
#nullable disable
using System;

namespace Blackglass.AssetPipeline
{
    /// <summary>Project-relative destination and asset-name rules, shared by the app (before staging) and Unity (before copying).</summary>
    public static class PathRules
    {
        static readonly char[] InvalidSegmentChars = { '<', '>', ':', '"', '|', '?', '*' };

        public static string Normalize(string folder)
        {
            if (folder == null) return "";
            return folder.Trim().Replace('\\', '/').TrimEnd('/').Trim();
        }

        /// <summary>Null when the destination is acceptable, otherwise a message for the user.</summary>
        public static string ValidateDestination(string folder)
        {
            var path = Normalize(folder);
            if (path.Length == 0) return "The destination folder is empty.";
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                return "The destination must be a folder inside the project's Assets folder (for example Assets/Art/Props).";
            foreach (var segment in path.Split('/'))
            {
                if (segment.Length == 0) return "The destination contains an empty folder name.";
                if (segment == "." || segment == "..") return "The destination must not contain '.' or '..'.";
                if (segment.IndexOfAny(InvalidSegmentChars) >= 0 || HasControl(segment))
                    return "The destination folder name '" + segment + "' contains an invalid character.";
                if (segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal))
                    return "The destination folder name '" + segment + "' must not end with a dot or a space.";
            }
            return null;
        }

        /// <summary>Null when the name is usable as an asset file name.</summary>
        public static string ValidateAssetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "The asset name is empty.";
            if (name.IndexOfAny(InvalidSegmentChars) >= 0 || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || HasControl(name))
                return "The asset name contains an invalid character.";
            if (name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith(".", StringComparison.Ordinal))
                return "The asset name must not start or end with a dot.";
            if (name != name.Trim()) return "The asset name must not start or end with a space.";
            return null;
        }

        static bool HasControl(string s)
        {
            foreach (var c in s)
                if (char.IsControl(c)) return true;
            return false;
        }
    }
}
```

`AssetNaming.cs`:

```csharp
#nullable disable
using System.IO;

namespace Blackglass.AssetPipeline
{
    /// <summary>Where an item's assets end up. Used by the app to detect conflicts and by Unity to write them.</summary>
    public static class AssetNaming
    {
        public static string ModelPath(ImportItem item)
        {
            return PathRules.Normalize(item.destinationFolder) + "/" + item.name + Path.GetExtension(item.sourcePath).ToLowerInvariant();
        }

        /// <summary>The prefab path, or null when the profile creates none (animations, or generatePrefab off).</summary>
        public static string PrefabPath(ImportItem item)
        {
            var folder = PathRules.Normalize(item.destinationFolder);
            switch (item.profile)
            {
                case ProfileIds.HumanoidCharacter:
                    return item.character.generatePrefab ? folder + "/" + item.name + "_Visual.prefab" : null;
                case ProfileIds.GenericProp:
                    return item.prop.generatePrefab ? folder + "/" + item.name + ".prefab" : null;
                case ProfileIds.EnvironmentModule:
                    return folder + "/" + item.name + ".prefab";
                default:
                    return null;
            }
        }
    }
}
```

`src/Core/Json/ManifestJson.cs`:

```csharp
using System.Text.Json;

namespace Blackglass.AssetStudio;

/// <summary>JSON settings for the manifest and result: public fields, camelCase names exactly as declared in the contract.</summary>
public static class ManifestJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, IncludeFields = true };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("The JSON document is empty.");

    public static T Clone<T>(T value) => Deserialize<T>(Serialize(value));
}
```

- [ ] **Step 7: Run to verify pass.** `dotnet test Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln` → all tests pass. If a `Unusable_measured_height…(NaN)` case fails because `0.005f < Min` is false for NaN, the code already tests `IsNaN` first; check there is no typo.

- [ ] **Step 8: Verify git sees the right files.** `git status --short` must list `.gitignore`, `Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln`, the two csproj files, the sources/tests and the contract folder, and must NOT list any `bin/` or `obj/` path.

- [ ] **Step 9: Commit.**

```bash
git add .gitignore Tools/BlackglassAssetStudio Assets/_Project/Editor/AssetPipeline
git commit -m "Asset Studio: solution scaffold, shared manifest contract, height scale and path rules

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

## Task 2: Profiles, asset names, classification and item factory

**Files:**
- Create: `src/Core/Settings/AppSettings.cs` (the data class only; the store is Task 3), `src/Core/Profiles/ImportProfile.cs`, `src/Core/Items/AssetItem.cs`, `src/Core/Items/AssetNames.cs`, `src/Core/Items/AssetItemFactory.cs`, `src/Core/Classification/AssetClassifier.cs`
- Test: `tests/Core.Tests/ProfileTests.cs`, `ClassifierTests.cs`, `AssetItemFactoryTests.cs`

**Interfaces:**
- Consumes: Contract (Task 1).
- Produces:
  - `AppSettings { string ProjectPath, UnityExePath, StagingFolder, DefaultSharedAvatarPath, LastDropFolder; float DefaultTargetHeight = 1.85f }`.
  - `ImportProfile(string Id, string DisplayName, string Kind, string Category, string[] Extensions)`; `ImportProfiles.Character/Locomotion/Combat/Reaction/Death/Prop/Environment` id constants, `All`, `Get(id)`, `For(kind, category)`, `Apply(ImportItem, ImportProfile, AppSettings)`, `DefaultDestination(ImportProfile, string name)`.
  - `Classification(string ProfileId, string Reason, string ClipName, string Element, bool Supported)`; `AssetClassifier.Classify(string path, string? dropFolder = null)`.
  - `AssetNames.FromFileStem(string stem)`; `AssetItem { string Id, OriginalPath, ProfileId, ClassificationReason; string? UnsupportedReason; ImportItem Import }`; `AssetItemFactory.Create(string path, string? dropFolder, AppSettings settings)`.

- [ ] **Step 1: Write failing tests.**

`tests/Core.Tests/ProfileTests.cs`:

```csharp
using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class ProfileTests
{
    static ImportItem Apply(string profileId, AppSettings? settings = null)
    {
        var item = new ImportItem { name = "Kestrel" };
        ImportProfiles.Apply(item, ImportProfiles.Get(profileId), settings ?? new AppSettings());
        return item;
    }

    [Fact]
    public void There_are_seven_profiles() => Assert.Equal(7, ImportProfiles.All.Count);

    [Fact]
    public void Unknown_profile_id_throws() => Assert.Throws<ArgumentException>(() => ImportProfiles.Get("Nope"));

    [Fact]
    public void For_maps_kind_and_category_back_to_the_profile()
    {
        Assert.Equal(ImportProfiles.Death, ImportProfiles.For(ProfileIds.HumanoidAnimation, AnimationCategories.Death).Id);
        Assert.Equal(ImportProfiles.Prop, ImportProfiles.For(ProfileIds.GenericProp, "").Id);
    }

    [Fact]
    public void Character_defaults_use_the_settings_target_height()
    {
        var item = Apply(ImportProfiles.Character, new AppSettings { DefaultTargetHeight = 1.9f });
        Assert.Equal(ProfileIds.HumanoidCharacter, item.profile);
        Assert.Equal(1.9f, item.character.targetHeight);
        Assert.True(item.character.normalizeHeight);
        Assert.True(item.character.rigHumanoid);
        Assert.True(item.character.generatePrefab);
        Assert.Equal("Assets/Art/Characters/Kestrel", item.destinationFolder);
    }

    [Theory]
    [InlineData(ImportProfiles.Locomotion, AnimationCategories.Locomotion, LoopModes.Yes, true, true, true)]
    [InlineData(ImportProfiles.Combat, AnimationCategories.Combat, LoopModes.No, false, true, false)]
    [InlineData(ImportProfiles.Reaction, AnimationCategories.Reaction, LoopModes.No, false, true, false)]
    [InlineData(ImportProfiles.Death, AnimationCategories.Death, LoopModes.No, false, true, true)]
    public void Animation_defaults(string profile, string category, string loop, bool rot, bool height, bool xz)
    {
        var item = Apply(profile, new AppSettings { DefaultSharedAvatarPath = "Assets/A.fbx" });
        Assert.Equal(ProfileIds.HumanoidAnimation, item.profile);
        Assert.Equal(category, item.animation.category);
        Assert.Equal(loop, item.animation.loop);
        Assert.Equal(rot, item.animation.bakeRotation);
        Assert.Equal(height, item.animation.bakeHeight);
        Assert.Equal(xz, item.animation.bakePositionXZ);
        Assert.Equal("Assets/A.fbx", item.animation.sharedAvatarPath);
        Assert.Equal("Assets/Art/Animations", item.destinationFolder);
    }

    [Fact]
    public void Prop_and_environment_never_normalise_scale()
    {
        Assert.Equal(1f, Apply(ImportProfiles.Prop).prop.scale);
        var env = Apply(ImportProfiles.Environment);
        Assert.Equal(1f, env.environment.scale);
        Assert.Equal(ThemeModes.None, env.environment.themeMode);
        Assert.Equal("Assets/_Project/Environment/Modules", env.destinationFolder);
    }

    [Fact]
    public void Obj_is_allowed_for_props_and_modules_only()
    {
        Assert.DoesNotContain(".obj", ImportProfiles.Get(ImportProfiles.Character).Extensions);
        Assert.DoesNotContain(".obj", ImportProfiles.Get(ImportProfiles.Locomotion).Extensions);
        Assert.Contains(".obj", ImportProfiles.Get(ImportProfiles.Prop).Extensions);
        Assert.Contains(".obj", ImportProfiles.Get(ImportProfiles.Environment).Extensions);
    }
}
```

`tests/Core.Tests/ClassifierTests.cs`:

```csharp
using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class ClassifierTests
{
    [Theory]
    [InlineData("C:\\x\\Idle.fbx", ImportProfiles.Locomotion)]
    [InlineData("C:\\x\\Walking.fbx", ImportProfiles.Locomotion)]
    [InlineData("C:\\x\\Run_Gun.fbx", ImportProfiles.Locomotion)]
    [InlineData("C:\\x\\Fire.fbx", ImportProfiles.Combat)]
    [InlineData("C:\\x\\Reload.fbx", ImportProfiles.Combat)]
    [InlineData("C:\\x\\Hit Reaction.fbx", ImportProfiles.Reaction)]
    [InlineData("C:\\x\\Darius Crouch Death.fbx", ImportProfiles.Death)]
    [InlineData("C:\\x\\Die.fbx", ImportProfiles.Death)]
    [InlineData("C:\\x\\DariusFire.fbx", ImportProfiles.Combat)]
    public void Animation_keywords(string path, string expected) =>
        Assert.Equal(expected, AssetClassifier.Classify(path).ProfileId);

    [Fact]
    public void Death_beats_locomotion_when_both_appear() =>
        Assert.Equal(ImportProfiles.Death, AssetClassifier.Classify("C:\\x\\Crouch Death.fbx").ProfileId);

    [Theory]
    [InlineData("C:\\x\\wall_straight.fbx", "WallStraight")]
    [InlineData("C:\\x\\Wall Corner A.fbx", "WallCorner")]
    [InlineData("C:\\x\\wall_end.fbx", "WallEnd")]
    [InlineData("C:\\x\\Wall Junction.fbx", "WallJunction")]
    [InlineData("C:\\x\\door_frame.fbx", "DoorFrame")]
    [InlineData("C:\\x\\Floor.fbx", "Floor")]
    [InlineData("C:\\x\\pillar.fbx", "Pillar")]
    [InlineData("C:\\x\\low_cover_long.fbx", "LowCoverLong")]
    [InlineData("C:\\x\\LowCover.fbx", "LowCover")]
    [InlineData("C:\\x\\crate.fbx", "Crate")]
    public void Environment_keywords_suggest_an_element(string path, string element)
    {
        var c = AssetClassifier.Classify(path);
        Assert.Equal(ImportProfiles.Environment, c.ProfileId);
        Assert.Equal(element, c.Element);
    }

    [Fact]
    public void Unknown_stem_is_a_generic_prop_with_a_reason()
    {
        var c = AssetClassifier.Classify("C:\\x\\Teapot.fbx");
        Assert.Equal(ImportProfiles.Prop, c.ProfileId);
        Assert.False(string.IsNullOrWhiteSpace(c.Reason));
        Assert.True(c.Supported);
    }

    [Theory]
    [InlineData("C:\\x\\Character.glb")]
    [InlineData("C:\\x\\Character.gltf")]
    [InlineData("C:\\x\\notes.txt")]
    public void Unsupported_extensions(string path)
    {
        var c = AssetClassifier.Classify(path);
        Assert.False(c.Supported);
        Assert.Contains("FBX", c.Reason);
    }

    [Fact]
    public void Glb_message_explains_the_decision() =>
        Assert.Contains("Blender", AssetClassifier.Classify("C:\\x\\a.glb").Reason);

    [Fact]
    public void Nearest_folder_hint_wins_over_the_name()
    {
        Assert.Equal(ImportProfiles.Environment, AssetClassifier.Classify("C:\\a\\Environment\\Idle.fbx").ProfileId);
        Assert.Equal(ImportProfiles.Prop, AssetClassifier.Classify("C:\\a\\Weapons\\Rifle.fbx").ProfileId);
        var anim = AssetClassifier.Classify("C:\\a\\Darius\\Animations\\Teapot.fbx");
        Assert.Equal(ImportProfiles.Locomotion, anim.ProfileId);
    }

    [Fact]
    public void Character_folder_is_a_weak_hint_that_animation_keywords_override()
    {
        Assert.Equal(ImportProfiles.Character, AssetClassifier.Classify("C:\\a\\Characters\\Kestrel.fbx").ProfileId);
        Assert.Equal(ImportProfiles.Combat, AssetClassifier.Classify("C:\\a\\Characters\\Fire.fbx").ProfileId);
    }

    [Fact]
    public void Folders_above_the_drop_folder_are_ignored()
    {
        var c = AssetClassifier.Classify("C:\\Props\\Pack\\Teapot.fbx", "C:\\Props\\Pack");
        Assert.Equal(ImportProfiles.Prop, c.ProfileId); // generic default, not because of 'Props'
        Assert.DoesNotContain("Props", c.Reason);
    }

    [Fact]
    public void Clip_name_is_a_pascal_case_stem() =>
        Assert.Equal("DariusWalkGun", AssetClassifier.Classify("C:\\x\\Darius Walk Gun.fbx").ClipName);
}
```

`tests/Core.Tests/AssetItemFactoryTests.cs`:

```csharp
using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class AssetItemFactoryTests
{
    [Theory]
    [InlineData("Darius Walk Gun", "Darius_Walk_Gun")]
    [InlineData("Café Prop", "Café_Prop")]
    [InlineData("a:b*c", "a_b_c")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("..", "Asset")]
    [InlineData("", "Asset")]
    public void Names_are_sanitised(string stem, string expected) => Assert.Equal(expected, AssetNames.FromFileStem(stem));

    [Fact]
    public void Sanitised_names_always_validate()
    {
        foreach (var stem in new[] { "a/b", "x?y", "tab\there", "...", "trail. " })
            Assert.Null(PathRules.ValidateAssetName(AssetNames.FromFileStem(stem)));
    }

    [Fact]
    public void Create_classifies_names_and_applies_the_profile()
    {
        var item = AssetItemFactory.Create("C:\\x\\Darius Walk Gun.fbx", null, new AppSettings { DefaultSharedAvatarPath = "Assets/A.fbx" });
        Assert.Equal(ImportProfiles.Locomotion, item.ProfileId);
        Assert.Equal("Darius_Walk_Gun", item.Import.name);
        Assert.Equal("DariusWalkGun", item.Import.animation.clipName);
        Assert.Equal("C:\\x\\Darius Walk Gun.fbx", item.OriginalPath);
        Assert.Equal(item.Id, item.Import.id);
        Assert.Null(item.UnsupportedReason);
    }

    [Fact]
    public void Create_sets_the_suggested_environment_element()
    {
        var item = AssetItemFactory.Create("C:\\x\\wall_corner.fbx", null, new AppSettings());
        Assert.Equal("WallCorner", item.Import.environment.element);
    }

    [Fact]
    public void Unsupported_files_are_kept_with_a_reason()
    {
        var item = AssetItemFactory.Create("C:\\x\\Hero.glb", null, new AppSettings());
        Assert.NotNull(item.UnsupportedReason);
    }

    [Fact]
    public void Two_items_get_distinct_ids()
    {
        var a = AssetItemFactory.Create("C:\\x\\a.fbx", null, new AppSettings());
        var b = AssetItemFactory.Create("C:\\x\\a.fbx", null, new AppSettings());
        Assert.NotEqual(a.Id, b.Id);
    }
}
```

- [ ] **Step 2: Run to verify failure** (`dotnet test …`): compile errors for the missing types.

- [ ] **Step 3: Implement.**

`src/Core/Settings/AppSettings.cs`:

```csharp
namespace Blackglass.AssetStudio;

/// <summary>Per-user settings (persisted by <see cref="SettingsStore"/>). Nothing here is ever committed.</summary>
public sealed class AppSettings
{
    public string ProjectPath { get; set; } = "";
    public string UnityExePath { get; set; } = "";
    /// <summary>Folder for run files (manifests, staged sources, logs). Empty = %LocalAppData%\BlackglassAssetStudio\runs.</summary>
    public string StagingFolder { get; set; } = "";
    public float DefaultTargetHeight { get; set; } = 1.85f;
    /// <summary>Project-relative path of the Humanoid model whose Avatar animation clips copy.</summary>
    public string DefaultSharedAvatarPath { get; set; } = "";
    public string LastDropFolder { get; set; } = "";
}
```

`src/Core/Profiles/ImportProfile.cs`:

```csharp
using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio;

public sealed record ImportProfile(string Id, string DisplayName, string Kind, string Category, string[] Extensions)
{
    public bool IsAnimation => Kind == ProfileIds.HumanoidAnimation;
}

/// <summary>The profile table and each profile's defaults. Import rules live here, not in the UI.</summary>
public static class ImportProfiles
{
    public const string Character = "HumanoidCharacter";
    public const string Locomotion = "AnimLocomotion";
    public const string Combat = "AnimCombat";
    public const string Reaction = "AnimReaction";
    public const string Death = "AnimDeath";
    public const string Prop = "GenericProp";
    public const string Environment = "EnvironmentModule";

    public const string DefaultThemePath = "Assets/_Project/Environment/Themes/CorporatePrototype/CorporatePrototype.asset";

    static readonly string[] FbxOnly = { ".fbx" };
    static readonly string[] FbxAndObj = { ".fbx", ".obj" };

    public static IReadOnlyList<ImportProfile> All { get; } = new[]
    {
        new ImportProfile(Character, "Humanoid Character", ProfileIds.HumanoidCharacter, "", FbxOnly),
        new ImportProfile(Locomotion, "Humanoid Animation — Locomotion", ProfileIds.HumanoidAnimation, AnimationCategories.Locomotion, FbxOnly),
        new ImportProfile(Combat, "Humanoid Animation — Combat", ProfileIds.HumanoidAnimation, AnimationCategories.Combat, FbxOnly),
        new ImportProfile(Reaction, "Humanoid Animation — Reaction", ProfileIds.HumanoidAnimation, AnimationCategories.Reaction, FbxOnly),
        new ImportProfile(Death, "Humanoid Animation — Death", ProfileIds.HumanoidAnimation, AnimationCategories.Death, FbxOnly),
        new ImportProfile(Prop, "Generic Prop", ProfileIds.GenericProp, "", FbxAndObj),
        new ImportProfile(Environment, "Environment Module", ProfileIds.EnvironmentModule, "", FbxAndObj),
    };

    public static ImportProfile Get(string id) =>
        All.FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException($"Unknown import profile '{id}'.", nameof(id));

    public static ImportProfile For(string kind, string category) =>
        All.FirstOrDefault(p => p.Kind == kind && (p.Category == category || !p.IsAnimation))
        ?? throw new ArgumentException($"No profile for kind '{kind}' and category '{category}'.");

    public static string DefaultDestination(ImportProfile profile, string name) => profile.Kind switch
    {
        ProfileIds.HumanoidCharacter => string.IsNullOrEmpty(name) ? "Assets/Art/Characters" : $"Assets/Art/Characters/{name}",
        ProfileIds.HumanoidAnimation => "Assets/Art/Animations",
        ProfileIds.GenericProp => "Assets/Art/Props",
        _ => "Assets/_Project/Environment/Modules",
    };

    /// <summary>Resets the item's profile-specific block to this profile's defaults and points it at the default destination.</summary>
    public static void Apply(ImportItem item, ImportProfile profile, AppSettings settings)
    {
        item.profile = profile.Kind;
        switch (profile.Kind)
        {
            case ProfileIds.HumanoidCharacter:
                item.character = new CharacterSettings { targetHeight = settings.DefaultTargetHeight };
                break;
            case ProfileIds.HumanoidAnimation:
                var a = new AnimationSettings
                {
                    category = profile.Category,
                    clipName = item.animation.clipName,
                    sharedAvatarPath = settings.DefaultSharedAvatarPath,
                };
                switch (profile.Category)
                {
                    case AnimationCategories.Locomotion: a.loop = LoopModes.Yes; a.bakeRotation = a.bakeHeight = a.bakePositionXZ = true; break;
                    case AnimationCategories.Death: a.loop = LoopModes.No; a.bakeHeight = a.bakePositionXZ = true; break;
                    default: a.loop = LoopModes.No; a.bakeHeight = true; break;
                }
                item.animation = a;
                break;
            case ProfileIds.GenericProp:
                item.prop = new PropSettings();
                break;
            default:
                item.environment = new EnvironmentSettings { element = item.environment.element, themePath = DefaultThemePath };
                break;
        }
        item.destinationFolder = DefaultDestination(profile, item.name);
    }
}
```

`src/Core/Items/AssetNames.cs`:

```csharp
using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio;

public static class AssetNames
{
    /// <summary>A valid asset name from a file stem: invalid characters and spaces become '_', never empty.</summary>
    public static string FromFileStem(string stem)
    {
        var chars = (stem ?? "").Trim().Select(c =>
            char.IsWhiteSpace(c) || char.IsControl(c) || "<>:\"|?*/\\".Contains(c) ? '_' : c).ToArray();
        var name = new string(chars).Trim('.', '_', ' ');
        // Keep interior underscores but drop the ones that came from edge characters; a name of only dots collapses to the fallback.
        if (name.Length == 0) return "Asset";
        return PathRules.ValidateAssetName(name) == null ? name : "Asset";
    }
}
```

(Check the expectations: `"a:b*c"` → `a_b_c`; `"  spaced  "` → trimmed first → `spaced`; `"Café Prop"` → `Café_Prop`; `".."` → trimmed to empty → `Asset`; `"trail. "` → `trail`; `"tab\there"` → `tab_here`.)

`src/Core/Items/AssetItem.cs`:

```csharp
using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio;

/// <summary>One entry in the list: the original file, its classification and the manifest item the user edits.</summary>
public sealed class AssetItem
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string OriginalPath { get; init; } = "";
    public string ProfileId { get; set; } = ImportProfiles.Prop;
    public string ClassificationReason { get; set; } = "";
    public string? UnsupportedReason { get; init; }
    public ImportItem Import { get; init; } = new();
}
```

`src/Core/Classification/AssetClassifier.cs`:

```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace Blackglass.AssetStudio;

public sealed record Classification(string ProfileId, string Reason, string ClipName, string Element, bool Supported);

/// <summary>A suggestion only (folder context, then file name, then extension); the user can always override the profile.</summary>
public static class AssetClassifier
{
    static readonly string[] Death = { "death", "die", "dies", "dying", "dead" };
    static readonly string[] Reaction = { "hit", "react", "reaction", "flinch", "stagger" };
    static readonly string[] Combat = { "fire", "firing", "shoot", "attack", "reload", "aim", "melee", "punch", "kick", "throw" };
    static readonly string[] Locomotion = { "idle", "walk", "walking", "run", "running", "sprint", "jog", "crouch", "strafe" };

    static readonly string[] AnimFolders = { "animations", "animation", "anims", "clips" };
    static readonly string[] EnvFolders = { "environment", "environments", "modules", "tiles" };
    static readonly string[] PropFolders = { "props", "prop", "weapons", "weapon" };
    static readonly string[] CharFolders = { "characters", "character", "operatives", "units" };

    public static Classification Classify(string path, string? dropFolder = null)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(path);
        var tokens = Tokenize(stem);
        var clip = ClipNameFrom(tokens);

        if (ext is ".glb" or ".gltf")
            return Unsupported($"{ext.ToUpperInvariant().TrimStart('.')} is not supported in v0.1 (Unity has no built-in importer for it). Convert it to FBX in Blender.", clip);
        if (ext is not (".fbx" or ".obj"))
            return Unsupported($"'{ext}' files are not supported; use FBX (or OBJ for props and environment modules).", clip);

        var folder = NearestFolderHint(path, dropFolder);
        var animationCategory = AnimationCategory(tokens);
        var element = EnvironmentElementFor(tokens);

        switch (folder.Kind)
        {
            case "env":
                return Ok(ImportProfiles.Environment, $"In an '{folder.Name}' folder.", clip, element);
            case "prop":
                return Ok(ImportProfiles.Prop, $"In a '{folder.Name}' folder.", clip, "");
            case "anim":
                return Ok(animationCategory ?? ImportProfiles.Locomotion,
                    animationCategory != null ? $"In an '{folder.Name}' folder; the name suggests it." : $"In an '{folder.Name}' folder; category unknown, assumed Locomotion.", clip, "");
        }

        if (animationCategory != null)
            return Ok(animationCategory, "The file name looks like an animation.", clip, "");
        if (folder.Kind == "char")
            return Ok(ImportProfiles.Character, $"In a '{folder.Name}' folder.", clip, "");
        if (element.Length > 0)
            return Ok(ImportProfiles.Environment, $"The file name looks like an environment piece ({element}).", clip, element);
        return Ok(ImportProfiles.Prop, "No hint in the name or folders; defaulting to Generic Prop.", clip, "");
    }

    static Classification Ok(string profile, string reason, string clip, string element) => new(profile, reason, clip, element, true);

    static Classification Unsupported(string reason, string clip) => new(ImportProfiles.Prop, reason, clip, "", false);

    static (string Kind, string Name) NearestFolderHint(string path, string? dropFolder)
    {
        var dir = Path.GetDirectoryName(path);
        for (var depth = 0; depth < 4 && !string.IsNullOrEmpty(dir); depth++)
        {
            var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
            var lower = name.ToLowerInvariant();
            if (AnimFolders.Contains(lower)) return ("anim", name);
            if (EnvFolders.Contains(lower)) return ("env", name);
            if (PropFolders.Contains(lower)) return ("prop", name);
            if (CharFolders.Contains(lower)) return ("char", name);
            if (dropFolder != null && SamePath(dir, dropFolder)) break;
            dir = Path.GetDirectoryName(dir);
        }
        return ("", "");
    }

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    static string? AnimationCategory(string[] tokens)
    {
        if (tokens.Any(t => Death.Contains(t))) return ImportProfiles.Death;
        if (tokens.Any(t => Reaction.Contains(t))) return ImportProfiles.Reaction;
        if (tokens.Any(t => Combat.Contains(t))) return ImportProfiles.Combat;
        if (tokens.Any(t => Locomotion.Contains(t))) return ImportProfiles.Locomotion;
        return null;
    }

    static string EnvironmentElementFor(string[] t)
    {
        bool Has(string w) => t.Contains(w);
        if (Has("terminal")) return "Terminal";
        if (Has("light")) return "LightFixture";
        if (Has("door")) return "DoorFrame";
        if (Has("pillar") || Has("column")) return "Pillar";
        if (Has("floor")) return "Floor";
        if (Has("crate")) return "Crate";
        if (Has("cover")) return Has("long") ? "LowCoverLong" : "LowCover";
        if (Has("lowcover")) return Has("long") ? "LowCoverLong" : "LowCover";
        if (Has("wall"))
        {
            if (Has("corner")) return "WallCorner";
            if (Has("junction")) return "WallJunction";
            if (Has("end")) return "WallEnd";
            return "WallStraight";
        }
        return "";
    }

    /// <summary>Lower-case words: camelCase and non-alphanumerics split, so "LowCover" gives "low","cover" but "lowcover" is also tried via the raw stem.</summary>
    static string[] Tokenize(string stem)
    {
        var spaced = Regex.Replace(stem, "(?<=[a-z0-9])(?=[A-Z])", " ");
        var words = Regex.Split(spaced, "[^\\p{L}\\p{N}]+").Where(w => w.Length > 0).Select(w => w.ToLowerInvariant()).ToList();
        words.Add(new string(stem.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()); // joined stem, e.g. "lowcover"
        return words.ToArray();
    }

    static string ClipNameFrom(string[] tokens)
    {
        // tokens' last element is the joined stem; the rest are the words.
        var sb = new StringBuilder();
        foreach (var w in tokens.Take(tokens.Length - 1))
            sb.Append(char.ToUpperInvariant(w[0])).Append(w, 1, w.Length - 1);
        return sb.Length == 0 ? "Clip" : sb.ToString();
    }
}
```

Note on `Low Cover` tokens: "low","cover" works via `Has("cover")`; "LowCover" splits to "low","cover" via camelCase. The joined token handles "lowcover" typed in lower case. Tokens such as "hit" match whole words only, so "White" does not match "hit".

`src/Core/Items/AssetItemFactory.cs`:

```csharp
using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio;

public static class AssetItemFactory
{
    public static AssetItem Create(string path, string? dropFolder, AppSettings settings)
    {
        var c = AssetClassifier.Classify(path, dropFolder);
        var profile = ImportProfiles.Get(c.ProfileId);
        var name = AssetNames.FromFileStem(Path.GetFileNameWithoutExtension(path));

        var item = new AssetItem
        {
            OriginalPath = path,
            ProfileId = profile.Id,
            ClassificationReason = c.Reason,
            UnsupportedReason = c.Supported ? null : c.Reason,
            Import = new ImportItem { name = name, sourcePath = path },
        };
        item.Import.id = item.Id;
        item.Import.animation.clipName = c.ClipName;
        item.Import.environment.element = c.Element;
        ImportProfiles.Apply(item.Import, profile, settings);
        return item;
    }
}
```

- [ ] **Step 4: Run to verify pass.** `dotnet test …` all green. Fix any classifier expectation mismatch by reading the failing case (the keyword tables and precedence above are the contract); do not weaken a test without recording why.

- [ ] **Step 5: Commit.**

```bash
git add Tools/BlackglassAssetStudio
git commit -m "Asset Studio: import profiles, asset names, file classification and item factory

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

## Task 3: Settings, project and Unity locators

**Files:**
- Create: `src/Core/Settings/SettingsStore.cs`, `src/Core/Settings/AppPaths.cs`, `src/Core/Locating/ProjectLocator.cs`, `src/Core/Locating/UnityLocator.cs`
- Test: `tests/Core.Tests/TestSupport/TempDir.cs`, `SettingsTests.cs`, `LocatorTests.cs`

**Interfaces:**
- Produces: `SettingsStore(string path)`, `.Load() -> AppSettings` (never throws; corrupt/missing gives defaults), `.Save(AppSettings)`; `AppPaths.SettingsPath`, `AppPaths.DefaultRunsRoot`, `AppPaths.RunsRoot(AppSettings)`; `ProjectLocator.IsProject(string?)`, `.FindFrom(string startDir) -> string?`, `.ReadEditorVersion(string project) -> string?`; `UnityLocator.FindExecutable(string version, IEnumerable<string> hubEditorRoots) -> string?`, `.DefaultHubRoots()`, `.Resolve(AppSettings, string projectPath) -> UnityResolution(string? Path, string Message)`.

- [ ] **Step 1: Tests.**

`tests/Core.Tests/TestSupport/TempDir.cs`:

```csharp
namespace Blackglass.AssetStudio.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bgas-" + Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public string Write(string relative, string content = "x")
    {
        var full = Combine(relative.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Makes a folder that looks like a Unity project (Assets and ProjectSettings/ProjectVersion.txt).</summary>
    public string MakeProject(string version = "6000.3.25f1")
    {
        Directory.CreateDirectory(Combine("Assets"));
        Write("ProjectSettings/ProjectVersion.txt", $"m_EditorVersion: {version}\nm_EditorVersionWithRevision: {version} (abc)\n");
        return Path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }
}
```

`tests/Core.Tests/SettingsTests.cs`:

```csharp
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class SettingsTests
{
    [Fact]
    public void Missing_file_gives_defaults()
    {
        using var t = new TempDir();
        var s = new SettingsStore(t.Combine("settings.json")).Load();
        Assert.Equal(1.85f, s.DefaultTargetHeight);
        Assert.Equal("", s.ProjectPath);
    }

    [Fact]
    public void Save_then_load_round_trips_including_spaces_and_other_drives()
    {
        using var t = new TempDir();
        var store = new SettingsStore(t.Combine("sub", "settings.json"));
        store.Save(new AppSettings { ProjectPath = "D:\\My Games\\Project Blackglass", DefaultTargetHeight = 1.9f, DefaultSharedAvatarPath = "Assets/A B.fbx" });
        var back = store.Load();
        Assert.Equal("D:\\My Games\\Project Blackglass", back.ProjectPath);
        Assert.Equal(1.9f, back.DefaultTargetHeight);
        Assert.Equal("Assets/A B.fbx", back.DefaultSharedAvatarPath);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_without_throwing()
    {
        using var t = new TempDir();
        var path = t.Write("settings.json", "{ this is not json");
        var s = new SettingsStore(path).Load();
        Assert.Equal(1.85f, s.DefaultTargetHeight);
    }

    [Fact]
    public void Non_positive_target_height_in_file_is_replaced_by_the_default()
    {
        using var t = new TempDir();
        var path = t.Write("settings.json", "{\"DefaultTargetHeight\": -3}");
        Assert.Equal(1.85f, new SettingsStore(path).Load().DefaultTargetHeight);
    }

    [Fact]
    public void Runs_root_uses_the_staging_folder_when_set()
    {
        Assert.Equal("D:\\Stage", AppPaths.RunsRoot(new AppSettings { StagingFolder = "D:\\Stage" }));
        Assert.Equal(AppPaths.DefaultRunsRoot, AppPaths.RunsRoot(new AppSettings()));
    }
}
```

`tests/Core.Tests/LocatorTests.cs`:

```csharp
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class LocatorTests
{
    [Fact]
    public void Project_detection_needs_assets_and_version_file()
    {
        using var t = new TempDir();
        Assert.False(ProjectLocator.IsProject(t.Path));
        t.MakeProject();
        Assert.True(ProjectLocator.IsProject(t.Path));
        Assert.False(ProjectLocator.IsProject(null));
        Assert.False(ProjectLocator.IsProject(""));
    }

    [Fact]
    public void FindFrom_walks_up_to_the_project()
    {
        using var t = new TempDir();
        t.MakeProject();
        var deep = t.Combine("Tools", "X", "bin");
        Directory.CreateDirectory(deep);
        Assert.Equal(t.Path, ProjectLocator.FindFrom(deep));
    }

    [Fact]
    public void FindFrom_returns_null_outside_a_project()
    {
        using var t = new TempDir();
        Assert.Null(ProjectLocator.FindFrom(t.Path));
    }

    [Fact]
    public void Editor_version_is_read_from_the_project()
    {
        using var t = new TempDir();
        t.MakeProject("6000.3.25f1");
        Assert.Equal("6000.3.25f1", ProjectLocator.ReadEditorVersion(t.Path));
    }

    [Fact]
    public void Unity_is_found_under_the_hub_root_for_the_projects_version_only()
    {
        using var hub = new TempDir();
        hub.Write("6000.3.25f1/Editor/Unity.exe");
        hub.Write("2022.3.46f1/Editor/Unity.exe");
        var found = UnityLocator.FindExecutable("6000.3.25f1", new[] { hub.Path });
        Assert.EndsWith(Path.Combine("6000.3.25f1", "Editor", "Unity.exe"), found);
        Assert.Null(UnityLocator.FindExecutable("6000.9.9f1", new[] { hub.Path }));
    }

    [Fact]
    public void Resolve_prefers_a_valid_explicit_path()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var exe = t.Write("my unity/Unity.exe");
        var r = UnityLocator.Resolve(new AppSettings { UnityExePath = exe }, project);
        Assert.Equal(exe, r.Path);
    }

    [Fact]
    public void Resolve_explains_a_missing_editor_and_names_the_version()
    {
        using var t = new TempDir();
        var project = t.MakeProject("6000.1.1f1");
        var r = UnityLocator.Resolve(new AppSettings(), project, new[] { t.Combine("nohub") });
        Assert.Null(r.Path);
        Assert.Contains("6000.1.1f1", r.Message);
    }

    [Fact]
    public void Resolve_rejects_a_bad_explicit_path_instead_of_silently_using_another()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var r = UnityLocator.Resolve(new AppSettings { UnityExePath = t.Combine("missing.exe") }, project, new[] { t.Combine("nohub") });
        Assert.Null(r.Path);
        Assert.Contains("missing.exe", r.Message);
    }
}
```

- [ ] **Step 2: Run to verify failure** (missing types).

- [ ] **Step 3: Implement.**

`src/Core/Settings/AppPaths.cs`:

```csharp
namespace Blackglass.AssetStudio;

public static class AppPaths
{
    public static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlackglassAssetStudio", "settings.json");

    public static string DefaultRunsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlackglassAssetStudio", "runs");

    public static string RunsRoot(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.StagingFolder) ? DefaultRunsRoot : settings.StagingFolder;
}
```

`src/Core/Settings/SettingsStore.cs`:

```csharp
using System.Text.Json;

namespace Blackglass.AssetStudio;

public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    readonly string path;

    public SettingsStore(string path) => this.path = path;

    /// <summary>Never throws: a missing or unreadable file gives the defaults.</summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
            if (!(s.DefaultTargetHeight > 0f) || float.IsInfinity(s.DefaultTargetHeight)) s.DefaultTargetHeight = 1.85f;
            return s;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
    }
}
```

`src/Core/Locating/ProjectLocator.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Blackglass.AssetStudio;

public static class ProjectLocator
{
    public static bool IsProject(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && Directory.Exists(Path.Combine(path, "Assets"))
        && File.Exists(Path.Combine(path, "ProjectSettings", "ProjectVersion.txt"));

    /// <summary>The nearest folder at or above <paramref name="startDir"/> that is a Unity project, or null.</summary>
    public static string? FindFrom(string startDir)
    {
        for (var d = new DirectoryInfo(startDir); d != null; d = d.Parent)
            if (IsProject(d.FullName)) return d.FullName;
        return null;
    }

    public static string? ReadEditorVersion(string projectPath)
    {
        var file = Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(file)) return null;
        var m = Regex.Match(File.ReadAllText(file), @"^m_EditorVersion:\s*(\S+)", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }
}
```

`src/Core/Locating/UnityLocator.cs`:

```csharp
using System.Text.Json;

namespace Blackglass.AssetStudio;

public sealed record UnityResolution(string? Path, string Message);

/// <summary>Finds the Unity Editor matching the project's version. Never substitutes a different version.</summary>
public static class UnityLocator
{
    public static string? FindExecutable(string version, IEnumerable<string> hubEditorRoots)
    {
        foreach (var root in hubEditorRoots)
        {
            var exe = Path.Combine(root, version, "Editor", "Unity.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    public static IEnumerable<string> DefaultHubRoots()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "Unity", "Hub", "Editor");

        var secondary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub", "secondaryInstallPath.json");
        string? extra = null;
        try
        {
            if (File.Exists(secondary))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(secondary));
                if (doc.RootElement.ValueKind == JsonValueKind.String) extra = doc.RootElement.GetString();
            }
        }
        catch (Exception e) when (e is JsonException or IOException) { }
        if (!string.IsNullOrWhiteSpace(extra)) yield return extra;
    }

    public static UnityResolution Resolve(AppSettings settings, string projectPath, IEnumerable<string>? hubRoots = null)
    {
        if (!string.IsNullOrWhiteSpace(settings.UnityExePath))
            return File.Exists(settings.UnityExePath)
                ? new UnityResolution(settings.UnityExePath, "Using the Unity executable from settings.")
                : new UnityResolution(null, $"The Unity executable in settings does not exist: {settings.UnityExePath}");

        var version = ProjectLocator.ReadEditorVersion(projectPath);
        if (version == null)
            return new UnityResolution(null, "Could not read the Unity version from ProjectSettings/ProjectVersion.txt. Set the Unity executable in Settings.");
        var exe = FindExecutable(version, hubRoots ?? DefaultHubRoots());
        return exe != null
            ? new UnityResolution(exe, $"Found Unity {version}.")
            : new UnityResolution(null, $"Unity {version} (the version this project uses) was not found in the usual Unity Hub folders. Install it, or set the Unity executable in Settings.");
    }
}
```

- [ ] **Step 4: Run to verify pass.** `dotnet test …`.

- [ ] **Step 5: Commit** (`git add Tools/BlackglassAssetStudio`; message "Asset Studio: settings store, project and Unity locators").

---

## Task 4: Local validation, ownership probe and duplicate-target detection

**Files:**
- Create: `src/Core/Validation/OwnershipProbe.cs`, `src/Core/Validation/ItemValidator.cs`
- Test: `tests/Core.Tests/ValidatorTests.cs`

**Interfaces:**
- Consumes: `AssetItem`, `ImportProfiles`, `AssetNaming`, `PathRules`, `HeightScale`, `ProjectLocator`.
- Produces: `Severity { Info, Warning, Error }`; `ValidationMessage(Severity Severity, string Text)`; `OwnershipProbe.IsStudioOwned(string metaPath) -> bool`; `ItemValidator(string projectPath)` with `Validate(AssetItem) -> List<ValidationMessage>` and `ValidateAll(IReadOnlyList<AssetItem>) -> Dictionary<string, List<ValidationMessage>>` (key = `AssetItem.Id`; includes duplicate-target errors).

- [ ] **Step 1: Tests** `tests/Core.Tests/ValidatorTests.cs`:

```csharp
using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class ValidatorTests
{
    static AssetItem Make(string profile, TempDir t, string file = "Kestrel.fbx", AppSettings? settings = null)
    {
        var src = t.Write("src/" + file);
        var item = AssetItemFactory.Create(src, null, settings ?? new AppSettings());
        ImportProfiles.Apply(item.Import, ImportProfiles.Get(profile), settings ?? new AppSettings());
        item.ProfileId = profile;
        return item;
    }

    static List<ValidationMessage> Errors(List<ValidationMessage> m) => m.Where(x => x.Severity == Severity.Error).ToList();

    [Fact]
    public void A_clean_character_has_no_errors()
    {
        using var t = new TempDir();
        var v = new ItemValidator(t.MakeProject());
        Assert.Empty(Errors(v.Validate(Make(ImportProfiles.Character, t))));
    }

    [Fact]
    public void Missing_source_is_an_error()
    {
        using var t = new TempDir();
        var item = Make(ImportProfiles.Character, t);
        File.Delete(item.OriginalPath);
        Assert.Contains(Errors(new ItemValidator(t.MakeProject()).Validate(item)), m => m.Text.Contains("does not exist"));
    }

    [Fact]
    public void Unsupported_files_are_errors_with_the_classifier_reason()
    {
        using var t = new TempDir();
        var src = t.Write("src/Hero.glb");
        var item = AssetItemFactory.Create(src, null, new AppSettings());
        Assert.Contains(Errors(new ItemValidator(t.MakeProject()).Validate(item)), m => m.Text.Contains("Blender"));
    }

    [Fact]
    public void Obj_is_refused_for_characters()
    {
        using var t = new TempDir();
        var src = t.Write("src/body.obj");
        var item = AssetItemFactory.Create(src, null, new AppSettings());
        ImportProfiles.Apply(item.Import, ImportProfiles.Get(ImportProfiles.Character), new AppSettings());
        item.ProfileId = ImportProfiles.Character;
        Assert.Contains(Errors(new ItemValidator(t.MakeProject()).Validate(item)), m => m.Text.Contains(".obj"));
    }

    [Fact]
    public void Bad_destination_and_name_are_errors()
    {
        using var t = new TempDir();
        var item = Make(ImportProfiles.Prop, t);
        item.Import.destinationFolder = "Packages/x";
        item.Import.name = "a/b";
        var errors = Errors(new ItemValidator(t.MakeProject()).Validate(item));
        Assert.Contains(errors, m => m.Text.Contains("Assets"));
        Assert.Contains(errors, m => m.Text.Contains("name"));
    }

    [Fact]
    public void Character_target_height_must_be_positive_only_when_normalising()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var item = Make(ImportProfiles.Character, t);
        item.Import.character.targetHeight = 0;
        Assert.Contains(Errors(new ItemValidator(project).Validate(item)), m => m.Text.Contains("target height", StringComparison.OrdinalIgnoreCase));
        item.Import.character.normalizeHeight = false;
        Assert.Empty(Errors(new ItemValidator(project).Validate(item)));
    }

    [Fact]
    public void Humanoid_character_requires_an_avatar()
    {
        using var t = new TempDir();
        var item = Make(ImportProfiles.Character, t);
        item.Import.character.createAvatar = false;
        Assert.Contains(Errors(new ItemValidator(t.MakeProject()).Validate(item)), m => m.Text.Contains("Avatar"));
    }

    [Fact]
    public void Animation_needs_an_existing_shared_avatar()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var item = Make(ImportProfiles.Locomotion, t, "Walk.fbx");
        Assert.Contains(Errors(new ItemValidator(project).Validate(item)), m => m.Text.Contains("shared Avatar"));
        item.Import.animation.sharedAvatarPath = "Assets/Missing.fbx";
        Assert.Contains(Errors(new ItemValidator(project).Validate(item)), m => m.Text.Contains("Missing.fbx"));
        t.Write("Assets/Real.fbx");
        item.Import.animation.sharedAvatarPath = "Assets/Real.fbx";
        Assert.Empty(Errors(new ItemValidator(project).Validate(item)));
    }

    [Fact]
    public void Animation_needs_a_clip_name()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        t.Write("Assets/Real.fbx");
        var item = Make(ImportProfiles.Locomotion, t, "Walk.fbx");
        item.Import.animation.sharedAvatarPath = "Assets/Real.fbx";
        item.Import.animation.clipName = " ";
        Assert.Contains(Errors(new ItemValidator(project).Validate(item)), m => m.Text.Contains("clip name"));
    }

    [Fact]
    public void Environment_needs_a_known_element_and_a_theme_when_registering()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var item = Make(ImportProfiles.Environment, t, "thing.fbx");
        item.Import.environment.element = "";
        Assert.Contains(Errors(new ItemValidator(project).Validate(item)), m => m.Text.Contains("element"));
        item.Import.environment.element = "WallStraight";
        Assert.Empty(Errors(new ItemValidator(project).Validate(item)));
        item.Import.environment.themeMode = ThemeModes.Append;
        item.Import.environment.themePath = "Assets/Nope.asset";
        Assert.Contains(Errors(new ItemValidator(project).Validate(item)), m => m.Text.Contains("theme"));
        item.Import.environment.themeMode = ThemeModes.Replace;
        t.Write("Assets/Theme.asset");
        item.Import.environment.themePath = "Assets/Theme.asset";
        item.Import.environment.replaceIndex = -1;
        Assert.Contains(Errors(new ItemValidator(project).Validate(item)), m => m.Text.Contains("index"));
    }

    [Fact]
    public void Environment_scale_other_than_one_is_a_warning_not_a_silent_change()
    {
        using var t = new TempDir();
        var item = Make(ImportProfiles.Environment, t, "wall.fbx");
        item.Import.environment.scale = 2f;
        Assert.Contains(new ItemValidator(t.MakeProject()).Validate(item), m => m.Severity == Severity.Warning && m.Text.Contains("scale"));
    }

    [Fact]
    public void Existing_unowned_asset_is_an_error_until_overwrite_is_allowed()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var item = Make(ImportProfiles.Prop, t, "Crate.fbx");
        t.Write("Assets/Art/Props/Crate.fbx");
        Assert.Contains(Errors(new ItemValidator(project).Validate(item)), m => m.Text.Contains("Allow overwrite"));
        item.Import.allowOverwrite = true;
        var after = new ItemValidator(project).Validate(item);
        Assert.Empty(Errors(after));
        Assert.Contains(after, m => m.Severity == Severity.Warning && m.Text.Contains("replace"));
    }

    [Fact]
    public void Existing_studio_owned_asset_is_an_update_not_a_conflict()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var item = Make(ImportProfiles.Prop, t, "Crate.fbx");
        t.Write("Assets/Art/Props/Crate.fbx");
        t.Write("Assets/Art/Props/Crate.fbx.meta", "fileFormatVersion: 2\nlabels:\n- BlackglassStudio\n");
        var m = new ItemValidator(project).Validate(item);
        Assert.Empty(Errors(m));
        Assert.Contains(m, x => x.Severity == Severity.Info && x.Text.Contains("update"));
    }

    [Fact]
    public void Two_items_with_the_same_target_are_both_rejected()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var a = Make(ImportProfiles.Prop, t, "Crate.fbx");
        var srcB = t.Write("src2/Crate.fbx");
        var b = AssetItemFactory.Create(srcB, null, new AppSettings());
        ImportProfiles.Apply(b.Import, ImportProfiles.Get(ImportProfiles.Prop), new AppSettings());
        b.ProfileId = ImportProfiles.Prop;
        var result = new ItemValidator(project).ValidateAll(new[] { a, b });
        Assert.Contains(result[a.Id], m => m.Severity == Severity.Error && m.Text.Contains("same asset"));
        Assert.Contains(result[b.Id], m => m.Severity == Severity.Error && m.Text.Contains("same asset"));
    }

    [Fact]
    public void Targets_that_differ_only_in_case_collide()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var a = Make(ImportProfiles.Prop, t, "crate.fbx");
        var b = Make(ImportProfiles.Prop, t, "CRATE2.fbx");
        b.Import.name = "CRATE";
        a.Import.name = "crate";
        Assert.Contains(new ItemValidator(project).ValidateAll(new[] { a, b })[a.Id], m => m.Text.Contains("same asset"));
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement.**

`src/Core/Validation/OwnershipProbe.cs`:

```csharp
using System.Text.RegularExpressions;
using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio;

/// <summary>Asset labels are stored in the asset's .meta file ("labels:" list), so ownership can be read without Unity.</summary>
public static class OwnershipProbe
{
    public static bool IsStudioOwned(string metaPath)
    {
        if (!File.Exists(metaPath)) return false;
        return Regex.IsMatch(File.ReadAllText(metaPath), @"^- " + Regex.Escape(ContractInfo.OwnershipLabel) + @"\s*$", RegexOptions.Multiline);
    }
}
```

`src/Core/Validation/ItemValidator.cs`:

```csharp
using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio;

public enum Severity { Info, Warning, Error }

public sealed record ValidationMessage(Severity Severity, string Text);

/// <summary>Instant local checks (no Unity). Measured values (height, bounds, Avatar, clips) come from the import itself.</summary>
public sealed class ItemValidator
{
    readonly string project;

    public ItemValidator(string projectPath) => project = projectPath;

    public List<ValidationMessage> Validate(AssetItem item)
    {
        var m = new List<ValidationMessage>();
        void Error(string text) => m.Add(new(Severity.Error, text));
        void Warn(string text) => m.Add(new(Severity.Warning, text));
        var i = item.Import;
        var profile = ImportProfiles.Get(item.ProfileId);

        if (item.UnsupportedReason != null) { Error(item.UnsupportedReason); return m; }
        if (!File.Exists(item.OriginalPath)) { Error($"The source file does not exist: {item.OriginalPath}"); return m; }

        var ext = Path.GetExtension(item.OriginalPath).ToLowerInvariant();
        if (!profile.Extensions.Contains(ext))
            Error($"{profile.DisplayName} cannot import '{ext}' files (accepts {string.Join(", ", profile.Extensions)}).");

        var nameError = PathRules.ValidateAssetName(i.name);
        if (nameError != null) Error(nameError);
        var destinationError = PathRules.ValidateDestination(i.destinationFolder);
        if (destinationError != null) Error(destinationError);

        switch (profile.Kind)
        {
            case ProfileIds.HumanoidCharacter:
                var c = i.character;
                if (c.normalizeHeight && c.scaleOverride == 0 && !(c.targetHeight > 0)) Error("The target height must be greater than 0.");
                if (c.scaleOverride < 0 || float.IsNaN(c.scaleOverride)) Error("The manual scale must be positive (or 0 for none).");
                if (c.rigHumanoid && !c.createAvatar) Error("A Humanoid rig needs an Avatar: tick Create Avatar or untick Humanoid.");
                break;
            case ProfileIds.HumanoidAnimation:
                var a = i.animation;
                if (string.IsNullOrWhiteSpace(a.clipName)) Error("Enter a clip name.");
                if (string.IsNullOrWhiteSpace(a.sharedAvatarPath))
                    Error("Pick a shared Avatar: a Humanoid model in the project that this clip should target (Settings > Default shared Avatar).");
                else if (!File.Exists(ProjectFile(a.sharedAvatarPath)))
                    Error($"The shared Avatar model does not exist in the project: {a.sharedAvatarPath}");
                break;
            case ProfileIds.GenericProp:
                if (!(i.prop.scale > 0)) Error("The prop scale must be greater than 0.");
                break;
            case ProfileIds.EnvironmentModule:
                var e = i.environment;
                if (!EnvironmentElements.All.Contains(e.element)) Error("Choose the environment element (wall, floor, cover, ...) this module is.");
                if (!(e.scale > 0)) Error("The module scale must be greater than 0.");
                else if (Math.Abs(e.scale - 1f) > 1e-6f) Warn($"A scale of {e.scale:0.###} will be applied to this module. Environment modules are never rescaled automatically; check it is what you want.");
                if (e.themeMode is ThemeModes.Append or ThemeModes.Replace)
                {
                    if (string.IsNullOrWhiteSpace(e.themePath) || !File.Exists(ProjectFile(e.themePath)))
                        Error($"The theme asset does not exist: {e.themePath}");
                    if (e.themeMode == ThemeModes.Replace && e.replaceIndex < 0) Error("The variant index to replace must be 0 or greater.");
                }
                break;
        }

        if (destinationError == null && nameError == null)
            foreach (var target in Targets(i))
                AddConflict(m, i, target);
        return m;
    }

    public Dictionary<string, List<ValidationMessage>> ValidateAll(IReadOnlyList<AssetItem> items)
    {
        var result = items.ToDictionary(x => x.Id, Validate);
        var owners = new Dictionary<string, List<AssetItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (PathRules.ValidateAssetName(item.Import.name) != null || PathRules.ValidateDestination(item.Import.destinationFolder) != null) continue;
            foreach (var target in Targets(item.Import))
            {
                if (!owners.TryGetValue(target, out var list)) owners[target] = list = new List<AssetItem>();
                list.Add(item);
            }
        }
        foreach (var (target, list) in owners.Where(p => p.Value.Count > 1))
            foreach (var item in list)
                result[item.Id].Add(new(Severity.Error, $"Another entry in this list targets the same asset ({target}). Rename one."));
        return result;
    }

    static IEnumerable<string> Targets(ImportItem i)
    {
        yield return AssetNaming.ModelPath(i);
        var prefab = AssetNaming.PrefabPath(i);
        if (prefab != null) yield return prefab;
    }

    void AddConflict(List<ValidationMessage> m, ImportItem i, string assetPath)
    {
        var full = ProjectFile(assetPath);
        if (!File.Exists(full)) return;
        if (OwnershipProbe.IsStudioOwned(full + ".meta"))
            m.Add(new(Severity.Info, $"{assetPath} exists and was created by Asset Studio: it will be updated."));
        else if (i.allowOverwrite)
            m.Add(new(Severity.Warning, $"{assetPath} exists and was not created by Asset Studio: it will be replaced."));
        else
            m.Add(new(Severity.Error, $"{assetPath} already exists and was not created by Asset Studio. Tick Allow overwrite to replace it."));
    }

    string ProjectFile(string assetPath) => Path.Combine(project, assetPath.Replace('/', Path.DirectorySeparatorChar));
}
```

- [ ] **Step 4: Run to verify pass.** Fix test fixtures only if they contradict the spec (e.g. an item built through `Make` for a profile whose default destination is already used).

- [ ] **Step 5: Commit** ("Asset Studio: local item validation, ownership probe, duplicate-target detection").

---

## Task 5: Staging, manifest builder, git snapshot, log excerpt and result formatter

**Files:**
- Create: `src/Core/Run/RunStaging.cs`, `ManifestBuilder.cs`, `GitSnapshot.cs`, `LogExcerpt.cs`, `ResultFormatter.cs`
- Test: `tests/Core.Tests/StagingTests.cs`, `ManifestBuilderTests.cs`, `GitSnapshotTests.cs`, `LogExcerptTests.cs`, `ResultFormatterTests.cs`

**Interfaces:**
- Consumes: `AssetItem`, `ManifestJson`, `ImportProfiles`, `PathRules`.
- Produces:
  - `StagedRun(string RunId, string RunDir, string ManifestPath, string ResultPath, string LogPath, IReadOnlyDictionary<string,string> StagedPaths)`; `RunStaging(string runsRoot)`, `.Create(IReadOnlyList<AssetItem>, DateTime? now = null) -> StagedRun`, `RunStaging.KeepRuns = 10`.
  - `ManifestBuilder.Build(StagedRun, IReadOnlyList<AssetItem>) -> ImportManifest`.
  - `GitSnapshot.TryCapture(string projectPath) -> IReadOnlyDictionary<string,string>?`, `.Parse(string porcelainZ, string repoRoot)`, `.Diff(before, after) -> IReadOnlyList<string>`.
  - `LogExcerpt.ErrorLines(string logPath, int max = 8) -> IReadOnlyList<string>`, `.LastLine(string logPath) -> string`.
  - `ResultFormatter.Format(ImportResult, Func<string,string> nameOf, IReadOnlyList<string> changedFiles, string logPath) -> string`.

- [ ] **Step 1: Tests.**

`tests/Core.Tests/StagingTests.cs`:

```csharp
using System.Security.Cryptography;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class StagingTests
{
    static AssetItem Item(TempDir t, string relative, string content = "fbx-bytes")
    {
        var path = t.Write(relative, content);
        return AssetItemFactory.Create(path, null, new AppSettings());
    }

    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Stages_files_with_spaces_and_non_ascii_names_and_leaves_the_source_untouched()
    {
        using var t = new TempDir();
        var item = Item(t, "src/Darius Walk Café.fbx");
        var before = Hash(item.OriginalPath);
        var stamp = File.GetLastWriteTimeUtc(item.OriginalPath);

        var run = new RunStaging(t.Combine("runs")).Create(new[] { item });

        Assert.True(File.Exists(run.StagedPaths[item.Id]));
        Assert.Equal(before, Hash(run.StagedPaths[item.Id]));
        Assert.Equal(before, Hash(item.OriginalPath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(item.OriginalPath));
        Assert.EndsWith("Darius Walk Café.fbx", run.StagedPaths[item.Id]);
    }

    [Fact]
    public void Same_file_name_from_two_folders_gets_separate_staging_folders()
    {
        using var t = new TempDir();
        var a = Item(t, "one/Walk.fbx", "A");
        var b = Item(t, "two/Walk.fbx", "B");
        var run = new RunStaging(t.Combine("runs")).Create(new[] { a, b });
        Assert.NotEqual(run.StagedPaths[a.Id], run.StagedPaths[b.Id]);
        Assert.Equal("A", File.ReadAllText(run.StagedPaths[a.Id]));
        Assert.Equal("B", File.ReadAllText(run.StagedPaths[b.Id]));
    }

    [Fact]
    public void Sibling_fbm_texture_folder_is_staged_too()
    {
        using var t = new TempDir();
        var item = Item(t, "src/Hero.fbx");
        t.Write("src/Hero.fbm/diffuse.png", "png");
        var run = new RunStaging(t.Combine("runs")).Create(new[] { item });
        var staged = Path.Combine(Path.GetDirectoryName(run.StagedPaths[item.Id])!, "Hero.fbm", "diffuse.png");
        Assert.True(File.Exists(staged));
    }

    [Fact]
    public void Run_paths_live_under_the_runs_root()
    {
        using var t = new TempDir();
        var run = new RunStaging(t.Combine("runs")).Create(new[] { Item(t, "src/a.fbx") });
        Assert.StartsWith(t.Combine("runs"), run.RunDir);
        Assert.Equal(Path.Combine(run.RunDir, "manifest.json"), run.ManifestPath);
        Assert.Equal(Path.Combine(run.RunDir, "result.json"), run.ResultPath);
        Assert.Equal(Path.Combine(run.RunDir, "unity.log"), run.LogPath);
    }

    [Fact]
    public void Only_the_newest_runs_are_kept()
    {
        using var t = new TempDir();
        var staging = new RunStaging(t.Combine("runs"));
        var item = Item(t, "src/a.fbx");
        var first = staging.Create(new[] { item }, new DateTime(2026, 1, 1, 10, 0, 0));
        for (var i = 0; i < RunStaging.KeepRuns + 2; i++)
            staging.Create(new[] { item }, new DateTime(2026, 2, 1, 10, 0, i));
        var dirs = Directory.GetDirectories(t.Combine("runs"));
        Assert.Equal(RunStaging.KeepRuns, dirs.Length);
        Assert.False(Directory.Exists(first.RunDir));
    }

    [Fact]
    public void Foreign_folders_in_the_runs_root_are_never_deleted()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Combine("runs", "my-notes"));
        var staging = new RunStaging(t.Combine("runs"));
        var item = Item(t, "src/a.fbx");
        for (var i = 0; i < RunStaging.KeepRuns + 2; i++)
            staging.Create(new[] { item }, new DateTime(2026, 2, 1, 10, 0, i));
        Assert.True(Directory.Exists(t.Combine("runs", "my-notes")));
    }
}
```

`tests/Core.Tests/ManifestBuilderTests.cs`:

```csharp
using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class ManifestBuilderTests
{
    [Fact]
    public void Items_point_at_staged_files_with_normalised_destinations_and_profile_kind()
    {
        using var t = new TempDir();
        var src = t.Write("src/Darius Death.fbx");
        var item = AssetItemFactory.Create(src, null, new AppSettings());
        item.Import.destinationFolder = "Assets\\Art\\Animations\\";
        var run = new RunStaging(t.Combine("runs")).Create(new[] { item });

        var m = ManifestBuilder.Build(run, new[] { item });

        Assert.Equal(ContractInfo.SchemaVersion, m.schemaVersion);
        Assert.Equal(run.RunId, m.runId);
        Assert.Equal(run.ResultPath, m.resultPath);
        Assert.Single(m.items);
        Assert.Equal(item.Id, m.items[0].id);
        Assert.Equal(ProfileIds.HumanoidAnimation, m.items[0].profile);
        Assert.Equal(AnimationCategories.Death, m.items[0].animation.category);
        Assert.Equal(run.StagedPaths[item.Id], m.items[0].sourcePath);
        Assert.Equal("Assets/Art/Animations", m.items[0].destinationFolder);
    }

    [Fact]
    public void Building_does_not_change_the_list_items()
    {
        using var t = new TempDir();
        var item = AssetItemFactory.Create(t.Write("src/Crate.fbx"), null, new AppSettings());
        var original = item.Import.sourcePath;
        var run = new RunStaging(t.Combine("runs")).Create(new[] { item });
        ManifestBuilder.Build(run, new[] { item });
        Assert.Equal(original, item.Import.sourcePath);
    }
}
```

`tests/Core.Tests/GitSnapshotTests.cs`:

```csharp
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class GitSnapshotTests
{
    [Fact]
    public void Parses_nul_separated_porcelain_and_skips_rename_origins()
    {
        using var t = new TempDir();
        t.Write("Assets/a b.fbx");
        var text = " M Assets/old.cs\0?? Assets/a b.fbx\0R  Assets/new.cs\0Assets/was.cs\0";
        var map = GitSnapshot.Parse(text, t.Path);
        Assert.Equal(3, map.Count);
        Assert.True(map.ContainsKey("Assets/a b.fbx"));
        Assert.True(map.ContainsKey("Assets/new.cs"));
        Assert.False(map.ContainsKey("Assets/was.cs"));
    }

    [Fact]
    public void Diff_lists_new_and_changed_entries_but_not_unchanged_or_vanished_ones()
    {
        var before = new Dictionary<string, string> { ["a"] = " M|1", ["b"] = " M|1", ["gone"] = " M|1" };
        var after = new Dictionary<string, string> { ["a"] = " M|1", ["b"] = " M|2", ["c"] = "??|5" };
        var diff = GitSnapshot.Diff(before, after);
        Assert.Equal(2, diff.Count);
        Assert.Contains(diff, d => d.Contains("b") && d.Contains("modified"));
        Assert.Contains(diff, d => d.Contains("c") && d.Contains("new"));
    }

    [Fact]
    public void A_file_modified_again_while_already_modified_is_detected_by_timestamp()
    {
        using var t = new TempDir();
        var path = t.Write("Assets/x.cs");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var before = GitSnapshot.Parse(" M Assets/x.cs\0", t.Path);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc));
        var after = GitSnapshot.Parse(" M Assets/x.cs\0", t.Path);
        Assert.Single(GitSnapshot.Diff(before, after));
    }
}
```

`tests/Core.Tests/LogExcerptTests.cs`:

```csharp
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class LogExcerptTests
{
    [Fact]
    public void Picks_the_last_error_lines_only()
    {
        using var t = new TempDir();
        var lines = new List<string> { "Loading project", "Assets/X.cs(3,4): error CS1002: ; expected" };
        for (var i = 0; i < 10; i++) lines.Add($"[AssetPipeline] ERROR item {i} failed");
        lines.Add("done");
        var log = t.Write("unity.log", string.Join("\n", lines));
        var errors = LogExcerpt.ErrorLines(log, 3);
        Assert.Equal(3, errors.Count);
        Assert.Contains("item 9", errors[^1]);
    }

    [Fact]
    public void Missing_log_gives_nothing() => Assert.Empty(LogExcerpt.ErrorLines("C:\\no\\such.log"));

    [Fact]
    public void Last_line_works_on_a_file_another_process_is_writing()
    {
        using var t = new TempDir();
        var path = t.Combine("unity.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var bytes = System.Text.Encoding.UTF8.GetBytes("first\nsecond line\n");
        writer.Write(bytes); writer.Flush();
        Assert.Equal("second line", LogExcerpt.LastLine(path));
    }
}
```

`tests/Core.Tests/ResultFormatterTests.cs`:

```csharp
using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class ResultFormatterTests
{
    [Fact]
    public void Report_shows_assets_scale_clips_warnings_errors_and_log()
    {
        var r = new ImportResult { runId = "r", unityVersion = "6000.3.25f1", success = false };
        var ok = new ItemResult { id = "a", success = true, measuredHeight = 2.4f, targetHeight = 1.85f, appliedScale = 0.7708f, scaleSource = ScaleSources.Auto };
        ok.importedAssets.Add("Assets/Art/K.fbx");
        ok.createdPrefabs.Add("Assets/Art/K_Visual.prefab");
        ok.avatar = new AvatarReport { valid = true, isHuman = true };
        ok.warnings.Add("stray geometry");
        ok.clips.Add(new ClipReport { name = "Walk", duration = 1.03f, loop = true, bakeHeight = true });
        var bad = new ItemResult { id = "b" };
        bad.errors.Add("Avatar is not humanoid");
        r.items.Add(ok); r.items.Add(bad);

        var text = ResultFormatter.Format(r, id => id == "a" ? "Kestrel" : "Other", new[] { "new: Assets/Art/K.fbx" }, "C:\\logs\\unity.log");

        Assert.Contains("Kestrel", text);
        Assert.Contains("Assets/Art/K_Visual.prefab", text);
        Assert.Contains("2.400", text);
        Assert.Contains("0.7708", text);
        Assert.Contains("Auto", text);
        Assert.Contains("Walk", text);
        Assert.Contains("stray geometry", text);
        Assert.Contains("Avatar is not humanoid", text);
        Assert.Contains("new: Assets/Art/K.fbx", text);
        Assert.Contains("C:\\logs\\unity.log", text);
        Assert.Contains("FAILED", text);
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement.**

`src/Core/Run/RunStaging.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Blackglass.AssetStudio;

public sealed record StagedRun(string RunId, string RunDir, string ManifestPath, string ResultPath, string LogPath,
    IReadOnlyDictionary<string, string> StagedPaths);

/// <summary>Copies sources into a per-run folder so the project only ever receives imported assets and the originals are never opened for writing.</summary>
public sealed class RunStaging
{
    public const int KeepRuns = 10;
    static readonly Regex RunFolder = new(@"^\d{8}-\d{6}-[0-9a-f]{4}$");
    readonly string root;

    public RunStaging(string runsRoot) => root = runsRoot;

    public StagedRun Create(IReadOnlyList<AssetItem> items, DateTime? now = null)
    {
        Directory.CreateDirectory(root);
        Prune(KeepRuns - 1);
        var runId = $"{now ?? DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        var runDir = Path.Combine(root, runId);
        var staged = new Dictionary<string, string>();
        foreach (var item in items)
        {
            var dir = Path.Combine(runDir, "staged", item.Id);
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, Path.GetFileName(item.OriginalPath));
            File.Copy(item.OriginalPath, target, overwrite: true);
            var fbm = Path.Combine(Path.GetDirectoryName(item.OriginalPath)!, Path.GetFileNameWithoutExtension(item.OriginalPath) + ".fbm");
            if (Directory.Exists(fbm)) CopyDirectory(fbm, Path.Combine(dir, Path.GetFileName(fbm)));
            staged[item.Id] = target;
        }
        return new StagedRun(runId, runDir, Path.Combine(runDir, "manifest.json"), Path.Combine(runDir, "result.json"),
            Path.Combine(runDir, "unity.log"), staged);
    }

    void Prune(int keep)
    {
        var old = Directory.GetDirectories(root).Where(d => RunFolder.IsMatch(Path.GetFileName(d)))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).Skip(keep);
        foreach (var dir in old)
        {
            try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
```

`src/Core/Run/ManifestBuilder.cs`:

```csharp
using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio;

public static class ManifestBuilder
{
    public static ImportManifest Build(StagedRun run, IReadOnlyList<AssetItem> items)
    {
        var manifest = new ImportManifest { runId = run.RunId, resultPath = run.ResultPath };
        manifest.items = items.Select(item =>
        {
            var profile = ImportProfiles.Get(item.ProfileId);
            var copy = ManifestJson.Clone(item.Import);
            copy.id = item.Id;
            copy.profile = profile.Kind;
            if (profile.IsAnimation) copy.animation.category = profile.Category;
            copy.destinationFolder = PathRules.Normalize(copy.destinationFolder);
            copy.sourcePath = run.StagedPaths[item.Id];
            return copy;
        }).ToArray();
        return manifest;
    }
}
```

`src/Core/Run/GitSnapshot.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Blackglass.AssetStudio;

/// <summary>Reports which project files an import touched by comparing `git status` before and after. Read-only: never stages or commits.</summary>
public static class GitSnapshot
{
    public static IReadOnlyDictionary<string, string>? TryCapture(string projectPath)
    {
        var root = Git(projectPath, "rev-parse", "--show-toplevel")?.Trim();
        if (string.IsNullOrEmpty(root)) return null;
        var status = Git(projectPath, "status", "--porcelain=v1", "-z", "-uall");
        return status == null ? null : Parse(status, root.Replace('/', Path.DirectorySeparatorChar));
    }

    public static Dictionary<string, string> Parse(string porcelainZ, string repoRoot)
    {
        var map = new Dictionary<string, string>();
        var entries = porcelainZ.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 4) continue;
            var status = entry[..2];
            var path = entry[3..];
            if (status[0] is 'R' or 'C') i++; // the next entry is the original path
            var full = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));
            var stamp = File.Exists(full) ? File.GetLastWriteTimeUtc(full).Ticks.ToString() : "-";
            map[path] = status + "|" + stamp;
        }
        return map;
    }

    public static IReadOnlyList<string> Diff(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after) =>
        after.Where(kv => !before.TryGetValue(kv.Key, out var old) || old != kv.Value)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{Label(kv.Value[..2])}: {kv.Key}").ToList();

    static string Label(string status) =>
        status == "??" ? "new" : status.Contains('D') ? "deleted" : status.Contains('A') ? "added" : status.Contains('M') ? "modified" : status.Trim();

    static string? Git(string dir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var error = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { return null; }
    }
}
```

`src/Core/Run/LogExcerpt.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Blackglass.AssetStudio;

public static class LogExcerpt
{
    static readonly Regex ErrorLine = new(@"error CS\d+|Exception|\[AssetPipeline\] ERROR", RegexOptions.Compiled);

    public static IReadOnlyList<string> ErrorLines(string logPath, int max = 8) =>
        ReadLines(logPath).Where(l => ErrorLine.IsMatch(l)).Select(l => l.Trim()).Distinct().TakeLast(max).ToList();

    public static string LastLine(string logPath) =>
        ReadLines(logPath).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";

    static string[] ReadLines(string path)
    {
        try
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd().Split('\n');
        }
        catch (IOException) { return Array.Empty<string>(); }
    }
}
```

(`LastLine` reads the whole file once a second; Unity batch logs are a few MB at most. Acceptable for v0.1.)

`src/Core/Run/ResultFormatter.cs`:

```csharp
using System.Text;
using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio;

public static class ResultFormatter
{
    public static string Format(ImportResult r, Func<string, string> nameOf, IReadOnlyList<string> changedFiles, string logPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Unity {r.unityVersion}: {(r.success ? "SUCCESS" : "FAILED")}");
        foreach (var e in r.errors) sb.AppendLine($"  ERROR: {e}");
        foreach (var w in r.warnings) sb.AppendLine($"  warning: {w}");
        foreach (var item in r.items)
        {
            sb.AppendLine();
            sb.AppendLine($"[{(item.success ? " OK " : "FAIL")}] {nameOf(item.id)}");
            foreach (var a in item.importedAssets) sb.AppendLine($"  imported: {a}");
            foreach (var p in item.createdPrefabs) sb.AppendLine($"  prefab:   {p}");
            foreach (var c in item.changedAssets) sb.AppendLine($"  updated:  {c}");
            if (!string.IsNullOrEmpty(item.registeredInTheme)) sb.AppendLine($"  theme:    {item.registeredInTheme}");
            if (item.measuredHeight > 0)
                sb.AppendLine($"  height:   measured {item.measuredHeight:0.000} m, target {item.targetHeight:0.000} m, visual scale {item.appliedScale:0.0000} ({item.scaleSource})");
            if (item.dimensions.x > 0 || item.dimensions.y > 0 || item.dimensions.z > 0)
                sb.AppendLine($"  size:     {item.dimensions.x:0.###} x {item.dimensions.y:0.###} x {item.dimensions.z:0.###} m");
            if (item.avatar.valid || item.avatar.isHuman || !string.IsNullOrEmpty(item.avatar.message))
                sb.AppendLine($"  avatar:   {(item.avatar.valid ? "valid" : "INVALID")}, {(item.avatar.isHuman ? "humanoid" : "not humanoid")} {item.avatar.message}".TrimEnd());
            foreach (var c in item.clips)
                sb.AppendLine($"  clip:     {c.name}, {c.duration:0.00} s, loop {Yn(c.loop)}, bake rotation {Yn(c.bakeRotation)} height {Yn(c.bakeHeight)} XZ {Yn(c.bakePositionXZ)}");
            foreach (var w in item.warnings) sb.AppendLine($"  warning:  {w}");
            foreach (var e in item.errors) sb.AppendLine($"  ERROR:    {e}");
        }
        if (changedFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Project files changed by this run (git):");
            foreach (var f in changedFiles) sb.AppendLine($"  {f}");
        }
        sb.AppendLine();
        sb.AppendLine($"Unity log: {logPath}");
        return sb.ToString();
    }

    static string Yn(bool b) => b ? "yes" : "no";
}
```

- [ ] **Step 4: Run to verify pass.**
- [ ] **Step 5: Commit** ("Asset Studio: run staging, manifest builder, git snapshot, log excerpt, result formatter").

---

## Task 6: Unity launcher and import runner

**Files:**
- Create: `src/Core/Run/UnityLauncher.cs`, `src/Core/Run/ImportRunner.cs`
- Test: `tests/Core.Tests/UnityLauncherTests.cs`, `ImportRunnerTests.cs`

**Interfaces:**
- Consumes: Tasks 1-5.
- Produces:
  - `UnityLauncher.EntryMethod` const `"Blackglass.AssetPipeline.AssetPipelineRunner.RunFromCommandLine"`; `UnityLauncher.IsProjectOpen(string projectPath) -> bool`; `UnityLauncher.BuildArguments(string projectPath, string manifestPath, string resultPath, string logPath) -> List<string>`.
  - `interface IUnityProcess { Task<int> RunAsync(string exe, IReadOnlyList<string> arguments, IProgress<string>? progress, string logPath); }` and `UnityProcess : IUnityProcess`.
  - `RunOutcome(string? FatalError, string RunId, string RunDir, string LogPath, int ExitCode, ImportResult? Result, IReadOnlyList<string> ChangedFiles)` with `bool Success`.
  - `ImportRunner(AppSettings settings, IUnityProcess process, Func<string,bool>? isProjectOpen = null, Func<string, IReadOnlyDictionary<string,string>?>? gitSnapshot = null)`; `Task<RunOutcome> RunAsync(IReadOnlyList<AssetItem> items, IProgress<string>? progress = null)`.

- [ ] **Step 1: Tests.**

`tests/Core.Tests/UnityLauncherTests.cs`:

```csharp
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class UnityLauncherTests
{
    [Fact]
    public void Arguments_keep_paths_with_spaces_as_single_elements()
    {
        var args = UnityLauncher.BuildArguments("D:\\My Games\\Blackglass", "C:\\Users\\A B\\m.json", "C:\\Users\\A B\\r.json", "C:\\Users\\A B\\u.log");
        Assert.Contains("-batchmode", args);
        Assert.Equal("D:\\My Games\\Blackglass", args[args.IndexOf("-projectPath") + 1]);
        Assert.Equal(UnityLauncher.EntryMethod, args[args.IndexOf("-executeMethod") + 1]);
        Assert.Equal("C:\\Users\\A B\\m.json", args[args.IndexOf("-blackglassManifest") + 1]);
        Assert.Equal("C:\\Users\\A B\\r.json", args[args.IndexOf("-blackglassResult") + 1]);
        Assert.Equal("C:\\Users\\A B\\u.log", args[args.IndexOf("-logFile") + 1]);
        Assert.Contains("-quit", args);
    }

    [Fact]
    public void Project_is_open_only_while_the_lockfile_is_held()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        Assert.False(UnityLauncher.IsProjectOpen(project));
        var lockfile = t.Write("Temp/UnityLockfile");
        Assert.False(UnityLauncher.IsProjectOpen(project)); // stale file from a crash, not held
        using (new FileStream(lockfile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.True(UnityLauncher.IsProjectOpen(project));
        Assert.False(UnityLauncher.IsProjectOpen(project));
    }
}
```

`tests/Core.Tests/ImportRunnerTests.cs`:

```csharp
using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class ImportRunnerTests
{
    sealed class FakeUnity : IUnityProcess
    {
        public readonly List<string[]> Calls = new();
        public Func<string[], int> Behaviour = _ => 0;

        public Task<int> RunAsync(string exe, IReadOnlyList<string> arguments, IProgress<string>? progress, string logPath)
        {
            var args = arguments.ToArray();
            Calls.Add(args);
            return Task.FromResult(Behaviour(args));
        }

        public static string Arg(string[] a, string name) => a[Array.IndexOf(a, name) + 1];
    }

    sealed class World : IDisposable
    {
        public readonly TempDir T = new();
        public readonly string Project;
        public readonly AppSettings Settings;
        public readonly FakeUnity Unity = new();

        public World()
        {
            Project = T.MakeProject();
            Settings = new AppSettings { ProjectPath = Project, UnityExePath = T.Write("fake/Unity.exe"), StagingFolder = T.Combine("runs") };
        }

        /// <summary>A valid list entry for a file with a space in its name (classified as an environment piece; any valid entry will do).</summary>
        public AssetItem Item(string file = "Crate Box.fbx") => AssetItemFactory.Create(T.Write("src/" + file), null, Settings);

        public ImportRunner Runner(Func<string, bool>? open = null, Func<string, IReadOnlyDictionary<string, string>?>? git = null) =>
            new(Settings, Unity, open ?? (_ => false), git ?? (_ => null));

        /// <summary>A fake Unity that echoes every manifest item back, as successes or failures.</summary>
        public void Echo(bool success = true)
        {
            Unity.Behaviour = args =>
            {
                var manifest = ManifestJson.Deserialize<ImportManifest>(File.ReadAllText(FakeUnity.Arg(args, "-blackglassManifest")));
                foreach (var item in manifest.items) Assert.True(File.Exists(item.sourcePath), "staged source must exist: " + item.sourcePath);
                var result = new ImportResult { runId = manifest.runId, unityVersion = "6000.3.25f1", success = success };
                foreach (var item in manifest.items) result.items.Add(new ItemResult { id = item.id, success = success });
                File.WriteAllText(FakeUnity.Arg(args, "-blackglassResult"), ManifestJson.Serialize(result));
                return success ? 0 : 1;
            };
        }

        public void Dispose() => T.Dispose();
    }

    [Fact]
    public async Task Happy_path_stages_writes_the_manifest_launches_and_returns_the_result()
    {
        using var w = new World();
        w.Echo();
        var item = w.Item();
        var outcome = await w.Runner().RunAsync(new[] { item });

        Assert.Null(outcome.FatalError);
        Assert.True(outcome.Success);
        Assert.Single(w.Unity.Calls);
        Assert.True(File.Exists(Path.Combine(outcome.RunDir, "manifest.json")));
        Assert.Equal(item.Id, outcome.Result!.items[0].id);
        Assert.True(File.Exists(item.OriginalPath));
    }

    [Fact]
    public async Task Failed_items_come_back_as_a_result_not_a_fatal_error()
    {
        using var w = new World();
        w.Echo(success: false);
        var outcome = await w.Runner().RunAsync(new[] { w.Item() });
        Assert.Null(outcome.FatalError);
        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Result);
        Assert.Equal(1, outcome.ExitCode);
    }

    [Fact]
    public async Task An_open_editor_stops_the_run_before_anything_is_staged()
    {
        using var w = new World();
        var outcome = await w.Runner(open: _ => true).RunAsync(new[] { w.Item() });
        Assert.Contains("Close it", outcome.FatalError);
        Assert.Empty(w.Unity.Calls);
        Assert.False(Directory.Exists(w.T.Combine("runs")));
    }

    [Fact]
    public async Task Validation_errors_stop_the_run_and_name_the_item()
    {
        using var w = new World();
        var item = w.Item();
        item.Import.destinationFolder = "Packages/nope";
        var outcome = await w.Runner().RunAsync(new[] { item });
        Assert.Contains("Crate_Box", outcome.FatalError);
        Assert.Contains("Assets", outcome.FatalError);
        Assert.Empty(w.Unity.Calls);
    }

    [Fact]
    public async Task Invalid_project_and_missing_unity_are_explained()
    {
        using var w = new World();
        var item = w.Item();
        w.Settings.ProjectPath = w.T.Combine("not-a-project");
        Assert.Contains("Unity project", (await w.Runner().RunAsync(new[] { item })).FatalError);

        w.Settings.ProjectPath = w.Project;
        w.Settings.UnityExePath = w.T.Combine("missing", "Unity.exe");
        Assert.Contains("Unity.exe", (await w.Runner().RunAsync(new[] { item })).FatalError);
    }

    [Fact]
    public async Task No_result_file_gives_the_exit_code_log_path_and_last_errors()
    {
        using var w = new World();
        w.Unity.Behaviour = args =>
        {
            File.WriteAllText(FakeUnity.Arg(args, "-logFile"), "booting\nAssets/Foo.cs(1,1): error CS1002: ; expected\n");
            return 1;
        };
        var outcome = await w.Runner().RunAsync(new[] { w.Item() });
        Assert.NotNull(outcome.FatalError);
        Assert.Contains("exit code 1", outcome.FatalError);
        Assert.Contains("unity.log", outcome.FatalError);
        Assert.Contains("error CS1002", outcome.FatalError);
        Assert.Null(outcome.Result);
    }

    [Fact]
    public async Task A_garbage_result_file_is_reported_not_thrown()
    {
        using var w = new World();
        w.Unity.Behaviour = args => { File.WriteAllText(FakeUnity.Arg(args, "-blackglassResult"), "{ not json"); return 0; };
        var outcome = await w.Runner().RunAsync(new[] { w.Item() });
        Assert.Contains("result", outcome.FatalError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_result_with_another_schema_version_is_rejected()
    {
        using var w = new World();
        w.Unity.Behaviour = args =>
        {
            File.WriteAllText(FakeUnity.Arg(args, "-blackglassResult"), "{\"schemaVersion\": 99, \"items\": []}");
            return 0;
        };
        Assert.Contains("schema", (await w.Runner().RunAsync(new[] { w.Item() })).FatalError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Changed_project_files_are_listed_from_the_git_snapshots()
    {
        using var w = new World();
        w.Echo();
        var calls = 0;
        IReadOnlyDictionary<string, string>? Snap(string _) =>
            ++calls == 1 ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Assets/Crate_Box.fbx"] = "??|1" };
        var outcome = await w.Runner(git: Snap).RunAsync(new[] { w.Item() });
        Assert.Single(outcome.ChangedFiles);
        Assert.Contains("Crate_Box.fbx", outcome.ChangedFiles[0]);
    }

    [Fact]
    public async Task Unity_receives_every_path_as_one_argument_even_with_spaces_and_accents()
    {
        using var w = new World();
        w.Echo();
        await w.Runner().RunAsync(new[] { w.Item("Darius Walk Café.fbx") });
        var args = w.Unity.Calls.Single();
        Assert.Equal(w.Project, FakeUnity.Arg(args, "-projectPath"));
        Assert.True(File.Exists(FakeUnity.Arg(args, "-blackglassManifest")));
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement.**

`src/Core/Run/UnityLauncher.cs`:

```csharp
using System.Diagnostics;

namespace Blackglass.AssetStudio;

public interface IUnityProcess
{
    Task<int> RunAsync(string exe, IReadOnlyList<string> arguments, IProgress<string>? progress, string logPath);
}

public sealed class UnityProcess : IUnityProcess
{
    public async Task<int> RunAsync(string exe, IReadOnlyList<string> arguments, IProgress<string>? progress, string logPath)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unity did not start.");
        var exited = process.WaitForExitAsync();
        var clock = Stopwatch.StartNew();
        while (!exited.IsCompleted)
        {
            await Task.WhenAny(exited, Task.Delay(1000));
            if (!exited.IsCompleted)
                progress?.Report($"Unity is working ({clock.Elapsed:mm\\:ss}) {LogExcerpt.LastLine(logPath)}".TrimEnd());
        }
        return process.ExitCode;
    }
}

public static class UnityLauncher
{
    public const string EntryMethod = "Blackglass.AssetPipeline.AssetPipelineRunner.RunFromCommandLine";

    /// <summary>The Editor holds an exclusive lock on Temp/UnityLockfile while the project is open. A stale file from a crash is not locked.</summary>
    public static bool IsProjectOpen(string projectPath)
    {
        var lockfile = Path.Combine(projectPath, "Temp", "UnityLockfile");
        if (!File.Exists(lockfile)) return false;
        try
        {
            using var fs = new FileStream(lockfile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public static List<string> BuildArguments(string projectPath, string manifestPath, string resultPath, string logPath) => new()
    {
        "-batchmode", "-projectPath", projectPath,
        "-executeMethod", EntryMethod,
        "-blackglassManifest", manifestPath,
        "-blackglassResult", resultPath,
        "-quit", "-logFile", logPath,
    };
}
```

`src/Core/Run/ImportRunner.cs`:

```csharp
using System.ComponentModel;
using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio;

public sealed record RunOutcome(string? FatalError, string RunId, string RunDir, string LogPath, int ExitCode,
    ImportResult? Result, IReadOnlyList<string> ChangedFiles)
{
    public bool Success => FatalError == null && Result is { success: true };
}

public sealed class ImportRunner
{
    readonly AppSettings settings;
    readonly IUnityProcess process;
    readonly Func<string, bool> isProjectOpen;
    readonly Func<string, IReadOnlyDictionary<string, string>?> gitSnapshot;

    public ImportRunner(AppSettings settings, IUnityProcess process, Func<string, bool>? isProjectOpen = null,
        Func<string, IReadOnlyDictionary<string, string>?>? gitSnapshot = null)
    {
        this.settings = settings;
        this.process = process;
        this.isProjectOpen = isProjectOpen ?? UnityLauncher.IsProjectOpen;
        this.gitSnapshot = gitSnapshot ?? GitSnapshot.TryCapture;
    }

    public async Task<RunOutcome> RunAsync(IReadOnlyList<AssetItem> items, IProgress<string>? progress = null)
    {
        static RunOutcome Fatal(string message, StagedRun? run = null, int exit = -1, IReadOnlyList<string>? changed = null) =>
            new(message, run?.RunId ?? "", run?.RunDir ?? "", run?.LogPath ?? "", exit, null, changed ?? Array.Empty<string>());

        if (items.Count == 0) return Fatal("There is nothing to import. Add files first.");
        if (!ProjectLocator.IsProject(settings.ProjectPath))
            return Fatal("The project path in Settings is not a Unity project (it needs an Assets folder and ProjectSettings/ProjectVersion.txt).");
        var unity = UnityLocator.Resolve(settings, settings.ProjectPath);
        if (unity.Path == null) return Fatal(unity.Message);
        if (isProjectOpen(settings.ProjectPath))
            return Fatal("The Unity Editor has this project open. Close it, then try again (Unity allows only one instance per project).");

        var validation = new ItemValidator(settings.ProjectPath).ValidateAll(items);
        var errors = items.SelectMany(i => validation[i.Id].Where(m => m.Severity == Severity.Error).Select(m => $"{i.Import.name}: {m.Text}")).ToList();
        if (errors.Count > 0) return Fatal("Fix these before importing:\n  " + string.Join("\n  ", errors));

        progress?.Report("Staging files...");
        StagedRun run;
        try { run = new RunStaging(AppPaths.RunsRoot(settings)).Create(items); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return Fatal($"Could not stage the source files: {e.Message}"); }

        File.WriteAllText(run.ManifestPath, ManifestJson.Serialize(ManifestBuilder.Build(run, items)));
        var before = gitSnapshot(settings.ProjectPath);

        progress?.Report("Starting Unity...");
        int exit;
        try
        {
            exit = await process.RunAsync(unity.Path, UnityLauncher.BuildArguments(settings.ProjectPath, run.ManifestPath, run.ResultPath, run.LogPath), progress, run.LogPath);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        { return Fatal($"Could not start Unity: {e.Message}", run); }

        var after = gitSnapshot(settings.ProjectPath);
        IReadOnlyList<string> changed = before != null && after != null ? GitSnapshot.Diff(before, after) : Array.Empty<string>();

        var (result, readError) = ReadResult(run.ResultPath);
        if (result == null)
        {
            var lines = LogExcerpt.ErrorLines(run.LogPath);
            var message = $"Unity finished (exit code {exit}) without a usable result. {readError}\nUnity log: {run.LogPath}";
            if (lines.Count > 0) message += "\nLast errors in the log:\n  " + string.Join("\n  ", lines);
            return Fatal(message, run, exit, changed);
        }
        return new RunOutcome(null, run.RunId, run.RunDir, run.LogPath, exit, result, changed);
    }

    static (ImportResult?, string) ReadResult(string path)
    {
        if (!File.Exists(path)) return (null, "No result file was written.");
        try
        {
            var result = ManifestJson.Deserialize<ImportResult>(File.ReadAllText(path));
            return result.schemaVersion == ContractInfo.SchemaVersion
                ? (result, "")
                : (null, $"The result file has schema version {result.schemaVersion}; this app expects {ContractInfo.SchemaVersion}.");
        }
        catch (System.Text.Json.JsonException e) { return (null, $"The result file could not be read: {e.Message}"); }
    }
}
```

- [ ] **Step 4: Run to verify pass.** The order of checks above (project, Unity executable, editor open, validation) is the contract the tests assume.
- [ ] **Step 5: Commit** ("Asset Studio: Unity batch launcher and import runner with structured result reading").

---

## Task 7: WPF application (Milestone A complete)

**Files:**
- Create: `src/Core/Settings/SettingsDefaults.cs` (+ `tests/Core.Tests/SettingsDefaultsTests.cs`), `src/App/BlackglassAssetStudio.App.csproj`, `App.xaml`, `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `SettingsWindow.xaml`, `SettingsWindow.xaml.cs`, `ViewModels/ViewModelBase.cs`, `RelayCommand.cs`, `AssetItemViewModel.cs`, `MainViewModel.cs`
- Modify: `BlackglassAssetStudio.sln` (add the App project)

**Interfaces:**
- Consumes: all Core types.
- Produces: `SettingsDefaults.Fill(AppSettings settings, string baseDirectory)`; the runnable `BlackglassAssetStudio.exe`.

The app is deliberately thin: it holds no import rules. Everything testable (classification on add, settings defaults, validation, running) is in Core.

- [ ] **Step 1: Test + implement `SettingsDefaults`.**

`tests/Core.Tests/SettingsDefaultsTests.cs`:

```csharp
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class SettingsDefaultsTests
{
    [Fact]
    public void Fills_the_project_path_by_walking_up_from_the_app_folder()
    {
        using var t = new TempDir();
        t.MakeProject();
        var app = t.Combine("Tools", "BlackglassAssetStudio", "bin");
        Directory.CreateDirectory(app);
        var s = new AppSettings();
        SettingsDefaults.Fill(s, app);
        Assert.Equal(t.Path, s.ProjectPath);
    }

    [Fact]
    public void Never_overwrites_values_the_user_set()
    {
        using var t = new TempDir();
        t.MakeProject();
        var s = new AppSettings { ProjectPath = "D:\\Elsewhere" };
        SettingsDefaults.Fill(s, t.Path);
        Assert.Equal("D:\\Elsewhere", s.ProjectPath);
    }

    [Fact]
    public void Suggests_the_darius_model_as_shared_avatar_only_when_it_exists()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var s = new AppSettings { ProjectPath = project };
        SettingsDefaults.Fill(s, t.Path);
        Assert.Equal("", s.DefaultSharedAvatarPath);

        t.Write("Assets/Art/Characters/Darius/Models/Darius Stand Idle.fbx");
        SettingsDefaults.Fill(s, t.Path);
        Assert.Equal("Assets/Art/Characters/Darius/Models/Darius Stand Idle.fbx", s.DefaultSharedAvatarPath);
    }
}
```

`src/Core/Settings/SettingsDefaults.cs`:

```csharp
namespace Blackglass.AssetStudio;

/// <summary>First-run conveniences. Only fills values that are still empty.</summary>
public static class SettingsDefaults
{
    /// <summary>The current known-good Humanoid model whose Avatar the existing clips copy (decision 034).</summary>
    public const string SharedAvatarCandidate = "Assets/Art/Characters/Darius/Models/Darius Stand Idle.fbx";

    public static void Fill(AppSettings s, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(s.ProjectPath))
            s.ProjectPath = ProjectLocator.FindFrom(baseDirectory) ?? "";
        if (string.IsNullOrWhiteSpace(s.DefaultSharedAvatarPath) && ProjectLocator.IsProject(s.ProjectPath)
            && File.Exists(Path.Combine(s.ProjectPath, SharedAvatarCandidate.Replace('/', Path.DirectorySeparatorChar))))
            s.DefaultSharedAvatarPath = SharedAvatarCandidate;
    }
}
```

Run `dotnet test`; expect pass.

- [ ] **Step 2: App project.** `src/App/BlackglassAssetStudio.App.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>BlackglassAssetStudio</AssemblyName>
    <RootNamespace>Blackglass.AssetStudio.App</RootNamespace>
    <Version>0.1.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Core\BlackglassAssetStudio.Core.csproj" />
  </ItemGroup>
</Project>
```
Then `cd Tools/BlackglassAssetStudio && dotnet sln add src/App/BlackglassAssetStudio.App.csproj`.

- [ ] **Step 3: View-model infrastructure.** `ViewModels/ViewModelBase.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Blackglass.AssetStudio.App.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>An empty name tells WPF that every property changed.</summary>
    protected void RaiseAll() => Raise(string.Empty);
}
```

`ViewModels/RelayCommand.cs`:

```csharp
using System.Windows.Input;

namespace Blackglass.AssetStudio.App.ViewModels;

public sealed class RelayCommand : ICommand
{
    readonly Func<Task> execute;
    readonly Func<bool>? canExecute;
    bool running;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(() => { execute(); return Task.CompletedTask; }, canExecute) { }

    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        running = true;
        CommandManager.InvalidateRequerySuggested();
        try { await execute(); }
        finally { running = false; CommandManager.InvalidateRequerySuggested(); }
    }
}
```

- [ ] **Step 4: `ViewModels/AssetItemViewModel.cs`.**

```csharp
using System.Collections.ObjectModel;
using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio.App.ViewModels;

/// <summary>Wraps one list entry for binding. Holds no rules: edits go to the manifest item; defaults come from ImportProfiles.</summary>
public sealed class AssetItemViewModel : ViewModelBase
{
    readonly AppSettings settings;
    ItemResult? lastResult;
    string status = "Not imported";

    public AssetItemViewModel(AssetItem item, AppSettings settings)
    {
        Item = item;
        this.settings = settings;
    }

    public AssetItem Item { get; }
    ImportItem Import => Item.Import;

    public ObservableCollection<string> Messages { get; } = new();

    public IReadOnlyList<ImportProfile> Profiles => ImportProfiles.All;
    public string[] LoopOptions { get; } = { LoopModes.Auto, LoopModes.Yes, LoopModes.No };
    public string[] ThemeModeOptions { get; } = { ThemeModes.None, ThemeModes.Append, ThemeModes.Replace };
    public string[] MaterialModeOptions { get; } = { MaterialModes.Embedded, MaterialModes.External };
    public string[] ElementOptions => EnvironmentElements.All;

    public string FileName => Path.GetFileName(Item.OriginalPath);
    public string FullPath => Item.OriginalPath;
    public string Reason => Item.UnsupportedReason ?? Item.ClassificationReason;
    public string ProfileName => Profile.DisplayName;
    public string Status { get => status; private set { status = value; Raise(); } }

    public ImportProfile Profile
    {
        get => ImportProfiles.Get(Item.ProfileId);
        set
        {
            if (value == null || value.Id == Item.ProfileId) return;
            Item.ProfileId = value.Id;
            ImportProfiles.Apply(Import, value, settings);
            RaiseAll();
        }
    }

    public bool IsCharacter => Profile.Kind == ProfileIds.HumanoidCharacter;
    public bool IsAnimation => Profile.IsAnimation;
    public bool IsProp => Profile.Kind == ProfileIds.GenericProp;
    public bool IsEnvironment => Profile.Kind == ProfileIds.EnvironmentModule;

    public string Name { get => Import.name; set { Import.name = value; Raise(); } }
    public string Destination { get => Import.destinationFolder; set { Import.destinationFolder = value; Raise(); } }
    public bool AllowOverwrite { get => Import.allowOverwrite; set { Import.allowOverwrite = value; Raise(); } }

    public float TargetHeight { get => Import.character.targetHeight; set { Import.character.targetHeight = value; Raise(); Raise(nameof(ScaleSummary)); } }
    public bool NormalizeHeight { get => Import.character.normalizeHeight; set { Import.character.normalizeHeight = value; Raise(); Raise(nameof(ScaleSummary)); } }
    public float ScaleOverride { get => Import.character.scaleOverride; set { Import.character.scaleOverride = value; Raise(); Raise(nameof(ScaleSummary)); } }
    public bool RigHumanoid { get => Import.character.rigHumanoid; set { Import.character.rigHumanoid = value; Raise(); } }
    public bool CreateAvatar { get => Import.character.createAvatar; set { Import.character.createAvatar = value; Raise(); } }
    public string MaterialMode { get => Import.character.materialMode; set { Import.character.materialMode = value; Raise(); } }
    public bool GeneratePrefab { get => Import.character.generatePrefab; set { Import.character.generatePrefab = value; Raise(); } }

    public string ClipName { get => Import.animation.clipName; set { Import.animation.clipName = value; Raise(); } }
    public string SharedAvatarPath { get => Import.animation.sharedAvatarPath; set { Import.animation.sharedAvatarPath = value; Raise(); } }
    public string Loop { get => Import.animation.loop; set { Import.animation.loop = value; Raise(); } }
    public bool BakeRotation { get => Import.animation.bakeRotation; set { Import.animation.bakeRotation = value; Raise(); } }
    public bool BakeHeight { get => Import.animation.bakeHeight; set { Import.animation.bakeHeight = value; Raise(); } }
    public bool BakePositionXZ { get => Import.animation.bakePositionXZ; set { Import.animation.bakePositionXZ = value; Raise(); } }

    public float PropScale { get => Import.prop.scale; set { Import.prop.scale = value; Raise(); } }
    public bool PropGeneratePrefab { get => Import.prop.generatePrefab; set { Import.prop.generatePrefab = value; Raise(); } }

    public string Element { get => Import.environment.element; set { Import.environment.element = value; Raise(); } }
    public string ThemePath { get => Import.environment.themePath; set { Import.environment.themePath = value; Raise(); } }
    public string ThemeMode { get => Import.environment.themeMode; set { Import.environment.themeMode = value; Raise(); } }
    public int ReplaceIndex { get => Import.environment.replaceIndex; set { Import.environment.replaceIndex = value; Raise(); } }
    public float EnvScale { get => Import.environment.scale; set { Import.environment.scale = value; Raise(); } }

    /// <summary>What the last import measured, and what the scale would be with the current target and override.</summary>
    public string ScaleSummary
    {
        get
        {
            if (lastResult == null || lastResult.measuredHeight <= 0) return "The source height is measured by Unity during the import and shown here afterwards.";
            var c = Import.character;
            var s = HeightScale.Compute(lastResult.measuredHeight, c.targetHeight, c.normalizeHeight, c.scaleOverride);
            return s.Ok
                ? $"Last import measured {lastResult.measuredHeight:0.000} m. With the current settings the visual scale is {s.scale:0.0000} ({s.source}); re-import to apply."
                : $"Last import measured {lastResult.measuredHeight:0.000} m. {s.error}";
        }
    }

    public void ApplyValidation(IEnumerable<ValidationMessage> messages)
    {
        var list = messages.ToList();
        Messages.Clear();
        foreach (var m in list) Messages.Add($"{m.Severity}: {m.Text}");
        var errors = list.Count(m => m.Severity == Severity.Error);
        Status = errors > 0 ? $"{errors} error(s)" : "Ready";
    }

    public void ApplyResult(ItemResult result)
    {
        lastResult = result;
        Status = result.success ? "Imported" : "Failed";
        Messages.Clear();
        foreach (var e in result.errors) Messages.Add("Error: " + e);
        foreach (var w in result.warnings) Messages.Add("Warning: " + w);
        Raise(nameof(ScaleSummary));
    }

    public void SetStatus(string text) => Status = text;
}
```

- [ ] **Step 5: `ViewModels/MainViewModel.cs`.**

```csharp
using System.Collections.ObjectModel;

namespace Blackglass.AssetStudio.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    static readonly string[] ModelExtensions = { ".fbx", ".obj", ".glb", ".gltf" };

    readonly SettingsStore store;
    AssetItemViewModel? selected;
    string statusText = "Add files to begin.";
    string resultText = "";
    string logPath = "";
    bool isBusy;

    public MainViewModel(SettingsStore store)
    {
        this.store = store;
        Settings = store.Load();
        SettingsDefaults.Fill(Settings, AppContext.BaseDirectory);
        store.Save(Settings);

        AddFilesCommand = new RelayCommand(() => { if (PickFiles != null) AddFiles(PickFiles(), null); });
        RemoveCommand = new RelayCommand(() => { if (Selected != null) Items.Remove(Selected); }, () => Selected != null && !IsBusy);
        ClearCommand = new RelayCommand(() => Items.Clear(), () => Items.Count > 0 && !IsBusy);
        ValidateCommand = new RelayCommand(Validate, () => Items.Count > 0 && !IsBusy);
        ImportSelectedCommand = new RelayCommand(() => ImportAsync(Selected == null ? new List<AssetItemViewModel>() : new List<AssetItemViewModel> { Selected }),
            () => Selected != null && !IsBusy);
        ImportAllCommand = new RelayCommand(() => ImportAsync(Items.ToList()), () => Items.Count > 0 && !IsBusy);
        SettingsCommand = new RelayCommand(OpenSettings, () => !IsBusy);
        OpenLogCommand = new RelayCommand(() => OpenPath?.Invoke(LogPath), () => LogPath.Length > 0);
        Items.CollectionChanged += (_, _) => Raise(nameof(HasItems));
    }

    public AppSettings Settings { get; }
    public ObservableCollection<AssetItemViewModel> Items { get; } = new();

    public AssetItemViewModel? Selected { get => selected; set { selected = value; Raise(); Raise(nameof(HasSelection)); } }
    public bool HasSelection => Selected != null;
    public bool HasItems => Items.Count > 0;
    public string StatusText { get => statusText; private set { statusText = value; Raise(); } }
    public string ResultText { get => resultText; private set { resultText = value; Raise(); } }
    public string LogPath { get => logPath; private set { logPath = value; Raise(); } }
    public bool IsBusy { get => isBusy; private set { isBusy = value; Raise(); } }

    public RelayCommand AddFilesCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand ValidateCommand { get; }
    public RelayCommand ImportSelectedCommand { get; }
    public RelayCommand ImportAllCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand OpenLogCommand { get; }

    /// <summary>Set by the window: a file picker, the settings dialog (true when saved) and "show this file in Explorer".</summary>
    public Func<IEnumerable<string>>? PickFiles { get; set; }
    public Func<AppSettings, bool>? ShowSettings { get; set; }
    public Action<string>? OpenPath { get; set; }

    /// <summary>Files, or folders (searched recursively for model files; the dropped folder is the classification context).</summary>
    public void AddDropped(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                AddFiles(Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                    .Where(f => ModelExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)), path);
            else
                AddFiles(new[] { path }, Path.GetDirectoryName(path));
        }
    }

    public void AddFiles(IEnumerable<string> paths, string? dropFolder)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (Items.Any(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
            Items.Add(new AssetItemViewModel(AssetItemFactory.Create(path, dropFolder, Settings), Settings));
            added++;
        }
        if (added > 0)
        {
            Selected ??= Items[^1];
            if (dropFolder != null) { Settings.LastDropFolder = dropFolder; store.Save(Settings); }
        }
        StatusText = added == 0 ? "No new files were added." : $"Added {added} file(s). Check each profile, then Validate or Import.";
    }

    void Validate()
    {
        if (!ProjectLocator.IsProject(Settings.ProjectPath)) { StatusText = "Set the Blackglass project path in Settings first."; return; }
        var results = new ItemValidator(Settings.ProjectPath).ValidateAll(Items.Select(i => i.Item).ToList());
        foreach (var vm in Items) vm.ApplyValidation(results[vm.Item.Id]);
        var errors = results.Values.Sum(v => v.Count(m => m.Severity == Severity.Error));
        StatusText = errors == 0 ? "Validation passed." : $"Validation found {errors} error(s). Select an entry to read them.";
    }

    void OpenSettings()
    {
        if (ShowSettings != null && ShowSettings(Settings)) { store.Save(Settings); StatusText = "Settings saved."; }
    }

    async Task ImportAsync(IReadOnlyList<AssetItemViewModel> subset)
    {
        if (subset.Count == 0 || IsBusy) return;
        IsBusy = true;
        try
        {
            foreach (var vm in subset) vm.SetStatus("Queued");
            var progress = new Progress<string>(text => StatusText = text);
            var runner = new ImportRunner(Settings, new UnityProcess());
            var outcome = await runner.RunAsync(subset.Select(i => i.Item).ToList(), progress);
            LogPath = outcome.LogPath;
            var names = Items.ToDictionary(i => i.Item.Id, i => i.Item.Import.name);
            if (outcome.FatalError != null)
            {
                ResultText = outcome.FatalError;
                foreach (var vm in subset) vm.SetStatus("Not run");
                StatusText = "Import did not run. See the Result tab.";
                return;
            }
            foreach (var r in outcome.Result!.items)
                Items.FirstOrDefault(i => i.Item.Id == r.id)?.ApplyResult(r);
            ResultText = ResultFormatter.Format(outcome.Result, id => names.GetValueOrDefault(id, id), outcome.ChangedFiles, outcome.LogPath);
            StatusText = outcome.Success ? "Import finished." : "Import finished with errors. See the Result tab.";
        }
        finally { IsBusy = false; }
    }
}
```

- [ ] **Step 6: Windows.** `App.xaml`:

```xml
<Application x:Class="Blackglass.AssetStudio.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml">
    <Application.Resources />
</Application>
```

`App.xaml.cs`:

```csharp
using System.Windows;

namespace Blackglass.AssetStudio.App;

public partial class App : Application { }
```

`MainWindow.xaml` (functional layout; no production polish):

```xml
<Window x:Class="Blackglass.AssetStudio.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Blackglass Asset Studio" Width="1200" Height="780" MinWidth="900" MinHeight="600"
        AllowDrop="True" Drop="OnDrop" DragOver="OnDragOver" Closing="OnClosing">
    <Window.Resources>
        <BooleanToVisibilityConverter x:Key="Vis" />
        <Style TargetType="TextBlock" x:Key="Label"><Setter Property="Margin" Value="0,6,0,2" /><Setter Property="FontWeight" Value="SemiBold" /></Style>
        <Style TargetType="GroupBox"><Setter Property="Margin" Value="0,8,0,0" /><Setter Property="Padding" Value="6" /></Style>
        <Style TargetType="Button"><Setter Property="Margin" Value="0,0,6,0" /><Setter Property="Padding" Value="10,4" /></Style>
    </Window.Resources>
    <DockPanel Margin="8">
        <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,8">
            <Button Content="Add Files..." Command="{Binding AddFilesCommand}" />
            <Button Content="Remove" Command="{Binding RemoveCommand}" />
            <Button Content="Clear" Command="{Binding ClearCommand}" />
            <Button Content="Validate" Command="{Binding ValidateCommand}" />
            <Button Content="Import Selected" Command="{Binding ImportSelectedCommand}" />
            <Button Content="Import All" Command="{Binding ImportAllCommand}" />
            <Button Content="Settings..." Command="{Binding SettingsCommand}" />
        </StackPanel>
        <StatusBar DockPanel.Dock="Bottom">
            <StatusBarItem><TextBlock Text="{Binding StatusText}" /></StatusBarItem>
            <StatusBarItem HorizontalAlignment="Right"><ProgressBar Width="120" Height="12" IsIndeterminate="True" Visibility="{Binding IsBusy, Converter={StaticResource Vis}}" /></StatusBarItem>
        </StatusBar>
        <Grid>
            <Grid.RowDefinitions><RowDefinition Height="*" /><RowDefinition Height="4" /><RowDefinition Height="200" /></Grid.RowDefinitions>
            <Grid Grid.Row="0">
                <Grid.ColumnDefinitions><ColumnDefinition Width="460" /><ColumnDefinition Width="8" /><ColumnDefinition Width="*" /></Grid.ColumnDefinitions>
                <Grid Grid.Column="0">
                    <ListView ItemsSource="{Binding Items}" SelectedItem="{Binding Selected}">
                        <ListView.View>
                            <GridView>
                                <GridViewColumn Header="File" Width="170" DisplayMemberBinding="{Binding FileName}" />
                                <GridViewColumn Header="Profile" Width="170" DisplayMemberBinding="{Binding ProfileName}" />
                                <GridViewColumn Header="Status" Width="90" DisplayMemberBinding="{Binding Status}" />
                            </GridView>
                        </ListView.View>
                    </ListView>
                    <TextBlock Text="Drop FBX / OBJ files or folders here" Foreground="Gray" HorizontalAlignment="Center" VerticalAlignment="Center" IsHitTestVisible="False">
                        <TextBlock.Style><Style TargetType="TextBlock"><Setter Property="Visibility" Value="Collapsed" />
                            <Style.Triggers><DataTrigger Binding="{Binding HasItems}" Value="False"><Setter Property="Visibility" Value="Visible" /></DataTrigger></Style.Triggers></Style></TextBlock.Style>
                    </TextBlock>
                </Grid>
                <ScrollViewer Grid.Column="2" VerticalScrollBarVisibility="Auto">
                    <StackPanel DataContext="{Binding Selected}" Margin="4,0,12,0">
                        <TextBlock Text="{Binding FullPath}" TextWrapping="Wrap" Foreground="Gray" />
                        <TextBlock Text="{Binding Reason}" TextWrapping="Wrap" Foreground="Gray" FontStyle="Italic" />
                        <TextBlock Text="Profile" Style="{StaticResource Label}" />
                        <ComboBox ItemsSource="{Binding Profiles}" SelectedItem="{Binding Profile}" DisplayMemberPath="DisplayName" />
                        <TextBlock Text="Asset name" Style="{StaticResource Label}" />
                        <TextBox Text="{Binding Name, UpdateSourceTrigger=LostFocus}" />
                        <TextBlock Text="Destination folder (inside Assets)" Style="{StaticResource Label}" />
                        <TextBox Text="{Binding Destination, UpdateSourceTrigger=LostFocus}" />
                        <CheckBox Content="Allow overwrite of existing assets not created by Asset Studio" IsChecked="{Binding AllowOverwrite}" Margin="0,6,0,0" />

                        <GroupBox Header="Humanoid character" Visibility="{Binding IsCharacter, Converter={StaticResource Vis}}">
                            <StackPanel>
                                <CheckBox Content="Humanoid rig" IsChecked="{Binding RigHumanoid}" />
                                <CheckBox Content="Create Avatar" IsChecked="{Binding CreateAvatar}" />
                                <CheckBox Content="Normalise height" IsChecked="{Binding NormalizeHeight}" />
                                <TextBlock Text="Target height (m)" Style="{StaticResource Label}" />
                                <TextBox Text="{Binding TargetHeight, UpdateSourceTrigger=LostFocus, StringFormat=0.###}" />
                                <TextBlock Text="Manual visual scale (0 = automatic)" Style="{StaticResource Label}" />
                                <TextBox Text="{Binding ScaleOverride, UpdateSourceTrigger=LostFocus, StringFormat=0.####}" />
                                <TextBlock Text="{Binding ScaleSummary}" TextWrapping="Wrap" Foreground="DimGray" Margin="0,6,0,0" />
                                <TextBlock Text="Materials" Style="{StaticResource Label}" />
                                <ComboBox ItemsSource="{Binding MaterialModeOptions}" SelectedItem="{Binding MaterialMode}" />
                                <CheckBox Content="Generate / update visual prefab" IsChecked="{Binding GeneratePrefab}" Margin="0,6,0,0" />
                            </StackPanel>
                        </GroupBox>

                        <GroupBox Header="Humanoid animation" Visibility="{Binding IsAnimation, Converter={StaticResource Vis}}">
                            <StackPanel>
                                <TextBlock Text="Clip name" Style="{StaticResource Label}" />
                                <TextBox Text="{Binding ClipName, UpdateSourceTrigger=LostFocus}" />
                                <TextBlock Text="Shared Avatar model (project path)" Style="{StaticResource Label}" />
                                <TextBox Text="{Binding SharedAvatarPath, UpdateSourceTrigger=LostFocus}" />
                                <TextBlock Text="Loop" Style="{StaticResource Label}" />
                                <ComboBox ItemsSource="{Binding LoopOptions}" SelectedItem="{Binding Loop}" />
                                <TextBlock Text="Bake into pose (in-place)" Style="{StaticResource Label}" />
                                <CheckBox Content="Root rotation" IsChecked="{Binding BakeRotation}" />
                                <CheckBox Content="Root height (Y)" IsChecked="{Binding BakeHeight}" />
                                <CheckBox Content="Root position (XZ)" IsChecked="{Binding BakePositionXZ}" />
                            </StackPanel>
                        </GroupBox>

                        <GroupBox Header="Generic prop" Visibility="{Binding IsProp, Converter={StaticResource Vis}}">
                            <StackPanel>
                                <CheckBox Content="Generate / update prefab" IsChecked="{Binding PropGeneratePrefab}" />
                                <TextBlock Text="Scale" Style="{StaticResource Label}" />
                                <TextBox Text="{Binding PropScale, UpdateSourceTrigger=LostFocus, StringFormat=0.###}" />
                            </StackPanel>
                        </GroupBox>

                        <GroupBox Header="Environment module" Visibility="{Binding IsEnvironment, Converter={StaticResource Vis}}">
                            <StackPanel>
                                <TextBlock Text="Element" Style="{StaticResource Label}" />
                                <ComboBox ItemsSource="{Binding ElementOptions}" SelectedItem="{Binding Element}" />
                                <TextBlock Text="Explicit scale correction (1 = none; never automatic)" Style="{StaticResource Label}" />
                                <TextBox Text="{Binding EnvScale, UpdateSourceTrigger=LostFocus, StringFormat=0.###}" />
                                <TextBlock Text="Register in theme" Style="{StaticResource Label}" />
                                <ComboBox ItemsSource="{Binding ThemeModeOptions}" SelectedItem="{Binding ThemeMode}" />
                                <TextBlock Text="Theme asset (project path)" Style="{StaticResource Label}" />
                                <TextBox Text="{Binding ThemePath, UpdateSourceTrigger=LostFocus}" />
                                <TextBlock Text="Variant index to replace" Style="{StaticResource Label}" />
                                <TextBox Text="{Binding ReplaceIndex, UpdateSourceTrigger=LostFocus}" />
                            </StackPanel>
                        </GroupBox>

                        <TextBlock Text="Messages" Style="{StaticResource Label}" Margin="0,12,0,2" />
                        <ItemsControl ItemsSource="{Binding Messages}"><ItemsControl.ItemTemplate><DataTemplate><TextBlock Text="{Binding}" TextWrapping="Wrap" /></DataTemplate></ItemsControl.ItemTemplate></ItemsControl>
                    </StackPanel>
                </ScrollViewer>
            </Grid>
            <GridSplitter Grid.Row="1" Height="4" HorizontalAlignment="Stretch" />
            <TabControl Grid.Row="2">
                <TabItem Header="Result">
                    <TextBox Text="{Binding ResultText, Mode=OneWay}" IsReadOnly="True" FontFamily="Consolas" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Auto" />
                </TabItem>
                <TabItem Header="Log">
                    <StackPanel Margin="8">
                        <TextBlock Text="{Binding LogPath}" TextWrapping="Wrap" />
                        <Button Content="Show log in Explorer" Command="{Binding OpenLogCommand}" HorizontalAlignment="Left" Margin="0,8,0,0" />
                    </StackPanel>
                </TabItem>
            </TabControl>
        </Grid>
    </DockPanel>
</Window>
```

`MainWindow.xaml.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Blackglass.AssetStudio.App.ViewModels;
using Microsoft.Win32;

namespace Blackglass.AssetStudio.App;

public partial class MainWindow : Window
{
    readonly MainViewModel vm;

    public MainWindow()
    {
        InitializeComponent();
        vm = new MainViewModel(new SettingsStore(AppPaths.SettingsPath))
        {
            PickFiles = () =>
            {
                var dialog = new OpenFileDialog { Multiselect = true, Filter = "3D models (*.fbx;*.obj;*.glb;*.gltf)|*.fbx;*.obj;*.glb;*.gltf|All files|*.*" };
                return dialog.ShowDialog(this) == true ? dialog.FileNames : Array.Empty<string>();
            },
            ShowSettings = settings => new SettingsWindow(settings) { Owner = this }.ShowDialog() == true,
            OpenPath = path =>
            {
                if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"" });
            },
        };
        DataContext = vm;
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) vm.AddDropped(paths);
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!vm.IsBusy) return;
        MessageBox.Show(this, "Unity is still importing. Closing now could leave project assets half-written, so please wait for it to finish.",
            "Import running", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Cancel = true;
    }
}
```

`SettingsWindow.xaml`:

```xml
<Window x:Class="Blackglass.AssetStudio.App.SettingsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Settings" Width="640" SizeToContent="Height" WindowStartupLocation="CenterOwner" ResizeMode="NoResize">
    <StackPanel Margin="12">
        <TextBlock Text="Blackglass project folder (contains Assets and ProjectSettings)" />
        <DockPanel><Button DockPanel.Dock="Right" Content="Browse..." Click="BrowseProject" Margin="6,0,0,0" /><TextBox x:Name="ProjectBox" /></DockPanel>
        <TextBlock x:Name="ProjectNote" Foreground="DimGray" Margin="0,2,0,8" />
        <TextBlock Text="Unity executable (leave empty to detect the project's version)" />
        <DockPanel><Button DockPanel.Dock="Right" Content="Browse..." Click="BrowseUnity" Margin="6,0,0,0" /><TextBox x:Name="UnityBox" /></DockPanel>
        <TextBlock x:Name="UnityNote" Foreground="DimGray" Margin="0,2,0,8" />
        <TextBlock Text="Staging / run folder (leave empty for %LocalAppData%\BlackglassAssetStudio\runs)" />
        <TextBox x:Name="StagingBox" Margin="0,0,0,8" />
        <TextBlock Text="Default character target height (m)" />
        <TextBox x:Name="HeightBox" Margin="0,0,0,8" />
        <TextBlock Text="Default shared Avatar model (project path, for animation clips)" />
        <TextBox x:Name="AvatarBox" Margin="0,0,0,12" />
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
            <Button Content="Check" Click="Check" Width="80" Margin="0,0,8,0" />
            <Button Content="Save" IsDefault="True" Click="Save" Width="80" Margin="0,0,8,0" />
            <Button Content="Cancel" IsCancel="True" Width="80" />
        </StackPanel>
    </StackPanel>
</Window>
```

`SettingsWindow.xaml.cs`:

```csharp
using System.Globalization;
using System.Windows;
using Microsoft.Win32;

namespace Blackglass.AssetStudio.App;

public partial class SettingsWindow : Window
{
    readonly AppSettings settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        this.settings = settings;
        ProjectBox.Text = settings.ProjectPath;
        UnityBox.Text = settings.UnityExePath;
        StagingBox.Text = settings.StagingFolder;
        HeightBox.Text = settings.DefaultTargetHeight.ToString("0.###", CultureInfo.CurrentCulture);
        AvatarBox.Text = settings.DefaultSharedAvatarPath;
        Check(this, new RoutedEventArgs());
    }

    void BrowseProject(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the Blackglass project folder" };
        if (dialog.ShowDialog(this) == true) { ProjectBox.Text = dialog.FolderName; Check(sender, e); }
    }

    void BrowseUnity(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Unity.exe|Unity.exe|Executables|*.exe" };
        if (dialog.ShowDialog(this) == true) { UnityBox.Text = dialog.FileName; Check(sender, e); }
    }

    void Check(object sender, RoutedEventArgs e)
    {
        var project = ProjectBox.Text.Trim();
        var isProject = ProjectLocator.IsProject(project);
        ProjectNote.Text = isProject ? $"OK. Unity version {ProjectLocator.ReadEditorVersion(project)}." : "Not a Unity project folder.";
        UnityNote.Text = isProject ? UnityLocator.Resolve(new AppSettings { UnityExePath = UnityBox.Text.Trim() }, project).Message : "";
    }

    void Save(object sender, RoutedEventArgs e)
    {
        if (!float.TryParse(HeightBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var height) || !(height > 0))
        {
            MessageBox.Show(this, "The target height must be a number greater than 0.", "Settings");
            return;
        }
        settings.ProjectPath = ProjectBox.Text.Trim();
        settings.UnityExePath = UnityBox.Text.Trim();
        settings.StagingFolder = StagingBox.Text.Trim();
        settings.DefaultTargetHeight = height;
        settings.DefaultSharedAvatarPath = AvatarBox.Text.Trim();
        DialogResult = true;
    }
}
```
(`OpenFolderDialog` exists in WPF since .NET 8.)

- [ ] **Step 7: Build and test.**

```bash
dotnet build Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln -c Debug
dotnet test Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln
```
Expect 0 errors, all tests passing.

- [ ] **Step 8: Launch check** (PowerShell): the app must start and stay up; close it afterwards.

```powershell
$exe = Get-ChildItem Tools\BlackglassAssetStudio\src\App\bin\Debug -Recurse -Filter BlackglassAssetStudio.exe | Select-Object -First 1
$p = Start-Process $exe.FullName -PassThru
Start-Sleep -Seconds 5
"HasExited=$($p.HasExited) Title=$($p.MainWindowTitle)"
if (-not $p.HasExited) { $p.CloseMainWindow() | Out-Null; Start-Sleep 2; if (-not $p.HasExited) { $p.Kill() } }
```
Expected: `HasExited=False Title=Blackglass Asset Studio`. Drag-drop cannot be scripted reliably: record "manual check pending" in the final report and list it in the manual test procedure (drag a `.fbx` from Explorer onto the window; it appears with a suggested profile; change the profile in the combo; Settings shows the detected project and Unity version).

- [ ] **Step 9: Commit** ("Asset Studio: WPF application shell with drag-drop list, profile inspector, settings and result tabs").

---

## Task 8: Unity pipeline foundation (Milestone B, Unity side)

**Files:**
- Create: `Assets/_Project/Editor/AssetPipeline/Blackglass.AssetPipeline.Editor.asmdef`, `Entry/AssetPipelineRunner.cs`, `Importers/AssetPaths.cs`, `Importers/OwnershipLabel.cs`, `Importers/SourceCopier.cs`
- Create tests: `Assets/_Project/Tests/EditMode/AssetPipeline/ContractUnityTests.cs`, `RunnerTests.cs`, `ScratchFolder.cs`
- Modify: `Assets/_Project/Tests/EditMode/Blackglass.Tests.EditMode.asmdef` (one reference: `"Blackglass.AssetPipeline.Editor"`)
- Commit all generated `.meta` files for the new files and folders (Unity creates them on its first run, including those for the Contract files from Task 1).

**Interfaces:**
- Consumes: Contract (Task 1).
- Produces: `AssetPipelineRunner.Run(string manifestPath, string resultPathOverride) -> ImportResult`, `RunFromCommandLine()`, menu `Blackglass/Asset Pipeline/Run Manifest...`; `AssetPaths.ProjectRoot/Full(string)/EnsureFolder(string)`; `OwnershipLabel.IsOwned(string)/Mark(string)`; `SourceCopier.CheckConflicts(ImportItem, ItemResult) -> bool`, `SourceCopier.CopyAndImport(ImportItem, ItemResult) -> string` (model asset path); test helper `ScratchFolder` (`Path = "Assets/_AssetStudioScratch"`, `Clean()`, `Darius(relative)`).

- [ ] **Step 1: Baseline.** Close the Unity Editor. Run `Tools/run-tests.sh EditMode` and record the totals in `.claude/scratchpad/asset-studio-baseline.txt`; they must be unchanged (plus the new tests) at the end.

- [ ] **Step 2: Write the tests first.**

`Assets/_Project/Tests/EditMode/AssetPipeline/ScratchFolder.cs`:

```csharp
#if UNITY_EDITOR
using UnityEditor;

namespace Blackglass.Tests.AssetPipeline
{
    /// <summary>A throwaway folder under Assets for importer tests. Always call Clean() in TearDown.</summary>
    internal static class ScratchFolder
    {
        public const string Path = "Assets/_AssetStudioScratch";

        public static void Clean()
        {
            if (AssetDatabase.IsValidFolder(Path))
                AssetDatabase.DeleteAsset(Path);
            AssetDatabase.Refresh();
        }

        /// <summary>Absolute path of a file of the existing Darius art, used as importer input (for example "Models/Darius Stand Idle.fbx").</summary>
        public static string Darius(string relativeToDarius) =>
            System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, "Art", "Characters", "Darius", relativeToDarius));
    }
}
#endif
```

`ContractUnityTests.cs`:

```csharp
#if UNITY_EDITOR
using System;
using System.IO;
using Blackglass.AssetPipeline;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests.AssetPipeline
{
    public class ContractUnityTests
    {
        static string FixturePath() => System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..",
            "Tools", "BlackglassAssetStudio", "tests", "Core.Tests", "Fixtures", "manifest-v1.json"));

        [Test]
        public void GoldenFixtureParsesWithJsonUtility()
        {
            var m = JsonUtility.FromJson<ImportManifest>(File.ReadAllText(FixturePath()));
            Assert.AreEqual(1, m.schemaVersion);
            Assert.AreEqual(3, m.items.Length);
            Assert.AreEqual(ProfileIds.HumanoidCharacter, m.items[0].profile);
            Assert.AreEqual(1.85f, m.items[0].character.targetHeight);
            StringAssert.Contains(" ", m.items[0].sourcePath);
            Assert.AreEqual("Walk", m.items[1].animation.clipName);
            Assert.IsTrue(m.items[1].allowOverwrite);
            Assert.AreEqual("WallStraight", m.items[2].environment.element);
            Assert.AreEqual(ThemeModes.Append, m.items[2].environment.themeMode);
        }

        [Test]
        public void ResultWrittenByJsonUtilityKeepsItsShape()
        {
            var r = new ImportResult { runId = "r", success = true };
            var item = new ItemResult { id = "i", success = true, appliedScale = 0.5f };
            item.clips.Add(new ClipReport { name = "Walk", loop = true });
            r.items.Add(item);
            var back = JsonUtility.FromJson<ImportResult>(JsonUtility.ToJson(r, true));
            Assert.AreEqual("Walk", back.items[0].clips[0].name);
            Assert.AreEqual(0.5f, back.items[0].appliedScale);
        }

        [Test]
        public void HeightScaleMatchesTheSpecRules()
        {
            var auto = HeightScale.Compute(2.4f, 1.85f, true, 0f);
            Assert.IsTrue(auto.Ok);
            Assert.AreEqual(1.85f / 2.4f, auto.scale, 1e-5f);
            Assert.AreEqual(ScaleSources.Manual, HeightScale.Compute(0f, 1.85f, true, 0.5f).source);
            Assert.IsFalse(HeightScale.Compute(0f, 1.85f, true, 0f).Ok);
            Assert.IsFalse(HeightScale.Compute(float.NaN, 1.85f, true, 0f).Ok);
        }

        [Test]
        public void EnvironmentElementListMatchesTheEnum()
        {
            CollectionAssert.AreEquivalent(Enum.GetNames(typeof(EnvironmentElement)), EnvironmentElements.All);
        }

        [Test]
        public void PathRulesRefuseEscapesFromAssets()
        {
            Assert.IsNull(PathRules.ValidateDestination("Assets/Art/Props"));
            Assert.IsNotNull(PathRules.ValidateDestination("Assets/../Library"));
            Assert.IsNotNull(PathRules.ValidateDestination("Packages/x"));
        }

        [Test]
        public void EntryMethodNameMatchesWhatTheAppLaunches()
        {
            Assert.AreEqual("Blackglass.AssetPipeline.AssetPipelineRunner.RunFromCommandLine",
                typeof(AssetPipelineRunner).FullName + "." + nameof(AssetPipelineRunner.RunFromCommandLine));
        }
    }
}
#endif
```

`RunnerTests.cs`:

```csharp
#if UNITY_EDITOR
using System.IO;
using Blackglass.AssetPipeline;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests.AssetPipeline
{
    public class RunnerTests
    {
        string dir;

        [SetUp]
        public void SetUp()
        {
            dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bgas-unity-" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(dir, true); } catch { }
            ScratchFolder.Clean();
        }

        string Write(ImportManifest m)
        {
            var path = System.IO.Path.Combine(dir, "manifest.json");
            File.WriteAllText(path, JsonUtility.ToJson(m));
            return path;
        }

        [Test]
        public void UnknownSchemaVersionFailsWithAMessageAndStillWritesTheResult()
        {
            var resultPath = System.IO.Path.Combine(dir, "result.json");
            var manifest = Write(new ImportManifest { schemaVersion = 99, resultPath = resultPath });
            var result = AssetPipelineRunner.Run(manifest, null);
            Assert.IsFalse(result.success);
            StringAssert.Contains("schema", string.Join(" ", result.errors).ToLowerInvariant());
            Assert.IsTrue(File.Exists(resultPath));
            Assert.IsFalse(JsonUtility.FromJson<ImportResult>(File.ReadAllText(resultPath)).success);
        }

        [Test]
        public void MissingManifestIsReportedThroughTheResultOverride()
        {
            var resultPath = System.IO.Path.Combine(dir, "result.json");
            var result = AssetPipelineRunner.Run(System.IO.Path.Combine(dir, "nope.json"), resultPath);
            Assert.IsFalse(result.success);
            Assert.IsTrue(File.Exists(resultPath));
        }

        [Test]
        public void UnknownProfileAndBadDestinationFailOnlyTheirOwnItem()
        {
            var m = new ImportManifest
            {
                resultPath = System.IO.Path.Combine(dir, "result.json"),
                items = new[]
                {
                    new ImportItem { id = "a", profile = "Nope", name = "A", destinationFolder = ScratchFolder.Path },
                    new ImportItem { id = "b", profile = ProfileIds.GenericProp, name = "B", destinationFolder = "Packages/x" },
                },
            };
            var result = AssetPipelineRunner.Run(Write(m), null);
            Assert.AreEqual(2, result.items.Count);
            Assert.IsFalse(result.items[0].success);
            StringAssert.Contains("Nope", result.items[0].errors[0]);
            Assert.IsFalse(result.items[1].success);
            StringAssert.Contains("Assets", result.items[1].errors[0]);
            Assert.IsFalse(result.success);
        }

        [Test]
        public void ResultCarriesRunIdAndUnityVersion()
        {
            var result = AssetPipelineRunner.Run(Write(new ImportManifest { runId = "run-1", resultPath = System.IO.Path.Combine(dir, "r.json") }), null);
            Assert.AreEqual("run-1", result.runId);
            Assert.AreEqual(Application.unityVersion, result.unityVersion);
            Assert.IsTrue(result.success); // an empty manifest has nothing to fail
        }
    }
}
#endif
```

Add `"Blackglass.AssetPipeline.Editor"` to the `references` of `Blackglass.Tests.EditMode.asmdef`.

- [ ] **Step 3: Create the asmdef, run, and watch the tests fail to compile.** `Assets/_Project/Editor/AssetPipeline/Blackglass.AssetPipeline.Editor.asmdef`:

```json
{
    "name": "Blackglass.AssetPipeline.Editor",
    "rootNamespace": "Blackglass.AssetPipeline",
    "references": [
        "Blackglass"
    ],
    "includePlatforms": [
        "Editor"
    ],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": false,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```
Run `Tools/run-tests.sh EditMode "Blackglass.Tests.AssetPipeline"` → compile errors for `AssetPipelineRunner` (this run also makes Unity create the `.meta` files).

- [ ] **Step 4: Implement the Unity foundation.**

`Importers/AssetPaths.cs`:

```csharp
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Blackglass.AssetPipeline
{
    public static class AssetPaths
    {
        public static string ProjectRoot { get { return Directory.GetParent(Application.dataPath).FullName; } }

        public static string Full(string assetPath)
        {
            return Path.Combine(ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>Creates every missing folder of "Assets/A/B" through the AssetDatabase.</summary>
        public static void EnsureFolder(string assetFolder)
        {
            var parts = assetFolder.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
```

`Importers/OwnershipLabel.cs`:

```csharp
using System;
using UnityEditor;

namespace Blackglass.AssetPipeline
{
    /// <summary>The "BlackglassStudio" AssetDatabase label marks assets this pipeline created, so re-imports may update them silently.</summary>
    public static class OwnershipLabel
    {
        public static bool IsOwned(string assetPath)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            return asset != null && Array.IndexOf(AssetDatabase.GetLabels(asset), ContractInfo.OwnershipLabel) >= 0;
        }

        public static void Mark(string assetPath)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null) return;
            var labels = AssetDatabase.GetLabels(asset);
            if (Array.IndexOf(labels, ContractInfo.OwnershipLabel) >= 0) return;
            var updated = new string[labels.Length + 1];
            Array.Copy(labels, updated, labels.Length);
            updated[labels.Length] = ContractInfo.OwnershipLabel;
            AssetDatabase.SetLabels(asset, updated);
        }
    }
}
```

`Importers/SourceCopier.cs`:

```csharp
using System.IO;
using UnityEditor;

namespace Blackglass.AssetPipeline
{
    public static class SourceCopier
    {
        /// <summary>False (with errors added) when a target already exists, is not Studio-owned, and overwrite was not allowed.</summary>
        public static bool CheckConflicts(ImportItem item, ItemResult result)
        {
            var ok = true;
            foreach (var target in new[] { AssetNaming.ModelPath(item), AssetNaming.PrefabPath(item) })
            {
                if (target == null || !File.Exists(AssetPaths.Full(target))) continue;
                if (OwnershipLabel.IsOwned(target) || item.allowOverwrite) continue;
                result.errors.Add(target + " already exists and was not created by Asset Studio. Tick Allow overwrite to replace it.");
                ok = false;
            }
            return ok;
        }

        /// <summary>Copies the staged source (and a sibling .fbm texture folder) into the destination and imports it synchronously. Returns the model's asset path.</summary>
        public static string CopyAndImport(ImportItem item, ItemResult result)
        {
            var folder = PathRules.Normalize(item.destinationFolder);
            AssetPaths.EnsureFolder(folder);
            var modelPath = AssetNaming.ModelPath(item);
            var target = AssetPaths.Full(modelPath);

            if (Path.GetFullPath(item.sourcePath) == Path.GetFullPath(target))
                throw new IOException("The source file is the destination file: " + modelPath);

            var existed = File.Exists(target);
            File.Copy(item.sourcePath, target, true);

            var fbm = Path.Combine(Path.GetDirectoryName(item.sourcePath), Path.GetFileNameWithoutExtension(item.sourcePath) + ".fbm");
            if (Directory.Exists(fbm))
                CopyDirectory(fbm, Path.Combine(AssetPaths.Full(folder), Path.GetFileName(fbm)));

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(modelPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            result.importedAssets.Add(modelPath);
            if (existed) result.changedAssets.Add(modelPath);
            return modelPath;
        }

        static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }
}
```

`Entry/AssetPipelineRunner.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Blackglass.AssetPipeline
{
    /// <summary>
    /// Batch/menu entry. Reads the manifest Asset Studio wrote, imports every item with Unity's own APIs, and writes a
    /// structured result (even when something fails). One item failing never stops the others.
    /// Command line: -executeMethod Blackglass.AssetPipeline.AssetPipelineRunner.RunFromCommandLine -blackglassManifest m.json -blackglassResult r.json
    /// </summary>
    public static class AssetPipelineRunner
    {
        public const string ManifestArg = "-blackglassManifest";
        public const string ResultArg = "-blackglassResult";
        const string LogPrefix = "[AssetPipeline] ";

        [MenuItem("Blackglass/Asset Pipeline/Run Manifest...")]
        static void RunFromMenu()
        {
            var path = EditorUtility.OpenFilePanel("Choose an Asset Studio manifest", "", "json");
            if (string.IsNullOrEmpty(path)) return;
            var result = Run(path, null);
            EditorUtility.DisplayDialog("Asset Pipeline", result.success ? "Import finished." : "Import finished with errors. See the Console and the result file.", "OK");
        }

        public static void RunFromCommandLine()
        {
            var code = 1;
            try
            {
                var result = Run(ArgValue(ManifestArg), ArgValue(ResultArg));
                code = result.success ? 0 : 1;
            }
            catch (Exception e)
            {
                Debug.LogError(LogPrefix + "ERROR " + e);
            }
            EditorApplication.Exit(code);
        }

        public static ImportResult Run(string manifestPath, string resultPathOverride)
        {
            var result = new ImportResult { unityVersion = Application.unityVersion };
            var resultPath = resultPathOverride;
            try
            {
                if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
                {
                    Fatal(result, "The manifest file was not found: " + manifestPath);
                    return Finish(result, resultPath);
                }
                var manifest = JsonUtility.FromJson<ImportManifest>(File.ReadAllText(manifestPath));
                result.runId = manifest.runId;
                if (string.IsNullOrEmpty(resultPath)) resultPath = manifest.resultPath;
                if (manifest.schemaVersion != ContractInfo.SchemaVersion)
                {
                    Fatal(result, "Unsupported manifest schema version " + manifest.schemaVersion + " (this Unity importer reads version " + ContractInfo.SchemaVersion + ").");
                    return Finish(result, resultPath);
                }
                foreach (var item in manifest.items)
                    result.items.Add(ImportOne(item));
            }
            catch (Exception e)
            {
                Fatal(result, e.Message);
                Debug.LogError(LogPrefix + "ERROR " + e);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return Finish(result, resultPath);
        }

        /// <summary>Validates and imports one item; never throws (an exception becomes an item error). Public so tests can drive the importers.</summary>
        public static ItemResult ImportOne(ImportItem item)
        {
            var r = new ItemResult { id = item.id };
            try
            {
                var problem = PathRules.ValidateAssetName(item.name) ?? PathRules.ValidateDestination(item.destinationFolder);
                if (problem != null)
                    r.errors.Add(problem);
                else
                    switch (item.profile)
                    {
                        // One case per profile is added by the importer tasks.
                        default:
                            r.errors.Add("Unknown or not yet supported import profile '" + item.profile + "'.");
                            break;
                    }
            }
            catch (Exception e)
            {
                r.errors.Add(e.Message);
                Debug.LogError(LogPrefix + "ERROR importing '" + item.name + "': " + e);
            }
            r.success = r.errors.Count == 0;
            if (!r.success)
                foreach (var error in r.errors) Debug.LogError(LogPrefix + "ERROR " + item.name + ": " + error);
            return r;
        }

        static void Fatal(ImportResult result, string message)
        {
            result.errors.Add(message);
            Debug.LogError(LogPrefix + "ERROR " + message);
        }

        static ImportResult Finish(ImportResult result, string resultPath)
        {
            result.success = result.errors.Count == 0 && result.items.All(i => i.success);
            if (!string.IsNullOrEmpty(resultPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
                File.WriteAllText(resultPath, JsonUtility.ToJson(result, true));
            }
            return result;
        }

        static string ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
```

- [ ] **Step 5: Run the new tests.** `Tools/run-tests.sh EditMode "Blackglass.Tests.AssetPipeline"` → all pass, `EXIT=0`. (If `Path` resolves to `ScratchFolder.Path` or a namespace, use `System.IO.Path` as the tests above do.)

- [ ] **Step 6: Full EditMode suite.** `Tools/run-tests.sh EditMode`; totals = Step 1 baseline plus the new tests, zero failures.

- [ ] **Step 7: Commit** everything under `Assets/_Project/Editor/AssetPipeline` and `Assets/_Project/Tests/EditMode/AssetPipeline` with their `.meta` files (folders included), plus the test asmdef change. Do **not** add the owner's pre-existing modified `Assets/Settings/Mobile_RPAsset.asset` and `ProjectSettings/URPProjectSettings.asset`:

```bash
git add Assets/_Project/Editor/AssetPipeline Assets/_Project/Editor/AssetPipeline.meta Assets/_Project/Tests/EditMode/AssetPipeline Assets/_Project/Tests/EditMode/AssetPipeline.meta Assets/_Project/Tests/EditMode/Blackglass.Tests.EditMode.asmdef
git status --short
git commit -m "Asset Studio: Unity pipeline foundation (runner, ownership label, source copier) with contract tests

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```
`git status --short` before committing must show nothing unexpected under `Assets/` besides the two owner files.

---

## Task 9: End-to-end smoke through the real launcher (Milestone B complete)

**Files:**
- Create: `tests/Core.Tests/UnityE2E/E2EFixture.cs`, `tests/Core.Tests/UnityE2E/PipelineSmokeTests.cs`

The E2E tests run the real `ImportRunner` and `UnityProcess` against the real project and real Unity. They are **opt-in**: each test returns immediately unless the environment variable `BLACKGLASS_E2E=1` is set, because they need the Unity Editor closed and take about a minute each.

**Interfaces:**
- Produces: `E2EFixture.Enabled`, `.Project`, `.Settings()`, `.Source(string)`, `.Item(...)`, `.Run(...)`, `.CleanScratch()` (reused by Tasks 10-12).

- [ ] **Step 1: Write the fixture and the test.**

`tests/Core.Tests/UnityE2E/E2EFixture.cs`:

```csharp
using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio.Tests;

/// <summary>Runs the real runner against the real project. Opt-in: set BLACKGLASS_E2E=1 and close the Unity Editor first.</summary>
internal static class E2EFixture
{
    public const string Scratch = "Assets/_AssetStudioScratch";

    public static bool Enabled => Environment.GetEnvironmentVariable("BLACKGLASS_E2E") == "1";

    public static string Project => ProjectLocator.FindFrom(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("The tests must run from inside the Blackglass repository.");

    public static AppSettings Settings() => new()
    {
        ProjectPath = Project,
        StagingFolder = Path.Combine(Path.GetTempPath(), "bgas-e2e-runs"),
        DefaultSharedAvatarPath = "",
    };

    /// <summary>One of the existing Darius art files, used as importer input.</summary>
    public static string Source(string relativeToDarius) =>
        Path.Combine(Project, "Assets", "Art", "Characters", "Darius", relativeToDarius.Replace('/', Path.DirectorySeparatorChar));

    public static AssetItem Item(string sourcePath, string profileId, string name, AppSettings settings, Action<ImportItem>? tweak = null)
    {
        var item = AssetItemFactory.Create(sourcePath, null, settings);
        ImportProfiles.Apply(item.Import, ImportProfiles.Get(profileId), settings);
        item.ProfileId = profileId;
        item.Import.name = name;
        item.Import.destinationFolder = Scratch;
        tweak?.Invoke(item.Import);
        return item;
    }

    public static Task<RunOutcome> Run(AppSettings settings, params AssetItem[] items) =>
        new ImportRunner(settings, new UnityProcess()).RunAsync(items);

    public static void CleanScratch()
    {
        var folder = Path.Combine(Project, "Assets", "_AssetStudioScratch");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        if (File.Exists(folder + ".meta")) File.Delete(folder + ".meta");
    }
}
```

`tests/Core.Tests/UnityE2E/PipelineSmokeTests.cs`:

```csharp
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class PipelineSmokeTests
{
    [Fact]
    public async Task Unity_reads_the_manifest_and_returns_a_structured_result_for_every_item()
    {
        if (!E2EFixture.Enabled) return;
        var settings = E2EFixture.Settings();
        var item = E2EFixture.Item(E2EFixture.Source("Models/Darius Stand Idle.fbx"), ImportProfiles.Prop, "Smoke_Prop", settings);
        try
        {
            var outcome = await E2EFixture.Run(settings, item);

            Assert.Null(outcome.FatalError);
            Assert.NotNull(outcome.Result);
            Assert.Equal(outcome.RunId, outcome.Result!.runId);
            Assert.StartsWith(ProjectLocator.ReadEditorVersion(settings.ProjectPath)!, outcome.Result.unityVersion);
            Assert.Equal(item.Id, outcome.Result.items.Single().id);
            Assert.True(File.Exists(outcome.LogPath));
        }
        finally { E2EFixture.CleanScratch(); }
    }
}
```

- [ ] **Step 2: Normal run stays green and skips.** `dotnet test Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln` passes (the E2E test returns early).

- [ ] **Step 3: Real run.** Close the Unity Editor, then (bash):

```bash
BLACKGLASS_E2E=1 dotnet test Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln --filter "FullyQualifiedName~PipelineSmokeTests"
```
Expected: pass in roughly a minute. If it fails with `without a usable result`, read the message: it contains the log path and the last errors. Common causes: Editor still open (the message says so), a compile error (fix it), a wrong entry-method name (the Unity log says `executeMethod method 'X' could not be found`).

- [ ] **Step 4: Verify no litter.** `git status --short` shows only your new files (no `Assets/_AssetStudioScratch`, no manifests under the project); the run folder under `%TEMP%\bgas-e2e-runs\<runId>\` holds `manifest.json`, `result.json`, `unity.log`. At this stage the item's own result is a failure ("Unknown or not yet supported import profile"); that is expected until Task 12, and the smoke test deliberately does not assert item success.

- [ ] **Step 5: Commit** ("Asset Studio: opt-in end-to-end smoke test through the real Unity launcher").

---

## Task 10: Humanoid character importer with visual height normalisation (Milestone C)

**Files:**
- Create: `Assets/_Project/Editor/AssetPipeline/Importers/ModelMeasure.cs`, `ModelPrefabWriter.cs`, `CharacterImporter.cs`
- Modify: `Assets/_Project/Editor/AssetPipeline/Entry/AssetPipelineRunner.cs` (one `case`)
- Create tests: `Assets/_Project/Tests/EditMode/AssetPipeline/ImporterTestSupport.cs`, `CharacterImporterTests.cs`; `tests/Core.Tests/UnityE2E/CharacterE2ETests.cs`; add `Hash` to `E2EFixture`

**Interfaces:**
- Consumes: `SourceCopier`, `OwnershipLabel`, `AssetNaming`, `HeightScale`, `AssetPipelineRunner.ImportOne`.
- Produces: `ModelMeasure.Measure(GameObject) -> Bounds?`; `ModelPrefabWriter.Write(string prefabPath, string rootName, GameObject modelAsset, string childName, float scale, Avatar avatar) -> GameObject` (the saved prefab asset; used again by Tasks 11-12); `CharacterImporter.Import(ImportItem, ItemResult)`, `CharacterImporter.TryResolveScale(float measuredHeight, CharacterSettings, ItemResult, out float scale) -> bool`.

The prefab shape (decision 7 of the spec): `<Name>_Visual` (root, identity scale, no components, no colliders) with one child `ImportedCharacter` (the model instance) that carries the correction scale and the `Animator` (Avatar assigned, no root motion). Nothing here reads or writes a gameplay root.

- [ ] **Step 1: Test support and tests first.**

`ImporterTestSupport.cs`:

```csharp
#if UNITY_EDITOR
using Blackglass.AssetPipeline;

namespace Blackglass.Tests.AssetPipeline
{
    internal static class ImporterTestSupport
    {
        public static ImportItem Item(string profile, string sourcePath, string name, string destination = ScratchFolder.Path)
        {
            return new ImportItem { id = name, profile = profile, name = name, sourcePath = sourcePath, destinationFolder = destination };
        }

        public static string Errors(ItemResult r) => string.Join("\n", r.errors);
    }
}
#endif
```

`CharacterImporterTests.cs`:

```csharp
#if UNITY_EDITOR
using System;
using System.IO;
using Blackglass.AssetPipeline;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests.AssetPipeline
{
    public class CharacterImporterTests
    {
        const string Source = "Models/Darius Stand Idle.fbx";

        [TearDown]
        public void TearDown() => ScratchFolder.Clean();

        static ImportItem Character(string name, Action<CharacterSettings> tweak = null)
        {
            var item = ImporterTestSupport.Item(ProfileIds.HumanoidCharacter, ScratchFolder.Darius(Source), name);
            item.character.targetHeight = 1.88f;
            tweak?.Invoke(item.character);
            return item;
        }

        static string Prefab(string name) => ScratchFolder.Path + "/" + name + "_Visual.prefab";

        [Test]
        public void NormalisesTheVisualHeightAndLeavesTheRootAlone()
        {
            var r = AssetPipelineRunner.ImportOne(Character("TestChar"));
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            Assert.That(r.measuredHeight, Is.InRange(1.70f, 1.85f), "Darius is about 1.77 m as imported");
            Assert.AreEqual(ScaleSources.Auto, r.scaleSource);
            Assert.AreEqual(1.88f / r.measuredHeight, r.appliedScale, 1e-4f);
            Assert.AreEqual(1.88f, r.targetHeight);
            CollectionAssert.Contains(r.createdPrefabs, Prefab("TestChar"));

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Prefab("TestChar"));
            Assert.AreEqual(Vector3.one, prefab.transform.localScale, "the visual prefab root is never scaled");
            var child = prefab.transform.Find("ImportedCharacter");
            Assert.IsNotNull(child);
            Assert.AreEqual(r.appliedScale, child.localScale.x, 1e-4f);
            Assert.AreEqual(child.localScale.x, child.localScale.y, 1e-6f);
            Assert.AreEqual(child.localScale.x, child.localScale.z, 1e-6f);
            Assert.IsEmpty(prefab.GetComponentsInChildren<Collider>(true), "visual prefabs carry no colliders");
            var animator = child.GetComponent<Animator>();
            Assert.IsNotNull(animator);
            Assert.IsNotNull(animator.avatar);
            Assert.IsTrue(animator.avatar.isHuman);
            Assert.IsFalse(animator.applyRootMotion);

            // The posed result must be the target height. If this fails with a value near target * scale, BakeMesh(.., true)
            // already includes the transform scale: switch ModelMeasure to BakeMesh(.., false).
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try { Assert.AreEqual(1.88f, ModelMeasure.Measure(instance).Value.size.y, 0.03f); }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }

        [Test]
        public void ManualScaleOverrideWinsOverTheMeasurement()
        {
            var r = AssetPipelineRunner.ImportOne(Character("TestChar", c => c.scaleOverride = 2f));
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            Assert.AreEqual(ScaleSources.Manual, r.scaleSource);
            Assert.AreEqual(2f, r.appliedScale);
            var child = AssetDatabase.LoadAssetAtPath<GameObject>(Prefab("TestChar")).transform.Find("ImportedCharacter");
            Assert.AreEqual(2f, child.localScale.x, 1e-5f);
        }

        [Test]
        public void NormaliseOffKeepsTheModelAtItsImportedSize()
        {
            var r = AssetPipelineRunner.ImportOne(Character("TestChar", c => c.normalizeHeight = false));
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            Assert.AreEqual(ScaleSources.None, r.scaleSource);
            var child = AssetDatabase.LoadAssetAtPath<GameObject>(Prefab("TestChar")).transform.Find("ImportedCharacter");
            Assert.AreEqual(Vector3.one, child.localScale);
        }

        [Test]
        public void NoPrefabWhenNotRequested()
        {
            var r = AssetPipelineRunner.ImportOne(Character("TestChar", c => c.generatePrefab = false));
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            Assert.IsEmpty(r.createdPrefabs);
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(Prefab("TestChar")));
        }

        [Test]
        public void ImportedModelAndPrefabAreLabelledAsStudioOwned()
        {
            AssetPipelineRunner.ImportOne(Character("TestChar"));
            Assert.IsTrue(OwnershipLabel.IsOwned(ScratchFolder.Path + "/TestChar.fbx"));
            Assert.IsTrue(OwnershipLabel.IsOwned(Prefab("TestChar")));
        }

        [Test]
        public void ReimportUpdatesInPlaceKeepingTheGuids()
        {
            AssetPipelineRunner.ImportOne(Character("TestChar"));
            var modelGuid = AssetDatabase.AssetPathToGUID(ScratchFolder.Path + "/TestChar.fbx");
            var prefabGuid = AssetDatabase.AssetPathToGUID(Prefab("TestChar"));

            var again = AssetPipelineRunner.ImportOne(Character("TestChar", c => c.scaleOverride = 1.5f));

            Assert.IsTrue(again.success, ImporterTestSupport.Errors(again));
            Assert.AreEqual(modelGuid, AssetDatabase.AssetPathToGUID(ScratchFolder.Path + "/TestChar.fbx"));
            Assert.AreEqual(prefabGuid, AssetDatabase.AssetPathToGUID(Prefab("TestChar")));
            CollectionAssert.Contains(again.changedAssets, ScratchFolder.Path + "/TestChar.fbx");
            Assert.AreEqual(1.5f, AssetDatabase.LoadAssetAtPath<GameObject>(Prefab("TestChar")).transform.Find("ImportedCharacter").localScale.x, 1e-5f);
        }

        [Test]
        public void ExistingUnlabelledAssetIsRefusedUnlessOverwriteIsAllowed()
        {
            AssetPaths.EnsureFolder(ScratchFolder.Path);
            File.Copy(ScratchFolder.Darius(Source), AssetPaths.Full(ScratchFolder.Path + "/TestChar.fbx"));
            AssetDatabase.Refresh();

            var refused = AssetPipelineRunner.ImportOne(Character("TestChar"));
            Assert.IsFalse(refused.success);
            StringAssert.Contains("already exists", ImporterTestSupport.Errors(refused));
            Assert.IsFalse(OwnershipLabel.IsOwned(ScratchFolder.Path + "/TestChar.fbx"), "a refused item must not claim the asset");

            var item = Character("TestChar");
            item.allowOverwrite = true;
            var ok = AssetPipelineRunner.ImportOne(item);
            Assert.IsTrue(ok.success, ImporterTestSupport.Errors(ok));
        }

        [Test]
        public void ARigidMeshThatCannotBeHumanoidFailsWithoutAPrefab()
        {
            var item = ImporterTestSupport.Item(ProfileIds.HumanoidCharacter, ScratchFolder.Darius("Weapons/Rifle.fbx"), "NotAPerson");
            var r = AssetPipelineRunner.ImportOne(item);
            Assert.IsFalse(r.success);
            StringAssert.Contains("Humanoid", ImporterTestSupport.Errors(r));
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(Prefab("NotAPerson")), "no prefab after a failed Humanoid mapping");
            Assert.IsFalse(r.avatar.valid);
        }

        [Test]
        public void ModelWithNoGeometryGivesAnErrorNeverANanScale()
        {
            Assert.IsNull(ModelMeasure.Measure(new GameObject("empty")));
            var r = new ItemResult();
            Assert.IsFalse(CharacterImporter.TryResolveScale(0f, new CharacterSettings(), r, out _));
            Assert.IsNotEmpty(r.errors);
            var manual = new ItemResult();
            Assert.IsTrue(CharacterImporter.TryResolveScale(0f, new CharacterSettings { scaleOverride = 0.5f }, manual, out var scale));
            Assert.AreEqual(0.5f, scale);
        }

        [Test]
        public void UnusualScalesAreWarnedAbout()
        {
            var r = new ItemResult();
            Assert.IsTrue(CharacterImporter.TryResolveScale(0.05f, new CharacterSettings(), r, out _)); // would scale x37
            Assert.IsNotEmpty(r.warnings);
        }

        [Test]
        public void ObjIsRefusedForCharacters()
        {
            var item = ImporterTestSupport.Item(ProfileIds.HumanoidCharacter, "C:/x/body.obj", "Body");
            var r = AssetPipelineRunner.ImportOne(item);
            Assert.IsFalse(r.success);
            StringAssert.Contains("FBX", ImporterTestSupport.Errors(r));
        }
    }
}
#endif
```

Add the `Source` constant path check: `ScratchFolder.Darius(Source)` must exist; the test fails clearly if the art moved.

- [ ] **Step 2: Run to verify failure** (`Tools/run-tests.sh EditMode "Blackglass.Tests.AssetPipeline.CharacterImporterTests"`): compile errors for the missing types.

- [ ] **Step 3: Implement.**

`Importers/ModelMeasure.cs`:

```csharp
using UnityEngine;

namespace Blackglass.AssetPipeline
{
    public static class ModelMeasure
    {
        /// <summary>
        /// World-space bounds of all geometry under the instance as currently posed, or null when there is none.
        /// SkinnedMeshRenderer.bounds is a padded rest-pose box, so skinned meshes are baked and their vertices transformed by
        /// the renderer (the same method as the Darius tests). Measure an instance at the origin with identity scale.
        /// </summary>
        public static Bounds? Measure(GameObject instance)
        {
            Bounds? total = null;
            foreach (var skinned in instance.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (skinned.sharedMesh == null) continue;
                var baked = new Mesh();
                skinned.BakeMesh(baked, true);
                foreach (var vertex in baked.vertices)
                    Grow(ref total, skinned.transform.TransformPoint(vertex));
                Object.DestroyImmediate(baked);
            }
            foreach (var filter in instance.GetComponentsInChildren<MeshFilter>())
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null || filter.sharedMesh == null) continue;
                var b = renderer.bounds;
                Grow(ref total, b.min);
                Grow(ref total, b.max);
            }
            return total;
        }

        static void Grow(ref Bounds? total, Vector3 point)
        {
            if (total == null)
            {
                total = new Bounds(point, Vector3.zero);
                return;
            }
            var b = total.Value;
            b.Encapsulate(point);
            total = b;
        }
    }
}
```

`Importers/ModelPrefabWriter.cs`:

```csharp
using UnityEditor;
using UnityEngine;

namespace Blackglass.AssetPipeline
{
    public static class ModelPrefabWriter
    {
        /// <summary>
        /// Writes (or updates in place, keeping the GUID) a prefab: an identity root named rootName with the model as one child named
        /// childName carrying the scale. When an avatar is given, the child's Animator uses it with no root motion.
        /// The model importer is configured without colliders, so the prefab has none; callers' tests assert it.
        /// </summary>
        public static GameObject Write(string prefabPath, string rootName, GameObject modelAsset, string childName, float scale, Avatar avatar)
        {
            var root = new GameObject(rootName);
            try
            {
                var child = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset, root.transform);
                child.name = childName;
                child.transform.localPosition = Vector3.zero;
                child.transform.localRotation = Quaternion.identity;
                child.transform.localScale = Vector3.one * scale;
                if (avatar != null)
                {
                    var animator = child.GetComponent<Animator>();
                    if (animator == null) animator = child.AddComponent<Animator>();
                    animator.avatar = avatar;
                    animator.applyRootMotion = false;
                }
                return PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
```

`Importers/CharacterImporter.cs`:

```csharp
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Blackglass.AssetPipeline
{
    public static class CharacterImporter
    {
        public static void Import(ImportItem item, ItemResult r)
        {
            var c = item.character;
            r.targetHeight = c.targetHeight;

            if (Path.GetExtension(item.sourcePath).ToLowerInvariant() != ".fbx")
            {
                r.errors.Add("Humanoid characters must be FBX files.");
                return;
            }
            if (c.rigHumanoid && !c.createAvatar)
            {
                r.errors.Add("A Humanoid rig needs an Avatar: enable Create Avatar or untick Humanoid.");
                return;
            }
            if (c.scaleOverride < 0f || float.IsNaN(c.scaleOverride))
            {
                r.errors.Add("The manual scale must be positive (or 0 for none).");
                return;
            }
            if (!SourceCopier.CheckConflicts(item, r)) return;

            var modelPath = SourceCopier.CopyAndImport(item, r);
            Configure(modelPath, c);
            OwnershipLabel.Mark(modelPath);

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            var avatar = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>().FirstOrDefault();
            r.avatar = DescribeAvatar(avatar, c);
            if (c.rigHumanoid && !(r.avatar.valid && r.avatar.isHuman))
            {
                r.errors.Add("The model could not be set up as a Humanoid: " + r.avatar.message);
                return;
            }

            var measured = MeasureInstance(model, r);
            if (r.errors.Count > 0) return;
            if (!TryResolveScale(measured, c, r, out var scale)) return;

            if (!c.generatePrefab) return;
            var prefabPath = AssetNaming.PrefabPath(item);
            var existed = File.Exists(AssetPaths.Full(prefabPath));
            ModelPrefabWriter.Write(prefabPath, item.name + "_Visual", model, "ImportedCharacter", scale, avatar);
            OwnershipLabel.Mark(prefabPath);
            (existed ? r.changedAssets : r.createdPrefabs).Add(prefabPath);
        }

        /// <summary>The visual scale for a measured height, applied to the visual child only. Pure apart from filling the result.</summary>
        public static bool TryResolveScale(float measuredHeight, CharacterSettings c, ItemResult r, out float scale)
        {
            var s = HeightScale.Compute(measuredHeight, c.targetHeight, c.normalizeHeight, c.scaleOverride);
            scale = s.scale;
            r.appliedScale = s.scale;
            r.scaleSource = s.source;
            if (!s.Ok)
            {
                r.errors.Add(s.error);
                return false;
            }
            if (s.scale < 0.1f || s.scale > 10f)
                r.warnings.Add("The visual scale " + s.scale.ToString("0.####") + " is unusual. Check the model's units and for stray geometry (hair, coat tails, floating parts) inflating the measured height, or set a manual scale.");
            else if (s.source == ScaleSources.Auto && (s.scale < 0.66f || s.scale > 1.5f))
                r.warnings.Add("The model needed a large correction (x" + s.scale.ToString("0.###") + "). If that is not expected, check for stray geometry or set a manual scale.");
            return true;
        }

        static void Configure(string modelPath, CharacterSettings c)
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            importer.animationType = c.rigHumanoid ? ModelImporterAnimationType.Human : ModelImporterAnimationType.Generic;
            importer.avatarSetup = c.createAvatar ? ModelImporterAvatarSetup.CreateFromThisModel : ModelImporterAvatarSetup.NoAvatar;
            importer.importAnimation = false;
            importer.addCollider = false;
            importer.materialLocation = c.materialMode == MaterialModes.External
                ? ModelImporterMaterialLocation.External
                : ModelImporterMaterialLocation.InPrefab;
            importer.SaveAndReimport();
        }

        static AvatarReport DescribeAvatar(Avatar avatar, CharacterSettings c)
        {
            if (!c.rigHumanoid)
                return new AvatarReport { valid = avatar != null && avatar.isValid, isHuman = false, message = "Generic rig." };
            if (avatar == null)
                return new AvatarReport { message = "Unity did not create an Avatar. The model needs a skeleton with a skinned mesh." };
            if (!avatar.isValid)
                return new AvatarReport { message = "The Humanoid bone mapping is invalid. In Unity, select the model > Rig tab > Configure to fix the mapping, or correct the skeleton (bone names, T-pose) in Blender." };
            if (!avatar.isHuman)
                return new AvatarReport { valid = true, message = "The Avatar is not humanoid." };
            return new AvatarReport { valid = true, isHuman = true };
        }

        /// <summary>Instantiates the model at the origin, fills dimensions and checks renderers. Returns the measured height (0 when unusable).</summary>
        static float MeasureInstance(GameObject model, ItemResult r)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            try
            {
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                if (instance.GetComponentsInChildren<Renderer>().Length == 0)
                {
                    r.errors.Add("The imported model contains no renderers (no meshes), so there is nothing to display.");
                    return 0f;
                }
                var bounds = ModelMeasure.Measure(instance);
                if (bounds == null) return 0f;
                var b = bounds.Value;
                r.dimensions = new Size3 { x = b.size.x, y = b.size.y, z = b.size.z };
                r.measuredHeight = b.size.y;
                if (Mathf.Abs(b.min.y) > 0.1f)
                    r.warnings.Add("The model's lowest point is " + b.min.y.ToString("0.###") + " m from its origin. Its feet should stand on the origin (check the pivot in Blender).");
                return b.size.y;
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }
    }
}
```

In `AssetPipelineRunner.ImportOne`, add the case before `default`:

```csharp
                        case ProfileIds.HumanoidCharacter:
                            CharacterImporter.Import(item, r);
                            break;
```

- [ ] **Step 4: Run the character tests.** `Tools/run-tests.sh EditMode "Blackglass.Tests.AssetPipeline"` → pass. Expected problem areas and what to do:
  - `NormalisesTheVisualHeight…` height assertion off by about the scale: apply the `BakeMesh(.., false)` switch described in the test comment, rerun.
  - `ARigidMeshThatCannotBeHumanoid…` passes only if Unity yields no valid Humanoid Avatar for the rifle; if Unity does produce a valid avatar for it (unlikely), substitute any other non-skinned FBX from the project and keep the assertion.
  - Label read fails (`IsOwned` false): the label is written by `SetLabels` into the `.meta`; make sure `AssetDatabase.SaveAssets()` runs (the runner does it at the end; in tests call `AssetDatabase.SaveAssets()` before asserting).

- [ ] **Step 5: Opt-in real-pipeline test** `tests/Core.Tests/UnityE2E/CharacterE2ETests.cs`. First add to `E2EFixture`:

```csharp
    public static string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
```

```csharp
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class CharacterE2ETests
{
    [Fact]
    public async Task Character_goes_through_the_real_pipeline_and_the_source_is_untouched()
    {
        if (!E2EFixture.Enabled) return;
        var settings = E2EFixture.Settings();
        var source = E2EFixture.Source("Models/Darius Stand Idle.fbx");
        var before = E2EFixture.Hash(source);
        var item = E2EFixture.Item(source, ImportProfiles.Character, "E2E_Char", settings, i => i.character.targetHeight = 1.85f);
        try
        {
            var outcome = await E2EFixture.Run(settings, item);

            Assert.Null(outcome.FatalError);
            var r = outcome.Result!.items.Single();
            Assert.True(r.success, string.Join("; ", r.errors));
            Assert.InRange(r.measuredHeight, 1.70f, 1.85f);
            Assert.Equal(1.85f / r.measuredHeight, r.appliedScale, 3);
            Assert.True(r.avatar.valid && r.avatar.isHuman);
            Assert.Contains("Assets/_AssetStudioScratch/E2E_Char_Visual.prefab", r.createdPrefabs);
            Assert.Equal(before, E2EFixture.Hash(source));
            Assert.DoesNotContain(outcome.ChangedFiles, f => f.EndsWith("Darius Stand Idle.fbx") || f.EndsWith("Darius Stand Idle.fbx.meta"));
        }
        finally { E2EFixture.CleanScratch(); }
    }
}
```
(`Assert.Equal(float, float, 3)`: if the installed xunit resolves this to a tolerance overload, use `Assert.InRange(r.appliedScale, expected - 0.001f, expected + 0.001f)` instead.)

Run with Unity closed: `BLACKGLASS_E2E=1 dotnet test Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln --filter "FullyQualifiedName~CharacterE2ETests"`. Expected pass; then `git status --short` shows no scratch folder and no change to Darius files.

- [ ] **Step 6: Full EditMode suite** (`Tools/run-tests.sh EditMode`) still green (no changes to gameplay code, but the new asmdef must not break compilation of the rest).
- [ ] **Step 7: Commit** ("Asset Studio: humanoid character importer with visual height normalisation and visual prefab"). Include the `.meta` files of the new Unity files.

---

## Task 11: Humanoid animation importer and shared Avatar handling (Milestone D)

**Files:**
- Create: `Assets/_Project/Editor/AssetPipeline/Importers/AnimationImporter.cs`
- Modify: `Entry/AssetPipelineRunner.cs` (one `case`)
- Create tests: `Assets/_Project/Tests/EditMode/AssetPipeline/AnimationImporterTests.cs`; `tests/Core.Tests/UnityE2E/AnimationE2ETests.cs`

**Interfaces:**
- Consumes: `SourceCopier`, `OwnershipLabel`, `AssetNaming`, `ImporterTestSupport`, `ScratchFolder`.
- Produces: `AnimationImporter.Import(ImportItem, ItemResult)`, `AnimationImporter.ResolveLoop(AnimationSettings) -> bool`.

Behaviour: the FBX is imported as Humanoid with **Copy From Other Avatar** (the shared Avatar), `importAnimation` on, materials off, and `clipAnimations` set to exactly one clip named `clipName` with loop and the three root "bake into pose" flags from the manifest. No `.anim` copies are made (decision 034). Unity has no "skip meshes" switch for a ModelImporter, so a downloaded animation FBX that includes a skin keeps its mesh inside the animation asset; downloading "Without Skin" from Mixamo avoids that (documented in Task 13).

- [ ] **Step 1: Tests first.** `AnimationImporterTests.cs`:

```csharp
#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using Blackglass.AssetPipeline;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests.AssetPipeline
{
    public class AnimationImporterTests
    {
        const string CharPath = ScratchFolder.Path + "/AnimChar.fbx";

        [SetUp]
        public void SetUp()
        {
            // A Humanoid model in the scratch folder is the shared Avatar the clips copy.
            var character = ImporterTestSupport.Item(ProfileIds.HumanoidCharacter, ScratchFolder.Darius("Models/Darius Stand Idle.fbx"), "AnimChar");
            character.character.generatePrefab = false;
            var r = AssetPipelineRunner.ImportOne(character);
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
        }

        [TearDown]
        public void TearDown() => ScratchFolder.Clean();

        static ImportItem Clip(string file, string name, string category, Action<AnimationSettings> tweak = null)
        {
            var item = ImporterTestSupport.Item(ProfileIds.HumanoidAnimation, ScratchFolder.Darius("Animations/" + file), name);
            item.animation.category = category;
            item.animation.clipName = name;
            item.animation.sharedAvatarPath = CharPath;
            tweak?.Invoke(item.animation);
            return item;
        }

        static AnimationClip LoadClip(string assetPath, string clipName) =>
            AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<AnimationClip>().First(c => c.name == clipName);

        [Test]
        public void LocomotionLoopsAndIsBakedInPlace()
        {
            var r = AssetPipelineRunner.ImportOne(Clip("Darius Walk Gun.fbx", "Walk", AnimationCategories.Locomotion,
                a => { a.loop = LoopModes.Yes; a.bakeRotation = true; a.bakeHeight = true; a.bakePositionXZ = true; }));
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));

            var path = ScratchFolder.Path + "/Walk.fbx";
            var clip = LoadClip(path, "Walk");
            Assert.Greater(clip.length, 0.2f);
            Assert.IsTrue(clip.isHumanMotion);
            Assert.IsTrue(AnimationUtility.GetAnimationClipSettings(clip).loopTime);
            var configured = ((ModelImporter)AssetImporter.GetAtPath(path)).clipAnimations.Single();
            Assert.AreEqual("Walk", configured.name);
            Assert.IsTrue(configured.lockRootRotation);
            Assert.IsTrue(configured.lockRootHeightY);
            Assert.IsTrue(configured.lockRootPositionXZ);

            Assert.AreEqual(1, r.clips.Count);
            Assert.AreEqual("Walk", r.clips[0].name);
            Assert.IsTrue(r.clips[0].loop);
            Assert.AreEqual(clip.length, r.clips[0].duration, 1e-4f);
            Assert.IsTrue(r.avatar.valid && r.avatar.isHuman);
            Assert.IsEmpty(r.createdPrefabs);
        }

        [Test]
        public void CombatDoesNotLoopAndBakesOnlyHeight()
        {
            var r = AssetPipelineRunner.ImportOne(Clip("Darius Fire.fbx", "Fire", AnimationCategories.Combat,
                a => { a.loop = LoopModes.No; a.bakeHeight = true; }));
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            var path = ScratchFolder.Path + "/Fire.fbx";
            Assert.IsFalse(AnimationUtility.GetAnimationClipSettings(LoadClip(path, "Fire")).loopTime);
            var configured = ((ModelImporter)AssetImporter.GetAtPath(path)).clipAnimations.Single();
            Assert.IsFalse(configured.lockRootRotation);
            Assert.IsTrue(configured.lockRootHeightY);
            Assert.IsFalse(configured.lockRootPositionXZ);
        }

        [Test]
        public void DeathBakesHeightAndHorizontalPosition()
        {
            var r = AssetPipelineRunner.ImportOne(Clip("Darius Death.fbx", "Death", AnimationCategories.Death,
                a => { a.loop = LoopModes.No; a.bakeHeight = true; a.bakePositionXZ = true; }));
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            var configured = ((ModelImporter)AssetImporter.GetAtPath(ScratchFolder.Path + "/Death.fbx")).clipAnimations.Single();
            Assert.IsTrue(configured.lockRootHeightY);
            Assert.IsTrue(configured.lockRootPositionXZ);
        }

        [Test]
        public void AutoLoopFollowsTheCategory()
        {
            Assert.IsTrue(AnimationImporter.ResolveLoop(new AnimationSettings { category = AnimationCategories.Locomotion, loop = LoopModes.Auto }));
            Assert.IsFalse(AnimationImporter.ResolveLoop(new AnimationSettings { category = AnimationCategories.Death, loop = LoopModes.Auto }));
            Assert.IsFalse(AnimationImporter.ResolveLoop(new AnimationSettings { category = AnimationCategories.Locomotion, loop = LoopModes.No }));
            Assert.IsTrue(AnimationImporter.ResolveLoop(new AnimationSettings { category = AnimationCategories.Combat, loop = LoopModes.Yes }));
        }

        [Test]
        public void MissingSharedAvatarFailsBeforeCopyingAnything()
        {
            var r = AssetPipelineRunner.ImportOne(Clip("Darius Walk Gun.fbx", "Walk", AnimationCategories.Locomotion, a => a.sharedAvatarPath = ScratchFolder.Path + "/Nope.fbx"));
            Assert.IsFalse(r.success);
            StringAssert.Contains("Avatar", ImporterTestSupport.Errors(r));
            Assert.IsFalse(File.Exists(AssetPaths.Full(ScratchFolder.Path + "/Walk.fbx")), "nothing is copied when the Avatar is unusable");
        }

        [Test]
        public void EmptyAvatarPathAndNonHumanoidAvatarAreActionableErrors()
        {
            var empty = AssetPipelineRunner.ImportOne(Clip("Darius Walk Gun.fbx", "Walk", AnimationCategories.Locomotion, a => a.sharedAvatarPath = ""));
            Assert.IsFalse(empty.success);
            StringAssert.Contains("shared Avatar", ImporterTestSupport.Errors(empty));

            var rifle = ImporterTestSupport.Item(ProfileIds.GenericProp, ScratchFolder.Darius("Weapons/Rifle.fbx"), "Rifle");
            Assert.IsTrue(AssetPipelineRunner.ImportOne(rifle).success);
            var notHuman = AssetPipelineRunner.ImportOne(Clip("Darius Walk Gun.fbx", "Walk", AnimationCategories.Locomotion, a => a.sharedAvatarPath = ScratchFolder.Path + "/Rifle.fbx"));
            Assert.IsFalse(notHuman.success);
            StringAssert.Contains("Humanoid", ImporterTestSupport.Errors(notHuman));
        }

        [Test]
        public void ClipNameIsRequired()
        {
            var r = AssetPipelineRunner.ImportOne(Clip("Darius Walk Gun.fbx", "Walk", AnimationCategories.Locomotion, a => a.clipName = " "));
            Assert.IsFalse(r.success);
            StringAssert.Contains("clip name", ImporterTestSupport.Errors(r).ToLowerInvariant());
        }

        [Test]
        public void ReimportUpdatesTheSameAssetAndAppliesNewFlags()
        {
            AssetPipelineRunner.ImportOne(Clip("Darius Walk Gun.fbx", "Walk", AnimationCategories.Locomotion, a => a.loop = LoopModes.Yes));
            var guid = AssetDatabase.AssetPathToGUID(ScratchFolder.Path + "/Walk.fbx");

            var again = AssetPipelineRunner.ImportOne(Clip("Darius Walk Gun.fbx", "Walk", AnimationCategories.Locomotion, a => a.loop = LoopModes.No));

            Assert.IsTrue(again.success, ImporterTestSupport.Errors(again));
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(ScratchFolder.Path + "/Walk.fbx"));
            Assert.IsFalse(AnimationUtility.GetAnimationClipSettings(LoadClip(ScratchFolder.Path + "/Walk.fbx", "Walk")).loopTime);
        }

        [Test]
        public void ExistingUnlabelledAnimationIsNotSilentlyReplaced()
        {
            AssetPaths.EnsureFolder(ScratchFolder.Path);
            File.Copy(ScratchFolder.Darius("Animations/Darius Walk Gun.fbx"), AssetPaths.Full(ScratchFolder.Path + "/Walk.fbx"));
            AssetDatabase.Refresh();
            var r = AssetPipelineRunner.ImportOne(Clip("Darius Walk Gun.fbx", "Walk", AnimationCategories.Locomotion));
            Assert.IsFalse(r.success);
            StringAssert.Contains("already exists", ImporterTestSupport.Errors(r));
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify failure** (compile error: `AnimationImporter` missing).

- [ ] **Step 3: Implement** `Importers/AnimationImporter.cs`:

```csharp
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Blackglass.AssetPipeline
{
    public static class AnimationImporter
    {
        public static void Import(ImportItem item, ItemResult r)
        {
            var a = item.animation;
            if (Path.GetExtension(item.sourcePath).ToLowerInvariant() != ".fbx")
            {
                r.errors.Add("Humanoid animations must be FBX files.");
                return;
            }
            if (string.IsNullOrWhiteSpace(a.clipName))
            {
                r.errors.Add("Enter a clip name.");
                return;
            }
            if (!SourceCopier.CheckConflicts(item, r)) return;

            var avatar = LoadSharedAvatar(a.sharedAvatarPath, AssetNaming.ModelPath(item), r);
            if (avatar == null) return;
            r.avatar = new AvatarReport { valid = true, isHuman = true, message = "Copies the Avatar of " + a.sharedAvatarPath };

            var modelPath = SourceCopier.CopyAndImport(item, r);
            var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            importer.sourceAvatar = avatar;
            importer.importAnimation = true;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.SaveAndReimport();
            OwnershipLabel.Mark(modelPath);

            importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            var take = PickTake(importer, r);
            if (take == null)
            {
                r.errors.Add("No animation take was found in this FBX. Export it from Mixamo/Blender with the animation included.");
                return;
            }
            var loop = ResolveLoop(a);
            take.name = a.clipName;
            take.loopTime = loop;
            take.loopPose = loop;
            take.lockRootRotation = a.bakeRotation;
            take.lockRootHeightY = a.bakeHeight;
            take.lockRootPositionXZ = a.bakePositionXZ;
            importer.clipAnimations = new[] { take };
            importer.SaveAndReimport();

            if (!string.IsNullOrEmpty(importer.animationImportErrors)) r.errors.Add("Unity reported animation import errors: " + importer.animationImportErrors);
            if (!string.IsNullOrEmpty(importer.animationImportWarnings)) r.warnings.Add("Unity animation import warning: " + importer.animationImportWarnings);
            if (!string.IsNullOrEmpty(importer.animationRetargetingWarnings)) r.warnings.Add("Retargeting: " + importer.animationRetargetingWarnings);

            Verify(modelPath, a, loop, r);
        }

        /// <summary>Auto means "by category": locomotion loops, everything else plays once.</summary>
        public static bool ResolveLoop(AnimationSettings a)
        {
            if (a.loop == LoopModes.Yes) return true;
            if (a.loop == LoopModes.No) return false;
            return a.category == AnimationCategories.Locomotion;
        }

        static Avatar LoadSharedAvatar(string path, string ownModelPath, ItemResult r)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                r.errors.Add("No shared Avatar was given. Choose a Humanoid model in the project for the clip to target (Settings > Default shared Avatar).");
                return null;
            }
            if (path == ownModelPath)
            {
                r.errors.Add("The shared Avatar cannot be the animation file itself.");
                return null;
            }
            var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
            if (avatar == null)
            {
                r.errors.Add("No Avatar was found in '" + path + "'. Choose a model that is imported as Humanoid (Rig tab > Animation Type: Humanoid).");
                return null;
            }
            if (!avatar.isValid || !avatar.isHuman)
            {
                r.errors.Add("The Avatar in '" + path + "' is not a valid Humanoid Avatar. Fix its mapping (Rig tab > Configure) or choose another model.");
                return null;
            }
            return avatar;
        }

        /// <summary>The take to use: Mixamo names it "mixamo.com"; the generic "Take 001" is only used when nothing else exists.</summary>
        static ModelImporterClipAnimation PickTake(ModelImporter importer, ItemResult r)
        {
            var takes = importer.defaultClipAnimations;
            if (takes == null || takes.Length == 0) return null;
            var preferred = takes.Where(t => !t.name.StartsWith("Take ")).ToArray();
            var pool = preferred.Length > 0 ? preferred : takes;
            if (pool.Length > 1)
                r.warnings.Add("The file contains " + pool.Length + " takes; using the first (" + pool[0].name + ").");
            return pool[0];
        }

        static void Verify(string modelPath, AnimationSettings a, bool loop, ItemResult r)
        {
            var all = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__")).ToArray();
            var clip = all.FirstOrDefault(c => c.name == a.clipName);
            if (clip == null)
            {
                r.errors.Add("The clip '" + a.clipName + "' was not found after import (found: " + string.Join(", ", all.Select(c => c.name)) + ").");
                return;
            }
            if (clip.length <= 0f) r.errors.Add("The clip '" + a.clipName + "' has no duration.");
            if (!clip.isHumanMotion) r.errors.Add("The clip '" + a.clipName + "' is not Humanoid motion; the file's rig may not match the shared Avatar.");
            var loops = AnimationUtility.GetAnimationClipSettings(clip).loopTime;
            if (loops != loop) r.errors.Add("The clip's Loop Time is " + loops + " but " + loop + " was requested.");
            r.clips.Add(new ClipReport
            {
                name = clip.name, duration = clip.length, loop = loops,
                bakeRotation = a.bakeRotation, bakeHeight = a.bakeHeight, bakePositionXZ = a.bakePositionXZ,
            });
        }
    }
}
```
(If `clip.isHumanMotion` does not compile in this Unity version, use `clip.humanMotion`.)

Add the case to `ImportOne`:

```csharp
                        case ProfileIds.HumanoidAnimation:
                            AnimationImporter.Import(item, r);
                            break;
```

- [ ] **Step 4: Run** `Tools/run-tests.sh EditMode "Blackglass.Tests.AssetPipeline"`. Typical issues: `defaultClipAnimations` empty on first read (call `importer.SaveAndReimport()` once more before reading, which the code does by re-fetching the importer after the first reimport); `loopPose` assigned but `loopTime` mismatch (Unity sets both separately; the post-check reads `loopTime`).

- [ ] **Step 5: Opt-in real-pipeline test** `tests/Core.Tests/UnityE2E/AnimationE2ETests.cs`:

```csharp
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class AnimationE2ETests
{
    [Fact]
    public async Task Locomotion_combat_and_death_clips_import_with_their_loop_and_bake_settings()
    {
        if (!E2EFixture.Enabled) return;
        var settings = E2EFixture.Settings();
        try
        {
            var character = E2EFixture.Item(E2EFixture.Source("Models/Darius Stand Idle.fbx"), ImportProfiles.Character, "E2E_Anim_Char", settings,
                i => i.character.generatePrefab = false);
            var first = await E2EFixture.Run(settings, character);
            Assert.True(first.Success, first.FatalError ?? string.Join("; ", first.Result!.items.SelectMany(i => i.errors)));

            settings.DefaultSharedAvatarPath = "Assets/_AssetStudioScratch/E2E_Anim_Char.fbx";
            AssetItem Clip(string file, string profile, string name) =>
                E2EFixture.Item(E2EFixture.Source("Animations/" + file), profile, name, settings, i => i.animation.clipName = name);
            var walk = Clip("Darius Walk Gun.fbx", ImportProfiles.Locomotion, "Walk");
            var fire = Clip("Darius Fire.fbx", ImportProfiles.Combat, "Fire");
            var death = Clip("Darius Death.fbx", ImportProfiles.Death, "Death");
            var second = await E2EFixture.Run(settings, walk, fire, death);

            Assert.Null(second.FatalError);
            var byId = second.Result!.items.ToDictionary(i => i.id);
            foreach (var item in new[] { walk, fire, death })
                Assert.True(byId[item.Id].success, string.Join("; ", byId[item.Id].errors));
            Assert.True(byId[walk.Id].clips.Single().loop);
            Assert.False(byId[fire.Id].clips.Single().loop);
            Assert.False(byId[death.Id].clips.Single().loop);
            Assert.True(byId[death.Id].clips.Single().bakePositionXZ);
            Assert.True(byId[walk.Id].clips.Single().duration > 0.2f);
        }
        finally { E2EFixture.CleanScratch(); }
    }
}
```
Run: `BLACKGLASS_E2E=1 dotnet test … --filter "FullyQualifiedName~AnimationE2ETests"` (Unity closed). Expected pass; no scratch left afterwards.

- [ ] **Step 6: Commit** ("Asset Studio: humanoid animation importer with shared Avatar, loop and root-bake settings") including `.meta` files.

---

## Task 12: Generic prop, environment module, theme registration (Milestone E)

**Files:**
- Create: `Importers/PropImporter.cs`, `Importers/EnvironmentSpecs.cs`, `Importers/EnvironmentModuleImporter.cs`
- Modify: `Entry/AssetPipelineRunner.cs` (two `case`s)
- Create tests: `Assets/_Project/Tests/EditMode/AssetPipeline/EnvironmentSpecsTests.cs`, `PropImporterTests.cs`, `EnvironmentModuleImporterTests.cs`; `tests/Core.Tests/UnityE2E/PropEnvironmentE2ETests.cs`

**Interfaces:**
- Consumes: `ModelMeasure`, `ModelPrefabWriter`, `SourceCopier`, `OwnershipLabel`, `AssetNaming`, `EnvironmentTheme`/`EnvironmentElement` (runtime asmdef `Blackglass`).
- Produces: `PropImporter.Import`, `PropImporter.ConfigureMeshImport(string modelPath)` (shared mesh-only import setup); `EnvironmentSpecs.For(EnvironmentElement) -> ModuleSpec` (null when unchecked) and `EnvironmentSpecs.Check(EnvironmentElement, Vector3 size, Vector3 min, Vector3 max) -> List<string>` (warnings); `EnvironmentModuleImporter.Import`, `EnvironmentModuleImporter.Register(string themePath, EnvironmentElement element, string mode, int replaceIndex, GameObject prefab) -> string` (null on success, otherwise a message).

Rules (spec): a prop or module is **never** auto-normalised. Props and modules: prefab root `<Name>` with identity scale and one child `Model` carrying the explicit manifest scale; collider-free. Environment dimensions are measured, compared with the Environment Asset Guide table and reported as warnings; theme registration is an explicit user choice and edits the theme through `SerializedObject`.

- [ ] **Step 1: Tests first.**

`EnvironmentSpecsTests.cs`:

```csharp
#if UNITY_EDITOR
using Blackglass.AssetPipeline;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests.AssetPipeline
{
    public class EnvironmentSpecsTests
    {
        static Vector3 Min(float x, float y, float z) => new Vector3(x, y, z);

        [Test]
        public void PerfectWallPassesWithNoWarnings()
        {
            var w = EnvironmentSpecs.Check(EnvironmentElement.WallStraight, new Vector3(1f, 3f, 1f), new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 3f, 0.5f));
            Assert.IsEmpty(w);
        }

        [Test]
        public void WrongHeightIsReportedWithTheNumbers()
        {
            var w = EnvironmentSpecs.Check(EnvironmentElement.WallStraight, new Vector3(1f, 2.4f, 1f), new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 2.4f, 0.5f));
            Assert.AreEqual(1, w.Count);
            StringAssert.Contains("Y", w[0]);
            StringAssert.Contains("2.4", w[0]);
            StringAssert.Contains("3", w[0]);
        }

        [Test]
        public void SmallOverhangIsAllowedButLargeIsNot()
        {
            Assert.IsEmpty(EnvironmentSpecs.Check(EnvironmentElement.WallStraight, new Vector3(1.1f, 3f, 1.1f), new Vector3(-0.55f, 0f, -0.55f), new Vector3(0.55f, 3f, 0.55f)));
            Assert.IsNotEmpty(EnvironmentSpecs.Check(EnvironmentElement.WallStraight, new Vector3(1.4f, 3f, 1f), new Vector3(-0.7f, 0f, -0.5f), new Vector3(0.7f, 3f, 0.5f)));
        }

        [Test]
        public void PivotChecksDifferForFloorAndWalls()
        {
            // A wall whose pivot is at its centre (hovering): lowest point -1.5 instead of 0.
            var hovering = EnvironmentSpecs.Check(EnvironmentElement.WallStraight, new Vector3(1f, 3f, 1f), new Vector3(-0.5f, -1.5f, -0.5f), new Vector3(0.5f, 1.5f, 0.5f));
            Assert.IsTrue(hovering.Exists(m => m.Contains("pivot")));
            // A floor has its pivot at the top face.
            Assert.IsEmpty(EnvironmentSpecs.Check(EnvironmentElement.Floor, new Vector3(1f, 0.2f, 1f), new Vector3(-0.5f, -0.2f, -0.5f), new Vector3(0.5f, 0f, 0.5f)));
        }

        [Test]
        public void CrateOnlyHasAnUpperLimit()
        {
            Assert.IsEmpty(EnvironmentSpecs.Check(EnvironmentElement.Crate, new Vector3(0.6f, 0.5f, 0.6f), new Vector3(-0.3f, 0f, -0.3f), new Vector3(0.3f, 0.5f, 0.3f)));
            Assert.IsNotEmpty(EnvironmentSpecs.Check(EnvironmentElement.Crate, new Vector3(1.6f, 1f, 1f), new Vector3(-0.8f, 0f, -0.5f), new Vector3(0.8f, 1f, 0.5f)));
        }

        [Test]
        public void EveryElementHasASpecOrIsDeliberatelyUnchecked()
        {
            foreach (EnvironmentElement e in System.Enum.GetValues(typeof(EnvironmentElement)))
                if (e != EnvironmentElement.LightFixture)
                    Assert.IsNotNull(EnvironmentSpecs.For(e), e.ToString());
            Assert.IsNull(EnvironmentSpecs.For(EnvironmentElement.LightFixture));
        }
    }
}
#endif
```

`PropImporterTests.cs`:

```csharp
#if UNITY_EDITOR
using Blackglass.AssetPipeline;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests.AssetPipeline
{
    public class PropImporterTests
    {
        [TearDown]
        public void TearDown() => ScratchFolder.Clean();

        static ImportItem Rifle(string name = "TestRifle") =>
            ImporterTestSupport.Item(ProfileIds.GenericProp, ScratchFolder.Darius("Weapons/Rifle.fbx"), name);

        [Test]
        public void ImportsAPropWithAColliderFreePrefabAndReportsSize()
        {
            var r = AssetPipelineRunner.ImportOne(Rifle());
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ScratchFolder.Path + "/TestRifle.prefab");
            Assert.IsNotNull(prefab);
            Assert.AreEqual(Vector3.one, prefab.transform.localScale);
            Assert.IsEmpty(prefab.GetComponentsInChildren<Collider>(true));
            Assert.AreEqual(Vector3.one, prefab.transform.Find("Model").localScale);
            Assert.Greater(Mathf.Max(r.dimensions.x, r.dimensions.y, r.dimensions.z), 0.1f);
            Assert.IsTrue(OwnershipLabel.IsOwned(ScratchFolder.Path + "/TestRifle.fbx"));
            Assert.IsTrue(OwnershipLabel.IsOwned(ScratchFolder.Path + "/TestRifle.prefab"));
        }

        [Test]
        public void ExplicitScaleIsAppliedToTheModelChildOnly()
        {
            var item = Rifle();
            item.prop.scale = 2f;
            var r = AssetPipelineRunner.ImportOne(item);
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ScratchFolder.Path + "/TestRifle.prefab");
            Assert.AreEqual(Vector3.one, prefab.transform.localScale);
            Assert.AreEqual(2f, prefab.transform.Find("Model").localScale.x, 1e-5f);
        }

        [Test]
        public void NoPrefabWhenNotRequested()
        {
            var item = Rifle();
            item.prop.generatePrefab = false;
            var r = AssetPipelineRunner.ImportOne(item);
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(ScratchFolder.Path + "/TestRifle.prefab"));
        }

        [Test]
        public void ReimportUpdatesInPlaceAndUnlabelledAssetsAreProtected()
        {
            AssetPipelineRunner.ImportOne(Rifle());
            var guid = AssetDatabase.AssetPathToGUID(ScratchFolder.Path + "/TestRifle.prefab");
            Assert.IsTrue(AssetPipelineRunner.ImportOne(Rifle()).success);
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(ScratchFolder.Path + "/TestRifle.prefab"));

            AssetPaths.EnsureFolder(ScratchFolder.Path);
            System.IO.File.Copy(ScratchFolder.Darius("Weapons/Rifle.fbx"), AssetPaths.Full(ScratchFolder.Path + "/Other.fbx"));
            AssetDatabase.Refresh();
            var refused = AssetPipelineRunner.ImportOne(Rifle("Other"));
            Assert.IsFalse(refused.success);
            StringAssert.Contains("already exists", ImporterTestSupport.Errors(refused));
        }

        [Test]
        public void ANonPositiveScaleIsRefused()
        {
            var item = Rifle();
            item.prop.scale = 0f;
            Assert.IsFalse(AssetPipelineRunner.ImportOne(item).success);
        }
    }
}
#endif
```

`EnvironmentModuleImporterTests.cs`:

```csharp
#if UNITY_EDITOR
using System.Linq;
using Blackglass.AssetPipeline;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests.AssetPipeline
{
    public class EnvironmentModuleImporterTests
    {
        [TearDown]
        public void TearDown() => ScratchFolder.Clean();

        // Darius is about 1.8 m tall, so as a "wall" he deliberately mismatches the 3 m module height.
        static ImportItem Wall(string name = "TestWall", string element = "WallStraight")
        {
            var item = ImporterTestSupport.Item(ProfileIds.EnvironmentModule, ScratchFolder.Darius("Models/Darius Stand Idle.fbx"), name);
            item.environment.element = element;
            return item;
        }

        static GameObject MakePrefab(string path)
        {
            var go = new GameObject(System.IO.Path.GetFileNameWithoutExtension(path));
            var saved = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return saved;
        }

        static EnvironmentTheme MakeTheme(string path)
        {
            var theme = EnvironmentTheme.Create(7, new EnvironmentTheme.Entry[0]);
            AssetDatabase.CreateAsset(theme, path);
            return theme;
        }

        static int VariantCount(EnvironmentTheme theme, EnvironmentElement element)
        {
            var entries = new SerializedObject(theme).FindProperty("entries");
            for (var i = 0; i < entries.arraySize; i++)
            {
                var entry = entries.GetArrayElementAtIndex(i);
                if (entry.FindPropertyRelative("element").intValue == (int)element)
                    return entry.FindPropertyRelative("variants").arraySize;
            }
            return 0;
        }

        [Test]
        public void ImportsAModuleAndWarnsAboutDimensionMismatchesWithoutRescaling()
        {
            var r = AssetPipelineRunner.ImportOne(Wall());
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            Assert.IsTrue(r.warnings.Any(w => w.Contains("Y")), "height mismatch must be reported: " + string.Join("\n", r.warnings));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ScratchFolder.Path + "/TestWall.prefab");
            Assert.IsNotNull(prefab);
            Assert.AreEqual(Vector3.one, prefab.transform.Find("Model").localScale, "environment modules are never auto-scaled");
            Assert.IsEmpty(prefab.GetComponentsInChildren<Collider>(true));
            Assert.Greater(r.dimensions.y, 1.5f);
            Assert.IsEmpty(r.changedAssets.Where(p => p.EndsWith(".asset")), "no theme change unless requested");
        }

        [Test]
        public void ExplicitScaleChangesTheModelChildAndTheReportedSize()
        {
            var item = Wall();
            item.environment.scale = 2f;
            var r = AssetPipelineRunner.ImportOne(item);
            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            Assert.AreEqual(2f, AssetDatabase.LoadAssetAtPath<GameObject>(ScratchFolder.Path + "/TestWall.prefab").transform.Find("Model").localScale.x, 1e-5f);
            Assert.Greater(r.dimensions.y, 3.2f);
        }

        [Test]
        public void UnknownElementIsAnError()
        {
            var r = AssetPipelineRunner.ImportOne(Wall("Bad", "Banana"));
            Assert.IsFalse(r.success);
            StringAssert.Contains("Banana", ImporterTestSupport.Errors(r));
        }

        [Test]
        public void ThemeAppendAddsOnceAndCreatesTheEntryWhenMissing()
        {
            AssetPaths.EnsureFolder(ScratchFolder.Path);
            var theme = MakeTheme(ScratchFolder.Path + "/Theme.asset");
            var prefab = MakePrefab(ScratchFolder.Path + "/P1.prefab");

            Assert.IsNull(EnvironmentModuleImporter.Register(ScratchFolder.Path + "/Theme.asset", EnvironmentElement.WallStraight, ThemeModes.Append, 0, prefab));
            Assert.AreEqual(1, VariantCount(theme, EnvironmentElement.WallStraight));
            Assert.IsNull(EnvironmentModuleImporter.Register(ScratchFolder.Path + "/Theme.asset", EnvironmentElement.WallStraight, ThemeModes.Append, 0, prefab));
            Assert.AreEqual(1, VariantCount(theme, EnvironmentElement.WallStraight), "appending the same prefab twice adds it once");

            var second = MakePrefab(ScratchFolder.Path + "/P2.prefab");
            Assert.IsNull(EnvironmentModuleImporter.Register(ScratchFolder.Path + "/Theme.asset", EnvironmentElement.WallStraight, ThemeModes.Append, 0, second));
            Assert.AreEqual(2, VariantCount(theme, EnvironmentElement.WallStraight));
            Assert.AreEqual(0, VariantCount(theme, EnvironmentElement.Floor), "other elements are untouched");
        }

        [Test]
        public void ThemeReplaceChecksTheIndexAndMissingThemesAreReported()
        {
            AssetPaths.EnsureFolder(ScratchFolder.Path);
            var theme = MakeTheme(ScratchFolder.Path + "/Theme.asset");
            var p1 = MakePrefab(ScratchFolder.Path + "/P1.prefab");
            var p2 = MakePrefab(ScratchFolder.Path + "/P2.prefab");
            EnvironmentModuleImporter.Register(ScratchFolder.Path + "/Theme.asset", EnvironmentElement.Crate, ThemeModes.Append, 0, p1);

            Assert.IsNull(EnvironmentModuleImporter.Register(ScratchFolder.Path + "/Theme.asset", EnvironmentElement.Crate, ThemeModes.Replace, 0, p2));
            Assert.AreEqual(1, VariantCount(theme, EnvironmentElement.Crate));
            StringAssert.Contains("out of range", EnvironmentModuleImporter.Register(ScratchFolder.Path + "/Theme.asset", EnvironmentElement.Crate, ThemeModes.Replace, 5, p2));
            StringAssert.Contains("out of range", EnvironmentModuleImporter.Register(ScratchFolder.Path + "/Theme.asset", EnvironmentElement.Floor, ThemeModes.Replace, 0, p2));
            Assert.AreEqual(0, VariantCount(theme, EnvironmentElement.Floor), "a failed replace must not leave an empty entry behind");
            StringAssert.Contains("not found", EnvironmentModuleImporter.Register(ScratchFolder.Path + "/Nope.asset", EnvironmentElement.Crate, ThemeModes.Append, 0, p1));
        }

        [Test]
        public void ImportCanRegisterTheNewPrefabInATheme()
        {
            AssetPaths.EnsureFolder(ScratchFolder.Path);
            var theme = MakeTheme(ScratchFolder.Path + "/Theme.asset");
            var item = Wall();
            item.environment.themePath = ScratchFolder.Path + "/Theme.asset";
            item.environment.themeMode = ThemeModes.Append;

            var r = AssetPipelineRunner.ImportOne(item);

            Assert.IsTrue(r.success, ImporterTestSupport.Errors(r));
            Assert.AreEqual(1, VariantCount(theme, EnvironmentElement.WallStraight));
            StringAssert.Contains("WallStraight", r.registeredInTheme);
            CollectionAssert.Contains(r.changedAssets, ScratchFolder.Path + "/Theme.asset");
        }

        [Test]
        public void ARegistrationProblemFailsTheItemClearly()
        {
            var item = Wall();
            item.environment.themePath = ScratchFolder.Path + "/Missing.asset";
            item.environment.themeMode = ThemeModes.Append;
            var r = AssetPipelineRunner.ImportOne(item);
            Assert.IsFalse(r.success);
            StringAssert.Contains("not found", ImporterTestSupport.Errors(r));
        }
    }
}
#endif
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement.**

`Importers/PropImporter.cs`:

```csharp
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Blackglass.AssetPipeline
{
    public static class PropImporter
    {
        public static void Import(ImportItem item, ItemResult r)
        {
            var p = item.prop;
            if (!(p.scale > 0f))
            {
                r.errors.Add("The prop scale must be greater than 0.");
                return;
            }
            if (!SourceCopier.CheckConflicts(item, r)) return;

            var modelPath = SourceCopier.CopyAndImport(item, r);
            ConfigureMeshImport(modelPath);
            OwnershipLabel.Mark(modelPath);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);

            var size = MeasureInto(model, p.scale, r, out _, out _);
            if (size.HasValue)
            {
                var largest = Mathf.Max(size.Value.x, size.Value.y, size.Value.z);
                if (largest < 0.05f || largest > 10f)
                    r.warnings.Add("The prop's largest side is " + largest.ToString("0.###") + " m. Check the model's units or set a scale.");
            }
            if (r.errors.Count > 0 || !p.generatePrefab) return;

            var prefabPath = AssetNaming.PrefabPath(item);
            var existed = File.Exists(AssetPaths.Full(prefabPath));
            ModelPrefabWriter.Write(prefabPath, item.name, model, "Model", p.scale, null);
            OwnershipLabel.Mark(prefabPath);
            (existed ? r.changedAssets : r.createdPrefabs).Add(prefabPath);
        }

        /// <summary>Mesh-only import: no animation, no colliders, materials inside the model.</summary>
        public static void ConfigureMeshImport(string modelPath)
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            importer.animationType = ModelImporterAnimationType.None;
            importer.importAnimation = false;
            importer.addCollider = false;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
            importer.SaveAndReimport();
        }

        /// <summary>
        /// Measures the model at the origin and fills dimensions (already multiplied by the explicit scale). Returns the scaled size,
        /// or null with an error when there is no geometry. min/max are the scaled bounds corners.
        /// </summary>
        public static Vector3? MeasureInto(GameObject model, float scale, ItemResult r, out Vector3 min, out Vector3 max)
        {
            min = max = Vector3.zero;
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            try
            {
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var bounds = ModelMeasure.Measure(instance);
                if (bounds == null)
                {
                    r.errors.Add("The imported model contains no meshes.");
                    return null;
                }
                var b = bounds.Value;
                min = b.min * scale;
                max = b.max * scale;
                var size = b.size * scale;
                r.dimensions = new Size3 { x = size.x, y = size.y, z = size.z };
                return size;
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }
    }
}
```

`Importers/EnvironmentSpecs.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass.AssetPipeline
{
    public enum PivotKind { BottomCenter, TopCenter }

    public sealed class ModuleSpec
    {
        public readonly Vector3 size;
        public readonly PivotKind pivot;
        /// <summary>True for props that may be smaller than the cell (crates): only the upper limit applies.</summary>
        public readonly bool maxOnly;

        public ModuleSpec(Vector3 size, PivotKind pivot, bool maxOnly = false)
        {
            this.size = size;
            this.pivot = pivot;
            this.maxOnly = maxOnly;
        }
    }

    /// <summary>The footprints and pivots of docs/EnvironmentAssetGuide.md section 2, as data. A test pins every element to a spec.</summary>
    public static class EnvironmentSpecs
    {
        public const float SizeTolerance = 0.05f;
        /// <summary>The guide: decoration may stick out at most about 0.12 m beyond the tile footprint.</summary>
        public const float OverhangAllowance = 0.12f;
        public const float PivotTolerance = 0.07f;

        public static ModuleSpec For(EnvironmentElement element)
        {
            switch (element)
            {
                case EnvironmentElement.Floor: return new ModuleSpec(new Vector3(1f, 0.2f, 1f), PivotKind.TopCenter);
                case EnvironmentElement.WallStraight:
                case EnvironmentElement.WallCorner:
                case EnvironmentElement.WallEnd:
                case EnvironmentElement.WallJunction:
                case EnvironmentElement.Pillar: return new ModuleSpec(new Vector3(1f, 3f, 1f), PivotKind.BottomCenter);
                case EnvironmentElement.DoorFrame: return new ModuleSpec(new Vector3(1f, 2.4f, 1f), PivotKind.BottomCenter);
                case EnvironmentElement.LowCover: return new ModuleSpec(new Vector3(1f, 1f, 1f), PivotKind.BottomCenter);
                case EnvironmentElement.LowCoverLong: return new ModuleSpec(new Vector3(2f, 1f, 1f), PivotKind.BottomCenter);
                case EnvironmentElement.Crate: return new ModuleSpec(new Vector3(1f, 1f, 1f), PivotKind.BottomCenter, true);
                case EnvironmentElement.Terminal: return new ModuleSpec(new Vector3(0.8f, 1.2f, 0.8f), PivotKind.BottomCenter);
                default: return null; // LightFixture: a thin prop placed on a wall face; nothing to compare
            }
        }

        /// <summary>Warnings (never errors, never a rescale) for a measured module. size/min/max are in the prefab root's space.</summary>
        public static List<string> Check(EnvironmentElement element, Vector3 size, Vector3 min, Vector3 max)
        {
            var warnings = new List<string>();
            var spec = For(element);
            if (spec == null) return warnings;

            var axes = new[] { "X", "Y", "Z" };
            for (var i = 0; i < 3; i++)
            {
                var expected = spec.size[i];
                var upper = expected + (i == 1 ? SizeTolerance : OverhangAllowance);
                var lower = spec.maxOnly ? 0f : expected - SizeTolerance;
                if (size[i] < lower || size[i] > upper)
                    warnings.Add(axes[i] + " is " + size[i].ToString("0.###") + " m but a " + element + " module should be " + expected.ToString("0.###") +
                                 " m (allowed " + lower.ToString("0.###") + " to " + upper.ToString("0.###") + " m).");
            }

            if (spec.pivot == PivotKind.BottomCenter && Mathf.Abs(min.y) > PivotTolerance)
                warnings.Add("The pivot is not at the bottom: the lowest point is at y = " + min.y.ToString("0.###") + " m (it should be 0).");
            if (spec.pivot == PivotKind.TopCenter && Mathf.Abs(max.y) > PivotTolerance)
                warnings.Add("The pivot is not at the top face: the highest point is at y = " + max.y.ToString("0.###") + " m (it should be 0).");
            var centreX = (min.x + max.x) * 0.5f;
            var centreZ = (min.z + max.z) * 0.5f;
            if (Mathf.Abs(centreX) > PivotTolerance || Mathf.Abs(centreZ) > PivotTolerance)
                warnings.Add("The pivot is not centred on the footprint (centre offset x " + centreX.ToString("0.###") + ", z " + centreZ.ToString("0.###") + " m).");
            return warnings;
        }
    }
}
```
(`EnvironmentElement` lives in namespace `Blackglass`, the parent of `Blackglass.AssetPipeline`, so it resolves without a using.)

`Importers/EnvironmentModuleImporter.cs`:

```csharp
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Blackglass.AssetPipeline
{
    public static class EnvironmentModuleImporter
    {
        public static void Import(ImportItem item, ItemResult r)
        {
            var e = item.environment;
            if (!Enum.TryParse(e.element, out EnvironmentElement element) || Array.IndexOf(EnvironmentElements.All, e.element) < 0)
            {
                r.errors.Add("'" + e.element + "' is not an environment element. Use one of: " + string.Join(", ", EnvironmentElements.All) + ".");
                return;
            }
            if (!(e.scale > 0f))
            {
                r.errors.Add("The module scale must be greater than 0.");
                return;
            }
            if (!SourceCopier.CheckConflicts(item, r)) return;

            var modelPath = SourceCopier.CopyAndImport(item, r);
            PropImporter.ConfigureMeshImport(modelPath);
            OwnershipLabel.Mark(modelPath);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);

            var size = PropImporter.MeasureInto(model, e.scale, r, out var min, out var max);
            if (size == null) return;
            if (Mathf.Abs(e.scale - 1f) > 1e-6f)
                r.warnings.Add("An explicit scale of " + e.scale.ToString("0.###") + " was applied to the module, as requested.");
            r.warnings.AddRange(EnvironmentSpecs.Check(element, size.Value, min, max));

            var prefabPath = AssetNaming.PrefabPath(item);
            var existed = File.Exists(AssetPaths.Full(prefabPath));
            var prefab = ModelPrefabWriter.Write(prefabPath, item.name, model, "Model", e.scale, null);
            OwnershipLabel.Mark(prefabPath);
            (existed ? r.changedAssets : r.createdPrefabs).Add(prefabPath);

            if (e.themeMode == ThemeModes.Append || e.themeMode == ThemeModes.Replace)
            {
                var problem = Register(e.themePath, element, e.themeMode, e.replaceIndex, prefab);
                if (problem != null)
                {
                    r.errors.Add(problem);
                    return;
                }
                r.registeredInTheme = e.themePath + " (" + element + ", " + e.themeMode + ")";
                if (!r.changedAssets.Contains(e.themePath)) r.changedAssets.Add(e.themePath);
            }
        }

        /// <summary>Adds or replaces a variant in an EnvironmentTheme through SerializedObject (its fields are private). Null on success, otherwise a message.</summary>
        public static string Register(string themePath, EnvironmentElement element, string mode, int replaceIndex, GameObject prefab)
        {
            var theme = AssetDatabase.LoadAssetAtPath<EnvironmentTheme>(themePath);
            if (theme == null) return "The theme asset was not found or is not an EnvironmentTheme: " + themePath;

            var so = new SerializedObject(theme);
            var entries = so.FindProperty("entries");
            SerializedProperty entry = null;
            for (var i = 0; i < entries.arraySize; i++)
            {
                var candidate = entries.GetArrayElementAtIndex(i);
                if (candidate.FindPropertyRelative("element").intValue == (int)element)
                {
                    entry = candidate;
                    break;
                }
            }
            if (entry == null)
            {
                entries.arraySize++;
                entry = entries.GetArrayElementAtIndex(entries.arraySize - 1);
                entry.FindPropertyRelative("element").intValue = (int)element;
                entry.FindPropertyRelative("variants").arraySize = 0;
            }

            var variants = entry.FindPropertyRelative("variants");
            if (mode == ThemeModes.Replace)
            {
                if (replaceIndex < 0 || replaceIndex >= variants.arraySize)
                    return "The theme has " + variants.arraySize + " variant(s) for " + element + "; index " + replaceIndex + " is out of range.";
                variants.GetArrayElementAtIndex(replaceIndex).objectReferenceValue = prefab;
            }
            else
            {
                for (var i = 0; i < variants.arraySize; i++)
                    if (variants.GetArrayElementAtIndex(i).objectReferenceValue == prefab)
                        return null; // already registered
                variants.arraySize++;
                variants.GetArrayElementAtIndex(variants.arraySize - 1).objectReferenceValue = prefab;
            }
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(theme);
            AssetDatabase.SaveAssets();
            return null;
        }
    }
}
```

Add the cases to `ImportOne`:

```csharp
                        case ProfileIds.GenericProp:
                            PropImporter.Import(item, r);
                            break;
                        case ProfileIds.EnvironmentModule:
                            EnvironmentModuleImporter.Import(item, r);
                            break;
```

- [ ] **Step 4: Run** `Tools/run-tests.sh EditMode "Blackglass.Tests.AssetPipeline"` → all green. Also re-run the Task 8 `UnknownProfileAndBadDestination…` test: it must still pass (profile `Nope` and the invalid destination for the prop item).

- [ ] **Step 5: Opt-in real-pipeline test** `tests/Core.Tests/UnityE2E/PropEnvironmentE2ETests.cs`:

```csharp
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class PropEnvironmentE2ETests
{
    [Fact]
    public async Task Prop_and_environment_module_import_in_one_run()
    {
        if (!E2EFixture.Enabled) return;
        var settings = E2EFixture.Settings();
        try
        {
            var prop = E2EFixture.Item(E2EFixture.Source("Weapons/Rifle.fbx"), ImportProfiles.Prop, "E2E_Rifle", settings);
            var wall = E2EFixture.Item(E2EFixture.Source("Models/Darius Stand Idle.fbx"), ImportProfiles.Environment, "E2E_Wall", settings,
                i => i.environment.element = "WallStraight");
            var outcome = await E2EFixture.Run(settings, prop, wall);

            Assert.Null(outcome.FatalError);
            var byId = outcome.Result!.items.ToDictionary(i => i.id);
            Assert.True(byId[prop.Id].success, string.Join("; ", byId[prop.Id].errors));
            Assert.True(byId[wall.Id].success, string.Join("; ", byId[wall.Id].errors));
            Assert.Contains("Assets/_AssetStudioScratch/E2E_Rifle.prefab", byId[prop.Id].createdPrefabs);
            Assert.Contains(byId[wall.Id].warnings, w => w.StartsWith("Y is"));
            Assert.Equal("", byId[wall.Id].registeredInTheme);
        }
        finally { E2EFixture.CleanScratch(); }
    }
}
```
Run with Unity closed: `BLACKGLASS_E2E=1 dotnet test … --filter "FullyQualifiedName~PropEnvironmentE2ETests"`. Expected pass.

- [ ] **Step 6: Full suites.** `Tools/run-tests.sh EditMode` green. Then commit ("Asset Studio: generic prop and environment module importers with dimension checks and theme registration"), with `.meta` files.

---

## Task 13: Documentation, decision record, publish check and final verification

**Files:**
- Create: `docs/AssetStudio.md`
- Modify: `docs/Decisions.md` (append 039)
- No code changes except fixes found by verification.

- [ ] **Step 1: Write `docs/AssetStudio.md`.** Concise manual with exactly these sections (copy the command lines as shown; keep each section short):

```markdown
# Blackglass Asset Studio v0.1

A Windows tool that imports FBX/OBJ files into the Blackglass Unity project with the right settings. It prepares a manifest and runs Unity in batch
mode; Unity's own APIs create every asset. It does not replace Blender. Design: `docs/superpowers/specs/2026-10-08-asset-studio-design.md`.

## 1. Build
Requires the .NET SDK 10 (`dotnet --list-sdks`).
    dotnet build Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln
    dotnet test  Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln

## 2. Run
    dotnet run --project Tools/BlackglassAssetStudio/src/App
or start `Tools/BlackglassAssetStudio/src/App/bin/Debug/net10.0-windows/BlackglassAssetStudio.exe`. **Close the Unity Editor before importing**:
Unity allows one instance per project.

## 3. Configure the project path
Settings... > Blackglass project folder (the folder with `Assets` and `ProjectSettings`). When the app runs from inside the repository it
proposes that folder itself. The Unity executable is found from `ProjectSettings/ProjectVersion.txt` (Unity Hub folders); browse for it if
needed. Settings live in `%AppData%\BlackglassAssetStudio\settings.json`; run files in `%LocalAppData%\BlackglassAssetStudio\runs\<runId>\`
(last 10 kept). Nothing machine-specific is written into the repository.

## 4. Import a character
1. Prepare the FBX in Blender: Humanoid-compatible skeleton, T-pose, feet on the origin, applied transforms.
2. Drop it on the window. Profile: Humanoid Character. Check the name and destination (default `Assets/Art/Characters/<Name>`).
3. Set the target height (default 1.85 m), Validate, Import Selected.
4. Result: `<Name>.fbx` (Humanoid, Avatar), `<Name>_Visual.prefab`. Put the visual prefab under a gameplay unit's `Visual` child (the shell is not
   edited by the tool). An invalid Humanoid mapping fails the item with instructions and creates no prefab.

## 5. Height normalisation
Unity measures the model after import (posed mesh bounds) and the visual scale is `target / measured`. The scale goes on the `ImportedCharacter`
child inside the visual prefab; the prefab root and any gameplay root stay at scale 1, so colliders, NavMeshAgent, ranges and cover are not
affected. Stray geometry (hair, coat tails, floating parts) can skew the measurement: set **Manual visual scale** (it always wins) and import
again; the same assets are updated in place. Large corrections raise a warning.

## 6. Import a Mixamo animation
1. Download from Mixamo as FBX, preferably **Without Skin**, and use the same character skeleton as the shared Avatar. Keep "In Place" on for
   locomotion.
2. Drop it; the name usually picks Locomotion / Combat / Reaction / Death. Override if wrong.
3. Shared Avatar: a Humanoid model already in the project (default `Assets/Art/Characters/Darius/Models/Darius Stand Idle.fbx`, offered when present).
4. Defaults: Locomotion loops, all root axes baked (in place); Combat and Reaction do not loop, bake height; Death does not loop, bakes
   height and horizontal position. The FBX import settings carry these (no `.anim` copies).
Note: Unity cannot skip meshes on import, so a skinned animation FBX keeps its mesh inside the animation asset.

## 7. Import an environment module
Follow `docs/EnvironmentAssetGuide.md` (1 m grid, pivots, front +Z, 3 m walls). Drop the FBX, choose the **Element**. The importer measures it,
compares with the guide and lists mismatches as warnings; it never rescales unless you type an explicit scale. It writes a collider-free prefab. To
use it, choose "Register in theme" Append (or Replace + index) with the theme asset, or add it to the theme by hand.

## 8. Unity batch mode
The app runs: `Unity.exe -batchmode -projectPath <project> -executeMethod Blackglass.AssetPipeline.AssetPipelineRunner.RunFromCommandLine
-blackglassManifest <manifest.json> -blackglassResult <result.json> -quit -logFile <unity.log>`. Unity reads the manifest (schemaVersion 1),
imports each item independently, writes `result.json` (assets, prefabs, warnings, errors, dimensions, Avatar, clips) and exits 0 on success.
Existing assets are updated only if Asset Studio created them (asset label `BlackglassStudio`); anything else needs **Allow overwrite**. From
inside an open Editor use `Blackglass > Asset Pipeline > Run Manifest...` with a `manifest.json` from a run folder. Sources are copied; the
originals are never modified. The result tab lists project files that changed (via `git status`); nothing is committed.

## 9. Troubleshooting
- "The Unity Editor has this project open": close it. A leftover `Temp/UnityLockfile` after a crash is ignored.
- "Unity <version> was not found": install that exact version in Unity Hub or browse to `Unity.exe`.
- "finished without a usable result": open the log (Log tab); the last error lines are quoted in the message. Usual causes: a compile error in
  the project, or an import crash.
- Humanoid mapping failed: open the model in Unity > Rig > Configure, or fix the skeleton in Blender.
- Clip not humanoid / wrong pose: the animation's skeleton differs from the shared Avatar's.
- Height looks wrong: stray geometry; use the manual scale.
- GLB/glTF are refused in v0.1: convert to FBX in Blender.

## 10. Publish a standalone EXE
    dotnet publish Tools/BlackglassAssetStudio/src/App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o Tools/BlackglassAssetStudio/publish
The result is `Tools/BlackglassAssetStudio/publish/BlackglassAssetStudio.exe` (not committed). On first run choose the project path in Settings.

## Tests
`dotnet test` runs the pure-logic suite. Real Unity runs are opt-in: close the Editor and set `BLACKGLASS_E2E=1`
(`BLACKGLASS_E2E=1 dotnet test Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln`). Unity-side tests: `Tools/run-tests.sh EditMode "Blackglass.Tests.AssetPipeline"`.
```
(In the real file use fenced `bash` blocks for the command lines instead of indentation.)

- [ ] **Step 2: Append decision 039 to `docs/Decisions.md`** (match the style of the existing entries: Decided / Why / Rejected / Implications / Known limitations):

```markdown
## 039 — Blackglass Asset Studio: an external ingestion tool, with Unity doing all asset work

- **Decided (2026-10-08):**
  - **Two components.** `Tools/BlackglassAssetStudio` is a standalone WPF app (Core library, App, tests, `net10.0-windows`). It classifies files, builds a versioned JSON manifest (`schemaVersion` 1), stages copies of the sources and launches Unity in batch mode. `Assets/_Project/Editor/AssetPipeline` (asmdef `Blackglass.AssetPipeline.Editor`) reads the manifest and does every Unity operation with Unity's APIs, then writes a structured `result.json`. The app never writes `.meta`, `.prefab` or other serialized Unity files.
  - **One contract, compiled twice.** The manifest/result DTOs and three pure rules (`HeightScale`, `PathRules`, `AssetNaming`) are plain C# files in `.../AssetPipeline/Contract/`, compiled by Unity and linked into the .NET `Core` project. A golden JSON fixture is parsed by both test suites.
  - **Character height** is a visual scale only: Unity measures the posed mesh bounds, the scale is `target / measured` (manual override wins; unusable measurements are errors, never NaN), and it is applied to the `ImportedCharacter` child of a generated `<Name>_Visual.prefab`. Gameplay roots are never read or written; Darius_Player is untouched.
  - **Animation** is imported as Humanoid with Copy From Other Avatar (a shared Humanoid model, default Darius's) and one `clipAnimations` entry carrying loop and the three root bake-into-pose flags; no `.anim` copies (consistent with 034).
  - **Environment modules** use the existing `EnvironmentElement` names and the guide's footprint table; mismatches are warnings, never an automatic rescale; theme registration is an explicit append/replace through `SerializedObject`.
  - **Safety.** Assets the pipeline creates carry the `BlackglassStudio` label; only labelled assets are updated silently, any other existing asset needs an explicit `allowOverwrite`. Run files live in `%LocalAppData%` (last 10 kept), not in the project. Originals are never modified. Nothing is committed by the tool.
  - **GLB/glTF are refused in v0.1** (no built-in Unity importer; a package would bypass `ModelImporter`).
  - **Batch mode needs the Editor closed**; the app checks `Temp/UnityLockfile`. There is no Cancel while Unity runs.
- **Deviations from the design spec:** no `StartAssetEditing` bracket (each item is imported synchronously so it can be measured); Validate is local, and measured values come back from the import.
- **Why:** importing a model by hand repeats the same ModelImporter settings every time, and AI-generated meshes need a size correction that must not leak into gameplay colliders and ranges.
- **Rejected:** writing Unity YAML from the app (fragile, bypasses importer behaviour); scaling the gameplay root (changes colliders, agents, ranges, cover); a Unity-only editor window (the user wanted a standalone tool with drag and drop); glTFast now; a stored import database (labels in the `.meta` carry ownership).
- **Implications:** new profiles are one table row plus one importer; a schema change bumps `schemaVersion` on both sides; Blender automation, GLB, animation retargeting libraries and a read-only inspect mode are v0.2 candidates.
- **Known limitations:** source height is known only after an import; Unity cannot skip meshes in animation FBXs; only a sibling `<name>.fbm` texture folder is copied; theme registration cannot remove variants.
```

- [ ] **Step 3: Publish check.**

```bash
dotnet publish Tools/BlackglassAssetStudio/src/App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o Tools/BlackglassAssetStudio/publish
ls -la Tools/BlackglassAssetStudio/publish | head
```
Then run the launch check from Task 7 Step 8 against `Tools/BlackglassAssetStudio/publish/BlackglassAssetStudio.exe`. Expected: it starts, title `Blackglass Asset Studio`. Confirm `git status` ignores `publish/`.

- [ ] **Step 4: Full verification.** Close the Unity Editor and run, in order, capturing the totals:

```bash
dotnet test Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln
BLACKGLASS_E2E=1 dotnet test Tools/BlackglassAssetStudio/BlackglassAssetStudio.sln
Tools/run-tests.sh EditMode
Tools/run-tests.sh PlayMode
```
Expected: all green; EditMode total = baseline + new Unity-side tests; PlayMode unchanged from baseline. Then:

```bash
git status --short                      # only intended files; no Assets/_AssetStudioScratch, no manifests/results under Assets or the repo
grep -h "error CS" Logs/TestRun-EditMode.log | sort -u
grep -c "\[AssetPipeline\] ERROR" Logs/TestRun-EditMode.log   # only the intentional failing-item tests may log these
```
Expected: no `error CS`; `[AssetPipeline] ERROR` lines only from the negative tests.

- [ ] **Step 5: Walk the 24-item validation list** (from the brief) and record the evidence for each in `.claude/scratchpad/asset-studio-verification.md`. Mapping: 1 solution builds (Step 3 of Task 7/this task); 2 app launches (Task 7 Step 8, Task 13 Step 3); 3 drag-drop (manual: below); 4 profiles selectable (manual + `ProfileTests`); 5 manifest generated (`ManifestBuilderTests`, run folder contents); 6 Unity batch launches (`PipelineSmokeTests`); 7 importer reads manifest (smoke + `RunnerTests`); 8 structured result (smoke); 9 character FBX imports (`CharacterE2ETests`); 10 height normalisation (`CharacterImporterTests.NormalisesTheVisualHeight…`); 11 no gameplay-root scaling (prefab root scale 1 asserted; `git diff` shows no gameplay prefab changed); 12 manual override (`ManualScaleOverrideWins…`); 13 Humanoid/Avatar works or reports (`ARigidMesh…`, `MissingSharedAvatar…`); 14 locomotion import (`AnimationE2ETests`); 15 loop settings (`LocomotionLoops…`, `CombatDoesNotLoop…`); 16 combat/death (`AnimationE2ETests`); 17 prop (`PropEnvironmentE2ETests`); 18 environment module (same); 19 sources untouched (`StagingTests`, `CharacterE2ETests` hash check); 20 project compiles (EditMode run); 21 gameplay functional (full EditMode + PlayMode); 22 no stale manifests/temp (`git status`; `StagingTests.Only_the_newest_runs_are_kept`); 23 understandable errors (messages asserted in tests; read the Result tab on a forced failure by importing a GLB and by leaving the Editor open); 24 no unexpected Unity log errors (Step 4 greps).

- [ ] **Step 6: Manual test procedure** (the owner runs this; record the outcome). Use the Darius files as inputs; the destination is a scratch folder you delete afterwards:
  1. Close the Unity Editor. Start the app. Settings: project path is the repository, Unity version detected, shared Avatar defaults to the Darius model.
  2. Drag `Assets/Art/Characters/Darius/Models/Darius Stand Idle.fbx` onto the list. Profile: Humanoid Character. Set name `TestDarius`, destination `Assets/_AssetStudioScratch`, target height `1.88`. Validate (no errors). Import Selected.
  3. Result tab: SUCCESS, measured about 1.77 m, scale about 1.06 (Auto), Avatar humanoid, prefab `TestDarius_Visual.prefab`, changed-files list.
  4. Open Unity: select `TestDarius_Visual.prefab`: root scale (1,1,1), child `ImportedCharacter` scale about 1.064, an Animator with the Avatar. Drag it into the Character Test Scene; its height is 1.88 m.
  5. Back in the app set Manual visual scale `1.2`, Import Selected again: item says updated; the child scale is 1.2.
  6. Drag the 14 files from `Assets/Art/Characters/Darius/Animations/` onto the list. Check suggested profiles (Idle/Walk/Run → Locomotion; Fire/Reload → Combat; Hit Reaction → Reaction; Death → Death); set the shared Avatar to `Assets/_AssetStudioScratch/TestDarius.fbx`, destination `Assets/_AssetStudioScratch/Anims`; Import All. In Unity: Walk loops, Fire does not, Death bakes horizontal position, all show Humanoid copy-avatar.
  7. Drag `Assets/Art/Characters/Darius/Weapons/Rifle.fbx` as Generic Prop and a mesh as Environment Module (WallStraight): see the dimension warnings; leave theme registration at None.
  8. Drag any `.glb`: it is refused with the Blender message.
  9. Delete `Assets/_AssetStudioScratch` (and its `.meta`) in Unity or Explorer; `git status` must be clean of it.

- [ ] **Step 7: Commit** docs and decision record ("Asset Studio: manual, decision 039 and verification notes").

- [ ] **Step 8: Final report and handoff.** Produce the Completion Report the brief asks for (solution/project structure; files created and changed; app architecture; Unity importer architecture; manifest schema; profiles; height normalisation; Humanoid/Avatar behaviour; animation behaviour; environment integration; settings and path handling; logging and error handling; test coverage; how to build and publish; the exact manual procedure above; known limitations; recommended v0.2: GLB via glTFast, read-only inspect run, Blender batch cleanup, an Editor-side "assign visual to gameplay shell" helper, animation controller generation, batch rename rules). Update the project memory with a short `asset-studio-state.md` (branch `asset-studio`, not merged or pushed, results, open manual checks) and one line in `MEMORY.md`. Do not merge, push or start v0.2.
