using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks;

/// <summary>Le journal qui permet enfin a GainsDisparus d'etre appele par le produit.</summary>
public class JournalDesLeviersAppliquesTests : IDisposable
{
    private readonly string _chemin = Path.Combine(Path.GetTempPath(), $"drdxt-journal-{Guid.NewGuid():N}.json");
    public void Dispose() { try { File.Delete(_chemin); } catch { } }

    private sealed class Registre(uint? valeur) : IRegistryReader
    {
        public Result<uint?> ReadDword(RegistryHiveKind h, string p, string n) => Result<uint?>.Ok(valeur);
        public Result<string?> ReadString(RegistryHiveKind h, string p, string n) => Result<string?>.Ok(null);
        public Result<string?> ReadMultiString(RegistryHiveKind h, string p, string n) => Result<string?>.Ok(null);
    }

    private static TweakSnapshot Snapshot(string id, string avant) => new(id,
        PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled",
            RegistryValueType.DWord, avant)).Value!);

    private static ApplyOutcome Garde(TweakSnapshot s) => new(s.TweakId, true, false, s, null);

    [Fact]
    public void Seuls_les_leviers_appliques_et_non_annules_sont_notes_et_le_plus_recent_l_emporte()
    {
        var j = new JournalDesLeviersAppliques(_chemin);
        j.Enregistrer(new ApplyReport(new[]
        {
            Garde(Snapshot("latency.gameDvrBackgroundOff", "1")),
            new ApplyOutcome("latency.hagsOn", true, AutoReverted: true, Snapshot("latency.hagsOn", "1"), null),
            new ApplyOutcome("fixit.power_plan", false, false, Snapshot("fixit.power_plan", "1"), null),
        }, null));
        j.Enregistrer(new ApplyReport(new[] { Garde(Snapshot("latency.gameDvrBackgroundOff", "0")) }, null));

        j.Lire().Should().ContainSingle().Which.OriginalValueJson.Should().Contain("\"0\"");
    }

    [Fact]
    public void Un_gain_revenu_a_la_valeur_d_avant_devient_un_constat_sans_ecriture()
    {
        var j = new JournalDesLeviersAppliques(_chemin);
        j.Enregistrer(new ApplyReport(new[] { Garde(Snapshot("latency.gameDvrBackgroundOff", "1")) }, null));

        var constats = j.Constats(new Registre(1));   // revenu a 1 : quelqu'un a defait
        constats.Should().ContainSingle();
        constats[0].Action.Should().Be(RepairAction.Recommandation, "remarquer, jamais reecrire");
        constats[0].Explication.Should().Contain("HistoricalCaptureEnabled");

        j.Constats(new Registre(0)).Should().BeEmpty("toujours en place : rien a dire");
    }

    [Fact]
    public void Un_journal_absent_ou_illisible_ne_dit_rien()
    {
        new JournalDesLeviersAppliques(_chemin).Lire().Should().BeEmpty();
        File.WriteAllText(_chemin, "{ pas du json");
        new JournalDesLeviersAppliques(_chemin).Constats(new Registre(1)).Should().BeEmpty();
    }

    // Un journal illisible lu comme vide serait reecrit par Enregistrer avec les seuls leviers du
    // jour : l'historique des gains disparaitrait en silence. Sans lecture sure, on n'ecrit pas.
    [Fact]
    public void Un_journal_illisible_n_est_pas_ecrase_par_le_suivant()
    {
        File.WriteAllText(_chemin, "{ pas du json");

        new JournalDesLeviersAppliques(_chemin)
            .Enregistrer(new ApplyReport(new[] { Garde(Snapshot("latency.gameDvrBackgroundOff", "1")) }, null));

        File.ReadAllText(_chemin).Should().Be("{ pas du json");
    }

    // Une entree nulle ou sans identifiant faisait lever Constats, appele pendant le scan.
    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"OriginalValueJson\":\"x\"}]")]
    public void Une_entree_amputee_ne_fait_pas_lever_le_scan(string contenu)
    {
        File.WriteAllText(_chemin, contenu);

        var act = () => new JournalDesLeviersAppliques(_chemin).Constats(new Registre(1));

        act.Should().NotThrow();
    }
}
