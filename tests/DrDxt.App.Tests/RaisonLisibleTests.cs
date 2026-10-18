using System.IO;
using System.Text.RegularExpressions;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Le message d'un échec (souvent l'anglais technique du helper) se collait à la phrase française du joueur. Il ne va qu'au
// journal ; le joueur lit la phrase du code, ou une phrase générique avec le code entre parenthèses, que le support relie
// au journal.
public class RaisonLisibleTests
{
    private static string L(string k) => k switch
    {
        "Erreur.NOT_ALLOWLISTED" => "Windows ne permet pas ce réglage ici.",
        "Erreur.Generique" => "raison technique : {0}",
        _ => k,
    };

    [Fact]
    public void Un_code_qui_a_sa_phrase_la_dit_sans_le_message_technique()
    {
        var journal = new List<string>();

        var raison = RaisonLisible.Dire(Result.Fail("NOT_ALLOWLISTED", "Operation not in allowlist"), L, journal.Add);

        raison.Should().Be("Windows ne permet pas ce réglage ici.");
        journal.Should().ContainSingle().Which.Should().Contain("Operation not in allowlist");
    }

    [Fact]
    public void Un_code_sans_phrase_dit_sa_raison_technique()
        => RaisonLisible.Dire(Result.Fail("HELPER_OP_FAILED", "pnputil exit 5"), L)
            .Should().Be("raison technique : HELPER_OP_FAILED");

    // Le crible : aucun ViewModel ne montre le message d'un échec ; seule RaisonLisible le lit, pour le journal.
    [Fact]
    public void Aucun_ViewModel_ne_montre_le_message_d_un_echec()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var vues = Directory.GetFiles(Path.Combine(d!.FullName, "src", "DrDxt.App", "ViewModels"), "*.cs");
        var message = new Regex(@"Error(\?|!)?\.(Value\.)?Message");
        // Ce qui lit le message sans le montrer : RaisonLisible (pour le journal), et une exception relancée vers l'appelant.
        string[] admises = ["RaisonLisible.cs"];

