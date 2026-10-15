using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// Des machines enregistrées (inventaire, réglages, constats du docteur) rejouées par le
/// scan complet et l'écran, sans WMI. Une fixture prouve « pas de faux » sur une machine qu'on n'a pas ; elle ne prouve
/// pas la justesse du lecteur, qui se prouve sur la vraie machine.
/// </summary>
public class MachinesEnregistreesTests
{
    internal sealed record Machine(string Nom, HardwareInventory Inventaire, SettingsState Reglages, List<RepairFinding> Sante);

    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    private static string Dossier()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return Path.Combine(d?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable"), "tests", "Shared", "machines");
    }

    internal static Machine Charger(string nom) => Lire(File.ReadAllText(Path.Combine(Dossier(), nom + ".json")));

    // Le corps de Charger, pour relire un fichier exporté sans le poser dans le dépôt.
    internal static Machine Lire(string json) => JsonSerializer.Deserialize<Machine>(json, Options)!;

    public static TheoryData<string> Noms()
    {
        var t = new TheoryData<string>();
        foreach (var f in Directory.EnumerateFiles(Dossier(), "*.json").Order()) t.Add(Path.GetFileNameWithoutExtension(f));
        return t;
    }

    [Fact]
    public void Les_machines_attendues_sont_la()
        => Noms().Cast<object[]>().Select(n => (string)n[0]).Should().Contain(["reference-i7-14700f-rtx4060-2026-10-06", "amd-radeon-synthetique",
            "intel-arc-synthetique", "sans-admin", "portable-batterie", "amd-rx6600-ryzen5-5600", "amd-rx6700xt-ryzen5-7600",
            "amd-rx7900xtx-ryzen9-7950x3d", "console-portable-rog-ally", "nvidia-gtx1650-i5-10400", "nvidia-rtx3060-i5-12400f",
            "nvidia-rtx5070-i5-13400f", "bureau-sans-jeu-i5-8go"]);

    // Sur les cinq machines, les constats de la suspension sélective USB (coupée ou non, entrées
    // suspendues ou non) ne font rien monter de plus dans « mérite ton attention » : aucune proposition n'est inventée.
    [Theory]
    [MemberData(nameof(Noms))]
    public void La_suspension_USB_ne_propose_rien_sur_aucune_machine(string nom)
    {
        var sante = Charger(nom).Sante;
        var avant = RepairTriage.Trier(sante).Attention.Count;
        RepairFinding[] usb =
        [
            DrDxt.Detection.Repair.SuspensionUsb.JugerPlan(new("suspension sélective USB (secteur)", 0, 1, "")),
            DrDxt.Detection.Repair.SuspensionUsb.JugerEntrees(5, 2),
        ];

        RepairTriage.Trier(sante.Concat(usb)).Attention.Should().HaveCount(avant);
    }

    // Une carte Intel Arc se dit « non lue » là où DrDXT ne lit que NVIDIA, comme une AMD.
    [Fact]
    public void Sur_une_carte_Intel_la_temperature_se_dit_non_lue_et_nomme_la_carte()
    {
        var c = Charger("intel-arc-synthetique").Sante.Select(Constat.Depuis).Single(c => c.Id == "sante/THERMIQUE/temperature du GPU");

        c.Etat.Should().Be(EtatConstat.NonLu);
        Charger("intel-arc-synthetique").Inventaire.Gpus.Should().OnlyContain(g => g.Vendor == "Intel");
    }

    // Un portable branché en « Meilleure autonomie » reçoit le geste ; le même mode sur un fixe ne le reçoit pas (B4).
    [Fact]
    public void Un_portable_en_meilleure_autonomie_recoit_le_geste()
    {
        var m = Charger("portable-batterie");

        m.Inventaire.Chassis.Should().Be(ChassisType.Laptop);
        m.Sante.Should().ContainSingle(s => s.Nom == "mode d'alimentation (secteur)")
            .Which.Action.Should().Be(RepairAction.Recommandation);
    }

    // Enregistrée sur la machine de référence par un compte sans droits administrateur (jeton filtré, intégrité
    // moyenne) ; ce qui demande les droits n'y est pas lu.
    [Fact]
    public void Sans_droits_administrateur_ce_qui_les_demande_se_dit_non_lu()
    {
        var sante = Charger("sans-admin").Sante;
        sante.Should().Contain(s => s.Nom == "mesure de Windows" && s.Statut == RepairStatus.Unknown);
        // Cette machine enregistrée garde des lectures fausses (« aucun rapport noyau » alors qu'il y en a 4, « ce disque
        // ne publie pas son compteur », « aucune zone thermique ACPI ne répond », un message d'exception anglais).
        sante.Single(s => s.Nom == "incident noyau conserve").Statut.Should().Be(RepairStatus.Unknown);
        sante.Select(s => s.Explication).Should().NotContain(e => e.Contains("aucun rapport noyau")
            || e.Contains("ne publie pas son compteur") || e.Contains("aucune zone thermique") || e.Contains("unauthorized"));
    }

