using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

// Les changements venus de l'extérieur : seule une version qui change devient une ligne de la frise.
public sealed class MondeDeLaMachineTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 16, 20, 0, 0, TimeSpan.Zero);

    private static MondeDeLaMachine.Etat E(int jours, string versionJeu, string versionPilote)
        => new(T.AddDays(jours), new() { [@"C:\Program Files (x86)\Call of Duty"] = versionJeu }, new() { ["NVIDIA GeForce RTX 4060"] = versionPilote });

    [Fact]
    public void Un_jeu_mis_a_jour_et_un_pilote_change_deviennent_deux_lignes_datees()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var ecarts = MondeDeLaMachine.Comparer(E(0, "3.14.2.28787707", "32.0.15.8180"), E(1, "3.14.3.28900000", "32.0.15.8195"));
        ecarts.Select(e => e.Libelle).Should().Equal(
            "Jeu mis à jour : Call of Duty 3.14.2.28787707 → 3.14.3.28900000",
            "Pilote d'affichage changé : NVIDIA GeForce RTX 4060 32.0.15.8180 → 32.0.15.8195");
        ecarts.Should().OnlyContain(e => e.Cle.StartsWith("monde:"));
    }

    // Notre règle : rien n'est déduit d'un nom de fichier ni d'une apparition ; seule une version lue qui change compte.
    [Fact]
    public void Rien_ne_change_ou_un_jeu_qui_apparait_ne_fait_aucune_ligne()
    {
        MondeDeLaMachine.Comparer(E(0, "1", "1"), E(1, "1", "1")).Should().BeEmpty();
        var nouveau = E(1, "1", "1") with { Jeux = new() { [@"D:\Jeux\Nouveau"] = "1.0" } };
        MondeDeLaMachine.Comparer(E(0, "1", "1"), nouveau).Should().BeEmpty();
    }

    [Fact]
    public void Un_premier_releve_ne_rend_rien_et_garde_l_etat()
    {
        var fichier = Path.Combine(Path.GetTempPath(), $"monde-{Guid.NewGuid():N}.json");
        MondeDeLaMachine.LirePrecedent(fichier).Should().BeNull();
        File.WriteAllText(fichier, System.Text.Json.JsonSerializer.Serialize(E(0, "1", "1")));
        MondeDeLaMachine.LirePrecedent(fichier)!.Jeux.Should().ContainKey(@"C:\Program Files (x86)\Call of Duty");
        File.Delete(fichier);
    }

    // Un état gardé sans ses listes ({}) vaut pas d'état : la comparaison ne tombe pas sur une liste absente.
    [Fact]
    public void Un_etat_garde_sans_ses_listes_vaut_pas_d_etat()
    {
        var fichier = Path.Combine(Path.GetTempPath(), $"monde-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(fichier, "{}");
            MondeDeLaMachine.LirePrecedent(fichier).Should().BeNull();
            File.WriteAllText(fichier, System.Text.Json.JsonSerializer.Serialize(E(0, "1", "1")));
            MondeDeLaMachine.LirePrecedent(fichier).Should().NotBeNull("témoin : un état complet se lit");
        }
        finally { File.Delete(fichier); }
    }

    // Ce qui n'a pas été lu à ce scan garde son état d'avant : des jeux non lus ne deviennent pas « aucun jeu », sans quoi la
    // mise à jour suivante ne serait plus vue.
    [Fact]
    public void Ce_qui_n_a_pas_ete_lu_garde_son_etat_d_avant()
    {
        var avant = E(0, "1", "1");
        var garde = MondeDeLaMachine.Garder(avant, null, new() { ["PCI\\VEN_10DE\\1"] = "2" }, new() { ["PCI\\VEN_10DE\\1"] = "RTX" }, T.AddDays(1));
        garde.Jeux.Should().BeEquivalentTo(avant.Jeux);
        garde.Pilotes.Should().ContainKey("PCI\\VEN_10DE\\1");
    }

    // Deux cartes du même modèle sont deux cartes : la clé est l'identifiant d'instance PnP, le nom n'est que le libellé.
    [Fact]
    public void Deux_cartes_du_meme_nom_gardent_chacune_leur_pilote()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var noms = new Dictionary<string, string> { ["PCI\\A"] = "RTX 4060", ["PCI\\B"] = "RTX 4060" };
        var avant = new MondeDeLaMachine.Etat(T, [], new() { ["PCI\\A"] = "1", ["PCI\\B"] = "1" }, noms);
        var apres = new MondeDeLaMachine.Etat(T.AddDays(1), [], new() { ["PCI\\A"] = "1", ["PCI\\B"] = "2" }, noms);
        MondeDeLaMachine.Comparer(avant, apres).Select(e => e.Libelle).Should().Equal("Pilote d'affichage changé : RTX 4060 1 → 2");
    }

    // HVCI rallumée entre deux scans devient une ligne de la frise ; non lue d'un côté, rien n'est affirmé.
    [Fact]
    public void L_integrite_de_la_memoire_qui_change_d_etat_devient_une_ligne()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var avant = E(0, "1", "1") with { Hvci = false };
        MondeDeLaMachine.Comparer(avant, E(1, "1", "1") with { Hvci = true }).Select(e => e.Libelle)
            .Should().Equal("Intégrité de la mémoire (HVCI) rallumée depuis le scan précédent.");
        MondeDeLaMachine.Comparer(avant, E(1, "1", "1") with { Hvci = false }).Should().BeEmpty();
        MondeDeLaMachine.Comparer(avant, E(1, "1", "1")).Should().BeEmpty("non lue n'est ni rallumée ni coupée");
        MondeDeLaMachine.Garder(avant, null, null, null, T.AddDays(1)).Hvci.Should().BeFalse("non lue, elle garde son état d'avant");
    }

    // La clé d'un écart est gardée d'un scan à l'autre : un format régional à calendrier hégirien (ar-SA) l'écrivait en 1448,
    // et un changement de format en faisait une autre clé pour le même écart.
    [Fact]
    public void La_cle_d_un_ecart_ne_depend_pas_du_format_regional()
    {
        var avant = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            MondeDeLaMachine.Comparer(E(0, "1", "1") with { Hvci = false }, E(1, "1", "1") with { Hvci = true })
                .Single().Cle.Should().Be("monde:hvci:active:202610172000");
        }
        finally { CultureInfo.CurrentCulture = avant; }
    }

    // La frise refuse l'écart (journal occupé, écriture ratée) : l'état d'avant reste gardé, sinon la mise à jour ne serait
    // plus jamais vue. Elle revient au scan suivant, bornée au même scan d'avant.
    [Fact]
    public void Un_ecart_que_la_frise_refuse_revient_au_scan_suivant()
    {
        var fichier = Path.Combine(Path.GetTempPath(), $"monde-{Guid.NewGuid():N}.json");
        try
        {
            Dictionary<string, string> Jeu(string v) => new() { [@"C:\Jeux\Call of Duty"] = v };
            MondeDeLaMachine.ComparerEtGarder(fichier, T, Jeu("1"), null, null, null).Ecarts.Should().BeEmpty("premier relevé");

            MondeDeLaMachine.ComparerEtGarder(fichier, T.AddDays(1), Jeu("2"), null, null, null, (_, _) => false)
                .Ecarts.Should().BeEmpty("la frise n'a rien pris");

            DateTimeOffset? borne = null;
            MondeDeLaMachine.ComparerEtGarder(fichier, T.AddDays(2), Jeu("2"), null, null, null, (_, d) => { borne = d; return true; })
                .Ecarts.Should().ContainSingle();
            borne.Should().Be(T);
        }
        finally { File.Delete(fichier); }
    }
}
