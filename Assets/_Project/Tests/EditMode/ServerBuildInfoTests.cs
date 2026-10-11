using NUnit.Framework;

namespace Blackglass.Tests
{
    public class ServerBuildInfoTests
    {
        [Test]
        public void Parse_ReadsKeyValueLines_AndSkipsTheRest()
        {
            var values = ServerBuildInfo.Parse("revision=abc123\r\n\n# comment\nnot a pair\ndirty=true\n builtUtc = 2026-10-11T17:00:00Z \n");
            Assert.That(values["revision"], Is.EqualTo("abc123"));
            Assert.That(values["dirty"], Is.EqualTo("true"));
            Assert.That(values["builtUtc"], Is.EqualTo("2026-10-11T17:00:00Z"));
            Assert.That(values.Count, Is.EqualTo(3));
        }

        [Test]
        public void Parse_OfNothing_IsEmpty()
        {
            Assert.That(ServerBuildInfo.Parse(null), Is.Empty);
        }

        [Test]
        public void Describe_ShowsRevisionDirtyAndTime()
        {
            var values = ServerBuildInfo.Parse("revision=abc123\ndirty=true\nbuiltUtc=2026-10-11T17:00:00Z\nbuildGuid=g1");
            Assert.That(ServerBuildInfo.Describe(values), Is.EqualTo("revision=abc123+dirty built=2026-10-11T17:00:00Z buildGuid=g1"));
        }

        [Test]
        public void Describe_WithoutAFile_SaysUnknown()
        {
            Assert.That(ServerBuildInfo.Describe(ServerBuildInfo.Parse("")), Is.EqualTo("revision=unknown built=unknown buildGuid=unknown"));
        }
    }
}
