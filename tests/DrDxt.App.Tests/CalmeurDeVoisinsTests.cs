using System.Diagnostics;
using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 03/10/2026 (boucle 2D2P, P3.2) : calmer un voisin gourmand pendant la partie, et lui rendre sa priorité ensuite.
// Le système est simulé : rien n'est posé sur la machine qui lance les tests.
public sealed class CalmeurDeVoisinsTests
{
    private sealed class Systeme : IPrioritesDeProcessus
    {
        public Dictionary<int, ProcessPriorityClass> Priorites { get; } = new();
        public ProcessPriorityClass? Lire(int pid) => Priorites.TryGetValue(pid, out var p) ? p : null;
        public bool Poser(int pid, ProcessPriorityClass p)
        {
            if (!Priorites.ContainsKey(pid)) return false;
            Priorites[pid] = p;
            return true;
        }
    }

    private const int Jeu = 100, PremierPlan = 200, Obs = 300;

    private static (CalmeurDeVoisins c, Systeme s) Nouveau()
    {
        var s = new Systeme();
        foreach (var pid in new[] { Jeu, PremierPlan, Obs, 400, 500 }) s.Priorites[pid] = ProcessPriorityClass.Normal;
        return (new CalmeurDeVoisins(s), s);
    }

    private static VoisinDuJeu V(string nom, double pourcent, int pid) => new(nom, pourcent, null, pid);

    [Fact]
    public void Un_voisin_gourmand_sur_deux_lectures_passe_apres_le_jeu_puis_retrouve_sa_priorite()
    {
        var (c, s) = Nouveau();
        c.Lecture([V("obs64", 60, Obs)], Jeu, PremierPlan).Should().BeEmpty("une seule lecture ne suffit pas");
        c.Lecture([V("obs64", 55, Obs)], Jeu, PremierPlan).Should().Equal("obs64");
        s.Priorites[Obs].Should().Be(ProcessPriorityClass.BelowNormal);

        c.Rendre();
        s.Priorites[Obs].Should().Be(ProcessPriorityClass.Normal);
    }

    [Fact]
    public void Ni_le_jeu_ni_le_premier_plan_ni_le_systeme_ne_sont_touches()
    {
        var (c, s) = Nouveau();
        VoisinDuJeu[] vus = [V("cod", 900, Jeu), V("chrome", 80, PremierPlan), V("svchost", 80, 400), V("MsMpEng", 90, 500)];
        c.Lecture(vus, Jeu, PremierPlan);
        c.Lecture(vus, Jeu, PremierPlan).Should().BeEmpty();
        s.Priorites.Values.Should().OnlyContain(p => p == ProcessPriorityClass.Normal);
    }

    [Fact]
    public void Sous_le_seuil_rien_ne_bouge()
    {
        var (c, s) = Nouveau();
        c.Lecture([V("discord", 20, Obs)], Jeu, PremierPlan);
        c.Lecture([V("discord", 24.9, Obs)], Jeu, PremierPlan).Should().BeEmpty();
        s.Priorites[Obs].Should().Be(ProcessPriorityClass.Normal);
    }

    [Fact]
    public void Une_priorite_deja_changee_par_quelqu_un_d_autre_n_est_ni_touchee_ni_rendue()
    {
        var (c, s) = Nouveau();
        s.Priorites[Obs] = ProcessPriorityClass.High;
        c.Lecture([V("obs64", 60, Obs)], Jeu, PremierPlan);
        c.Lecture([V("obs64", 60, Obs)], Jeu, PremierPlan).Should().BeEmpty();
        c.Rendre();
        s.Priorites[Obs].Should().Be(ProcessPriorityClass.High);
    }

    [Fact]
    public void Un_voisin_remonte_par_le_joueur_pendant_la_partie_n_est_pas_redescendu_a_la_fin()
    {
        var (c, s) = Nouveau();
        c.Lecture([V("obs64", 60, Obs)], Jeu, PremierPlan);
        c.Lecture([V("obs64", 60, Obs)], Jeu, PremierPlan);
        s.Priorites[Obs] = ProcessPriorityClass.AboveNormal;
        c.Rendre();
        s.Priorites[Obs].Should().Be(ProcessPriorityClass.AboveNormal, "le choix du joueur passe avant celui de DrDXT");
    }

    // ── 07/10/2026 (L-VOISINS) : proposé d'après les parties, jamais sur un protégé ni sur ce que le joueur exclut ──────
    private static DrDxt.Application.Parties.PartieRelevee Partie(params DrDxt.Application.Parties.VoisinEnPartie[]? voisins)
        => new("cod.exe", DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now, 6, 120, 90, DrDxt.Core.BottleneckState.CpuBoundOrAppBound,
            Voisins: voisins);

    [Fact]
    public void Sans_voisins_lus_rien_n_est_propose_et_c_est_non_lu()
    {
        CalmeurDeVoisins.AProposer([], []).Should().BeNull();
        CalmeurDeVoisins.AProposer([Partie(null), Partie(null)], []).Should().BeNull("des parties sans relevé des voisins ne disent rien");
        CalmeurDeVoisins.AProposer([Partie()], []).Should().BeEmpty("lus, aucun au-dessus du seuil");
    }

    [Fact]
    public void Le_voisin_gourmand_est_propose_pas_le_systeme_ni_l_antitriche_ni_l_exclu()
    {
        var parties = new[]
        {
            Partie(new("obs64", 60, 3), new("svchost", 80, 4), new("EasyAntiCheat_EOS", 40, 2), new("discord", 30, 2)),
            Partie(new("SomeAntiCheatService", 50, 2), new("OBS64", 70, 2)),
        };
        CalmeurDeVoisins.AProposer(parties, ["Discord"]).Should().Equal("OBS64");
    }

    [Fact]
    public void Ni_un_antitriche_ni_un_programme_exclu_par_le_joueur_ne_sont_calmes()
    {
        var s = new Systeme();
        foreach (var pid in new[] { 300, 400, 500 }) s.Priorites[pid] = ProcessPriorityClass.Normal;
        var c = new CalmeurDeVoisins(s, ["OBS64", "Discord"]);
        VoisinDuJeu[] vus = [V("obs64", 60, 300), V("BEService", 60, 400), V("discord", 60, 500)];
        c.Lecture(vus, Jeu, PremierPlan);
        c.Lecture(vus, Jeu, PremierPlan).Should().BeEmpty();
        s.Priorites.Values.Should().OnlyContain(p => p == ProcessPriorityClass.Normal);
    }
}
