using DrDxt.Core;
using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

/// <summary>
/// 25/09/2026 — le signal « une mise à jour Windows vient de s'installer » lit le même journal que
/// l'audit de santé. Il n'était pas écrit : il rendait toujours « non ».
/// </summary>
public sealed class EventLogUpdateSignalTests
{
    private static readonly DateTimeOffset Maintenant = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed class Journal(Result<IReadOnlyList<EssaiDeMiseAJour>> rendu) : IWindowsUpdateLog
    {
        public DateTimeOffset? DemandeDepuis;
        public Result<IReadOnlyList<EssaiDeMiseAJour>> Depuis(DateTimeOffset earliest)
        {
            DemandeDepuis = earliest;
            return rendu;
        }
    }

    private static Journal Avec(params EssaiDeMiseAJour[] essais) =>
        new(Result<IReadOnlyList<EssaiDeMiseAJour>>.Ok(essais));

    private static Task<Result<bool>> Lire(IWindowsUpdateLog journal) =>
        new EventLogUpdateSignal(journal, () => Maintenant).RecentSetupCompletionAsync(CancellationToken.None);

    [Fact]
    public async Task Une_mise_a_jour_Windows_reussie_dans_la_fenetre_est_un_signal()
    {
        var journal = Avec(new EssaiDeMiseAJour(Maintenant.AddHours(-3), true,
            "2026-09 Mise à jour cumulative pour Windows 11 (KB5099999)", null));

        (await Lire(journal)).Value.Should().BeTrue();
        journal.DemandeDepuis.Should().Be(Maintenant - EventLogUpdateSignal.Fenetre);
    }

    [Fact]
    public async Task Les_definitions_Defender_et_les_echecs_ne_comptent_pas()
    {
        var journal = Avec(
            new EssaiDeMiseAJour(Maintenant.AddHours(-1), true,
                "Mise à jour de la veille de sécurité pour Microsoft Defender Antivirus - KB2267602", null),
            new EssaiDeMiseAJour(Maintenant.AddHours(-2), false, "Mise à jour cumulative pour Windows 11", "0x8024001e"));

        (await Lire(journal)).Value.Should().BeFalse();
    }

    [Fact]
    public async Task Un_journal_illisible_est_un_echec_pas_un_non()
    {
        var journal = new Journal(Result<IReadOnlyList<EssaiDeMiseAJour>>.Fail(ErrorCodes.DetectionFailed, "illisible"));

        (await Lire(journal)).IsOk.Should().BeFalse("le déclencheur traite l'échec comme « pas de signal », sans l'inventer");
    }
}
