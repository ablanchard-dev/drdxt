using System.IO;
using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// « Exporter ma machine ». Le fichier d'un joueur testeur doit devenir une fixture de
/// tests/Shared/machines telle quelle, et ne rien porter qui désigne le joueur ou son PC. Aucun test ne lit ce PC : les
/// valeurs propres à la machine sont semées dans une fausse machine.
/// </summary>
public class ExportDeMachineTests
{
    [Theory]
    [MemberData(nameof(MachinesEnregistreesTests.Noms), MemberType = typeof(MachinesEnregistreesTests))]
    public void Une_machine_exportee_se_recharge_avec_le_meme_inventaire_et_les_memes_reglages(string nom)
    {
        var m = MachinesEnregistreesTests.Charger(nom);

        var relue = MachinesEnregistreesTests.Lire(MachineExportee.Json(m.Nom, null, null, m.Inventaire, m.Reglages, m.Sante, []));

        relue.Nom.Should().Be(m.Nom);
        relue.Inventaire.Should().BeEquivalentTo(m.Inventaire);
        relue.Reglages.Should().BeEquivalentTo(m.Reglages);
        // Les textes de santé peuvent perdre une version à la forme d'une adresse (« 1.4.6.3 ») ; les constats restent.
        relue.Sante.Select(s => (s.Domaine, s.Statut, s.Action)).Should().Equal(m.Sante.Select(s => (s.Domaine, s.Statut, s.Action)));
    }

    // Les valeurs semées, une entrée par ligne de MachineExportee.CeQuiEstRetire.
    private const string Compte = "JoueurSecret", Pc = "PC-JOUEUR-7Q", Domaine = "MAISON-JOUEUR";
    private const string SerieBios = "SN7Q4X9Z2K", SerieDisque = "S6WSNJ0R123456", Uuid = "4C4C4544-0042-3510-8048-B4C04F4D3732";
    private const string Mac = "A4-BB-6D-12-34-56", MacPoints = "a4:bb:6d:12:34:57", MacColle = "A4BB6D123458";
    private const string Ipv4 = "192.168.1.42", Ipv6 = "fe80::1c2d:3e4f:5a6b:7c8d";
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string CleLicence = "7KQ2M-XW9TR-B4HNP-3JC8D-VF6GY";
    private const string EmpreinteHex = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string Volume = @"\\?\Volume{0e1f2a3b-4c5d-6e7f-8091-a2b3c4d5e6f7}\";
    private const string Courriel = "joueur.secret@example.com";
    private const string AutreCompte = "Bernard";
    // Un compte de deux lettres, et l'employeur d'un dossier OneDrive.
    private const string CompteCourt = "Qz", Entreprise = "Zorglub Industries";

    private static readonly Dictionary<string, string[]> Semees = new()
    {
        [MachineExportee.CeQuiEstRetire[0]] = [AutreCompte],
        [MachineExportee.CeQuiEstRetire[1]] = [Compte, Pc, Domaine, CompteCourt],
        [MachineExportee.CeQuiEstRetire[2]] = [SerieBios, SerieDisque, Uuid],
        [MachineExportee.CeQuiEstRetire[3]] = [Mac, MacPoints, MacColle],
        [MachineExportee.CeQuiEstRetire[4]] = [Ipv4, Ipv6],
        [MachineExportee.CeQuiEstRetire[5]] = [Sid],
        [MachineExportee.CeQuiEstRetire[6]] = ["0e1f2a3b-4c5d-6e7f-8091-a2b3c4d5e6f7"],
        [MachineExportee.CeQuiEstRetire[7]] = [Courriel],
        [MachineExportee.CeQuiEstRetire[8]] = [Entreprise],
        [MachineExportee.CeQuiEstRetire[9]] = [CleLicence, EmpreinteHex],
    };

