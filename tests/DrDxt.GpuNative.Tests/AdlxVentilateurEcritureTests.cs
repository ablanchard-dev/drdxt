using System.Runtime.InteropServices;
using DrDxt.Detection.Gpu.Adlx;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests;

// Les écritures du ventilateur d'AdlxNative sur une DOUBLURE de la table ADLX (même principe qu'AdlxLectureTests) : de vrais
// objets en mémoire non managée, aux entrées relevées dans IGPUManualFanTuning.h. Le chemin natif réel tourne ; seule la DLL
// est remplacée. Aucune carte AMD sur ce PC : la preuve réelle attend un rapport de validation.
public sealed unsafe class AdlxVentilateurEcritureTests
{
    private const int Ok = 0, Refus = 3;

    private sealed class Etat { public int Vitesse, Temperature; }

    private sealed class Carte : IDisposable
    {
        public Etat[] Courbe = [new() { Vitesse = 0, Temperature = 40 }, new() { Vitesse = 30, Temperature = 55 }, new() { Vitesse = 50, Temperature = 65 },
            new() { Vitesse = 75, Temperature = 75 }, new() { Vitesse = 100, Temperature = 90 }];
        public Etat[] Defaut = [new() { Vitesse = 0, Temperature = 40 }, new() { Vitesse = 25, Temperature = 55 }, new() { Vitesse = 45, Temperature = 65 },
            new() { Vitesse = 70, Temperature = 75 }, new() { Vitesse = 100, Temperature = 90 }];
        public bool ZeroRpm = true, ZeroRpmDefaut = true, ZeroRpmSupporte = true, Fan1 = true, IgnoreLaPose;
        public (int Min, int Max) Plage = (20, 100);
        public int Poses;
        public double Temperature = 61.0;

        private readonly List<IntPtr> _blocs = [];
        public readonly Dictionary<IntPtr, object> Objets = [];
        public IntPtr Systeme;

        public Carte()
        {
            var gpu = Objet(new Gpu(2), (5, (IntPtr)(delegate* unmanaged<IntPtr, int*, int>)&Entier5));
            var liste = Objet(new ListeDePtr([gpu]), (3, (IntPtr)(delegate* unmanaged<IntPtr, uint>)&Taille3),
                (11, (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&A11));
            var fan1 = Objet(new Fan1(this), (23, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&Sortie23),
                (27, (IntPtr)(delegate* unmanaged<IntPtr, sbyte*, int>)&Bool27));
            var fan = Objet(new Fan(this, fan1), (2, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&Qi2),
                (3, (IntPtr)(delegate* unmanaged<IntPtr, int*, int*, int>)&Plages3),
                (4, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&Sortie4), (5, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&Sortie5),
                (6, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int*, int>)&Valider6), (7, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&Poser7),
                (8, (IntPtr)(delegate* unmanaged<IntPtr, sbyte*, int>)&Bool8), (9, (IntPtr)(delegate* unmanaged<IntPtr, sbyte*, int>)&Bool9),
                (10, (IntPtr)(delegate* unmanaged<IntPtr, byte, int>)&SetBool10));
            var ts = Objet(new Ts(fan), (10, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, sbyte*, int>)&ParCarteBool10),
                (16, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&ParCarte16));
            var metriques = Objet(new Metriques(this), (7, (IntPtr)(delegate* unmanaged<IntPtr, double*, int>)&Reel7));
            var perf = Objet(new Perf(metriques), (18, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&ParCarte18));
            Systeme = Objet(new Systeme(liste, ts, perf), systeme: true, (1, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&Sortie1),
                (8, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&Sortie8), (9, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&Sortie9));
            _courante = this;
        }

        public IntPtr Liste(IEnumerable<Etat> etats) =>
            Objet(new ListeDePtr(etats.Select(e => Objet(e, (3, (IntPtr)(delegate* unmanaged<IntPtr, int*, int>)&Entier3),
                (4, (IntPtr)(delegate* unmanaged<IntPtr, int, int>)&SetEntier4), (5, (IntPtr)(delegate* unmanaged<IntPtr, int*, int>)&Entier5),
                (6, (IntPtr)(delegate* unmanaged<IntPtr, int, int>)&SetEntier6))).ToArray()),
                (3, (IntPtr)(delegate* unmanaged<IntPtr, uint>)&Taille3), (11, (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&A11));

        private IntPtr Objet(object modele, params (int Slot, IntPtr Fn)[] entrees) => Objet(modele, false, entrees);

        private IntPtr Objet(object modele, bool systeme, params (int Slot, IntPtr Fn)[] entrees)
        {
            var table = Marshal.AllocHGlobal(32 * IntPtr.Size);
            var objet = Marshal.AllocHGlobal(IntPtr.Size);
            _blocs.Add(table);
            _blocs.Add(objet);
            for (var i = 0; i < 32; i++) Marshal.WriteIntPtr(table, i * IntPtr.Size, IntPtr.Zero);
            if (!systeme) Marshal.WriteIntPtr(table, 1 * IntPtr.Size, (IntPtr)(delegate* unmanaged<IntPtr, long>)&Rel);
            foreach (var (slot, fn) in entrees) Marshal.WriteIntPtr(table, slot * IntPtr.Size, fn);
            Marshal.WriteIntPtr(objet, table);
            Objets[objet] = modele;
            return objet;
        }

        public void Dispose()
        {
            _courante = null;
            foreach (var b in _blocs) Marshal.FreeHGlobal(b);
        }
    }

