using System.Text.Json;
using DrDxt.Application.Tweaks;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Autopilot;

/// <summary>
/// « Annuler », le retrait automatique, l'essai et la surveillance de la carte lisent leur annulation dans des
/// fichiers de %LOCALAPPDATA% que tout programme du compte écrit sans droits, et les leviers la rejouent par le helper
/// élevé sans la relire. Une entrée qui porte autre chose que l'annulation de son levier ne doit jamais sortir du
/// fichier : sinon une simple écriture de fichier faisait exécuter n'importe quelle op de l'allowlist au premier clic.
/// </summary>
public sealed class AnnulationsLuesDansUnFichierTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), $"drdxt-annulations-{Guid.NewGuid():N}");
    public AnnulationsLuesDansUnFichierTests() => Directory.CreateDirectory(_dossier);
    public void Dispose() { try { Directory.Delete(_dossier, recursive: true); } catch { /* dossier temporaire du test */ } }

    private string Fichier(string nom) => Path.Combine(_dossier, nom);
    private static string Json(PrivilegedOp op) => PrivilegedOpCodec.Serialize(op).Value!;

    private static readonly string HwSchMode = Json(new SetRegistryValueOp(RegistryHiveKind.LocalMachine,
        @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueType.DWord, "1"));

    public static TheoryData<string, string> Pieges() => new()
    {
        // Un levier de registre qui porterait le redémarrage dans le BIOS.
        { "fixit.sane_pagefile", Json(new RebootToFirmwareSetupOp(10)) },
        // Le bon type, une feuille qui n'est pas la sienne : le fichier d'échange coupé.
        { "latency.hagsOn", Json(new SetRegistryValueOp(RegistryHiveKind.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles", RegistryValueType.MultiString, "")) },
        { "gpu.undervolt", Json(new SetCpuVoltageOffsetOp(-125)) },
        { "latency.prefer_max_perf", Json(new SetBcdValueOp("useplatformclock", "Yes")) },
        // Un levier que rien ne rejoue.
        { "levier.inconnu", HwSchMode },
    };

    [Theory]
    [MemberData(nameof(Pieges))]
    public void Une_entree_piegee_du_journal_des_leviers_n_est_jamais_rendue(string id, string annulation)
    {
        File.WriteAllText(Fichier("leviers-appliques.json"), JsonSerializer.Serialize(new[]
        {
            new TweakSnapshot(id, annulation),
            new TweakSnapshot("latency.hagsOn", HwSchMode),
        }));

        var lus = new JournalDesLeviersAppliques(Fichier("leviers-appliques.json")).Lire();

        lus.Should().NotContain(s => s.OriginalValueJson == annulation && s.TweakId == id);
        lus.Should().ContainSingle(s => s.TweakId == "latency.hagsOn" && s.OriginalValueJson == HwSchMode,
            "la vraie annulation de son levier reste");
    }

    [Theory]
    [MemberData(nameof(Pieges))]
    public void Un_reglage_retenu_piege_n_est_jamais_rendu(string id, string annulation)
    {
        var retenus = new ReglagesRetenus(Fichier("reglage-carte-retenu.json"));
        retenus.Garder(new ReglageRetenu(id, annulation, null, DateTimeOffset.Now));

        retenus.Charger().Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Pieges))]
    public void Une_preuve_en_attente_piegee_n_est_jamais_rendue(string id, string annulation)
    {
        var attente = new PreuvesEnAttente(Fichier("preuve-repos-carte.json"));
        attente.Garder(new PreuveEnAttente(id, annulation, null, [100.0]));

        attente.Charger().Should().BeNull();
    }

    // Le retour au repos de la carte rejoue « état P préféré » sur le profil de base : son annulation passe.
    [Fact]
    public void L_annulation_du_repos_de_la_carte_reste_rejouable()
    {
        var retour = Json(new ApplyNvApiDrsSettingOp(0x1057EB71, 1, "Base Profile"));
        var retenus = new ReglagesRetenus(Fichier("reglage-carte-retenu.json"));
        retenus.Garder(new ReglageRetenu("gpu.repos_global_normal", retour, null, DateTimeOffset.Now));

        retenus.Charger().Should().NotBeNull();
    }

    // Le mode d'écran se rend sans le helper, depuis son propre format.
    [Fact]
    public void Le_mode_d_ecran_garde_son_annulation()
    {
        File.WriteAllText(Fichier("leviers-appliques.json"), JsonSerializer.Serialize(new[]
        {
            new TweakSnapshot("fixit.ecran_frequence", "{\"Peripherique\":\"\\\\\\\\.\\\\DISPLAY1\",\"Hz\":144}"),
        }));

        new JournalDesLeviersAppliques(Fichier("leviers-appliques.json")).Lire().Should().ContainSingle();
    }
}
