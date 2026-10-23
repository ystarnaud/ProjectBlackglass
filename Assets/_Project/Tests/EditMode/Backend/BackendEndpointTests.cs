using Blackglass.Backend.Adapter;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>The backend address (Stage 13, §7): plain HTTP to a loopback address and port only; the SSH tunnel is the transport security.</summary>
    public sealed class BackendEndpointTests
    {
        [TestCase("http://127.0.0.1:5081", "http://127.0.0.1:5081")]
        [TestCase("http://[::1]:5090", "http://[::1]:5090")]
        [TestCase("http://127.0.0.1:1", "http://127.0.0.1:1")]
        [TestCase("http://127.0.0.1:65535", "http://127.0.0.1:65535")]
        public void A_loopback_address_and_port_is_accepted(string text, string baseUrl)
        {
            Assert.That(BackendEndpoint.TryParse(text, out var endpoint, out var refusal), Is.True, refusal);
            Assert.That(endpoint.BaseUrl, Is.EqualTo(baseUrl));
            Assert.That(refusal, Is.Null);
        }

        [TestCase(null, "missing")]
        [TestCase("", "missing")]
        [TestCase("https://127.0.0.1:5081", "not_http")]
        [TestCase("HTTP://127.0.0.1:5081", "not_http")]
        [TestCase("127.0.0.1:5081", "not_http")]
        [TestCase("http://localhost:5081", "not_loopback")]
        [TestCase("http://10.0.0.1:5081", "not_loopback")]
        [TestCase("http://127.0.0.2:5081", "not_loopback")]
        [TestCase("http://0.0.0.0:5081", "not_loopback")]
        [TestCase("http://[::]:5081", "not_loopback")]
        [TestCase("http://127.0.0.1:5081/x", "has_path")]
        [TestCase("http://127.0.0.1:5081/", "has_path")]
        [TestCase("http://127.0.0.1:5081?q", "has_query")]
        [TestCase("http://127.0.0.1:5081#f", "has_query")]
        [TestCase("http://u@127.0.0.1:1", "has_userinfo")]
        [TestCase("http://127.0.0.1", "bad_port")]
        [TestCase("http://127.0.0.1:", "bad_port")]
        [TestCase("http://127.0.0.1:0", "bad_port")]
        [TestCase("http://127.0.0.1:65536", "bad_port")]
        [TestCase("http://127.0.0.1:+80", "bad_port")]
        [TestCase("http://127.0.0.1:080", "bad_port")]
        [TestCase("http://[::1]", "bad_port")]
        public void Anything_else_is_refused_with_its_code(string text, string code)
        {
            Assert.That(BackendEndpoint.TryParse(text, out var endpoint, out var refusal), Is.False);
            Assert.That(endpoint, Is.Null);
            Assert.That(refusal, Is.EqualTo(code));
        }
    }
}
