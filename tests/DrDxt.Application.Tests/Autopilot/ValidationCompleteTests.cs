using DrDxt.Application.Autopilot;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Autopilot;

// 07/10/2026 (revue n° 8) : la validation complète pose, relit, rend, relit ; un levier qui ne revient pas est dit.
public sealed class ValidationCompleteTests : IDisposable
{
    private readonly string _chemin = Path.Combine(Path.GetTempPath(), $"drdxt-validation-{Guid.NewGuid():N}.json");
    private readonly Dictionary<string, int> _machine = new();

    public void Dispose() { try { File.Delete(_chemin); } catch (IOException) { } }

    private sealed class Refuse(string id, string code) : ITweak
    {
        public string Id => id;
        public TweakCategory Category => default;
        public bool RequiresElevation => true;
        public bool RequiresRestorePoint => false;
        public string SummaryKey => id;
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => true;
        public Task<Result<TweakSnapshot>> ApplyAsync() => Task.FromResult(Result<TweakSnapshot>.Fail(code, "refusé"));
        public Task<Result> RevertAsync(TweakSnapshot snapshot) => Task.FromResult(Result.Ok());
    }

    private sealed class Levier(string id, Dictionary<string, int> machine, bool concerne = true, bool rendRefuse = false) : ITweak
    {
        public int Rendus;
        public string Id => id;
        public TweakCategory Category => default;
        public bool RequiresElevation => true;
        public bool RequiresRestorePoint => false;
        public string SummaryKey => id;
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => concerne;
        public Task<Result<TweakSnapshot>> ApplyAsync()
        {
            var origine = machine.GetValueOrDefault(id);
            machine[id] = 1;
            return Task.FromResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(id, origine.ToString())));
        }
        public Task<Result> RevertAsync(TweakSnapshot snapshot)
        {
            Rendus++;
            if (rendRefuse) return Task.FromResult(Result.Fail("REFUS", "clé protégée"));
            machine[id] = int.Parse(snapshot.OriginalValueJson);
            return Task.FromResult(Result.Ok());
        }
    }

    private ValidationComplete.Lecture Relire(TweakSnapshot s)
        => new(_machine.GetValueOrDefault(s.TweakId) == 1, $"lu {_machine.GetValueOrDefault(s.TweakId)}");

    [Fact]
    public async Task Chaque_levier_est_pose_relu_rendu_et_la_machine_finit_comme_elle_a_commence()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        var leviers = new ITweak[] { new Levier("a", _machine), new Levier("b", _machine), new Levier("c", _machine, concerne: false) };

        var r = await ValidationComplete.ValiderAsync(leviers, null!, null!, journal, Relire);

        r.NonRemis.Should().BeEmpty();
        r.Texte.Should().Contain("=== a").And.Contain("validé (écrit, relu, rendu, relu)");
        r.Texte.Should().Contain("=== c").And.Contain("pas touché");
        _machine.Values.Should().OnlyContain(v => v == 0);
        journal.Lire().Value.Should().BeNull("rien ne reste en essai");
    }

    [Fact]
    public async Task Un_levier_qui_ne_revient_pas_est_dit_et_reste_au_journal()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        var refuse = new Levier("a", _machine, rendRefuse: true);

        var r = await ValidationComplete.ValiderAsync([refuse], null!, null!, journal, Relire);

        refuse.Rendus.Should().Be(2, "une seconde chance avant de conclure");
        r.NonRemis.Should().ContainSingle().Which.Should().StartWith("a");
        r.Texte.Should().Contain("PAS REMIS D'ORIGINE");
        journal.Lire().Value!.TweakId.Should().Be("a", "le lancement suivant doit pouvoir le rendre");
    }

    [Fact]
    public async Task Un_essai_interrompu_est_rendu_au_lancement_suivant()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        var levier = new Levier("a", _machine);
        _machine["a"] = 0;
        var pose = await levier.ApplyAsync();
        journal.Poser("a", pose.Value);   // la fenêtre meurt ici

        var texte = await ValidationComplete.RemettreAsync(new FichierRevertEnAttente(_chemin), id => id == "a" ? levier : null);

        texte.Propre.Should().BeTrue();
        texte.Texte.Should().Contain("remis d'origine");
        _machine["a"].Should().Be(0);
        journal.Lire().Value.Should().BeNull();
    }

    // Revue n° 9 : le levier suivant écraserait l'essai au journal ; la validation s'arrête au premier non rendu.
    [Fact]
    public async Task Le_premier_levier_non_rendu_arrete_la_validation_et_reste_seul_au_journal()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        var suivant = new Levier("b", _machine);

        var r = await ValidationComplete.ValiderAsync([new Levier("a", _machine, rendRefuse: true), suivant], null!, null!, journal, Relire);

        r.Texte.Should().Contain("ARRÊT").And.NotContain("=== b");
        _machine.Should().NotContainKey("b");
        journal.Lire().Value!.TweakId.Should().Be("a");
    }

    // Revue n° 9 : arrêté pendant l'écriture (origine pas encore gardée) = à vérifier, jamais « tout est remis ».
    [Fact]
    public async Task Un_essai_coupe_pendant_l_ecriture_n_est_jamais_dit_propre()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        journal.Poser("a", null);

        var remise = await ValidationComplete.RemettreAsync(journal, _ => null);

        remise.Propre.Should().BeFalse();
        remise.Texte.Should().Contain("À vérifier à la main");
    }

    [Fact]
    public async Task Une_relecture_qui_leve_n_arrete_pas_la_validation()
    {
        var journal = new FichierRevertEnAttente(_chemin);

        var r = await ValidationComplete.ValiderAsync([new Levier("a", _machine), new Levier("b", _machine)], null!, null!, journal,
            s => s.TweakId == "a" ? throw new InvalidOperationException("ADLX absent") : Relire(s));

        r.Texte.Should().Contain("relecture impossible : ADLX absent").And.Contain("=== b");
        r.NonRemis.Should().ContainSingle().Which.Should().Contain("n'a pas pu le confirmer");
        _machine.Values.Should().OnlyContain(v => v == 0);
    }

    // Revue n° 10 : le bilan compte, et un refus de l'accord de Windows arrête tout au lieu de se reproposer.
    [Fact]
    public async Task Le_bilan_compte_et_un_refus_de_Windows_arrete_la_boucle()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        var r = await ValidationComplete.ValiderAsync(
            [new Levier("a", _machine), new Levier("b", _machine, concerne: false), new Refuse("c", "TWEAK.SDK_UNAVAILABLE"),
             new Refuse("d", ErrorCodes.ElevationDeclined), new Levier("e", _machine)], null!, null!, journal, Relire);

        (r.Valides, r.NonTouches, r.Refuses).Should().Be((1, 1, 1));
        r.ValidesIds.Should().Equal("a");
        r.EssayesIds.Should().Equal(["a", "c"], "b non concerné ; d : l'accord Windows refusé ne dit rien du levier");
        r.ArretPar.Should().Be(ValidationComplete.Arret.RefusDeWindows);
        r.Texte.Should().NotContain("=== e");
        journal.Lire().Value.Should().BeNull();
    }

    // Revue n° 10 : posé en partie, retour arrière raté : à vérifier, journal gardé, boucle arrêtée.
    [Fact]
    public async Task Un_levier_pose_en_partie_reste_au_journal_et_arrete_la_boucle()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        var r = await ValidationComplete.ValiderAsync([new Refuse("a", ErrorCodes.TweakPartiallyApplied), new Levier("b", _machine)],
            null!, null!, journal, Relire);

        r.NonRemis.Should().ContainSingle().Which.Should().Contain("posé en partie");
        r.ArretPar.Should().Be(ValidationComplete.Arret.LevierNonRendu);
        journal.Lire().Value!.TweakId.Should().Be("a");
        _machine.Should().NotContainKey("b");
    }

    [Fact]
    public async Task Un_jeu_ouvert_en_cours_arrete_avant_le_levier_suivant()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        var appels = 0;
        var r = await ValidationComplete.ValiderAsync([new Levier("a", _machine), new Levier("b", _machine)], null!, null!,
            journal, Relire, jeuOuvert: () => appels++ > 0);

        r.ArretPar.Should().Be(ValidationComplete.Arret.JeuOuvert);
        r.Valides.Should().Be(1);
        _machine.Should().NotContainKey("b");
    }

    // Revue n° 10 : un autre levier ne s'écrit pas par-dessus un essai pas encore rendu.
    [Fact]
    public void Le_journal_refuse_un_autre_essai_par_dessus_un_essai_pas_rendu()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        journal.Poser("a", new TweakSnapshot("a", "0")).IsOk.Should().BeTrue();

        journal.Poser("b", null).IsOk.Should().BeFalse();
        journal.Poser("a", new TweakSnapshot("a", "1")).IsOk.Should().BeTrue("le même levier complète son essai");
        journal.Lire().Value!.TweakId.Should().Be("a");

        // Revue n° 11 : le MÊME levier, retenté, ne recapture pas par-dessus son origine gardée.
        journal.Poser("a", null).IsOk.Should().BeFalse();
        journal.Lire().Value!.Snapshot!.OriginalValueJson.Should().Be("1");

        journal.Lever();
        journal.Poser("b", null).IsOk.Should().BeTrue();
    }
}
