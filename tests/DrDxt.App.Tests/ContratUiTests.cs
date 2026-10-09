using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// 06/10/2026 (chantier B0, sortie S1-S3) : le back est livré prêt pour l'UI. Chaque constat porte ses données
/// (S1), aucun texte métier ne vit dans les vues (S2), et la surface des ViewModels est gelée dans un fichier que l'UI
/// lit (S3). Changer ce fichier = un commit nommé « Contrat UI : … ».
/// </summary>
public class ContratUiTests
{
    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    // ── S1 ────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<HardwareInventory> Machines()
    {
        // Mal réglée (AMD X3D, une barrette), dégénérée (rien de lisible), Intel portable : trois familles de constats.
        yield return new HardwareInventory(
            new CpuInfo("AMD Ryzen 9 7950X3D", "AMD", 16, 32, true, 2),
            new[] { new GpuInfo("AMD Radeon RX 7900 XT", "AMD", 20L << 30, "1", TriState.Off) },
            new[] { new MemoryModule("PN", "G.Skill", 4800, 6000, DeviceLocator: "Controller0-DIMMA2") },
            new MotherboardInfo("ASUS", "X670E"), new BiosInfo("2", DateTimeOffset.UtcNow.AddYears(-3), true),
            ChassisType.Desktop);
        yield return new HardwareInventory(
            new CpuInfo("Unknown CPU", "Unknown", 0, 0, false, 0), Array.Empty<GpuInfo>(), Array.Empty<MemoryModule>(),
            new MotherboardInfo("Unknown", "Unknown"), new BiosInfo("0", null, false), ChassisType.Unknown);
        yield return new HardwareInventory(
            new CpuInfo("Intel Core i5-1235U", "GenuineIntel", 10, 12, false, 1),
            new[] { new GpuInfo("Intel Iris Xe Graphics", "Intel", 1L << 30, "31.0.101.4502", TriState.Unknown) },
            new[] { new MemoryModule("PN", "Samsung", 3200, 3200, DeviceLocator: "ChannelA-DIMM0") },
            new MotherboardInfo("LENOVO", "21C1"), new BiosInfo("1.2", DateTimeOffset.UtcNow, true), ChassisType.Laptop);
    }

    private static readonly SettingsState[] Etats =
    [
        new(TriState.Off, TriState.Off, "381b4222-f694-41f0-9685-ff5bb260df2e", TriState.Off, TriState.Off, [], TriState.Off),
        new(TriState.Unknown, TriState.Unknown, "", TriState.Unknown, TriState.Unknown, [], TriState.Unknown),
    ];

    private static List<Constat> ConstatsProduits()
    {
        var constats = new List<Constat>();
        foreach (var hw in Machines())
            foreach (var st in Etats)
            {
                if (new ScanScorer().Score(hw, st, null) is { IsOk: true } r)
                    constats.AddRange(r.Value!.Findings.Select(Constat.Depuis));
                if (new DrDxt.Bios.BiosAdvisor().Advise(hw, st) is { IsOk: true } b)
                    constats.AddRange(b.Value!.Select(Constat.Depuis));
            }
        // La santé se rejoue sur les fixtures (B8a) ; d'ici là, un constat de chaque forme que le docteur produit.
        constats.Add(Constat.Depuis(new RepairFinding("STABILITE", "arrets brutaux", "11 sur 90 jours", null, RepairStatus.Suspicious, "x")));
        constats.Add(Constat.Depuis(new RepairFinding("THERMIQUE", "temperature du processeur", null, null, RepairStatus.Unknown, "x")));
        constats.Add(Constat.Depuis(new RepairFinding("STABILITE", "point de restauration sain", "« a »", null, RepairStatus.Observed, "x", RepairAction.Recommandation)));
        return constats;
    }

