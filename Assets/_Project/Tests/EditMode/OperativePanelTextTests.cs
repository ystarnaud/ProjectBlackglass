using NUnit.Framework;

namespace Blackglass.Tests
{
    public class OperativePanelTextTests
    {
        [Test]
        public void ShortId_IsTheFirstEightCharacters_OrADashWhenEmpty()
        {
            Assert.That(OperativePanelView.ShortId("6f1d2a40-3b7c-4e95-8a1f-52c0d9e7b301"), Is.EqualTo("6f1d2a40"));
            Assert.That(OperativePanelView.ShortId("abc"), Is.EqualTo("abc"));
            Assert.That(OperativePanelView.ShortId(""), Is.EqualTo("-"));
            Assert.That(OperativePanelView.ShortId(null), Is.EqualTo("-"));
        }

        [Test]
        public void DescribeHeader_NameRoleAndShortId() =>
            Assert.That(OperativePanelView.DescribeHeader("Darius", "Assault", "6f1d2a40-3b7c"), Is.EqualTo("Darius - Assault [6f1d2a40]"));

        [Test]
        public void DescribeProgress_ShowsTheNextThreshold_OrMax()
        {
            Assert.That(OperativePanelView.DescribeProgress(2, 5, 130, 250), Is.EqualTo("Rank 2/5 | XP 130/250"));
            Assert.That(OperativePanelView.DescribeProgress(5, 5, 700, null), Is.EqualTo("Rank 5/5 (max) | XP 700"));
        }

        [Test]
        public void DescribeStat_ShowsBaseToEffective_OnlyWhenTheyDiffer()
        {
            Assert.That(OperativePanelView.DescribeStat("Max health", "130", "155"), Is.EqualTo("Max health: 130 -> 155"));
            Assert.That(OperativePanelView.DescribeStat("Max health", "130", "130"), Is.EqualTo("Max health: 130"));
        }

        [Test]
        public void DescribePicks_ListsThemAndFlagsAvailablePicks()
        {
            Assert.That(OperativePanelView.DescribePicks(new string[0], 0), Is.EqualTo("Picks: none"));
            Assert.That(OperativePanelView.DescribePicks(new[] { "Combat Training", "Reinforced" }, 0), Is.EqualTo("Picks: Combat Training, Reinforced"));
            Assert.That(OperativePanelView.DescribePicks(new[] { "Combat Training" }, 2), Is.EqualTo("Picks: Combat Training | PICK AVAILABLE x2"));
        }

        [Test]
        public void DescribeStatus_NotDeployedAliveOrDead()
        {
            Assert.That(OperativePanelView.DescribeStatus(false, false), Is.EqualTo("not deployed"));
            Assert.That(OperativePanelView.DescribeStatus(true, true), Is.EqualTo("alive"));
            Assert.That(OperativePanelView.DescribeStatus(true, false), Is.EqualTo("dead"));
        }

        [Test]
        public void DescribeRosterRow_OneLinePerOperative() =>
            Assert.That(OperativePanelView.DescribeRosterRow("Kestrel", "Recon", 2, 130, "dead"), Is.EqualTo("Kestrel (Recon) rank 2 XP 130 dead"));

        [Test]
        public void HudOperativeLabel_NameAndRole() =>
            Assert.That(PrototypeHud.DescribeOperative("Darius", "Assault"), Is.EqualTo("Darius (Assault)"));
    }
}
