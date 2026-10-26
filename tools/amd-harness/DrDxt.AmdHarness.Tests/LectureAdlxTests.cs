using System.Globalization;
using DrDxt.AmdHarness.Reporting;
using DrDxt.Detection.Gpu.Adlx;
using FluentAssertions;
using Xunit;

namespace DrDxt.AmdHarness.Tests;

/// <summary>« --lire » montre chaque réglage que DrDXT écrit sur une carte AMD, pour qu'un testeur le compare à AMD
/// Software : la Vsync du pilote comprise, que le plafond VRR met « toujours activée ». Il montre aussi ce que DrDXT lit
/// sans l'écrire (Enhanced Sync, réglages Radeon, mesures de la carte, FreeSync de chaque écran), par les seules entrées
/// de lecture d'ADLX : la carte en doublure refuse toute écriture.</summary>
public sealed class LectureAdlxTests
{
    [Theory]
    [InlineData(0, "toujours coupée")]
    [InlineData(1, "coupée sauf avis du jeu")]
    [InlineData(2, "activée sauf avis du jeu")]
    [InlineData(3, "toujours activée")]
    public void La_vsync_du_pilote_a_sa_ligne(int mode, string attendu)
        => LectureAdlx.Lignes(new AdlxReglagesLus(VsyncMode: mode), true).Should().Contain($"Vsync         : {attendu}");

    [Fact]
    public void Une_vsync_non_lue_le_dit()
        => LectureAdlx.Lignes(new AdlxReglagesLus(), true).Should().Contain("Vsync         : non lue");

    // La limite de puissance et sa plage, telles qu'ADLX les rend : c'est ce qui dira si l'unité est un % du TDP ou un
    // décalage autour de 0, le jour où un testeur AMD les met à côté d'AMD Software.
    [Fact]
    public void La_limite_de_puissance_se_dit_avec_sa_plage_et_sinon_non_lue()
    {
        LectureAdlx.LignesPuissance([(1, new AdlxPowerReads { PowerLimitPct = 0, PowerRange = new AdlxIntRange { Min = -10, Max = 15, Step = 1 } })])
            .Should().ContainSingle().Which.Should().Be("Puissance (carte 1) : limite 0, plage -10 à 15, pas 1");
        LectureAdlx.LignesPuissance([]).Should().Equal("Puissance     : non lue");
    }

    // Les lignes d'avant restent, dans leur ordre, et la limite de puissance ferme toujours le bloc ADLX.
    [Fact]
    public void Les_lignes_d_avant_restent_dans_leur_ordre()
    {
        var carte = new CarteEnLecture
        {
            Reglages = new AdlxReglagesLus(Sam: true, AntiLag: false, FrtcActif: true, FrtcFps: 141, ChillActif: false, VsyncMode: 3),
            Puissances = new Dictionary<int, AdlxPowerReads> { [1] = new() { PowerLimitPct = 0, PowerRange = new AdlxIntRange { Min = -10, Max = 15, Step = 1 } } },
        };

        var l = LectureAdlx.Lire(carte, true);

        l.Should().ContainInOrder(
            "amdadlx64.dll dans System32 : présente",
            "ADLX : carte dédiée trouvée",
            "SAM (ReBAR)   : activé",
            "Anti-Lag      : désactivé",
            "FRTC          : activé, 141 images/s",
            "Chill         : désactivé",
            "Limite images : 141 images/s",
            "Vsync         : toujours activée",
            "Température   : non lue",
            "Puissance (carte 1) : limite 0, plage -10 à 15, pas 1");
        l[^1].Should().Be("Puissance (carte 1) : limite 0, plage -10 à 15, pas 1");
    }

    // ── Enhanced Sync et réglages Radeon (LireReglages) ─────────────────────────────────

