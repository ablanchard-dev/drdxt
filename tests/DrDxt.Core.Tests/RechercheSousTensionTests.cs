using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// La recherche de la sous-tension stable, par paliers, en pur. Chaque palier
// passe le test de refroidissement ; une erreur matérielle, un plantage (le PC redémarre en plein palier) ou une écriture refusée
// arrêtent la recherche ; le verdict est le dernier palier stable.
public sealed class RechercheSousTensionTests
{
    [Fact]
    public void Les_paliers_descendent_jusqu_au_premier_instable_et_le_verdict_est_le_dernier_stable()
    {
        var r = RechercheSousTension.Nouvelle(new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));
        r.PalierEnCours.Should().Be(-30);

        r = r.Apres(IssuePalier.Stable(maxC: 92, freinePourcent: 20));
        r.PalierEnCours.Should().Be(-50);
        r = r.Apres(IssuePalier.Stable(maxC: 88, freinePourcent: 8));
        r = r.Apres(IssuePalier.Instable("2 erreurs matérielles corrigées (WHEA)"));

        r.Finie.Should().BeTrue();
        r.Verdict.Should().Be(-50);
        r.Raison.Should().Contain("WHEA");
    }

    [Fact]
    public void Tous_stables_le_verdict_est_le_plus_bas_et_jamais_sous_le_plancher()
    {
        var r = RechercheSousTension.Nouvelle(DateTimeOffset.UnixEpoch);
        while (!r.Finie) r = r.Apres(IssuePalier.Stable(80, 0));
        r.Verdict.Should().Be(RechercheSousTension.Paliers[^1]);
        RechercheSousTension.Paliers.Should().OnlyContain(p => p < 0 && p >= -TensionCpu.PlancherMv);
    }

    [Fact]
    public void Un_plantage_en_plein_palier_se_lit_au_redemarrage_et_garde_le_dernier_stable()
    {
        var debut = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
        var r = RechercheSousTension.Nouvelle(debut).Apres(IssuePalier.Stable(92, 20)); // −30 stable, −50 en cours
        var palierCommence = debut.AddMinutes(3);
        r = r with { PalierCommenceLe = palierCommence };

        r.ApresRedemarrage(demarrage: palierCommence.AddMinutes(1)).Verdict.Should().Be(-30, "−50 a planté le PC");
        r.ApresRedemarrage(demarrage: palierCommence.AddMinutes(-60)).Finie.Should().BeFalse("démarré avant le palier : pas de plantage");
    }

    [Fact]
    public void Une_ecriture_refusee_des_le_premier_palier_n_a_pas_de_verdict_et_dit_pourquoi()
    {
        var r = RechercheSousTension.Nouvelle(DateTimeOffset.UnixEpoch).Apres(IssuePalier.Refuse("Undervolt Protection"));
        r.Finie.Should().BeTrue();
        r.Verdict.Should().BeNull();
        r.Raison.Should().Contain("Undervolt Protection");
    }
}
