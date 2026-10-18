using DrDxt.Application.Autopilot;
using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.AmdHarness.Tests;

/// <summary>
/// Chaque geste du banc n'écrit que ce qu'il annonce. « --valider » n'écrit jamais de fréquence, de tension ni de
/// ventilateur (sa promesse au joueur) ; « --valider-frequence » n'écrit que la fréquence maximale AMD ; la remise ne rend
/// que ce que les deux ont posé. Ni l'un ni l'autre ne monte la fréquence de plus de 100 MHz au-dessus de l'origine du
/// démarrage (le helper la refuserait aussi), et aucun ne remet la carte à l'usine, un geste du joueur seulement.
/// </summary>
public sealed class GestesDuBancTests
{
    private sealed class Compteur : IElevatedClient
    {
        public List<PrivilegedOp> Recus { get; } = [];
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) { Recus.Add(op); return Task.FromResult(Result.Ok()); }
    }

    private const int Lue = 2900;
    private static int? MaxFreq(int _) => Lue;

    private static async Task<bool> Passe(ValidationMachine.Geste geste, PrivilegedOp op)
    {
        var interieur = new Compteur();
        var r = await ValidationMachine.ClientDuGeste(geste, interieur, MaxFreq, (_, lue) => lue).ExecuteAsync(op, CancellationToken.None);
        r.IsOk.Should().Be(interieur.Recus.Count == 1, "refusée, une écriture n'atteint jamais l'exécuteur");
        return r.IsOk;
    }

    [Fact]
    public void Valider_ne_porte_aucun_levier_de_frequence()
        => ValidationComplete.Leviers.Should().NotContain("gpu.undervolt").And.NotContain("gpu.fan_curve");

    [Fact]
    public async Task Valider_refuse_toute_frequence_meme_une_baisse()
    {
        (await Passe(ValidationMachine.Geste.Valider, new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, Lue - 50))).Should().BeFalse();
        (await Passe(ValidationMachine.Geste.Valider, new ApplyAdlxTuningOp(0, AdlxTuningKind.PowerLimit, 90))).Should().BeTrue("témoin");
    }

    [Fact]
    public async Task La_frequence_n_ecrit_que_la_frequence_maximale_AMD()
    {
        (await Passe(ValidationMachine.Geste.Frequence, new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, Lue - 50))).Should().BeTrue();
        (await Passe(ValidationMachine.Geste.Frequence, new ApplyAdlxTuningOp(0, AdlxTuningKind.PowerLimit, 90))).Should().BeFalse();
        (await Passe(ValidationMachine.Geste.Frequence, new ApplyAdlxTuningOp(0, AdlxTuningKind.AntiLag, 1))).Should().BeFalse();
        (await Passe(ValidationMachine.Geste.Frequence, new ApplyNvApiClockOffsetOp(0, -50, 0))).Should().BeFalse();
        (await Passe(ValidationMachine.Geste.Frequence, new ApplyGpuFanCurveOp(0, []))).Should().BeFalse();
    }

    [Theory]
    [InlineData(nameof(ValidationMachine.Geste.Frequence))]
    [InlineData(nameof(ValidationMachine.Geste.Remettre))]
    public async Task Jamais_plus_de_100_MHz_au_dessus_de_la_frequence_lue(string nom)
    {
        var geste = Enum.Parse<ValidationMachine.Geste>(nom);
        (await Passe(geste, new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, Lue + 100))).Should().BeTrue("à la limite de la bande");
        (await Passe(geste, new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, Lue + 101))).Should().BeFalse();
    }

    [Fact]
    public async Task Une_frequence_illisible_n_est_jamais_ecrite()
    {
        var interieur = new Compteur();
        var r = await ValidationMachine.ClientDuGeste(ValidationMachine.Geste.Frequence, interieur, _ => null, (_, lue) => lue)
            .ExecuteAsync(new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, Lue - 50), CancellationToken.None);

        r.IsOk.Should().BeFalse();
        interieur.Recus.Should().BeEmpty();
    }

    // La remise à l'usine de la carte est un geste du joueur seulement (AMD Software y ramène aussi mémoire, ventilateur et
    // puissance) : aucun geste du banc ne l'émet ni ne la laisse passer.
    [Theory]
    [InlineData(nameof(ValidationMachine.Geste.Valider))]
    [InlineData(nameof(ValidationMachine.Geste.Frequence))]
    [InlineData(nameof(ValidationMachine.Geste.Remettre))]
    public async Task Aucun_geste_du_banc_ne_remet_la_carte_a_l_usine(string nom)
        => (await Passe(Enum.Parse<ValidationMachine.Geste>(nom), new ApplyAdlxTuningOp(0, AdlxTuningKind.RemiseUsine, 0))).Should().BeFalse();

    [Fact]
    public async Task La_remise_rend_ce_que_les_deux_gestes_posent_et_rien_d_autre()
    {
        (await Passe(ValidationMachine.Geste.Remettre, new ApplyAdlxTuningOp(0, AdlxTuningKind.PowerLimit, 90))).Should().BeTrue();
        (await Passe(ValidationMachine.Geste.Remettre, new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, Lue + 50))).Should().BeTrue();
        (await Passe(ValidationMachine.Geste.Remettre, new ApplyNvApiClockOffsetOp(0, -50, 0))).Should().BeFalse();
        (await Passe(ValidationMachine.Geste.Remettre, new ApplyGpuFanCurveOp(0, []))).Should().BeFalse();
    }
}

/// <summary>
/// Un journal forgé (ou un vieux journal de l'App) qui porte « fréquence lue + 100 MHz », rejoué à chaque REMETTRE.cmd,
/// monterait la carte par paliers jusqu'au plafond du pilote si la borne suivait la fréquence lue. Elle suit l'origine
/// retenue au premier passage depuis le démarrage, comme le helper : la carte ne dépasse jamais origine + 100 MHz.
/// </summary>
public sealed class LaFrequenceNeMontePasParPaliersTests
{
    private sealed class Carte : IElevatedClient
    {
        public int MaxFreq = 2900;
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            if (op is ApplyAdlxTuningOp { Kind: AdlxTuningKind.Undervolt } a) MaxFreq = a.IntValue;
            return Task.FromResult(Result.Ok());
        }
    }

    [Theory]
    [InlineData(nameof(ValidationMachine.Geste.Remettre))]
    [InlineData(nameof(ValidationMachine.Geste.Frequence))]
    public async Task Un_journal_a_lue_plus_100_rejoue_ne_monte_jamais_au_dela_de_origine_plus_100(string nom)
    {
        var carte = new Carte();
        int? retenue = null;
        int? Retenir(int _, int lue) => retenue ??= lue;   // la clé volatile : le premier passage depuis le démarrage
        var client = ValidationMachine.ClientDuGeste(Enum.Parse<ValidationMachine.Geste>(nom), carte, _ => carte.MaxFreq, Retenir);

        for (var i = 0; i < 5; i++)
            await client.ExecuteAsync(new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, carte.MaxFreq + 100), CancellationToken.None);

        carte.MaxFreq.Should().Be(3000, "une seule montée, jusqu'à l'origine du démarrage + 100, puis plus rien");
    }
}
