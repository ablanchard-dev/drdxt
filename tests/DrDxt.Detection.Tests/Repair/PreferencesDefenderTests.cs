using DrDxt.Core;
using DrDxt.Detection.Repair;
using DrDxt.Detection.Settings;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Les freins cachés du plan, l'accès contrôlé aux dossiers et les exclusions de Defender : lus et jugés, jamais écrits.
public sealed class PreferencesDefenderTests
{
    private static readonly string[] Jeux = [@"D:\SteamLibrary", @"C:\Program Files (x86)\Call of Duty"];

    private static PreferencesDefender.Lues Lu(int? acces = 0, string[]? autorisees = null, string[]? chemins = null, string[]? processus = null)
        => new(acces, autorisees ?? [], chemins ?? [], processus ?? []);

    [Fact]
    public void Un_plafond_de_frequence_ou_un_refroidissement_passif_est_un_frein()
    {
        PowerSettingsReader.Frein(new("frequence maximale", 3000, 0, "MHz")).Should().BeTrue();
        PowerSettingsReader.Frein(new("frequence maximale", 0, 0, "MHz")).Should().BeFalse("0 MHz veut dire aucun plafond");
        PowerSettingsReader.Frein(new("politique de refroidissement", 0, 1, "")).Should().BeTrue();
        PowerSettingsReader.Frein(new("etat maximal (coeurs efficaces)", 100, 100, "%")).Should().BeFalse();
        PowerSettingsReader.Frein(new("frequence maximale", null, 0, "MHz")).Should().BeNull("non lu n'est pas « bon »");
    }

    [Fact]
    public void Les_modes_disque_seulement_ne_sont_pas_l_audit_des_dossiers()
    {
        // ControlledFolderAccessType : 3 BlockDiskModificationOnly, 4 AuditDiskModificationOnly ; aucun ne garde les dossiers.
        PreferencesDefender.JugerAccesControle(Lu(3), Jeux).ValeurCourante.Should().Be("disque seulement");
        PreferencesDefender.JugerAccesControle(Lu(4), Jeux).ValeurCourante.Should().Be("disque seulement");
        PreferencesDefender.JugerAccesControle(Lu(2), Jeux).ValeurCourante.Should().Be("audit seulement");
    }

    [Fact]
    public void L_acces_controle_actif_sans_jeu_autorise_merite_le_regard()
    {
        PreferencesDefender.JugerAccesControle(Lu(1), Jeux).Statut.Should().Be(RepairStatus.Suspicious);
        PreferencesDefender.JugerAccesControle(Lu(1, [@"C:\Program Files (x86)\Call of Duty\cod.exe"]), Jeux).Statut.Should().Be(RepairStatus.Observed);
        PreferencesDefender.JugerAccesControle(Lu(0), Jeux).Statut.Should().Be(RepairStatus.ExpectedDefault);
        PreferencesDefender.JugerAccesControle(null, Jeux).Statut.Should().Be(RepairStatus.Unknown);
    }

    // Defender cache ses exclusions à un compte sans droits : « invisible d'ici », jamais « aucune ».
    [Fact]
    public void Des_exclusions_cachees_ne_valent_pas_aucune()
    {
        var c = PreferencesDefender.JugerExclusions(Lu(chemins: ["N/A: Must be an administrator to view exclusions"]), Jeux).Single();

        c.Statut.Should().Be(RepairStatus.Unknown);
        c.ValeurCourante.Should().Be("invisible d'ici");
    }

    [Fact]
    public void Une_exclusion_de_processus_par_nom_nu_est_un_risque_et_rien_n_est_conseille_a_exclure()
    {
        var constats = PreferencesDefender.JugerExclusions(Lu(chemins: [@"D:\"], processus: ["cod.exe", @"C:\outils\x.exe"]), Jeux).ToList();

        constats.Single(c => c.Nom == "exclusion de processus par nom").ValeurCourante.Should().Be("cod.exe");
        constats.Single(c => c.Nom == "exclusions Defender").ValeurCourante.Should().Be("0 exclu(s), 1 couvert(s) par un dossier parent, 1 non exclu(s)");
        constats.Should().NotContain(c => c.Nom == "exclusions Defender" && c.Action != RepairAction.Aucune,
            "DrDXT ne conseille jamais d'ajouter une exclusion");
    }
}
