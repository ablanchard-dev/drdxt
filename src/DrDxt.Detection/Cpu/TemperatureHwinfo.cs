using System.IO.MemoryMappedFiles;
using System.Text;

namespace DrDxt.Detection.Cpu;

/// <summary>
/// La température du processeur lue dans la mémoire partagée de HWiNFO, quand HWiNFO tourne
/// avec « Shared Memory Support » (réglage <c>SensorsSM=1</c>). DrDXT n'ajoute aucun pilote : sans pilote noyau, Windows
/// ne donne pas le capteur des cœurs, et la zone ACPI est un piège (constante à la décimale, voir RepairAuditService).
/// HWiNFO a son propre pilote ; DrDXT ne fait que lire ce qu'il publie. HWiNFO absent ou arrêté : <c>null</c>.
///
/// Disposition (SDK public de HWiNFO, structures alignées sur 1 octet) : en-tête « HWiS », puis les capteurs, puis les
/// relevés ; chaque relevé porte son type (1 = température), son libellé d'origine et sa valeur courante en double.
/// La taille d'un relevé est lue dans l'en-tête, jamais supposée : les versions récentes l'allongent.
/// </summary>
public static class TemperatureHwinfo
{
    public const string NomMemoire = @"Global\HWiNFO_SENS_SM2";
    private const uint Signature = 0x53695748; // « HWiS »
    private const uint TypeTemperature = 1;
    private const uint TypeVentilateur = 3;

    // Libellés d'origine (anglais, ceux du SDK, jamais traduits) du capteur de paquet, par ordre de préférence.
    private static readonly string[] Libelles = ["CPU Package", "CPU (Tctl/Tdie)", "CPU Die (average)", "Core Max"];

    public sealed record Lecture(double Celsius, string Libelle);

    /// <summary>Lit HWiNFO s'il publie. Toute panne (absent, droits, disposition inattendue) : <c>null</c>.</summary>
    public static Lecture? Lire()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(NomMemoire, MemoryMappedFileRights.Read);
            using var vue = mmf.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
            var octets = new byte[vue.Length];
            vue.ReadExactly(octets);
            return Analyser(octets, DateTimeOffset.UtcNow);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Au-delà, la dernière publication de HWiNFO est trop vieille : HWiNFO figé ou en pause, la valeur ne dit
    /// plus « maintenant ». **Non chiffré** : HWiNFO publie toutes les 2 s par défaut ; 15 s laisse une large marge.</summary>
    public static readonly TimeSpan Fraicheur = TimeSpan.FromSeconds(15);

    /// <summary>Pur : le bloc tel que HWiNFO le publie. Signature absente, bornes incohérentes ou publication périmée
    /// (horodatage de l'en-tête, secondes Unix à l'octet 12) : <c>null</c>.</summary>
    public static Lecture? Analyser(byte[] b, DateTimeOffset maintenant)
    {
        if (b.Length < 44 || BitConverter.ToUInt32(b, 0) != Signature) return null;
        var publie = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(BitConverter.ToInt64(b, 12), 0, 253402300799));
        if ((maintenant - publie).Duration() > Fraicheur) return null;
        var debut = BitConverter.ToUInt32(b, 32);
        var taille = BitConverter.ToUInt32(b, 36);
        var nombre = BitConverter.ToUInt32(b, 40);
        if (taille < 316 || debut + (long)taille * nombre > b.Length) return null;

