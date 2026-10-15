using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// Le test mémoire de Windows au prochain démarrage, une seule fois : `bcdedit /bootsequence {memdiag}`, ce que fait
// mdsched. Le gestionnaire de démarrage efface la séquence après l'avoir suivie ; l'annuler avant, c'est la retirer.
public sealed class TestMemoireAuDemarrageTests
{
    private sealed class Bcd
    {
        public bool EntreeMemdiag { get; init; } = true;
        public string? Sequence { get; set; }
        public bool EcritureIgnoree { get; init; }
        public List<string> Appels { get; } = [];

        public (int, string) Run(string[] a)
        {
            Appels.Add(string.Join(" ", a));
            switch (a[0])
            {
                case "/bootsequence":
                    if (!EcritureIgnoree) Sequence = a[1];
                    return (0, "");
                case "/deletevalue" when a is [_, "{bootmgr}", "bootsequence"]:
                    var avait = Sequence is not null;
                    Sequence = null;
                    return (avait ? 0 : 1, "");
                case "/enum" when a[1] == "{memdiag}":
                    return EntreeMemdiag ? (0, "identificateur          {memdiag}\npath                    \\EFI\\Microsoft\\Boot\\memtest.efi") : (1, "");
                case "/enum" when a[1] == "{bootmgr}":
                    return (0, "identificateur          {bootmgr}\ntoolsdisplayorder       {memdiag}"
                               + (Sequence is null ? "" : $"\nbootsequence            {Sequence}"));
                default:
                    return (1, "");
            }
        }
    }

    [Fact]
    public void Le_test_memoire_est_demande_pour_le_prochain_demarrage_et_relu()
    {
        var bcd = new Bcd();

        new Win32BcdActions(bcd.Run).ScheduleMemoryDiagnostic().IsOk.Should().BeTrue();

        bcd.Sequence.Should().Be("{memdiag}");
        bcd.Appels.Should().Contain("/bootsequence {memdiag}");
    }

    [Fact]
    public void Sans_entree_memdiag_rien_n_est_ecrit()
    {
        var bcd = new Bcd { EntreeMemdiag = false };

        new Win32BcdActions(bcd.Run).ScheduleMemoryDiagnostic().IsOk.Should().BeFalse();

        bcd.Appels.Should().NotContain(c => c.StartsWith("/bootsequence"), "une séquence vers une entrée absente ne démarrerait rien");
    }

    [Fact]
    public void Une_sequence_que_la_relecture_ne_voit_pas_est_un_echec()
        => new Win32BcdActions(new Bcd { EcritureIgnoree = true }.Run).ScheduleMemoryDiagnostic().IsOk.Should().BeFalse();

    [Fact]
    public void Annuler_retire_la_sequence_et_le_confirme()
    {
        var bcd = new Bcd { Sequence = "{memdiag}" };

        new Win32BcdActions(bcd.Run).CancelMemoryDiagnostic().IsOk.Should().BeTrue();

        bcd.Sequence.Should().BeNull();
    }

    [Fact]
    public void Annuler_sans_rien_en_attente_est_l_etat_voulu()
        => new Win32BcdActions(new Bcd().Run).CancelMemoryDiagnostic().IsOk.Should().BeTrue();

    // La séquence unique d'un autre outil (récupération, firmware) n'est ni écrasée ni retirée.
    [Fact]
    public void La_sequence_d_un_autre_outil_reste()
    {
        var bcd = new Bcd { Sequence = "{a1b2c3d4-0000-0000-0000-000000000000}" };

        new Win32BcdActions(bcd.Run).ScheduleMemoryDiagnostic().IsOk.Should().BeFalse();
        new Win32BcdActions(bcd.Run).CancelMemoryDiagnostic().IsOk.Should().BeFalse();

        bcd.Sequence.Should().Be("{a1b2c3d4-0000-0000-0000-000000000000}");
        bcd.Appels.Should().NotContain(c => c.StartsWith("/bootsequence") || c.StartsWith("/deletevalue"));
    }

    [Fact]
    public void L_allowlist_laisse_passer_les_deux_gestes_sans_parametre()
    {
        new OpAllowlist().Validate(new ScheduleMemoryDiagnosticOp()).IsOk.Should().BeTrue();
        new OpAllowlist().Validate(new CancelMemoryDiagnosticOp()).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Les_deux_gestes_traversent_le_codec()
    {
        PrivilegedOpCodec.Deserialize(PrivilegedOpCodec.Serialize(new ScheduleMemoryDiagnosticOp()).Value!).Value
            .Should().BeOfType<ScheduleMemoryDiagnosticOp>();
        PrivilegedOpCodec.Deserialize(PrivilegedOpCodec.Serialize(new CancelMemoryDiagnosticOp()).Value!).Value
            .Should().BeOfType<CancelMemoryDiagnosticOp>();
    }
}
