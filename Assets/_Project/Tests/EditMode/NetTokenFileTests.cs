using NUnit.Framework;

namespace Blackglass.Tests
{
    public class NetTokenFileTests
    {
        [Test]
        public void AValidFile_GivesOneEntryPerLine()
        {
            var text = "# development tokens\r\naccount-a tok-a squad\r\n\r\naccount-b tok-b none\r\naccount-t tok-t none other-attempt\r\n";
            var entries = NetTokenFile.Parse(text, out var warnings);

            Assert.That(warnings, Is.Empty);
            Assert.That(entries, Has.Count.EqualTo(3));
            Assert.That(entries[0].Account, Is.EqualTo("account-a"));
            Assert.That(entries[0].Token, Is.EqualTo("tok-a"));
            Assert.That(entries[0].OwnsSquad, Is.True);
            Assert.That(entries[0].Attempt, Is.Null);
            Assert.That(entries[1].OwnsSquad, Is.False);
            Assert.That(entries[2].Attempt, Is.EqualTo("other-attempt"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   \n# only a comment\n")]
        public void NoEntries_GiveAnEmptyList(string text)
        {
            var entries = NetTokenFile.Parse(text, out var warnings);
            Assert.That(entries, Is.Empty);
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void BadLines_AreSkipped_AndNeverEchoTheToken()
        {
            const string secret = "s3cr3t-token-value";
            var text = "only-two " + secret + "\n" +
                       "acct " + secret + "-b maybe\n" +
                       "a b c d e\n" +
                       "ok tok-ok squad\n";
            var entries = NetTokenFile.Parse(text, out var warnings);

            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Token, Is.EqualTo("tok-ok"));
            Assert.That(warnings, Has.Count.EqualTo(3));
            foreach (var warning in warnings)
            {
                StringAssert.DoesNotContain(secret, warning);
                StringAssert.DoesNotContain("tok-ok", warning);
                StringAssert.StartsWith("line ", warning);
            }
        }

        [Test]
        public void ADuplicateToken_IsSkippedWithAWarning()
        {
            var entries = NetTokenFile.Parse("a dup-7f3a squad\nb dup-7f3a none\n", out var warnings);
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Account, Is.EqualTo("a"));
            Assert.That(warnings, Has.Count.EqualTo(1));
            StringAssert.DoesNotContain("dup-7f3a", warnings[0]);
        }

        [Test]
        public void TabsAndExtraSpaces_SeparateFields()
        {
            var entries = NetTokenFile.Parse("a\t tok-a \t squad\n", out var warnings);
            Assert.That(warnings, Is.Empty);
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Token, Is.EqualTo("tok-a"));
        }
    }
}
