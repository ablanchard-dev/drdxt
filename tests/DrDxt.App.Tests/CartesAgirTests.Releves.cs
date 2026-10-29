using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>Les relevés de la machine entre deux scans : réglages BIOS, ventilateur, mode Jeu et HAGS.</summary>
public sealed partial class CartesAgirTests
{
    // Un réglage BIOS changé entre deux références se note, daté du démarrage du PC.
    [Fact]
    public void Un_reglage_BIOS_change_se_note_date_du_demarrage()
    {
        var chemin = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"changements-{Guid.NewGuid():N}.json");
        try
        {
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(chemin);
            // L'heure du démarrage est la couture, pas l'horloge du PC (le compteur de ticks et le journal de
            // démarrage peuvent diverger : 20 h mesurées sur la machine de référence le 12/10).
            var demarrage = DateTimeOffset.Now.AddHours(-5);
            vm.DemarrageDuPc = () => demarrage;
            vm.NoterChangementBios("Intel Default Settings", "Intel Default Settings");
            vm.NoterChangementBios(null, "MSI Performance Settings");
            vm.JournalChangements.Lire().Should().BeEmpty("témoin : rien de changé, ou pas d'avant connu");

            var mesureAvant = DateTimeOffset.Now.AddDays(-3);
            vm.NoterChangementBios("Intel Default Settings", "MSI Performance Settings", mesureAvant);

            var c = vm.JournalChangements.Lire().Should().ContainSingle().Subject;
            c.Libelle.Should().Contain("Changement.Bios");
            c.Quand.Should().Be(demarrage, "le changement a pris effet au plus tard au démarrage");
            c.Depuis.Should().Be(mesureAvant, "et au plus tôt après la dernière mesure sous l'ancien réglage");
            vm.ChangementsEnAttente.Should().ContainSingle(l => l.EnJeu == "Changement.Tableau.SansAvant", "la carte se refait aussitôt, sans partie d'avant (08/10 : en tableau)");
        }
        finally { System.IO.File.Delete(chemin); }
    }

    // L'Instant Replay basculé entre deux scans entre au journal, nommé en mots.
    [Fact]
    public async Task Un_reglage_bascule_entre_deux_scans_entre_au_journal()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"suivi-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            vm.SuiviReglages = new DrDxt.Application.Parties.SuiviDesReglages(System.IO.Path.Combine(dossier, "s.json"));
            var t = DateTimeOffset.Now;
            await vm.ReleverReglagesSuivisAsync(new Dictionary<string, string> { ["nvidia.instant_replay"] = "on" }, t);
            vm.JournalChangements.Lire().Should().BeEmpty("témoin : premier relevé");

            await vm.ReleverReglagesSuivisAsync(new Dictionary<string, string> { ["nvidia.instant_replay"] = "off" }, t.AddHours(1));

            var c = vm.JournalChangements.Lire().Should().ContainSingle().Subject;
            c.Libelle.Should().Be("Changement.Reglage");
            c.Depuis.Should().Be(t);
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Le relevé de fond tient le verrou des réglages suivis : le relevé de l'écran attend son tour hors du fil de l'écran,
    // qui sinon se figeait jusqu'à la fin de l'attente. Le verrou rendu, le relevé aboutit et la bascule entre au journal.
    [Fact]
    public async Task Un_releve_qui_attend_son_tour_ne_fige_pas_l_ecran()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"suivi-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var chemin = System.IO.Path.Combine(dossier, "s.json");
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            vm.SuiviReglages = new DrDxt.Application.Parties.SuiviDesReglages(chemin, attenteMax: TimeSpan.FromSeconds(3));
            var t = DateTimeOffset.Now;
            await vm.ReleverReglagesSuivisAsync(new Dictionary<string, string> { ["nvidia.instant_replay"] = "on" }, t);

            Task releve;
            var chrono = System.Diagnostics.Stopwatch.StartNew();
            using (DrDxt.Application.Parties.VerrouDuReleve.Prendre(chemin + ".verrou") ?? throw new InvalidOperationException("verrou du test"))
            {
                releve = vm.ReleverReglagesSuivisAsync(new Dictionary<string, string> { ["nvidia.instant_replay"] = "off" }, t.AddHours(1));
                chrono.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "l'appelant reprend la main pendant que le relevé attend");
                releve.IsCompleted.Should().BeFalse("le verrou est tenu : le relevé attend encore");
            }
            await releve;

            vm.JournalChangements.Lire().Should().ContainSingle("le verrou rendu, la bascule est relevée");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // La courbe du ventilateur processeur se suit en une valeur : huit octets au BIOS, un seul changement daté.
    [Fact]
    public void La_courbe_du_ventilateur_processeur_est_suivie_en_une_valeur()
    {
        var bios = new List<BiosSettingReading> { new("CPU Fan1 Smart Fan Control", 1, "Enabled", true) };
        long[] t = [40, 62, 80, 92], v = [35, 55, 70, 100];
        for (var i = 0; i < 4; i++)
        {
            bios.Add(new($"CPU Fan1 level {i + 1} Temperature", t[i], $"{t[i]} °C", false));
            bios.Add(new($"CPU Fan1 level {i + 1} Fan Speed", v[i], $"{v[i]} %", false));
        }

        var etats = ScanViewModel.EtatsSuivis(bios: bios);

        etats["bios.courbe_ventilateur_cpu"].Should().Be("40-62-80-92 °C, 35-55-70-100 %");
        etats["bios.ventilateur_cpu_intelligent"].Should().Be("Enabled");
        etats.Keys.Should().NotContain(k => k.Contains("level"), "les paliers ne se suivent pas un par un");

        bios.RemoveAt(bios.Count - 1);
        ScanViewModel.EtatsSuivis(bios: bios).Should().NotContainKey("bios.courbe_ventilateur_cpu", "un palier manquant : rien n'est inventé");
    }

    // La limite de courant, le Lite Load et CEP lus au BIOS entrent au suivi ; un changement date du démarrage.
    [Fact]
    public void Les_reglages_BIOS_du_processeur_sont_suivis_et_dates_au_demarrage()
    {
        var bios = new[]
        {
            new BiosSettingReading("CPU Current Limit", 160, "160 A", false),
            new BiosSettingReading("CPU Lite Load", 80, "Mode 9", false),
            new BiosSettingReading("XMP", 1, "Profile 1", false),
        };
        var etats = ScanViewModel.EtatsSuivis(bios: bios);
        etats["bios.limite_courant"].Should().Be("160 A");
        etats["bios.lite_load"].Should().Be("Mode 9");
        etats.Should().NotContainKey("XMP", "hors des réglages suivis");

        var scanAvant = DateTimeOffset.Now.AddHours(-10);
        var demarrage = DateTimeOffset.Now.AddHours(-3);
        var vu = new DrDxt.Application.Parties.Changement(DateTimeOffset.Now, "Changement.Reglage", scanAvant, "bios.limite_courant:Auto");
        ScanViewModel.DaterAuDemarrage(vu, demarrage).Quand.Should().Be(demarrage);
        ScanViewModel.DaterAuDemarrage(vu with { Cle = "windows.hags:on" }, demarrage).Quand.Should().Be(vu.Quand, "pas un réglage du BIOS");
        ScanViewModel.DaterAuDemarrage(vu with { Depuis = demarrage.AddHours(1) }, demarrage).Quand.Should().Be(vu.Quand,
            "démarrage avant le scan d'avant : le changement n'est pas de lui");
    }

    // Le mode Jeu et HAGS effectif entrent au suivi ; un état non lu n'y entre pas.
    [Fact]
    public void Le_mode_Jeu_et_HAGS_effectif_sont_suivis_quand_ils_sont_lus()
    {
        var st = new SettingsState(TriState.Unknown, TriState.Unknown, "", TriState.Off, TriState.On, null, TriState.Unknown,
            HagsEffective: TriState.On);
        var etats = ScanViewModel.EtatsSuivis(st);
        etats["windows.game_mode"].Should().Be("on");
        etats["windows.hags"].Should().Be("on", "l'état effectif, pas la valeur du registre qui attend un redémarrage");
        ScanViewModel.EtatsSuivis(st with { GameMode = TriState.Unknown }).Should().NotContainKey("windows.game_mode");

        // Plan 2.4 : la fréquence et la tension de la mémoire datent un changement fait au BIOS.
        var ram = ScanViewModel.EtatsSuivis(null, Hw() with { Memory = [new MemoryModule("F4-4000C18-32GTZR", "G.Skill", 3800, 4000, 26, 2, 1350)] });
        ram["memoire.frequence"].Should().Be("3800 MT/s");
        ram["memoire.tension"].Should().Be("1.35 V");
        ScanViewModel.EtatsSuivis(null, Hw() with { Memory = [] }).Should().NotContainKey("memoire.frequence");
    }

    // Le journal tenu par un autre programme pendant le relevé : la bascule n'y entre pas, mais elle n'est pas perdue ; le
    // relevé suivant, le journal rendu, l'y fait entrer.
    [Fact]
    public async Task Une_bascule_que_le_journal_n_a_pas_prise_revient_au_releve_suivant()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"suivi-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var journal = System.IO.Path.Combine(dossier, "j.json");
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(journal);
            vm.SuiviReglages = new DrDxt.Application.Parties.SuiviDesReglages(System.IO.Path.Combine(dossier, "s.json"));
            var t = DateTimeOffset.Now;
            await vm.ReleverReglagesSuivisAsync(new Dictionary<string, string> { ["nvidia.instant_replay"] = "on" }, t);
            System.IO.File.WriteAllText(journal, "[]");

            using (new System.IO.FileStream(journal, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
                await vm.ReleverReglagesSuivisAsync(new Dictionary<string, string> { ["nvidia.instant_replay"] = "off" }, t.AddHours(1));
            System.IO.File.ReadAllText(journal).Should().Be("[]", "témoin : le journal tenu n'a rien pris");
            vm.JournalChangements.Lire().Should().ContainSingle("refusée, la bascule attend le passage suivant, et se lit déjà");

            await vm.ReleverReglagesSuivisAsync(new Dictionary<string, string> { ["nvidia.instant_replay"] = "off" }, t.AddHours(2));

            vm.JournalChangements.Lire().Should().ContainSingle().Which.Depuis.Should().Be(t);
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }
}
