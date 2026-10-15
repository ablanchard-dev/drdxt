using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks;

/// <summary>
/// Des leviers enregistrés qui ne pouvaient jamais
/// s'appliquer (AppliesTo toujours faux) et que l'App montrait quand même. Un levier dont AppliesTo est toujours faux n'a
/// sa place que s'il sert à DÉFAIRE une écriture ancienne, ou s'il attend une ligne nommée du plan ; il est alors listé
/// ici avec sa raison, et la liste ne garde que des leviers réellement morts.
/// </summary>
public sealed class AucunLevierMortTests
{
    private static readonly Dictionary<string, string> Exceptions = new(StringComparer.Ordinal)
    {
        ["NvidiaUltraLowLatencyTweak.cs"] = "latency.reflex : défait seulement l'écriture d'une ancienne version (manifeste, retour au démarrage)",
        // La raison « pas encore construite » était périmée.
        ["GpuFanCurveTweak.cs"] = "gpu.fan_curve : jamais proposé par l'autopilote (ReglagesCarteInterdits) ; le ventilateur s'écrit en mode Expert seulement (VentilateurExpert), et ce levier garde son entrée au catalogue pour la couverture et le retour au démarrage",
    };

    private static readonly Regex ToujoursFaux = new(@"bool\s+AppliesTo\s*\([^)]*\)\s*(=>\s*false\s*;|\{\s*return\s+false\s*;\s*\})", RegexOptions.Compiled);

    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!.FullName;
    }

    private static List<string> LeviersMorts()
        => Directory.GetFiles(Path.Combine(Racine(), "src", "DrDxt.Application", "Tweaks"), "*.cs", SearchOption.AllDirectories)
            .Where(f => ToujoursFaux.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName).OfType<string>().ToList();

    [Fact]
    public void Le_crible_voit_un_AppliesTo_toujours_faux()
    {
        ToujoursFaux.IsMatch("public override bool AppliesTo(HardwareInventory hw, SettingsState state) => false;").Should().BeTrue();
        ToujoursFaux.IsMatch("public bool AppliesTo(HardwareInventory hw, SettingsState s) { return false; }").Should().BeTrue();
        ToujoursFaux.IsMatch("public bool AppliesTo(HardwareInventory hw, SettingsState s) => hw.Gpu is not null;").Should().BeFalse();
    }

    [Fact]
    public void Aucun_levier_ne_reste_mort_sans_raison()
        => LeviersMorts().Where(f => !Exceptions.ContainsKey(f)).Should().BeEmpty(
            "un levier qui ne s'applique jamais est retiré, ou listé ici avec la ligne du plan qui le rouvre");

    [Fact]
    public void Chaque_exception_est_encore_un_levier_mort()
    {
        var morts = LeviersMorts();
        morts.Should().NotBeEmpty("témoin : le crible lit bien les fichiers des leviers");
        Exceptions.Keys.Except(morts).Should().BeEmpty("une exception sans objet se retire de la liste");
    }
}
