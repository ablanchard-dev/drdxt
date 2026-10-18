using System.Diagnostics;
using System.Runtime.InteropServices;
using DrDxt.Core;
using static DrDxt.Detection.Natif;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// Combien de rapports par seconde une souris ou une manette envoie vraiment, et avec quelle régularité, mesuré pendant
/// que le joueur la bouge. C'est ce chiffre, pas celui de la boîte, qui dit si 8000 Hz arrivent : la mesure démonte le
/// mythe ou le confirme, appareil par appareil. Lecture seule : une fenêtre invisible reçoit les entrées brutes, rien
/// n'est écrit ni intercepté (les entrées continuent vers les autres programmes).
/// </summary>
/// <remarks>
/// Microsoft Learn, Raw Input : RegisterRawInputDevices avec RIDEV_INPUTSINK (0x100) et une fenêtre cible, puis WM_INPUT
/// (0x00FF) et GetRawInputData(RID_INPUT). Pour un appareil HID, RAWHID porte dwCount rapports de dwSizeHid octets :
/// « Each WM_INPUT can indicate several inputs » ; chacun compte. Une souris rend un rapport par message. Les intervalles
/// sont pris à la réception (QueryPerformanceCounter) : un message qui porte plusieurs rapports compte un seul intervalle
/// et ses rapports en plus, sans intervalle inventé entre eux.
/// </remarks>
public static class CadenceDesEntrees
{
    public sealed record Appareil(string Nom, bool Virtuel, int Rapports, double DureeS, double MedianeMs, double P99Ms, int Trous);

    /// <summary>Pur : les statistiques d'un appareil à partir des instants de réception (en secondes) et du nombre de
    /// rapports par message. Un trou est un intervalle de plus de 3 fois la médiane. **Non chiffré.**</summary>
    public static Appareil? Calculer(string nom, bool virtuel, IReadOnlyList<double> instants, IReadOnlyList<int> rapportsParMessage)
    {
        if (instants.Count < 3) return null;
        var intervalles = instants.Zip(instants.Skip(1), (a, b) => (b - a) * 1000).Order().ToList();
        double Quantile(double q) => intervalles[(int)Math.Min(intervalles.Count - 1, Math.Ceiling(q * intervalles.Count) - 1)];
        var mediane = Quantile(0.5);
        var duree = instants[^1] - instants[0];
        if (duree <= 0) return null;
        return new Appareil(nom, virtuel, rapportsParMessage.Sum() - rapportsParMessage[0], duree, mediane, Quantile(0.99),
            intervalles.Count(i => mediane > 0 && i > 3 * mediane));
    }

    public static double Cadence(Appareil a) => a.Rapports / a.DureeS;

    public static RepairFinding Juger(IReadOnlyList<Appareil>? appareils, bool coucheActive, TimeSpan duree)
    {
        if (appareils is null)
            return new RepairFinding("LATENCE", "cadence reelle des entrees", null, null, RepairStatus.Unknown, Docteur.T("CR.NonLu"));
        if (appareils.Count == 0)
            return new RepairFinding("LATENCE", "cadence reelle des entrees", null, null, RepairStatus.Unknown, Docteur.T("CR.RienBouge", duree.TotalSeconds));
        var lignes = appareils.OrderByDescending(Cadence).Select(a => Docteur.T(a.Virtuel ? "CR.LigneVirtuelle" : "CR.Ligne",
            a.Nom, Cadence(a), a.MedianeMs, a.P99Ms, a.MedianeMs > 0 ? a.P99Ms / a.MedianeMs : 0, a.Trous));
        var texte = string.Join(" ", lignes) + (coucheActive ? Docteur.T("CR.Couche") : "");
        // Un appareil virtuel est dit, jamais pris pour le chiffre : il peut envoyer plus vite que l'appareil qu'il recopie.
        if (appareils.Where(a => !a.Virtuel).MaxBy(Cadence) is not { } reel)
            return new RepairFinding("LATENCE", "cadence reelle des entrees", null, null, RepairStatus.Unknown, texte + Docteur.T("CR.SeulementVirtuels"));
        return new RepairFinding("LATENCE", "cadence reelle des entrees", Docteur.T("CR.Valeur", Cadence(reel)), null,
            RepairStatus.Observed, texte + Docteur.T("CR.Mythe"));
    }

    // ── Mesure ──────────────────────────────────────────────────────────────────────────

    /// <summary>Écoute les entrées brutes des souris et des manettes pendant <paramref name="duree"/>, sur un fil à part.
    /// null si l'écoute n'a pas pu s'ouvrir.</summary>
    public static IReadOnlyList<Appareil>? Mesurer(TimeSpan duree, CancellationToken ct = default)
        => Ecouter(duree, ct)?.Select(f => (f, d: Decrire(f.Appareil))).Select(x => Calculer(x.d.Nom, x.d.Virtuel, x.f.Instants, x.f.Rapports))
            .OfType<Appareil>().ToList();

    /// <summary>Ce qu'un appareil a envoyé pendant l'écoute : l'instant de chaque message et le nombre de rapports qu'il
    /// portait.</summary>
    internal sealed record Flux(IntPtr Appareil, List<double> Instants, List<int> Rapports);

