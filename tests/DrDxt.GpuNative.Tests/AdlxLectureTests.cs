using System.Runtime.InteropServices;
using DrDxt.Detection.Gpu.Adlx;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests;

// AdlxNative.LireReglages marche sur des DOUBLURES de la table ADLX : de vrais objets en mémoire
// non managée (premier mot = table de pointeurs de fonctions natives), aux entrées relevées dans les en-têtes du SDK.
// Le chemin natif réel (Slot, Release, délégués de AdlxNative) est celui qui tourne ; seule la DLL est remplacée.
// Les entrées sont des fonctions [UnmanagedCallersOnly] : un pointeur tiré d'un délégué managé serait reconnu par le
// runtime et refusé au retour (autre type de délégué). Pas de carte AMD sur ce PC (RTX 4060) : la preuve réelle est la
// commande du harnais, dans research/north-star/adlx-lecture.md.
public sealed unsafe class AdlxLectureTests
{
    private const int Ok = 0;
    private const int NotSupported = 3; // tout code non nul est un échec ADLX

    // ── Les réponses de la doublure, par (objet, entrée) ─────────────────────────────────────
    private delegate int HSortie(out IntPtr o);
    private delegate int HA(uint i, out IntPtr o);
    private delegate uint HTaille();
    private delegate int HEntier(out int v);
    private delegate int HBool(out sbyte v);
    private delegate int HReel(out double v);
    private delegate int HParCarte(IntPtr gpu, out IntPtr o);
    private delegate int HQi(string? id, out IntPtr o);

    [ThreadStatic] private static Doublure? _courante; // LireReglages appelle sur le fil du test, de façon synchrone
    private static T H<T>(IntPtr p, int slot) where T : Delegate => (T)_courante!.Reponses[(p, slot)];

    [UnmanagedCallersOnly] private static long Rel(IntPtr p) { _courante!.Rendus++; return 0; }
    [UnmanagedCallersOnly] private static uint Taille3(IntPtr p) => H<HTaille>(p, 3)();
    [UnmanagedCallersOnly] private static int A11(IntPtr p, uint i, IntPtr* o) { var r = H<HA>(p, 11)(i, out var x); *o = x; return r; }
    [UnmanagedCallersOnly] private static int Qi2(IntPtr p, IntPtr id, IntPtr* o) { var r = H<HQi>(p, 2)(Marshal.PtrToStringUni(id), out var x); *o = x; return r; }
    [UnmanagedCallersOnly] private static int Sortie1(IntPtr p, IntPtr* o) => Sortie(p, 1, o);
    [UnmanagedCallersOnly] private static int Sortie3(IntPtr p, IntPtr* o) => Sortie(p, 3, o);
    [UnmanagedCallersOnly] private static int Sortie4(IntPtr p, IntPtr* o) => Sortie(p, 4, o);
    [UnmanagedCallersOnly] private static int Sortie6(IntPtr p, IntPtr* o) => Sortie(p, 6, o);
    [UnmanagedCallersOnly] private static int Sortie7(IntPtr p, IntPtr* o) => Sortie(p, 7, o);
    [UnmanagedCallersOnly] private static int Sortie8(IntPtr p, IntPtr* o) => Sortie(p, 8, o);
    [UnmanagedCallersOnly] private static int Sortie9(IntPtr p, IntPtr* o) => Sortie(p, 9, o);
    [UnmanagedCallersOnly] private static int ParCarte3(IntPtr p, IntPtr g, IntPtr* o) => ParCarte(p, 3, g, o);
    [UnmanagedCallersOnly] private static int ParCarte4(IntPtr p, IntPtr g, IntPtr* o) => ParCarte(p, 4, g, o);
    [UnmanagedCallersOnly] private static int ParCarte9(IntPtr p, IntPtr g, IntPtr* o) => ParCarte(p, 9, g, o);
    [UnmanagedCallersOnly] private static int ParCarte18(IntPtr p, IntPtr g, IntPtr* o) => ParCarte(p, 18, g, o);
    [UnmanagedCallersOnly] private static int Bool3(IntPtr p, sbyte* v) => Bool(p, 3, v);
    [UnmanagedCallersOnly] private static int Bool4(IntPtr p, sbyte* v) => Bool(p, 4, v);
    [UnmanagedCallersOnly] private static int Entier5(IntPtr p, int* v) => Entier(p, 5, v);
    [UnmanagedCallersOnly] private static int Entier6(IntPtr p, int* v) => Entier(p, 6, v);
    [UnmanagedCallersOnly] private static int Entier7(IntPtr p, int* v) => Entier(p, 7, v);
    [UnmanagedCallersOnly] private static int Reel7(IntPtr p, double* v) { var r = H<HReel>(p, 7)(out var x); *v = x; return r; }

