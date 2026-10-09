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