        var trouves = new List<(int Rang, double Valeur, string Libelle)>();
        for (var i = 0; i < nombre; i++)
        {
            var o = (int)(debut + taille * i);
            if (BitConverter.ToUInt32(b, o) != TypeTemperature) continue;
            var libelle = Texte(b, o + 12, 128);
            var rang = Array.IndexOf(Libelles, libelle);
            if (rang < 0) continue;
            var valeur = BitConverter.ToDouble(b, o + 284);
            // Hors de ce qu'un processeur peut afficher : une disposition mal lue, pas une température.
            if (valeur is > 0 and < 125) trouves.Add((rang, valeur, libelle));
        }
        return trouves.Count == 0 ? null : trouves.OrderBy(t => t.Rang).Select(t => new Lecture(t.Valeur, t.Libelle)).First();
    }

    /// <summary>Les ventilateurs que HWiNFO publie,
    /// ou null. Toute panne : null.</summary>
    public static IReadOnlyList<DrDxt.Core.VentilateurLu>? LireVentilateurs()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(NomMemoire, MemoryMappedFileRights.Read);
            using var vue = mmf.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
            var octets = new byte[vue.Length];
            vue.ReadExactly(octets);
            return AnalyserVentilateurs(octets, DateTimeOffset.UtcNow);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Pur : les relevés de type ventilateur (3, en tr/min), dans l'ordre publié, numérotés à partir de
    /// <see cref="DrDxt.Core.VentilateurLu.DebutHwinfo"/>, avec leur libellé d'origine ; le cycle n'est pas publié : inconnu.
    /// Même garde que la température (signature, bornes, fraîcheur) ; aucun ventilateur : null.</summary>
    public static IReadOnlyList<DrDxt.Core.VentilateurLu>? AnalyserVentilateurs(byte[] b, DateTimeOffset maintenant)
    {
        if (b.Length < 44 || BitConverter.ToUInt32(b, 0) != Signature) return null;
        var publie = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(BitConverter.ToInt64(b, 12), 0, 253402300799));
        if ((maintenant - publie).Duration() > Fraicheur) return null;
        var debut = BitConverter.ToUInt32(b, 32);
        var taille = BitConverter.ToUInt32(b, 36);
        var nombre = BitConverter.ToUInt32(b, 40);
        if (taille < 316 || debut + (long)taille * nombre > b.Length) return null;
        var lus = new List<DrDxt.Core.VentilateurLu>();
        for (var i = 0; i < nombre; i++)
        {
            var o = (int)(debut + taille * i);
            if (BitConverter.ToUInt32(b, o) != TypeVentilateur) continue;
            // Les ventilateurs de la carte graphique s'arrêtent sous faible charge (mode 0 tr/min) : jugés comme ceux du
            // boîtier, ils passeraient pour des roulements usés. HWiNFO nomme leur capteur « GPU [#0]: … ».
            if (Capteur(b, o)?.StartsWith("GPU", StringComparison.OrdinalIgnoreCase) == true) continue;
            var valeur = BitConverter.ToDouble(b, o + 284);
            // Hors de ce qu'un ventilateur de PC peut tourner : une disposition mal lue.
            if (valeur is < 0 or > 20_000 || double.IsNaN(valeur)) continue;
            lus.Add(new DrDxt.Core.VentilateurLu(DrDxt.Core.VentilateurLu.DebutHwinfo + lus.Count, (int)Math.Round(valeur), null, Texte(b, o + 12, 128)));
        }
        return lus.Count == 0 ? null : lus;
    }

    /// <summary>La version du HWiNFO qui tourne, lue dans son exécutable (HWiNFO est élevé :
    /// QueryFullProcessImageName sous PROCESS_QUERY_LIMITED_INFORMATION, que Windows accorde au même compte).
    /// <c>null</c> = HWiNFO ne tourne pas ; « ? » = il tourne, version non lue.</summary>
    public static string? VersionQuiTourne()
    {
        string? trouve = null;
        foreach (var nom in new[] { "HWiNFO64", "HWiNFO32", "HWiNFO" })
        {
            // Chaque processus rendu est libéré, pas seulement le premier.
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(nom))
            {
                using (p)
                {
                    if (trouve is not null) continue;
                    var chemin = CheminDe(p.Id);
                    trouve = chemin is null ? "?" : System.Diagnostics.FileVersionInfo.GetVersionInfo(chemin).FileVersion?.Trim() ?? "?";
                }
            }
        }
        return trouve;
    }

    /// <summary>La mémoire partagée de HWiNFO existe-t-elle ? Sépare « ne publie pas » de
    /// « publie, mais la température n'y est pas lisible » (HWiNFO en pause, capteur au nom inattendu).</summary>
    public static bool MemoirePubliee()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(NomMemoire, MemoryMappedFileRights.Read);
            return true;
        }
        catch (Exception) { return false; }
    }

    private static string? CheminDe(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var tampon = new StringBuilder(1024);
            var taille = tampon.Capacity;
            return QueryFullProcessImageName(h, 0, tampon, ref taille) ? tampon.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint acces, bool heriter, int pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr h, uint drapeaux, StringBuilder nom, ref int taille);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    // Le nom du capteur d'un relevé (index en +4 du relevé, section des capteurs décrite en +20/+24/+28 de l'en-tête, nom
    // d'origine en +8 du capteur) ; null si la section n'est pas lisible.
    private static string? Capteur(byte[] b, int releve)
    {
        var index = BitConverter.ToUInt32(b, releve + 4);
        var debut = BitConverter.ToUInt32(b, 20);
        var taille = BitConverter.ToUInt32(b, 24);
        var nombre = BitConverter.ToUInt32(b, 28);
        if (taille < 136 || index >= nombre || debut + (long)taille * (index + 1) > b.Length) return null;
        return Texte(b, (int)(debut + taille * index + 8), 128);
    }

    private static string Texte(byte[] b, int o, int max)
    {
        var fin = Array.IndexOf(b, (byte)0, o, max);
        return Encoding.ASCII.GetString(b, o, (fin < 0 ? o + max : fin) - o);
    }
}
