using System.Globalization;
using System.IO;
using System.Text.Json;
using DrDxt.App.ViewModels;
using DrDxt.Bios;
using DrDxt.Core;
using FluentAssertions;
using Xunit.Abstractions;

namespace DrDxt.App.Tests;

// Le QR du guide BIOS ouvre la page de DrDXT au téléphone, le guide entier dans le fragment de l'adresse : jamais envoyé
// au serveur. Il tient dans un QR lisible (version 25 au plus) pour chaque modèle de guide du dépôt, au pire cas.
public sealed class GuideBiosParLaPageTests(ITestOutputHelper sortie)
{
    private static string Langue(string cle, string langue)
        => new DrDxt.Infrastructure.Localization.ResxLocalizer().Get(cle, new CultureInfo(langue));

    private sealed class UnSeulModele(GuideTemplate modele) : IGuideLibrary
    {
        public Result<GuideTemplate> Resolve(BiosVendor vendor, string biosUiGeneration, string? board = null, string? biosVersion = null)
            => Result<GuideTemplate>.Ok(modele);
    }

    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    public static TheoryData<int> Modeles()
    {
        var t = new TheoryData<int>();
        for (var i = 0; i < Tous().Count; i++) t.Add(i);
        return t;
    }

    private static IReadOnlyList<GuideTemplate> Tous()
        => GuideTemplateValidator.ParseAndValidate(File.ReadAllText(Path.Combine(Racine(), "src", "DrDxt.Bios", "data", "guides.json"))).Value!;

    private static BiosFinding Constat(FirmwareSettingKind k) => new(k, Severity.Major, "t", "r", BiosMatrixClass.Guide, TriState.Off,
        BiosRecommendedAction.Enable, "a", Gain: null, StateNotReadable: false);

    // Le pire cas : toutes les étapes du pas à pas, la ligne CSM du secours, le lien du manuel et un nom de carte long.
    private static PersonalizedGuide PireCas(GuideTemplate modele)
    {
        var hw = new HardwareInventory(new CpuInfo("CPU", "Intel", 8, 16, false, 1), [new GpuInfo("GPU", "NVIDIA", 8L << 30, "1", TriState.Off)],
            [new MemoryModule("PN", "G.Skill", 3200, 3600)], new MotherboardInfo("Micro-Star International Co., Ltd.", "PRO B760-P WIFI DDR4 (MS-7D98)"),
            new BiosInfo("1.A0", DateTimeOffset.UtcNow, true), ChassisType.Desktop);
        return new GuideGenerator(new UnSeulModele(modele)).Generate(hw,
            [Constat(FirmwareSettingKind.XmpExpo), Constat(FirmwareSettingKind.Above4G), Constat(FirmwareSettingKind.ReBar), Constat(FirmwareSettingKind.Csm)]).Value!;
    }