    private sealed record Gpu(int Type);
    private sealed record ListeDePtr(IntPtr[] Elements);
    private sealed record Systeme(IntPtr Gpus, IntPtr Ts, IntPtr Perf);
    private sealed record Ts(IntPtr Fan);
    private sealed record Fan(Carte C, IntPtr Fan1);
    private sealed record Fan1(Carte C);
    private sealed record Perf(IntPtr Metriques);
    private sealed record Metriques(Carte C);

    [ThreadStatic] private static Carte? _courante;
    private static T O<T>(IntPtr p) => (T)_courante!.Objets[p];
    private static int Sortir(IntPtr valeur, IntPtr* o) { *o = valeur; return valeur == IntPtr.Zero ? Refus : Ok; }

    [UnmanagedCallersOnly] private static long Rel(IntPtr p) => 0;
    [UnmanagedCallersOnly] private static uint Taille3(IntPtr p) => (uint)O<ListeDePtr>(p).Elements.Length;
    [UnmanagedCallersOnly] private static int A11(IntPtr p, uint i, IntPtr* o) { var l = O<ListeDePtr>(p).Elements; return Sortir(i < l.Length ? l[i] : IntPtr.Zero, o); }
    [UnmanagedCallersOnly] private static int Sortie1(IntPtr p, IntPtr* o) => Sortir(O<Systeme>(p).Gpus, o);
    [UnmanagedCallersOnly] private static int Sortie8(IntPtr p, IntPtr* o) => Sortir(O<Systeme>(p).Ts, o);
    [UnmanagedCallersOnly] private static int Sortie9(IntPtr p, IntPtr* o) => Sortir(O<Systeme>(p).Perf, o);
    [UnmanagedCallersOnly] private static int ParCarteBool10(IntPtr p, IntPtr g, sbyte* v) { *v = 1; return Ok; }
    [UnmanagedCallersOnly] private static int ParCarte16(IntPtr p, IntPtr g, IntPtr* o) => Sortir(O<Ts>(p).Fan, o);
    [UnmanagedCallersOnly] private static int ParCarte18(IntPtr p, IntPtr g, IntPtr* o) => Sortir(O<Perf>(p).Metriques, o);
    [UnmanagedCallersOnly] private static int Reel7(IntPtr p, double* v) { *v = O<Metriques>(p).C.Temperature; return Ok; }

    [UnmanagedCallersOnly] private static int Entier5(IntPtr p, int* v)
    {
        *v = _courante!.Objets[p] switch { Gpu g => g.Type, Etat e => e.Temperature, _ => 0 };
        return Ok;
    }
    [UnmanagedCallersOnly] private static int Entier3(IntPtr p, int* v) { *v = O<Etat>(p).Vitesse; return Ok; }
    [UnmanagedCallersOnly] private static int SetEntier4(IntPtr p, int v) { O<Etat>(p).Vitesse = v; return Ok; }
    [UnmanagedCallersOnly] private static int SetEntier6(IntPtr p, int v) { O<Etat>(p).Temperature = v; return Ok; }