        vues.Should().HaveCountGreaterThan(10, "témoin : le crible lit les ViewModels");
        vues.Where(f => !admises.Contains(Path.GetFileName(f)))
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (Fichier: Path.GetFileName(f), Ligne: i + 1, Texte: l)))
            .Where(x => message.IsMatch(x.Texte) && !x.Texte.Contains("throw new"))
            .Select(x => $"{x.Fichier}:{x.Ligne}").Should().BeEmpty();
    }
    // La phrase de repli ne sait pas ce qui a été écrit avant l'échec (plusieurs sites échouent APRÈS une écriture
    // réussie) : elle n'affirme rien sur l'état de la machine, la phrase d'appel le dit. Même règle pour un code rendu par
    // plusieurs sites, dont une exception après l'envoi (HELPER_UNREACHABLE).
    [Theory]
    [InlineData("fr-FR", "HELPER.OP_FAILED")]
    [InlineData("en-US", "HELPER.OP_FAILED")]
    [InlineData("fr-FR", "HELPER_UNREACHABLE")]
    [InlineData("en-US", "HELPER_UNREACHABLE")]
    public void Le_repli_n_affirme_rien_sur_l_etat_de_la_machine(string langue, string code)
    {
        var resx = new DrDxt.Infrastructure.Localization.ResxLocalizer();
        var culture = System.Globalization.CultureInfo.GetCultureInfo(langue);
        string Loc(string k) => resx.Get(k, culture);

        var raison = RaisonLisible.Dire(Result.Fail(code, "x"), Loc);
        var phrase = string.Format(culture, Loc("Changement.PasSortiDuManifeste"), "HAGS", raison);

        raison.Should().NotContain("rien").And.NotContain("nothing").And.NotContain("changed").And.NotContain("modifié").And.NotContain("remis");
        phrase.Should().NotContain("rien n'a").And.NotContain("nothing was");
    }

    // La requête est partie, la réponse manque : l'op a pu se faire. Le préfixe d'échec de l'appelant serait faux ; la
    // phrase de l'incertitude le remplace. Tout autre échec garde sa phrase.
    [Fact]
    public void Une_reponse_manquante_remplace_la_phrase_d_echec()
    {
        string Loc(string k) => k switch
        {
            "Erreur.HELPER_NO_REPLY" => "peut être posé ou non",
            "Erreur.HELPER_NO_REPLY.JOURNALED" => "peut être posé ou non, rendu après",
            _ => L(k),
        };

        RaisonLisible.OuIncertain(Result.Fail(ErrorCodes.HelperNoReply, "x"), "Échec : x", Loc).Should().Be("peut être posé ou non");
        RaisonLisible.OuIncertain(Result.Fail(ErrorCodes.HelperNoReplyJournalisee, "x"), "Échec : x", Loc).Should().Be("peut être posé ou non, rendu après");
        RaisonLisible.OuIncertain(Result.Fail("NOT_ALLOWLISTED", "x"), "Échec : x", Loc).Should().Be("Échec : x");
    }

    // La promesse du rendu n'est faite que pour une pose notée au journal, et dite telle qu'elle est : le rejeu tourne au
    // lancement suivant du helper (un geste qui demande les droits) ou au démarrage du service, jamais à la fermeture même.
    [Theory]
    [InlineData("fr-FR", "Relance le scan", "valeur d'origine")]
    [InlineData("en-US", "Run the scan again", "original value")]
    public void Seule_une_pose_journalisee_promet_le_rendu(string langue, string relancer, string rendu)
    {
        var resx = new DrDxt.Infrastructure.Localization.ResxLocalizer();
        var culture = System.Globalization.CultureInfo.GetCultureInfo(langue);
        string Loc(string k) => resx.Get(k, culture);

        var nue = RaisonLisible.Dire(Result.Fail(ErrorCodes.HelperNoReply, "x"), Loc);
        var journalisee = RaisonLisible.Dire(Result.Fail(ErrorCodes.HelperNoReplyJournalisee, "x"), Loc);

        nue.Should().Contain(relancer).And.NotContain(rendu);
        journalisee.Should().Contain(relancer).And.Contain(rendu);
    }

    // Les refus du journal d'écriture anticipée : avant l'écriture, rien n'est écrit et c'est dit. Un acquit refusé arrive
    // après, la pose est en place : il n'a pas de phrase propre (aucun écran ne le lit ; la fermeture dit JEA.AcquitRefuse),
    // et s'il était lu, le repli neutre ne dirait jamais « rien n'a été écrit ».
    [Theory]
    [InlineData("fr-FR", "JEA_REFUS", "rien n'a été écrit")]
    [InlineData("en-US", "JEA_REFUS", "nothing was written")]
    [InlineData("fr-FR", "JEA_JOURNAL", "rien n'a été écrit")]
    [InlineData("en-US", "JEA_JOURNAL", "nothing was written")]
    [InlineData("fr-FR", "JEA_ACQUIT_REFUSE", "JEA_ACQUIT_REFUSE")]
    [InlineData("en-US", "JEA_ACQUIT_REFUSE", "JEA_ACQUIT_REFUSE")]
    public void Un_refus_du_journal_dit_ce_qui_est_ecrit(string langue, string code, string attendu)
    {
        var resx = new DrDxt.Infrastructure.Localization.ResxLocalizer();
        var culture = System.Globalization.CultureInfo.GetCultureInfo(langue);
        string Loc(string k) => resx.Get(k, culture);

        var raison = RaisonLisible.Dire(Result.Fail(code, "x"), Loc);

        raison.Should().Contain(attendu);
        if (code == "JEA_ACQUIT_REFUSE") raison.Should().NotContain("rien n'a été écrit").And.NotContain("nothing was written");
    }
}
