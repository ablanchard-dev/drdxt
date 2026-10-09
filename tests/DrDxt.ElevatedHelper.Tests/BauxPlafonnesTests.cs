using DrDxt.Core;
using DrDxt.ElevatedHelper.Leases;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>Revue de sécurité du 08/10/2026 : un bail est borné (10 minutes par renouvellement, 8 par appelant). Sans
/// plafond, un appelant gardait le helper administrateur vivant pour toujours et annulait l'arrêt pour inactivité.</summary>
public sealed class BauxPlafonnesTests
{
    private static readonly string Inverse = PrivilegedOpCodec.Serialize(new ApplyNvApiClockOffsetOp(0, 0, 0)).Value!;
    private static readonly LeaseOwner Appelant = new(ProcessId: 4242);

    private static LeaseWatchdog Watchdog()
    {
        var horloge = Substitute.For<IClock>();
        horloge.UtcNow.Returns(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        return new LeaseWatchdog(horloge, new OpAllowlist(), Substitute.For<IPrivilegedOpExecutor>(), Substitute.For<IDiagnosticsLog>());
    }

    [Fact]
    public void Un_bail_de_plus_de_dix_minutes_est_refuse()
    {
        var w = Watchdog();
        w.Establish(Appelant, "gpu-expert", Inverse, 600).IsOk.Should().BeTrue();
        w.Establish(Appelant, "long", Inverse, 601).IsOk.Should().BeFalse();
        w.Renew(Appelant, "gpu-expert", 601).IsOk.Should().BeFalse();
        w.Renew(Appelant, "gpu-expert", 120).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Pas_plus_de_huit_baux_par_appelant()
    {
        var w = Watchdog();
        for (var i = 0; i < LeaseWatchdog.BauxMaxParAppelant; i++)
            w.Establish(Appelant, $"bail-{i}", Inverse, 120).IsOk.Should().BeTrue();

        w.Establish(Appelant, "neuvieme", Inverse, 120).IsOk.Should().BeFalse();
        w.Establish(Appelant, "bail-0", Inverse, 120).IsOk.Should().BeTrue("rétablir un bail déjà tenu n'en ajoute pas");
        w.NombreDeBaux.Should().Be(LeaseWatchdog.BauxMaxParAppelant);
    }
}
