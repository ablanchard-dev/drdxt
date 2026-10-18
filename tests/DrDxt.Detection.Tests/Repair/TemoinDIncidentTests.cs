using System.Globalization;
using System.Text.Json;
using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Le témoin d'incident : la fenêtre relue, la cadence, et ce que le docteur en dit.
public sealed class TemoinDIncidentTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 15, 22, 9, 0, TimeSpan.Zero);

    [Fact]
    public void Un_arret_brutal_relit_l_heure_d_avant_sa_note_et_le_reste_dix_minutes()
    {
        TemoinDIncident.Fenetre("arret brutal", T).Should().Be((T.AddMinutes(-60), T));
        TemoinDIncident.Fenetre("pilote graphique relance", T).Should().Be((T.AddMinutes(-10), T));
    }

    [Fact]
    public void Au_plus_un_temoin_toutes_les_deux_minutes()
    {
        TemoinDIncident.AEcrire(T, null).Should().BeTrue();
        TemoinDIncident.AEcrire(T.AddSeconds(90), T).Should().BeFalse();
        TemoinDIncident.AEcrire(T.AddMinutes(2), T).Should().BeTrue();
    }

    [Fact]
    public void Chaque_declencheur_est_nomme_par_son_fournisseur_et_son_numero()
    {
        TemoinDIncident.Genre("Microsoft-Windows-Kernel-Power", 41).Should().Be("arret brutal");
        TemoinDIncident.Genre("Microsoft-Windows-Kernel-Power", 42).Should().BeNull();
        TemoinDIncident.Requete().Should().Contain("Provider[@Name='Display'] and EventID=4101");
    }

    [Fact]
    public void Le_docteur_dit_le_dernier_temoin_avec_ses_sources_sans_designer_de_cause()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var lignes = new[] { new TemoinDIncident.Ligne(T.AddMinutes(-3), "System", "nvlddmkm", 153, 2),
            new TemoinDIncident.Ligne(T.AddMinutes(-2), "System", "nvlddmkm", 153, 2), new TemoinDIncident.Ligne(T.AddMinutes(-1), "Application", "X", 1, 3) };
        var c = TemoinDIncident.Juger([new(T, "arret brutal", T.AddMinutes(-60), T, lignes), new(T.AddDays(-2), "arret brutal", T, T, [])],
            T.AddDays(1)).Single();
        c.Statut.Should().Be(RepairStatus.Observed);
        c.Explication.Should().Contain("arrêt brutal").And.Contain("nvlddmkm 153 (2x)").And.Contain("pas un verdict")
            .And.Contain("1 autre(s) témoin(s)");
    }

    [Fact]
    public void Un_temoin_de_plus_de_trente_jours_ou_un_dossier_non_lu_ne_dit_rien()
    {
        TemoinDIncident.Juger([new(T, "arret brutal", T, T, [])], T.AddDays(31)).Should().BeEmpty();
        TemoinDIncident.Juger(null, T).Should().BeEmpty();
    }

    [Fact]
    public void Les_temoins_ecrits_se_relisent_du_plus_recent_au_plus_ancien()
    {
        var dossier = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dossier, "temoin-1.json"), JsonSerializer.Serialize(new TemoinDIncident.Temoin(T, "arret brutal", T, T, [])));
        File.WriteAllText(Path.Combine(dossier, "temoin-2.json"), JsonSerializer.Serialize(new TemoinDIncident.Temoin(T.AddDays(1), "arret brutal", T, T, [])));
        File.WriteAllText(Path.Combine(dossier, "temoin-3.json"), "abîmé");
        TemoinDIncident.LireTous(dossier)!.Select(t => t.Incident).Should().Equal(T.AddDays(1), T);
        TemoinDIncident.LireTous(Path.Combine(dossier, "absent")).Should().BeEmpty();
    }
}
