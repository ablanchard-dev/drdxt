using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// Le temps de réveil que le pilote de chaque périphérique d'entrée annonce pour revenir de veille (D1, D2, D3) à l'état
/// actif. Un appareil qui annonce un réveil long et qui s'endort pendant une pause répond en retard au premier geste. Dit,
/// jamais réglé : DrDXT ne touche pas à la mise en veille des appareils USB. Lecture seule.
/// </summary>
/// <remarks>
/// DEVPKEY_Device_PowerData (devpkey.h) rend CM_POWER_DATA (winnt.h, Windows SDK 10.0.26100.0) : PD_Size, état récent,
/// PD_Capabilities (PDCAP_D1_SUPPORTED 0x2, D2 0x4, D3 0x8), puis PD_D1Latency, PD_D2Latency, PD_D3Latency, en unités de
/// 100 µs (Microsoft Learn, DEVICE_CAPABILITIES, auquel CM_POWER_DATA renvoie : « Set to zero if the device does not
/// support the D state »). Relevé sur la machine de référence le 16/10/2026 : cinq appareils, D3 pris en charge, toutes
/// les latences à 0, donc non annoncées.
/// </remarks>
public static class ReveilDesPeripheriques
{
    public sealed record Releve(string Nom, uint Capacites, uint D1, uint D2, uint D3);

    /// <summary>Pur : le pire réveil annoncé en millisecondes ; null si aucun n'est annoncé ; NaN si une latence est donnée
    /// pour un état que l'appareil dit ne pas prendre en charge (valeur incohérente : on ne devine pas l'unité).</summary>
    public static double? PireReveilMs(Releve r)
    {
        bool Incoherent(uint latence, uint bit) => latence > 0 && (r.Capacites & bit) == 0;
        if (Incoherent(r.D1, 0x2) || Incoherent(r.D2, 0x4) || Incoherent(r.D3, 0x8)) return double.NaN;
        var pire = Math.Max(r.D1, Math.Max(r.D2, r.D3));
        return pire == 0 ? null : pire / 10.0;
    }

    public static Releve? Decoder(string nom, byte[]? donnees)
        => donnees is { Length: >= 24 } b && BitConverter.ToUInt32(b, 0) >= 24
            ? new Releve(nom, BitConverter.ToUInt32(b, 8), BitConverter.ToUInt32(b, 12), BitConverter.ToUInt32(b, 16), BitConverter.ToUInt32(b, 20))
            : null;

    public static RepairFinding Juger(IReadOnlyList<(string Nom, Releve? Releve)>? lus)
    {
        if (lus is null || lus.Count == 0)
            return new RepairFinding("PERIPHERIQUES", "reveil des appareils d'entree", null, null, RepairStatus.Unknown, Docteur.T("RV.NonLu"));
        var annonces = lus.Where(l => l.Releve is not null).Select(l => (l.Nom, Ms: PireReveilMs(l.Releve!))).ToList();
        var incoherents = annonces.Where(a => a.Ms is double d && double.IsNaN(d)).Select(a => a.Nom).ToList();
        var valables = annonces.Where(a => a.Ms is double d && !double.IsNaN(d)).OrderByDescending(a => a.Ms).ToList();
        var texte = valables.Count == 0
            ? Docteur.T("RV.AucunAnnonce", lus.Count)
            : Docteur.T("RV.Pire", valables[0].Nom, valables[0].Ms!.Value, valables.Count);
        if (incoherents.Count > 0) texte += Docteur.T("RV.Incoherent", string.Join(", ", incoherents));
        return new RepairFinding("PERIPHERIQUES", "reveil des appareils d'entree",
            valables.Count == 0 ? Docteur.T("RV.NonAnnonce") : Docteur.T("RV.Valeur", valables[0].Ms!.Value), null, RepairStatus.Observed,
            texte + Docteur.T("RV.Jamais"));
    }

    public static RepairFinding LireEtJuger(IReadOnlyList<PeripheriquesDEntree.Appareil>? appareils)
        => Juger(appareils?.Select(a => (a.Nom, Decoder(a.Nom, ArbreDesPeripheriques.Binaire(a.Noeud, ArbreDesPeripheriques.DonneesAlimentation)))).ToList());
}
