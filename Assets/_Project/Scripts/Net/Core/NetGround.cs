using System;
using System.Globalization;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A ground point as an operator reads and types it in the loopback fixture checkpoint: "x,z" in metres with one decimal,
    /// invariant culture. The fixture server's log writes it, the net-test client's overlay shows it under the cursor, and the
    /// scripted client's -assistHostile and -assistExtraction read it. Height is dropped: the ground is the plane y = 0.
    /// </summary>
    public static class NetGround
    {
        public static string Format(Vector3 point) => FormattableString.Invariant($"{point.x:0.0},{point.z:0.0}");

        /// <summary>Exactly two finite numbers separated by one comma; anything else is refused with the zero point.</summary>
        public static bool TryParse(string text, out Vector3 point)
        {
            point = Vector3.zero;
            if (string.IsNullOrEmpty(text))
                return false;
            var parts = text.Split(',');
            if (parts.Length != 2 || !TryNumber(parts[0], out var x) || !TryNumber(parts[1], out var z))
                return false;
            point = new Vector3(x, 0f, z);
            return true;
        }

        static bool TryNumber(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
