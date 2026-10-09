using System.Globalization;
using DrDxt.Bios;
using DrDxt.Core;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.Bios.Tests;

/// <summary>
/// Every guide resx key the generator can emit must resolve in both FR and EN (zero
/// hard-coded UI text — §19 i18n). ResxLocalizer returns the key itself when missing,
/// so a real value must differ from the key.
/// </summary>
public class GuideLocalizationTests
{
    private static readonly ILocalizer Localizer = new ResxLocalizer();

    public static IEnumerable<object[]> AllGuideKeys()
    {
        var keys = new[]
        {
            // Step instruction keys
            BiosGuideStringKeys.StepXmp, BiosGuideStringKeys.StepAbove4G,
            BiosGuideStringKeys.StepCsm, BiosGuideStringKeys.StepRebar,
            BiosGuideStringKeys.StepX3dCppc, BiosGuideStringKeys.StepPcie,
            BiosGuideStringKeys.StepBiosVersion, BiosGuideStringKeys.StepGeneric,
            // Confidence labels
            BiosGuideStringKeys.Verified, BiosGuideStringKeys.UnverifiedFallback,
            // CSM suppression warning
            BiosGuideStringKeys.CsmSuppressedLegacy,
            // Recovery ladder
            BiosGuideStringKeys.RecoveryNoBootNormal, BiosGuideStringKeys.RecoveryMemoryTraining,
            BiosGuideStringKeys.RecoveryClearCmos, BiosGuideStringKeys.RecoveryLoadDefaults,
            BiosGuideStringKeys.RecoveryOneChangeAtATime,
            // M3 closed loop: post-reboot verdicts
            BiosLoopStringKeys.VerdictTook, BiosLoopStringKeys.VerdictDidNotTake,
            BiosLoopStringKeys.VerdictUnreadable, BiosLoopStringKeys.AllTook,
            // M3 closed loop: recovery card
            BiosLoopStringKeys.RecoveryHeader, BiosLoopStringKeys.RecoveryReenter,
            BiosLoopStringKeys.RecoveryOneChangeAtATime, BiosLoopStringKeys.RecoveryLoadDefaults,
            BiosLoopStringKeys.RecoveryClearCmos,
        };
        return keys.Select(k => new object[] { k });
    }

    [Theory]
    [MemberData(nameof(AllGuideKeys))]
    public void Guide_key_exists_in_fr_and_en(string key)
    {
        var en = Localizer.Get(key, new CultureInfo("en"));
        var fr = Localizer.Get(key, new CultureInfo("fr"));
        en.Should().NotBeNullOrEmpty().And.NotBe(key, $"key '{key}' missing from EN resx");
        fr.Should().NotBeNullOrEmpty().And.NotBe(key, $"key '{key}' missing from FR resx");
    }
}