    private static int Sortie(IntPtr p, int slot, IntPtr* o) { var r = H<HSortie>(p, slot)(out var x); *o = x; return r; }
    private static int ParCarte(IntPtr p, int slot, IntPtr g, IntPtr* o) { var r = H<HParCarte>(p, slot)(g, out var x); *o = x; return r; }
    private static int Bool(IntPtr p, int slot, sbyte* v) { var r = H<HBool>(p, slot)(out var x); *v = x; return r; }
    private static int Entier(IntPtr p, int slot, int* v) { var r = H<HEntier>(p, slot)(out var x); *v = x; return r; }

    private static readonly Dictionary<(Type, int), IntPtr> Entrees = new()
    {
        [(typeof(HTaille), 3)] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&Taille3,
        [(typeof(HA), 11)] = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&A11,
        [(typeof(HQi), 2)] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&Qi2,
        [(typeof(HSortie), 1)] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&Sortie1,
        [(typeof(HSortie), 3)] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&Sortie3,
        [(typeof(HSortie), 4)] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&Sortie4,
        [(typeof(HSortie), 6)] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&Sortie6,
        [(typeof(HSortie), 7)] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&Sortie7,
        [(typeof(HSortie), 8)] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&Sortie8,
        [(typeof(HSortie), 9)] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&Sortie9,
        [(typeof(HParCarte), 3)] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&ParCarte3,
        [(typeof(HParCarte), 4)] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&ParCarte4,
        [(typeof(HParCarte), 9)] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&ParCarte9,
        [(typeof(HParCarte), 18)] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&ParCarte18,
        [(typeof(HBool), 3)] = (IntPtr)(delegate* unmanaged<IntPtr, sbyte*, int>)&Bool3,
        [(typeof(HBool), 4)] = (IntPtr)(delegate* unmanaged<IntPtr, sbyte*, int>)&Bool4,
        [(typeof(HEntier), 5)] = (IntPtr)(delegate* unmanaged<IntPtr, int*, int>)&Entier5,
        [(typeof(HEntier), 6)] = (IntPtr)(delegate* unmanaged<IntPtr, int*, int>)&Entier6,
        [(typeof(HEntier), 7)] = (IntPtr)(delegate* unmanaged<IntPtr, int*, int>)&Entier7,
        [(typeof(HReel), 7)] = (IntPtr)(delegate* unmanaged<IntPtr, double*, int>)&Reel7,
    };

    /// <summary>Ce que la carte doublure répond. Une entrée nommée dans <see cref="Erreurs"/> rend une erreur ADLX.</summary>
    private sealed class Scenario
    {
        public int[] Types = [1, 2]; // puce intégrée d'abord, carte dédiée ensuite (7800X3D + RX 9070 XT)
        public (bool Sup, bool On) Sam = (true, true);
        public (bool Sup, bool On) AntiLag = (true, true);
        public (bool Sup, bool On, int Fps) Frtc = (true, true, 141);
        public (bool Sup, bool On, int Min, int Max) Chill = (true, false, 0, 0);
        public double Temperature = 58.0;
        // A-FS1 : les écrans branchés sur la carte, tels qu'ADLX les nomme.
        public (string Nom, bool Sup, bool On)[] Ecrans = [("VG27AQ", true, true), ("MAG274QRF", true, false)];
        public HashSet<string> Erreurs = [];
    }

    /// <summary>Le système doublure : objets ADLX en mémoire non managée, libérés à la fin.</summary>
    private sealed class Doublure : IDisposable
    {
        public readonly Dictionary<(IntPtr, int), Delegate> Reponses = [];
        private readonly List<IntPtr> _blocs = [];
        private readonly Scenario _s;
        public int Remis, Rendus;
        public IntPtr CarteDemandee;
        public IntPtr Systeme { get; }
        public IntPtr[] Gpus { get; }

