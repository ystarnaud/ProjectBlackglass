using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio.App.ViewModels;

/// <summary>The "edit several items" panel. It holds no rules: a change is written through each item's own view model, so hand-edit tracking and
/// profile changes behave exactly as when editing one item. A field the items disagree on reads as empty (strings) or indeterminate (check boxes).</summary>
public sealed class BatchSelectionViewModel : ViewModelBase
{
    readonly IReadOnlyList<AssetItemViewModel> items;

    public BatchSelectionViewModel(IReadOnlyList<AssetItemViewModel> items) => this.items = items;

    IEnumerable<AssetItemViewModel> Animations => items.Where(i => i.IsAnimation);

    public string Title => $"Edit {items.Count} selected items";
    public bool AllAnimations => items.Count > 0 && items.All(i => i.IsAnimation);

    public IReadOnlyList<ImportProfile> Profiles => ImportProfiles.All;
    public string[] LoopOptions { get; } = { LoopModes.Auto, LoopModes.Yes, LoopModes.No };

    public ImportProfile? Profile
    {
        get
        {
            var c = BatchEdit.Common(items.Select(i => i.Profile.Id));
            return c.Mixed || c.Value == null ? null : ImportProfiles.Get(c.Value);
        }
        set
        {
            if (value == null) return;
            foreach (var i in items) i.Profile = value;
            Changed();
        }
    }

    public string Destination
    {
        get => BatchEdit.Common(items.Select(i => i.Destination)).Value ?? "";
        set { if (string.IsNullOrWhiteSpace(value)) return; foreach (var i in items) i.Destination = value.Trim(); Changed(); }
    }
    public bool DestinationMixed => BatchEdit.Common(items.Select(i => i.Destination)).Mixed;

    public string Skeleton
    {
        get => BatchEdit.Common(Animations.Select(i => i.SharedAvatarPath)).Value ?? "";
        set { if (string.IsNullOrWhiteSpace(value)) return; foreach (var i in Animations) i.SharedAvatarPath = value.Trim(); Changed(); }
    }
    public bool SkeletonMixed => BatchEdit.Common(Animations.Select(i => i.SharedAvatarPath)).Mixed;

    public string? Loop
    {
        get => BatchEdit.Common(Animations.Select(i => i.Loop)).Value;
        set { if (value == null) return; foreach (var i in Animations) i.Loop = value; Changed(); }
    }

    public bool? BakeRotation { get => Flag(i => i.BakeRotation); set => SetFlag(value, (i, v) => i.BakeRotation = v); }
    public bool? BakeHeight { get => Flag(i => i.BakeHeight); set => SetFlag(value, (i, v) => i.BakeHeight = v); }
    public bool? BakePositionXZ { get => Flag(i => i.BakePositionXZ); set => SetFlag(value, (i, v) => i.BakePositionXZ = v); }

    public bool? AllowOverwrite
    {
        get { var c = BatchEdit.Common(items.Select(i => i.AllowOverwrite)); return c.Mixed ? null : c.Value; }
        set
        {
            if (value == null) { RaiseAll(); return; } // a three-state box cycles through "mixed"; that is not a choice
            foreach (var i in items) i.AllowOverwrite = value.Value;
            Changed();
        }
    }

    bool? Flag(Func<AssetItemViewModel, bool> read)
    {
        var c = BatchEdit.Common(Animations.Select(read));
        return c.Mixed ? null : c.Value;
    }

    void SetFlag(bool? value, Action<AssetItemViewModel, bool> write)
    {
        if (value == null) { RaiseAll(); return; }
        foreach (var i in Animations) write(i, value.Value);
        Changed();
    }

    void Changed()
    {
        foreach (var i in items) i.Refresh();
        RaiseAll();
    }
}
