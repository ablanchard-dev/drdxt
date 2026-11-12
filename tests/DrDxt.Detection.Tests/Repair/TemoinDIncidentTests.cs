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
        var c = TemoinDIncident.Juger([new(T.AddHours(1), "arret brutal", T.AddMinutes(-60), T, lignes), new(T.AddDays(-2), "arret brutal", T, T, [])],
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

    // Un arrêt brutal est noté au démarrage qui le suit, et le PC a pu rester éteint des heures entre les deux : l'heure
    // relue est celle d'avant le dernier signe de vie, pas celle d'avant la note.
    [Fact]
    public void Un_arret_brutal_relit_l_heure_d_avant_le_dernier_signe_de_vie()
    {
        var signe = T.AddHours(-36);
        TemoinDIncident.Fenetre("arret brutal", T, signe).Should().Be((signe.AddMinutes(-60), signe));
        TemoinDIncident.Fenetre("arret brutal", T, T.AddMinutes(5)).Should().Be((T.AddMinutes(-60), T), "un signe d'après la note n'en est pas un");
        TemoinDIncident.Fenetre("pilote graphique relance", T, signe).Should().Be((T.AddMinutes(-10), T), "il est noté sur le vif");
    }

    [Fact]
    public void Le_dernier_signe_de_vie_est_l_evenement_qui_precede_le_demarrage_de_Windows()
    {
        (DateTimeOffset, string?, int)[] journal =
        [
            (T, "Microsoft-Windows-Kernel-Power", 41),
            (T.AddSeconds(-2), "Microsoft-Windows-Kernel-Boot", 29),
            (T.AddSeconds(-2), "Microsoft-Windows-Kernel-General", 12),
            (T.AddHours(-36), "Microsoft-Windows-Kernel-General", 15),
            (T.AddHours(-37), "Service Control Manager", 7000),
        ];
        TemoinDIncident.DernierSigneDeVie(journal, T).Should().Be(T.AddHours(-36));
        TemoinDIncident.DernierSigneDeVie(journal.Take(2), T).Should().BeNull("sans le démarrage, rien ne dit où finit la séance d'avant");
        TemoinDIncident.DernierSigneDeVie(journal.Take(3), T).Should().BeNull();
    }

    // Un incident noté longtemps après le démarrage de Windows est noté sur le vif : la séance d'avant n'y est pour rien.
    [Fact]
    public void Un_demarrage_ancien_ne_donne_aucun_signe_de_vie()
    {
        (DateTimeOffset, string?, int)[] journal =
        [
            (T, "Microsoft-Windows-WHEA-Logger", 18),
            (T.AddMinutes(-11), "Microsoft-Windows-Kernel-General", 12),
            (T.AddHours(-36), "Microsoft-Windows-Kernel-General", 15),
        ];
        TemoinDIncident.DernierSigneDeVie(journal, T).Should().BeNull();
    }

    // Une erreur matérielle fatale arrête la machine : Windows ne la note qu'au démarrage suivant, comme l'arrêt brutal.
    [Fact]
    public void Une_erreur_fatale_notee_au_demarrage_relit_l_heure_d_avant_le_dernier_signe_de_vie()
    {
        var signe = T.AddHours(-5);
        TemoinDIncident.SeNoteApresCoup("erreur materielle fatale").Should().BeTrue();
        TemoinDIncident.SeNoteApresCoup("arret brutal").Should().BeTrue();
        TemoinDIncident.SeNoteApresCoup("pilote graphique relance").Should().BeFalse();
        TemoinDIncident.Fenetre("erreur materielle fatale", T, signe).Should().Be((signe.AddMinutes(-60), signe));
        TemoinDIncident.Fenetre("erreur materielle fatale", T).Should().Be((T.AddMinutes(-10), T), "sans démarrage juste avant, elle est notée sur le vif");
    }

    [Fact]
    public void Le_docteur_date_une_erreur_fatale_notee_apres_coup_de_son_dernier_signe_de_vie()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var signe = T.AddDays(-3);
        var c = TemoinDIncident.Juger([new(T, "erreur materielle fatale", signe.AddMinutes(-60), signe, [])], T.AddDays(1)).Single();
        c.ValeurCourante.Should().Contain(signe.ToLocalTime().ToString("dd/MM", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Le_docteur_date_l_arret_brutal_de_son_dernier_signe_de_vie()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var signe = T.AddHours(-36);
        var c = TemoinDIncident.Juger([new(T, "arret brutal", signe.AddMinutes(-60), signe, [])], T.AddDays(1)).Single();
        var jourDeLArret = signe.ToLocalTime().ToString("dd/MM", CultureInfo.InvariantCulture);
        c.ValeurCourante.Should().Contain(jourDeLArret);
        c.Explication.Should().Contain(jourDeLArret);
    }

    // Un témoin d'arrêt brutal dont la fenêtre finit à sa note a relu l'heure d'avant le redémarrage : ses lignes peuvent
    // venir du redémarrage lui-même, et la phrase ne les dit pas « juste avant l'incident ».
    [Fact]
    public void Un_arret_brutal_relu_avant_son_redemarrage_le_dit()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var lignes = new[] { new TemoinDIncident.Ligne(T.AddSeconds(-2), "System", "Microsoft-Windows-Kernel-Boot", 29, 2) };
        var c = TemoinDIncident.Juger([new(T, "arret brutal", T.AddMinutes(-60), T, lignes)], T.AddDays(1)).Single();
        c.Explication.Should().Contain("redémarrage").And.NotContain("juste avant");
    }

    [Fact]
    [Trait("Category", "Machine")] // relit le vrai journal Système : sans arrêt brutal dans le journal, rien à vérifier
    public void Sur_cette_machine_le_dernier_arret_brutal_a_un_signe_de_vie_avant_sa_note()
    {
        var arrets = new DrDxt.Detection.Hardware.WindowsUnexpectedShutdownLog().Detail(DateTimeOffset.Now.AddDays(-90));
        if (arrets is not { IsOk: true, Value.Count: > 0 }) return;
        var note = arrets.Value[0].Quand;

        var signe = TemoinDIncident.LireDernierSigneDeVie(note);

        signe.Should().NotBeNull().And.BeBefore(note);
        var (debut, fin) = TemoinDIncident.Fenetre("arret brutal", note, signe);
        TemoinDIncident.LireFenetre(debut, fin).Should().OnlyContain(l => l.Quand <= signe);
        Console.WriteLine($"note {note.ToLocalTime():s} signe {signe!.Value.ToLocalTime():s}");
    }
}
