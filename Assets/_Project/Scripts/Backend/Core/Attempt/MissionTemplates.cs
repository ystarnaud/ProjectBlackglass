using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Blackglass.Backend.Core
{
    /// <summary>A mission template this build can run: its id, version and operative capacity (the backend's mission_templates row).</summary>
    public sealed class MissionTemplateSpec
    {
        public MissionTemplateSpec(string id, int version, int minOperatives, int maxOperatives)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Version = version;
            MinOperatives = minOperatives;
            MaxOperatives = maxOperatives;
        }

        public string Id { get; }
        public int Version { get; }
        public int MinOperatives { get; }
        public int MaxOperatives { get; }
    }

    /// <summary>
    /// The templates this build supports (owner ruling, Stage 12 Track L final rulings §2): exactly procedural-standard:1, with
    /// the backend's capacity of 1 to 3 operatives (migration 0007). Anything else is refused before gameplay
    /// (AttemptStart: unsupported_template, unsupported_squad_size); there is no fallback to another template.
    /// </summary>
    public static class MissionTemplates
    {
        public static readonly IReadOnlyList<MissionTemplateSpec> Supported =
            new ReadOnlyCollection<MissionTemplateSpec>(new[] { new MissionTemplateSpec("procedural-standard", 1, 1, 3) });
    }
}
