using DrDxt.Application.Parties;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// Un réglage basculé entre deux scans devient un changement borné par les deux relevés.
public sealed class SuiviDesReglagesTests : IDisposable
{
    private readonly string _chemin = Path.Combine(Path.GetTempPath(), $"suivi-{Guid.NewGuid():N}.json");
    public void Dispose()
    {
        try { File.Delete(_chemin); } catch { }
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_chemin)!, Path.GetFileName(_chemin) + ".mis-de-cote-*"))
            try { File.Delete(f); } catch { }
    }

    private static string Libelle(string nom, string avant, string apres) => $"{nom}: {avant}>{apres}";

    [Fact]
    public void Une_bascule_entre_deux_releves_devient_un_changement_borne()
    {
        var s = new SuiviDesReglages(_chemin);
        var t1 = new DateTimeOffset(2026, 10, 4, 6, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddHours(5);

        s.Relever(new Dictionary<string, string> { ["IR"] = "allumé" }, t1, Libelle).Should().BeEmpty("premier relevé : aucun avant");
        s.Relever(new Dictionary<string, string> { ["IR"] = "allumé" }, t1.AddHours(1), Libelle).Should().BeEmpty("rien n'a bougé");

        var c = s.Relever(new Dictionary<string, string> { ["IR"] = "éteint" }, t2, Libelle).Should().ContainSingle().Subject;
        c.Libelle.Should().Be("IR: allumé>éteint");
        c.Quand.Should().Be(t2);
        c.Depuis.Should().Be(t1.AddHours(1), "la bascule a eu lieu entre les deux derniers relevés");
    }

    // Un relevé « {} » se lit sans erreur mais sans états : pris pour un historique, il faisait lever le relevé à chaque scan.
    [Fact]
    public void Un_releve_sans_etats_est_mis_de_cote_et_le_suivi_repart()
    {
        File.WriteAllText(_chemin, "{}");
        var s = new SuiviDesReglages(_chemin);

        s.Relever(new Dictionary<string, string> { ["IR"] = "allumé" }, DateTimeOffset.Now, Libelle).Should().BeEmpty();
        s.Relever(new Dictionary<string, string> { ["IR"] = "éteint" }, DateTimeOffset.Now, Libelle).Should().ContainSingle();
    }

    [Fact]
    public void Un_reglage_non_relu_n_est_pas_un_changement_et_garde_son_etat()
    {
        var s = new SuiviDesReglages(_chemin);
        var t = DateTimeOffset.Now;
        s.Relever(new Dictionary<string, string> { ["IR"] = "allumé", ["HAGS"] = "1" }, t, Libelle);
        s.Relever(new Dictionary<string, string> { ["HAGS"] = "1" }, t.AddMinutes(1), Libelle).Should().BeEmpty();
        var c = s.Relever(new Dictionary<string, string> { ["IR"] = "éteint" }, t.AddMinutes(2), Libelle)
            .Should().ContainSingle(c => c.Libelle == "IR: allumé>éteint", "l'état d'avant a survécu au relevé qui ne l'a pas lu").Subject;
        c.Depuis.Should().Be(t, "borné par le dernier relevé qui a LU ce réglage, pas par le dernier relevé tout court");
    }
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
    private static Dictionary<string, string> Tout(string v) => new() { ["windows.hags"] = v, ["windows.game_mode"] = v, ["memoire.fichier_echange"] = v };

    // Le test instable d'HistoireDUnJoueurTests, la cause figée : le relevé d'après Optimiser (le mode Jeu seul) tombait sur
    // un fichier tenu un instant par une écriture. Lu « vide », il récrivait le fichier avec le seul mode Jeu : HAGS et le
    // fichier d'échange perdaient leur avant, et leurs bascules ne se relevaient jamais.
    [Fact]
    public void Une_lecture_ratee_n_efface_pas_l_historique()
    {
        var s = new SuiviDesReglages(_chemin);
        s.Relever(Tout("off"), T0, Libelle);

        // Le fichier, tenu un instant par une autre écriture au moment de ce relevé : sa lecture échoue.
        new SuiviDesReglages(_chemin, () => DrDxt.Core.Result<string?>.Fail(SuiviDesReglages.CodeErreur, "partage refusé (test)"))
            .Relever(new Dictionary<string, string> { ["windows.game_mode"] = "on" }, T0.AddDays(3), Libelle).Should().BeEmpty();

        s.Relever(Tout("on"), T0.AddDays(6), Libelle).Select(c => c.Cle)
            .Should().BeEquivalentTo(["windows.hags:on", "windows.game_mode:on", "memoire.fichier_echange:on"]);
    }

    [Fact]
    public void Un_fichier_absent_est_un_premier_releve_qui_s_ecrit()
    {
        new SuiviDesReglages(_chemin).Relever(Tout("off"), T0, Libelle).Should().BeEmpty("sans avant, aucune bascule inventée");
        File.Exists(_chemin).Should().BeTrue();
    }

    // Illisible pour de bon : mis de côté (la trace reste), et le suivi repart au lieu d'être bloqué pour toujours.
    [Fact]
    public void Un_fichier_illisible_est_mis_de_cote_et_le_suivi_repart()
    {
        File.WriteAllText(_chemin, "{ pas du json");
        var s = new SuiviDesReglages(_chemin);

        s.Relever(Tout("off"), T0, Libelle).Should().BeEmpty();

        Directory.GetFiles(Path.GetDirectoryName(_chemin)!, Path.GetFileName(_chemin) + ".mis-de-cote-*").Should().ContainSingle()
            .Which.Should().Match(f => File.ReadAllText(f) == "{ pas du json");
        s.Relever(Tout("on"), T0.AddDays(1), Libelle).Should().HaveCount(3, "le suivi a repris depuis le relevé neuf");
    }

    // Deux relevés du même fichier, en même temps, chacun par son instance (comme deux processus) : aucun ne perd ce que
    // l'autre a écrit.
    [Fact]
    public void Des_releves_concurrents_ne_perdent_aucun_reglage()
    {
        new SuiviDesReglages(_chemin).Relever(Tout("off"), T0, Libelle);

        // Une attente large : le verrou n'est pas équitable et chaque écriture peut être ralentie (antivirus) ; ici, on prouve
        // qu'aucun réglage ne se perd, pas la borne de l'attente (Verrou_tenu_par_un_autre_processus_rien_n_est_ecrit).
        Parallel.For(0, 20, i => new SuiviDesReglages(_chemin, attenteMax: TimeSpan.FromSeconds(30)).Relever(
            new Dictionary<string, string> { [$"essai.{i}"] = "on" }, T0.AddMinutes(i), Libelle));

        var changes = new SuiviDesReglages(_chemin).Relever(
            Enumerable.Range(0, 20).ToDictionary(i => $"essai.{i}", _ => "off").Concat(Tout("on")).ToDictionary(), T0.AddDays(1), Libelle);
        changes.Should().HaveCount(23, "chaque réglage a gardé son avant");
    }
    // L'App et le relevé de fond (deux processus) : l'un tient le verrou plus longtemps que l'attente bornée. Le relevé qui
    // n'a pas pu le prendre n'écrit rien, comme une lecture ratée, et l'historique reste.
    [Fact]
    public void Verrou_tenu_par_un_autre_processus_rien_n_est_ecrit()
    {
        var s = new SuiviDesReglages(_chemin);
        s.Relever(Tout("off"), T0, Libelle);

        using (VerrouDuReleve.Prendre(_chemin + ".verrou"))
            new SuiviDesReglages(_chemin, attenteMax: TimeSpan.FromMilliseconds(200)).Relever(new Dictionary<string, string> { ["windows.game_mode"] = "on" }, T0.AddDays(3), Libelle)
                .Should().BeEmpty();

        s.Relever(Tout("on"), T0.AddDays(6), Libelle).Should().HaveCount(3, "l'historique n'a pas été touché");
        File.Exists(_chemin + ".verrou").Should().BeFalse("le verrou disparaît avec son détenteur");
    }

    // Le journal refuse les changements (occupé, écriture ratée) : la référence n'avance pas, sinon la bascule serait perdue
    // pour toujours (le relevé suivant ne verrait plus de différence). Elle revient au relevé suivant, bornée pareil.
    [Fact]
    public void Un_journal_qui_refuse_les_changements_ne_perd_pas_la_bascule()
    {
        var s = new SuiviDesReglages(_chemin);
        s.Relever(Tout("off"), T0, Libelle);

        s.Relever(Tout("on"), T0.AddDays(1), Libelle, _ => false).Should().BeEmpty("le journal n'a rien pris");

        s.Relever(Tout("on"), T0.AddDays(2), Libelle, _ => true).Should().HaveCount(3)
            .And.OnlyContain(c => c.Depuis == T0, "bornée au dernier relevé que le journal a pris");
    }
}
