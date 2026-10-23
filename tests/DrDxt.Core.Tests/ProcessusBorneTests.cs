using System.Diagnostics;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Les outils de Windows lancés par DrDXT passent par un seul lanceur : chemin absolu, dossier de travail fixé, deux flux lus
// et bornés en taille, attente bornée, arbre arrêté à la borne. Un outil qui ne rend pas la main ne fige pas l'appelant ; une
// sortie abondante ne fige pas l'outil.
public sealed class ProcessusBorneTests
{
    private static string Systeme(string exe) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe);

    private static ProcessStartInfo Cmd(string commande)
    {
        var psi = new ProcessStartInfo(Systeme("cmd.exe"));
        foreach (var a in new[] { "/d", "/c", commande }) psi.ArgumentList.Add(a);
        return psi;
    }

    [Fact]
    public void Un_outil_qui_ne_rend_pas_la_main_echoue_vite_et_il_est_arrete()
    {
        var psi = new ProcessStartInfo(Systeme("ping.exe"));
        foreach (var a in new[] { "-n", "30", "127.0.0.1" }) psi.ArgumentList.Add(a);
        var chrono = Stopwatch.StartNew();

        var r = ProcessusBorne.Executer(psi, TimeSpan.FromMilliseconds(500));

        chrono.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        r.Lance.Should().BeTrue();
        r.Fini.Should().BeFalse();
        Action relire = () => { using var p = Process.GetProcessById(r.Pid); p.HasExited.Should().BeTrue("arrêté à la borne"); };
        try { relire(); } catch (ArgumentException) { /* déjà sorti de la table des processus : arrêté */ }
    }

    // Une erreur standard abondante et non lue remplirait le tuyau : l'outil resterait bloqué jusqu'à la borne. Bloqué, il
    // ne finit à aucune borne : celle-ci n'a qu'à dépasser le temps honnête de l'outil, jusqu'à 5 s sur un processeur saturé.
    [Fact]
    public void Une_sortie_abondante_sur_les_deux_flux_ne_fige_pas_l_outil()
    {
        var r = ProcessusBorne.Executer(
            Cmd("for /l %i in (1,1,2000) do @(echo sortie-sortie-sortie-sortie-sortie & echo erreur-erreur-erreur-erreur-erreur 1>&2)"),
            TimeSpan.FromSeconds(30));

        r.Fini.Should().BeTrue("les deux flux sont lus pendant l'attente");
        r.Code.Should().Be(0);
        r.Sortie.Should().Contain("sortie-sortie");
        r.Erreurs.Should().Contain("erreur-erreur");
    }

    // Au-delà de la borne de taille, la suite est lue et jetée : l'outil finit, la mémoire ne suit pas sa sortie.
    [Fact]
    public void La_sortie_gardee_est_bornee_en_taille_et_l_outil_finit()
    {
        var r = ProcessusBorne.Executer(
            Cmd("for /l %i in (1,1,2000) do @(echo sortie-sortie-sortie-sortie-sortie & echo erreur-erreur-erreur-erreur-erreur 1>&2)"),
            TimeSpan.FromSeconds(30), maxCaracteres: 1000);

        r.Fini.Should().BeTrue();
        r.Sortie.Length.Should().Be(1000);
        r.Erreurs.Length.Should().Be(1000);
    }

    // Un petit-enfant qui hérite des flux et survit à l'outil garde le tuyau ouvert : la fin de la lecture s'attend, bornée,
    // et l'outil fini rend son vrai code.
    [Fact]
    public void Un_enfant_qui_garde_les_flux_ouverts_ne_fige_pas_l_appel()
    {
        var debut = DateTime.Now.AddSeconds(-1);
        var chrono = Stopwatch.StartNew();

        var r = ProcessusBorne.Executer(Cmd("start /b ping -n 8 127.0.0.1 & echo parti"), TimeSpan.FromSeconds(1));

        chrono.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4.5), "borne d'une seconde, plancher de deux pour les flux");
        r.Fini.Should().BeTrue("l'outil lui-même a rendu la main");
        r.Code.Should().Be(0);
        r.Sortie.Should().Contain("parti");
        // Arrêté, un processus reste un instant dans la table : sur un processeur saturé, le PING arrêté y figurait encore,
        // déjà sorti. Seul un PING encore vivant contredit l'arrêt.
        Process.GetProcessesByName("PING").Where(p => { try { return p.StartTime >= debut && !p.HasExited; } catch (Exception) { return false; } })
            .Should().BeEmpty("le petit-enfant est arrêté avec l'arbre");
    }

    // Un nom nu partirait dans l'ordre de recherche de CreateProcess (dossier de l'appli, dossier courant) avant System32.
    [Fact]
    public void Un_chemin_qui_n_est_pas_absolu_est_refuse_avant_tout_lancement()
    {
        Action lancer = () => ProcessusBorne.Executer(new ProcessStartInfo("ping.exe"), TimeSpan.FromSeconds(1));
        lancer.Should().Throw<ArgumentException>();
    }

    // Le dossier courant de l'appelant n'est pas celui de l'outil : un fichier relatif ne s'y écrit pas.
    [Fact]
    public void Sans_dossier_de_travail_l_outil_tourne_dans_system32()
    {
        var r = ProcessusBorne.Executer(Cmd("cd"), TimeSpan.FromSeconds(4));

        r.Sortie.Trim().Should().BeEquivalentTo(Environment.GetFolderPath(Environment.SpecialFolder.System));
    }

    // Une seule implémentation à garder juste : aucun autre fichier de src ne lance un processus dont il redirige les flux.
    [Fact]
    public void Les_outils_ne_se_lancent_avec_leurs_flux_que_par_le_lanceur_borne()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent!;
        Directory.EnumerateFiles(Path.Combine(d.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && Path.GetFileName(f) != "ProcessusBorne.cs")
            .Where(f => File.ReadAllText(f) is var t && t.Contains("Process.Start(", StringComparison.Ordinal)
                        && t.Contains("RedirectStandard", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Should().BeEmpty();
    }
}
