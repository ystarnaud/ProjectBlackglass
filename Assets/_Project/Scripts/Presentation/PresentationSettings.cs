using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Presentation switches. Turning any of them changes only what is shown, never a gameplay result.</summary>
    [Serializable]
    public sealed class PresentationSettings
    {
        public bool enabled = true;
        public PresentationQuality quality = PresentationQuality.High;
        [Range(0f, 1f)] public float masterVolume = 0.8f;
        /// <summary>0 turns the camera impulse off.</summary>
        [Range(0f, 1f)] public float cameraShake = 0.5f;
    }
}
