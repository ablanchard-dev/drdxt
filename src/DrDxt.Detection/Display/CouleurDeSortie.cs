using DrDxt.Core;

namespace DrDxt.Detection.Display;

/// <summary>
/// Ce que la carte envoie à chaque écran actif : encodage (RVB ou YCbCr), bits par couleur, couleur avancée (HDR) ou non.
/// Écran par écran, jamais une seule lecture pour tous. Lecture seule.
/// </summary>
/// <remarks>
/// Windows SDK 10.0.26100, wingdi.h : DisplayConfigGetDeviceInfo avec DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO (9)
/// remplit DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO (32 octets) : en-tête de 20, champ de bits à 20 (bit 0 prise en charge,
/// bit 1 activée), DISPLAYCONFIG_COLOR_ENCODING à 24 (0 RVB, 1 YCbCr 4:4:4, 2 4:2:2, 3 4:2:0, 4 intensité),
/// bitsPerColorChannel à 28. Le débit du lien n'est pas comparé : les débits HDMI et DisplayPort ne sont pas publiés en
/// accès libre, donc un sous-échantillonnage est dit, sans dire s'il était évitable. Relevé sur la machine de référence le
/// 17/10/2026 : deux écrans en RVB 8 bits, sans couleur avancée.
/// </remarks>
public static class CouleurDeSortie
{
    public sealed record Sortie(string? Nom, uint Encodage, uint Bits, bool Avancee);

    private const int Taille = 32;

    /// <summary>Pur : le corps du paquet rempli par Windows.</summary>
    public static Sortie Decouper(byte[] paquet, string? nom)
        => new(nom, BitConverter.ToUInt32(paquet, 24), BitConverter.ToUInt32(paquet, 28), (BitConverter.ToUInt32(paquet, 20) & 0b10) != 0);

    /// <summary>Une sortie par chemin actif ; null = chemins non lus. Un écran qui ne répond pas est sauté.</summary>
    public static IReadOnlyList<Sortie>? Lire(IReadOnlyList<CheminsDAffichage.Chemin>? chemins)
    {
        if (chemins is null) return null;
        var sorties = new List<Sortie>();
        foreach (var c in chemins)
        {
            var p = CheminsDAffichage.Paquet(9, Taille, c.CibleCarte, c.CibleId);
            if (Natif.DisplayConfigGetDeviceInfo(p) == 0) sorties.Add(Decouper(p, c.Nom));
        }
        return sorties;
    }

    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<Sortie>? sorties)
    {
        if (sorties is null || sorties.Count == 0)
        {
            yield return new RepairFinding("DISPLAY", "sortie couleur", null, null, RepairStatus.Unknown, Docteur.T("COUL.NonLu"));
            yield break;
        }
        foreach (var s in sorties)
        {
            var encodage = s.Encodage switch
            {
                0 => Docteur.T("COUL.Rvb"), 1 => "YCbCr 4:4:4", 2 => "YCbCr 4:2:2", 3 => "YCbCr 4:2:0", _ => Docteur.T("COUL.Autre"),
            };
            var valeur = Docteur.T("COUL.Valeur", encodage, s.Bits, s.Avancee ? "HDR" : "SDR");
            var nom = Docteur.Nom("COUL.Nom", s.Nom ?? Docteur.T("TOPO.EcranSansNom"));
            // RVB ou 4:4:4 en 8 bits ou plus : toute l'information passe.
            yield return s.Encodage is 0 or 1 && s.Bits >= 8
                ? new RepairFinding("DISPLAY", nom, valeur, null, RepairStatus.ExpectedDefault, Docteur.T("COUL.Complete"))
                : new RepairFinding("DISPLAY", nom, valeur, null, RepairStatus.Observed,
                    Docteur.T(s.Encodage is 2 or 3 ? "COUL.SousEchantillonne" : "COUL.PeuDeBits", s.Bits));
        }
    }
}
