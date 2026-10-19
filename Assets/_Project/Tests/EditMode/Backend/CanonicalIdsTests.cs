using Blackglass.Backend.Core;
using NUnit.Framework;

namespace Blackglass.Tests
{
    public sealed class CanonicalIdsTests
    {
        const string Attempt = "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41";
        const string Hash = "sha256:32de05dc0000000000000000000000000000000000000000000000000000abcd";

        [Test]
        public void Uuid_forms()
        {
            Assert.That(CanonicalIds.IsUuid(Attempt), Is.True);
            Assert.That(CanonicalIds.IsUuid("0192f1a5-0000-4000-8000-000000000001"), Is.True);
            Assert.That(CanonicalIds.IsUuid(Attempt.ToUpperInvariant()), Is.False);
            Assert.That(CanonicalIds.IsUuid(Attempt.Replace("-", "")), Is.False);
            Assert.That(CanonicalIds.IsUuid("{" + Attempt + "}"), Is.False);
            Assert.That(CanonicalIds.IsUuid(Attempt.Substring(0, 35) + "g"), Is.False);
            Assert.That(CanonicalIds.IsUuid(null), Is.False);
            Assert.That(CanonicalIds.IsUuid(Attempt + "\n"), Is.False);
        }

        [Test]
        public void Attempt_ids_are_version_7_with_the_rfc_variant()
        {
            Assert.That(CanonicalIds.IsAttemptId(Attempt), Is.True);
            Assert.That(CanonicalIds.IsAttemptId("0192f1a4-5c2e-4b1d-9a0e-3f6c1d2b8e41"), Is.False);
            Assert.That(CanonicalIds.IsAttemptId("0192f1a4-5c2e-7b1d-ca0e-3f6c1d2b8e41"), Is.False);
            Assert.That(CanonicalIds.IsAttemptId(Attempt.ToUpperInvariant()), Is.False);
            Assert.That(CanonicalIds.IsAttemptId(Attempt + "\n"), Is.False);
        }

        [Test]
        public void Sha256_form()
        {
            Assert.That(CanonicalIds.IsSha256(Hash), Is.True);
            Assert.That(CanonicalIds.IsSha256(Hash.Substring(0, Hash.Length - 1)), Is.False);
            Assert.That(CanonicalIds.IsSha256(Hash + "0"), Is.False);
            Assert.That(CanonicalIds.IsSha256(Hash.ToUpperInvariant()), Is.False);
            Assert.That(CanonicalIds.IsSha256("SHA256:" + Hash.Substring(7)), Is.False);
            Assert.That(CanonicalIds.IsSha256(Hash + "\n"), Is.False);
        }

        [Test]
        public void Layout_hash_form()
        {
            Assert.That(CanonicalIds.IsLayoutHash("cf2e14dece9cc2a6"), Is.True);
            Assert.That(CanonicalIds.IsLayoutHash("CF2E14DECE9CC2A6"), Is.False);
            Assert.That(CanonicalIds.IsLayoutHash("cf2e14dece9cc2a"), Is.False);
            Assert.That(CanonicalIds.IsLayoutHash("cf2e14dece9cc2a6\n"), Is.False);
        }

        [Test]
        public void Definition_id_form()
        {
            Assert.That(CanonicalIds.IsDefinitionId("item.medkit"), Is.True);
            Assert.That(CanonicalIds.IsDefinitionId("item.alpha-rifle"), Is.True);
            Assert.That(CanonicalIds.IsDefinitionId("Item.Medkit"), Is.False);
            Assert.That(CanonicalIds.IsDefinitionId("item..x"), Is.False);
            Assert.That(CanonicalIds.IsDefinitionId("item_x"), Is.False);
            Assert.That(CanonicalIds.IsDefinitionId(".item"), Is.False);
            Assert.That(CanonicalIds.IsDefinitionId("item."), Is.False);
            Assert.That(CanonicalIds.IsDefinitionId("item medkit"), Is.False);
            Assert.That(CanonicalIds.IsDefinitionId(new string('a', 64)), Is.True);
            Assert.That(CanonicalIds.IsDefinitionId(new string('a', 65)), Is.False);
            Assert.That(CanonicalIds.IsDefinitionId(""), Is.False);
            Assert.That(CanonicalIds.IsDefinitionId("item.medkit\n"), Is.False);
        }

        [Test]
        public void Word_form()
        {
            Assert.That(CanonicalIds.IsWord("extract", 64), Is.True);
            Assert.That(CanonicalIds.IsWord("reach_beacon.2-b", 64), Is.True);
            Assert.That(CanonicalIds.IsWord("Extract", 64), Is.False);
            Assert.That(CanonicalIds.IsWord("a__b", 64), Is.False);
            Assert.That(CanonicalIds.IsWord("_a", 64), Is.False);
            Assert.That(CanonicalIds.IsWord(new string('a', 65), 64), Is.False);
            Assert.That(CanonicalIds.IsWord("", 64), Is.False);
            Assert.That(CanonicalIds.IsWord("extract\n", 64), Is.False);
        }

        [Test]
        public void Build_revision_form()
        {
            Assert.That(CanonicalIds.IsBuildRevision("r1"), Is.True);
            Assert.That(CanonicalIds.IsBuildRevision("R1.2_b-3"), Is.True);
            Assert.That(CanonicalIds.IsBuildRevision("-r1"), Is.False);
            Assert.That(CanonicalIds.IsBuildRevision("r 1"), Is.False);
            Assert.That(CanonicalIds.IsBuildRevision(new string('a', 64)), Is.True);
            Assert.That(CanonicalIds.IsBuildRevision(new string('a', 65)), Is.False);
            Assert.That(CanonicalIds.IsBuildRevision(""), Is.False);
            Assert.That(CanonicalIds.IsBuildRevision("r1\n"), Is.False);
        }
    }
}
