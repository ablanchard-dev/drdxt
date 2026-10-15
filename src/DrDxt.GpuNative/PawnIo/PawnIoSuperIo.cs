namespace DrDxt.GpuNative.PawnIo;

/// <summary>
/// Les ventilateurs de la carte mère, lus par sa puce Super I/O avec le module PawnIO LpcIO (namazso/PawnIO.Modules,
/// publication 0.2.11, LGPL-2.1+, figé par empreinte). Prouvé sur la machine de référence (MSI B760, Nuvoton NCT6687D,
/// identifiant 0xD592 sur le port 0x4E, base EC 0xA20) : ventilateur CPU 1265 tr/min à 140/255.
/// Protocole : LibreHardwareMonitor (Nct677X.cs, LpcPort.cs) : entrée en configuration (0x87, 0x87), périphérique logique 0x0B,
/// base en 0x60, sortie (0xAA) ; mémoire EC par page (base+4), index (base+5), donnée (base+6), la page rendue (0xFF) après
/// chaque lecture pour les autres lecteurs (HWiNFO, LibreHardwareMonitor) ; le verrou du bus ISA partagé avec eux est pris.
/// Lecture seule : aucun réglage de ventilateur n'est écrit. Limite dite : la famille NCT6687 seulement (cartes MSI récentes) ;
/// une autre puce (ITE, autres Nuvoton) n'est pas devinée : rien n'est lu.
/// </summary>
public static class PawnIoSuperIo
{
    public const string EmpreinteLpcIo = "b3896a1cab0d808fca31fe2ebcae045d59dac690da87b17c858bb8da357eb45e";

    /// <summary>Un en-tête de ventilateur : son régime (0 = arrêté ou rien de branché) et son cycle PWM en %.</summary>
    public sealed record Ventilateur(int Index, int TrMin, int PwmPourcent);

    public sealed record VentilateursLus(ushort Puce, IReadOnlyList<Ventilateur> Ventilateurs);

    /// <summary>Les gestes sur les ports (couture de test) : ceux du module LpcIO, rien d'autre.</summary>
    public interface IPorts
    {
        void ChoisirEmplacement(ulong slot);
        void Ecrire(ulong port, ulong valeur);
        ulong Lire(ulong port);
        ulong LireMotConfig(ulong registre);
        void EcrireConfig(ulong registre, ulong valeur);
        void TrouverBars();
    }

    private const int Entetes = 8;

    /// <summary>Une lecture fait ~130 échanges avec PawnIO, ~79 ms de processeur (mesuré sur la machine de référence) : lue chaque
    /// seconde en partie, ce serait ~8 % d'un cœur et le bus ISA tenu d'autant. La règle « ventilateur qui s'arrête » cumule
    /// au moins 5 s d'arrêt sur la partie : une lecture toutes les 5 s suffit.</summary>
    public static readonly TimeSpan IntervalleLecture = TimeSpan.FromSeconds(5);
    private static readonly DerniereLecture<VentilateursLus> Recente = new(IntervalleLecture);

    /// <summary>La dernière lecture si elle a moins de <see cref="IntervalleLecture"/>, sinon une neuve.</summary>
    public static VentilateursLus? LireRecent() => Recente.Obtenir(Lire, DateTimeOffset.UtcNow);

    /// <summary>Lit les ventilateurs sur cette machine ; null sans PawnIO, sans droits, ou sur une puce non reconnue.</summary>
    public static VentilateursLus? Lire()
    {
        if (!PawnIoIntel.Installe() || PawnIoIntel.Module("LpcIO", EmpreinteLpcIo) is not { } module) return null;
        try
        {
            using var bus = new Mutex(false, @"Global\Access_ISABUS.HTP.Method");
            if (!Prendre(bus, TimeSpan.FromMilliseconds(500))) return null;
            try
            {
                PawnIoIntel.AssurerResolveur();
                if (PawnIoIntel.pawnio_open(out var h) != 0) return null;
                try
                {
                    return PawnIoIntel.pawnio_load(h, module, (nuint)module.Length) == 0 ? Lire(new PortsPawnIo(h)) : null;
                }
                finally { PawnIoIntel.pawnio_close(h); }
            }
            finally { bus.ReleaseMutex(); }
        }
        catch (Exception) { return null; }
    }