    // Tant que la page ne répond pas en ligne, un QR qui l'ouvrirait mènerait à une page introuvable : le QR garde le texte
    // brut, lisible sans réseau. PageDuGuide.EnLigne se bascule le jour où GitHub Pages sert docs/.
    [Fact]
    public void Tant_que_la_page_n_est_pas_en_ligne_le_qr_garde_le_texte()
    {
        var g = new BiosGuideViewModel(PireCas(Tous()[0]), c => Langue(c, "fr"));

        PageDuGuide.EnLigne.Should().BeFalse("GitHub Pages n'est pas encore activé sur le dépôt public");
        g.QrVisible.Should().BeTrue();
        g.QrPorteLaPage.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Modeles))]
    public void Chaque_guide_du_depot_ouvre_la_page_dans_un_qr_lisible(int i)
    {
        foreach (var langue in new[] { "fr", "en" })
        {
            using var _ = Docteur.Portee(CultureInfo.GetCultureInfo(langue == "fr" ? "fr-FR" : "en-US"));
            var g = new BiosGuideViewModel(PireCas(Tous()[i]), c => Langue(c, langue));
            g.AdressePage.Should().NotBeNull($"la page accepte le guide {Tous()[i].BiosUiGeneration} en {langue}");
            var modules = BiosGuideViewModel.Modules(g.AdressePage!);
            sortie.WriteLine($"{Tous()[i].Vendor} {Tous()[i].BiosUiGeneration} {langue} : {g.AdressePage!.Length} car., {modules} modules");

            modules.Should().NotBeNull().And.BeLessThanOrEqualTo(BiosGuideViewModel.ModulesMaximum);
            g.AdressePage.Should().StartWith(PageDuGuide.Adresse + "#");
        }
    }

    // Le décodage suit la spécification de la page : base64url sans remplissage, deflate brut, JSON version 1.
    [Fact]
    public void L_adresse_se_relit_selon_la_specification_de_la_page()
    {
        var guide = PireCas(Tous()[0]);
        var g = new BiosGuideViewModel(guide, c => Langue(c, "fr"));
        var fragment = g.AdressePage![(PageDuGuide.Adresse.Length + 1)..];

        fragment.Should().MatchRegex("^[A-Za-z0-9_-]+$", "base64url, sans remplissage : rien à échapper dans une adresse");
        var lu = PageDuGuide.Lire(fragment)!;
        lu.V.Should().Be(1);
        lu.Langue.Should().Be("fr");
        lu.Carte.Should().Be(g.BoardLabel);
        lu.ToucheEntree.Should().Be(g.EnterKey);
        lu.Etapes.Select(e => e[0]).Should().Equal(guide.Steps.Select(s => PageDuGuide.Code(s.InstructionKey)), "une consigne connue de la page voyage par son code");
        lu.Etapes.Select(e => e.ElementAtOrDefault(1)).Should().Equal(g.Steps.Select(s => s.ExactValue is { Length: > 0 } v ? v : null));
        lu.SecoursHaut.Should().Equal(guide.Recovery.Top.Select(s => PageDuGuide.Code(s.InstructionKey)));
        lu.SecoursBas.Should().BeNull("le bas est le même que le haut : porté une seule fois");
        lu.Manuel.Should().Be("https://www.msi.com/Motherboard/PRO-B760-P-WIFI-DDR4/support");
    }

    [Theory]
    [InlineData("")]
    [InlineData("pas du base64 !")]
    [InlineData("AAAA")]
    public void Un_fragment_invalide_ne_se_lit_pas(string fragment) => PageDuGuide.Lire(fragment).Should().BeNull();

    private static string Page() => File.ReadAllText(Path.Combine(Racine(), "docs", "guide-bios", "index.html"));

    // La page dit chaque phrase fixe comme l'App : ses textes sont ceux des fichiers de traduction, en français et en
    // anglais, sous le code que l'adresse porte.
    [Fact]
    public void Les_phrases_de_la_page_sont_celles_de_l_App()
    {
        var bloc = System.Text.RegularExpressions.Regex.Match(Page(), "<script type=\"application/json\" id=\"textes\">(.*?)</script>",
            System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        using var textes = JsonDocument.Parse(bloc);

        PageDuGuide.ClesDeLaPage.Select(PageDuGuide.Code).Should().OnlyHaveUniqueItems();
        foreach (var langue in new[] { "fr", "en" })
            foreach (var cle in PageDuGuide.ClesDeLaPage)
                textes.RootElement.GetProperty(langue).GetProperty(PageDuGuide.Code(cle)).GetString()
                    .Should().Be(Langue(cle, langue), $"{cle} en {langue} : recopier la phrase du fichier de traduction dans la page");
    }

    // Autonome et muette : rien de chargé ailleurs, aucun appel, le seul script autorisé par son empreinte, et rien
    // d'écrit comme du HTML (un guide forgé ne peut pas injecter de balise).
    [Fact]
    public void La_page_ne_charge_rien_et_n_appelle_rien()
    {
        var page = Page();
        var script = System.Text.RegularExpressions.Regex.Match(page, "<script>(.*?)</script>", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        var empreinte = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(script)));

        page.Should().Contain($"script-src 'sha256-{empreinte}'", "l'empreinte de la politique suit le script ; la recalculer après une retouche")
            .And.Contain("connect-src 'none'").And.Contain("default-src 'none'").And.Contain("\"deflate-raw\"");
        page.Should().NotMatchRegex("(src|href)=\"(https?:)?//", "aucune ressource ni lien fixe vers ailleurs");
        foreach (var interdit in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "fetch(", "XMLHttpRequest", "WebSocket", "sendBeacon", "import(" })
            script.Should().NotContain(interdit);
    }

    // Une adresse se forge : la page n'affiche que des phrases à elle, sous des règles strictes, et ce qu'elle refuserait
    // n'est jamais encodé (le QR garde alors le texte brut).
    private static PersonalizedGuide AvecEtape(string cle, string valeur)
        => PireCas(Tous()[0]) with { Steps = [new GuideStep(1, cle, valeur, FirmwareSettingKind.XmpExpo, IsRecoveryRelated: false)] };

    [Theory]
    [InlineData("Bios.Guide.Step.Inconnue", "OC  →  XMP  →  = Profile 1")]
    [InlineData("Bios.Guide.Step.Xmp", "CPU Core Voltage  →  = 1.55 V")]
    [InlineData("Bios.Guide.Step.Xmp", "CPU Core Voltage  →  = 1550mV")]
    // La règle de tension ne dépend pas de la casse, et l'unité écrite en toutes lettres compte aussi.
    [InlineData("Bios.Guide.Step.Xmp", "CPU Core Voltage  →  = 1.55v")]
    [InlineData("Bios.Guide.Step.Xmp", "CPU Core Voltage  →  = 1350 mv")]
    [InlineData("Bios.Guide.Step.Xmp", "CPU Core Voltage  →  = 1.55 volts")]
    [InlineData("Bios.Guide.Step.Xmp", "CPU Core Voltage  →  = 1.55 VOLT")]
    [InlineData("Bios.Guide.Step.Xmp", "va sur https://exemple.test")]
    [InlineData("Bios.Guide.Step.Xmp", "www.exemple.test  →  = oui")]
    [InlineData("Bios.Guide.Step.Xmp", "valeur avec un accent : é")]
    public void Ce_que_la_page_refuserait_n_est_jamais_encode(string cle, string valeur)
    {
        var g = new BiosGuideViewModel(AvecEtape(cle, valeur), c => Langue(c, "fr"));

        g.AdressePage.Should().BeNull();
        g.QrPorteLaPage.Should().BeFalse();
        g.QrVisible.Should().BeTrue("le texte brut reste");
    }

    [Fact]
    public void Une_valeur_trop_longue_est_refusee()
        => new BiosGuideViewModel(AvecEtape("Bios.Guide.Step.Xmp", new string('A', 301)), c => Langue(c, "fr")).AdressePage.Should().BeNull();

    [Fact]
    public void Un_fragment_forge_avec_un_code_inconnu_ne_se_lit_pas()
    {
        var forge = new PageDuGuide.Contenu(1, "fr", "Carte", "Del", "F10", [["Inconnue", "OC"]], ["ClearCmos"], null, null, null);

        PageDuGuide.Lire(PageDuGuide.Fragment(forge)).Should().BeNull();
        PageDuGuide.Lire(PageDuGuide.Fragment(forge with { Etapes = [["Xmp", "OC"]] })).Should().NotBeNull("témoin : le même fragment avec un code connu");
    }

    // Les règles de la page sont celles de l'App ; aucune ligne « vérifié » ; la ligne fixe d'avertissement en tête.
    [Fact]
    public void La_page_applique_les_regles_de_l_App_et_avertit()
    {
        var page = Page();
        var bloc = System.Text.RegularExpressions.Regex.Match(page, "<script type=\"application/json\" id=\"regles\">(.*?)</script>",
            System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        JsonSerializer.Deserialize<Dictionary<string, string>>(bloc).Should().Equal(PageDuGuide.Regles);

        page.Should().NotContain("Vérifié pour ta carte").And.NotContain("Verified for your exact board");
        page.Should().Contain("Ces consignes viennent de DrDXT sur ton PC. Si tu n'as pas scanné ce QR toi-même depuis DrDXT, ne les suis pas.")
            .And.Contain("These instructions come from DrDXT on your PC. If you did not scan this QR yourself from DrDXT, do not follow them.");
    }

    [Fact]
    public void L_adresse_est_celle_du_dossier_publie()
        => PageDuGuide.Adresse.Should().EndWith("/guide-bios/", "GitHub Pages sert docs/ : docs/guide-bios/index.html");

    // Rien d'autre que ce que le guide affiche : les champs de la spécification, et du nom de la machine seulement le
    // modèle de la carte.
    [Fact]
    public void L_adresse_ne_porte_que_ce_que_le_guide_affiche()
    {
        var guide = PireCas(Tous()[0]);
        var g = new BiosGuideViewModel(guide, c => Langue(c, "fr"));
        var json = JsonSerializer.Serialize(PageDuGuide.Depuis(guide, g, "fr"),
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeSubsetOf(["v", "l", "c", "e", "s", "p", "h", "b", "n", "m"], "aucun champ « vérifié » : une adresse forgée ne peut pas le prétendre");
        json.Should().NotContain(Environment.MachineName).And.NotContain(Environment.UserName).And.NotContain("1.A0", "la version du BIOS n'est pas affichée");
    }
}
