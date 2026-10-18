using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution.Adlx;
using DrDxt.Detection.Gpu.Adlx;
using FluentAssertions;
using Microsoft.Win32;

namespace DrDxt.ElevatedHelper.Tests;

// Retenue par démarrage seulement, l'origine de la MaxFreq suivait la valeur posée par DrDXT quand le pilote la garde au
// redémarrage : +100 MHz par démarrage. Le natif est simulé, la mémoire vit sous HKCU, et un redémarrage efface la clé
// volatile comme Windows le fait.
public sealed class AdlxOrigineEntreDemarragesTests : IDisposable
{
    private readonly string _chemin = @"Software\DrDXT-Tests\" + Guid.NewGuid().ToString("N");
    public void Dispose() { try { Registry.CurrentUser.DeleteSubKeyTree(_chemin, false); } catch { } }

    private readonly FakeAdlxNative _carte = new()
    {
        GfxReads = new AdlxGfxReads { MaxFreqMhz = 2900, VoltMv = 1100, MaxFreqRange = new AdlxIntRange { Min = 500, Max = 4000, Step = 1 } },
    };

    private ADLXTuningActions Helper(Func<int, string?>? identite = null)
    {
        var origine = new OrigineAdlx(identite ?? (_ => "0|PCI\\VEN_1002&DEV_7550"), Registry.CurrentUser, _chemin);
        return new ADLXTuningActions(_carte, origine.Retenir, origine.Noter);
    }

    private void Redemarrer() => Registry.CurrentUser.DeleteSubKeyTree(_chemin + @"\CeDemarrage", false);

    [Fact]
    public void Plus_100_redemarrage_plus_100_est_refuse()
    {
        Helper().SetTuning(0, AdlxTuningKind.Undervolt, 3000).IsOk.Should().BeTrue();
        Redemarrer();   // le pilote garde 3000

        var r = Helper().SetTuning(0, AdlxTuningKind.Undervolt, 3100);

        r.IsOk.Should().BeFalse("l'origine reste 2900 d'un démarrage à l'autre");
        _carte.GfxReads.MaxFreqMhz.Should().Be(3000);
    }

    [Fact]
    public void Un_reglage_du_joueur_entre_deux_demarrages_devient_la_nouvelle_origine()
    {
        Helper().SetTuning(0, AdlxTuningKind.Undervolt, 3000).IsOk.Should().BeTrue();
        Redemarrer();
        _carte.GfxReads = _carte.GfxReads with { MaxFreqMhz = 2600 };   // réglée dans AMD Software

        Helper().SetTuning(0, AdlxTuningKind.Undervolt, 2500).IsOk.Should().BeTrue("2500 est dans la bande autour de 2600, pas de 2900");
    }

    [Fact]
    public void Revenir_a_l_origine_puis_redemarrer_garde_l_origine()
    {
        Helper().SetTuning(0, AdlxTuningKind.Undervolt, 3000).IsOk.Should().BeTrue();
        Helper().SetTuning(0, AdlxTuningKind.Undervolt, 2900).IsOk.Should().BeTrue();
        Redemarrer();

        Helper().SetTuning(0, AdlxTuningKind.Undervolt, 3000).IsOk.Should().BeTrue();
        Helper().SetTuning(0, AdlxTuningKind.Undervolt, 3001).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Carte_non_identifiee_rien_n_est_ecrit()
    {
        Helper(_ => null).SetTuning(0, AdlxTuningKind.Undervolt, 3000).IsOk.Should().BeFalse();

        _carte.SetGfxMaxFreqMhz.Should().BeNull();
    }

    [Fact]
    public void Origine_non_notee_rien_n_est_ecrit()
    {
        var origine = new OrigineAdlx(_ => "carte", Registry.CurrentUser, _chemin);
        var helper = new ADLXTuningActions(_carte, origine.Retenir, (_, _, _) => false);

        helper.SetTuning(0, AdlxTuningKind.Undervolt, 3000).IsOk.Should().BeFalse();
        _carte.SetGfxMaxFreqMhz.Should().BeNull();
    }

    // La désinstallation retire HKLM\Software\DrDXT, donc l'origine gardée.
    [Fact]
    public void La_desinstallation_retire_l_origine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        File.ReadAllText(Path.Combine(d!.FullName, "installer", "DrDXT.iss"))
            .Should().Contain("Root: HKLM; Subkey: \"Software\\DrDXT\"; Flags: uninsdeletekey");
    }
}
