using DrDxt.Core;

namespace DrDxt.Measurement.Memory;

/// <summary>
/// ISLC (Wagnardsoft) vide la mémoire « en attente » de Windows quand la mémoire libre
/// tombe sous 1 024 Mo (son réglage par défaut). Ça n'aide que sur une machine où la mémoire libre s'effondre en partie,
/// en pratique 16 Go. DrDXT ne vide rien : il dit, mesure à l'appui, quand ce cas se produit pendant la partie mesurée,
/// et seulement s'il s'accompagne d'à-coups dans les images. Sans les deux, il ne dit rien.
/// </summary>
public static class MemoireEnPartie
{
    public const string Nom = "mémoire libre pendant la partie";

    /// <summary>Le seuil par défaut d'ISLC (« free memory is lower than 1024 MB »).</summary>
    public const ulong SeuilLibreOctets = 1024UL * 1024 * 1024;

    /// <summary>Une image est un à-coup quand elle dure plus du double de l'image médiane.</summary>
    public const double FacteurACoup = 2.0;

    /// <summary>Au moins trois à-coups : un seul peut venir de la scène.</summary>
    public const int ACoupsMinimum = 3;

    public static int ACoups(IReadOnlyList<double> imagesMs)
    {
        if (imagesMs.Count == 0) return 0;
        var triees = imagesMs.Order().ToList();
        var mediane = triees[triees.Count / 2];
        return imagesMs.Count(t => t > mediane * FacteurACoup);
    }

    /// <summary>Le constat, ou <c>null</c> quand il n'y a rien à dire (mémoire jamais basse, ou basse sans à-coups,
    /// ou pas mesurée).</summary>
    public static RepairFinding? Juger(ulong? libreMinOctets, IReadOnlyList<double>? imagesMs)
    {
        if (libreMinOctets is not { } libre || imagesMs is null || libre >= SeuilLibreOctets) return null;
        var acoups = ACoups(imagesMs);
        if (acoups < ACoupsMinimum) return null;
        var mo = libre / (1024 * 1024);
        return new RepairFinding(TestMemoireEnSession.Domaine, Nom, Docteur.T("ME.Valeur", mo, acoups), Docteur.T("ME.Attendu"),
            RepairStatus.Suspicious,
            Docteur.T("ME.Explication", mo, acoups));
    }
}
