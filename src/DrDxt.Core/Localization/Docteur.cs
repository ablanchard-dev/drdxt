using System.Globalization;
using System.Resources;

namespace DrDxt.Core;

/// <summary>
/// Les phrases du docteur, dans la langue de l'App. L'App la fixe au démarrage (celle de Windows) ; sans elle, le français,
/// la langue dans laquelle le docteur a été écrit. Une clé absente se rend telle quelle : la parité des deux fichiers est
/// gardée par un test.
/// </summary>
public static class Docteur
{
    private static readonly ResourceManager s_textes = new("DrDxt.Core.Resources.Docteur", typeof(Docteur).Assembly);
    private static readonly AsyncLocal<CultureInfo?> s_portee = new();
    private static CultureInfo s_langue = CultureInfo.GetCultureInfo("fr-FR");

    public static CultureInfo Langue => s_portee.Value ?? s_langue;

    /// <summary>Au démarrage de l'App : la langue de Windows, ramenée à l'anglais quand le docteur ne la parle pas. Le
    /// texte retombe déjà sur l'anglais ; les dates et nombres doivent suivre (sinon « Okt. 15 at 14:00 »).</summary>
    public static void FixerLangue(CultureInfo langue) => s_langue = Parlee(langue);

    public static CultureInfo Parlee(CultureInfo langue)
        => langue.TwoLetterISOLanguageName is "fr" or "en" ? langue : CultureInfo.GetCultureInfo("en-US");

    /// <summary>Une langue pour ce fil d'exécution seulement (tests, rapport dans une autre langue).</summary>
    public static IDisposable Portee(CultureInfo langue)
    {
        var avant = s_portee.Value;
        s_portee.Value = langue;
        return new Retour(() => s_portee.Value = avant);
    }

    public static string T(string cle) => Chercher(cle) ?? cle;

    /// <summary>La phrase, ou null si la clé n'existe pas (un titre sans traduction s'affiche alors tel quel).</summary>
    public static string? Chercher(string cle) => s_textes.GetString(cle, Langue);

    public static string T(string cle, params object?[] valeurs) => string.Format(Langue, T(cle), valeurs);

    /// <summary>Le deux-points entre un libellé et sa valeur : le français met une espace avant, l'anglais non.</summary>
    public static string DeuxPoints => Langue.TwoLetterISOLanguageName == "fr" ? " : " : ": ";

    private static readonly CultureInfo Francais = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Le texte est-il celui de la clé, dans l'une des langues du docteur (français, ou l'anglais du fichier
    /// neutre) ? Pour reconnaître un texte formé sous une autre langue que celle d'aujourd'hui.</summary>
    public static bool EstLeTexteDe(string cle, string? texte)
        => texte is not null && (texte == s_textes.GetString(cle, Francais) || texte == s_textes.GetString(cle, CultureInfo.InvariantCulture));

    // Les noms de constats qui portent une partie variable (le disque, la carte, le jeu, le service). Seuls ceux-ci se
    // reformulent à l'affichage ; un nom fixe se traduit par sa clé « Nom.<nom> ».
    private static readonly string[] ClesDeNom =
    [
        "SW.ErreursNom", "SW.SanteNom", "BR.Nom.Lien", "BR.Nom.LienSsd", "BR.Nom.Ecran", "FS.Nom", "RU.Nom", "IM.NomChanges",
        "SE.Remplacable.Nom", "SE.NonCite.Nom", "TA.Nom", "NV.Nom", "PX.Nom", "BL.Nom", "DC.Nom", "RB.Nom", "FS.NomManette", "RR.NomRegle", "TO.NomAffinite", "VC.Nom", "G32.Nom", "COUL.Nom",
    ];

    /// <summary>Le nom d'un constat, toujours formé en français : c'est la clé du journal (deux scans se comparent par
    /// Domaine + Nom), et elle ne change pas avec la langue de Windows. L'affichage le traduit (<see cref="TraduireNom"/>).</summary>
    public static string Nom(string cle, params object?[] valeurs)
        => string.Format(Francais, s_textes.GetString(cle, Francais) ?? cle, valeurs);

    /// <summary>Un nom formé par <see cref="Nom"/>, dans la langue de l'App : son gabarit français est reconnu, ses parties
    /// variables reprises dans le gabarit de la langue. Null en français, ou quand aucun gabarit ne le reconnaît.</summary>
    public static string? TraduireNom(string nom)
    {
        if (Langue.TwoLetterISOLanguageName == "fr") return null;
        foreach (var (cle, motif) in Motifs.Value)
        {
            if (motif.Match(nom) is not { Success: true } trouve) continue;
            var valeurs = Enumerable.Range(0, 10).TakeWhile(i => trouve.Groups[$"v{i}"].Success)
                .Select(i => (object?)ValeurTraduite(trouve.Groups[$"v{i}"].Value)).ToArray();
            return T(cle, valeurs);
        }
        return null;
    }

    // Les gabarits français ne changent pas : leurs motifs se compilent une fois.
    private static readonly Lazy<(string Cle, System.Text.RegularExpressions.Regex Motif)[]> Motifs = new(() => ClesDeNom
        .Where(cle => s_textes.GetString(cle, Francais) is not null)
        .Select(cle => (cle, new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Escape(s_textes.GetString(cle, Francais)!),
            @"\\\{(\d+)\}", m => $"(?<v{m.Groups[1].Value}>.+?)") + "$", System.Text.RegularExpressions.RegexOptions.Compiled)))
        .ToArray());

    /// <summary>Tous les textes du docteur dans une langue, clé et gabarit (pour <see cref="Retraduction"/>).</summary>
    public static IEnumerable<(string Cle, string Texte)> Textes(CultureInfo langue)
    {
        if (s_textes.GetResourceSet(langue, createIfNotExists: true, tryParents: true) is not { } jeu) yield break;
        foreach (System.Collections.DictionaryEntry e in jeu)
            if (e.Key is string cle && e.Value is string texte) yield return (cle, texte);
    }

    // Une partie variable qui est elle-même un texte du docteur (la famille d'un changement : « pilotes ») se traduit aussi.
    private static string ValeurTraduite(string valeur)
        => Familles.Value.TryGetValue(valeur, out var cle) ? T(cle) : valeur;

    // Le texte français de chaque famille de changement, vers sa clé : lu une fois.
    private static readonly Lazy<Dictionary<string, string>> Familles = new(() =>
    {
        var familles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (s_textes.GetResourceSet(Francais, createIfNotExists: true, tryParents: true) is { } jeu)
            foreach (System.Collections.DictionaryEntry e in jeu)
                if (e.Key is string cle && cle.StartsWith("IM.Famille.", StringComparison.Ordinal) && e.Value is string texte)
                    familles.TryAdd(texte, cle);
        return familles;
    });

    private sealed class Retour(Action rendre) : IDisposable
    {
        public void Dispose() => rendre();
    }
}