    /// <summary>Un outil mort en tenant le verrou du bus le laisse abandonné : WaitOne lève en l'ayant pris. Pris, donc, et
    /// relâché par l'appelant comme d'habitude ; sinon le bus resterait tenu par ce fil indéfiniment.</summary>
    internal static bool Prendre(Mutex verrou, TimeSpan delai)
    {
        try { return verrou.WaitOne(delai); }
        catch (AbandonedMutexException) { return true; }
    }

    /// <summary>Le protocole, sur des ports donnés. La sortie de configuration part toujours, même sur une erreur.</summary>
    public static VentilateursLus? Lire(IPorts ports)
    {
        foreach (var slot in new ulong[] { 0, 1 })
        {
            var registre = slot == 0 ? 0x2EUL : 0x4EUL;
            ushort puce;
            ulong baseEc = 0;
            try
            {
                ports.ChoisirEmplacement(slot);
                try
                {
                    // L'entrée en configuration est dans le try : une seconde écriture refusée laisserait la puce en mode
                    // configuration sans la sortie, et HWiNFO ou LibreHardwareMonitor, qui partagent le bus, la trouveraient ainsi.
                    ports.Ecrire(registre, 0x87);
                    ports.Ecrire(registre, 0x87);
                    puce = (ushort)ports.LireMotConfig(0x20);
                    if (!Nct6687(puce)) continue;
                    ports.EcrireConfig(0x07, 0x0B);
                    baseEc = ports.LireMotConfig(0x60);
                    ports.TrouverBars();
                }
                finally { ports.Ecrire(registre, 0xAA); }
            }
            catch (InvalidOperationException) { continue; } // un emplacement refusé par le module n'empêche pas l'autre
            if (baseEc is 0 or 0xFFFF) continue;
            var ventilateurs = Enumerable.Range(0, Entetes).Select(i =>
            {
                var r = (ushort)(0x140 + 2 * i);
                var pwm = LireEc(ports, baseEc, (ushort)(0x160 + i));
                return new Ventilateur(i, Regime(LireEc(ports, baseEc, r), LireEc(ports, baseEc, (ushort)(r + 1))),
                    (int)Math.Round(pwm * 100 / 255.0));
            }).ToList();
            return new VentilateursLus(puce, ventilateurs);
        }
        return null;
    }

    /// <summary>La famille NCT6687 (identifiant 0xD5xx).</summary>
    public static bool Nct6687(ushort puce) => puce >> 8 == 0xD5;

    /// <summary>Pur : le régime d'un en-tête ; 0xFFFF et 0xFFF8 (hors mesure, rien de branché) se lisent 0.</summary>
    public static int Regime(byte haut, byte bas)
    {
        var v = haut << 8 | bas;
        return v is 0xFFFF or 0xFFF8 ? 0 : v;
    }

    private static byte LireEc(IPorts p, ulong baseEc, ushort adresse)
    {
        // Attendre que la page soit libre (un autre lecteur peut s'en servir), 500 ms au plus, puis la prendre quand même.
        var limite = Environment.TickCount64 + 500;
        while (p.Lire(baseEc + 4) != 0xFF && Environment.TickCount64 < limite) Thread.Sleep(1);
        p.Ecrire(baseEc + 4, (ulong)(adresse >> 8));
        p.Ecrire(baseEc + 5, (ulong)(adresse & 0xFF));
        var v = (byte)p.Lire(baseEc + 6);
        p.Ecrire(baseEc + 4, 0xFF);
        return v;
    }

    private sealed class PortsPawnIo(nint h) : IPorts
    {
        public void ChoisirEmplacement(ulong slot) => Executer("ioctl_select_slot", [slot], 0);
        public void Ecrire(ulong port, ulong valeur) => Executer("ioctl_pio_outb", [port, valeur], 0);
        public ulong Lire(ulong port) => Executer("ioctl_pio_inb", [port], 1);
        public ulong LireMotConfig(ulong registre) => Executer("ioctl_superio_inw", [registre], 1);
        public void EcrireConfig(ulong registre, ulong valeur) => Executer("ioctl_superio_outb", [registre, valeur], 0);
        public void TrouverBars() => Executer("ioctl_find_bars", [], 0);

        private ulong Executer(string ioctl, ulong[] entree, int sorties)
        {
            var sortie = new ulong[sorties];
            var r = PawnIoIntel.pawnio_execute(h, ioctl, entree, (nuint)entree.Length, sortie, (nuint)sorties, out _);
            if (r != 0) throw new InvalidOperationException($"{ioctl} = 0x{r:X}");
            return sorties > 0 ? sortie[0] : 0;
        }
    }
}
