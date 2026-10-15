using System.IO;
using DrDxt.Infrastructure.Diagnostics;
using FluentAssertions;

namespace DrDxt.Infrastructure.Tests;

// -----------------------------------------------------------------------
// Le journal des changements n'avait aucune borne.
//
// `DiagnosticsLog.Change` fait un `File.AppendAllText` et rien d'autre : pas de plafond,
// pas de rotation. Le fichier vit sous `%LOCALAPPDATA%\DrDXT\diagnostics` et n'est jamais
// relu en entier par le produit... sauf par `ExportAsync`, qui fait `File.ReadAllText` et
// copie tout dans le fichier que le client envoie au support.
//
// Deux consequences, et la seconde est celle qui se voit :
//   * le fichier grandit tant que le produit sert, sur le disque de quelqu'un qui n'a
//     jamais demande a garder ca ;
//   * l'export grandit avec lui. Or `DiagnosticReport`, l'autre bouton d'export, porte
//     cette regle en tete de fichier : « le client le colle dans un Discord ou un mail,
//     et il doit pouvoir le relire lui-meme avant de l'envoyer ». Deux surfaces portent
//     la meme promesse ; une seule etait bornee.
//
// La borne choisie ne fait disparaitre personne en silence : quand le plafond est
// atteint, les lignes les plus anciennes partent et une ligne le DIT, a la place exacte
// ou elles etaient. Un journal tronque se lit ; un journal qui se tait sur ce qu'il a
// perdu ment par omission.
//
// Ce que la borne ne touche pas : ce qui permet de tout defaire. Le retour aux valeurs
// d'origine s'appuie sur le manifeste signe sous %ProgramData% (voir `ModeDesinstallation`),
// jamais sur ce journal, qui est une trace lisible par un humain.
// -----------------------------------------------------------------------
public sealed class LeJournalNeGrossitPasSansFinTests : IDisposable
{
    private readonly string _dossier;

    public LeJournalNeGrossitPasSansFinTests()
    {
        _dossier = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dossier);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dossier)) Directory.Delete(_dossier, recursive: true);
    }

    private string Journal => Path.Combine(_dossier, "changes.journal.log");

    /// <summary>Ecrit assez d'entrees pour depasser le plafond, avec une valeur reconnaissable.</summary>
    private static void Remplir(DiagnosticsLog log, int combien, string marque)
    {
        for (var i = 0; i < combien; i++)
            log.Change($"Levier{i}", "Off", $"{marque}-{i}", "DrDXT");
    }

    /// <summary>Le plafond employe ici : le vrai est a 2 Mo, verifiable sans ecrire 2 Mo.
    /// Le journal est injecte comme le dossier l'est deja ; c'est le meme chemin de code.</summary>
    private const long PlafondDEssai = 32 * 1024;

    private DiagnosticsLog Log() => new(_dossier, logger: null, plafondOctets: PlafondDEssai);

    /// <summary>De quoi ecrire trois fois le plafond d'essai (une entree pese ~60 octets).
    ///
    /// Le premier essai visait 1,4 fois le plafond : sous mutation (borne retiree), le fichier
    /// restait sous le seuil verifie et le test de taille restait VERT. Il nommait la borne
    /// sans la prouver. Le volume est donc dimensionne pour que l'absence de borne se voie.</summary>
    private static int AssezPourDepasser() => (int)(PlafondDEssai * 3 / 60);

    [Fact]
    public void Le_plafond_est_une_vraie_borne_et_pas_un_voeu()
    {
        var log = Log();

        Remplir(log, AssezPourDepasser(), "valeur");

        new FileInfo(Journal).Length.Should().BeLessThan(PlafondDEssai * 2,
            "sans borne, ce fichier grandit tant que le produit sert, sur le disque de " +
            "quelqu'un qui n'a jamais demande a garder ca");
    }

    [Fact]
    public void Ce_qui_vient_d_etre_fait_est_toujours_la()
    {
        // La borne ne vaut que si elle jette le PASSE. Jeter le present rendrait le
        // journal inutile exactement quand on le consulte : juste apres un changement.
        var log = Log();

        Remplir(log, AssezPourDepasser(), "ancien");
        log.Change("LevierTresRecent", "Off", "On", "DrDXT");

        var texte = File.ReadAllText(Journal);
        texte.Should().Contain("LevierTresRecent");
    }

    [Fact]
    public void Ce_qui_a_ete_retire_est_DIT_a_la_place_ou_il_etait()
    {
        var log = Log();

        Remplir(log, AssezPourDepasser(), "ancien");

        var lignes = File.ReadAllLines(Journal);
        lignes.Should().NotBeEmpty();
        lignes[0].Should().Contain("entrees plus anciennes retirees",
            "un journal tronque se lit ; un journal qui se tait sur ce qu'il a perdu ment " +
            "par omission -- et c'est un journal d'audit");
    }

    [Fact]
    public void Un_journal_ordinaire_n_est_pas_touche()
    {
        // Le contrepoids : la rotation ne doit rien changer a l'usage reel, ou un
        // utilisateur pose quelques dizaines de lignes. Sans lui, on aurait pu tronquer
        // tout le temps et croire la borne satisfaite.
        var log = Log();

        Remplir(log, 50, "normal");

        var lignes = File.ReadAllLines(Journal);
        lignes.Should().HaveCount(50);
        lignes[0].Should().Contain("Levier0");
        lignes[0].Should().NotContain("retirees");
    }

    [Fact]
    public async Task L_export_ne_peut_donc_plus_depasser_cette_taille_non_plus()
    {
        // C'est la consequence qui se voit : l'export est le fichier que le client envoie
        // au support, et l'autre export du produit porte la regle « il doit pouvoir le
        // relire lui-meme avant de l'envoyer ».
        var log = Log();
        Remplir(log, AssezPourDepasser(), "valeur");

        var export = await log.ExportAsync();

        export.IsOk.Should().BeTrue();
        new FileInfo(export.Value!).Length.Should().BeLessThan(PlafondDEssai * 2);
    }
}
