using System.Globalization;
using DrDxt.Core;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.Infrastructure.Tests;

public sealed class ResxLocalizerTests
{
    // -----------------------------------------------------------------------
    // Tests de construction directe
    // -----------------------------------------------------------------------

    [Fact]
    public void Get_KnownKey_ScanTitle_ReturnsEnglish()
    {
        var localizer = new ResxLocalizer();
        var result = localizer.Get("Scan_Title", new CultureInfo("en"));
        result.Should().Be("Scan your PC");
    }

    [Fact]
    public void Get_KnownKey_ScanTitle_ReturnsFrench()
    {
        var localizer = new ResxLocalizer();
        var result = localizer.Get("Scan_Title", new CultureInfo("fr"));
        result.Should().Be("Analyse ton PC");
    }

    [Fact]
    public void Get_ScanTitle_EnAndFrAreDifferent()
    {
        var localizer = new ResxLocalizer();
        var en = localizer.Get("Scan_Title", new CultureInfo("en"));
        var fr = localizer.Get("Scan_Title", new CultureInfo("fr"));
        en.Should().NotBe(fr, "satellite assembly must produce distinct FR text");
    }

    [Fact]
    public void Get_KnownKey_CommonCancel_ReturnsEnglish()
    {
        var localizer = new ResxLocalizer();
        var result = localizer.Get("Common_Cancel", new CultureInfo("en"));
        result.Should().Be("Cancel");
    }

    [Fact]
    public void Get_KnownKey_CommonCancel_ReturnsFrench()
    {
        var localizer = new ResxLocalizer();
        var result = localizer.Get("Common_Cancel", new CultureInfo("fr"));
        result.Should().Be("Annuler");
    }

    [Fact]
    public void Get_KnownKey_CommonOk_BothCultures()
    {
        var localizer = new ResxLocalizer();
        localizer.Get("Common_Ok", new CultureInfo("en")).Should().Be("OK");
        localizer.Get("Common_Ok", new CultureInfo("fr")).Should().Be("OK");
    }

    // Le score ne doit pas revenir par le catalogue : un plancher mathematique a 47 le rend
    // incapable de dire qu'une machine est mal reglee. La regle vaut pour les deux langues et pour les deux formes de cle.
    [Theory]
    [InlineData("Scan_Score_Label")]
    [InlineData("scan.score.label")]
    public void Get_ScoreKey_IsGoneFromBothCatalogues(string key)
    {
        var localizer = new ResxLocalizer();

        localizer.Get(key, new CultureInfo("fr")).Should().Be(key,
            "une cle absente se rend elle-meme : le score ne doit plus avoir de libelle");
        localizer.Get(key, new CultureInfo("en")).Should().Be(key);
    }

    [Fact]
    public void Get_MissingKey_ReturnsKeyItself()
    {
        var localizer = new ResxLocalizer();
        var result = localizer.Get("No_Such_Key", new CultureInfo("en"));
        result.Should().Be("No_Such_Key", "missing key must fall back to the key string, never throw");
    }

    [Fact]
    public void Get_MissingKey_WithFrCulture_ReturnsKeyItself()
    {
        var localizer = new ResxLocalizer();
        var result = localizer.Get("No_Such_Key", new CultureInfo("fr"));
        result.Should().Be("No_Such_Key");
    }

    [Fact]
    public void Get_NullCulture_UsesCurrentUICulture_DoesNotThrow()
    {
        var localizer = new ResxLocalizer();
        var saved = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en");
            var result = localizer.Get("Common_Ok", null);
            result.Should().NotBeNullOrEmpty("null culture must fall back to CurrentUICulture without throwing");
        }
        finally
        {
            CultureInfo.CurrentUICulture = saved;
        }
    }

    // -----------------------------------------------------------------------
    // DI tests
    // -----------------------------------------------------------------------

    [Fact]
    public void DI_AddDrDxtCore_ResolvesILocalizer()
    {
        var services = new ServiceCollection();
        services.AddDrDxtCore(
            diagnosticsDirectory: Path.Combine(Path.GetTempPath(), "drdxt-loc-test-" + Guid.NewGuid()),
            logDirectory:         Path.Combine(Path.GetTempPath(), "drdxt-loc-log-"  + Guid.NewGuid()));

        using var provider = services.BuildServiceProvider();
        var localizer = provider.GetRequiredService<ILocalizer>();

        localizer.Should().NotBeNull();
        localizer.Should().BeOfType<ResxLocalizer>();
    }

    [Fact]
    public void DI_ILocalizer_IsSingleton()
    {
        var services = new ServiceCollection();
        services.AddDrDxtCore(
            diagnosticsDirectory: Path.Combine(Path.GetTempPath(), "drdxt-loc-test-" + Guid.NewGuid()),
            logDirectory:         Path.Combine(Path.GetTempPath(), "drdxt-loc-log-"  + Guid.NewGuid()));

        using var provider = services.BuildServiceProvider();
        var a = provider.GetRequiredService<ILocalizer>();
        var b = provider.GetRequiredService<ILocalizer>();

        a.Should().BeSameAs(b);
    }

    [Fact]
    public void DI_ILocalizer_ReturnsCorrectFrenchTranslation()
    {
        var services = new ServiceCollection();
        services.AddDrDxtCore(
            diagnosticsDirectory: Path.Combine(Path.GetTempPath(), "drdxt-loc-test-" + Guid.NewGuid()),
            logDirectory:         Path.Combine(Path.GetTempPath(), "drdxt-loc-log-"  + Guid.NewGuid()));

        using var provider = services.BuildServiceProvider();
        var localizer = provider.GetRequiredService<ILocalizer>();

        var fr = localizer.Get("Scan_Title", new CultureInfo("fr"));
        fr.Should().Be("Analyse ton PC");
    }
}
