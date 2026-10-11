using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace Blackglass
{
    /// <summary>
    /// What "a modal panel owns the input" means: while open, every action outside the named modal maps is disabled (so a
    /// weapon, a movement key or a command cannot fire from input meant for the panel) and the modal maps' actions are
    /// enabled. Close puts every action back exactly as it was, whether it was on or off. Enforce, called every frame while
    /// open, switches off any gameplay action a component re-enabled meanwhile and remembers that it wanted to be on, so
    /// the restore is still right. Time scale and tactical pause are never touched.
    /// </summary>
    public sealed class InputModalGate
    {
        readonly InputActionAsset asset;
        readonly HashSet<string> modalMaps;
        readonly Dictionary<InputAction, bool> wasEnabled = new Dictionary<InputAction, bool>();

        public InputModalGate(InputActionAsset asset, params string[] modalMaps)
        {
            this.asset = asset;
            this.modalMaps = new HashSet<string>(modalMaps);
        }

        public bool IsOpen { get; private set; }

        public void Open()
        {
            if (IsOpen || asset == null)
                return;
            wasEnabled.Clear();
            foreach (var map in asset.actionMaps)
            {
                var modal = modalMaps.Contains(map.name);
                foreach (var action in map.actions)
                {
                    wasEnabled[action] = action.enabled;
                    if (modal)
                        action.Enable();
                    else
                        action.Disable();
                }
            }
            IsOpen = true;
        }

        public void Enforce()
        {
            if (!IsOpen || asset == null)
                return;
            foreach (var map in asset.actionMaps)
            {
                var modal = modalMaps.Contains(map.name);
                foreach (var action in map.actions)
                {
                    if (modal && !action.enabled)
                        action.Enable();
                    else if (!modal && action.enabled)
                    {
                        wasEnabled[action] = true;
                        action.Disable();
                    }
                }
            }
        }

        public void Close()
        {
            if (!IsOpen)
                return;
            foreach (var pair in wasEnabled)
            {
                if (pair.Value)
                    pair.Key.Enable();
                else
                    pair.Key.Disable();
            }
            wasEnabled.Clear();
            IsOpen = false;
        }
    }
}
