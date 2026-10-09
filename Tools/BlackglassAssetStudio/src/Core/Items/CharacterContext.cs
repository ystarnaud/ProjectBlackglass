using Blackglass.AssetPipeline;

namespace Blackglass.AssetStudio;

/// <summary>The character a batch of animations belongs to: its name, its folder and the Humanoid model whose Avatar the clips copy.</summary>
public sealed record CharacterContext(string Name, string Folder, string ModelPath)
{
    public string AnimationsFolder => Folder + "/Animations";

    /// <summary>Works out the character from the project path of its model, e.g. Assets/Art/Characters/EnemyUnit/Models/EnemyUnit.fbx.</summary>
    public static CharacterContext FromModel(string modelPath)
    {
        var path = (modelPath ?? "").Replace('\\', '/').Trim('/');
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stem = Path.GetFileNameWithoutExtension(segments.Length > 0 ? segments[^1] : "");

        for (var i = 0; i + 1 < segments.Length; i++)
        {
            if (!segments[i].Equals("Characters", StringComparison.OrdinalIgnoreCase)) continue;
            var next = segments[i + 1];
            var nextIsFileOrModels = i + 2 >= segments.Length || next.Equals("Models", StringComparison.OrdinalIgnoreCase) || next.Equals("Model", StringComparison.OrdinalIgnoreCase);
            // Assets/.../Characters/<Name>/... names the character; Characters/X.fbx or Characters/Models/X.fbx use the file name instead.
            var name = nextIsFileOrModels ? stem : next;
            return new CharacterContext(name, string.Join('/', segments.Take(i + 1)) + "/" + name, path);
        }

        var folder = segments.Take(Math.Max(0, segments.Length - 1)).ToList();
        if (folder.Count > 1 && (folder[^1].Equals("Models", StringComparison.OrdinalIgnoreCase) || folder[^1].Equals("Model", StringComparison.OrdinalIgnoreCase)))
            folder.RemoveAt(folder.Count - 1);
        return new CharacterContext(stem, string.Join('/', folder), path);
    }
}

/// <summary>Names of the item fields a user can edit by hand; a batch update never overwrites these.</summary>
public static class ItemFields
{
    public const string Name = "name";
    public const string Destination = "destination";
    public const string ClipName = "clipName";
    public const string SharedAvatar = "sharedAvatar";
}

/// <summary>Fills an animation item from its character so a whole folder of clips needs no per-file typing.</summary>
public static class AnimationBatch
{
    public static void Apply(AssetItem item, CharacterContext character)
    {
        if (item.Import.profile != ProfileIds.HumanoidAnimation) return;
        var words = AnimationNaming.ActionWords(Path.GetFileNameWithoutExtension(item.OriginalPath), character.Name);
        if (!item.EditedFields.Contains(ItemFields.Name)) item.Import.name = AnimationNaming.AssetName(character.Name, words);
        if (!item.EditedFields.Contains(ItemFields.ClipName)) item.Import.animation.clipName = AnimationNaming.ClipName(words);
        if (!item.EditedFields.Contains(ItemFields.Destination)) item.Import.destinationFolder = character.AnimationsFolder;
        if (!item.EditedFields.Contains(ItemFields.SharedAvatar)) item.Import.animation.sharedAvatarPath = character.ModelPath;
    }
}
