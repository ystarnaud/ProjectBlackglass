using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>
    /// The backend core stays library-free (spec §2.3, plan D1): its assembly definition references nothing and excludes the
    /// engine, and no source file names a networking or engine library. The asmdef is the real boundary; this audit pins it.
    /// </summary>
    public sealed class CoreSourceAuditTests
    {
        static string CoreFolder => Path.Combine(ContractVectors.ProjectRoot, "Assets", "_Project", "Scripts", "Backend", "Core");

        static readonly Regex Library = new Regex(
            @"\b(using\s+)?(UnityEngine|UnityEditor|FishNet|GameKit|Unity\.Netcode|Unity\.Networking|Unity\.Transport|Unity\.Collections|System\.Net|Mirror|LiteNetLib)\b\s*[.;]",
            RegexOptions.CultureInvariant);

        static string WithoutComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(source, @"//[^\n]*", " ");
        }

        [Test]
        public void Every_core_file_names_no_library()
        {
            var files = Directory.GetFiles(CoreFolder, "*.cs", SearchOption.AllDirectories);
            Assert.That(files, Is.Not.Empty, "the audit must not pass vacuously");
            foreach (var file in files)
            {
                var code = WithoutComments(File.ReadAllText(file));
                Assert.That(Library.IsMatch(code), Is.False, file + " names a library");
                Assert.That(code.Contains("UnityWebRequest"), Is.False, file + " names UnityWebRequest");
            }
        }

        [Test]
        public void The_core_assembly_definition_has_no_references()
        {
            var text = Regex.Replace(File.ReadAllText(Path.Combine(CoreFolder, "Blackglass.Backend.Core.asmdef")), @"\s+", "");
            StringAssert.Contains("\"references\":[]", text);
            StringAssert.Contains("\"noEngineReferences\":true", text);
            StringAssert.Contains("\"overrideReferences\":true", text);
            StringAssert.Contains("\"precompiledReferences\":[]", text);
            StringAssert.Contains("\"allowUnsafeCode\":false", text);
        }

        [Test]
        public void The_gameplay_assembly_does_not_reference_core_yet()
        {
            var text = File.ReadAllText(Path.Combine(ContractVectors.ProjectRoot, "Assets", "_Project", "Scripts", "Blackglass.asmdef"));
            StringAssert.DoesNotContain("Blackglass.Backend.Core", text);
        }

        [Test]
        public void The_core_assembly_loads_without_engine_types()
        {
            var assembly = typeof(Blackglass.Backend.Core.Base58Id).Assembly;
            var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
            Assert.That(referenced.Any(n => n.StartsWith("UnityEngine") || n.StartsWith("FishNet") || n.StartsWith("Unity.")), Is.False,
                string.Join(", ", referenced));
        }
    }
}
