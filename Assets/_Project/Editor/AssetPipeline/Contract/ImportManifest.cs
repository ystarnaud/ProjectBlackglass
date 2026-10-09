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