    [UnmanagedCallersOnly] private static int Qi2(IntPtr p, IntPtr id, IntPtr* o)
    {
        var f = O<Fan>(p);
        return Sortir(f.C.Fan1 && Marshal.PtrToStringUni(id) == "IADLXManualFanTuning1" ? f.Fan1 : IntPtr.Zero, o);
    }
    [UnmanagedCallersOnly] private static int Plages3(IntPtr p, int* vitesse, int* temperature)
    {
        var c = O<Fan>(p).C;
        (vitesse[0], vitesse[1], vitesse[2]) = (c.Plage.Min, c.Plage.Max, 1);
        (temperature[0], temperature[1], temperature[2]) = (25, 100, 1);
        return Ok;
    }
    [UnmanagedCallersOnly] private static int Sortie4(IntPtr p, IntPtr* o) { var c = O<Fan>(p).C; return Sortir(c.Liste(c.Courbe.Select(e => new Etat { Vitesse = e.Vitesse, Temperature = e.Temperature })), o); }
    [UnmanagedCallersOnly] private static int Sortie5(IntPtr p, IntPtr* o) { var c = O<Fan>(p).C; return Sortir(c.Liste(c.Courbe.Select(_ => new Etat())), o); }
    [UnmanagedCallersOnly] private static int Sortie23(IntPtr p, IntPtr* o) { var c = O<Fan1>(p).C; return Sortir(c.Liste(c.Defaut.Select(e => new Etat { Vitesse = e.Vitesse, Temperature = e.Temperature })), o); }
    [UnmanagedCallersOnly] private static int Valider6(IntPtr p, IntPtr etats, int* erreur) { *erreur = -1; return Ok; }
    [UnmanagedCallersOnly] private static int Poser7(IntPtr p, IntPtr etats)
    {
        var c = O<Fan>(p).C;
        c.Poses++;
        if (c.IgnoreLaPose) return Ok;
        c.Courbe = O<ListeDePtr>(etats).Elements.Select(e => O<Etat>(e)).Select(e => new Etat { Vitesse = e.Vitesse, Temperature = e.Temperature }).ToArray();
        return Ok;
    }
    [UnmanagedCallersOnly] private static int Bool8(IntPtr p, sbyte* v) { *v = (sbyte)(O<Fan>(p).C.ZeroRpmSupporte ? 1 : 0); return Ok; }
    [UnmanagedCallersOnly] private static int Bool9(IntPtr p, sbyte* v) { *v = (sbyte)(O<Fan>(p).C.ZeroRpm ? 1 : 0); return Ok; }
    [UnmanagedCallersOnly] private static int Bool27(IntPtr p, sbyte* v) { *v = (sbyte)(O<Fan1>(p).C.ZeroRpmDefaut ? 1 : 0); return Ok; }
    [UnmanagedCallersOnly] private static int SetBool10(IntPtr p, byte v) { O<Fan>(p).C.ZeroRpm = v != 0; return Ok; }

    [Fact]
    public void Une_vitesse_fixe_se_pose_sur_chaque_etat_garde_les_temperatures_et_coupe_le_Zero_RPM()
    {
        using var c = new Carte();

        new AdlxNative(c.Systeme).PoserVentilateurFixe(60).Should().BeNull();

        c.Courbe.Select(e => e.Vitesse).Should().AllBeEquivalentTo(60);
        c.Courbe.Select(e => e.Temperature).Should().Equal(40, 55, 65, 75, 90);
        c.ZeroRpm.Should().BeFalse("à l'arrêt au repos, la vitesse fixe ne vaudrait rien");
    }

    [Fact]
    public void Hors_de_la_plage_de_la_carte_rien_ne_s_ecrit()
    {
        using var c = new Carte { Plage = (30, 100) };

        new AdlxNative(c.Systeme).PoserVentilateurFixe(25).Should().Contain("outside");
        c.Poses.Should().Be(0);
    }

    [Fact]
    public void Une_pose_que_la_carte_ignore_se_dit()
    {
        using var c = new Carte { IgnoreLaPose = true };

        new AdlxNative(c.Systeme).PoserVentilateurFixe(60).Should().Contain("read back");
    }

    [Fact]
    public void Le_retour_remet_la_courbe_et_le_Zero_RPM_par_defaut_et_les_relit()
    {
        using var c = new Carte { ZeroRpm = false };
        c.Courbe = c.Courbe.Select(e => new Etat { Vitesse = 60, Temperature = e.Temperature }).ToArray();

        new AdlxNative(c.Systeme).RendreVentilateurAuto().Should().BeNull();

        c.Courbe.Select(e => e.Vitesse).Should().Equal(0, 25, 45, 70, 100);
        c.ZeroRpm.Should().BeTrue();
    }

    [Fact]
    public void Sans_la_v1_le_retour_ne_devine_pas_la_courbe_de_la_carte()
    {
        using var c = new Carte { Fan1 = false };

        new AdlxNative(c.Systeme).RendreVentilateurAuto().Should().Contain("IADLXManualFanTuning1");
        c.Poses.Should().Be(0);
    }

    [Fact]
    public void La_temperature_de_la_carte_dediee_se_lit()
    {
        using var c = new Carte();
        new AdlxNative(c.Systeme).LireTemperatureCarte().Should().Be(61.0);
    }
}
