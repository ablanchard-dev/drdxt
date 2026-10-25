using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;
using DrDxt.ElevatedHelper.Execution.Adlx;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// ADLXTuningActions : la couture d'écriture ADLX (AMD), miroir de NvApiTuningActions.
// Tout se prouve sans carte graphique parce que la frontière native (IAdlxNative) est
// remplacée par FakeAdlxNative : un test unitaire n'écrit jamais un delta d'horloge ou de
// puissance sur du vrai matériel, donc le vrai chemin SetTuning n'est exercé que contre le
// faux (la vraie écriture ADLX se vérifie à part, sur banc).
//
// Ce que ces tests figent (le contrat du helper, hérité de la spécification et du précédent NVAPI) :
//   1. Lecture avant écriture : Undervolt lit l'état GFX courant avant d'écrire.
//   2. MaxFreq seule, tension inchangée : Undervolt écrit tel quel la cible MaxFreq absolue
//      qu'on lui donne (l'App a déjà converti le décalage de l'utilisateur en cible absolue)
//      et renvoie la tension courante telle quelle, si bien que la frontière native saute
//      l'écriture de tension (ce qui garde RDNA4 sain).
//   3. PowerLimit passe par TrySetPowerLimit avec le pourcentage exact et ne touche pas au GFX.
//   4. Défense en profondeur sur la plage pilote : une cible absolue hors de la plage MaxFreq
//      que le pilote annonce lui-même est refusée par le helper (refuser, jamais borner) et
//      n'atteint jamais l'écriture pilote. (La bande prudente de décalage vit côté App ; la
//      bande en % de PowerLimit est tout de même recopiée et imposée ici.)
//   5. Échec propre : ADLX absent, lecture en faute, écriture en faute, genre inconnu, mauvais
//      index renvoient tous Result.Fail(HELPER.OP_FAILED), jamais un faux Ok, jamais une exception.
// -----------------------------------------------------------------------

/// <summary>Faux pilotable de la frontière native ADLX. Enregistre l'ordre et les arguments
/// de chaque appel natif pour prouver sans carte graphique le contrat lecture-avant-écriture,
/// l'écriture MaxFreq seule et l'échec propre par refus par défaut. Ne touche jamais au code
/// natif.</summary>
internal sealed class FakeAdlxNative : IAdlxNative
{
    public bool IsAvailable { get; set; } = true;

    // Résultat de lecture configurable (l'état GFX courant que le helper relit).
    public bool ReadGfxSucceeds { get; set; } = true;
    public AdlxGfxReads GfxReads { get; set; }

    // Résultats d'écriture configurables.
    public bool SetGfxSucceeds { get; set; } = true;
    public bool SetPowerSucceeds { get; set; } = true;

    // Injection de faute : faire lever n'importe quel appel natif, pour prouver que le helper ne
    // laisse aucune exception traverser l'IPC (elle doit devenir un Result.Fail propre).
    public bool ThrowOnAnyCall { get; set; }

    // Interaction native enregistrée (ordre et arguments d'écriture capturés).
    public List<string> Calls { get; } = new();
    public int? SetGfxMaxFreqMhz { get; private set; }
    public int? SetGfxVoltMv { get; private set; }
    public int? SetPowerPct { get; private set; }

    public bool TryReadGfx(int gpuIndex, out AdlxGfxReads reads)
    {
        Calls.Add(nameof(TryReadGfx));
        if (ThrowOnAnyCall) throw new InvalidOperationException("simulated native fault");
        reads = GfxReads;
        return ReadGfxSucceeds;
    }

    // Un échec qui a quand même écrit la fréquence (écriture partielle), et un retour qui ne prend pas.
    public bool EcrireMemeEnEchec { get; set; }
    public int? PiloteRamene { get; set; }
    public bool RetourRefuse { get; set; }
    private bool _dejaEcrit;

