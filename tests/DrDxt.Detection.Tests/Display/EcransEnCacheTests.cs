using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Display;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Detection.Tests.Display;

// Lire les écrans coûte 40 à 57 ms (mesuré le 25/10/2026 sur la machine de référence : les modes de chaque écran, la lecture
// VRR au pilote, les deux écrans pour n'en garder qu'un), et un scan les relit plusieurs fois en quelques millisecondes
// (chaque levier qui en dépend, AppliesTo puis EtatNonLu). La lecture se garde un court instant ; un échec ne se garde pas.
public sealed class EcransEnCacheTests
{
    private static readonly DisplayInfo Principal = new(IsPrimaryGameDisplay: true, SupportsVrr: true, RefreshHz: 144);
    private static readonly DisplayInfo Second = new(IsPrimaryGameDisplay: false, SupportsVrr: false, RefreshHz: 60);

    private static (EcransEnCache Ecrans, IDisplayInfo Source, Action<TimeSpan> Avancer) Monter()
    {
        var source = Substitute.For<IDisplayInfo>();
        source.GetAllDisplays().Returns(Result<IReadOnlyList<DisplayInfo>>.Ok([Principal, Second]));
        var maintenant = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        return (new EcransEnCache(source, () => maintenant), source, d => maintenant += d);
    }

    [Fact]
    public void Plusieurs_lectures_rapprochees_lisent_les_ecrans_une_fois()
    {
        var (ecrans, source, _) = Monter();

        ecrans.GetPrimaryGameDisplay().Value.Should().Be(Principal);
        ecrans.GetAllDisplays().Value.Should().Equal(Principal, Second);
        ecrans.GetPrimaryGameDisplay();

        source.Received(1).GetAllDisplays();
        source.DidNotReceive().GetPrimaryGameDisplay();
    }

    [Fact]
    public void Passe_le_court_instant_les_ecrans_se_relisent()
    {
        var (ecrans, source, avancer) = Monter();
        ecrans.GetPrimaryGameDisplay();

        avancer(EcransEnCache.Duree + TimeSpan.FromMilliseconds(1));
        ecrans.GetPrimaryGameDisplay();

        source.Received(2).GetAllDisplays();
    }

    [Fact]
    public void Un_echec_ne_se_garde_pas()
    {
        var (ecrans, source, _) = Monter();
        source.GetAllDisplays().Returns(Result<IReadOnlyList<DisplayInfo>>.Fail(ErrorCodes.TweakSdkUnavailable, "pas de bureau"));

        ecrans.GetPrimaryGameDisplay().IsOk.Should().BeFalse();
        ecrans.GetPrimaryGameDisplay();

        source.Received(2).GetAllDisplays();
    }

    [Fact]
    public void Sans_ecran_principal_la_lecture_echoue_sans_deviner()
    {
        var (ecrans, source, _) = Monter();
        source.GetAllDisplays().Returns(Result<IReadOnlyList<DisplayInfo>>.Ok([Second]));

        ecrans.GetPrimaryGameDisplay().IsOk.Should().BeFalse();
    }
}