    // Non pris en charge ou non lu se dit « non lu » : « désactivé » ferait croire à un choix du joueur.
    [Theory]
    [InlineData(true, "activé")]
    [InlineData(false, "désactivé")]
    [InlineData(null, "non lu")]
    public void Enhanced_Sync_et_chaque_reglage_Radeon_ont_leur_ligne(bool? etat, string attendu)
    {
        var carte = new CarteEnLecture
        {
            Reglages = new AdlxReglagesLus(EnhancedSyncActif: etat, BoostActif: etat, ImageSharpeningActif: etat, RsrActif: etat, AfmfActif: etat),
        };

        LectureAdlx.Lire(carte, true).Should().Contain($"Enhanced Sync : {attendu}")
            .And.Contain($"Radeon Boost  : {attendu}")
            .And.Contain($"Image Sharpening : {attendu}")
            .And.Contain($"RSR           : {attendu}")
            .And.Contain($"AFMF          : {attendu}");
    }

    [Fact]
    public void Chaque_reglage_Radeon_se_lit_sur_son_propre_champ()
    {
        var carte = new CarteEnLecture
        {
            Reglages = new AdlxReglagesLus(EnhancedSyncActif: false, BoostActif: true, ImageSharpeningActif: null, RsrActif: false, AfmfActif: true),
        };

        LectureAdlx.Lire(carte, true).Should().Contain("Enhanced Sync : désactivé")
            .And.Contain("Radeon Boost  : activé")
            .And.Contain("Image Sharpening : non lu")
            .And.Contain("RSR           : désactivé")
            .And.Contain("AFMF          : activé");
    }

    // ── Mesures de la carte (LireMetriques) ─────────────────────────────────────────────

