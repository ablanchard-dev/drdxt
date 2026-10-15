using System.Security.Cryptography;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// Tests unitaires ciblés des primitives de sécurité.
// -----------------------------------------------------------------------
public sealed class TypedRequestHmacTests
{
    private const string Op = "{\"Kind\":\"ping\"}";

    [Fact]
    public void Verify_MatchingKeyAndFields_IsTrue()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = Guid.NewGuid().ToString("N");
        var mac = TypedRequestHmac.Compute(key, Op, nonce);

        TypedRequestHmac.Verify(key, Op, nonce, mac).Should().BeTrue();
    }

    [Fact]
    public void Verify_WrongKey_IsFalse()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var other = RandomNumberGenerator.GetBytes(32);
        var nonce = Guid.NewGuid().ToString("N");
        var mac = TypedRequestHmac.Compute(key, Op, nonce);

        TypedRequestHmac.Verify(other, Op, nonce, mac).Should().BeFalse();
    }

    [Fact]
    public void Verify_DifferentNonce_IsFalse()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var mac = TypedRequestHmac.Compute(key, Op, "nonce-a");

        TypedRequestHmac.Verify(key, Op, "nonce-b", mac).Should().BeFalse();
    }

    [Fact]
    public void Verify_MalformedBase64_IsFalse_NoThrow()
    {
        var key = RandomNumberGenerator.GetBytes(32);

        var act = () => TypedRequestHmac.Verify(key, Op, "n", "not-base64-@@@");

        act.Should().NotThrow();
        TypedRequestHmac.Verify(key, Op, "n", "not-base64-@@@").Should().BeFalse();
    }

    [Fact]
    public void Verify_NullHmac_IsFalse_NoThrow()
    {
        var key = RandomNumberGenerator.GetBytes(32);

        var act = () => TypedRequestHmac.Verify(key, Op, "n", null!);

        act.Should().NotThrow("a null HMAC must fail-close, not throw ArgumentNullException");
        TypedRequestHmac.Verify(key, Op, "n", null!).Should().BeFalse();
    }

    [Fact]
    public void Verify_EmptyHmac_IsFalse()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        TypedRequestHmac.Verify(key, Op, "n", "").Should().BeFalse();
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
// Cadrage du fil et désérialisation : fermés en cas d'échec.
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
        var framed = Frame("{\"Ok\":true,\"ErrorCode\":\"e\"}");
        using var ms = new MemoryStream(framed);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var msg = await WireCodec.ReadMessageAsync<PrivilegedResponse>(ms, cts.Token);

        msg.Should().NotBeNull();
        msg!.Ok.Should().BeTrue();
        msg.ErrorCode.Should().Be("e");
    }

    [Fact]
    public async Task ReadMessage_UnknownMember_ReturnsNull_FailClosed()
    {
        var framed = Frame("{\"Ok\":true,\"ErrorCode\":\"e\",\"EVIL\":\"y\"}");
        using var ms = new MemoryStream(framed);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var msg = await WireCodec.ReadMessageAsync<PrivilegedResponse>(ms, cts.Token);

        msg.Should().BeNull("an unexpected JSON member must be rejected fail-closed");
    }

    // Le plafond de trame n'etait tenu par aucun test. Sans lui, un client du compte
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

        var msg = await WireCodec.ReadMessageAsync<PrivilegedResponse>(ms, CancellationToken.None);

        msg.Should().BeNull();
    }

    [Fact]
    public async Task ReadMessage_ExactementLePlafond_EstLu()
    {
        var json = "{\"Ok\":true,\"ErrorCode\":\"e\"}";
        using var ms = new MemoryStream(Frame(json.PadRight(WireCodec.MaxMessageBytes)));

        var msg = await WireCodec.ReadMessageAsync<PrivilegedResponse>(ms, CancellationToken.None);

        msg.Should().NotBeNull();
    }
}
