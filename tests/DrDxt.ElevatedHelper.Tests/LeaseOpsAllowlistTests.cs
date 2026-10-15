using DrDxt.Core;
using DrDxt.ElevatedHelper;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// Les ops de bail sont en liste blanche feuille exacte (validation structurelle seulement ;
// l'op de retour arrière qu'elles portent est validée à part par le chien de garde). Le refus
// par défaut s'applique toujours aux ops de bail malformées.
// -----------------------------------------------------------------------
public sealed class LeaseOpsAllowlistTests
{
    private static readonly OpAllowlist Allow = new();

    private static string RevertJson() =>
        PrivilegedOpCodec.Serialize(new ApplyNvApiClockOffsetOp(0, 0, 0)).Value!;

    [Fact]
    public void Well_formed_EstablishLease_is_allowed()
    {
        Allow.Validate(new EstablishLeaseOp("lease-1", RevertJson(), 30)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Well_formed_RenewLease_is_allowed()
    {
        Allow.Validate(new RenewLeaseOp("lease-1", 30)).IsOk.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EstablishLease_without_an_id_is_denied(string leaseId)
    {
        var r = Allow.Validate(new EstablishLeaseOp(leaseId, RevertJson(), 30));
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EstablishLease_with_non_positive_duration_is_denied(int seconds)
    {
        Allow.Validate(new EstablishLeaseOp("lease-1", RevertJson(), seconds)).IsOk.Should().BeFalse();
    }

    [Fact]
    public void EstablishLease_with_empty_revert_payload_is_denied()
    {
        var r = Allow.Validate(new EstablishLeaseOp("lease-1", "", 30));
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RenewLease_with_non_positive_duration_is_denied(int seconds)
    {
        Allow.Validate(new RenewLeaseOp("lease-1", seconds)).IsOk.Should().BeFalse();
    }

    [Fact]
    public void RenewLease_without_an_id_is_denied()
    {
        Allow.Validate(new RenewLeaseOp("", 30)).IsOk.Should().BeFalse();
    }
}
