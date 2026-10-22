using System.Globalization;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>The loopback fixture checkpoint: a ground point as the operator reads it in the server log and types it to the client.</summary>
    public class NetGroundTests
    {
        [Test]
        public void Format_IsXCommaZ_WithOneDecimal_IgnoringHeight()
        {
            Assert.That(NetGround.Format(new Vector3(12.34f, 5f, -4f)), Is.EqualTo("12.3,-4.0"));
            Assert.That(NetGround.Format(new Vector3(-0.06f, 0f, 7.26f)), Is.EqualTo("-0.1,7.3"));
        }

        [Test]
        public void Format_AndParse_IgnoreTheCurrentCulture()
        {
            var before = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");
                Assert.That(NetGround.Format(new Vector3(1.5f, 0f, -2.5f)), Is.EqualTo("1.5,-2.5"));
                Assert.That(NetGround.TryParse("1.5,-2.5", out var point), Is.True);
                Assert.That(point, Is.EqualTo(new Vector3(1.5f, 0f, -2.5f)));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = before;
            }
        }

        [Test]
        public void Parse_ReadsWhatFormatWrites()
        {
            Assert.That(NetGround.TryParse(NetGround.Format(new Vector3(-12.5f, 3f, 3f)), out var point), Is.True);
            Assert.That(point, Is.EqualTo(new Vector3(-12.5f, 0f, 3f)));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("3")]
        [TestCase("1,2,3")]
        [TestCase("a,1")]
        [TestCase("1,")]
        [TestCase("NaN,1")]
        [TestCase("1,Infinity")]
        [TestCase("1e40,0")]
        public void Parse_RefusesAnythingElse(string text)
        {
            Assert.That(NetGround.TryParse(text, out var point), Is.False);
            Assert.That(point, Is.EqualTo(Vector3.zero));
        }
    }
}
