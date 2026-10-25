using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// Chaque changement daté se juge sur les parties d'avant et d'après.
public sealed class JournalDesChangementsTests : IDisposable
{
    private readonly string _chemin = Path.Combine(Path.GetTempPath(), $"changements-{Guid.NewGuid():N}.json");
    public void Dispose() { try { File.Delete(_chemin); } catch { } }

    [Fact]
    public void Un_changement_est_garde_date_et_un_scan_relance_ne_le_double_pas()
    {
        var j = new JournalDesChangements(_chemin);
        j.Lire().Should().BeEmpty("témoin : fichier absent, aucun changement inventé");
        var quand = new DateTimeOffset(2026, 10, 4, 4, 30, 0, TimeSpan.Zero);

        j.Ajouter(new Changement(quand, "BIOS")).IsOk.Should().BeTrue();
        j.Ajouter(new Changement(quand.AddSeconds(20), "BIOS"));
        j.Ajouter(new Changement(quand.AddHours(1), "Levier"));

        j.Lire().Select(c => c.Libelle).Should().Equal("Levier", "BIOS");
    }

    // Un JSON valide aux entrées vides : elles sont écartées, et l'ajout suivant ne tombe pas dessus.
    [Fact]
    public void Les_entrees_vides_d_un_journal_valide_sont_ecartees()
    {
        File.WriteAllText(_chemin, "[null, {}, {\"Quand\":\"2026-10-04T04:30:00+00:00\",\"Libelle\":\"BIOS\"}]");
        var j = new JournalDesChangements(_chemin);
        j.Lire().Select(c => c.Libelle).Should().Equal("BIOS");
        j.Ajouter(new Changement(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), "Levier")).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Un_fichier_abime_se_lit_vide()
    {
        File.WriteAllText(_chemin, "{ pas du json");
        new JournalDesChangements(_chemin).Lire().Should().BeEmpty();
    }

    private static PartieRelevee P(DateTimeOffset debut, double fps, double low1, string jeu = "cod.exe")
        => new(jeu, debut, debut.AddMinutes(20), 6, fps, low1, BottleneckState.CpuBoundOrAppBound);

    [Fact]
    public void Le_bilan_dit_les_medianes_de_chaque_cote_et_le_verdict()
    {
        var reglage = new DateTimeOffset(2026, 10, 4, 4, 30, 0, TimeSpan.Zero);
        var parties = new[]
        {
            P(reglage.AddDays(-2), 130, 90), P(reglage.AddDays(-1), 128, 88), P(reglage.AddHours(-3), 132, 92),
            P(reglage.AddHours(2), 150, 105), P(reglage.AddHours(5), 148, 104), P(reglage.AddDays(1), 152, 107),
            P(reglage.AddHours(3), 300, 250, "autre.exe"),
        };

        var b = ComparaisonDesParties.Bilan(parties, reglage);

        b.Jeu.Should().Be("cod.exe");
        b.Verdict.Should().Be(VerdictEnJeu.Mieux);
        (b.Avant, b.Apres).Should().Be((3, 3));
        b.Low1Avant.Should().Be(90);
        b.Low1Apres.Should().Be(105);
        b.FpsApres.Should().Be(150);
    }

    // Les parties entre la dernière mesure et le démarrage ne comptent d'aucun côté.
    [Fact]
    public void Les_parties_de_la_fenetre_incertaine_ne_comptent_d_aucun_cote()
    {
        var demarrage = new DateTimeOffset(2026, 10, 4, 4, 30, 0, TimeSpan.Zero);
        var mesure = demarrage.AddDays(-3);
        var parties = new[]
        {
            P(mesure.AddDays(-2), 130, 90), P(mesure.AddDays(-1), 128, 88), P(mesure.AddHours(-3), 132, 92),
            P(mesure.AddDays(1), 150, 106), P(mesure.AddDays(2), 151, 107),
            P(demarrage.AddHours(2), 150, 105), P(demarrage.AddHours(5), 148, 104), P(demarrage.AddDays(1), 152, 107),
        };

        var b = ComparaisonDesParties.Bilan(parties, demarrage, mesure);

        (b.Avant, b.Apres).Should().Be((3, 3));
        b.Verdict.Should().Be(VerdictEnJeu.Mieux);
        ComparaisonDesParties.Bilan(parties, demarrage).Avant.Should().Be(5, "témoin : sans borne, elles passaient dans « avant »");
    }

