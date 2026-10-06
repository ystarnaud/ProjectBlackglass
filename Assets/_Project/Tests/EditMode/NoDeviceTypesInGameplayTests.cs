using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// Gameplay and input components consume semantic actions only: physical devices, face-button names and device types
    /// are allowed in Scripts/Input (and nowhere else), so controller type cannot leak into movement, combat, cover,
    /// command queues or AI.
    /// </summary>
    public class NoDeviceTypesInGameplayTests
    {
        static readonly string[] Forbidden =
        {
            "Gamepad.current", "Keyboard.current", "Mouse.current", "Pointer.current",
            "buttonSouth", "buttonEast", "buttonWest", "buttonNorth",
            "XInputController", "DualShock", "DualSense", "SwitchPro",
        };

        [Test]
        public void OnlyScriptsInput_NamesDevicesOrPhysicalButtons()
        {
            var root = Path.Combine(Application.dataPath, "_Project", "Scripts");
            var checkedFiles = 0;
            foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var normalised = file.Replace('\\', '/');
                if (normalised.Contains("/Scripts/Input/"))
                    continue;
                var text = File.ReadAllText(file);
                foreach (var token in Forbidden)
                    Assert.That(text, Does.Not.Contain(token), $"{normalised} mentions '{token}'");
                checkedFiles++;
            }
            Assert.That(checkedFiles, Is.GreaterThan(30), "The scan found suspiciously few gameplay files");
        }
    }
}
