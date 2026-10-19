using System;
using Blackglass.Backend.Core;
using NUnit.Framework;

namespace Blackglass.Tests
{
    public sealed class Base58IdTests
    {
        [TestCase(1L, "2")]
        [TestCase(57L, "z")]
        [TestCase(58L, "21")]
        [TestCase(3364L, "211")]
        [TestCase(long.MaxValue, "NQm6nKp8qFC")]
        public void Round_trips(long id, string text)
        {
            Assert.That(Base58Id.Format(id), Is.EqualTo(text));
            Assert.That(Base58Id.TryParse(text, out var parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(id));
        }

        [TestCase("")]
        [TestCase(null)]
        [TestCase("1")]
        [TestCase("12")]
        [TestCase("0")]
        [TestCase("O")]
        [TestCase("I")]
        [TestCase("l")]
        [TestCase("NQm6nKp8qFD")]
        [TestCase("222222222222")]
        [TestCase("2é")]
        [TestCase("2 ")]
        [TestCase("2\n")]
        public void Refuses_non_canonical_text(string text)
        {
            Assert.That(Base58Id.TryParse(text, out var id), Is.False);
            Assert.That(id, Is.EqualTo(0));
        }

        [Test]
        public void Is_case_sensitive()
        {
            Assert.That(Base58Id.TryParse("2k", out var lower), Is.True);
            Assert.That(Base58Id.TryParse("2K", out var upper), Is.True);
            Assert.That(lower, Is.Not.EqualTo(upper));
        }

        [TestCase("JG")]
        [TestCase("C")]
        [TestCase("D")]
        [TestCase("2k")]
        [TestCase("2m")]
        [TestCase("2n")]
        public void Accepts_the_vector_ids(string text)
        {
            Assert.That(Base58Id.TryParse(text, out var id), Is.True);
            Assert.That(Base58Id.Format(id), Is.EqualTo(text));
        }

        [Test]
        public void Orders_numerically_not_lexically()
        {
            Base58Id.TryParse("z", out var z);
            Base58Id.TryParse("21", out var twentyOne);
            Assert.That(z, Is.LessThan(twentyOne));
            Assert.That(string.CompareOrdinal("z", "21"), Is.GreaterThan(0));
        }

        [TestCase(0L)]
        [TestCase(-1L)]
        public void Format_refuses_non_positive(long id) =>
            Assert.Throws<ArgumentOutOfRangeException>(() => Base58Id.Format(id));
    }
}