    [Fact]
    public void Le_jeu_juge_est_celui_joue_des_deux_cotes()
    {
        var quand = new DateTimeOffset(2026, 10, 4, 4, 30, 0, TimeSpan.Zero);
        var parties = new[]
        {
            P(quand.AddDays(-3), 130, 90), P(quand.AddDays(-2), 130, 90), P(quand.AddDays(-1), 130, 90),
            P(quand.AddHours(1), 150, 105), P(quand.AddHours(2), 150, 105), P(quand.AddHours(3), 150, 105),
            P(quand.AddHours(4), 90, 60, "bf6.exe"), P(quand.AddHours(5), 90, 60, "bf6.exe"),
            P(quand.AddHours(6), 90, 60, "bf6.exe"), P(quand.AddHours(7), 90, 60, "bf6.exe"),
        };

        var b = ComparaisonDesParties.Bilan(parties, quand);

        b.Jeu.Should().Be("cod.exe", "bf6 n'a aucune partie d'avant ; cod en a des deux côtés");
        b.Verdict.Should().Be(VerdictEnJeu.Mieux);
    }

    [Fact]
    public void Sans_assez_de_parties_apres_le_bilan_dit_combien_il_en_manque()
    {
        var reglage = DateTimeOffset.Now;
        var b = ComparaisonDesParties.Bilan([P(reglage.AddDays(-1), 130, 90), P(reglage.AddHours(1), 150, 105)], reglage);
        b.Verdict.Should().Be(VerdictEnJeu.PasEncoreJuge);
        (b.Avant, b.Apres).Should().Be((1, 1));
        ComparaisonDesParties.Bilan([], reglage).Jeu.Should().BeNull();
    }

