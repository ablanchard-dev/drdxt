using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

/// <summary>
/// Ce fichier a affirme le contraire pendant une journee, et la machine l'a dementi.
///
/// Il exigeait un tube de controle prive (`drdxt-presentmon-control`), au motif que le binaire
/// vendore documente `--control-pipe` dans ses propres chaines d'aide. Trois mesures par le SCM,
/// le 07/09/2026, ont montre que l'option est acceptee **et sans effet sur le nom publie** :
///
///  1. service DrDXT 2.4.1 + `--control-pipe \\.\pipe\drdxt-presentmon-control`, Intel arrete
///     → publie `sharedpresentmonsvcnamedpipe-in/-out`, le nom par defaut ;
///  2. meme essai avec le binaire Intel 2.5.1 → meme resultat (et 2.5.1 refuse `--intro-nsm`) ;
///  3. client 2.4.1 → service Intel 2.5.1 : PM_STATUS 12 ; → service DrDXT 2.4.1 : succes.
///
/// Un test qui exige un nom impossible ne protege rien : il verrouille une erreur. Ce fichier
/// verrouille desormais le fait — il n'existe qu'un tube, et le produit doit le savoir.
/// </summary>
public class PresentMonNamingTests
{
    [Fact]
    public void Le_client_ouvre_le_SEUL_tube_que_le_service_publie()
    {
        // Mesure 1 : quoi qu'on passe au serveur, il publie ce nom-la.
        PresentMonNaming.ControlPipe.Should().Be(@"\\.\pipe\sharedpresentmonsvcnamedpipe");
    }

    [Fact]
    public void Le_produit_ne_pose_AUCUN_argument_dont_il_ignore_l_effet()
    {
        // `--control-pipe` est prouve sans effet ; `--intro-nsm` et `--etw-session-name` n'ont
        // jamais ete mesures. Poser un argument non mesure, c'est inventer une difference entre
        // deux installations qui n'en ont aucune.
        PresentMonNaming.ServiceArguments.Should().BeEmpty();
    }

    [Fact]
    public void Le_binPath_construit_reste_lisible_par_la_decision_de_cycle_de_vie()
    {
        // La garde qui compte vraiment : ce que l'installeur ecrit doit etre ce que `Decide` relit.
        const string binaire = @"C:\Program Files\DrDXT\PresentMonService.exe";

        PresentMonServicePlan.Decide(exists: true, PresentMonServicePlan.BinPath(binaire), binaire)
            .Should().Be(PresentMonServiceAction.StartOnly);
    }
}
