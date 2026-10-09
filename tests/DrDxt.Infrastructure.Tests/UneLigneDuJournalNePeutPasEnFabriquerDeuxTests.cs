using FluentAssertions;
using DrDxt.Infrastructure.Diagnostics;

namespace DrDxt.Infrastructure.Tests;

// -----------------------------------------------------------------------
// 18/09/2026. `DiagnosticsLog` est le journal d'audit de DrDXT : ce qu'un
// utilisateur exporte et envoie au support quand quelque chose a bouge sur sa
// machine. Sa documentation annonce une garantie, en tete de classe :
//
//     // One line per entry:
//     //   2026-06-21T14:03:22Z [DrDXT] tweak=... from='Off' to='On'
//
// et, six lignes plus bas, la reconnait comme NON TENUE :
//
//     // Note: values are not newline-sanitized; callers must pass single-line
//     // strings. Windows registry/power-plan values are single-line in
//     // practice, so this is fine for V1.
//
// Une precondition ecrite dans un commentaire n'est pas une precondition : rien
// ne la verifie, et un `\r\n` dans une valeur ne casse pas le fichier -- il y
// AJOUTE une entree, horodatee et signee comme les vraies. Sur un journal
// d'audit, c'est la difference entre un fichier abime (qu'on voit) et un
// fichier qui ment (qu'on ne voit pas).
//
// Ce que je n'affirme pas : qu'un appelant le fasse aujourd'hui. Verifie ce
// jour, les deux chemins qui portent une valeur venue de la machine sont surs
// par accident -- `PrivilegedOpCodec` serialise sans indentation (donc du JSON
// sur une ligne), et la verification d'etat ne remplit `RequestedState` que
// pour les `DWord`, les REG_MULTI_SZ y ressortant `null`. Ce test ne repare
// donc pas une casse observee : il tient la phrase que la classe donne deja
// pour sa garantie, au lieu de la laisser dependre de deux accidents.
// -----------------------------------------------------------------------
public sealed class UneLigneDuJournalNePeutPasEnFabriquerDeuxTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "drdxt-journal-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { /* le menage n'est pas le test */ }
    }

    private string[] LignesDuJournal() =>
        File.ReadAllLines(Path.Combine(_dossier, "changes.journal.log"));

    [Fact]
    public void Le_crible_lit_bien_une_entree_ordinaire()
    {
        // Temoin : sans lui, un journal vide rendrait le test suivant vert sur du vide.
        var journal = new DiagnosticsLog(_dossier);

        journal.Change("ReflexLowLatency", "Off", "On", "DrDXT");

        var lignes = LignesDuJournal();
        lignes.Should().HaveCount(1);
        lignes[0].Should().Contain("tweak=ReflexLowLatency").And.Contain("from='Off'").And.Contain("to='On'");
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void Une_valeur_multiligne_n_ajoute_pas_d_entree(string saut)
    {
        var journal = new DiagnosticsLog(_dossier);
        // Une entree forgee, mise en forme comme les vraies.
        var valeur = $"Off{saut}2026-01-01T00:00:00Z [DrDXT] tweak=HpetForce from='On' to='Off'";

        journal.Change("ReflexLowLatency", valeur, "On", "DrDXT");

        LignesDuJournal().Should().HaveCount(1,
            "une entree du journal doit rester une ligne : un saut de ligne dans une valeur " +
            "n'abime pas le fichier, il y AJOUTE une entree qui a l'air vraie");
    }

    [Fact]
    public void Le_texte_de_la_valeur_n_est_pas_perdu()
    {
        // L'autre sens : neutraliser le saut de ligne ne doit pas amputer ce qui a ete lu sur
        // la machine. Un journal d'audit qui tronque une valeur ment lui aussi, plus poliment.
        var journal = new DiagnosticsLog(_dossier);

        journal.Change("SanePagefile", "C:\\pagefile.sys 0 0\nD:\\pagefile.sys 16 16", "system-managed", "DrDXT");

        var ligne = LignesDuJournal().Single();
        ligne.Should().Contain("C:\\pagefile.sys 0 0");
        ligne.Should().Contain("D:\\pagefile.sys 16 16", "les deux entrees d'un REG_MULTI_SZ doivent rester lisibles");
    }

    [Fact]
    public async Task L_export_rend_exactement_le_journal()
    {
        // Ce que le support recoit doit etre ce que la machine a ecrit, ligne pour ligne.
        var journal = new DiagnosticsLog(_dossier);
        journal.Change("A", "1", "2", "DrDXT");
        journal.Change("B", "x\ny", "z", "DrDXT");

        var chemin = await journal.ExportAsync();

        chemin.IsOk.Should().BeTrue();
        File.ReadAllLines(chemin.Value!).Should().HaveCount(2).And.BeEquivalentTo(LignesDuJournal());
    }
}
