using DrDxt.Application.Autopilot;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Autopilot;

// Le rendu au démarrage ne rejoue l'origine que sur la valeur de DrDXT : changé depuis, rien n'est réécrit par-dessus. Rendu,
// « remis » ne se dit que relu à l'origine.
public sealed class RevertAuDemarrageRelitLaValeurTests : IDisposable
{
    private const string Id = "latency.hagsOn";
    private const string Chemin = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private readonly string _fichier = Path.Combine(Path.GetTempPath(), $"drdxt-rv-relu-{Guid.NewGuid():N}.json");
    public void Dispose() { try { File.Delete(_fichier); } catch (IOException) { } }

    private uint _valeur;
    private bool _rendu;

    private (FichierRevertEnAttente Journal, ITweakCatalog Catalogue, IRegistryReader Registre) Monter(uint courante, uint apresRendu)
    {
        _valeur = courante;
        var journal = new FichierRevertEnAttente(_fichier);
        journal.Poser(Id, new TweakSnapshot(Id, DrDxt.Tests.Shared.InversesDeTest.Pour(Id, "1"), DrDxt.Tests.Shared.InversesDeTest.Pour(Id, "2")))
            .IsOk.Should().BeTrue();
        var levier = Substitute.For<ITweak>();
        levier.Id.Returns(Id);
        levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(_ => { _rendu = true; _valeur = apresRendu; return Result.Ok(); });
        var registre = Substitute.For<IRegistryReader>();
        registre.ReadDword(RegistryHiveKind.LocalMachine, Chemin, "HwSchMode").Returns(_ => Result<uint?>.Ok(_valeur));
        return (journal, new DrDxt.Application.Tweaks.TweakCatalog(new[] { levier }), registre);
    }

    [Fact]
    public void Un_reglage_change_depuis_n_est_pas_reecrit()
    {
        var (journal, catalogue, registre) = Monter(courante: 3, apresRendu: 1);

        var phrase = RevertAuDemarrage.Executer(journal, catalogue, out _, registre: registre);

        _rendu.Should().BeFalse("ni l'origine ni la valeur de DrDXT : le choix de quelqu'un d'autre");
        phrase.Should().Be(Docteur.T("RV.ChangeDepuis", Id));
        journal.Lire().Value.Should().BeNull("plus rien de DrDXT n'y est à rendre");
    }

    [Fact]
    public void Remis_ne_se_dit_que_relu_a_l_origine()
    {
        var (journal, catalogue, registre) = Monter(courante: 2, apresRendu: 2);
        RevertAuDemarrage.Executer(journal, catalogue, out _, registre: registre).Should().Be(Docteur.T("RV.EncorePose", Id));
        _rendu.Should().BeTrue();

        (journal, catalogue, registre) = Monter(courante: 2, apresRendu: 1);
        RevertAuDemarrage.Executer(journal, catalogue, out _, registre: registre).Should().Be(Docteur.T("RV.Remis", Id), "témoin : relu à l'origine");
    }

    // Le levier dit « rendu » mais la relecture montre encore la valeur de DrDXT (un pilote qui borne) : le journal reste, le
    // lancement suivant réessaie. Levé, plus rien ne l'aurait rendu.
    [Fact]
    public void Encore_pose_apres_le_rendu_le_journal_reste_pour_le_lancement_suivant()
    {
        var (journal, catalogue, registre) = Monter(courante: 2, apresRendu: 2);

        RevertAuDemarrage.Executer(journal, catalogue, out _, registre: registre).Should().Be(Docteur.T("RV.EncorePose", Id));

        journal.Lire().Value!.TweakId.Should().Be(Id);
    }

    [Fact]
    public void Encore_pose_selon_le_verificateur_le_journal_reste()
    {
        var (journal, catalogue) = MonterCarte(_fichier, () => { });

        RevertAuDemarrage.Executer(journal, catalogue, out _, verdict: _ => DrDxt.Application.Tweaks.EtatApplique.Applied)
            .Should().Be(Docteur.T("RV.EncorePose", "gpu.power_limit"));

        journal.Lire().Value!.TweakId.Should().Be("gpu.power_limit");
    }

    // Même règle pour la remise de la validation complète.
    [Fact]
    public async Task La_remise_de_la_validation_garde_le_journal_si_la_valeur_de_drdxt_reste()
    {
        var (journal, catalogue, registre) = Monter(courante: 2, apresRendu: 2);

        var remise = await ValidationComplete.RemettreAsync(journal, id => catalogue.Get(id).Value, (_, _) => true, registre,
            s => DrDxt.Application.Tweaks.EtatApplique.Applied);

        remise.Texte.Should().Be(Docteur.T("VC.Remise.EncorePose", Id));
        journal.Lire().Value!.TweakId.Should().Be(Id);
    }

    private static (FichierRevertEnAttente Journal, ITweakCatalog Catalogue) MonterCarte(string fichier, Action rendu)
    {
        var journal = new FichierRevertEnAttente(fichier);
        journal.Poser("gpu.power_limit", new TweakSnapshot("gpu.power_limit", DrDxt.Tests.Shared.InversesDeTest.Pour("gpu.power_limit", "100"),
            DrDxt.Tests.Shared.InversesDeTest.Pour("gpu.power_limit", "90"))).IsOk.Should().BeTrue();
        var levier = Substitute.For<ITweak>();
        levier.Id.Returns("gpu.power_limit");
        levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(_ => { rendu(); return Result.Ok(); });
        return (journal, new DrDxt.Application.Tweaks.TweakCatalog(new[] { levier }));
    }

    // La limite de la carte ne se relit pas au registre : le démarrage se fie au vérificateur de la pose, avant d'écrire
    // (changée depuis, rien n'est réécrit) et après (« remis » seulement relu à l'origine).
    [Fact]
    public void Une_feuille_non_relisable_se_juge_par_le_verificateur_au_demarrage()
    {
        var rendus = 0;
        var (journal, catalogue) = MonterCarte(_fichier, () => rendus++);
        RevertAuDemarrage.Executer(journal, catalogue, out _, verdict: _ => DrDxt.Application.Tweaks.EtatApplique.Drifted)
            .Should().Be(Docteur.T("RV.ChangeDepuis", "gpu.power_limit"));
        rendus.Should().Be(0);

        (journal, catalogue) = MonterCarte(_fichier, () => rendus++);
        RevertAuDemarrage.Executer(journal, catalogue, out _, verdict: _ => DrDxt.Application.Tweaks.EtatApplique.Applied)
            .Should().Be(Docteur.T("RV.EncorePose", "gpu.power_limit"), "rendu par le levier, mais la carte porte encore la valeur de DrDXT");
        rendus.Should().Be(1);
    }
}
