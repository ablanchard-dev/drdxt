using System.Security.Cryptography;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// Focused unit tests for the security primitives.
// -----------------------------------------------------------------------
public sealed class RequestHmacTests
{
    [Fact]
    public void Verify_MatchingKeyAndFields_IsTrue()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = Guid.NewGuid().ToString("N");
        var mac = RequestHmac.Compute(key, OpKind.Ping, nonce);

        RequestHmac.Verify(key, OpKind.Ping, nonce, mac).Should().BeTrue();
    }

    [Fact]
    public void Verify_WrongKey_IsFalse()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var other = RandomNumberGenerator.GetBytes(32);
        var nonce = Guid.NewGuid().ToString("N");
        var mac = RequestHmac.Compute(key, OpKind.Ping, nonce);

        RequestHmac.Verify(other, OpKind.Ping, nonce, mac).Should().BeFalse();
    }

    [Fact]
    public void Verify_DifferentNonce_IsFalse()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var mac = RequestHmac.Compute(key, OpKind.Ping, "nonce-a");

        RequestHmac.Verify(key, OpKind.Ping, "nonce-b", mac).Should().BeFalse();
    }

    [Fact]
    public void Verify_MalformedBase64_IsFalse_NoThrow()
    {
        var key = RandomNumberGenerator.GetBytes(32);

        var act = () => RequestHmac.Verify(key, OpKind.Ping, "n", "not-base64-@@@");

        act.Should().NotThrow();
        RequestHmac.Verify(key, OpKind.Ping, "n", "not-base64-@@@").Should().BeFalse();
    }

    [Fact]
    public void Verify_NullHmac_IsFalse_NoThrow()
    {
        var key = RandomNumberGenerator.GetBytes(32);

        var act = () => RequestHmac.Verify(key, OpKind.Ping, "n", null!);

        act.Should().NotThrow("a null HMAC must fail-close, not throw ArgumentNullException");
        RequestHmac.Verify(key, OpKind.Ping, "n", null!).Should().BeFalse();
    }

    [Fact]
    public void Verify_EmptyHmac_IsFalse()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        RequestHmac.Verify(key, OpKind.Ping, "n", "").Should().BeFalse();
    }
}

public sealed class NonceTrackerTests
{
    [Fact]
    public void TryRegister_FreshNonce_ReturnsTrue()
    {
        var tracker = new NonceTracker();
        tracker.TryRegister("abc").Should().BeTrue();
    }

    [Fact]
    public void TryRegister_RepeatedNonce_ReturnsFalse()
    {
        var tracker = new NonceTracker();
        tracker.TryRegister("abc").Should().BeTrue();
        tracker.TryRegister("abc").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryRegister_EmptyOrWhitespace_ReturnsFalse(string nonce)
    {
        var tracker = new NonceTracker();
        tracker.TryRegister(nonce).Should().BeFalse("a missing nonce must never pass anti-replay");
    }
}

// -----------------------------------------------------------------------
// Wire framing / deserialization fail-closed behavior.
// -----------------------------------------------------------------------
public sealed class WireCodecTests
{
    private static byte[] Frame(string json)
    {
        var body = System.Text.Encoding.UTF8.GetBytes(json);
        var prefix = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(prefix, body.Length);
        return prefix.Concat(body).ToArray();
    }

    [Fact]
    public async Task ReadMessage_WellFormed_RoundTrips()
    {
        var framed = Frame("{\"Kind\":0,\"Nonce\":\"n\",\"HmacBase64\":\"x\"}");
        using var ms = new MemoryStream(framed);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var msg = await WireCodec.ReadMessageAsync<PrivilegedRequest>(ms, cts.Token);

        msg.Should().NotBeNull();
        msg!.Kind.Should().Be(OpKind.Ping);
        msg.Nonce.Should().Be("n");
    }

    [Fact]
    public async Task ReadMessage_UnknownMember_ReturnsNull_FailClosed()
    {
        var framed = Frame("{\"Kind\":0,\"Nonce\":\"n\",\"HmacBase64\":\"x\",\"EVIL\":\"y\"}");
        using var ms = new MemoryStream(framed);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var msg = await WireCodec.ReadMessageAsync<PrivilegedRequest>(ms, cts.Token);

        msg.Should().BeNull("an unexpected JSON member must be rejected fail-closed");
    }

    // 26/09/2026 : le plafond de trame n'etait tenu par aucun test. Sans lui, un client du compte
    // annonce 2 Gio et le helper eleve tente de les allouer avant d'avoir lu un seul octet.
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(WireCodec.MaxMessageBytes + 1)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ReadMessage_LongueurHorsBorne_RendNull_SansAllouer(int longueur)
    {
        var prefix = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(prefix, longueur);
        using var ms = new MemoryStream(prefix);

        var msg = await WireCodec.ReadMessageAsync<PrivilegedRequest>(ms, CancellationToken.None);

        msg.Should().BeNull();
    }

    [Fact]
    public async Task ReadMessage_ExactementLePlafond_EstLu()
    {
        var json = "{\"Kind\":0,\"Nonce\":\"n\",\"HmacBase64\":\"x\"}";
        using var ms = new MemoryStream(Frame(json.PadRight(WireCodec.MaxMessageBytes)));

        var msg = await WireCodec.ReadMessageAsync<PrivilegedRequest>(ms, CancellationToken.None);

        msg.Should().NotBeNull();
    }
}
