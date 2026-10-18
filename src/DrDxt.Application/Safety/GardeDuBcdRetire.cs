using System.Text.Json;

namespace DrDxt.Application.Safety;

/// <summary>
/// La valeur BCD que la carte Windows retire (bootstatuspolicy), gardée sur disque pour son « Annuler ». bcdedit écrit son
/// magasin sur disque tout de suite : une garde écrite après lui, ou laissée dans le cache du système, se perdait sur une
/// coupure entre les deux, et le réglage retiré n'avait plus d'annulation. La garde s'écrit donc avant l'appel au helper,
/// forcée sur le disque (écriture directe du fichier final, puis vidage), et s'efface si le helper refuse.
/// Fichier du compte : à la lecture, seul l'élément de la carte est rendu, et le helper borne la valeur.
/// </summary>
public sealed class GardeDuBcdRetire(string chemin)
{
    /// <summary>Le seul élément que la carte retire.</summary>
    public const string Element = "bootstatuspolicy";

    private sealed record Contenu(string Element, string Valeur);

    /// <summary>Écrit la garde et la force sur le disque. Faux = rien n'est garanti sur le disque : l'appelant n'écrit pas
    /// le BCD (jamais un retrait sans annulation).</summary>
    public bool Ecrire(string element, string valeur)
    {
        if (element != Element || string.IsNullOrEmpty(valeur)) return false;
        try
        {
            // Le fichier final directement : il n'y a pas d'ancien contenu à protéger, et un renommage resterait, lui, dans le
            // cache. Coupé en cours d'écriture, il est illisible, donc pas une garde, et bcdedit n'a pas encore tourné.
            // Fichier du compte : le nom est retiré (un lien posé d'avance n'est que ce nom), puis créé en exclusif. Un lien
            // reposé entre les deux fait échouer la création : pas de garde, donc pas d'écriture du BCD.
            Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
            File.Delete(chemin);
            using var flux = new FileStream(chemin, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            JsonSerializer.Serialize(flux, new Contenu(element, valeur));
            flux.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>La garde, ou null (absente, illisible, ou un autre élément que celui de la carte).</summary>
    public (string Element, string Valeur)? Lire()
    {
        try
        {
            if (!File.Exists(chemin)) return null;
            var g = JsonSerializer.Deserialize<Contenu>(File.ReadAllText(chemin));
            // Le helper borne les valeurs, pas l'élément : une garde modifiée ferait poser par « Annuler » un autre élément
            // permis (useplatformclock Yes).
            return g is { Element: Element, Valeur.Length: > 0 } ? (g.Element, g.Valeur) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Le helper a refusé, ou l'annulation est faite : plus rien à garder.</summary>
    public void Effacer()
    {
        try { File.Delete(chemin); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* garde orpheline : l'écran ne la lit que si l'audit ne voit plus l'élément, et elle ne pose rien seule */ }
    }
}