    public bool TrySetGfx(int gpuIndex, int newMaxFreqMhz, int newVoltMv, out AdlxGfxReads original)
    {
        Calls.Add(nameof(TrySetGfx));
        SetGfxMaxFreqMhz = newMaxFreqMhz;
        SetGfxVoltMv = newVoltMv;
        original = GfxReads;
        if (ThrowOnAnyCall) throw new InvalidOperationException("simulated native fault");
        if (EcrireMemeEnEchec && !(_dejaEcrit && RetourRefuse)) GfxReads = GfxReads with { MaxFreqMhz = newMaxFreqMhz };
        // Une écriture acceptée prend, sauf un pilote qui ramène la cible à sa propre valeur (PiloteRamene).
        else if (SetGfxSucceeds && !EcrireMemeEnEchec) GfxReads = GfxReads with { MaxFreqMhz = PiloteRamene ?? newMaxFreqMhz };
        _dejaEcrit = true;
        return SetGfxSucceeds;
    }

    public bool TrySetPowerLimit(int gpuIndex, int newPowerLimitPct, out AdlxPowerReads original)
    {
        Calls.Add(nameof(TrySetPowerLimit));
        SetPowerPct = newPowerLimitPct;
        original = default;
        if (ThrowOnAnyCall) throw new InvalidOperationException("simulated native fault");
        return SetPowerSucceeds;
    }

    // Membres en lecture seule que la couture d'écriture n'utilise pas : échec propre, jamais journalisés comme une écriture.
    public bool TryReadPower(int gpuIndex, out AdlxPowerReads reads) { reads = default; return false; }
    public bool TryReadFan(int gpuIndex, out AdlxFanReads reads) { reads = default; return false; }
    public IReadOnlyList<GpuProbeStep> ProbeGfxPath(int gpuIndex) => Array.Empty<GpuProbeStep>();

    public string? AntiLagRefus { get; set; }
    public bool? AntiLagEcrit { get; private set; }

    public int? FrtcEcrit { get; private set; }
    public int? VsyncEcrit { get; private set; }

    public string? EcrireVsync(int mode)
    {
        Calls.Add(nameof(EcrireVsync));
        VsyncEcrit = mode;
        return null;
    }

    public string? EcrireFrtc(int code)
    {
        Calls.Add(nameof(EcrireFrtc));
        FrtcEcrit = code;
        return null;
    }

    public int Usine { get; private set; }
    public string? UsineRefus { get; set; }
    /// <summary>Le pilote dit la remise faite (code 0) sans rien remettre.</summary>
    public bool UsineSansEffet { get; set; }
    /// <summary>IsAtFactory ne se lit pas.</summary>
    public bool UsineIllisible { get; set; }
    private bool _aLUsine;
    public bool? EstALUsine(int gpuIndex) => UsineIllisible ? null : _aLUsine;

    public string? RemettreReglageUsine(int gpuIndex)
    {
        Calls.Add(nameof(RemettreReglageUsine));
        Usine++;
        if (UsineRefus is null && !UsineSansEffet) { GfxReads = GfxReads with { MaxFreqMhz = 2900 }; _aLUsine = true; }
        return UsineRefus;
    }

    public string? EcrireAntiLag(bool actif)
    {
        Calls.Add(nameof(EcrireAntiLag));
        if (AntiLagRefus is null) AntiLagEcrit = actif;
        return AntiLagRefus;
    }
}

public sealed class ADLXTuningActionsTests
{
    // Un état GFX courant aux plages larges, pour qu'un décalage dans la bande soit lui-même dans la plage.
    private static AdlxGfxReads StockGfx(int maxFreqMhz = 2900, int voltMv = 1100) => new()
    {
        MaxFreqMhz = maxFreqMhz,
        MaxFreqRange = new AdlxIntRange { Min = 500, Max = 4000, Step = 1 },
        VoltMv = voltMv,
        VoltRange = new AdlxIntRange { Min = 0, Max = 1200, Step = 1 },
        MinFreqMhz = 500,
        MinFreqRange = new AdlxIntRange { Min = 500, Max = 4000, Step = 1 },
    };

    // --- 1+2 : lecture avant écriture, MaxFreq seule, tension inchangée ------------------------

