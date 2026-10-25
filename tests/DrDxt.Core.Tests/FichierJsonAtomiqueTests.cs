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
        try
        {
            // Une jonction se retire seule d'abord : la suppression récursive refuse de la traverser.
            foreach (var d in Directory.GetDirectories(_dossier).Where(d => new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)))
                Directory.Delete(d);
            Directory.Delete(_dossier, recursive: true);
        }
        catch { /* nettoyage au mieux */ }
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

    // Le contenu sur le disque ne suffit pas : un renommage resté en cache pouvait, après une coupure, rendre le fichier
    // d'avant, sans l'entrée que le journal d'écriture anticipée venait de noter. Le renommage est lui aussi écrit à travers.
    [Fact]
    public void Le_renommage_remplace_la_cible_et_part_sur_le_disque()
    {
        Directory.CreateDirectory(_dossier);
        var cible = Path.Combine(_dossier, "journal.json");
        File.WriteAllText(cible, "avant");
        var tmp = FichierJsonAtomique.NomTemporaire(cible);
        File.WriteAllText(tmp, "apres");

        FichierJsonAtomique.Remplacer(tmp, cible);

        File.ReadAllText(cible).Should().Be("apres");
        File.Exists(tmp).Should().BeFalse();

        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var source = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.Core", "Storage", "FichierJsonAtomique.cs"));
        source.Should().Contain("MoveFileExW(tmp, cible, RemplacerExistant | EcritureATravers)")
            .And.Contain("EcritureATravers = 0x8", "MOVEFILE_WRITE_THROUGH");
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
            .Should().BeLessThan(ecrire.IndexOf("Remplacer(tmp", StringComparison.Ordinal));
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

    // Élevé, un dossier du compte peut être une jonction posée par le compte vers une racine protégée : le processus
    // administrateur y écrirait. Aucune écriture élevée ne traverse un point d'analyse ; sans élévation, rien ne change.
    private (string Lien, string Cible) Jonction()
    {
        var cible = Path.Combine(_dossier, "protege");
        var lien = Path.Combine(_dossier, "DrDXT");
        Directory.CreateDirectory(cible);
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{lien}\" \"{cible}\"")
        { CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        new DirectoryInfo(lien).Attributes.HasFlag(FileAttributes.ReparsePoint).Should().BeTrue("témoin : la jonction existe");
        return (lien, cible);
    }

    [Fact]
    public void Eleve_rien_ne_s_ecrit_a_travers_une_jonction()
    {
        var (lien, cible) = Jonction();
        var chemin = Path.Combine(lien, "coupures-demarrage.json");

        FichierJsonAtomique.PointDAnalyseSurLeChemin(chemin).Should().Be(lien);
        new FichierJsonAtomique(chemin, ErrorCodes.IoFailure, eleve: () => true).WriteAtomic("[]").IsOk.Should().BeFalse();
        FichierJsonAtomique.AjouterSansLien(chemin, "x", ErrorCodes.IoFailure, eleve: () => true).IsOk.Should().BeFalse();
        Directory.GetFiles(cible).Should().BeEmpty("rien n'est posé dans la cible");

        File.WriteAllText(Path.Combine(cible, "coupures-demarrage.json"), "[]");
        new FichierJsonAtomique(chemin, ErrorCodes.IoFailure, eleve: () => true).MettreDeCote().Should().BeFalse();
        Directory.GetFiles(cible).Should().ContainSingle("le fichier de la cible n'est pas renommé");
    }

    [Fact]
    public void Sans_elevation_une_jonction_du_compte_reste_suivie()
    {
        var (lien, cible) = Jonction();

        new FichierJsonAtomique(Path.Combine(lien, "f.json"), ErrorCodes.IoFailure, eleve: () => false).WriteAtomic("[1]").IsOk.Should().BeTrue();

        File.ReadAllText(Path.Combine(cible, "f.json")).Should().Be("[1]");
    }

    [Fact]
    public void Eleve_un_chemin_sans_point_d_analyse_s_ecrit()
    {
        var chemin = Path.Combine(_dossier, "sous", "f.json");

        FichierJsonAtomique.PointDAnalyseSurLeChemin(chemin).Should().BeNull();
        new FichierJsonAtomique(chemin, ErrorCodes.IoFailure, eleve: () => true).WriteAtomic("[1]").IsOk.Should().BeTrue();
    }

    private sealed record Entree(string Nom);

    // Pour un écrivain qui lit puis réécrit une liste : une lecture ratée est un échec, jamais une liste vide qu'il écraserait ;
    // absent ou vide, la liste est vide ; illisible ou de la mauvaise forme, elle est mise de côté avant.
    [Fact]
    public void La_liste_a_reecrire_distingue_lecture_ratee_absence_et_liste_illisible()
    {
        Directory.CreateDirectory(_dossier);
        var chemin = Path.Combine(_dossier, "l.json");
        var f = new FichierJsonAtomique(chemin, ErrorCodes.IoFailure);

        f.LireListePourReecrire<Entree>().Value.Should().BeEmpty("absent : une liste vide, légitime");

        File.WriteAllText(chemin, "[{\"Nom\":\"a\"}]");
        using (new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.None))
            f.LireListePourReecrire<Entree>().IsOk.Should().BeFalse("ne se lit pas : un échec, pas une liste vide");
        f.LireListePourReecrire<Entree>().Value.Should().Equal(new Entree("a"));

        File.WriteAllText(chemin, "[1,2]");
        f.LireListePourReecrire<Entree>().Value.Should().BeEmpty();
        Directory.GetFiles(_dossier, "l.json.mis-de-cote-*").Should().ContainSingle("de la mauvaise forme : mise de côté, pas écrasée");
    }

    // Un autre processus lit le fichier à l'instant du remplacement (l'App et le relevé de fond lisent les mêmes journaux) :
    // le renommage par-dessus une cible ouverte est refusé. L'écriture était perdue ; elle reprend une fois la lecture finie.
    [Fact]
    public async Task Un_lecteur_qui_tient_la_cible_ne_fait_pas_perdre_l_ecriture()
    {
        Directory.CreateDirectory(_dossier);
        var chemin = Path.Combine(_dossier, "journal.json");
        File.WriteAllText(chemin, "[]");
        var fichier = new FichierJsonAtomique(chemin, "TEST");

        Result ecrit;
        using (var lecteur = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var ecriture = Task.Run(() => fichier.WriteAtomic("[1]"));
            await Task.Delay(100);
            lecteur.Dispose();
            ecrit = await ecriture;
        }

        ecrit.IsOk.Should().BeTrue();
        File.ReadAllText(chemin).Should().Be("[1]");
    }

    // Un journal qui ne fait qu'allonger (crash.log, une panne d'interface après l'autre) garde au-delà de son plafond la
    // moitié la plus récente, coupée à une ligne.
    [Fact]
    public void Un_journal_borne_garde_sa_fin_la_plus_recente()
    {
        Directory.CreateDirectory(_dossier);
        var chemin = Path.Combine(_dossier, "crash.log");
        var lignes = Enumerable.Range(0, 5000).Select(i => $"ligne {i:D5} " + new string('x', 40)).ToList();
        FichierJsonAtomique.AjouterSansLien(chemin, string.Join("\n", lignes) + "\n", "T").IsOk.Should().BeTrue();

        FichierJsonAtomique.BornerSansLien(chemin, 64 * 1024, "T").IsOk.Should().BeTrue();

        var reste = FichierJsonAtomique.LireSansLien(chemin, "T").Value!;
        new FileInfo(chemin).Length.Should().BeLessThanOrEqualTo(64 * 1024);
        reste.Should().EndWith(lignes[^1] + "\n").And.StartWith("ligne ");
        reste.Length.Should().BeGreaterThan(16 * 1024, "témoin : la fin est gardée, pas effacée");
    }

    [Fact]
    public void Un_journal_sous_son_plafond_n_est_pas_touche()
    {
        Directory.CreateDirectory(_dossier);
        var chemin = Path.Combine(_dossier, "crash.log");
        FichierJsonAtomique.AjouterSansLien(chemin, "court\n", "T");
        var avant = File.GetLastWriteTimeUtc(chemin);

        FichierJsonAtomique.BornerSansLien(chemin, 64 * 1024, "T").IsOk.Should().BeTrue();

        File.GetLastWriteTimeUtc(chemin).Should().Be(avant);
        File.ReadAllText(chemin).Should().Be("court\n");
    }
}
