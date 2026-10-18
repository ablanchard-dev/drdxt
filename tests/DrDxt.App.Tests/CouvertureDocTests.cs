using System.IO;
using System.Text;
using DrDxt.Application.Tweaks;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.App.Tests;

/// <summary>
/// Docs/COUVERTURE.md, la liste « ce que DrDXT fait sur quel PC », générée depuis le code et jamais
/// écrite à la main. Ce test la recalcule ; si le fichier diverge, il échoue. Régénérer exprès : DRDXT_COUVERTURE=ecrire.
/// </summary>
public sealed class CouvertureDocTests
{
    private static readonly SettingsState Etat = new(TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On);

    private static HardwareInventory Pc(string cpu, string vendeurCpu, bool x3d, int ccd, string gpu, string vendeurGpu, long vram,
        ChassisType chassis) => new(new CpuInfo(cpu, vendeurCpu, 8, 16, x3d, ccd),
        new[] { new GpuInfo(gpu, vendeurGpu, vram, "1", TriState.On) },
        new[] { new MemoryModule("PN", "K", 3200, 3200) }, new MotherboardInfo("M", "B"), new BiosInfo("1", null, true), chassis);

    private static readonly (string Nom, HardwareInventory Hw)[] Machines =
    [
        ("Fixe Intel + NVIDIA", Pc("Core i7-14700F", "Intel", false, 1, "NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, ChassisType.Desktop)),
        ("Fixe Ryzen X3D + AMD", Pc("Ryzen 9 7950X3D", "AMD", true, 2, "AMD Radeon RX 7900 XT", "AMD", 20L << 30, ChassisType.Desktop)),
        ("Fixe, carte Intel intégrée", Pc("Core i5-13400", "Intel", false, 1, "Intel UHD Graphics 730", "Intel", 0, ChassisType.Desktop)),
        ("Portable NVIDIA", Pc("Core i7-13700H", "Intel", false, 1, "NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA", 8L << 30, ChassisType.Laptop)),
        ("Console portable", Pc("Ryzen Z1 Extreme", "AMD", false, 1, "AMD Radeon 780M", "AMD", 0, ChassisType.Handheld)),
    ];

    private static string Fr(string cle) => new DrDxt.Infrastructure.Localization.ResxLocalizer().Get(cle, new System.Globalization.CultureInfo("fr"));

    internal static string Generer(IReadOnlyList<ITweak> catalogue)
    {
        // Le catalogue et les gestes qui vivent hors de lui mais ont leur page (le repos de la carte) : la liste promet
        // « réglage par réglage ».
        var ids = catalogue.Select(t => t.Id).Union(CouvertureDesLeviers.ReglagesDecrits, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("# Ce que DrDXT fait, selon le PC");
        sb.AppendLine();
        sb.AppendLine("Généré depuis le code par `CouvertureDocTests` : ne pas modifier à la main. « possible » = le levier existe");
        sb.AppendLine("sur ce matériel ; il n'est proposé que si la machine lue en a besoin, et mesuré en jeu quand il touche les");
        sb.AppendLine("images. Sinon, la raison.");
        sb.AppendLine();
        sb.AppendLine("| Levier | Page | " + string.Join(" | ", Machines.Select(m => m.Nom)) + " |");
        sb.AppendLine("|---|---|" + string.Concat(Machines.Select(_ => "---|")));
        foreach (var id in ids.OrderBy(CouvertureDesLeviers.Domaine, StringComparer.Ordinal).ThenBy(i => i, StringComparer.Ordinal))
        {
            var cases = Machines.Select(m =>
                CouvertureDesLeviers.Calculer([new Toujours(id)], m.Hw, Etat, new HashSet<string>()).Single() switch
                {
                    { RaisonCle: { } r } => Fr(r),
                    { Statut: StatutDuLevier.Expert } => "mode Expert, sur ton accord",
                    _ => "possible",
                });
            sb.AppendLine($"| {Fr(DrDxt.App.ViewModels.TweakDisplayKeys.TitleKey(id))} (`{id}`) | {CouvertureDesLeviers.Domaine(id)} | "
                          + string.Join(" | ", cases) + " |");
        }
        return sb.ToString();
    }

    /// <summary>Le matériel seul : un levier « applicable » partout, pour ne lire que les raisons de matériel.</summary>
    private sealed class Toujours(string id) : ITweak
    {
        public string Id => id;
        public TweakCategory Category => TweakCategory.Latency;
        public bool RequiresElevation => false;
        public bool RequiresRestorePoint => false;
        public string SummaryKey => "";
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => true;
        public Task<Result<TweakSnapshot>> ApplyAsync() => throw new NotSupportedException();
        public Task<Result> RevertAsync(TweakSnapshot snapshot) => throw new NotSupportedException();
    }

    [Fact]
    public void Le_fichier_de_couverture_est_celui_que_le_code_calcule()
    {
        using var sp = new ServiceCollection().AddDrDxtApp().BuildServiceProvider();
        var attendu = Generer(sp.GetRequiredService<ITweakCatalog>().All());
        attendu.Should().Contain("Portable : ", "témoin : les raisons sont lues en français");

        var racine = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(racine.FullName, "DrDXT.sln"))) racine = racine.Parent!;
        var chemin = Path.Combine(racine.FullName, "docs", "COUVERTURE.md");
        if (Environment.GetEnvironmentVariable("DRDXT_COUVERTURE") == "ecrire")
        {
            File.WriteAllText(chemin, attendu.ReplaceLineEndings("\n"), new UTF8Encoding(false));
            Assert.Fail("couverture régénérée : relancer sans DRDXT_COUVERTURE");
        }
        File.Exists(chemin).Should().BeTrue("docs/COUVERTURE.md se génère avec DRDXT_COUVERTURE=ecrire");
        File.ReadAllText(chemin).ReplaceLineEndings("\n").Should().Be(attendu.ReplaceLineEndings("\n"),
            "la liste publique suit le code ; régénérer avec DRDXT_COUVERTURE=ecrire");
    }
}
