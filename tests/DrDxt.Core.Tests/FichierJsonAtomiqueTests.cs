using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// L'ecriture atomique, une seule fois. Cinq magasins l'appellent (references memoire et CPU,
/// journal de performance, journal des constats, marqueur BIOS) ; trois la recopiaient. Ces tests
/// portent ce que les cinq supposent.
/// </summary>
public sealed class FichierJsonAtomiqueTests : IDisposable
{
    private readonly string _dossier =
        Path.Combine(Path.GetTempPath(), "drdxt-atomique-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { /* nettoyage au mieux */ }
    }

    [Fact]
    public void Cree_le_dossier_ecrit_et_ne_laisse_aucun_tmp()
    {
        var chemin = Path.Combine(_dossier, "sous", "f.json");

        new FichierJsonAtomique(chemin, ErrorCodes.IoFailure).WriteAtomic("[1]").IsOk.Should().BeTrue();

        File.ReadAllText(chemin).Should().Be("[1]");
        File.Exists(chemin + ".tmp").Should().BeFalse("le temporaire est renomme, jamais laisse");
    }

    [Fact]
    public void Une_seconde_ecriture_REMPLACE_le_contenu()
    {
        var chemin = Path.Combine(_dossier, "f.json");
        var fichier = new FichierJsonAtomique(chemin, ErrorCodes.IoFailure);

        fichier.WriteAtomic("[1]");
        fichier.WriteAtomic("[2]");

        fichier.Read().Value.Should().Be("[2]");
        File.Exists(chemin + ".tmp").Should().BeFalse();
    }

    // Sans vidage, après une coupure, le renommage peut avoir pris et le contenu non : la cible est illisible, et ce sont
    // des journaux d'annulation. Le temporaire est écrit en WriteThrough et vidé sur le disque avant le renommage.
    [Fact]
    public void Le_temporaire_est_force_sur_le_disque_avant_le_renommage()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var source = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.Core", "Storage", "FichierJsonAtomique.cs"));
        var ecrire = source[source.IndexOf("public Result WriteAtomic", StringComparison.Ordinal)..];
        ecrire = ecrire[..ecrire.IndexOf("public bool MettreDeCote", StringComparison.Ordinal)];

        ecrire.Should().Contain("FileOptions.WriteThrough").And.Contain("Flush(flushToDisk: true)");
        ecrire.IndexOf("Flush(flushToDisk: true)", StringComparison.Ordinal)
            .Should().BeLessThan(ecrire.IndexOf("File.Move(", StringComparison.Ordinal));
    }

    [Fact]
    public void Mettre_de_cote_garde_la_trace_et_libere_la_cible()
    {
        var chemin = Path.Combine(_dossier, "f.json");
        var fichier = new FichierJsonAtomique(chemin, ErrorCodes.IoFailure);
        fichier.WriteAtomic("{ abime");

        fichier.MettreDeCote().Should().BeTrue();

        File.Exists(chemin).Should().BeFalse();
        File.ReadAllText(Directory.GetFiles(_dossier, "f.json.mis-de-cote-*").Single()).Should().Be("{ abime");
    }

    [Fact]
    public void Un_fichier_absent_se_lit_null_et_pas_en_echec()
    {
        var r = new FichierJsonAtomique(Path.Combine(_dossier, "absent.json"), ErrorCodes.IoFailure).Read();

        r.IsOk.Should().BeTrue();
        r.Value.Should().BeNull();
    }

    [Fact]
    public void Un_echec_porte_le_code_DU_MAGASIN_et_ne_leve_rien()
    {
        // Le dossier parent est un fichier : impossible d'y creer quoi que ce soit.
        Directory.CreateDirectory(_dossier);
        var bloquant = Path.Combine(_dossier, "bloquant");
        File.WriteAllText(bloquant, "x");

        var r = new FichierJsonAtomique(Path.Combine(bloquant, "f.json"), ErrorCodes.BiosPendingChangeIo)
            .WriteAtomic("[1]");

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosPendingChangeIo,
            "un appelant qui distingue ses erreurs par code ne doit pas les voir fusionner");
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lien, string cible, IntPtr securite);

    // En App élevée, un lien dur posé sous le nom prévisible « <fichier>.tmp » vers un fichier protégé faisait tronquer et
    // réécrire ce fichier en administrateur : l'ouverture en FileMode.Create suit le lien. Le temporaire a un nom aléatoire,
    // créé en exclusif : le lien posé à l'avance n'est jamais ouvert.
    [Fact]
    public void Un_lien_dur_pose_sous_le_nom_du_temporaire_ne_fait_jamais_reecrire_sa_cible()
    {
        Directory.CreateDirectory(_dossier);
        var protege = Path.Combine(_dossier, "protege.txt");
        File.WriteAllText(protege, "intact");
        var chemin = Path.Combine(_dossier, "f.json");
        CreateHardLink(chemin + ".tmp", protege, IntPtr.Zero).Should().BeTrue("le lien dur du test doit se poser");

        new FichierJsonAtomique(chemin, ErrorCodes.IoFailure).WriteAtomic("[1]").IsOk.Should().BeTrue();

        File.ReadAllText(protege).Should().Be("intact");
        File.ReadAllText(chemin).Should().Be("[1]");
    }

    // Une écriture qui échoue (la cible est un dossier) ne laisse aucun temporaire derrière elle.
    [Fact]
    public void Une_ecriture_qui_echoue_ne_laisse_aucun_temporaire()
    {
        var chemin = Path.Combine(_dossier, "f.json");
        Directory.CreateDirectory(chemin);

        new FichierJsonAtomique(chemin, ErrorCodes.IoFailure).WriteAtomic("[1]").IsOk.Should().BeFalse();

        Directory.GetFiles(_dossier).Should().BeEmpty();
    }
    // Un journal qu'on allonge (crash.log, journal des changements) : un nom posé d'avance en lien dur vers un autre fichier
    // faisait écrire l'App élevée dans ce fichier. L'ajout ouvre sans suivre de lien, lit le nombre de liens sur le handle,
    // et n'écrit que dans un fichier qui n'a qu'un nom.
    [Fact]
    public void Un_ajout_ne_s_ecrit_jamais_a_travers_un_lien_dur()
    {
        Directory.CreateDirectory(_dossier);
        var temoin = Path.Combine(_dossier, "temoin.txt");
        File.WriteAllText(temoin, "intact");
        var journal = Path.Combine(_dossier, "journal.log");
        CreateHardLink(journal, temoin, IntPtr.Zero).Should().BeTrue("le lien dur du test doit se poser");

        FichierJsonAtomique.AjouterSansLien(journal, "ligne", ErrorCodes.IoFailure).IsOk.Should().BeFalse();

        File.ReadAllText(temoin).Should().Be("intact");
    }

    [Fact]
    public void Un_ajout_cree_le_journal_puis_l_allonge()
    {
        var journal = Path.Combine(_dossier, "sous", "journal.log");

        FichierJsonAtomique.AjouterSansLien(journal, "a", ErrorCodes.IoFailure).IsOk.Should().BeTrue();
        FichierJsonAtomique.AjouterSansLien(journal, "b", ErrorCodes.IoFailure).IsOk.Should().BeTrue();

        File.ReadAllText(journal).Should().Be("ab");
    }
}