        public Doublure(Scenario s)
        {
            _s = s;
            Gpus = s.Types.Select(t => Objet(new() { [5] = new HEntier((out int v) => { v = t; return Err("type"); }) })).ToArray();
            var liste = Objet(new()
            {
                [3] = new HTaille(() => (uint)Gpus.Length),
                [11] = new HA((uint i, out IntPtr o) => Remettre(i < Gpus.Length ? Gpus[i] : IntPtr.Zero, out o)),
            });
            var sam = Fonction("sam", s.Sam.Sup, s.Sam.On, []);
            var ts1 = Objet(new()
            {
                [18] = new HParCarte((IntPtr g, out IntPtr o) =>
                {
                    CarteDemandee = g;
                    return g == Gpus[^1] ? Remettre(sam, out o, "get-sam") : Echec(out o);
                }),
            });
            var ts = Objet(new()
            {
                [2] = new HQi((string? id, out IntPtr o) => id == "IADLXGPUTuningServices1" ? Remettre(ts1, out o, "qi") : Echec(out o)),
            });
            var antiLag = Fonction("antilag", s.AntiLag.Sup, s.AntiLag.On, []);
            var frtc = Fonction("frtc", s.Frtc.Sup, s.Frtc.On, new() { [6] = Int("frtc-fps", s.Frtc.Fps) });
            var chill = Fonction("chill", s.Chill.Sup, s.Chill.On, new() { [6] = Int("chill-min", s.Chill.Min), [7] = Int("chill-max", s.Chill.Max) });
            var trois = Objet(new()
            {
                [3] = ParCarteVers(antiLag, "get-antilag"),
                [4] = ParCarteVers(chill, "get-chill"),
                [9] = ParCarteVers(frtc, "get-frtc"),
            });
            var metriques = Objet(new() { [7] = new HReel((out double v) => { v = s.Temperature; return Err("temp"); }) });
            var perf = Objet(new() { [18] = ParCarteVers(metriques, "get-metrics") });

            var ecrans = s.Ecrans.Select(e =>
            {
                var nom = Marshal.StringToHGlobalAnsi(e.Nom);
                _blocs.Add(nom);
                return (Ecran: Objet(new() { [6] = new HSortie((out IntPtr o) => { o = nom; return Err("nom"); }) }),
                        FreeSync: Fonction("freesync", e.Sup, e.On, []));
            }).ToArray();
            var listeEcrans = Objet(new()
            {
                [3] = new HTaille(() => (uint)ecrans.Length),
                [11] = new HA((uint i, out IntPtr o) => Remettre(i < ecrans.Length ? ecrans[i].Ecran : IntPtr.Zero, out o)),
            });
            var affichage = Objet(new()
            {
                [4] = SortieVers(listeEcrans, "displays"),
                [9] = new HParCarte((IntPtr ecran, out IntPtr o) =>
                    Remettre(ecrans.FirstOrDefault(e => e.Ecran == ecran).FreeSync, out o, "get-freesync")),
            });

            Systeme = Objet(new()
            {
                [1] = SortieVers(liste, "gpus"),
                [3] = SortieVers(affichage, "display-services"),
                [7] = SortieVers(trois, "3d"),
                [8] = SortieVers(ts, "tuning"),
                [9] = SortieVers(perf, "perf"),
            }, systeme: true);
            _courante = this;
        }

        private int Err(string nom) => _s.Erreurs.Contains(nom) ? NotSupported : Ok;
        private static int Echec(out IntPtr o) { o = IntPtr.Zero; return NotSupported; }

        private int Remettre(IntPtr p, out IntPtr o, string? nom = null)
        {
            if (p == IntPtr.Zero || (nom is not null && _s.Erreurs.Contains(nom))) return Echec(out o);
            o = p;
            Remis++;
            return Ok;
        }

        private HSortie SortieVers(IntPtr p, string nom) => (out IntPtr o) => Remettre(p, out o, nom);
        private HParCarte ParCarteVers(IntPtr p, string nom) => (IntPtr _, out IntPtr o) => Remettre(p, out o, nom);
        private HEntier Int(string nom, int valeur) => (out int v) => { v = valeur; return Err(nom); };

        private IntPtr Fonction(string nom, bool sup, bool on, Dictionary<int, Delegate> autres)
        {
            autres[3] = new HBool((out sbyte v) => { v = (sbyte)(sup ? 1 : 0); return Err(nom + "-supported"); });
            autres[4] = new HBool((out sbyte v) => { v = (sbyte)(on ? 1 : 0); return Err(nom + "-enabled"); });
            return Objet(autres);
        }

