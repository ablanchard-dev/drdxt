using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Les codes d'erreur par levier (espace TWEAK.*).
public class M2TweakErrorCodesTests
{
    [Fact] public void TweakNotApplicable_is_stable() => ErrorCodes.TweakNotApplicable.Should().Be("TWEAK.NOT_APPLICABLE");
    [Fact] public void TweakSdkUnavailable_is_stable() => ErrorCodes.TweakSdkUnavailable.Should().Be("TWEAK.SDK_UNAVAILABLE");
    [Fact] public void TweakElevationRequired_is_stable() => ErrorCodes.TweakElevationRequired.Should().Be("TWEAK.ELEVATION_REQUIRED");

    [Fact]
    public void Tweak_codes_are_distinct_from_each_other()
    {
        ErrorCodes.TweakNotApplicable.Should().NotBe(ErrorCodes.TweakSdkUnavailable);
        ErrorCodes.TweakNotApplicable.Should().NotBe(ErrorCodes.TweakElevationRequired);
        ErrorCodes.TweakSdkUnavailable.Should().NotBe(ErrorCodes.TweakElevationRequired);
    }
}