    [Fact]
    public void Chaque_mesure_de_la_carte_a_sa_ligne_et_son_unite()
    {
        var carte = new CarteEnLecture { Metriques = new AdlxMetriques(PuissanceW: 182.4, TemperatureC: 61, PointChaudC: 78.4, FrequenceMhz: 2450, VentilateurTrMin: 1320) };

        // Un Windows en français ne change ni le séparateur décimal ni l'unité : la ligne se compare à HWiNFO telle quelle.
        var avant = CultureInfo.CurrentCulture;
        IReadOnlyList<string> l;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); l = LectureAdlx.Lire(carte, true); }
        finally { CultureInfo.CurrentCulture = avant; }

        l.Should().Contain("Consommation  : 182.4 W")
            .And.Contain("Fréquence GPU : 2450 MHz")
            .And.Contain("Point chaud   : 78.4 °C")
            .And.Contain("Ventilateur   : 1320 tr/min");
        l.Should().NotContain(x => x.StartsWith("Mesures ADLX", StringComparison.Ordinal), "tout est lu : il n'y a pas de raison à donner");
    }

    [Fact]
    public void Une_mesure_non_lue_le_dit_et_les_autres_restent()
    {
        var carte = new CarteEnLecture { Metriques = new AdlxMetriques(PuissanceW: 95.5, PointChaudC: null, FrequenceMhz: null, VentilateurTrMin: 870) };

        LectureAdlx.Lire(carte, true).Should().Contain("Consommation  : 95.5 W")
            .And.Contain("Fréquence GPU : non lue")
            .And.Contain("Point chaud   : non lu")
            .And.Contain("Ventilateur   : 870 tr/min");
    }

    // Le Zero RPM arrête les ventilateurs au repos : 0 tr/min est une mesure, pas une absence. Chaque zéro lu reste zéro.
    [Fact]
    public void Un_zero_mesure_reste_zero()
    {
        var carte = new CarteEnLecture { Metriques = new AdlxMetriques(PuissanceW: 0, PointChaudC: 0, FrequenceMhz: 0, VentilateurTrMin: 0) };

        LectureAdlx.Lire(carte, true).Should().Contain("Consommation  : 0.0 W")
            .And.Contain("Fréquence GPU : 0 MHz")
            .And.Contain("Point chaud   : 0.0 °C")
            .And.Contain("Ventilateur   : 0 tr/min");
    }

    [Fact]
    public void Des_mesures_que_rien_ne_lit_donnent_leur_raison_et_jamais_zero()
    {
        var carte = new CarteEnLecture { Metriques = AdlxMetriques.RienLu("GetCurrentGPUMetrics failed.") };

        LectureAdlx.Lire(carte, true).Should().Contain("Mesures ADLX  : rien lu (GetCurrentGPUMetrics failed.)")
            .And.Contain("Consommation  : non lue")
            .And.Contain("Fréquence GPU : non lue")
            .And.Contain("Point chaud   : non lu")
            .And.Contain("Ventilateur   : non lu");
    }

    // ── FreeSync de chaque écran (LireFreeSync) ─────────────────────────────────────────

    // Chaque écran se reconnaît à son numéro, dans l'ordre d'ADLX, même quand deux écrans ont le même nom ; le
    // remplissage d'un nom EDID ne déborde pas sur la ligne.
    [Fact]
    public void Chaque_ecran_a_sa_ligne_FreeSync_dans_l_ordre_d_ADLX()
    {
        var carte = new CarteEnLecture
        {
            Ecrans =
            [
                new("VG27AQ", true, true),
                new("LG ULTRAGEAR", true, false),
                new("DELL U2419H", false, false),
                new("", null, null),
                new("VG27AQ   ", true, null),
            ],
        };

        LectureAdlx.Lire(carte, true).Where(x => x.StartsWith("FreeSync", StringComparison.Ordinal)).Should().Equal(
            "FreeSync (écran 1, VG27AQ) : pris en charge, activé",
            "FreeSync (écran 2, LG ULTRAGEAR) : pris en charge, désactivé",
            "FreeSync (écran 3, DELL U2419H) : non pris en charge, désactivé",
            "FreeSync (écran 4, nom non lu) : prise en charge non lue, non lu",
            "FreeSync (écran 5, VG27AQ) : pris en charge, non lu");
    }

    // Une liste vide veut dire « rien lu » (contrat de LireFreeSync), jamais « aucun écran » ni « désactivé ».
    [Fact]
    public void Sans_ecran_lu_FreeSync_est_non_lu()
        => LectureAdlx.Lire(new CarteEnLecture(), true).Where(x => x.StartsWith("FreeSync", StringComparison.Ordinal))
            .Should().Equal("FreeSync      : non lu");

    // ── Sans pilote AMD, et rien que des lectures ───────────────────────────────────────

    [Fact]
    public void Sans_DLL_chaque_lecture_dit_non_lu()
    {
        var l = LectureAdlx.Lire(null, false);

        l.Should().Contain("amdadlx64.dll dans System32 : absente")
            .And.Contain("Enhanced Sync : non lu")
            .And.Contain("Radeon Boost  : non lu")
            .And.Contain("Image Sharpening : non lu")
            .And.Contain("RSR           : non lu")
            .And.Contain("AFMF          : non lu")
            .And.Contain("Consommation  : non lue")
            .And.Contain("Fréquence GPU : non lue")
            .And.Contain("Point chaud   : non lu")
            .And.Contain("Ventilateur   : non lu")
            .And.Contain("FreeSync      : non lu")
            .And.Contain("Puissance     : non lue");
        l.Should().ContainSingle(x => x.StartsWith("ADLX : rien lu (", StringComparison.Ordinal));
    }

    // « --lire » ne fait que lire : ni Set*, ni remise à l'usine, ni ventilateur, ni lecture hors de son bloc.
    [Fact]
    public void Lire_n_appelle_que_les_entrees_de_lecture_du_bloc()
    {
        var carte = new CarteEnLecture
        {
            Reglages = new AdlxReglagesLus(Sam: true, VsyncMode: 3, BoostActif: true),
            Metriques = new AdlxMetriques(PuissanceW: 150),
            Ecrans = [new("VG27AQ", true, true)],
            Puissances = new Dictionary<int, AdlxPowerReads> { [1] = new() },
        };

        LectureAdlx.Lire(carte, true);

        carte.Ecritures.Should().BeEmpty();
        carte.Appels.Distinct().Should().BeEquivalentTo(nameof(IAdlxNative.LireReglages), nameof(IAdlxNative.LireMetriques),
            nameof(IAdlxNative.LireFreeSync), nameof(IAdlxNative.TryReadPower));
    }

    // Le témoin de la garde ci-dessus : une écriture sur la doublure échoue, et reste notée même si l'appelant avale
    // l'exception (AdlxReglages.Lire en avale).
    [Fact]
    public void Une_ecriture_sur_la_doublure_echoue_et_reste_notee()
    {
        var carte = new CarteEnLecture();

        var ecrire = () => carte.EcrireVsync(3);

        ecrire.Should().Throw<InvalidOperationException>();
        carte.Ecritures.Should().Equal(nameof(IAdlxNative.EcrireVsync));
    }

    // Un membre ajouté à IAdlxNative avec un corps par défaut passerait la doublure sans être noté.
    [Fact]
    public void La_doublure_porte_elle_meme_chaque_membre_d_IAdlxNative()
        => typeof(CarteEnLecture).GetInterfaceMap(typeof(IAdlxNative)).TargetMethods
            .Should().OnlyContain(m => m.DeclaringType == typeof(CarteEnLecture));

    /// <summary>Une carte AMD en doublure qui ne sait que lire : chaque appel est noté ; une écriture est notée, puis lève.
    /// Elle porte elle-même chaque membre d'IAdlxNative, pour qu'aucun corps par défaut ne passe sans être vu.</summary>
    private sealed class CarteEnLecture : IAdlxNative
    {
        public AdlxReglagesLus Reglages { get; init; } = new();
        public AdlxMetriques Metriques { get; init; } = new();
        public IReadOnlyList<AdlxEcranFreeSync> Ecrans { get; init; } = [];
        public IReadOnlyDictionary<int, AdlxPowerReads> Puissances { get; init; } = new Dictionary<int, AdlxPowerReads>();
        public List<string> Appels { get; } = [];
        public List<string> Ecritures { get; } = [];

        public bool IsAvailable => true;

        public AdlxReglagesLus LireReglages() => Note(nameof(LireReglages), Reglages);
        public AdlxMetriques LireMetriques() => Note(nameof(LireMetriques), Metriques);
        public IReadOnlyList<AdlxEcranFreeSync> LireFreeSync() => Note(nameof(LireFreeSync), Ecrans);
        public bool TryReadPower(int gpuIndex, out AdlxPowerReads reads) => Note(nameof(TryReadPower), Puissances.TryGetValue(gpuIndex, out reads));

        // Des lectures, mais pas celles de « --lire » : notées, pour que la garde les voie.
        public bool TryReadGfx(int gpuIndex, out AdlxGfxReads reads) { reads = default; return Note(nameof(TryReadGfx), false); }
        public bool TryReadFan(int gpuIndex, out AdlxFanReads reads) { reads = default; return Note(nameof(TryReadFan), false); }
        public IReadOnlyList<GpuProbeStep> ProbeGfxPath(int gpuIndex) => Note<IReadOnlyList<GpuProbeStep>>(nameof(ProbeGfxPath), []);
        public bool? EstALUsine(int gpuIndex) => Note<bool?>(nameof(EstALUsine), null);
        public double? LireTemperatureCarte() => Note<double?>(nameof(LireTemperatureCarte), null);

        // Les écritures : notées, puis refusées net.
        public bool TrySetGfx(int gpuIndex, int newMaxFreqMhz, int newVoltMv, out AdlxGfxReads original) => throw Ecriture(nameof(TrySetGfx));
        public bool TrySetPowerLimit(int gpuIndex, int newPowerLimitPct, out AdlxPowerReads original) => throw Ecriture(nameof(TrySetPowerLimit));
        public string? EcrireAntiLag(bool actif) => throw Ecriture(nameof(EcrireAntiLag));
        public string? RemettreReglageUsine(int gpuIndex) => throw Ecriture(nameof(RemettreReglageUsine));
        public string? EcrireFrtc(int code) => throw Ecriture(nameof(EcrireFrtc));
        public string? EcrireVsync(int mode) => throw Ecriture(nameof(EcrireVsync));
        public string? PoserVentilateurFixe(int pourcent) => throw Ecriture(nameof(PoserVentilateurFixe));
        public string? RendreVentilateurAuto() => throw Ecriture(nameof(RendreVentilateurAuto));

        private T Note<T>(string nom, T valeur)
        {
            Appels.Add(nom);
            return valeur;
        }

        private InvalidOperationException Ecriture(string nom)
        {
            Appels.Add(nom);
            Ecritures.Add(nom);
            return new InvalidOperationException($"« --lire » a appelé {nom} : une écriture, dans un rapport en lecture seule.");
        }
    }
}
