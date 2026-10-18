using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// D'où vient une erreur WHEA : lue dans les champs nommés de l'événement, jamais dans le message ni devinée d'après la banque.
public sealed class OrigineDesErreursMateriellesTests
{
    private static string Xml(params (string Nom, string Valeur)[] champs)
        => "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><EventData>"
           + string.Concat(champs.Select(c => $"<Data Name='{c.Nom}'>{c.Valeur}</Data>")) + "</EventData></Event>";

    private static readonly DateTimeOffset T = new(2026, 10, 15, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Les_champs_d_une_erreur_processeur_sont_lus_par_leur_nom()
    {
        var o = ErreursMaterielles.LireOrigine(Xml(("ErrorSource", "1"), ("ApicId", "12"), ("MCABank", "5"), ("MciStat", "0x9C2040000000011B")))!;
        o.ApicId.Should().Be(12);
        o.Banque.Should().Be(5);
        o.Statut.Should().Be(0x9C2040000000011BUL);
    }

    [Fact]
    public void Les_champs_d_une_erreur_PCIe_sont_lus_par_leur_nom()
    {
        var o = ErreursMaterielles.LireOrigine(Xml(("Bus", "1"), ("Device", "0"), ("Function", "0"), ("VendorID", "0x10DE"),
            ("DeviceID", "0x2882"), ("PortType", "0"), ("CorrectableErrorStatus", "0x1")))!;
        (o.Bus, o.Fabricant, o.Produit, o.StatutCorrigeable).Should().Be((1, 0x10DE, 0x2882, 1u));
    }

    [Fact]
    public void Un_evenement_sans_champ_d_origine_ou_illisible_ne_donne_rien()
    {
        ErreursMaterielles.LireOrigine(Xml(("Length", "200"))).Should().BeNull();
        ErreursMaterielles.LireOrigine("<pas du xml").Should().BeNull();
    }

    // Notre règle : le numéro de banque ne désigne jamais un bloc. Une banque basse n'est pas « un cœur ».
    [Fact]
    public void Une_banque_basse_n_est_jamais_attribuee_a_un_coeur()
    {
        foreach (var langue in new[] { "fr-FR", "en-US" })
        {
            using var _ = Docteur.Portee(CultureInfo.GetCultureInfo(langue));
            var c = ErreursMaterielles.Attribuer([new(T, 2, true, new(3, 1, 0x9C2040000000011BUL, null, null, null, null, null, null, null))]).Single();
            c.Explication.Should().Contain("APIC").And.Contain("MCA_IPID");
            c.Explication.Should().NotMatchRegex(@"(?i)\bc(œ|oe)ur\b|\bcore\b");
        }
    }

    [Fact]
    public void Une_erreur_PCIe_nomme_son_peripherique_et_ses_causes_et_un_avis_seul_est_un_avertissement()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var lien = new ErreursMaterielles.Origine(null, null, null, 1, 0, 0, 0x10DE, 0x2882, 0, 0x2000);
        var c = ErreursMaterielles.Attribuer([new(T, 2, false, lien), new(T.AddHours(1), 2, false, lien)]).Single();
        c.Explication.Should().Contain("2 sur le lien PCIe du périphérique 01:00.0 (fabricant 10DE, produit 2882)")
            .And.Contain("Advisory Non-Fatal Error").And.Contain("pas une panne du lien");
    }

    [Fact]
    public void Les_informations_et_les_erreurs_sans_origine_ne_disent_rien()
        => ErreursMaterielles.Attribuer([new(T, 3, true, new(1, 1, null, null, null, null, null, null, null, null)), new(T, 2)])
            .Should().BeEmpty();
}