    [Fact]
    public void Undervolt_reads_then_writes_the_absolute_target_maxfreq_only_with_voltage_unchanged()
    {
        var fake = new FakeAdlxNative { GfxReads = StockGfx(maxFreqMhz: 2900, voltMv: 1100) };
        var actions = new ADLXTuningActions(fake);

        // L'op porte une cible MaxFreq absolue (l'App a déjà converti le décalage de l'utilisateur
        // en cible absolue en lisant la MaxFreq courante).
        var result = actions.SetTuning(0, AdlxTuningKind.Undervolt, 2800);

        result.IsOk.Should().BeTrue("an in-range absolute target with a healthy GPU must apply");
        // Lecture avant écriture, puis relecture de la valeur écrite.
        fake.Calls.Should().Equal(nameof(IAdlxNative.TryReadGfx), nameof(IAdlxNative.TrySetGfx), nameof(IAdlxNative.TryReadGfx));
        // La cible absolue est écrite telle quelle : aucune arithmétique de décalage côté helper.
        fake.SetGfxMaxFreqMhz.Should().Be(2800);
        // La tension repart inchangée, preuve que seule MaxFreq est écrite (la frontière native
        // saute alors l'écriture de tension sans effet, ce qui garde RDNA4 sain).
        fake.SetGfxVoltMv.Should().Be(1100);
    }

    // --- 3 : PowerLimit passe par TrySetPowerLimit, ne touche pas au GFX ----------------------

    [Fact]
    public void PowerLimit_routes_to_TrySetPowerLimit_with_the_exact_percent_and_no_gfx_write()
    {
        var fake = new FakeAdlxNative();
        var actions = new ADLXTuningActions(fake);

        var result = actions.SetTuning(0, AdlxTuningKind.PowerLimit, 80);

        result.IsOk.Should().BeTrue();
        fake.Calls.Should().Equal(nameof(IAdlxNative.TrySetPowerLimit));
        fake.SetPowerPct.Should().Be(80);
        fake.SetGfxMaxFreqMhz.Should().BeNull("a power-limit write must never touch GFX tuning");
    }

    // --- 4 : défense en profondeur sur la plage pilote : une cible absolue hors de la plage
    //        MaxFreq annoncée par le pilote est refusée et n'atteint jamais l'écriture --------
    // StockGfx annonce une plage MaxFreq matérielle de [500, 4000] MHz. Le helper lit d'abord
    // l'état GFX (la plage vient de cette lecture) puis refuse toute cible absolue en dehors :
    // la lecture a lieu même pour une cible refusée, l'écriture jamais.

    [Theory]
    [InlineData(2800, true)]            // dans la plage pilote et dans la bande : atteint l'écriture
    [InlineData(500, false)]            // bord bas matériel, mais 2400 MHz sous la lecture : hors bande
    [InlineData(499, false)]            // un cran sous le plancher pilote : refusé
    [InlineData(4001, false)]           // un cran au-dessus du plafond pilote : refusé
    [InlineData(int.MinValue, false)]   // total, sans débordement
    [InlineData(int.MaxValue, false)]
    public void Undervolt_rejects_a_target_outside_the_driver_hardware_range(int targetMhz, bool reachesDriver)
    {
        var fake = new FakeAdlxNative { GfxReads = StockGfx() }; // range [500, 4000]
        var actions = new ADLXTuningActions(fake);

        var result = actions.SetTuning(0, AdlxTuningKind.Undervolt, targetMhz);

        if (reachesDriver)
        {
            result.IsOk.Should().BeTrue();
            fake.Calls.Should().Equal(nameof(IAdlxNative.TryReadGfx), nameof(IAdlxNative.TrySetGfx), nameof(IAdlxNative.TryReadGfx));
            fake.SetGfxMaxFreqMhz.Should().Be(targetMhz, "an in-range target is written verbatim");
        }
        else
        {
            result.IsOk.Should().BeFalse();
            result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
            // La lecture a eu lieu (la plage en vient), l'écriture jamais. Le plafond d'overclock dans la plage (4000 MHz)
            // est au-dessus de la bande : Au_dessus_de_la_bande_rien_n_est_ecrit_et_la_remise_a_l_usine_est_demandee.
            fake.Calls.Should().Equal(nameof(IAdlxNative.TryReadGfx));
            fake.SetGfxMaxFreqMhz.Should().BeNull("an out-of-range target must NEVER reach the driver write");
        }
    }

