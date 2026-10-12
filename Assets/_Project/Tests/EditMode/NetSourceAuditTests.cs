using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>Keeps the networking core independent of any networking library (spec section 3.2, CLAUDE.md).</summary>
    public class NetSourceAuditTests
    {
        const string Folder = "Assets/_Project/Scripts/Net/Core";

        [Test]
        public void TheCoreFiles_ReferenceNoNetworkingLibrary()
        {
            var files = Directory.GetFiles(Folder, "*.cs");
            Assert.That(files, Is.Not.Empty, "the audit must not pass vacuously");
            string[] forbidden = { "FishNet", "Unity.Netcode", "Unity.Networking", "Mirror", "LiteNetLib", "System.Net.Sockets" };
            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                foreach (var token in forbidden.Where(text.Contains))
                    Assert.Fail(Path.GetFileName(file) + " must not reference '" + token + "'");
            }
        }
    }
}
