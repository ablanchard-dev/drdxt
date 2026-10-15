using System.Text.RegularExpressions;

namespace DrDxt.Core;

/// <summary>
/// Le fichier d'échange géré par le système sur un disque dur alors que Windows est sur un SSD (<see cref="FichierDEchangeSurDisqueDur"/>
/// le constate). Rendre la gestion à Windows (<c>?:\pagefile.sys</c>, la valeur que pose « Gestion automatique » dans
/// sysdm.cpl) le remet sur le disque système. Seules les entrées « taille gérée par le système » sur un lecteur nommé
/// (<c>D:\pagefile.sys 0 0</c>, ou sans taille) se déplacent : une taille choisie à la main est un choix du joueur.
/// </summary>
public static class FichierDEchangeADeplacer
{
    private static readonly Regex GereParLeSysteme = new(@"^[A-Za-z]:\\pagefile\.sys( 0 0)?$", RegexOptions.CultureInvariant);

    /// <summary>Une entrée de <c>PagingFiles</c> en taille gérée par le système, sur un lecteur nommé.</summary>
    public static bool EntreeGereeParLeSysteme(string entree) => GereParLeSysteme.IsMatch(entree.Trim());

    /// <param name="pagingFiles">Les entrées de <c>PagingFiles</c> ; null = non lu.</param>
    /// <param name="typesParLettre">Le type du disque de chaque lettre (3 disque dur, 4 SSD) ; une lettre absente n'est pas dite.</param>
    /// <param name="lettreSysteme">La lettre du disque de Windows.</param>
    /// <returns>On : à déplacer ; Off : rien à déplacer ; Unknown : non lu, ou un cas que DrDXT laisse au joueur.</returns>
    public static TriState Juger(IReadOnlyList<string>? pagingFiles, IReadOnlyDictionary<char, int> typesParLettre, char lettreSysteme)
    {
        var entrees = pagingFiles?.Select(e => e.Trim()).Where(e => e.Length > 0).ToList();
        // Désactivé (liste vide) : c'est fixit.sane_pagefile qui le rend.
        if (entrees is null || entrees.Count == 0) return TriState.Unknown;
        if (entrees.Any(e => e.StartsWith("?:", StringComparison.Ordinal))) return TriState.Off;
        if (!entrees.All(EntreeGereeParLeSysteme)) return TriState.Unknown;
        if (!typesParLettre.TryGetValue(char.ToUpperInvariant(lettreSysteme), out var systeme)) return TriState.Unknown;
        if (systeme != JeuxSurDisqueDur.Ssd) return TriState.Off;
        var types = entrees.Select(e => typesParLettre.TryGetValue(char.ToUpperInvariant(e[0]), out var t) ? t : 0).ToList();
        if (types.Contains(JeuxSurDisqueDur.DisqueDur)) return TriState.On;
        return types.All(t => t == JeuxSurDisqueDur.Ssd) ? TriState.Off : TriState.Unknown;
    }
}
