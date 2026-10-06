using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ControllerHudTests
    {
        [TestCase(InputFamily.KeyboardMouse, "Input: Keyboard/Mouse")]
        [TestCase(InputFamily.Xbox, "Input: Xbox")]
        [TestCase(InputFamily.PlayStation, "Input: PlayStation")]
        [TestCase(InputFamily.Nintendo, "Input: Nintendo")]
        [TestCase(InputFamily.GenericGamepad, "Input: Generic Gamepad")]
        public void DescribeInput_NamesTheActiveFamily(InputFamily family, string expected) =>
            Assert.That(PrototypeHud.DescribeInput(family), Is.EqualTo(expected));

        [Test]
        public void DescribePromptLine_JoinsLabelsAndPrompts()
        {
            Assert.That(PrototypeHud.DescribePromptLine(("Confirm", "A"), ("Cancel", "B"), ("Queue", "LT")),
                Is.EqualTo("Confirm: A | Cancel: B | Queue: LT"));
            Assert.That(PrototypeHud.DescribePromptLine(), Is.EqualTo(string.Empty));
        }

        [Test]
        public void DescribePauseBanner_KeepsTheKeyboardWording() =>
            Assert.That(PrototypeHud.DescribePauseBanner("Space"), Is.EqualTo("TACTICAL PAUSE - Space to resume"));

        [Test]
        public void CursorView_NamesWhoOwnsTheRightStick()
        {
            Assert.That(TacticalCursorView.DescribeRightStick(true), Is.EqualTo("Right stick: Cursor"));
            Assert.That(TacticalCursorView.DescribeRightStick(false), Is.EqualTo("Right stick: Camera"));
        }

        [Test]
        public void CursorView_DescribesWhatTheCursorIsOn()
        {
            var host = new GameObject("Raider");
            try
            {
                var health = host.AddComponent<Health>();
                Assert.That(TacticalCursorView.Describe(PointerTarget.None), Is.EqualTo(string.Empty));
                Assert.That(TacticalCursorView.Describe(PointerTarget.OnGround(Vector3.zero)), Is.EqualTo("Ground"));
                Assert.That(TacticalCursorView.Describe(PointerTarget.OnHostile(health, Vector3.zero)), Is.EqualTo("Hostile Raider"));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }
    }
}
