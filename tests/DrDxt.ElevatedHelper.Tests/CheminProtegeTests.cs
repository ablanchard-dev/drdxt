using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// 25/09/2026 (revue de sécurité) — sur de vrais chemins de la machine, pas des ACL fabriquées.
public sealed class CheminProtegeTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), $"drdxt-chemin-{Guid.NewGuid():N}");

    public CheminProtegeTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { /* dossier temporaire du test */ }
    }

    [Fact]
    public void Un_executable_de_System32_est_protege()
        => CheminProtege.Verifier(Path.Combine(Environment.SystemDirectory, "sc.exe")).Should().BeNull();

    [Fact]
    public void Un_executable_sous_le_profil_n_est_pas_protege()
    {
        var exe = Path.Combine(_dossier, "PresentMonService.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        CheminProtege.Verifier(exe).Should().NotBeNull("le compte peut le remplacer : pas de démarrage en SYSTEM");
    }

    [Fact]
    public void Un_executable_sous_Program_Files_est_protege()
    {
        // L'installation par défaut de DrDXT vit sous Program Files : ce contrôle doit y passer.
        var exe = @"C:\Program Files\Windows Defender\MpCmdRun.exe";
        if (!File.Exists(exe)) return;
        CheminProtege.Verifier(exe).Should().BeNull();
    }

    [Fact]
    public void Un_executable_absent_n_est_pas_une_preuve()
        => CheminProtege.Verifier(Path.Combine(_dossier, "absent.exe")).Should().NotBeNull();
}
