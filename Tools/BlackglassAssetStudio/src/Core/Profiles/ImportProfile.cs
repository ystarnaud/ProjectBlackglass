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
