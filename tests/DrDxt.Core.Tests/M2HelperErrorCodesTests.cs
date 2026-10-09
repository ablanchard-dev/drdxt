using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// M2 Phase A — the elevated-helper error codes (HELPER.* namespace).
public class M2HelperErrorCodesTests
{
    [Fact] public void NotAllowlisted_is_stable()  => ErrorCodes.NotAllowlisted.Should().Be("HELPER.NOT_ALLOWLISTED");
    [Fact] public void HelperSignatureInvalid_is_stable() => ErrorCodes.HelperSignatureInvalid.Should().Be("HELPER.SIGNATURE_INVALID");
    [Fact] public void HelperOpFailed_is_stable()  => ErrorCodes.HelperOpFailed.Should().Be("HELPER.OP_FAILED");

    [Fact]
    public void Helper_codes_are_distinct_from_each_other()
    {
        ErrorCodes.NotAllowlisted.Should().NotBe(ErrorCodes.HelperSignatureInvalid);
        ErrorCodes.NotAllowlisted.Should().NotBe(ErrorCodes.HelperOpFailed);
        ErrorCodes.HelperSignatureInvalid.Should().NotBe(ErrorCodes.HelperOpFailed);
    }
}
