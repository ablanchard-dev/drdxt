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
        TemoinDIncident.LireTous(dossier, _ => true)!.Select(t => t.Incident).Should().Equal(T.AddDays(1), T);
        TemoinDIncident.LireTous(Path.Combine(dossier, "absent"), _ => true).Should().BeEmpty();
    }

    // %ProgramData% laisse tout compte y poser des fichiers avant l'installation, et la réparation du dossier ne descend pas.
    // Un témoin hors confiance n'est pas dit par le docteur, n'occupe aucune place gardée (des noms plus récents posés d'avance
    // feraient supprimer chaque vrai témoin aussitôt écrit) et n'est jamais supprimé par le service.
    [Fact]
    public void Un_temoin_hors_confiance_n_est_ni_lu_ni_garde_ni_supprime()
    {
        var dossier = Directory.CreateTempSubdirectory().FullName;
        string Poser(string nom, DateTimeOffset incident)
        {
            var f = Path.Combine(dossier, nom);
            File.WriteAllText(f, JsonSerializer.Serialize(new TemoinDIncident.Temoin(incident, "arret brutal", incident, incident, [])));
            return f;
        }
        var vrais = Enumerable.Range(0, TemoinDIncident.Gardes + 2).Select(i => Poser($"temoin-2026{i:D4}.json", T.AddMinutes(i))).ToList();
        var poses = Enumerable.Range(0, 3).Select(i => Poser($"temoin-9999{i:D4}.json", T.AddYears(5))).ToHashSet();
        bool Fiable(string f) => !poses.Contains(f);

        TemoinDIncident.LireTous(dossier, Fiable)!.Should().HaveCount(TemoinDIncident.Gardes + 2)
            .And.OnlyContain(t => t.Incident < T.AddYears(1));
        TemoinDIncident.EnTrop(dossier, Fiable).Should().BeEquivalentTo(vrais.Take(2));
    }
}
