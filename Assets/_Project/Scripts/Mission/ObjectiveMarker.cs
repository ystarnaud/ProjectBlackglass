using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Tints the renderers under it from a colour function each frame, through a MaterialPropertyBlock (no material instance
    /// is created, so nothing leaks when the mission is destroyed). Debug-quality feedback: the terminal shows whether it can
    /// be used, is being worked or is done; the extraction zone shows locked, open or done.
    /// </summary>
    public sealed class ObjectiveMarker : MonoBehaviour
    {
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int LegacyColor = Shader.PropertyToID("_Color");
        static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

        static readonly Color Locked = new Color(0.45f, 0.45f, 0.5f);
        static readonly Color Available = new Color(0.2f, 0.75f, 0.95f);
        static readonly Color Working = new Color(0.95f, 0.8f, 0.2f);
        static readonly Color Done = new Color(0.15f, 0.5f, 0.2f);

        Func<Color> colour;
        Renderer[] renderers;
        MaterialPropertyBlock block;

        public static Color TerminalColour(MissionInteractable terminal)
        {
            if (terminal == null)
                return Locked;
            if (terminal.IsCompleted)
                return Done;
            if (!terminal.IsAvailable)
                return Locked;
            return terminal.User != null ? Working : Available;
        }

        public static Color ZoneColour(ObjectiveState state)
        {
            switch (state)
            {
                case ObjectiveState.Active:
                    return new Color(0.2f, 0.9f, 0.3f);
                case ObjectiveState.Completed:
                    return Done;
                case ObjectiveState.Failed:
                    return new Color(0.9f, 0.2f, 0.2f);
                default:
                    return Locked;
            }
        }

        public void Bind(Func<Color> source, Renderer[] targets = null)
        {
            colour = source;
            renderers = targets;
            Apply();
        }

        void Update() => Apply();

        void Apply()
        {
            if (colour == null)
                return;
            renderers ??= GetComponentsInChildren<Renderer>();
            block ??= new MaterialPropertyBlock();
            var tint = colour();
            foreach (var renderer in renderers)
            {
                if (renderer == null)
                    continue;
                renderer.GetPropertyBlock(block);
                block.SetColor(BaseColor, tint);
                block.SetColor(LegacyColor, tint);
                block.SetColor(EmissionColor, tint * 1.5f); // materials without emission ignore it
                renderer.SetPropertyBlock(block);
            }
        }
    }
}
