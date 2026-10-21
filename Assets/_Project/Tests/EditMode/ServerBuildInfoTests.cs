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

        const string Revision = "0123456789abcdef0123456789abcdef01234567";
        const string Tree = "89abcdef0123456789abcdef0123456789abcdef";

        [Test]
        public void SourceLines_StampExactlyTheCaptureTakenBeforeUnityStarted()
        {
            Assert.That(ServerBuildInfo.SourceLines(Revision, Tree, "false"),
                Is.EqualTo(new[] { "revision=" + Revision, "sourceTree=" + Tree, "dirty=false" }));
            Assert.That(ServerBuildInfo.SourceLines(Revision, Tree, "true"),
                Is.EqualTo(new[] { "revision=" + Revision, "sourceTree=" + Tree, "dirty=true" }));
        }

        [TestCase(null, null, null)]                           // the Editor menu: no capture
        [TestCase(Revision, Tree, null)]                       // cleanliness missing
        [TestCase(Revision, Tree, "maybe")]
        [TestCase("abc1234", Tree, "false")]                   // not a full revision
        [TestCase("0123456789ABCDEF0123456789abcdef01234567", Tree, "false")]
        [TestCase(Revision, "", "false")]
        public void SourceLines_WithoutAValidCapture_AreUnknown_NeverClean(string revision, string tree, string dirty)
        {
            Assert.That(ServerBuildInfo.SourceLines(revision, tree, dirty),
                Is.EqualTo(new[] { "revision=unknown", "sourceTree=unknown", "dirty=unknown" }));
        }

        [Test]
        public void TheServerBuild_StampsTheCapture_AndNeverReadsGitItself()
        {
            var text = System.IO.File.ReadAllText("Assets/_Project/Editor/Server/ServerBuild.cs");
            Assert.That(text, Does.Contain("BLACKGLASS_SOURCE_REVISION"));
            Assert.That(text, Does.Contain("ServerBuildInfo.SourceLines("));
            Assert.That(text, Does.Not.Contain("status --porcelain"), "the source state is never read while Unity runs");
        }

        [Test]
        public void Describe_WithoutAFile_SaysUnknown()
        {
            Assert.That(ServerBuildInfo.Describe(ServerBuildInfo.Parse("")), Is.EqualTo("revision=unknown built=unknown buildGuid=unknown"));
        }
    }
}
