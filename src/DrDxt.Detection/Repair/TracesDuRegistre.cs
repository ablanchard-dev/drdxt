using System.Globalization;
using System.Runtime.Versioning;
using DrDxt.Core;
using Microsoft.Win32;
using static DrDxt.Detection.Natif;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Lit, en lecture seule, les clés de <see cref="TracesDOptimiseurs.Liste"/> et les politiques d'affinité des
/// interruptions des périphériques PCI, avec la date de dernière écriture de chaque clé (RegQueryInfoKeyW,
/// lpftLastWriteTime : la dernière modification de la clé ou de l'une de ses valeurs). Rien n'est écrit.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TracesDuRegistre
{
    /// <summary>Les lectures, ou null si le registre ne répond pas.</summary>
    public static IReadOnlyList<TracesDOptimiseurs.Lecture>? Lire()
    {
        try
        {
            var lectures = new List<TracesDOptimiseurs.Lecture>();
            foreach (var c in TracesDOptimiseurs.Liste)
            {
                using var cle = Registry.LocalMachine.OpenSubKey(c.Chemin);
                if (cle is not null) lectures.Add(new(c.Id, c.Chemin, Valeurs(cle, c.Valeurs), DateDe(cle)));
            }
            using var pci = Registry.LocalMachine.OpenSubKey(TracesDOptimiseurs.RacineDesPeripheriques);
            lectures.AddRange(ParPeripherique(pci?.GetSubKeyNames() ?? [], peripherique => Affinites(pci!, peripherique)));
            return lectures;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Pur : chaque périphérique lu à part. Une clé refusée (ACL, périphérique retiré pendant la lecture) ne coûte
    /// que ce périphérique, jamais les traces des autres.</summary>
    internal static IEnumerable<T> ParPeripherique<T>(IEnumerable<string> peripheriques, Func<string, IEnumerable<T>> lire)
    {
        var lues = new List<T>();
        foreach (var p in peripheriques)
        {
            try { lues.AddRange(lire(p)); }
            catch (Exception) { /* ce périphérique seul reste non lu */ }
        }
        return lues;
    }

    private static List<TracesDOptimiseurs.Lecture> Affinites(RegistryKey pci, string peripherique)
    {
        var lectures = new List<TracesDOptimiseurs.Lecture>();
        using var p = pci.OpenSubKey(peripherique);
        foreach (var instance in p?.GetSubKeyNames() ?? [])
        {
            using var i = p!.OpenSubKey(instance);
            using var a = i?.OpenSubKey(TracesDOptimiseurs.SousCleAffinite);
            if (a is null) continue;
            var valeurs = Valeurs(a, TracesDOptimiseurs.ValeursAffinite);
            if (valeurs.Count == 0) continue;
            lectures.Add(new("affinite",
                $@"{TracesDOptimiseurs.RacineDesPeripheriques}\{peripherique}\{instance}\{TracesDOptimiseurs.SousCleAffinite}",
                valeurs, DateDe(a), NomDuPeripherique(i!)));
        }
        return lectures;
    }

    private static DateTimeOffset? DateDe(RegistryKey cle)
    {
        try
        {
            return RegQueryInfoKey(cle.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var ft) == 0 && ft > 0 ? DateTimeOffset.FromFileTime(ft) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Les seules valeurs de la liste présentes dans la clé, en texte : un nombre en décimal, des octets en
    /// hexadécimal.</summary>
    internal static Dictionary<string, string> Valeurs(RegistryKey cle, IReadOnlyList<string> noms)
    {
        var lues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var nom in noms)
            if (cle.GetValue(nom, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } v)
                lues[nom] = Texte(v);
        return lues;
    }

    internal static string Texte(object v) => v switch
    {
        int i => ((uint)i).ToString(CultureInfo.InvariantCulture),
        long l => ((ulong)l).ToString(CultureInfo.InvariantCulture),
        byte[] b => "0x" + Convert.ToHexString(b),
        string[] s => string.Join(" ", s),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "?",
    };

    /// <summary>FriendlyName, sinon DeviceDesc (« @oem12.inf,%nom%;NVIDIA GeForce RTX 4060 » : la partie après « ; »).</summary>
    private static string? NomDuPeripherique(RegistryKey instance)
    {
        var nom = instance.GetValue("FriendlyName") as string ?? instance.GetValue("DeviceDesc") as string;
        return nom is null ? null : nom[(nom.LastIndexOf(';') + 1)..].Trim();
    }
}
