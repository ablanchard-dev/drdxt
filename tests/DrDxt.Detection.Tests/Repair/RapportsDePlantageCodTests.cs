using System.Globalization;
using System.IO.Compression;
using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Les rapports que Call of Duty écrit en plantant : forme relevée sur la machine de référence (info.json plat, pile en csv).
public sealed class RapportsDePlantageCodTests
{
    private const string Info = """
        {"crash_time":"2026-06-08 04:32:22(337ms) UTC","exception_code":"0x00001338","crash_signature":"B50C0011-05928406-72DFD49D-D42F1649",
         "extch_perf_phys_avail_mb":"36337","extch_perf_commit_total_mb":"31286","extch_perf_commit_limit_mb":"69380",
         "vidmem_deficit_mb":"0","vidmem_sys_deficit_mb":"0","gfx_error_removed_code":"887A0005"}
        """;

    private const string Pile = "UsedInCrashSignature,FrameIndex,FrameAddress,FrameAdjustedAddress,ModuleBaseAddress,ModuleName\r\n"
        + @"1,3,0x1,0x1,0x1,C:\Program Files (x86)\Call of Duty\_retail_\cod.exe" + "\r\n"
        + @"1,2,0x1,0x1,0x1,C:\Program Files\Overlay\hook64.dll" + "\r\n"
        + @"0,1,0x1,0x1,0x1,C:\WINDOWS\SYSTEM32\ntdll.dll" + "\r\n"
        + "#,,,,,Number of frames captured: 3\r\n";

    [Fact]
    public void Un_rapport_se_decode_avec_son_heure_sa_memoire_et_son_erreur_DirectX()
    {
        var r = RapportsDePlantageCod.Decoder(Info, Pile)!;
        r.Quand.Should().Be(new DateTimeOffset(2026, 6, 8, 4, 32, 22, TimeSpan.Zero));
        (r.MemoireLibreMo, r.EngageMo, r.LimiteEngagementMo, r.DeficitVideoMo).Should().Be((36337L, 31286L, 69380L, 0L));
        r.ErreurDirectX.Should().Be("DXGI_ERROR_DEVICE_REMOVED");
        r.Modules.Should().Equal("hook64.dll");
    }

    // Notre règle : une clé absente reste « non lue », jamais un zéro inventé.
    [Fact]
    public void Une_cle_absente_reste_non_lue()
    {
        var r = RapportsDePlantageCod.Decoder("""{"crash_time":"2026-06-08 04:32:22(337ms) UTC"}""", null)!;
        (r.MemoireLibreMo, r.DeficitVideoMo, r.ErreurDirectX).Should().Be(((long?)null, (long?)null, (string?)null));
        RapportsDePlantageCod.Decoder("""{"exception_code":"0x1"}""", null).Should().BeNull("sans heure, pas de rapport daté");
        RapportsDePlantageCod.Decoder("pas du json", null).Should().BeNull();
    }

    [Fact]
    public void Le_constat_dit_les_faits_la_signature_repetee_et_le_meme_incident_sans_accuser()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var r = RapportsDePlantageCod.Decoder(Info, Pile)!;
        var c = RapportsDePlantageCod.Juger([r, r with { Quand = r.Quand.AddDays(-1), ErreurDirectX = null }], [r.Quand.AddMinutes(5)]).Single();
        c.Statut.Should().Be(RepairStatus.Observed);
        c.Action.Should().Be(RepairAction.Aucune);
        c.Explication.Should().Contain("revient 2 fois").And.Contain("DXGI_ERROR_DEVICE_REMOVED").And.Contain("hook64.dll")
            .And.Contain("1 de ces rapports").And.Contain("ne désignent pas de coupable");
    }

    [Fact]
    public void Dossier_absent_ne_dit_rien_et_dossier_illisible_est_non_lu()
    {
        RapportsDePlantageCod.Juger([], []).Should().BeEmpty();
        RapportsDePlantageCod.Juger(null, []).Single().Statut.Should().Be(RepairStatus.Unknown);
        RapportsDePlantageCod.Lire(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), DateTimeOffset.MinValue).Should().BeEmpty();
    }

    // La taille qu'une entrée zip déclare peut mentir : la lecture compte elle-même les octets lus.
    [Fact]
    public void Une_entree_plus_longue_que_son_plafond_n_est_pas_lue_quoi_qu_elle_declare()
    {
        RapportsDePlantageCod.LireBorne(new MemoryStream(new byte[101]), 100).Should().BeNull();
        RapportsDePlantageCod.LireBorne(new MemoryStream("{}"u8.ToArray()), 100).Should().Be("{}");
    }

    // L'archive est lue en mémoire, jamais extraite ; une archive trop grosse n'est pas ouverte.
    [Fact]
    public void Une_archive_est_lue_sans_extraction_et_une_archive_trop_grosse_est_ignoree()
    {
        var dossier = Directory.CreateTempSubdirectory().FullName;
        using (var z = ZipFile.Open(Path.Combine(dossier, "cod_1.zip"), ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("info.json").Open())) w.Write(Info);
            using (var w = new StreamWriter(z.CreateEntry("stacktrace_crash.csv").Open())) w.Write(Pile);
        }
        File.WriteAllBytes(Path.Combine(dossier, "cod_2.zip"), new byte[RapportsDePlantageCod.ArchiveMax + 1]);
        RapportsDePlantageCod.Lire(dossier, DateTimeOffset.MinValue).Should().ContainSingle();
        Directory.EnumerateFileSystemEntries(dossier).Should().HaveCount(2, "rien n'a été extrait");
    }
}
