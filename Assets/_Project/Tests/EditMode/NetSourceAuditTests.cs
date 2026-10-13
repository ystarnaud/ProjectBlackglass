using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
            string[] forbidden = { "FishNet", "Unity.Netcode", "Unity.Networking", "Mirror", "LiteNetLib", "System.Net.Sockets", "Unity.Transport", "Unity.Collections" };
            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                foreach (var token in forbidden.Where(text.Contains))
                    Assert.Fail(Path.GetFileName(file) + " must not reference '" + token + "'");
            }
        }

        [Test]
        public void TheAdapter_NeverGivesOwnership_NorSpawnsWithAnOwner()
        {
            var files = Directory.GetFiles("Assets/_Project/Scripts/Net/Adapter", "*.cs");
            Assert.That(files, Is.Not.Empty, "the audit must not pass vacuously");
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                var text = File.ReadAllText(file);
                Assert.That(text.Contains("GiveOwnership"), Is.False, name + " gives ownership of an object");
                Assert.That(text.Contains("_clientAuthoritative"), Is.False, name + " touches transform authority at runtime");
                foreach (Match call in Regex.Matches(text, @"\.Spawn\(([^()]*)\)"))
                    Assert.That(call.Groups[1].Value.Contains(","), Is.False, name + " spawns with an owner: " + call.Value);
            }
        }
    }
}
