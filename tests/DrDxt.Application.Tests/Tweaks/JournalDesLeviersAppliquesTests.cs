using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks;

/// <summary>Le journal qui permet enfin a GainsDisparus d'etre appele par le produit.</summary>
public class JournalDesLeviersAppliquesTests : IDisposable
{
    private readonly string _chemin = Path.Combine(Path.GetTempPath(), $"drdxt-journal-{Guid.NewGuid():N}.json");
    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_chemin)!, Path.GetFileName(_chemin) + "*"))
            try { File.Delete(f); } catch { }
    }

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
    public void Seuls_les_leviers_appliques_et_non_annules_sont_notes_et_la_plus_ancienne_origine_reste()
    {
        var j = new JournalDesLeviersAppliques(_chemin);
        j.Enregistrer(new ApplyReport(new[]
        {
            Garde(Snapshot("latency.gameDvrBackgroundOff", "1")),
            new ApplyOutcome("latency.hagsOn", true, AutoReverted: true, Snapshot("latency.hagsOn", "1"), null),
            new ApplyOutcome("fixit.power_plan", false, false, Snapshot("fixit.power_plan", "1"), null),
        }, null));
        // Reposé sans avoir été rendu, le levier relit sa propre valeur posée : ce n'est pas l'origine du joueur.
        j.Enregistrer(new ApplyReport(new[] { Garde(Snapshot("latency.gameDvrBackgroundOff", "0") with { AppliedValueJson = "posee" }) }, null));

        var garde = j.Lire().Should().ContainSingle().Subject;
        garde.OriginalValueJson.Should().Contain("\"1\"");
        garde.AppliedValueJson.Should().Be("posee", "la valeur posée, elle, suit la dernière pose");
    }

    [Fact]
    public void Rendu_puis_repose_le_levier_repart_de_sa_nouvelle_origine()
    {
        var j = new JournalDesLeviersAppliques(_chemin);
        j.Enregistrer(new ApplyReport(new[] { Garde(Snapshot("latency.gameDvrBackgroundOff", "1")) }, null));
        j.Retirer("latency.gameDvrBackgroundOff").Should().BeTrue();
        j.Enregistrer(new ApplyReport(new[] { Garde(Snapshot("latency.gameDvrBackgroundOff", "0")) }, null));

        j.Lire().Should().ContainSingle().Which.OriginalValueJson.Should().Contain("\"0\"");
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("doublon")]
    public void Un_journal_qui_porte_un_vide_ou_un_doublon_s_enregistre_encore(string contenu)
    {
        var s = Snapshot("latency.gameDvrBackgroundOff", "1");
        File.WriteAllText(_chemin, contenu == "doublon" ? System.Text.Json.JsonSerializer.Serialize(new[] { s, s }) : contenu);

        new JournalDesLeviersAppliques(_chemin).Enregistrer(new ApplyReport(new[] { Garde(Snapshot("latency.gameDvrBackgroundOff", "0")) }, null))
            .Should().BeTrue();
        new JournalDesLeviersAppliques(_chemin).Lire().Should().ContainSingle();
    }

    // Une entrée sans identifiant (écrite hors de DrDXT) faisait tomber la lecture entière, et chaque enregistrement.
    [Fact]
    public void Une_entree_sans_identifiant_n_efface_pas_les_autres()
    {
        var s = Snapshot("latency.gameDvrBackgroundOff", "1");
        File.WriteAllText(_chemin, "[{\"TweakId\":null,\"OriginalValueJson\":\"x\"}," + System.Text.Json.JsonSerializer.Serialize(s) + "]");

        new JournalDesLeviersAppliques(_chemin).Lire().Should().ContainSingle();
        new JournalDesLeviersAppliques(_chemin).Enregistrer(new ApplyReport(new[] { Garde(Snapshot("latency.hagsOn", "1")) }, null))
            .Should().BeTrue();
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
    public void Un_journal_absent_ne_dit_rien_un_journal_illisible_est_dit()
    {
        new JournalDesLeviersAppliques(_chemin).Constats(new Registre(1)).Should().BeEmpty();
        File.WriteAllText(_chemin, "{ pas du json");

        var constats = new JournalDesLeviersAppliques(_chemin).Constats(new Registre(1));

        constats.Should().ContainSingle().Which.Statut.Should().Be(RepairStatus.Unknown,
            "sans lui, « Annuler » et le retrait automatique ne voient plus ce que DrDXT a posé");
    }

    // Illisible (coupure, ou fichier écrit par un autre programme du compte), le journal s'arrêtait en silence pour toujours :
    // plus aucun levier noté. Il est mis de côté, sa trace reste, et le journal repart.
    [Fact]
    public void Un_journal_illisible_est_mis_de_cote_et_le_journal_repart()
    {
        File.WriteAllText(_chemin, "{ pas du json");
        var j = new JournalDesLeviersAppliques(_chemin);

        j.Enregistrer(new ApplyReport(new[] { Garde(Snapshot("latency.gameDvrBackgroundOff", "1")) }, null));

        j.Lire().Should().ContainSingle();
        var deCote = Directory.GetFiles(Path.GetDirectoryName(_chemin)!, Path.GetFileName(_chemin) + ".mis-de-cote-*");
        deCote.Should().ContainSingle();
        File.ReadAllText(deCote[0]).Should().Be("{ pas du json");
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

    // L'origine d'une pose ne se garde que si la valeur trouvée avant la pose suivante est encore la pose de DrDXT.
    [Fact]
    public void La_premiere_origine_ne_reste_que_si_la_pose_de_DrDXT_etait_encore_la()
    {
        var j = new JournalDesLeviersAppliques(_chemin);
        var poseDrDxt = Snapshot("latency.gameDvrBackgroundOff", "0").OriginalValueJson;
        j.Enregistrer(new ApplyReport(new[] { Garde(Snapshot("latency.gameDvrBackgroundOff", "1") with { AppliedValueJson = poseDrDxt }) }, null));

        j.Enregistrer(new ApplyReport(new[] { Garde(Snapshot("latency.gameDvrBackgroundOff", "0") with { AppliedValueJson = poseDrDxt }) }, null));
        j.Lire().Single().OriginalValueJson.Should().Be(Snapshot("x", "1").OriginalValueJson, "la pose de DrDXT était encore là");

        j.Enregistrer(new ApplyReport(new[] { Garde(Snapshot("latency.gameDvrBackgroundOff", "2") with { AppliedValueJson = poseDrDxt }) }, null));
        j.Lire().Single().OriginalValueJson.Should().Be(Snapshot("x", "2").OriginalValueJson, "le joueur a repris la main");
    }
}
