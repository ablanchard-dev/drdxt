using System.Globalization;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// UI1 (critique du 08/10, défaut 7) : la limite de puissance se disait trois fois. « Pas encore validé sur une vraie
/// machine » s'affichait sur une vraie machine ; la note disait toujours « un arrêt récent le ferme », même quand rien
/// ne la ferme (la vraie raison d'une fermeture est dite par la phrase « pourquoi » d'Optimiser).
/// </summary>
public sealed class LaCarteSeDitUneFoisTests
{
    private static string Fr(string cle) => new ResxLocalizer().Get(cle, new CultureInfo("fr"));
    private static string En(string cle) => new ResxLocalizer().Get(cle, new CultureInfo("en"));

    [Fact]
    public void Un_reglage_non_prouve_le_dit_sans_nier_la_machine()
    {
        Fr("Carte.Propose.NonValide").Should().Be("pas encore prouvé sur ce matériel");
        En("Carte.Propose.NonValide").Should().NotContain("real machine");
    }

    [Fact]
    public void La_note_de_la_carte_ne_dit_que_ce_qui_est_toujours_vrai()
    {
        Fr("Carte.Note.ReglageCarte").Should().NotContain("arrêt").And.Contain("gain mesuré");
        En("Carte.Note.ReglageCarte").Should().NotContain("shutdown").And.NotContain("crash");
    }

    // Revue du 08/10 (ventilateur) : « jamais sous ce que la carte vise » n'était vrai qu'à l'instant de la pose ; une vitesse
    // fixée au repos reste fixe en jeu. Le texte dit ce qui est vrai.
    [Fact]
    public void Le_ventilateur_dit_qu_il_ne_suit_plus_la_temperature()
    {
        Fr("Carte.Expert.Ventilateur").Should().NotContain("jamais sous").And.Contain("température");
        En("Carte.Expert.Ventilateur").Should().NotContain("never below").And.Contain("temperature");
    }

    // UI1 (critique du 08/10, réseau) : « Pas encore disponible » s'affichait au-dessus du bouton actif de surveillance du
    // ping ; on ne savait plus si le bouton servait. Le texte nomme ce qui n'est pas proposé : la protection.
    [Fact]
    public void La_protection_du_ping_se_nomme_et_ne_dit_pas_le_bouton_indisponible()
    {
        Fr("Ping.Protection.Indisponible").Should().StartWith("Protection du ping").And.NotContain("Pas encore disponible");
        En("Ping.Protection.Indisponible").Should().StartWith("Ping protection").And.NotContain("Not available yet");
    }

    // Après ADM1 (08/10/2026) : la limite de puissance prouvée par « Vérifier DrDXT » sur ce PC ne dit plus « pas encore
    // prouvé sur ce matériel ».
    [Fact]
    public void Un_reglage_verifie_ici_le_dit_au_lieu_de_pas_encore_prouve()
    {
        DrDxt.App.ViewModels.ScanViewModel.MentionDePreuve(false, true, Fr).Should().StartWith("vérifié ici");
        DrDxt.App.ViewModels.ScanViewModel.MentionDePreuve(false, false, Fr).Should().Be("pas encore prouvé sur ce matériel");
        DrDxt.App.ViewModels.ScanViewModel.MentionDePreuve(true, false, Fr).Should().BeEmpty();
    }
}
