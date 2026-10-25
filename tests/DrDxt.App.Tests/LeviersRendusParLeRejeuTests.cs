using DrDxt.App.Platform;
using DrDxt.Application.Tweaks;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Journal;
using DrDxt.Reconciler.Desinstallation;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Le rejeu rend une pose dont l'App n'a pas acquitté l'origine, alors qu'elle l'a déjà notée : resté au journal des
// leviers, le levier serait dit « défait » par une mise à jour de Windows au scan suivant.
public sealed class LeviersRendusParLeRejeuTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private static readonly SetRegistryValueOp Pose =
        new(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueType.DWord, "2");
    private static readonly TweakSnapshot Garde = new("latency.hagsOn",
        PrivilegedOpCodec.Serialize(Pose with { Value = "1" }).Value!, PrivilegedOpCodec.Serialize(Pose).Value!);

    private static RapportDuRejeu.Ligne Ligne(IssueDuRendu issue, string? cle = null)
        => new("HwSchMode", issue, "", false, cle ?? RejeuDuJournal.CleDe(Pose, Sid));

    [Fact]
    public void Un_levier_rendu_par_le_rejeu_et_relu_a_l_origine_n_est_plus_garde()
        => LeviersRendusParLeRejeu.Ids([Garde], [Ligne(IssueDuRendu.Rendu)], _ => EtatApplique.NotApplied, Sid)
            .Should().Equal("latency.hagsOn");

    [Fact]
    public void Repose_depuis_il_reste_garde()
        => LeviersRendusParLeRejeu.Ids([Garde], [Ligne(IssueDuRendu.Rendu)], _ => EtatApplique.Applied, Sid).Should().BeEmpty();

    [Theory]
    [InlineData(IssueDuRendu.Respecte)]
    [InlineData(IssueDuRendu.EchecDEcriture)]
    public void Une_feuille_que_le_rejeu_n_a_pas_rendue_ne_retire_rien(IssueDuRendu issue)
        => LeviersRendusParLeRejeu.Ids([Garde], [Ligne(issue)], _ => EtatApplique.NotApplied, Sid).Should().BeEmpty();

    [Fact]
    public void Une_autre_feuille_rendue_ne_retire_rien()
        => LeviersRendusParLeRejeu.Ids([Garde], [Ligne(IssueDuRendu.Rendu, "AUTRE")], _ => EtatApplique.NotApplied, Sid).Should().BeEmpty();
}
