using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Lets presentation say "this screen point is over my UI" without gameplay knowing about the UI. The HUD installs the
    /// test; input components ask before they treat a press as a world click. No test installed means nothing is blocked.
    /// </summary>
    public sealed class PointerBlocker : MonoBehaviour
    {
        Func<Vector2, bool> test;

        internal void SetTest(Func<Vector2, bool> overHud) => test = overHud;

        /// <summary>Whether a test is installed (tests: the HUD removes its own when it is disabled).</summary>
        internal bool HasTest => test != null;

        public bool IsBlocking(Vector2 screen) => test != null && test(screen);
    }
}
