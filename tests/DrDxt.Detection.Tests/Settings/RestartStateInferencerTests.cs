using DrDxt.Core;
using DrDxt.Detection.Settings;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Settings;

/// <summary>
/// L'inférenceur était livré avec toute sa justification et aucun test. Ceux-ci figent les deux pièges
/// que sa propre doc nomme : une justification en commentaire ne prouve rien tant qu'un test n'échoue pas quand elle casse.
/// </summary>
public class RestartStateInferencerTests
{
    private static readonly string[] NoRenames = Array.Empty<string>();
    private static readonly string[] SomeRenames = { @"\??\C:\Windows\Temp\x.dll" };

    // ---- diagnostic à froid (aucun changement à vérifier) ----------------------------

    [Fact]
    public void Servicing_key_alone_requires_a_restart()
        => RestartStateInferencer.Infer(true, NoRenames, TriState.Off, TimeSpan.FromHours(1), null)
            .Verdict.Should().Be(RestartVerdict.RestartRequired);

    [Fact]
    public void Pending_file_renames_alone_never_claim_a_restart_is_required()
    {
        // Piège 1 : les contrôles tout faits s'allument ici sur une machine parfaitement saine.
        var s = RestartStateInferencer.Infer(false, SomeRenames, TriState.Off, TimeSpan.FromHours(1), null);

        s.Verdict.Should().Be(RestartVerdict.Indeterminate);
        s.Verdict.Should().NotBe(RestartVerdict.RestartRequired);
    }

    [Fact]
    public void Nothing_readable_is_indeterminate_not_a_clean_bill_of_health()
        => RestartStateInferencer.Infer(null, null, TriState.Unknown, null, null)
            .Verdict.Should().Be(RestartVerdict.Indeterminate);

    [Fact]
    public void Read_and_clean_reports_no_restart_pending()
        => RestartStateInferencer.Infer(false, NoRenames, TriState.Off, TimeSpan.FromHours(1), null)
            .Verdict.Should().Be(RestartVerdict.NoRestartPending);

    // ---- vérifier un changement ------------------------------------------------------

    [Fact]
    public void A_kernel_session_older_than_the_change_means_the_change_is_not_in_effect()
        => RestartStateInferencer.Infer(false, NoRenames, TriState.Off,
                uptime: TimeSpan.FromHours(5), sinceChange: TimeSpan.FromHours(2))
            .Verdict.Should().Be(RestartVerdict.RestartRequired);

    [Fact]
    public void Fast_startup_makes_a_restart_after_the_change_unprovable()
    {
        // Piège 2 : la durée de fonctionnement dit que la machine a démarré après le changement, mais avec
        // le démarrage rapide ce démarrage a pu restaurer l'ancienne session noyau. Dire « appliqué » ici
        // serait le mensonge silencieux que toute la classe existe pour empêcher.
        var s = RestartStateInferencer.Infer(false, NoRenames, TriState.On,
            uptime: TimeSpan.FromMinutes(10), sinceChange: TimeSpan.FromHours(2));

        s.Verdict.Should().Be(RestartVerdict.Indeterminate);
        s.Reason.Should().Contain("Restart");   // dit au joueur quel bouton, pas seulement « inconnu »
    }

    [Fact]
    public void An_unreadable_fast_startup_is_not_treated_as_off()
        => RestartStateInferencer.Infer(false, NoRenames, TriState.Unknown,
                uptime: TimeSpan.FromMinutes(10), sinceChange: TimeSpan.FromHours(2))
            .Verdict.Should().Be(RestartVerdict.Indeterminate);

    [Fact]
    public void A_real_restart_after_the_change_puts_it_in_effect()
        => RestartStateInferencer.Infer(false, NoRenames, TriState.Off,
                uptime: TimeSpan.FromMinutes(10), sinceChange: TimeSpan.FromHours(2))
            .Verdict.Should().Be(RestartVerdict.NoRestartPending);

    [Fact]
    public void Queued_renames_do_not_spoil_a_verified_change()
    {
        var s = RestartStateInferencer.Infer(false, SomeRenames, TriState.Off,
            uptime: TimeSpan.FromMinutes(10), sinceChange: TimeSpan.FromHours(2));

        s.Verdict.Should().Be(RestartVerdict.NoRestartPending);
        s.Reason.Should().Contain("routine");
    }

    [Fact]
    public void An_unreadable_uptime_cannot_verify_a_change()
        => RestartStateInferencer.Infer(false, NoRenames, TriState.Off,
                uptime: null, sinceChange: TimeSpan.FromHours(2))
            .Verdict.Should().Be(RestartVerdict.Indeterminate);

    // ---- la machine de référence (i7-14700F, Windows 11) ------------------------------

    [Fact]
    public void The_real_healthy_machine_that_every_market_tool_calls_reboot_required()
    {
        // Mesuré en direct : 86 entrées PendingFileRenameOperations (Edge, EdgeUpdate,
        // gamingservicesproxy) alors que les deux clés de maintenance lisent false. La machine est saine.
        // Tout contrôle qui teste la simple présence de cette valeur crierait « redémarrage requis » ici.
        var renames = Enumerable.Range(0, 86)
            .Select(i => $@"\??\C:\Program Files (x86)\Microsoft\EdgeUpdate.3.{i}")
            .ToArray();

        var r = RestartStateInferencer.Infer(false, renames, TriState.On, TimeSpan.FromDays(11), null);

        r.Verdict.Should().NotBe(RestartVerdict.RestartRequired,
            "queued renames alone never establish that a restart is owed");
        r.Verdict.Should().Be(RestartVerdict.Indeterminate);
    }

    [Fact]
    public void Fast_startup_plus_an_eleven_day_uptime_cannot_confirm_a_change_took_effect()
    {
        // Même machine : HiberbootEnabled=1 et LastBootUpTime vieux de 11 jours. Un changement écrit
        // depuis n'est pas passé par une vraie initialisation du noyau, et « Arrêter » ne lui en
        // donnerait pas non plus. Dire « appliqué » ici est l'échec que tout ce type existe pour arrêter.
        var r = RestartStateInferencer.Infer(
            false, NoRenames, TriState.On, TimeSpan.FromDays(11), TimeSpan.FromDays(3));

        r.Verdict.Should().Be(RestartVerdict.RestartRequired);
        r.Reason.Should().Contain("not restarted since");
    }
}
