using NUnit.Framework;

namespace Blackglass.Tests
{
    public class NetSelectionTests
    {
        [Test]
        public void Only_ReplacesTheSelection_AndToggleAddsOrRemoves()
        {
            var s = new NetSelection();
            s.Only(2);
            s.Toggle(1);
            Assert.That(s.Selected, Is.EqualTo(new[] { 2, 1 }));
            Assert.That(s.Primary, Is.EqualTo(2));
            s.Toggle(2);
            Assert.That(s.Selected, Is.EqualTo(new[] { 1 }));
            s.Only(1);
            Assert.That(s.Selected, Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void Next_CyclesOneOperativeInIdOrder()
        {
            var s = new NetSelection();
            s.Next(new[] { 1, 2 });
            Assert.That(s.Selected, Is.EqualTo(new[] { 1 }));
            s.Next(new[] { 1, 2 });
            Assert.That(s.Selected, Is.EqualTo(new[] { 2 }));
            s.Next(new[] { 1, 2 });
            Assert.That(s.Selected, Is.EqualTo(new[] { 1 }));
            s.Next(new int[0]);
            Assert.That(s.Count, Is.Zero);
        }

        [Test]
        public void All_SelectsEveryGivenId()
        {
            var s = new NetSelection();
            s.All(new[] { 2, 1 });
            Assert.That(s.Selected, Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void Retain_DropsTheDead()
        {
            var s = new NetSelection();
            s.All(new[] { 1, 2 });
            s.Retain(id => id == 2);
            Assert.That(s.Selected, Is.EqualTo(new[] { 2 }));
            Assert.That(s.Primary, Is.EqualTo(2));
            s.Retain(id => false);
            Assert.That(s.Primary, Is.Zero);
        }
    }
}
