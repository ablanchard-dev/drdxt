﻿using System.Text.RegularExpressions;

namespace DrDxt.Core;

/// <summary>
/// La carte qui rend le jeu, et la seule règle de « carte dédiée » du produit. Windows liste souvent la puce intégrée en
/// premier : la première carte de l'inventaire n'est pas celle du joueur. Une carte dédiée a sa propre mémoire vidéo ; une
/// puce intégrée partage la mémoire système et DXGI lui donne ~128 Mo de mémoire dédiée, d'où le seuil de 1 Gio (même seuil
/// que la lecture de la carte du jeu). La mémoire seule ne suffit pas : une APU AMD réglée avec une réserve UMA de 2 Go rend
/// 2 Go de mémoire dédiée. Le nom tranche alors.
/// </summary>
public static class CartesGraphiques
{
    public const long MemoireDedieeMin = 1L << 30;

    // Une Intel dédiée est une Arc A ou B suivie de son numéro (A770, B580) ; « Arc(TM) Graphics » est la puce intégrée des
    // processeurs Core Ultra.
    private static readonly Regex ArcDediee = new(@"Arc(\(TM\))?\s+[AB]\d{3}", RegexOptions.IgnoreCase);

    // Une puce intégrée AMD n'a rien entre « Radeon » et la fin du nom sinon « Vega N », « RX Vega N » ou « NNNM », suivis ou
    // non de « Graphics » : Radeon(TM) Graphics, Radeon 780M Graphics, Radeon(TM) RX Vega 11 Graphics (Ryzen 2400G), Radeon(TM)
    // 610M. Une carte dédiée a son numéro après RX ou Pro : RX 7800 XT, RX 6800M, Pro W7600, « Radeon (TM) RX 480 Graphics ».
    private static readonly Regex AmdIntegree = new(@"Radeon\s*(\(TM\))?\s+((RX\s+)?Vega\s+\d+\s*|\d{3,4}M\s*)?(Graphics)?\s*$",
        RegexOptions.IgnoreCase);

    // « ATI » comme mot : la sous-chaîne est dans « NVIDIA Corporation » et « Intel Corporation ».
    private static readonly Regex MotAti = new(@"\bATI\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Vrai pour une chaîne constructeur AMD, processeur ou carte : WMI dit « AuthenticAMD » pour les processeurs,
    /// « Advanced Micro Devices » ou « ATI » pour les cartes.</summary>
    public static bool EstAmd(string? constructeur) =>
        !string.IsNullOrWhiteSpace(constructeur)
        && (constructeur.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || constructeur.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)
            || MotAti.IsMatch(constructeur));

    public static bool EstDediee(GpuInfo carte) => carte.VramBytes >= MemoireDedieeMin && !IntegreeAuNom(carte);

    /// <summary>La première carte dédiée, ou <c>null</c> sur une puce intégrée seule.</summary>
    public static GpuInfo? CarteDediee(HardwareInventory hw) => hw.Gpus.FirstOrDefault(EstDediee);

    private static bool IntegreeAuNom(GpuInfo c)
    {
        if (c.Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return !ArcDediee.IsMatch(c.Model);
        return EstAmd(c.Vendor) && AmdIntegree.IsMatch(c.Model);
    }
}