        // Un objet ADLX : un mot qui pointe vers une table de 24 entrées ; entrée absente = pointeur nul. Hors
        // IADLXSystem, l'entrée 1 est Release.
        private IntPtr Objet(Dictionary<int, Delegate> reponses, bool systeme = false)
        {
            var table = Marshal.AllocHGlobal(24 * IntPtr.Size);
            var objet = Marshal.AllocHGlobal(IntPtr.Size);
            _blocs.Add(table);
            _blocs.Add(objet);
            for (int i = 0; i < 24; i++) Marshal.WriteIntPtr(table, i * IntPtr.Size, IntPtr.Zero);
            if (!systeme) Marshal.WriteIntPtr(table, 1 * IntPtr.Size, (IntPtr)(delegate* unmanaged<IntPtr, long>)&Rel);
            foreach (var (slot, rep) in reponses)
            {
                Marshal.WriteIntPtr(table, slot * IntPtr.Size, Entrees[(rep.GetType(), slot)]);
                Reponses[(objet, slot)] = rep;
            }
            Marshal.WriteIntPtr(objet, table);
            return objet;
        }

        public void Dispose()
        {
            _courante = null;
            foreach (var b in _blocs) Marshal.FreeHGlobal(b);
        }
    }

    private static (AdlxReglagesLus Lus, Doublure D) Lire(Scenario s)
    {
        var d = new Doublure(s);
        return (new AdlxNative(d.Systeme).LireReglages(), d);
    }

    [Fact]
    public void Lit_SAM_Anti_Lag_FRTC_Chill_et_temperature_de_la_carte_dediee()
    {
        var (lus, d) = Lire(new Scenario());
        using (d)
        {
            lus.Raison.Should().BeNull();
            lus.Sam.Should().BeTrue();
            lus.AntiLag.Should().BeTrue();
            lus.FrtcActif.Should().BeTrue();
            lus.FrtcFps.Should().Be(141);
            lus.ChillActif.Should().BeFalse();
            lus.ChillMaxFps.Should().BeNull("Chill éteint : ses bornes ne limitent rien");
            lus.TemperatureC.Should().Be(58.0);
            lus.LimiteImages.Should().Be(141);
        }
    }

    [Fact]
    public void La_puce_integree_en_index_0_n_est_pas_lue_a_la_place_de_la_carte_dediee()
    {
        var (lus, d) = Lire(new Scenario { Types = [1, 2] });
        using (d)
        {
            d.CarteDemandee.Should().Be(d.Gpus[1]);
            lus.Sam.Should().BeTrue();
        }
    }

    [Fact]
    public void Plusieurs_cartes_sans_dediee_disent_non_lu_plutot_que_lire_la_mauvaise()
    {
        var (lus, d) = Lire(new Scenario { Types = [1, 1] });
        using (d)
        {
            lus.Raison.Should().Contain("none as discrete");
            lus.Sam.Should().BeNull();
            lus.TemperatureC.Should().BeNull();
        }
    }

    [Fact]
    public void Une_seule_carte_est_lue_meme_sans_type()
    {
        var (lus, d) = Lire(new Scenario { Types = [0] });
        using (d) lus.Sam.Should().BeTrue();
    }

    [Theory]
    [InlineData("sam-enabled")]
    [InlineData("qi")]
    [InlineData("get-sam")]
    [InlineData("tuning")]
    public void Une_entree_SAM_en_erreur_laisse_SAM_non_lu_et_le_reste_lu(string entree)
    {
        var (lus, d) = Lire(new Scenario { Erreurs = [entree] });
        using (d)
        {
            lus.Sam.Should().BeNull();
            lus.AntiLag.Should().BeTrue();
            lus.TemperatureC.Should().Be(58.0);
        }
    }

    [Theory]
    [InlineData("antilag-supported")]
    [InlineData("antilag-enabled")]
    [InlineData("get-antilag")]
    [InlineData("3d")]
    public void Anti_Lag_en_erreur_est_non_lu(string entree)
    {
        var (lus, d) = Lire(new Scenario { Erreurs = [entree] });
        using (d) lus.AntiLag.Should().BeNull();
    }

    [Fact]
    public void FRTC_allume_sans_valeur_lue_ne_donne_pas_de_limite()
    {
        var (lus, d) = Lire(new Scenario { Erreurs = ["frtc-fps"] });
        using (d)
        {
            lus.FrtcActif.Should().BeTrue();
            lus.FrtcFps.Should().BeNull();
            lus.LimiteImages.Should().BeNull("une limite allumée de valeur inconnue n'est pas « aucune »");
        }
    }

