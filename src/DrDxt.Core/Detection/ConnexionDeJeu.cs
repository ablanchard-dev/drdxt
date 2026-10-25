namespace DrDxt.Core;

/// <summary>Une carte réseau active qui a une passerelle (donc qui peut porter le jeu vers internet).
/// <paramref name="Virtuelle"/> = un adaptateur de VPN, de machine virtuelle ou de partage de connexion d'un téléphone, que
/// Windows dit « Ethernet » ; <paramref name="PorteLaRoute"/> = celle par laquelle Windows envoie vers internet.</summary>
public sealed record CarteVersInternet(string Nom, bool SansFil, bool Virtuelle = false, bool PorteLaRoute = false);

/// <summary>La connexion Wi-Fi telle que Windows la lit, sans la localisation. <paramref name="PhyType"/> =
/// DOT11_PHY_TYPE (windot11.h) ; <paramref name="QualitePct"/> = 0 à 100 (Microsoft : 0 ≈ −100 dBm, 100 ≈ −50 dBm) ;
/// <paramref name="FrequenceMhz"/> peut arriver en kHz (le champ est documenté en kHz malgré son nom).</summary>
public sealed record QualiteWifi(int PhyType, int QualitePct, int? RssiDbm, int? FrequenceMhz, int? Canal)
{
    private int? Mhz => FrequenceMhz is > 100_000 and var khz ? khz / 1000 : FrequenceMhz;

    public string? Bande => Mhz switch
    {
        >= 2400 and < 2500 => Docteur.T("CJ.Bande24"),
        >= 5150 and < 5925 => "5 GHz",
        >= 5925 and <= 7125 => "6 GHz",
        _ => null,
    };

    /// <summary>Sous -67 dBm, le seuil que les guides de conception Wi-Fi pour la voix et le temps réel (Cisco, Ekahau)
    /// fixent comme minimum. Sans RSSI, la qualité de Windows, linéaire de -100 dBm (0 %) à -50 dBm (100 %), donne 66 %.</summary>
    public bool Faible => RssiDbm is { } dbm ? dbm < -67 : QualitePct < 66;

    public string? Generation => PhyType switch { 7 => "Wi-Fi 4", 8 => "Wi-Fi 5", 10 => "Wi-Fi 6", 11 => "Wi-Fi 7", _ => null };

    public string Phrase()
    {
        var morceaux = new List<string> { RssiDbm is { } dbm ? Docteur.T("CJ.SignalDbm", QualitePct, dbm) : Docteur.T("CJ.Signal", QualitePct) };
        if (Bande is { } b) morceaux.Add(Canal is { } c ? Docteur.T("CJ.Canal", b, c) : b);
        if (Generation is { } g) morceaux.Add(g);
        return string.Join(", ", morceaux);
    }
}

/// <summary>
/// Le jeu passe-t-il par le Wi-Fi ? Source : Activision, « Improving lag and performance »
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
                Docteur.T("CJ.NonLues"));
        if (cartes.Count == 0)
            return new RepairFinding("RESEAU", Nom, Docteur.T("CJ.AucuneSortie"), null, RepairStatus.Observed,
                Docteur.T("CJ.SansPasserelle"));
        // Un VPN ou un partage de connexion se dit « Ethernet » ; seule une vraie carte tranche. Câble et
        // Wi-Fi tous deux reliés : celle qui porte la route vers internet décide, sinon rien n'est affirmé.
        var physiques = cartes.Where(c => !c.Virtuelle).ToList();
        var cables = physiques.Where(c => !c.SansFil).ToList();
        var sansFil = physiques.Where(c => c.SansFil).ToList();
        var cable = cables.Count > 0 && (sansFil.Count == 0 || cables.Any(c => c.PorteLaRoute));
        var wifi = sansFil.Count > 0 && (cables.Count == 0 || sansFil.Any(c => c.PorteLaRoute));
        if (cable == wifi)
            return new RepairFinding("RESEAU", Nom, null, null, RepairStatus.Unknown,
                Docteur.T("CJ.Inconnu", string.Join(", ", cartes.Select(c => c.Nom))));
        if (cable)
            return new RepairFinding("RESEAU", Nom, Docteur.T("CJ.Cable"), Docteur.T("CJ.Cable"), RepairStatus.ExpectedDefault,
                Docteur.T("CJ.CableExplication", string.Join(", ", cables.Select(c => c.Nom))));
        // Le Wi-Fi est souvent le seul choix du logement : il n'est « à corriger » que sur un signal mesuré faible.
        return new RepairFinding("RESEAU", Nom, "Wi-Fi", Docteur.T("CJ.Cable"), wifiLu is { Faible: true } ? RepairStatus.Suspicious : RepairStatus.NonDefault,
            Docteur.T("CJ.Wifi", string.Join(", ", sansFil.Select(c => c.Nom)), wifiLu is { } q ? $"{Docteur.DeuxPoints}{q.Phrase()}" : ""),
            RepairAction.Recommandation);
    }
}
