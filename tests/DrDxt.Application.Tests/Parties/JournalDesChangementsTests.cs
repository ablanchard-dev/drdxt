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
            await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() =>
                (i % 2 == 0 ? a : b).Ajouter(new Changement(t.AddHours(i), $"changement {i}")))));
            a.Lire().Should().HaveCount(30, "aucun ajout n'est écrasé par l'autre journal");

            var pire = new BilanDuChangement(VerdictEnJeu.Pire, "cod.exe", 3, 3, 95, 81, 140, 140);
            await Task.WhenAll(a.Lire().Select((c, i) => Task.Run(() => (i % 2 == 0 ? a : b).Figer(c, pire))));
            b.Lire().Should().OnlyContain(c => c.Fige != null, "aucun bilan figé n'est perdu");
        }
        finally { File.Delete(chemin); }
    }
}
