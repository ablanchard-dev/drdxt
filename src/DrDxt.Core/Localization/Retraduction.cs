using System.Globalization;
using System.Text.RegularExpressions;

namespace DrDxt.Core;

/// <summary>
/// Les phrases d'un constat écrites dans l'autre langue du docteur (une machine exportée par un testeur anglophone, relue
/// en français), réécrites dans la langue courante. Le texte est découpé, de gauche à droite, en morceaux que reconnaît
/// un gabarit de l'autre langue ; chaque morceau est réécrit avec le gabarit de la même clé dans la langue courante et
/// les mêmes valeurs. Un texte qui ne se découpe pas entièrement en gabarits reste tel quel : rien n'est deviné.
/// </summary>
public static class Retraduction
{
    // Un gabarit sans texte fixe (« {0} ») reconnaîtrait n'importe quoi : il n'entre pas dans le découpage. Trois lettres
    // suffisent (« {0} h à {1} h ») : la garde des valeurs, plus bas, écarte les coupes fausses.
    private const int LitteralMinimum = 3;

    private sealed record Gabarit(string Cle, Regex Morceau, Regex? Fin, int Litteral);

    // Le découpage essaie chaque gabarit à chaque position, et chaque valeur capturée se relit à son tour : un long texte
    // d'incident (un millier de caractères) y passait plus de 10 s, et le scan rejoué ne finissait jamais. Un texte qui ne
    // se découpe pas dans cette borne reste tel quel, comme un texte que rien ne reconnaît. Elle compte le travail, pas des
    // millisecondes : en temps, un PC chargé (un jeu, une suite de tests en parallèle) rendait sans traduction un texte qui
    // se traduit au repos ; comptée, le même texte donne le même résultat sur toutes les machines. Chaque essai d'un gabarit
    // compte ce qu'il peut lire, le reste du texte. Sur les machines enregistrées, la traduction la plus chère en compte
    // 1,6 million ; le long texte du test, qui ne se découpe pas, atteint la borne en une vingtaine de millisecondes.
    internal const long TravailMax = 5_000_000;

    // La borne se lit entre deux essais : une correspondance qui s'emballerait sur un texte forgé lui échapperait. Chaque
    // essai a donc sa propre limite, son dépassement rendant le texte tel quel. Un essai normal dure quelques microsecondes.
    private static readonly TimeSpan CorrespondanceMax = TimeSpan.FromMilliseconds(100);

    private sealed class Travail(long restant) { public long Restant = restant; }

    private sealed class BorneAtteinte : Exception;

    public static string? Retraduire(string? texte)
    {
        if (string.IsNullOrWhiteSpace(texte)) return texte;
        var gabarits = Docteur.Langue.TwoLetterISOLanguageName == "fr" ? GabaritsAnglais.Value : GabaritsFrancais.Value;
        try { return Relire(texte, gabarits, new Travail(TravailMax)); }
        catch (Exception ex) when (ex is BorneAtteinte or RegexMatchTimeoutException) { return texte; }
    }

    // La borne est celle du texte entier : les valeurs relues en chemin la partagent.
    private static string Relire(string texte, IReadOnlyList<Gabarit> gabarits, Travail travail)
    {
        if (string.IsNullOrWhiteSpace(texte)) return texte;
        var memo = new Dictionary<int, List<string>?>();
        return Decouper(texte, 0, gabarits, memo, travail) is { } morceaux ? string.Concat(morceaux) : texte;
    }

    // Les mots outils de la langue source : une valeur qui en porte est une phrase, pas une donnée.
    private static readonly Regex MotsFrancais = new(@"\b(le|la|les|des|du|une|est|pas|aucun|aucune|ton|ta|tes|pour|avec|dans|sur|qui|mais)\b|[éèàçùêâôî]",
        RegexOptions.Compiled);
    private static readonly Regex MotsAnglais = new(@"\b(the|and|is|are|not|your|with|for|this|that|was|from|when)\b", RegexOptions.Compiled);

