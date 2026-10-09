using System.IO;
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
            // Apply reset these two to the profile defaults, so they are no longer hand-edited.
            Item.EditedFields.Remove(ItemFields.Destination);
            Item.EditedFields.Remove(ItemFields.SharedAvatar);
            RaiseAll();
            AfterProfileChange?.Invoke(this);
        }
    }

    public bool IsCharacter => Profile.Kind == ProfileIds.HumanoidCharacter;
    public bool IsAnimation => Profile.IsAnimation;
    public bool IsProp => Profile.Kind == ProfileIds.GenericProp;
    public bool IsEnvironment => Profile.Kind == ProfileIds.EnvironmentModule;

    /// <summary>Called after the profile changes, so the window can re-apply the chosen character to a clip that just became an animation.</summary>
    public Action<AssetItemViewModel>? AfterProfileChange { get; set; }

    /// <summary>Fills this item from the character if it is an animation; fields edited by hand are kept.</summary>
    public void ApplyCharacter(CharacterContext character)
    {
        AnimationBatch.Apply(Item, character);
        RaiseAll();
    }

    void Edited(string field) => Item.EditedFields.Add(field);

    public string Name
    {
        get => Import.name;
        set
        {
            Import.name = value;
            Edited(ItemFields.Name);
            // A character is saved into a folder named after it, so renaming it moves the folder unless the folder was typed by hand.
            if (Profile.Kind == ProfileIds.HumanoidCharacter && !Item.EditedFields.Contains(ItemFields.Destination))
                Import.destinationFolder = ImportProfiles.DefaultDestination(Profile, value);
            Raise();
            Raise(nameof(Destination));
        }
    }
    public string Destination { get => Import.destinationFolder; set { Import.destinationFolder = value; Edited(ItemFields.Destination); Raise(); } }
    public bool AllowOverwrite { get => Import.allowOverwrite; set { Import.allowOverwrite = value; Raise(); } }

    public float TargetHeight { get => Import.character.targetHeight; set { Import.character.targetHeight = value; Raise(); Raise(nameof(ScaleSummary)); } }
    public bool NormalizeHeight { get => Import.character.normalizeHeight; set { Import.character.normalizeHeight = value; Raise(); Raise(nameof(ScaleSummary)); } }
    public float ScaleOverride { get => Import.character.scaleOverride; set { Import.character.scaleOverride = value; Raise(); Raise(nameof(ScaleSummary)); } }
    public bool RigHumanoid { get => Import.character.rigHumanoid; set { Import.character.rigHumanoid = value; Raise(); } }
    public bool CreateAvatar { get => Import.character.createAvatar; set { Import.character.createAvatar = value; Raise(); } }
    public string MaterialMode { get => Import.character.materialMode; set { Import.character.materialMode = value; Raise(); } }
    public bool GeneratePrefab { get => Import.character.generatePrefab; set { Import.character.generatePrefab = value; Raise(); } }

    public string ClipName { get => Import.animation.clipName; set { Import.animation.clipName = value; Edited(ItemFields.ClipName); Raise(); } }
    public string SharedAvatarPath { get => Import.animation.sharedAvatarPath; set { Import.animation.sharedAvatarPath = value; Edited(ItemFields.SharedAvatar); Raise(); } }
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