    [Fact]
    public void Chaque_constat_porte_ses_donnees()
    {
        var constats = ConstatsProduits();

        // Témoin : un crible vert sur zéro constat ne prouve rien.
        constats.Select(c => c.Famille).Distinct().Should().BeEquivalentTo(["scan", "bios", "sante"]);
        constats.Should().HaveCountGreaterThan(10);
        constats.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.Categorie)
                                          && !string.IsNullOrWhiteSpace(c.Titre));
        constats.Where(c => c.Etat == EtatConstat.Lu && c.Famille == "sante")
            .Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Valeur) && !string.IsNullOrWhiteSpace(c.Pourquoi));
        constats.Where(c => c.Etat == EtatConstat.Lu && c.Famille == "bios")
            .Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Pourquoi));
    }

    [Fact]
    public void Un_constat_non_lu_se_dit_non_lu_dans_chaque_famille()
    {
        Constat.Depuis(new RepairFinding("THERMIQUE", "temperature du processeur", null, null, RepairStatus.Unknown, "x"))
            .Etat.Should().Be(EtatConstat.NonLu);
        Constat.Depuis(new Finding("Gpu", Severity.Info, "Finding.Gpu.ReBarUnknown", null)).Etat.Should().Be(EtatConstat.NonLu);
        Constat.Depuis(new Finding("Gpu", Severity.Minor, "Finding.Gpu.ReBarOff", 3)).Etat.Should().Be(EtatConstat.Lu);
    }

    // Revue du 06/10 (F-6) : « non lu » se reconnaît au suffixe « Unknown » de la clé ; la convention est gardée par la
    // liste des clés qui disent elles-mêmes ne pas savoir, et par une clé hors liste qui reste « lu ».
    [Theory]
    [InlineData("Finding.Display.RefreshUnknown")]
    [InlineData("Finding.Gpu.IntelRebarUnknown")]
    [InlineData("Finding.Gpu.ReBarUnknown")]
    [InlineData("Finding.Latency.VrrInactiveSupportUnknown")]
    [InlineData("Finding.Latency.VrrUnknown")]
    [InlineData("Finding.Memory.ChannelTopologyUnknown")]
    [InlineData("Finding.Memory.SlotPositionUnknown")]
    [InlineData("Finding.Scheduling.X3dUnknown")]
    public void Les_cles_qui_disent_ne_pas_savoir_sont_non_lues(string cle)
        => Constat.Depuis(new Finding("x", Severity.Info, cle, null)).Etat.Should().Be(EtatConstat.NonLu);

    [Fact]
    public void Les_cles_non_lues_sont_exactement_celles_de_la_liste()
    {
        var cles = typeof(ContratUiTests).GetMethod(nameof(Les_cles_qui_disent_ne_pas_savoir_sont_non_lues))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false).Cast<InlineDataAttribute>()
            .Select(a => (string)a.GetData(null!).Single()[0]).ToHashSet();
        var resx = System.Xml.Linq.XDocument.Load(Path.Combine(Racine(), "src", "DrDxt.Infrastructure", "Resources", "Strings.resx"))
            .Descendants("data").Select(d => (string)d.Attribute("name")!).Where(n => n.StartsWith("Finding.", StringComparison.Ordinal)
                && !n.StartsWith("Finding.Cat.", StringComparison.Ordinal)).ToList();

        resx.Should().HaveCountGreaterThan(20, "le crible doit avoir lu les vraies clés");
        resx.Where(n => Constat.Depuis(new Finding("x", Severity.Info, n, null)).Etat == EtatConstat.NonLu)
            .Should().BeEquivalentTo(cles, "une clé « non lue » nouvelle ou renommée se décide ici");
    }

    // ── S2 ────────────────────────────────────────────────────────────────────────────

    private static readonly Regex TexteEnDur = new(@"(?<![A-Za-z])(?:Text|Content|ToolTip|Header|Title)=""(?!\{)([^""]{13,})""");

    [Fact]
    public void Aucun_texte_metier_en_dur_dans_les_vues()
    {
        var xaml = Directory.EnumerateFiles(Path.Combine(Racine(), "src", "DrDxt.App"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")).ToList();

        xaml.Should().HaveCountGreaterThan(3, "le crible doit avoir lu les vraies vues");
        xaml.SelectMany(f => TexteEnDur.Matches(File.ReadAllText(f)).Select(m => $"{Path.GetFileName(f)} : {m.Groups[1].Value}"))
            .Should().BeEmpty("un texte affiché vit dans les resx, pas dans la vue");
    }

    [Fact]
    public void Le_code_derriere_les_vues_reste_court()
    {
        var fichiers = Directory.EnumerateFiles(Path.Combine(Racine(), "src", "DrDxt.App"), "*.xaml.cs", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "App.xaml.cs").ToList();

        fichiers.Should().NotBeEmpty();
        fichiers.Where(f => File.ReadAllLines(f).Length > 30).Should().BeEmpty("la logique vit dans les ViewModels");
    }

    // ── S3 ────────────────────────────────────────────────────────────────────────────

    internal static string Surface()
    {
        var sb = new StringBuilder();
        var types = typeof(ViewModels.ScanViewModel).Assembly.GetTypes()
            // Revue du 06/10 (F-5) : les types imbriqués publics, et les valeurs d'une énumération (les états que l'UI
            // aura à montrer).
            .Where(t => (t.IsPublic || t.IsNestedPublic) && t.Namespace == "DrDxt.App.ViewModels")
            .OrderBy(t => t.FullName, StringComparer.Ordinal);
        foreach (var t in types)
        {
            sb.Append(t.FullName![(t.Namespace!.Length + 1)..].Replace('+', '.')).Append('\n');
            if (t.IsEnum)
            {
                sb.Append("  = ").Append(string.Join(", ", Enum.GetNames(t))).Append('\n');
                continue;
            }
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                         .OrderBy(p => p.Name, StringComparer.Ordinal))
                sb.Append("  ").Append(p.Name).Append(" : ").Append(Nom(p.PropertyType)).Append('\n');
        }
        return sb.ToString();
    }

    private static string Nom(Type t) => !t.IsGenericType ? t.Name
        : $"{t.Name[..t.Name.IndexOf('`')]}<{string.Join(", ", t.GetGenericArguments().Select(Nom))}>";

    [Fact]
    public void La_surface_des_ViewModels_est_celle_du_contrat()
    {
        var fichier = Path.Combine(Racine(), "tests", "DrDxt.App.Tests", "contrats-viewmodels.txt");
        // Régénérer exprès : DRDXT_CONTRAT_UI=ecrire, puis un commit « Contrat UI : … ».
        if (Environment.GetEnvironmentVariable("DRDXT_CONTRAT_UI") == "ecrire")
        {
            File.WriteAllText(fichier, Surface());
            // Revue du 06/10 (F-7) : écrire puis comparer serait vert à coup sûr.
            Assert.Fail("contrat régénéré : relancer sans DRDXT_CONTRAT_UI");
        }

        File.Exists(fichier).Should().BeTrue();
        Surface().Should().Be(File.ReadAllText(fichier).Replace("\r\n", "\n"),
            "l'UI se construit sur ce contrat : un changement de ViewModel se décide, il ne glisse pas");
    }
}
