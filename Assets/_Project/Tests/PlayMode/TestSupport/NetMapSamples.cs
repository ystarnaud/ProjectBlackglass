using System.Collections.Generic;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>Synthetic public maps for the transport tests (owner ruling: prove the largest supported map crosses the wire).</summary>
    internal static class NetMapSamples
    {
        /// <summary>
        /// A valid map at every protocol limit: a 128 x 128 grid covered by 1024 floor rectangles of 4 x 4, 2048 boxes of every
        /// kind, and 64 containers. Every real map the projection accepts is no larger. Built from the limits alone, no seed.
        /// </summary>
        public static NetMap Largest()
        {
            var side = NetMap.MaxSide;
            var floors = new List<RectInt>(NetMap.MaxFloors);
            for (var y = 0; y < side; y += 4)
                for (var x = 0; x < side; x += 4)
                    floors.Add(new RectInt(x, y, 4, 4));
            var boxes = new List<NetMapBox>(NetMap.MaxBoxes);
            for (var i = 0; i < NetMap.MaxBoxes; i++)
                boxes.Add(new NetMapBox((MissionBoxKind)(i % 5), new RectInt(i * 7 % (side - 1), i * 13 % (side - 1), 1 + i % 2, 1 + i / 2 % 2),
                    0.5f + i % 7 * 0.5f));
            var containers = new List<Vector2Int>(NetMap.MaxContainers);
            for (var i = 0; i < NetMap.MaxContainers; i++)
                containers.Add(new Vector2Int(i * 2, i));
            return new NetMap(side, side, floors, boxes, containers);
        }
    }
}
