using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// La tâche « DrDXT helper » lance le helper avec --service pour toute la session. Le tube reste ouvert plus longtemps ; ce
/// qui le protège (l'authentification des appelants, l'allowlist, l'exécuteur) ne dépend pas du mode. --service ne change
/// que l'arrêt : plus d'arrêt sur inactivité ni à la fermeture de l'App, seulement quand l'installeur tourne, baux rendus.
/// </summary>
public sealed class LeModeServiceNeChangeQueLArretTests
{
    private static string Source(string relatif)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDxt.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, relatif));
    }

    [Fact]
    public void Le_drapeau_ne_sert_qu_a_l_arret_et_au_message_d_ecoute()
    {
        var programme = Source("src/DrDxt.ElevatedHelper/Program.cs");

        Regex.Matches(programme, @"\bserviceMode\b").Should().HaveCount(3, "la lecture du drapeau, le choix de l'arrêt, le message d'écoute");
        programme.Should().Contain("var inactivite = serviceMode ? null : new ArretSurInactivite(");
        programme.Should().Contain("(elevated{(serviceMode ? \", service mode\" : string.Empty)})");
    }

    [Fact]
    public void En_service_le_helper_tient_la_session_meme_App_fermee_et_inactif()
    {
        Program.DoitSArreter(null, bauxActifs: 0, vivant: _ => false, installeurEnCours: () => false).Should().BeFalse(
            "App fermée, aucune connexion : la tâche le garde pour toute la session");
    }

    [Fact]
    public void En_service_il_s_arrete_quand_l_installeur_tourne_ses_baux_rendus()
    {
        Program.DoitSArreter(null, bauxActifs: 0, vivant: _ => true, installeurEnCours: () => true).Should().BeTrue();
        Program.DoitSArreter(null, bauxActifs: 1, vivant: _ => true, installeurEnCours: () => true).Should().BeFalse(
            "un bail actif se rend d'abord, jamais coupé par un arrêt forcé");
    }

    [Fact]
    public void Lance_a_la_demande_il_garde_l_arret_sur_inactivite()
    {
        var maintenant = DateTimeOffset.Parse("2026-10-29T20:00:00Z");
        var inactivite = new ArretSurInactivite(TimeSpan.FromMinutes(15), () => maintenant);

        Program.DoitSArreter(inactivite, 0, _ => true, () => false).Should().BeFalse("témoin : moins de 15 minutes");
        maintenant = maintenant.AddMinutes(15);
        Program.DoitSArreter(inactivite, 0, _ => true, () => false).Should().BeTrue("15 minutes sans connexion ni bail");
    }
}
