using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Response curve for an analog stick after its dead zone: keeps the direction, clamps the magnitude to 1 and raises
    /// it to a power, so small deflections are fine and full deflection is full speed. Pure.
    /// </summary>
    public static class StickResponse
    {
        public static Vector2 Curve(Vector2 value, float exponent)
        {
            var magnitude = Mathf.Min(1f, value.magnitude);
            if (magnitude <= 0f)
                return Vector2.zero;
            return value.normalized * Mathf.Pow(magnitude, Mathf.Max(1f, exponent));
        }
    }
}
