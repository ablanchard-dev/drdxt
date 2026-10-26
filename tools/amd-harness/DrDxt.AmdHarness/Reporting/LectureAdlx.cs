using System.Globalization;
using DrDxt.Detection.Gpu.Adlx;

namespace DrDxt.AmdHarness.Reporting;

/// <summary>Ce que « --lire » affiche : ce que l'ADLX lit sur la carte AMD dédiée, une ligne par réglage, par mesure et par
/// écran, pour qu'un testeur mette chaque valeur à côté de celle d'AMD Software. <see cref="Lire"/> lit par les seules
/// entrées de lecture du natif ; les fonctions Lignes* sont pures : elles mettent en forme une lecture déjà faite.</summary>
internal static class LectureAdlx
{
    /// <summary>Tout le bloc ADLX de « --lire », lu par les seules entrées de lecture de <paramref name="natif"/> (LireReglages,
    /// LireMetriques, LireFreeSync, TryReadPower) : aucun Set*, aucune remise à l'usine. null = DLL absente : aucun appel
    /// natif, chaque ligne dit « non lu ».</summary>
    public static IReadOnlyList<string> Lire(IAdlxNative? natif, bool dllPresente)
        => [.. Lignes(AdlxReglages.Lire(natif), dllPresente), .. LignesMetriques(LireMetriques(natif)),
            .. LignesFreeSync(LireFreeSync(natif)), .. LignesPuissance(LirePuissances(natif))];

    public static IReadOnlyList<string> Lignes(AdlxReglagesLus lus, bool dllPresente)
    {
        return
        [
            $"amdadlx64.dll dans System32 : {(dllPresente ? "présente" : "absente")}",
            lus.Raison is null ? "ADLX : carte dédiée trouvée" : $"ADLX : rien lu ({lus.Raison})",
            $"SAM (ReBAR)   : {Etat(lus.Sam)}",
            $"Anti-Lag      : {Etat(lus.AntiLag)}",
            $"FRTC          : {Etat(lus.FrtcActif)}{(lus.FrtcFps is { } f ? $", {f} images/s" : "")}",
            $"Chill         : {Etat(lus.ChillActif)}{(lus.ChillMaxFps is { } c ? $", {lus.ChillMinFps}-{c} images/s" : "")}",
            $"Limite images : {(lus.LimiteImages is { } l ? (l == 0 ? "aucune" : $"{l} images/s") : "non lue")}",
            $"Vsync         : {Vsync(lus.VsyncMode)}",
            // Enhanced Sync et les réglages Radeon : lus seulement, DrDXT ne les pose pas. Les noms d'AMD Software. AdlxNative
            // lit un Enhanced Sync non pris en charge éteint, comme FRTC et Chill : ici, « désactivé » couvre ce cas.
            $"Enhanced Sync : {Etat(lus.EnhancedSyncActif)}",
            $"Radeon Boost  : {Etat(lus.BoostActif)}",
            $"Image Sharpening : {Etat(lus.ImageSharpeningActif)}",
            $"RSR           : {Etat(lus.RsrActif)}",
            $"AFMF          : {Etat(lus.AfmfActif)}",
            $"Température   : {(lus.TemperatureC is { } t ? $"{t:0.0} °C" : "non lue")}",
        ];
    }

    /// <summary>Ce que la carte rend à l'instant (IADLXGPUMetrics), une ligne par grandeur avec son unité, pour HWiNFO ou la
    /// surcouche d'AMD Software. Une grandeur non lue le dit ; un zéro lu reste zéro (le Zero RPM arrête les ventilateurs).
    /// Le point décimal ne suit pas la langue de Windows, comme les lignes PawnIO.</summary>
    public static IReadOnlyList<string> LignesMetriques(AdlxMetriques m)
    {
        var c = CultureInfo.InvariantCulture;
        List<string> lignes = m.Raison is { } raison ? [$"Mesures ADLX  : rien lu ({raison})"] : [];
        lignes.Add($"Consommation  : {(m.PuissanceW is { } w ? w.ToString("0.0", c) + " W" : "non lue")}");
        lignes.Add($"Fréquence GPU : {(m.FrequenceMhz is { } f ? f.ToString(c) + " MHz" : "non lue")}");
        lignes.Add($"Point chaud   : {(m.PointChaudC is { } t ? t.ToString("0.0", c) + " °C" : "non lu")}");
        lignes.Add($"Ventilateur   : {(m.VentilateurTrMin is { } v ? v.ToString(c) + " tr/min" : "non lu")}");
        return lignes;
    }