    [Theory]
    [MemberData(nameof(Noms))]
    public async Task Chaque_machine_se_rejoue_jusqu_a_l_ecran_sans_constat_invente(string nom)
    {
        var m = Charger(nom);
        var scan = new ScanScorer().Score(m.Inventaire, m.Reglages, null);
        scan.IsOk.Should().BeTrue();
        var bios = new DrDxt.Bios.BiosAdvisor().Advise(m.Inventaire, m.Reglages);

        var constats = scan.Value!.Findings.Select(Constat.Depuis)
            .Concat(bios.IsOk ? bios.Value!.Select(Constat.Depuis) : [])
            .Concat(m.Sante.Select(Constat.Depuis)).ToList();
        constats.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.Titre));
        // La santé n'est pas rejouée, elle est enregistrée (relevée le 06/10) ; S1 porte donc sur
        // ces vrais constats, pas sur une comparaison d'une valeur à elle-même.
        m.Sante.Where(s => s.Statut != RepairStatus.Unknown)
            .Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.ValeurCourante) && !string.IsNullOrWhiteSpace(s.Explication));

        // Jusqu'à l'écran : le scan complet du ViewModel, sur ces seules données.
        var vm = Vm(m);
        await vm.ScanCommand.ExecuteAsync(null);
        // Sans mesure en jeu, le scan se dit dégradé : c'est l'état honnête d'une fixture, jamais un échec.
        vm.State.Should().BeOneOf(ViewModels.ScanState.Done, ViewModels.ScanState.Degraded);
        vm.RepairFindings.Concat(vm.RepairBlindSpots).Should().NotBeEmpty();
        // Aucune partie relevée sur une fixture, donc aucun voisin lu : le calmeur se dit « non lu », jamais proposé.
        vm.PropositionCalmer.Should().Be("Voisins.Proposition.NonLu");
        vm.CalmeurPropose.Should().BeFalse();
        // Une fixture publique ne porte aucun chemin d'un vrai compte (seul le compte de remplacement « joueur » est permis).
        vm.RepairFindings.Concat(vm.RepairBlindSpots).SelectMany(r => new[] { r.ValeurCourante, r.Explication })
            .Should().NotContain(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"[A-Z]:\\Users\\(?!joueur\\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    // Le mode de boost lu par le docteur sur chaque machine (2 = défaut partout) ne fait proposer
    // aucun retour du boost. Le constat porte la valeur lue et le défaut du plan, en tête de leur texte.
    [Theory]
    [MemberData(nameof(Noms))]
    public void Chaque_machine_ne_recoit_aucun_retour_du_boost_invente(string nom)
    {
        var m = Charger(nom);
        var constat = m.Sante.Single(s => s.Nom == "mode de boost");
        static uint Tete(string? texte) => uint.Parse(texte!.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        var plan = Guid.Parse(m.Reglages.PowerPlanGuid);
        var etat = m.Reglages with { ModeDeBoost = new ModeDeBoostLu(plan, Tete(constat.ValeurCourante), Tete(constat.ValeurAttendue)) };

        var levier = new DrDxt.Application.Tweaks.FixIt.ModeBoostTweak(
            Substitute.For<DrDxt.Application.Tweaks.Hardware.IPowerPlanReader>(), Substitute.For<IElevatedClient>());

        levier.AppliesTo(m.Inventaire, etat).Should().BeFalse($"{nom} lit un boost {constat.ValeurCourante}, son défaut");
        // Et la fixture telle qu'enregistrée : sans ce champ (exports d'une ancienne version), elle se dit non lue, jamais « rien à
        // faire » ; avec (CONTRIB2, export récent), le boost lu se juge lui-même et n'est pas proposé non plus.
        if (m.Reglages.ModeDeBoost is null) levier.EtatNonLu(m.Reglages).Should().BeTrue();
        else levier.AppliesTo(m.Inventaire, m.Reglages).Should().BeFalse($"{nom} a lu son boost, à son défaut");
    }

    [Fact]
    public void Sur_une_carte_AMD_ce_que_DrDXT_ne_lit_que_chez_NVIDIA_se_dit_non_lu()
    {
        var m = Charger("amd-radeon-synthetique");
        var constats = new ScanScorer().Score(m.Inventaire, m.Reglages, null).Value!.Findings.Select(Constat.Depuis)
            .Concat(m.Sante.Select(Constat.Depuis)).ToList();

        constats.Should().Contain(c => c.Etat == EtatConstat.NonLu && c.Categorie == "Gpu");
        constats.Should().Contain(c => c.Id == "sante/THERMIQUE/temperature du GPU" && c.Etat == EtatConstat.NonLu);
    }

    internal static ViewModels.ScanViewModel Vm(Machine m)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(m.Inventaire));
        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(m.Reglages));
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Fail(ErrorCodes.WmiFail, "pas de mesure dans une fixture"));
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(new List<StorageDevice>()));
        return ScanViewModelTestSeams.Build(hd, sr, br, new ScanScorer(), sd, k => k, repairAudit: () => m.Sante);
    }
}