    private static (HardwareInventory, SettingsState, List<RepairFinding>) FausseMachine()
    {
        var m = MachinesEnregistreesTests.Charger("reference-i7-14700f-rtx4060-2026-10-06");
        var hw = m.Inventaire with { Board = new MotherboardInfo("MSI", $"PRO B760 {SerieBios}") };
        var st = m.Reglages with
        {
            NetworkAdapters = [new NetworkAdapterState($"Ethernet de {Compte}", $"Realtek {Mac}", true, TriState.Off,
                TriState.On, TriState.Off, TriState.Off)],
        };
        var sante = new List<RepairFinding>(m.Sante)
        {
            new("TACHES", $"tâche de {Pc}", $@"C:\Users\{Compte}\Downloads\outil.exe", null, RepairStatus.Suspicious,
                $@"lancée par {Domaine}\{Compte} ({Sid}), aussi sous C:\Users\{AutreCompte}\AppData\x.exe ; écrire à {Courriel}",
                Provenance: $"{Volume} disque {SerieDisque}, machine {Uuid}"),
            new("RESEAU", "carte", $"{MacPoints} {MacColle}", null, RepairStatus.Observed, $"adresses {Ipv4} et {Ipv6}"),
            new("FICHIERS", "synchro", $@"C:\Users\{Compte}\OneDrive - {Entreprise}\notes.txt", null, RepairStatus.Observed,
                $"ouvert par {CompteCourt} hier"),
            new("LICENCE", "clé", CleLicence, null, RepairStatus.Observed, $"empreinte {EmpreinteHex}"),
        };
        return (hw, st, sante);
    }

    // « Unknown » est ce que WMI rend pour un numéro de série vide : il ne désigne aucun PC, le rapport le garde lisible.
    [Fact]
    public void Une_valeur_generique_de_wmi_ne_se_retire_pas()
        => MachineExportee.AnonymiserTexte("mémoire : unknown (moins de 3 passes) ; série Default string", ["Unknown", "Default string", "Bob"])
            .Should().Contain("unknown (moins de 3 passes)").And.Contain("Default string");

    [Fact]
    public void Un_rapport_en_texte_libre_perd_les_memes_valeurs_et_garde_le_materiel()
    {
        // Le rapport de vérification part en public (ticket GitHub) ; les règles de la machine exportée s'y appliquent.
        // Le même texte que la fausse machine, mis à plat comme un rapport : chaque valeur semée dans son contexte réel.
        var (hw, st, sante) = FausseMachine();
        var texte = string.Join(Environment.NewLine, sante.SelectMany(f => new[] { f.Nom, f.ValeurCourante ?? "", f.Explication, f.Provenance ?? "" }))
            + Environment.NewLine + hw.Board.Product + Environment.NewLine + string.Join(Environment.NewLine, (st.NetworkAdapters ?? []).Select(a => $"{a.Name} {a.Description}"))
            + Environment.NewLine + "Matériel : AMD Ryzen 7 7800X3D | AMD Radeon RX 9070 XT (pilote 32.0.21013.1000)";

        var propre = MachineExportee.AnonymiserTexte(texte, [Compte, Pc, Domaine, SerieBios, SerieDisque, Uuid, MacColle, CompteCourt]);

        foreach (var (regle, valeurs) in Semees)
            foreach (var v in valeurs)
                propre.Should().NotContainEquivalentOf(v, $"« {regle} »");
        propre.Should().Contain("AMD Radeon RX 9070 XT (pilote 32.0.21013.1000)", "un modèle et un pilote ne désignent personne");
    }

    [Fact]
    public void Aucune_valeur_qui_designe_le_joueur_ou_son_pc_ne_sort()
    {
        Semees.Keys.Should().BeEquivalentTo(MachineExportee.CeQuiEstRetire, "chaque règle écrite a sa valeur semée");
        var (hw, st, sante) = FausseMachine();

        var json = MachineExportee.Json("essai", null, true, hw, st, sante, [Compte, Pc, Domaine, SerieBios, SerieDisque, Uuid, MacColle, CompteCourt]);

        foreach (var (regle, valeurs) in Semees)
            foreach (var v in valeurs)
                json.Should().NotContainEquivalentOf(v, $"« {regle} »");
        // Ce qui n'identifie personne reste : la suite du chemin, et la machine se relit comme une fixture.
        json.Should().Contain(@"C:\\Users\\joueur\\Downloads\\outil.exe");
        var relue = MachinesEnregistreesTests.Lire(json);
        relue.Inventaire.Cpu.Should().Be(hw.Cpu);
        relue.Reglages.PowerPlanGuid.Should().Be(st.PowerPlanGuid);
    }

