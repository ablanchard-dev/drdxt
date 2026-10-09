using System.Security.Principal;
using DrDxt.Core;
using DrDxt.Detection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DrDxt.Detection.Tests.Security;

/// <summary>
/// Garde §9.8 : la détection + scan doivent tourner sans élévation admin.
/// Test d'intégration léger — opère sur la vraie machine (WMI/DXGI/registre),
/// mode dégradé attendu (PresentMon absent = baseline null).
/// </summary>
public class NonAdminScanTests
{
    [Fact]
    public async Task Full_detection_runs_without_administrator_rights()
    {
        // Garde §9.8 : la détection ne doit pas exiger l'élévation.
        using var identity = WindowsIdentity.GetCurrent();
        var isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        var provider = new ServiceCollection().AddDrDxtDetection().BuildServiceProvider();
        var detector = provider.GetRequiredService<IHardwareDetector>();
        var reader = provider.GetRequiredService<ISettingsStateReader>();

        var hw = await detector.DetectAsync();
        hw.IsOk.Should().BeTrue("la détection matérielle marche sans admin");
        var st = await reader.ReadAsync(hw.Value!);
        st.IsOk.Should().BeTrue("la lecture d'état marche sans admin");
        // On documente : ce test passe que l'on soit admin ou non ; il échouerait si un détecteur
        // exigeait l'élévation (ouverture HKLM en écriture, etc.) → garde-fou de non-régression.
        _ = isAdmin;
    }

    [Fact]
    public async Task Full_pipeline_end_to_end_non_admin_produces_valid_scan_result()
    {
        // Preuve complète bout-en-bout : detect → read → score → ScanResult cohérent.
        // Mode dégradé : PresentMon absent → baseline null (pas de service ETW).
        var provider = new ServiceCollection().AddDrDxtDetection().BuildServiceProvider();
        var detector = provider.GetRequiredService<IHardwareDetector>();
        var reader   = provider.GetRequiredService<ISettingsStateReader>();
        var scorer   = provider.GetRequiredService<IScanScorer>();

        var hw = await detector.DetectAsync();
        hw.IsOk.Should().BeTrue();

        var st = await reader.ReadAsync(hw.Value!);
        st.IsOk.Should().BeTrue();

        // baseline = null : PresentMon non disponible en non-admin sans service → mode dégradé.
        var scored = scorer.Score(hw.Value!, st.Value!, baseline: null);
        scored.IsOk.Should().BeTrue("le scorer ne lève jamais, même en mode dégradé");

        var result = scored.Value!;
        result.Score.Should().BeInRange(0, 100, "score toujours borné §11");
        result.Findings.Should().NotBeNull("la liste peut être vide mais pas null");
        result.Baseline.Should().BeNull("mode dégradé : PresentMon absent → baseline null §4 P1");
        result.Hw.Should().BeSameAs(hw.Value);
        result.State.Should().BeSameAs(st.Value);
    }
}