    // Deux écrivains en même temps (le relevé de fond et l'App) ne perdent plus l'écriture de l'autre.
    [Fact]
    public async Task Des_ecritures_simultanees_de_deux_journaux_sur_le_meme_fichier_ne_se_perdent_pas()
    {
        var chemin = Path.Combine(Path.GetTempPath(), $"journal-verrou-{Guid.NewGuid():N}.json");
        try
        {
            var t = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
            var a = new JournalDesChangements(chemin);
            var b = new JournalDesChangements(chemin);
            // Sur un runner lent, un écrivain peut attendre le verrou plus de 10 s et rendre « occupé » : ce n'est pas une
            // perte. La perte, c'est un ajout qui a réussi et qui n'est plus dans le fichier. Seul ce refus est toléré :
            // il porte le même code qu'une écriture refusée, son message l'en distingue.
            var occupe = Docteur.T("App.JournalChangementsOccupe");
            var ajouts = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() =>
                (i % 2 == 0 ? a : b).Ajouter(new Changement(t.AddHours(i), $"changement {i}")))));
            ajouts.Should().Contain(r => r.IsOk);
            ajouts.Should().NotContain(r => !r.IsOk && r.Error!.Value.Message != occupe, "seul le verrou attendu trop longtemps");
            a.Lire().Should().HaveCount(30, "aucun ajout n'est perdu : refusé, il attend le passage suivant, et se lit déjà");

            var pire = new BilanDuChangement(VerdictEnJeu.Pire, "cod.exe", 3, 3, 95, 81, 140, 140);
            var lus = a.Lire();
            var figes = await Task.WhenAll(lus.Select((c, i) => Task.Run(() => (i % 2 == 0 ? a : b).Figer(c, pire))));
            figes.Should().Contain(r => r.IsOk);
            figes.Should().NotContain(r => !r.IsOk && r.Error!.Value.Message != occupe, "seul le verrou attendu trop longtemps");
            b.Lire().Should().HaveCount(lus.Count, "figer ne retire aucune entrée");
            var reussis = lus.Where((c, i) => figes[i].IsOk).Select(c => c.Libelle).ToHashSet();
            b.Lire().Where(c => reussis.Contains(c.Libelle)).Should().OnlyContain(c => c.Fige != null, "aucun bilan figé n'est perdu");
        }
        finally { File.Delete(chemin); }
    }

    [Fact]
    public void Un_journal_abime_est_mis_de_cote_avant_le_prochain_ajout()
    {
        var chemin = Path.Combine(Path.GetTempPath(), $"journal-abime-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(chemin, "{ tronqu");
            var journal = new JournalDesChangements(chemin);

            journal.Ajouter(new Changement(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), "un changement")).IsOk.Should().BeTrue();

            Directory.GetFiles(Path.GetDirectoryName(chemin)!, Path.GetFileName(chemin) + ".mis-de-cote-*").Should().ContainSingle()
                .Which.Should().Match(f => File.ReadAllText(f) == "{ tronqu");
            journal.Lire().Should().ContainSingle();
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(chemin)!, Path.GetFileName(chemin) + "*")) File.Delete(f);
        }
    }

    // Un JSON valide de la mauvaise forme (une liste de nombres) ne se lit pas en changements : mis de côté, pas écrasé.
    [Fact]
    public void Une_liste_de_la_mauvaise_forme_est_mise_de_cote_avant_l_ajout()
    {
        File.WriteAllText(_chemin, "[1,2]");
        var j = new JournalDesChangements(_chemin);

        j.Ajouter(new Changement(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), "Levier")).IsOk.Should().BeTrue();

        j.Lire().Select(c => c.Libelle).Should().Equal("Levier");
        Directory.GetFiles(Path.GetDirectoryName(_chemin)!, Path.GetFileName(_chemin) + ".mis-de-cote-*")
            .Should().ContainSingle().Which.Should().Match(f => File.ReadAllText(f) == "[1,2]");
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_chemin)!, Path.GetFileName(_chemin) + ".mis-de-cote-*")) File.Delete(f);
    }

    // La référence d'un relevé n'a pas pu s'écrire après que le journal a pris ses changements : le relevé suivant les rend
    // de nouveau, à une autre heure, mais avec la même clé et la même borne basse. C'est le même changement, pas un second.
    [Fact]
    public void Un_changement_rendu_de_nouveau_par_le_releve_suivant_n_est_pas_double()
    {
        var j = new JournalDesChangements(_chemin);
        var depuis = new DateTimeOffset(2026, 10, 4, 4, 30, 0, TimeSpan.Zero);
        j.Ajouter(new Changement(depuis.AddHours(1), "HAGS", depuis, "windows.hags:on")).IsOk.Should().BeTrue();

        j.Ajouter(new Changement(depuis.AddHours(5), "HAGS", depuis, "windows.hags:on")).IsOk.Should().BeTrue();
        j.Ajouter(new Changement(depuis.AddHours(6), "HAGS", depuis.AddHours(5), "windows.hags:on")).IsOk.Should().BeTrue();

        j.Lire().Select(c => c.Quand).Should().Equal(new[] { depuis.AddHours(6), depuis.AddHours(1) },
            "le même relevé rendu deux fois ne compte qu'une ; une borne neuve est une bascule neuve");
    }

    // L'horloge a reculé (pile du BIOS, fuseau, réglage à la main) et le journal est plein : le changement qui vient d'arriver
    // est daté avant tous les autres. Il reste ; c'est la plus vieille des entrées d'avant qui part.
    [Fact]
    public void Journal_plein_et_horloge_reculee_le_dernier_changement_reste()
    {
        var j = new JournalDesChangements(_chemin);
        var t = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < JournalDesChangements.Gardes; i++) j.Ajouter(new Changement(t.AddHours(i), $"L{i}")).IsOk.Should().BeTrue();

        j.Ajouter(new Changement(t.AddDays(-1), "Arrivé après le recul")).IsOk.Should().BeTrue();

        var lus = j.Lire().Select(c => c.Libelle).ToList();
        lus.Should().HaveCount(JournalDesChangements.Gardes).And.Contain("Arrivé après le recul").And.NotContain("L0");
    }

    // L'App lancée élevée créait le mutex nommé du journal ; son descripteur par défaut ne s'ouvre pas depuis le relevé non élevé
    // du même compte, qui levait UnauthorizedAccessException à chaque ajout. Simulé : un mutex de ce nom que ce compte ne peut
    // pas ouvrir. Le verrou entre processus est un fichier du dossier du compte, que les deux ouvrent.
    [Fact]
    public void Un_mutex_d_un_processus_eleve_n_empeche_pas_d_ajouter()
    {
        var journal = new JournalDesChangements(_chemin, TimeSpan.FromMilliseconds(50));
        using var mutex = new Mutex(false, AncienNomDuVerrou(_chemin));
        var refus = new System.Security.AccessControl.MutexSecurity();
        refus.AddAccessRule(new System.Security.AccessControl.MutexAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.MutexRights.FullControl, System.Security.AccessControl.AccessControlType.Deny));
        mutex.SetAccessControl(refus);

        journal.Ajouter(new Changement(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), "BIOS")).IsOk.Should().BeTrue();
        new JournalDesChangements(_chemin).Lire().Select(c => c.Libelle).Should().Equal("BIOS");
    }

    private static string AncienNomDuVerrou(string chemin) => @"Local\DrDXT-journal-" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(chemin).ToUpperInvariant())))[..32];

    // Le verrou tenu plus longtemps que l'attente (un autre processus qui écrit lentement) : l'ajout était refusé, et ses
    // appelants (la pose, le rendu de la garde) ne reprenaient jamais. Une pose perdue coupait le retrait automatique et la
    // garde d'incident de ce levier. Refusé, l'ajout attend : il se lit tout de suite, et l'ajout suivant l'écrit avec lui.
    [Fact]
    public void Un_ajout_refuse_par_le_verrou_n_est_pas_perdu()
    {
        var t = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var journal = new JournalDesChangements(_chemin, TimeSpan.FromMilliseconds(50));
        using (var tenu = new ManualResetEventSlim())
        using (var lache = new ManualResetEventSlim())
        {
            var autre = new Thread(() =>
            {
                using var verrou = VerrouDuReleve.Prendre(journal.CheminDuVerrou);
                tenu.Set();
                lache.Wait();
            });
            autre.Start();
            tenu.Wait();

            journal.Ajouter(new Changement(t, "pose:hags", null, "pose:latency.hagsOn")).IsOk.Should().BeFalse("témoin : le verrou est tenu");
            journal.Lire().Select(c => c.Libelle).Should().Contain("pose:hags", "la garde et le retrait le voient déjà");

            lache.Set();
            autre.Join();
        }

        journal.Ajouter(new Changement(t.AddHours(1), "BIOS")).IsOk.Should().BeTrue();
        new JournalDesChangements(_chemin).Lire().Select(c => c.Libelle).Should().Equal("BIOS", "pose:hags");
    }

    // Les mises à jour de jeux et de pilotes remplissent l'anneau de 50 en deux ou trois mois. La date de pose d'un levier en
    // sortait, et avec elle le retrait automatique et le contrôle des leviers volatils de ce levier. La dernière pose de chaque
    // levier reste, quel que soit son âge.
    [Fact]
    public void La_derniere_pose_d_un_levier_ne_sort_pas_de_l_anneau()
    {
        var t = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var j = new JournalDesChangements(_chemin);
        j.Ajouter(new Changement(t, "latency.hagsOn", null, "pose:latency.hagsOn"));
        for (var i = 1; i <= JournalDesChangements.Gardes + 10; i++)
            j.Ajouter(new Changement(t.AddDays(i), $"mise à jour {i}", null, $"jeu:{i}"));

        j.Lire().Should().Contain(c => c.Cle == "pose:latency.hagsOn");
        j.Lire().Should().HaveCount(JournalDesChangements.Gardes + 1, "les 50 plus récentes, plus la dernière pose du levier");
    }

    // Un verdict figé se relit des mois plus tard. Écrit en entier, une valeur ajoutée avant « Pire » dans l'énumération
    // changerait un « Mieux » figé, et le retrait automatique retirerait un levier prouvé bon. Il s'écrit par son nom ; les
    // journaux écrits en entiers se relisent encore.
    [Fact]
    public void Un_verdict_fige_s_ecrit_par_son_nom_et_l_ancien_entier_se_relit()
    {
        var t = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var j = new JournalDesChangements(_chemin);
        var c = new Changement(t, "HAGS");
        j.Ajouter(c);
        j.Figer(c, new BilanDuChangement(VerdictEnJeu.Pire, "cod.exe", 3, 3, 95, 81, 140, 140)).IsOk.Should().BeTrue();

        File.ReadAllText(_chemin).Should().Contain("\"Verdict\":\"Pire\"");

        File.WriteAllText(_chemin, File.ReadAllText(_chemin).Replace("\"Verdict\":\"Pire\"", "\"Verdict\":3"));
        new JournalDesChangements(_chemin).Lire().Single().Fige!.Verdict.Should().Be(VerdictEnJeu.Pire);
    }
}
