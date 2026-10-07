using UnityEngine;

namespace Blackglass
{
    /// <summary>One visual module to place: what it is, where (world, base on the ground) and its yaw. Plain data.</summary>
    public readonly struct VisualPlacement
    {
        public VisualPlacement(EnvironmentElement element, Vector3 position, float yawDegrees, Vector2Int tile)
        {
            Element = element;
            Position = position;
            YawDegrees = yawDegrees;
            Tile = tile;
        }

        public EnvironmentElement Element { get; }
        public Vector3 Position { get; }
        public float YawDegrees { get; }
        /// <summary>The tile this module belongs to (the first tile of a multi-tile module). Keys variant selection.</summary>
        public Vector2Int Tile { get; }

        public override string ToString() => $"{Element} {Tile} yaw {YawDegrees}";
    }
}