    /// <summary>FreeSync de chaque écran branché sur la carte AMD : son numéro dans l'ordre d'ADLX (deux écrans du même
    /// modèle restent distincts), son nom EDID, la prise en charge puis l'état. Vide = rien lu (contrat de LireFreeSync),
    /// jamais « désactivé ».</summary>
    public static IReadOnlyList<string> LignesFreeSync(IReadOnlyList<AdlxEcranFreeSync> ecrans)
        => ecrans.Count == 0
            ? ["FreeSync      : non lu"]
            : ecrans.Select((e, i) => $"FreeSync (écran {i + 1}, {(string.IsNullOrWhiteSpace(e.Nom) ? "nom non lu" : e.Nom.Trim())}) : "
                                      + $"{PriseEnCharge(e.Supporte)}, {Etat(e.Actif)}").ToList();

    /// <summary>La limite de puissance de chaque carte lisible et sa plage (GetPowerLimitRange), telles qu'ADLX les rend.
    /// L'unité (% du TDP, ou décalage autour de 0) n'est relevée sur aucune vraie carte : c'est cette ligne, mise à côté
    /// d'AMD Software, qui la prouvera. gpu.power_limit n'est pas proposé sur AMD d'ici là.</summary>
    public static IReadOnlyList<string> LignesPuissance(IReadOnlyList<(int Index, AdlxPowerReads Lu)> cartes)
        => cartes.Count == 0
            ? ["Puissance     : non lue"]
            : cartes.Select(c => $"Puissance (carte {c.Index}) : limite {c.Lu.PowerLimitPct}, plage {c.Lu.PowerRange.Min} à "
                                 + $"{c.Lu.PowerRange.Max}, pas {c.Lu.PowerRange.Step}").ToList();

    /// <summary>La puissance de chaque index que le produit sonde, par les seules entrées Get* d'ADLX (TryReadPower) : rien
    /// n'est écrit. Sans pilote AMD : vide.</summary>
    public static IReadOnlyList<(int Index, AdlxPowerReads Lu)> LirePuissances()
        => LirePuissances(AdlxReglages.DllPresente() ? AdlxReglages.Partage : null);

    private static IReadOnlyList<(int Index, AdlxPowerReads Lu)> LirePuissances(IAdlxNative? adlx)
    {
        var puissances = new List<(int, AdlxPowerReads)>();
        if (adlx is not null)
            for (var i = 0; i < DrDxt.Application.Tweaks.Hardware.AdlxGpuProbe.MaxProbeIndices; i++)
                if (adlx.TryReadPower(i, out var lu)) puissances.Add((i, lu));
        return puissances;
    }

    // Le contrat d'IAdlxNative dit « ne lève jamais » ; une lecture qui lève quand même se dit non lue, comme
    // AdlxReglages.Lire le fait pour les réglages, au lieu d'arrêter tout le rapport.
    private static AdlxMetriques LireMetriques(IAdlxNative? natif)
    {
        if (natif is null) return AdlxMetriques.RienLu(AdlxReglages.DllAbsente);
        try { return natif.LireMetriques(); }
        catch (Exception ex) { return AdlxMetriques.RienLu($"Lecture ADLX interrompue : {ex.GetType().Name}."); }
    }

    private static IReadOnlyList<AdlxEcranFreeSync> LireFreeSync(IAdlxNative? natif)
    {
        if (natif is null) return [];
        try { return natif.LireFreeSync(); }
        catch (Exception) { return []; }
    }

    private static string Etat(bool? v) => v switch { true => "activé", false => "désactivé", null => "non lu" };

    private static string PriseEnCharge(bool? v) => v switch
    {
        true => "pris en charge",
        false => "non pris en charge",
        null => "prise en charge non lue",
    };

    // ADLX_WAIT_FOR_VERTICAL_REFRESH_MODE, les noms d'AMD Software.
    private static string Vsync(int? mode) => mode switch
    {
        0 => "toujours coupée",
        1 => "coupée sauf avis du jeu",
        2 => "activée sauf avis du jeu",
        3 => "toujours activée",
        null => "non lue",
        _ => $"mode {mode} inconnu",
    };
}