    // Un trou ne garde qu'une donnée (un nombre, un nom de matériel) ou une phrase qui se retraduit elle-même : sans cette
    // garde, le dernier trou d'un gabarit avale une ancienne phrase entière et la « traduction » paraît complète.
    private static bool ValeurAcceptable(string retraduite)
        => !(Docteur.Langue.TwoLetterISOLanguageName == "fr" ? MotsAnglais : MotsFrancais).IsMatch(retraduite);

    private static List<string>? Decouper(string texte, int i, IReadOnlyList<Gabarit> gabarits, Dictionary<int, List<string>?> memo,
        Travail travail)
    {
        if (i == texte.Length) return [];
        if (memo.TryGetValue(i, out var deja)) return deja;
        memo[i] = null;
        foreach (var g in gabarits)
        {
            foreach (var motif in g.Fin is { } fin ? new[] { g.Morceau, fin } : [g.Morceau])
            {
                if ((travail.Restant -= texte.Length - i) < 0) throw new BorneAtteinte();
                if (motif.Match(texte, i) is not { Success: true } m || m.Length == 0) continue;
                if (Decouper(texte, i + m.Length, gabarits, memo, travail) is not { } suite) continue;
                var brutes = Enumerable.Range(0, 10).TakeWhile(n => m.Groups[$"v{n}"].Success).Select(n => m.Groups[$"v{n}"].Value).ToList();
                var valeurs = brutes.Select(v => Relire(v, gabarits, travail)).ToList();
                if (!valeurs.All(ValeurAcceptable)) continue;
                return memo[i] = [Docteur.T(g.Cle, valeurs.Cast<object?>().ToArray()), .. suite];
            }
        }
        // Une espace entre deux morceaux concaténés, ou le point qu'un constat enregistré ajoute au gabarit, se garde tel
        // quel, après les gabarits qui commencent par lui.
        if ((char.IsWhiteSpace(texte[i]) || texte[i] is '.' or ';') && Decouper(texte, i + 1, gabarits, memo, travail) is { } apresSeparateur)
            return memo[i] = [texte[i].ToString(), .. apresSeparateur];
        return null;
    }

    private static readonly CultureInfo Francais = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly CultureInfo Anglais = CultureInfo.GetCultureInfo("en-US");

    private static readonly Lazy<IReadOnlyList<Gabarit>> GabaritsFrancais = new(() => Gabarits(Francais));
    private static readonly Lazy<IReadOnlyList<Gabarit>> GabaritsAnglais = new(() => Gabarits(Anglais));

    // Les plus longs d'abord : un gabarit précis passe avant un gabarit qui en est le début.
    private static IReadOnlyList<Gabarit> Gabarits(CultureInfo langue) => Docteur.Textes(langue)
        .Select(t => (t.Cle, t.Texte, Litteral: Regex.Replace(t.Texte, @"\{\d+(:[^}]*)?\}", "").Length))
        .Where(t => t.Litteral >= LitteralMinimum)
        .Select(t =>
        {
            var echappe = Regex.Escape(t.Texte);
            string Trous(bool dernierGourmand)
            {
                var trous = Regex.Matches(echappe, @"\\\{(\d+)(:[^}]*)?}").Count;
                var n = 0;
                return Regex.Replace(echappe, @"\\\{(\d+)(:[^}]*)?}",
                    m => $"(?<v{m.Groups[1].Value}>{(dernierGourmand && ++n == trous ? ".+" : ".+?")})");
            }
            // Un gabarit qui finit par une valeur ne se reconnaît en entier qu'en fin de texte.
            var finitParUneValeur = Regex.IsMatch(t.Texte, @"\{\d+(:[^}]*)?\}$");
            // Interprétés, pas compilés : une expression compilée prépare son code au premier essai qui passe son début, et
            // des milliers de gabarits le faisaient pendant le premier texte lu (1,5 s pour le long texte du test, 4 ms relu),
            // dans la limite de chaque essai, que cette préparation faisait sauter sur un PC chargé.
            return new Gabarit(t.Cle, new Regex(@"\G" + Trous(false), RegexOptions.None, CorrespondanceMax),
                finitParUneValeur ? new Regex(@"\G" + Trous(true) + "$", RegexOptions.None, CorrespondanceMax) : null, t.Litteral);
        })
        .OrderByDescending(g => g.Litteral)
        .ToList();
}
