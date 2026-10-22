using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>
    /// The offline scenes are byte for byte the accepted ones (owner, 2026-10-22: the offline prototype stays unchanged). Line
    /// endings are ignored. A deliberate offline change needs the owner's authorization and a new hash here.
    /// </summary>
    public class OfflineSceneGuardTests
    {
        [TestCase("Assets/_Project/Scenes/ProceduralMission.unity", "89ba4eea16fe964aae80fac83ca2a927e1a2b4edcc5cfbefb636c2d6320df9c8")]
        [TestCase("Assets/_Project/Scenes/Prototype.unity", "b5a15d0ac7aa5ab27c53bfde9f5502af2a7201f27bc5628bd42b4c355a27ae1b")]
        public void TheOfflineScene_IsTheAcceptedOne(string path, string sha256)
        {
            var bytes = File.ReadAllBytes(path).Where(b => b != (byte)'\r').ToArray();
            using (var sha = SHA256.Create())
            {
                var hash = string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
                Assert.That(hash, Is.EqualTo(sha256), path + " changed");
            }
        }
    }
}
