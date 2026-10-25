namespace DrDxt.Core;

/// <summary>
/// La température d'un Ryzen, dans ses registres thermiques lus par SMN. Pur : décode des registres déjà lus. Source, lue
/// comme documentation (AMD ne publie pas ces adresses pour le grand public) : pilote Linux k10temp (drivers/hwmon/k10temp.c,
/// commit 451b1c19) : registre de contrôle à 0x59800, température dans les bits 31:21 par pas de 0,125 °C, décalée de
/// 49 °C quand le bit 19 (plage) ou les bits 17:16 valent 1 ; une température par CCD à 0x59800 + décalage + 4 × n, valide
/// si le bit 11 est levé, bits 10:0 par pas de 0,125 °C moins 49.
/// </summary>
public static class TemperatureAmd
{
    public const uint AdresseTctl = 0x00059800;

    /// <summary>Les décalages des températures par CCD, et leur nombre, par famille et modèle (k10temp) ; null ailleurs.</summary>
    public static (uint Decalage, int Nombre)? Ccd(int famille, int modele) => (famille, modele) switch
    {
        (0x17 or 0x18, 0x01 or 0x08 or 0x11 or 0x18) => (0x154, 4),
        (0x17 or 0x18, 0x31 or 0x47 or 0x60 or 0x68 or 0x71) => (0x154, 8),
        (0x17 or 0x18, >= 0xA0 and <= 0xAF) => (0x300, 8),
        (0x19, 0x00 or 0x01 or 0x08 or 0x21 or (>= 0x50 and <= 0x5F)) => (0x154, 8),
        (0x19, >= 0x40 and <= 0x4F) => (0x300, 8),
        (0x19, >= 0x60 and <= 0x7F) => (0x308, 8),
        (0x19, (>= 0x10 and <= 0x1F) or (>= 0xA0 and <= 0xAF)) => (0x300, 12),
        (0x1A, >= 0x00 and <= 0x1F) => (0x1F0, 16),
        (0x1A, >= 0x40 and <= 0x4F) => (0x308, 8),
        _ => null,
    };

    public static uint AdresseCcd(uint decalage, int ccd) => AdresseTctl + decalage + (uint)ccd * 4;

    /// <summary>Tctl, la température de contrôle. Sur les premiers Ryzen X et Threadripper, elle est décalée au-dessus de la
    /// vraie (k10temp, tctl_offset_table) : <see cref="DecalageTdie"/> le retire. Null sur la réponse d'erreur du SMN
    /// (tous les bits à 1), et sur toute lecture hors du physique.</summary>
    public static double? Tctl(uint registre)
    {
        if (registre == uint.MaxValue) return null;
        var t = (registre >> 21) * 0.125;
        return Physique((registre & (1u << 19)) != 0 || (registre & 0x30000) == 0x30000 ? t - 49 : t);
    }

    /// <summary>La température d'un CCD, ou null quand le registre ne la dit pas valide (CCD absent, réponse d'erreur, lecture
    /// hors du physique).</summary>
    public static double? TemperatureCcd(uint registre) =>
        registre == uint.MaxValue || (registre & 0x800) == 0 ? null : Physique((registre & 0x7FF) * 0.125 - 49);

    // Un Ryzen se coupe vers 115 °C, décalage Tctl des premiers X compris ; au-delà, ou sous -20 °C, le registre ne dit pas
    // une température : une valeur de garde ou de moniteur fondée dessus serait fausse.
    private static double? Physique(double t) => t is < -20 or > 130 ? null : t;

    /// <summary>Ce que Tctl ajoute à la vraie température sur les modèles que k10temp nomme (famille 17h seulement).</summary>
    public static double DecalageTdie(int famille, string? modele)
    {
        if (famille != 0x17 || modele is null) return 0;
        foreach (var (nom, decalage) in DecalagesTdie)
            if (modele.Contains(nom, StringComparison.Ordinal)) return decalage;
        return 0;
    }

