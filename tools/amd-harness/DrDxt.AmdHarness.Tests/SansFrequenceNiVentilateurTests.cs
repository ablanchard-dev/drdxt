using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.AmdHarness.Tests;

/// <summary>
/// 09/10/2026 (diffusion publique du zip) : la règle du 29/09 (jamais tension, fréquence ni ventilateur) tenue par le code
/// du banc, pas seulement par la liste de ses leviers : une écriture interdite n'atteint jamais le canal élevé.
/// </summary>
public sealed class SansFrequenceNiVentilateurTests
{
    private sealed class Compteur : IElevatedClient
    {
        public List<PrivilegedOp> Recus { get; } = [];
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) { Recus.Add(op); return Task.FromResult(Result.Ok()); }
    }

    public static TheoryData<PrivilegedOp> Interdites => new()
    {
        new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, -100),
        new ApplyNvApiClockOffsetOp(0, -100, 0),
        new ApplyGpuFanCurveOp(0, []),
    };

    [Theory]
    [MemberData(nameof(Interdites))]
    public async Task Une_ecriture_de_frequence_ou_de_ventilateur_est_refusee_avant_le_canal_eleve(PrivilegedOp op)
    {
        var interieur = new Compteur();

        var r = await new SansFrequenceNiVentilateur(interieur).ExecuteAsync(op, CancellationToken.None);

        r.IsOk.Should().BeFalse();
        interieur.Recus.Should().BeEmpty("rien d'interdit ne doit atteindre l'exécuteur");
    }

    [Fact]
    public async Task Les_leviers_du_test_passent_limite_de_puissance_et_anti_lag()
    {
        var interieur = new Compteur();
        var filtre = new SansFrequenceNiVentilateur(interieur);

        (await filtre.ExecuteAsync(new ApplyAdlxTuningOp(0, AdlxTuningKind.PowerLimit, 90), CancellationToken.None)).IsOk.Should().BeTrue();
        (await filtre.ExecuteAsync(new ApplyAdlxTuningOp(0, AdlxTuningKind.AntiLag, 1), CancellationToken.None)).IsOk.Should().BeTrue();
        (await filtre.ExecuteAsync(new ApplyNvApiPowerLimitOp(0, 90), CancellationToken.None)).IsOk.Should().BeTrue();
        interieur.Recus.Should().HaveCount(3);
    }
}