    [Theory]
    [InlineData(60, true)]              // plancher : atteint le pilote
    [InlineData(100, true)]             // plafond : atteint le pilote
    [InlineData(80, true)]
    [InlineData(59, false)]             // sous le plancher : refusé
    [InlineData(101, false)]            // au-dessus du plafond : refusé
    [InlineData(int.MinValue, false)]
    [InlineData(int.MaxValue, false)]
    public void PowerLimit_enforces_the_safe_bounds_band(int pct, bool reachesDriver)
    {
        var fake = new FakeAdlxNative();
        var actions = new ADLXTuningActions(fake);

        var result = actions.SetTuning(0, AdlxTuningKind.PowerLimit, pct);

        if (reachesDriver)
        {
            result.IsOk.Should().BeTrue();
            fake.Calls.Should().Equal(nameof(IAdlxNative.TrySetPowerLimit));
            fake.SetPowerPct.Should().Be(pct);
        }
        else
        {
            result.IsOk.Should().BeFalse();
            result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
            fake.Calls.Should().BeEmpty("an out-of-band power limit must NEVER touch the driver");
        }
    }

    // --- 5 : échec propre, jamais un faux Ok, jamais une exception ---------------------------

    [Fact]
    public void Unavailable_adlx_fails_clean_and_writes_nothing()
    {
        var fake = new FakeAdlxNative { IsAvailable = false, GfxReads = StockGfx() };
        var actions = new ADLXTuningActions(fake);

        var undervolt = actions.SetTuning(0, AdlxTuningKind.Undervolt, -50);
        var power = actions.SetTuning(0, AdlxTuningKind.PowerLimit, 80);

        undervolt.IsOk.Should().BeFalse();
        undervolt.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        power.IsOk.Should().BeFalse();
        power.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        fake.Calls.Should().BeEmpty("no native call may be made when ADLX is unavailable");
    }

