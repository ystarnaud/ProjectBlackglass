using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>How much of the layout the player starts with.</summary>
    public enum MapKnowledge
    {
        /// <summary>Every region is known from the start.</summary>
        Full,
        /// <summary>The friendly room and its corridors, plus the extraction room.</summary>
        Partial,
        /// <summary>Nothing but what the squad can see.</summary>
        None,
    }

    /// <summary>Which objectives the player starts knowing about (an unknown objective still exists and still completes).</summary>
    public enum ObjectiveKnowledge
    {
        All,
        /// <summary>Extraction and the Interact objective; the elimination objective is learned in play.</summary>
        ExtractionAndTerminal,
        ExtractionOnly,
        None,
    }

    /// <summary>
    /// Per-mission battlefield-uncertainty configuration. Plain serializable data inside MissionSettings. The default is
    /// "nothing hidden" (fog off), so existing scenes and tests behave exactly as before; a preset opts a mission in.
    /// </summary>
    [Serializable]
    public sealed class IntelligenceSettings
    {
        public const int PresetCount = 5;

        /// <summary>False: everything is known and nothing is hidden; the rest of the settings (except cameras) is ignored.</summary>
        public bool fogEnabled;
        public MapKnowledge map = MapKnowledge.Full;
        public ObjectiveKnowledge objectives = ObjectiveKnowledge.All;
        /// <summary>List an unknown objective by its vague title ("Locate the data terminal") instead of hiding it.</summary>
        public bool showUnknownObjectives = true;
        /// <summary>This many hostiles (spawn order) start as last-known markers.</summary>
        public int enemyMarkersAtStart;
        /// <summary>How far a friendly sees (360 degrees, needs line of sight). Above the 12 m enemy detection range by default.</summary>
        public float observationRange = 14f;
        /// <summary>Security cameras (and one camera-control terminal when above zero).</summary>
        public int cameraCount;
        public float cameraRange = 12f;
        /// <summary>The full field-of-view angle of a camera, degrees.</summary>
        public float cameraFov = 90f;
        /// <summary>True: hacked cameras keep observing live; false: the hack reveals what they cover once.</summary>
        public bool cameraStaysLive = true;
        /// <summary>How long a hostile that fires stays observed, scaled seconds.</summary>
        public float exposureSeconds = 3f;

        /// <summary>A clamped copy; the original is untouched.</summary>
        public IntelligenceSettings Validated()
        {
            var copy = (IntelligenceSettings)MemberwiseClone();
            copy.enemyMarkersAtStart = Mathf.Clamp(enemyMarkersAtStart, 0, 8);
            copy.observationRange = Mathf.Clamp(observationRange, 4f, 40f);
            copy.cameraCount = Mathf.Clamp(cameraCount, 0, 6);
            copy.cameraRange = Mathf.Clamp(cameraRange, 4f, 40f);
            copy.cameraFov = Mathf.Clamp(cameraFov, 30f, 180f);
            copy.exposureSeconds = Mathf.Clamp(exposureSeconds, 0f, 10f);
            return copy;
        }

        /// <summary>One line with every setting.</summary>
        public string Describe() =>
            FormattableString.Invariant($"fog={fogEnabled} map={map} objectives={objectives} listUnknown={showUnknownObjectives} ") +
            FormattableString.Invariant($"markers={enemyMarkersAtStart} sight={observationRange:0.#} cameras={cameraCount} camRange={cameraRange:0.#} ") +
            FormattableString.Invariant($"camFov={cameraFov:0.#} live={cameraStaysLive} expose={exposureSeconds:0.#}");

        public static IntelligenceSettings Full() => new IntelligenceSettings();

        /// <summary>No map, only the extraction known, hostiles hidden, three cameras and their control terminal.</summary>
        public static IntelligenceSettings Blind() => new IntelligenceSettings
        {
            fogEnabled = true, map = MapKnowledge.None, objectives = ObjectiveKnowledge.ExtractionOnly, cameraCount = 3,
        };

        /// <summary>The whole layout known; the objectives and hostiles are not.</summary>
        public static IntelligenceSettings LayoutKnown() => new IntelligenceSettings
        {
            fogEnabled = true, map = MapKnowledge.Full, objectives = ObjectiveKnowledge.None,
        };

        /// <summary>The objectives known; the layout and hostiles are not.</summary>
        public static IntelligenceSettings ObjectivesKnown() => new IntelligenceSettings
        {
            fogEnabled = true, map = MapKnowledge.None, objectives = ObjectiveKnowledge.All,
        };

        /// <summary>A briefing: partial layout, extraction and the terminal known, one enemy marker.</summary>
        public static IntelligenceSettings Briefed() => new IntelligenceSettings
        {
            fogEnabled = true, map = MapKnowledge.Partial, objectives = ObjectiveKnowledge.ExtractionAndTerminal, enemyMarkersAtStart = 1,
        };

        /// <summary>Preset by index; any index wraps around.</summary>
        public static IntelligenceSettings Preset(int index)
        {
            switch (((index % PresetCount) + PresetCount) % PresetCount)
            {
                case 1: return Blind();
                case 2: return LayoutKnown();
                case 3: return ObjectivesKnown();
                case 4: return Briefed();
                default: return Full();
            }
        }

        public static string PresetName(int index)
        {
            switch (((index % PresetCount) + PresetCount) % PresetCount)
            {
                case 1: return "Blind";
                case 2: return "Layout known";
                case 3: return "Objectives known";
                case 4: return "Briefed";
                default: return "Full knowledge";
            }
        }
    }
}