    [Theory]
    [InlineData("temp")]
    [InlineData("get-metrics")]
    [InlineData("perf")]
    public void Temperature_en_erreur_est_non_lue(string entree)
    {
        var (lus, d) = Lire(new Scenario { Erreurs = [entree] });
        using (d) lus.TemperatureC.Should().BeNull();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(255.0)]
    public void Une_temperature_impossible_n_est_pas_une_temperature(double valeur)
    {
        var (lus, d) = Lire(new Scenario { Temperature = valeur });
        using (d) lus.TemperatureC.Should().BeNull();
    }

    [Fact]
    public void Liste_de_cartes_en_erreur_rien_n_est_lu_et_la_raison_est_dite()
    {
        var (lus, d) = Lire(new Scenario { Erreurs = ["gpus"] });
        using (d)
        {
            lus.Raison.Should().Contain("GetGPUs");
            lus.AntiLag.Should().BeNull();
        }
    }

    [Fact]
    public void SAM_non_pris_en_charge_est_non_lu_mais_Anti_Lag_non_pris_en_charge_est_eteint()
    {
        var (lus, d) = Lire(new Scenario { Sam = (false, false), AntiLag = (false, false) });
        using (d)
        {
            lus.Sam.Should().BeNull("la plateforme qui ne peut pas n'est pas « éteint, à allumer »");
            lus.AntiLag.Should().BeFalse();
        }
    }

    [Fact]
    public void Chill_allume_plafonne_a_son_maximum()
    {
        var (lus, d) = Lire(new Scenario { Frtc = (true, false, 0), Chill = (true, true, 70, 160) });
        using (d)
        {
            lus.ChillMinFps.Should().Be(70);
            lus.LimiteImages.Should().Be(160);
        }
    }

    [Fact]
    public void Chaque_interface_remise_est_rendue()
    {
        var (_, d) = Lire(new Scenario());
        using (d)
        {
            d.Remis.Should().BeGreaterThan(10);
            d.Rendus.Should().Be(d.Remis);
        }
    }

    // ── A-FS1 : FreeSync lu au pilote, écran par écran ───────────────────────────
    [Fact]
    public void Lit_FreeSync_de_chaque_ecran_avec_son_nom_et_rend_tout_ce_qu_il_prend()
    {
        using var d = new Doublure(new Scenario());
        var ecrans = new AdlxNative(d.Systeme).LireFreeSync();

        ecrans.Should().Equal(new AdlxEcranFreeSync("VG27AQ", true, true), new AdlxEcranFreeSync("MAG274QRF", true, false));
        d.Remis.Should().BeGreaterThan(4);
        d.Rendus.Should().Be(d.Remis, "chaque objet ADLX obtenu est rendu");
    }

    [Theory]
    [InlineData("freesync-enabled", true, null)]
    [InlineData("freesync-supported", null, null)]
    [InlineData("get-freesync", null, null)]
    public void Une_entree_FreeSync_en_erreur_laisse_l_etat_non_lu(string entree, bool? sup, bool? actif)
    {
        using var d = new Doublure(new Scenario { Ecrans = [("VG27AQ", true, true)], Erreurs = [entree] });
        new AdlxNative(d.Systeme).LireFreeSync().Should().Equal(new AdlxEcranFreeSync("VG27AQ", sup, actif));
    }

    [Theory]
    [InlineData("display-services")]
    [InlineData("displays")]
    public void Sans_service_d_affichage_aucun_ecran_n_est_invente(string entree)
    {
        using var d = new Doublure(new Scenario { Erreurs = [entree] });
        new AdlxNative(d.Systeme).LireFreeSync().Should().BeEmpty();
        d.Rendus.Should().Be(d.Remis);
    }

    [Fact]
    public void Sans_systeme_ADLX_rien_n_est_lu_et_rien_ne_leve()
    {
        var lus = new AdlxNative(IntPtr.Zero).LireReglages();
        lus.Raison.Should().NotBeNullOrEmpty();
        lus.Sam.Should().BeNull();
        lus.LimiteImages.Should().BeNull();
    }

    [Fact]
    public void Limite_images_ne_conclut_aucune_que_si_les_deux_sont_lues_eteintes()
    {
        new AdlxReglagesLus(FrtcActif: false, ChillActif: false).LimiteImages.Should().Be(0);
        new AdlxReglagesLus(FrtcActif: false).LimiteImages.Should().BeNull();
        new AdlxReglagesLus(FrtcActif: true, FrtcFps: 141).LimiteImages.Should().BeNull("Chill non lu");
        new AdlxReglagesLus(FrtcActif: true, FrtcFps: 141, ChillActif: true, ChillMaxFps: 120).LimiteImages.Should().Be(120);
    }
}
