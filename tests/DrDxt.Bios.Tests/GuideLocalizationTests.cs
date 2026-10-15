using System.Globalization;
using DrDxt.Bios;
using DrDxt.Core;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.Bios.Tests;

/// <summary>
/// Chaque clé resx de guide que le générateur peut émettre doit se résoudre en FR et en EN (zéro
/// texte d'interface en dur). ResxLocalizer rend la clé elle-même quand elle manque,
/// donc une vraie valeur doit différer de la clé.
/// </summary>
public class GuideLocalizationTests
{
    private static readonly ILocalizer Localizer = new ResxLocalizer();

    public static IEnumerable<object[]> AllGuideKeys()
    {
        var keys = new[]
        {
            // Clés d'instruction des étapes
            BiosGuideStringKeys.StepXmp, BiosGuideStringKeys.StepAbove4G,
            BiosGuideStringKeys.StepCsm, BiosGuideStringKeys.StepRebar,
            BiosGuideStringKeys.StepX3dCppc, BiosGuideStringKeys.StepPcie,
            BiosGuideStringKeys.StepBiosVersion, BiosGuideStringKeys.StepGeneric,
            // Étiquettes de confiance
            BiosGuideStringKeys.Verified, BiosGuideStringKeys.UnverifiedFallback,
            // Avertissement de retrait du CSM
            BiosGuideStringKeys.CsmSuppressedLegacy,
            // Échelle de récupération
            BiosGuideStringKeys.RecoveryNoBootNormal, BiosGuideStringKeys.RecoveryMemoryTraining,
            BiosGuideStringKeys.RecoveryClearCmos, BiosGuideStringKeys.RecoveryLoadDefaults,
            BiosGuideStringKeys.RecoveryOneChangeAtATime,
            // Boucle fermée : verdicts après redémarrage
            BiosLoopStringKeys.VerdictTook, BiosLoopStringKeys.VerdictDidNotTake,
            BiosLoopStringKeys.VerdictUnreadable, BiosLoopStringKeys.AllTook,
            // Boucle fermée : carte de récupération
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
