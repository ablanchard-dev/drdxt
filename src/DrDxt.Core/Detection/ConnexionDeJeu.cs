namespace DrDxt.Core;

/// <summary>Une carte réseau active qui a une passerelle (donc qui peut porter le jeu vers internet). Revue F-43 :
/// <paramref name="Virtuelle"/> = un adaptateur de VPN, de machine virtuelle ou de partage de connexion d'un téléphone, que
/// Windows dit « Ethernet » ; <paramref name="PorteLaRoute"/> = celle par laquelle Windows envoie vers internet.</summary>
public sealed record CarteVersInternet(string Nom, bool SansFil, bool Virtuelle = false, bool PorteLaRoute = false);

/// <summary>NET1 (08/10/2026) : la connexion Wi-Fi telle que Windows la lit, sans la localisation. <paramref name="PhyType"/> =
/// DOT11_PHY_TYPE (windot11.h) ; <paramref name="QualitePct"/> = 0 à 100 (Microsoft : 0 ≈ −100 dBm, 100 ≈ −50 dBm) ;
/// <paramref name="FrequenceMhz"/> peut arriver en kHz (le champ est documenté en kHz malgré son nom).</summary>
public sealed record QualiteWifi(int PhyType, int QualitePct, int? RssiDbm, int? FrequenceMhz, int? Canal)
{
    private int? Mhz => FrequenceMhz is > 100_000 and var khz ? khz / 1000 : FrequenceMhz;

    public string? Bande => Mhz switch
    {
        >= 2400 and < 2500 => "2,4 GHz",
        >= 5150 and < 5925 => "5 GHz",
        >= 5925 and <= 7125 => "6 GHz",
        _ => null,
    };

    public string? Generation => PhyType switch { 7 => "Wi-Fi 4", 8 => "Wi-Fi 5", 10 => "Wi-Fi 6", 11 => "Wi-Fi 7", _ => null };

    public string Phrase()
    {
        var morceaux = new List<string> { RssiDbm is { } dbm ? $"signal {QualitePct} % ({dbm} dBm)" : $"signal {QualitePct} %" };
        if (Bande is { } b) morceaux.Add(Canal is { } c ? $"{b} canal {c}" : b);
        if (Generation is { } g) morceaux.Add(g);
        return string.Join(", ", morceaux);
    }
}

/// <summary>
/// 07/10/2026 (chantier D6) : le jeu passe-t-il par le Wi-Fi ? Source : Activision, « Improving lag and performance »
/// (support.activision.com, articles « improving-lag-and-performance-in-modern-warfare-4 ») : le Wi-Fi a une fiabilité et
/// un débit très variables et peut fortement dégrader le jeu en ligne ; le câble est recommandé. Lecture seule ; le gain
/// n'est pas chiffré (il dépend du logement, du routeur, des voisins).
/// </summary>
public static class ConnexionDeJeu
{
    public static RepairFinding Juger(IReadOnlyList<CarteVersInternet>? cartes, QualiteWifi? wifiLu = null)
    {
        const string Nom = "connexion de jeu";
        if (cartes is null)
            return new RepairFinding("RESEAU", Nom, null, null, RepairStatus.Unknown,
                "les cartes réseau n'ont pas pu être lues : DrDXT ne sait pas si tu joues en Wi-Fi ou au câble.");
        if (cartes.Count == 0)
            return new RepairFinding("RESEAU", Nom, "aucune sortie vers internet", null, RepairStatus.Observed,
                "aucune carte réseau active n'a de passerelle : pas de connexion vers internet au moment de la lecture.");
        // Revue F-43 : un VPN ou un partage de connexion se dit « Ethernet » ; seule une vraie carte tranche. Câble et
        // Wi-Fi tous deux reliés : celle qui porte la route vers internet décide, sinon rien n'est affirmé.
        var physiques = cartes.Where(c => !c.Virtuelle).ToList();
        var cables = physiques.Where(c => !c.SansFil).ToList();
        var sansFil = physiques.Where(c => c.SansFil).ToList();
        var cable = cables.Count > 0 && (sansFil.Count == 0 || cables.Any(c => c.PorteLaRoute));
        var wifi = sansFil.Count > 0 && (cables.Count == 0 || sansFil.Any(c => c.PorteLaRoute));
        if (cable == wifi)
            return new RepairFinding("RESEAU", Nom, null, null, RepairStatus.Unknown,
                $"DrDXT ne sait pas si le jeu passe par le câble ou par le Wi-Fi ({string.Join(", ", cartes.Select(c => c.Nom))}) : "
                + "la route vers internet ne passe par aucune des deux, ou par un VPN.");
        if (cable)
            return new RepairFinding("RESEAU", Nom, "câble", "câble", RepairStatus.ExpectedDefault,
                $"tu es relié par câble ({string.Join(", ", cables.Select(c => c.Nom))}) : c'est ce "
                + "que recommandent les éditeurs pour le jeu en ligne.");
        return new RepairFinding("RESEAU", Nom, "Wi-Fi", "câble", RepairStatus.Suspicious,
            $"tu joues en Wi-Fi ({string.Join(", ", sansFil.Select(c => c.Nom))}"
            + (wifiLu is { } q ? $" : {q.Phrase()}" : "") + "). Activision le dit : le Wi-Fi a une "
            + "fiabilité et un débit très variables et peut fortement dégrader le jeu en ligne (pertes, latence qui saute). "
            + "Un câble Ethernet jusqu'à la box règle le problème à la source ; à défaut, un adaptateur CPL. DrDXT ne change "
            + "rien sur le réseau.", RepairAction.Recommandation);
    }
}
