using System.Globalization;

namespace DrDxt.Core;

/// <summary>Le Curve Optimizer et l'échelle PBO tels qu'ils sont réglés. <paramref name="DecalageTousCoeurs"/> : en pas
/// du menu, négatif pour une baisse de tension ; null en mode cœur par cœur.</summary>
public sealed record CurveOptimizerLu(bool Actif, string Mode, int? DecalageTousCoeurs, double? EchellePbo);

/// <summary>
/// Lit le Curve Optimizer et l'échelle PBO d'un Ryzen, pour ne jamais conseiller d'aller plus loin sur une puce déjà
/// poussée. La source est le BIOS nommé (<see cref="TablesBiosAmd"/>) : mode, signe et amplitude de la copie que le
/// menu montre. Le SMU ne sert que de témoin (commandes de lecture de <see cref="SmuAmd"/>) : s'il contredit le BIOS,
/// rien n'est lu. Aucune borne d'amplitude n'est imposée ici : les valeurs possibles sont celles du menu de la carte, et
/// Zen 4 va plus loin que Zen 3. Pur, lecture seule.
/// </summary>
public static class LectureCurveOptimizer
{
    /// <summary>Pur : null quand le BIOS ne nomme pas le Curve Optimizer, ou qu'un mode « tous les cœurs » n'a pas son
    /// signe et son amplitude.</summary>
    public static CurveOptimizerLu? DepuisBios(IReadOnlyList<BiosSettingReading>? bios)
    {
        if (bios?.FirstOrDefault(r => r.Setting == "Curve Optimizer") is not { } mode) return null;
        var echelle = bios.FirstOrDefault(r => r.Setting == "PBO Scalar") is { } s ? Echelle(s.Libelle) : null;
        if (mode.Libelle is "Disabled" or "Auto") return new CurveOptimizerLu(false, mode.Libelle, 0, echelle);
        if (mode.Libelle != "All Cores") return new CurveOptimizerLu(true, mode.Libelle, null, echelle);
        var signe = bios.FirstOrDefault(r => r.Setting == "Curve Optimizer Sign")?.Libelle;
        if (signe is not ("Positive" or "Negative") || bios.FirstOrDefault(r => r.Setting == "Curve Optimizer Magnitude") is not { } m)
            return null;
        var decalage = (int)m.Raw * (signe == "Negative" ? -1 : 1);
        return new CurveOptimizerLu(decalage != 0, mode.Libelle, decalage, echelle);
    }

    // « 4X », « 10x » : l'échelle telle que le menu l'écrit ; « Auto » et le reste ne sont pas un nombre.
    private static double? Echelle(string libelle) =>
        libelle.Length > 1 && char.ToUpperInvariant(libelle[^1]) == 'X'
        && double.TryParse(libelle[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : null;

    /// <summary>Pur : l'échelle PBO rendue par la commande GetPBOScalar, un flottant dans le premier argument
    /// (ZenStates-Core 869b7e82, GetPBOScalar.cs) ; null hors de ]0 ; 10], les bornes de cette même source.</summary>
    public static double? EchelleSmu(uint argument)
    {
        var x = BitConverter.Int32BitsToSingle(unchecked((int)argument));
        return float.IsFinite(x) && x > 0 && x <= 10 ? x : null;
    }

    /// <summary>Pur : le BIOS, confirmé par le témoin SMU quand il a été lu. Une contradiction rend null (non lu).
    /// <paramref name="margesParCoeur"/> : les marges lues cœur par cœur (GetDldoPsmMargin), en pas signés.</summary>
    public static CurveOptimizerLu? Croiser(CurveOptimizerLu? bios, double? echelleSmu, IReadOnlyList<int>? margesParCoeur)
    {
        if (bios is null) return null;
        if (echelleSmu is { } e && bios.EchellePbo is { } b && Math.Abs(e - b) > 0.01) return null;
        if (margesParCoeur is { Count: > 0 } marges)
        {
            if (!bios.Actif && marges.Any(x => x != 0)) return null;
            if (bios.DecalageTousCoeurs is { } d && marges.Any(x => x != d)) return null;
        }
        return bios;
    }

    /// <summary>Pur : le réglage tel qu'il se dit (« All Cores -20 », « Per Core »).</summary>
    public static string Texte(CurveOptimizerLu lu) =>
        lu.DecalageTousCoeurs is { } d && lu.Mode == "All Cores" ? $"{lu.Mode} {d}" : lu.Mode;
}
