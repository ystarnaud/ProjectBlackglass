using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>One security camera: where it hangs, where it faces, how far and how wide it sees. Coverage also needs line of sight, which the service tests.</summary>
    public readonly struct CameraSpec
    {
        public CameraSpec(int deviceId, Vector3 position, Vector3 forward, float range, float halfAngleDegrees, GameObject visual = null)
        {
            DeviceId = deviceId;
            Position = position;
            Forward = forward;
            Range = range;
            HalfAngle = halfAngleDegrees;
            Visual = visual;
        }

        public int DeviceId { get; }
        public Vector3 Position { get; }
        public Vector3 Forward { get; }
        public float Range { get; }
        /// <summary>Half of the field of view, degrees.</summary>
        public float HalfAngle { get; }
        /// <summary>The scene object (no collider), or null in tests.</summary>
        public GameObject Visual { get; }

        /// <summary>Inside the range and the cone. Sight is a separate test (a wall between still blocks it).</summary>
        public bool Covers(Vector3 point) => ObservationRules.InCone(Position, Forward, HalfAngle, Range, point);
    }

    /// <summary>The mission's cameras and whether the player has hacked them. Plain state; the service reads it every pass.</summary>
    public sealed class CameraNetwork
    {
        readonly List<CameraSpec> cameras;

        public CameraNetwork(IReadOnlyList<CameraSpec> cameras)
        {
            this.cameras = new List<CameraSpec>(cameras ?? throw new ArgumentNullException(nameof(cameras)));
        }

        public IReadOnlyList<CameraSpec> Cameras => cameras;
        public bool Compromised { get; private set; }

        public void Compromise() => Compromised = true;
    }
}
