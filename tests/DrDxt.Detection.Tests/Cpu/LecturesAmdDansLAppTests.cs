using DrDxt.Core;
using DrDxt.Detection.Cpu;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Cpu;

// Les lectures AMD déjà écrites (Tctl, S5_RESET_STATUS, timings du contrôleur), branchées dans l'App derrière
// LecturesAmdProfondes.ValideesEnVie. Porte fermée : rien n'est appelé et le docteur dit ce qu'il disait. Porte ouverte
// (ici seulement, avec un faux PawnIO) : la valeur décodée arrive au constat. Aucune lecture PawnIO réelle.
public sealed class LecturesAmdDansLAppTests
{
    private const uint Zen3 = 0x00A20F10u; // famille 19h, modèle 21h
    private static readonly GenerationAmd.Ligne Generation = GenerationAmd.Reconnaitre("AuthenticAMD", Zen3)!;
    private static readonly IReadOnlySet<string> Fermee = new HashSet<string>();
    private static IReadOnlySet<string> Ouverte => new HashSet<string> { Generation.Generation };

    // Tctl à 65 °C : bits 31:21 par pas de 0,125 °C (k10temp).
    private static uint? SmnTctl(uint adresse) => adresse == TemperatureAmd.AdresseTctl ? 520u << 21 : null;

    [Fact]
    public void Porte_fermee_la_temperature_n_appelle_rien()
    {
        var appels = 0;
        LecturesAmd.DerrierePorte(Fermee, () => { appels++; return Generation; }, () => { appels++; return true; },
            () => { appels++; return 65; }).Should().BeNull();
        appels.Should().Be(0, "porte vide : ni le CPUID, ni PawnIO, ni le service");
    }

    [Fact]
    public void Une_autre_generation_validee_n_ouvre_pas_celle_ci()
    {
        var lu = false;
        LecturesAmd.DerrierePorte(new HashSet<string> { "une autre" }, () => Generation, () => true, () => { lu = true; return 65; })
            .Should().BeNull();
        lu.Should().BeFalse();
    }

    [Fact]
    public void Porte_ouverte_la_temperature_decodee_arrive()
        => LecturesAmd.DerrierePorte(Ouverte, () => Generation, () => true,
                () => LecturesAmd.Temperature("AMD Ryzen 7 5800X", Zen3, SmnTctl))!
            .Celsius.Should().Be(65);

    [Fact]
    public void PawnIO_absent_ou_sans_droits_rien_n_est_lu_meme_porte_ouverte()
    {
        var lu = false;
        LecturesAmd.DerrierePorte(Ouverte, () => Generation, () => false, () => { lu = true; return 65; }).Should().BeNull();
        lu.Should().BeFalse();
    }

    [Fact]
    public void La_lecture_SMN_reste_bornee_aux_adresses_permises()
    {
        var demandees = new List<uint>();
        LecturesAmd.Temperature(null, Zen3, a => { demandees.Add(a); return 520u << 21; });
        LecturesAmd.CanauxMemoire(a => { demandees.Add(a); return 0; });
        demandees.Should().NotBeEmpty().And.OnlyContain(a => SmnAmd.Autorisee(a));
    }

    // Valeurs inventées, disposition de TimingsMemoireAmd : DDR4-3467 17-20-20-41, tRC 61, tRFC 610, sur le canal 0 seul.
    private static readonly Dictionary<uint, uint> Ddr4 = new()
    {
        [0x50200] = 52 | (1u << 11),
        [0x50204] = 17 | (41u << 8) | (20u << 16) | (20u << 24),
        [0x50208] = 61 | (20u << 16),
        [0x50260] = 610,
        [0x50264] = 610,
    };

    private static uint? SmnCanal0(uint adresse) => Ddr4.TryGetValue(adresse, out var v) ? v : 0;

    private static List<RepairFinding> Docteur(IReadOnlySet<string> validees, Action appel) =>
        LecturesAmdProfondes.Juger(Generation, pawnIoInstalle: true, () => RepairAuditService.ConstatsAmdProfonds(Generation,
            typesSmbios: [26, 26], vitesseKit: 3200, bios: null, carte: null, versionBios: null,
            canaux: () => { appel(); return LecturesAmd.CanauxMemoire(SmnCanal0); },
            reset: () => { appel(); return 1u << 1; }), validees).ToList();

    [Fact]
    public void Porte_fermee_le_docteur_dit_une_ligne_non_lue_et_ne_lit_rien()
    {
        var appels = 0;
        var constats = Docteur(Fermee, () => appels++);

        appels.Should().Be(0);
        constats.Should().ContainSingle().Which.Explication.Should()
            .Be(DrDxt.Core.Docteur.T("LAP.NonValidee", DrDxt.Core.Docteur.T("LAP.Liste"), Generation.Generation));
    }

    [Fact]
    public void Porte_ouverte_les_timings_et_le_registre_de_redemarrage_arrivent_au_docteur()
    {
        var constats = Docteur(Ouverte, () => { });

        constats.Single(c => c.Nom == "timings appliques").ValeurCourante.Should().StartWith("A 3467 MT/s 17-20-20-41");
        constats.Single(c => c.Nom == ResetAmd.Nom).ValeurCourante.Should().Be("0x00000002");
        // Les timings appliqués lus, le réglé contre l'appliqué n'attend plus que le BIOS nommé.
        constats.Single(c => c.Nom == "regle contre applique (memoire)").Explication.Should().Be(DrDxt.Core.Docteur.T("RCA.BiosNonNomme"));
    }

    [Fact]
    public void Porte_ouverte_sans_lecture_chaque_constat_dit_non_lu()
    {
        var constats = LecturesAmdProfondes.Juger(Generation, pawnIoInstalle: true, () => RepairAuditService.ConstatsAmdProfonds(Generation,
            [26], 3200, null, null, null, canaux: () => null, reset: () => null), Ouverte).ToList();

        constats.Single(c => c.Nom == "timings appliques").Explication.Should().Be(DrDxt.Core.Docteur.T("TMA.NonLus"));
        constats.Single(c => c.Nom == ResetAmd.Nom).Explication.Should().Be(DrDxt.Core.Docteur.T("S5.NonLu"));
        constats.Single(c => c.Nom == "regle contre applique (memoire)").Explication.Should().Be(DrDxt.Core.Docteur.T("RCA.AppliquesNonLus"));
    }
}
