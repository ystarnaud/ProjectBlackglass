using System.Security.Cryptography;
using Blackglass.Accounts.Domain.Auth;

namespace Blackglass.Accounts.Domain.Tests.Auth;

public class OpaqueSecretTests
{
    [Fact]
    public void A_new_secret_is_256_bits_in_its_prefixed_text_form_and_round_trips()
    {
        var secret = OpaqueSecret.New(OpaqueSecret.AccessTokenPrefix);
        Assert.Matches("^at1_[A-Za-z0-9_-]{43}$", secret.Text);
        Assert.True(OpaqueSecret.TryParse(secret.Text, OpaqueSecret.AccessTokenPrefix, out var parsed));
        Assert.Equal(secret.Hash(), parsed!.Hash());
        Assert.Equal(32, secret.Hash().Length);
    }

    [Fact]
    public void Two_new_secrets_differ() =>
        Assert.NotEqual(OpaqueSecret.New("at1").Text, OpaqueSecret.New("at1").Text);

    [Fact]
    public void The_hash_is_sha256_of_the_raw_32_bytes_not_of_the_text()
    {
        var secret = OpaqueSecret.New("dk1");
        var raw = System.Buffers.Text.Base64Url.DecodeFromChars(secret.Text.AsSpan(4));
        Assert.Equal(SHA256.HashData(raw), secret.Hash());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("at1_")]
    [InlineData("dk1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]   // the other prefix
    [InlineData("at1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]    // 42 characters
    [InlineData("at1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]  // 44 characters
    [InlineData("at1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]   // not base64url
    [InlineData("at1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]   // non-canonical trailing bits
    [InlineData(" at1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("at1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n")]
    [InlineData("AT1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_text_is_refused(string? text) =>
        Assert.False(OpaqueSecret.TryParse(text, OpaqueSecret.AccessTokenPrefix, out _));

    [Fact]
    public void ToString_never_reveals_the_secret()
    {
        var secret = OpaqueSecret.New("at1");
        Assert.Equal("at1_<redacted>", secret.ToString());
        Assert.DoesNotContain(secret.Text[4..], $"{secret}");
    }

    [Fact]
    public void The_log_prefix_is_8_lowercase_hex_characters_of_the_hash() =>
        Assert.Equal("00010203", OpaqueSecret.HashPrefix(new byte[] { 0, 1, 2, 3, 4, 5 }));
}
