using DrDxt.Application.Safety;
using FluentAssertions;

namespace DrDxt.Application.Tests.Safety;

/// <summary>La garde du BCD retiré s'écrit avant bcdedit, forcée sur le disque : une coupure entre les deux ne laisse plus
/// un réglage retiré sans annulation. Fichier du compte : seul l'élément de la carte en ressort.</summary>
public sealed class GardeDuBcdRetireTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), $"drdxt-bcd-{Guid.NewGuid():N}");
    private string Fichier => Path.Combine(_dossier, "bcd-rendu.json");
    public void Dispose() { try { Directory.Delete(_dossier, recursive: true); } catch { /* dossier temporaire du test */ } }

    [Fact]
    public void Ecrite_elle_est_sur_le_disque_tout_de_suite_et_se_relit()
    {
        var garde = new GardeDuBcdRetire(Fichier);

        garde.Ecrire(GardeDuBcdRetire.Element, "IgnoreAllFailures").Should().BeTrue();

        File.Exists(Fichier).Should().BeTrue();
        File.Exists(Fichier + ".tmp").Should().BeFalse("le fichier final s'écrit directement : aucun renommage laissé au cache");
        new GardeDuBcdRetire(Fichier).Lire().Should().Be((GardeDuBcdRetire.Element, "IgnoreAllFailures"));
    }

    [Fact]
    public void Un_autre_element_ne_s_ecrit_pas()
    {
        new GardeDuBcdRetire(Fichier).Ecrire("useplatformclock", "Yes").Should().BeFalse();
        File.Exists(Fichier).Should().BeFalse();
    }

    // Le helper borne les valeurs, pas l'élément : une garde modifiée à la main ne fait rien poser d'autre.
    [Fact]
    public void Une_garde_modifiee_pour_un_autre_element_n_est_pas_rendue()
    {
        Directory.CreateDirectory(_dossier);
        File.WriteAllText(Fichier, "{\"Element\":\"useplatformclock\",\"Valeur\":\"Yes\"}");

        new GardeDuBcdRetire(Fichier).Lire().Should().BeNull();
    }

    [Fact]
    public void Sans_disque_ecrivable_l_ecriture_le_dit()
    {
        Directory.CreateDirectory(_dossier);
        File.WriteAllText(Path.Combine(_dossier, "fichier"), "");
        // Le dossier de la garde est un fichier : rien ne peut s'écrire, l'appelant ne touche pas au BCD.
        new GardeDuBcdRetire(Path.Combine(_dossier, "fichier", "bcd-rendu.json"))
            .Ecrire(GardeDuBcdRetire.Element, "IgnoreAllFailures").Should().BeFalse();
    }

    [Fact]
    public void Effacee_apres_un_refus_du_helper_il_ne_reste_rien()
    {
        var garde = new GardeDuBcdRetire(Fichier);
        garde.Ecrire(GardeDuBcdRetire.Element, "IgnoreAllFailures");

        garde.Effacer();

        garde.Lire().Should().BeNull();
        File.Exists(Fichier).Should().BeFalse();
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lien, string cible, IntPtr securite);

    // Fichier du compte, écrit par l'App (élevée) : un nom posé d'avance en lien dur faisait réécrire un autre fichier. Le
    // nom est retiré (le lien seul), puis le fichier créé en exclusif ; un lien reposé entre les deux fait échouer la garde,
    // donc le BCD n'est pas écrit.
    [Fact]
    public void Un_lien_dur_pose_sous_le_nom_de_la_garde_ne_fait_jamais_reecrire_sa_cible()
    {
        Directory.CreateDirectory(_dossier);
        var temoin = Path.Combine(_dossier, "temoin.txt");
        File.WriteAllText(temoin, "intact");
        CreateHardLink(Fichier, temoin, IntPtr.Zero).Should().BeTrue();

        new GardeDuBcdRetire(Fichier).Ecrire(GardeDuBcdRetire.Element, "IgnoreAllFailures").Should().BeTrue();

        File.ReadAllText(temoin).Should().Be("intact");
        new GardeDuBcdRetire(Fichier).Lire().Should().Be((GardeDuBcdRetire.Element, "IgnoreAllFailures"));
    }
}