    [Fact]
    public async Task Le_bouton_exporte_ce_que_le_scan_a_lu_et_dit_ou()
    {
        var m = MachinesEnregistreesTests.Charger("amd-radeon-synthetique");
        (HardwareInventory Hw, SettingsState St, IReadOnlyList<RepairFinding> Sante)? recu = null;
        var vm = Vm(m, (hw, st, sante) => { recu = (hw, st, sante); return @"D:\Documents\machine.json"; });

        vm.CanExportMachine.Should().BeTrue();
        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ExporterMaMachineCommand.ExecuteAsync(null);

        recu.Should().NotBeNull();
        recu!.Value.Hw.Should().BeEquivalentTo(m.Inventaire);
        recu.Value.St.Should().BeEquivalentTo(m.Reglages);
        recu.Value.Sante.Should().BeEquivalentTo(m.Sante);
        vm.LastMachineExportPath.Should().Be(@"D:\Documents\machine.json");
    }

    [Fact]
    public async Task Sans_scan_ou_si_l_ecriture_echoue_le_bouton_le_dit()
    {
        var m = MachinesEnregistreesTests.Charger("amd-radeon-synthetique");
        var vm = Vm(m, (_, _, _) => throw new IOException("disque plein"));

        await vm.ExporterMaMachineCommand.ExecuteAsync(null);
        vm.LastMachineExportPath.Should().BeEmpty();
        vm.MessageDExport.Should().Be("Machine.ExportFailed");

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ExporterMaMachineCommand.ExecuteAsync(null);
        vm.LastMachineExportPath.Should().BeEmpty();
        vm.MessageDExport.Should().Be("Machine.ExportFailed");
    }

    // L'échec d'un export s'écrivait dans le message d'Optimiser, loin des boutons d'export, et y restait : il se dit près
    // de l'export, et un export réussi l'efface.
    [Fact]
    public async Task L_echec_d_un_export_se_dit_pres_de_l_export_et_un_export_reussi_l_efface()
    {
        var m = MachinesEnregistreesTests.Charger("amd-radeon-synthetique");
        var essais = 0;
        var vm = Vm(m, (_, _, _) => ++essais == 1 ? throw new IOException("disque plein") : @"D:\Documents\machine.json");
        await vm.ScanCommand.ExecuteAsync(null);

        await vm.ExporterMaMachineCommand.ExecuteAsync(null);
        vm.MessageDExport.Should().Be("Machine.ExportFailed");
        vm.ApplyBlockedMessage.Should().BeNullOrEmpty("le message d'Optimiser ne parle pas de l'export");

        await vm.ExporterMaMachineCommand.ExecuteAsync(null);
        vm.MessageDExport.Should().BeEmpty();
        vm.LastMachineExportPath.Should().Be(@"D:\Documents\machine.json");
    }

    [Fact]
    public void Sans_couture_pas_de_bouton()
        => ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), new DrDxt.Detection.Scoring.ScanScorer(), Substitute.For<IStorageDetector>(), k => k)
            .CanExportMachine.Should().BeFalse();

    private static ViewModels.ScanViewModel Vm(MachinesEnregistreesTests.Machine m,
        Func<HardwareInventory, SettingsState, IReadOnlyList<RepairFinding>, string> exporter)
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
        return ScanViewModelTestSeams.Build(hd, sr, br, new DrDxt.Detection.Scoring.ScanScorer(), sd, k => k,
            repairAudit: () => m.Sante, exporterMachine: exporter);
    }
}