    [Fact]
    public void Negative_gpu_index_fails_clean_and_writes_nothing()
    {
        var fake = new FakeAdlxNative { GfxReads = StockGfx() };
        var actions = new ADLXTuningActions(fake);

        var result = actions.SetTuning(-1, AdlxTuningKind.Undervolt, -50);

        result.IsOk.Should().BeFalse("a negative GPU index is never valid");
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        fake.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Gfx_read_failure_fails_clean_and_never_writes()
    {
        var fake = new FakeAdlxNative { ReadGfxSucceeds = false, GfxReads = StockGfx() };
        var actions = new ADLXTuningActions(fake);

        var result = actions.SetTuning(0, AdlxTuningKind.Undervolt, 2800);

        result.IsOk.Should().BeFalse("a failed pre-write read must abort before any write");
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        fake.Calls.Should().Equal(nameof(IAdlxNative.TryReadGfx));
    }

    [Fact]
    public void Gfx_write_failure_fails_clean_and_never_reports_ok()
    {
        var fake = new FakeAdlxNative { SetGfxSucceeds = false, GfxReads = StockGfx() };
        var actions = new ADLXTuningActions(fake);

        // Une cible absolue dans la plage, pour que le chemin atteigne l'écriture (c'est son échec qu'on teste).
        var result = actions.SetTuning(0, AdlxTuningKind.Undervolt, 2800);

        result.IsOk.Should().BeFalse("a driver write failure must NEVER surface as Ok");
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        // Relue après l'échec ; inchangée, rien d'autre n'est écrit.
        fake.Calls.Should().Equal(nameof(IAdlxNative.TryReadGfx), nameof(IAdlxNative.TrySetGfx), nameof(IAdlxNative.TryReadGfx));
    }

    // Une écriture refusée qui en a laissé une partie est rendue ; si elle reste, l'échec le DIT.
    [Fact]
    public void Gfx_partial_write_is_put_back_and_said_when_it_stays()
    {
        var rendue = new FakeAdlxNative { SetGfxSucceeds = false, GfxReads = StockGfx(), EcrireMemeEnEchec = true };
        var r = new ADLXTuningActions(rendue).SetTuning(0, AdlxTuningKind.Undervolt, 2800);
        r.IsOk.Should().BeFalse();
        rendue.GfxReads.MaxFreqMhz.Should().Be(2900, "la fréquence d'origine est reposée");
        r.Error!.Value.Message.Should().NotContain("left MaxFreq");

        var restee = new FakeAdlxNative { SetGfxSucceeds = false, GfxReads = StockGfx(), EcrireMemeEnEchec = true, RetourRefuse = true };
        var r2 = new ADLXTuningActions(restee).SetTuning(0, AdlxTuningKind.Undervolt, 2800);
        r2.IsOk.Should().BeFalse();
        r2.Error!.Value.Message.Should().Contain("left MaxFreq at 2800 MHz").And.Contain("was 2900 MHz");
    }

    [Fact]
    public void Power_write_failure_fails_clean_and_never_reports_ok()
    {
        var fake = new FakeAdlxNative { SetPowerSucceeds = false };
        var actions = new ADLXTuningActions(fake);

        var result = actions.SetTuning(0, AdlxTuningKind.PowerLimit, 80);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        fake.Calls.Should().Equal(nameof(IAdlxNative.TrySetPowerLimit));
    }

    // Anti-Lag s'écrit (0 ou 1) par la couture native ; son refus remonte, une autre valeur n'y va pas.
    [Fact]
    public void AntiLag_writes_through_the_native_seam_and_its_refusal_surfaces()
    {
        var fake = new FakeAdlxNative();
        var actions = new ADLXTuningActions(fake);

        actions.SetTuning(0, AdlxTuningKind.AntiLag, 1).IsOk.Should().BeTrue();
        fake.AntiLagEcrit.Should().BeTrue();
        actions.SetTuning(0, AdlxTuningKind.AntiLag, 0).IsOk.Should().BeTrue();
        fake.AntiLagEcrit.Should().BeFalse();

        fake.AntiLagRefus = "Anti-Lag n'est pas pris en charge par cette carte.";
        var refus = actions.SetTuning(0, AdlxTuningKind.AntiLag, 1);
        refus.IsOk.Should().BeFalse();
        refus.Error!.Value.Message.Should().Contain("pas pris en charge");
    }

    // FRTC passe son code tel quel (cible ou moins la cible) ; 0 n'atteint pas le pilote.
    [Fact]
    public void FrameRateTarget_passes_its_code_and_zero_never_reaches_the_driver()
    {
        var fake = new FakeAdlxNative();
        var actions = new ADLXTuningActions(fake);

        actions.SetTuning(0, AdlxTuningKind.FrameRateTarget, 141).IsOk.Should().BeTrue();
        fake.FrtcEcrit.Should().Be(141);
        actions.SetTuning(0, AdlxTuningKind.FrameRateTarget, -60).IsOk.Should().BeTrue();
        fake.FrtcEcrit.Should().Be(-60);

        var zero = new FakeAdlxNative();
        new ADLXTuningActions(zero).SetTuning(0, AdlxTuningKind.FrameRateTarget, 0).IsOk.Should().BeFalse();
        zero.Calls.Should().BeEmpty();
    }

    // La Vsync du pilote AMD, mode 0 à 3 ; un autre mode n'atteint pas le pilote.
    [Fact]
    public void VerticalRefresh_passes_modes_0_to_3_and_refuses_the_rest()
    {
        var fake = new FakeAdlxNative();
        var actions = new ADLXTuningActions(fake);

        actions.SetTuning(0, AdlxTuningKind.VerticalRefresh, 3).IsOk.Should().BeTrue();
        fake.VsyncEcrit.Should().Be(3);
        actions.SetTuning(0, AdlxTuningKind.VerticalRefresh, 0).IsOk.Should().BeTrue();
        fake.VsyncEcrit.Should().Be(0);

        var hors = new FakeAdlxNative();
        new ADLXTuningActions(hors).SetTuning(0, AdlxTuningKind.VerticalRefresh, 4).IsOk.Should().BeFalse();
        hors.Calls.Should().BeEmpty();
    }

    [Fact]
    public void AntiLag_value_other_than_0_or_1_is_refused_before_the_driver()
    {
        var fake = new FakeAdlxNative();

        new ADLXTuningActions(fake).SetTuning(0, AdlxTuningKind.AntiLag, 2).IsOk.Should().BeFalse();
        fake.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Undefined_kind_fails_clean_and_writes_nothing()
    {
        var fake = new FakeAdlxNative { GfxReads = StockGfx() };
        var actions = new ADLXTuningActions(fake);

        var result = actions.SetTuning(0, (AdlxTuningKind)99, 0);

        result.IsOk.Should().BeFalse("an undefined enum value must be refused, never a crash");
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        fake.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Never_throws_even_when_the_native_boundary_faults()
    {
        var fake = new FakeAdlxNative { ThrowOnAnyCall = true, GfxReads = StockGfx() };
        var actions = new ADLXTuningActions(fake);

        var act = () => actions.SetTuning(0, AdlxTuningKind.Undervolt, -50);

        var result = act.Should().NotThrow(
            "an exception must NEVER cross the IPC — it is caught and returned as a clean Result").Subject;
        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public void Public_constructor_wires_the_real_native_boundary_without_throwing()
    {
        // Le constructeur sans paramètre branche le vrai AdlxNative (initialisation paresseuse, aucun
        // appel ADLX ici) : construire la couture reste sûr même sur une machine sans AMD.
        var act = () => new ADLXTuningActions();

        act.Should().NotThrow();
    }
    // La plage du pilote monte jusqu'au plafond d'overclock : la bande prudente de l'App ([-200, +100] MHz) s'applique aussi
    // dans le helper, autour de la MaxFreq retenue à ce démarrage, pas de la valeur lue (sinon +100 en +100).
    [Theory]
    [InlineData(2700, true)]
    [InlineData(3000, true)]
    [InlineData(2699, false)]
    public void Undervolt_reste_dans_la_bande_autour_de_la_frequence_du_demarrage(int cible, bool ecrit)
    {
        var fake = new FakeAdlxNative { GfxReads = StockGfx(maxFreqMhz: 2950) }; // déjà montée de 50 depuis le démarrage
        var actions = new ADLXTuningActions(fake, (_, _) => 2900);

        actions.SetTuning(0, AdlxTuningKind.Undervolt, cible).IsOk.Should().Be(ecrit);
        if (!ecrit) fake.SetGfxMaxFreqMhz.Should().BeNull("hors bande, rien n'atteint le pilote");
    }

    // Au-dessus de la bande (une sous-tension gardée au redémarrage, à rendre) : jamais la fréquence demandée, la remise à
    // l'usine du pilote, dite au joueur même réussie (elle remet aussi la mémoire, le ventilateur et la puissance).
    [Fact]
    public void Au_dessus_de_la_bande_rien_n_est_ecrit_et_la_remise_a_l_usine_est_demandee()
    {
        var fake = new FakeAdlxNative { GfxReads = StockGfx(maxFreqMhz: 2700) };
        var actions = new ADLXTuningActions(fake, (_, _) => 2700);

        var r = actions.SetTuning(0, AdlxTuningKind.Undervolt, 2900);

        fake.Usine.Should().Be(0, "la remise à l'usine est un geste du joueur, jamais celui d'une annulation");
        fake.SetGfxMaxFreqMhz.Should().BeNull("la fréquence demandée au-dessus de la bande n'est jamais écrite");
        r.Error!.Value.Code.Should().Be(ErrorCodes.GpuRemiseUsineRequise);
        r.Error.Value.Message.Should().Be(Docteur.T("ADLX.UsineRequise", 2900));
    }

    // Le geste à part : la remise à l'usine, et rien d'autre.
    [Fact]
    public void La_remise_a_l_usine_demandee_remet_la_carte_sans_ecrire_de_frequence()
    {
        var fake = new FakeAdlxNative { GfxReads = StockGfx(maxFreqMhz: 2500) };

        new ADLXTuningActions(fake, (_, _) => 2500).SetTuning(0, AdlxTuningKind.RemiseUsine, 0).IsOk.Should().BeTrue();

        fake.Usine.Should().Be(1);
        fake.SetGfxMaxFreqMhz.Should().BeNull();
    }

    // Le code 0 de ResetToFactory ne prouve rien : la carte relue (IsAtFactory) doit se dire à l'usine, sinon l'App dirait
    // « remis » et sortirait le levier du journal.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Une_remise_a_l_usine_que_la_carte_ne_confirme_pas_est_un_echec(bool sansEffet, bool illisible)
    {
        var fake = new FakeAdlxNative { GfxReads = StockGfx(maxFreqMhz: 2700), UsineSansEffet = sansEffet, UsineIllisible = illisible };

        var r = new ADLXTuningActions(fake, (_, _) => 2700).SetTuning(0, AdlxTuningKind.RemiseUsine, 0);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().StartWith(Docteur.T("ADLX.UsineImpossible", "x")[..20]);
    }

    [Fact]
    public void Une_remise_a_l_usine_ratee_dit_le_geste_dans_AMD_Software()
    {
        var fake = new FakeAdlxNative { GfxReads = StockGfx(maxFreqMhz: 2700), UsineRefus = "refus du pilote" };

        var r = new ADLXTuningActions(fake, (_, _) => 2700).SetTuning(0, AdlxTuningKind.RemiseUsine, 0);

        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        r.Error.Value.Message.Should().Be(Docteur.T("ADLX.UsineImpossible", "refus du pilote"));
    }

    // Une fois à la fréquence demandée (après la remise à l'usine), l'essai suivant n'écrit rien et rend Ok.
    [Fact]
    public void Deja_a_la_frequence_demandee_rien_n_est_ecrit()
    {
        var fake = new FakeAdlxNative { GfxReads = StockGfx(maxFreqMhz: 2900) };

        new ADLXTuningActions(fake, (_, _) => 2700).SetTuning(0, AdlxTuningKind.Undervolt, 2900).IsOk.Should().BeTrue();
        fake.Usine.Should().Be(0);
        fake.SetGfxMaxFreqMhz.Should().BeNull();
    }

    [Fact]
    public void Undervolt_ne_s_ecrit_pas_sans_frequence_du_demarrage_retenue()
    {
        var fake = new FakeAdlxNative { GfxReads = StockGfx() };
        var actions = new ADLXTuningActions(fake, (_, _) => null);

        actions.SetTuning(0, AdlxTuningKind.Undervolt, 2800).IsOk.Should().BeFalse();
        fake.SetGfxMaxFreqMhz.Should().BeNull();
    }

    // Un pilote qui ramène la cible ne laisse pas une valeur que personne n'a demandée : l'origine revient, l'échec est dit.
    [Fact]
    public void Undervolt_non_relu_a_la_cible_rend_l_origine_et_echoue()
    {
        var fake = new FakeAdlxNative { GfxReads = StockGfx(maxFreqMhz: 2900), PiloteRamene = 2850 };
        var actions = new ADLXTuningActions(fake);

        var r = actions.SetTuning(0, AdlxTuningKind.Undervolt, 2800);

        r.IsOk.Should().BeFalse();
        fake.SetGfxMaxFreqMhz.Should().Be(2900, "la dernière écriture repose l'origine");
    }

    // L'origine qui ne revient pas porte le code qui garde le bail (son expiration la rend) et le journal : l'écran ne dit pas
    // « rendue ». Revenue, l'échec reste générique.
    [Fact]
    public void Une_origine_qui_ne_revient_pas_porte_le_code_qui_garde_le_bail_et_le_journal()
    {
        var restee = new FakeAdlxNative { SetGfxSucceeds = false, GfxReads = StockGfx(), EcrireMemeEnEchec = true, RetourRefuse = true };
        new ADLXTuningActions(restee).SetTuning(0, AdlxTuningKind.Undervolt, 2800).Error!.Value.Code
            .Should().Be(ErrorCodes.TweakPartiallyApplied, "écriture partielle restée");

        var ramenee = new FakeAdlxNative { GfxReads = StockGfx(maxFreqMhz: 2900), PiloteRamene = 2850 };
        new ADLXTuningActions(ramenee).SetTuning(0, AdlxTuningKind.Undervolt, 2800).Error!.Value.Code
            .Should().Be(ErrorCodes.TweakPartiallyApplied, "le pilote ramène aussi l'origine : 2850 reste");

        var rendue = new FakeAdlxNative { SetGfxSucceeds = false, GfxReads = StockGfx(), EcrireMemeEnEchec = true };
        new ADLXTuningActions(rendue).SetTuning(0, AdlxTuningKind.Undervolt, 2800).Error!.Value.Code
            .Should().Be(ErrorCodes.HelperOpFailed, "l'origine est revenue : rien ne reste");
    }
}