    private static readonly (string Nom, double Decalage)[] DecalagesTdie =
    [
        ("AMD Ryzen 5 1600X", 20), ("AMD Ryzen 7 1700X", 20), ("AMD Ryzen 7 1800X", 20), ("AMD Ryzen 7 2700X", 10),
        ("AMD Ryzen Threadripper 19", 27), ("AMD Ryzen Threadripper 29", 27),
    ];
}

/// <summary>
/// Les seules adresses SMN que DrDXT lit. Le module de lecture AMD n'en garde aucune liste : c'est celle-ci qui borne ce
/// qu'il lit, et rien ne s'écrit. La température de contrôle, celle de chaque CCD aux décalages connus, et les registres de
/// timings de chaque canal mémoire (<see cref="TimingsMemoireAmd.Registres"/>, douze canaux au plus). Pur.
/// </summary>
public static class SmnAmd
{
    public const int CanauxMax = 12;

    private static readonly HashSet<uint> Autorisees =
    [
        TemperatureAmd.AdresseTctl,
        .. new uint[] { 0x154, 0x300, 0x308, 0x1F0 }.SelectMany(d => Enumerable.Range(0, 16).Select(n => TemperatureAmd.AdresseCcd(d, n))),
        .. Enumerable.Range(0, CanauxMax).SelectMany(c => TimingsMemoireAmd.Registres.Select(r => TimingsMemoireAmd.Adresse(c, r))),
    ];

    public static bool Autorisee(uint adresse) => Autorisees.Contains(adresse);
}

/// <summary>
/// La cause matérielle des redémarrages d'un Ryzen, dans FCH::PM::S5_RESET_STATUS (lue par le module AMDReset, famille 17h
/// et suivantes). Pur. Sens des bits : pilote Linux (arch/x86/kernel/cpu/amd.c, s5_reset_reason_txt, commit 87db7795). Les
/// bits restent levés tant que personne ne les efface (Linux les efface à chaque démarrage ; DrDXT n'écrit jamais) : ils
/// disent donc ce qui est arrivé depuis le dernier effacement, pas forcément au dernier arrêt.
/// </summary>
public static class ResetAmd
{
    public const string Nom = "cause materielle des redemarrages";

    /// <summary>Les bits que la source nomme ; les autres sont réservés.</summary>
    public static readonly IReadOnlySet<int> BitsConnus = new HashSet<int>
        { 0, 1, 2, 4, 9, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 29, 30, 31 };

    /// <summary>Ceux qui disent une panne (chaleur, erreur non corrigée, arrêt interne, chien de garde) plutôt qu'un geste.</summary>
    public static readonly IReadOnlySet<int> BitsDePanne = new HashSet<int> { 0, 9, 23, 25, 27, 29, 30, 31 };

    /// <summary>Les bits connus levés, ou null quand la lecture a rendu tous les bits à 1 (réponse d'erreur).</summary>
    public static IReadOnlyList<int>? Bits(uint valeur)
        => valeur == uint.MaxValue ? null : BitsConnus.Where(b => (valeur & (1u << b)) != 0).Order().ToList();

    /// <summary>Le constat ; rien sur un processeur non reconnu. valeur null : non lu.</summary>
    public static IEnumerable<RepairFinding> Juger(GenerationAmd.Ligne? generation, uint? valeur)
    {
        if (generation is null) yield break;
        if (valeur is not { } v || Bits(v) is not { } bits)
        {
            yield return new RepairFinding("STABILITE", Nom, null, null, RepairStatus.Unknown, Docteur.T("S5.NonLu"));
            yield break;
        }
        var hex = "0x" + v.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        if (bits.Count == 0)
        {
            yield return new RepairFinding("STABILITE", Nom, Docteur.T("S5.Aucune", hex), null, RepairStatus.Observed, Docteur.T("S5.AucuneExplication"));
            yield break;
        }
        var causes = string.Join(" ; ", bits.Select(b => Docteur.T("S5.Bit" + b)));
        yield return new RepairFinding("STABILITE", Nom, hex, null,
            bits.Any(BitsDePanne.Contains) ? RepairStatus.Suspicious : RepairStatus.Observed,
            Docteur.T("S5.Causes", causes));
    }
}
