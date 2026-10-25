using FluentAssertions;

namespace DrDxt.Integration.Tests;

// -----------------------------------------------------------------------
// Trouve par l'audit de DrDXT sur sa propre installation (rapport exporte d'un scan reel) :
// « Service systeme remplacable : PresentMon Service » et « DrDXT - reglages au demarrage ». Lu ensuite :
// les fichiers poses dans Program Files\DrDXT appartenaient au compte qui avait lance l'installeur, avec
// le controle total (regle CREATEUR PROPRIETAIRE de Program Files). UAC active, ce compte non eleve
// pouvait remplacer deux executables qui tournent en SYSTEM et le helper qui tourne en administrateur.
//
// Mesure sur la machine de reference apres correction : 693 fichiers, tous aux Administrateurs, aucun
// modifiable par un compte ordinaire. Ce test garde l'etape et son ordre : un service demarre avant
// que les droits soient repris laisserait la fenetre ouverte.
// -----------------------------------------------------------------------
public sealed class LInstalleurRendLaProprieteAuxAdministrateursTests
{
    private static string Iss()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "installer", "DrDXT.iss")).Replace("\r\n", "\n");
    }

    [Fact]
    public void La_propriete_des_fichiers_copies_revient_aux_Administrateurs_puis_l_arbre_est_relu()
    {
        var iss = Iss();
        var proteger = iss[iss.IndexOf("function ProtegerLeDossier(): Boolean;", StringComparison.Ordinal)..];
        proteger = proteger[..proteger.IndexOf("\nend;", StringComparison.Ordinal)];

        proteger.Should().Contain("'\\*\" /setowner *S-1-5-32-544 /T /C /Q'",
            "le SID des Administrateurs, pas un nom traduit selon la langue ; les fichiers, pas le dossier, qui perdrait sa regle "
            + "DROITS DU PROPRIETAIRE");
        proteger.IndexOf("/setowner", StringComparison.Ordinal).Should()
            .BeLessThan(proteger.IndexOf("Droits('verifier'", StringComparison.Ordinal), "la relecture vient apres");
    }

    [Fact]
    public void Aucun_service_ne_demarre_avant_que_les_droits_soient_repris()
    {
        var iss = Iss();
        var etape = iss[iss.IndexOf("procedure CurStepChanged", StringComparison.Ordinal)..];
        etape = etape[..etape.IndexOf("\nend;", StringComparison.Ordinal)];

        var proteger = etape.IndexOf("ProtegerLeDossier()", StringComparison.Ordinal);
        proteger.Should().BeGreaterThan(0);
        proteger.Should().BeLessThan(etape.IndexOf("InstallPresentMonService()", StringComparison.Ordinal));
        proteger.Should().BeLessThan(etape.IndexOf("InstallOrRemoveReconcilerService()", StringComparison.Ordinal));
    }

    // Relecture adverse : les codes de retour d'icacls allaient au journal et les services SYSTEM
    // demarraient quand meme, avec des fichiers qu'un compte ordinaire pouvait remplacer. Un echec les laisse
    // non installes, et le joueur le lit.
    [Fact]
    public void Si_les_droits_ne_sont_pas_repris_aucun_service_n_est_installe_et_le_joueur_le_lit()
    {
        var iss = Iss();
        iss.Should().Contain("function ProtegerLeDossier(): Boolean;", "la protection rend un verdict");
        var etape = iss[iss.IndexOf("procedure CurStepChanged", StringComparison.Ordinal)..];
        etape = etape[..etape.IndexOf("\nend;", StringComparison.Ordinal)];

        var garde = etape.IndexOf("if ProtegerLeDossier() then", StringComparison.Ordinal);
        garde.Should().BeGreaterThan(0);
        garde.Should().BeLessThan(etape.IndexOf("InstallPresentMonService()", StringComparison.Ordinal));
        etape.Should().Contain("SuppressibleMsgBox(", "un echec se dit, meme en installation silencieuse il est journalise");
    }
}
