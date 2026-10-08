using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Draws a security device (a camera, the camera-control terminal) only once the player has found it (decision 037).
    /// Renderers only: the terminal's collider and the interactable stay (the pointer and the prompt gate it separately).
    /// </summary>
    public sealed class DevicePresenter : MonoBehaviour
    {
        IntelligenceService intelligence;
        int deviceId;
        Renderer[] renderers = new Renderer[0];
        bool drawn = true;

        public void Bind(IntelligenceService service, int id)
        {
            intelligence = service;
            deviceId = id;
            renderers = GetComponentsInChildren<Renderer>(true);
            drawn = true;
            Apply();
        }

        void LateUpdate() => Apply();

        void Apply()
        {
            var shown = intelligence == null || intelligence.IsDeviceShown(deviceId);
            if (shown == drawn)
                return;
            drawn = shown;
            foreach (var renderer in renderers)
            {
                if (renderer != null)
                    renderer.forceRenderingOff = !shown;
            }
        }

        /// <summary>Attaches a presenter to the mission's camera-control terminal and to every camera.</summary>
        public static void AttachTo(GeneratedMission mission, IntelligenceService service)
        {
            if (mission == null || service == null)
                return;
            if (mission.CameraTerminal != null)
                mission.CameraTerminal.gameObject.AddComponent<DevicePresenter>().Bind(service, SecurityPlan.TerminalDeviceId);
            if (mission.Network == null)
                return;
            foreach (var camera in mission.Network.Cameras)
            {
                if (camera.Visual != null)
                    camera.Visual.AddComponent<DevicePresenter>().Bind(service, camera.DeviceId);
            }
        }
    }
}