    /// <summary>L'écoute, sur un fil à part ; null si elle n'a pas pu s'ouvrir.</summary>
    internal static IReadOnlyList<Flux>? Ecouter(TimeSpan duree, CancellationToken ct)
    {
        // Raw Input n'admet qu'une fenêtre cible par type d'appareil et par processus (Microsoft Learn, RAWINPUTDEVICE) :
        // deux écoutes en même temps se voleraient l'abonnement, et le retrait de l'une couperait l'autre. Elles se suivent.
        UneEcoute.Wait(ct);
        try
        {
            IReadOnlyList<Flux>? resultat = null;
            var fil = new Thread(() => resultat = EcouterIci(duree, ct)) { IsBackground = true };
            fil.SetApartmentState(ApartmentState.STA);
            fil.Start();
            fil.Join(duree + TimeSpan.FromSeconds(5));
            return resultat;
        }
        finally { UneEcoute.Release(); }
    }

    private static readonly SemaphoreSlim UneEcoute = new(1, 1);

    private static IReadOnlyList<Flux>? EcouterIci(TimeSpan duree, CancellationToken ct)
    {
        var instants = new Dictionary<IntPtr, Flux>();
        WndProc procedure = (h, msg, w, l) =>
        {
            if (msg == 0x00FF) Recevoir(l, instants);
            return DefWindowProcW(h, msg, w, l);
        };
        var classe = new WNDCLASSEXW { cbSize = Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = procedure, lpszClassName = "DrDxtCadence" + Environment.CurrentManagedThreadId };
        if (RegisterClassExW(ref classe) == 0) return null;
        var fenetre = CreateWindowExW(0, classe.lpszClassName, "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);   // HWND_MESSAGE
        if (fenetre == IntPtr.Zero) return null;
        try
        {
            // Page 0x01 : souris (0x02), manettes (0x04, 0x05) ; en arrière-plan (RIDEV_INPUTSINK).
            var demandes = new[] { (ushort)0x02, (ushort)0x04, (ushort)0x05 }
                .Select(u => new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = u, dwFlags = 0x100, hwndTarget = fenetre }).ToArray();
            if (!RegisterRawInputDevices(demandes, (uint)demandes.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>())) return null;
            var fin = Stopwatch.GetTimestamp() + (long)(duree.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < fin && !ct.IsCancellationRequested)
            {
                while (PeekMessageW(out var m, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref m);
                    DispatchMessageW(ref m);
                }
                MsgWaitForMultipleObjects(0, null, false, 5, 0x04FF);
            }
            var retrait = demandes.Select(d => new RAWINPUTDEVICE { usUsagePage = d.usUsagePage, usUsage = d.usUsage, dwFlags = 0x1, hwndTarget = IntPtr.Zero }).ToArray();
            RegisterRawInputDevices(retrait, (uint)retrait.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());   // RIDEV_REMOVE
        }
        finally
        {
            DestroyWindow(fenetre);
            UnregisterClassW(classe.lpszClassName, IntPtr.Zero);
            GC.KeepAlive(procedure);
        }
        return instants.Values.ToList();
    }

    private static void Recevoir(IntPtr donnees, Dictionary<IntPtr, Flux> instants)
    {
        var maintenant = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        uint taille = 0;
        var entete = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        GetRawInputData(donnees, 0x10000003, IntPtr.Zero, ref taille, entete);   // RID_INPUT
        if (taille < entete) return;
        var tampon = Marshal.AllocHGlobal((int)taille);
        try
        {
            if (GetRawInputData(donnees, 0x10000003, tampon, ref taille, entete) != taille) return;
            var h = Marshal.PtrToStructure<RAWINPUTHEADER>(tampon);
            if (h.hDevice == IntPtr.Zero) return;   // entrée injectée par un logiciel : ce n'est pas un appareil
            var n = h.dwType == 2 ? Marshal.ReadInt32(tampon, (int)entete + 4) : 1;   // RIM_TYPEHID : RAWHID.dwCount
            if (!instants.TryGetValue(h.hDevice, out var s)) instants[h.hDevice] = s = new(h.hDevice, [], []);
            s.Instants.Add(maintenant);
            s.Rapports.Add(Math.Max(1, n));
        }
        finally { Marshal.FreeHGlobal(tampon); }
    }

    internal static (string Nom, bool Virtuel) Decrire(IntPtr appareil)
    {
        uint longueur = 0;
        GetRawInputDeviceInfoW(appareil, 0x20000007, (char[]?)null, ref longueur);   // RIDI_DEVICENAME
        var nom = new char[longueur + 1];
        if (GetRawInputDeviceInfoW(appareil, 0x20000007, nom, ref longueur) <= 0) return ("?", false);
        var chemin = new string(nom).TrimEnd('\0');
        // \\?\HID#VID_xxxx&PID_yyyy#7&...#{guid} : l'identifiant d'instance entre le préfixe et le GUID d'interface.
        var morceaux = chemin.Replace(@"\\?\", "").Split('#');
        var instance = morceaux.Length >= 3 ? string.Join("\\", morceaux.Take(3)) : chemin;
        var chaine = ArbreDesPeripheriques.Chaine(instance);
        var lisible = chaine.Skip(1).Select(c => ArbreDesPeripheriques.Texte(c, ArbreDesPeripheriques.NomConvivial)
                                                   ?? ArbreDesPeripheriques.Texte(c, ArbreDesPeripheriques.Description))
            .FirstOrDefault(n => n is not null) ?? instance;
        return (lisible, CouchesDEntree.Virtuel(chaine));
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public int cbSize; public uint style; public WndProc lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private record struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; public uint lPrivate; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW unnamedParam1);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int X, int Y,
        int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjects(uint nCount, IntPtr[]? pHandles, bool fWaitAll, uint dwMilliseconds, uint dwWakeMask);
    [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);
}
