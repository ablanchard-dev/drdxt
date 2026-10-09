using DrDxt.Core;

namespace DrDxt.Detection.Settings;

/// <summary>
/// Un écran déclare-t-il un rafraîchissement variable ? Lu dans son EDID, jamais déduit.
///
/// Pourquoi ce fichier existe : <c>SettingsState.VrrCapableDisplay</c> valait <c>Unknown</c>
/// depuis le début, avec le contrat écrit dans <c>HardwareContracts</c> — « stays Unknown until
/// display-capability detection is wired ». Deux leviers de latence en dépendent
/// (<c>latency.vrr_vsync_cap</c>, <c>latency.vrrFlipModelOn</c>) et se taisaient donc, y compris
/// sur un écran de jeu à 200 Hz qui déclare FreeSync.
///
/// Mécanisme : l'EDID que Windows conserve pour chaque écran. Lecture seule, <b>sans élévation
/// et sans NVAPI</b> — donc valable NVIDIA, AMD et Intel. Ça élargit la couverture matérielle au
/// lieu de la restreindre, contrairement à une voie pilote.
///
/// <b>Le descripteur « Monitor Range Limits » (0xFD) ne prouve rien.</b> Tous les écrans en
/// portent un : il décrit la plage de modes supportés, pas le VRR. Le Samsung 144 Hz de la
/// machine de référence en a un (50-144 Hz) et n'est pas VRR. S'en servir ferait dire « capable »
/// à n'importe quel écran rapide.
///
/// Ce qui fait foi, et rien d'autre — sources : la liste amd-gfx (AMD VSDB, versions et tailles :
/// ratatoskr.run/amd-gfx/2026/07/17291713 ; règle de validité HDMI Forum, drm/edid) et CTA-861 :
/// <list type="bullet">
///   <item>AMD VSDB, OUI <c>0x00001A</c> : FreeSync ;</item>
///   <item>HDMI Forum VSDB, OUI <c>0xC45DD8</c> : VRR HDMI 2.1, avec sa règle de validité.</item>
/// </list>
/// </summary>
public static class EdidVrrCapabilite
{
    private const int TailleBloc = 128;
    private const byte TagExtensionCta = 0x02;
    private const int TagVendorSpecific = 3;

    private static readonly byte[] EnteteEdid = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    /// <summary>
    /// <c>On</c> = un bloc VRR est déclaré. <c>Off</c> = l'EDID est lisible et n'en déclare
    /// aucun. <c>Unknown</c> = l'EDID est absent, tronqué ou n'en est pas un.
    ///
    /// La distinction Off / Unknown porte tout le sens. « Cet écran n'est pas VRR » est une
    /// Réponse, qui autorise le produit à se taire pour une bonne raison. « Je n'ai pas su lire »
    /// n'en est pas une, et rendre <c>Off</c> dans ce cas fermerait un levier sur une lecture
    /// ratée — le défaut que ce dépôt traque partout ailleurs.
    /// </summary>
    public static TriState Lire(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < TailleBloc || !edid[..8].SequenceEqual(EnteteEdid))
            return TriState.Unknown;

        var extensions = edid[126];
        for (var i = 1; i <= extensions; i++)
        {
            var debut = TailleBloc * i;
            if (debut + TailleBloc > edid.Length)
                break; // extension annoncee mais absente : on ne conclut rien dessus
            var bloc = edid.Slice(debut, TailleBloc);
            if (bloc[0] != TagExtensionCta)
                continue;

            if (DeclareDuVrr(bloc))
                return TriState.On;
        }

        return TriState.Off;
    }

    /// <summary>Parcourt les data blocks CTA-861 d'une extension et cherche un bloc VRR.</summary>
    private static bool DeclareDuVrr(ReadOnlySpan<byte> extension)
    {
        // L'octet 2 dit ou commencent les descripteurs detailles ; les data blocks vivent entre
        // l'octet 4 et la. Une valeur < 4 signifie « aucun data block » (et non « tout le bloc »).
        var finDesBlocs = extension[2];
        if (finDesBlocs < 4)
            return false;
        finDesBlocs = Math.Min(finDesBlocs, (byte)(TailleBloc - 1));

        var p = 4;
        while (p < finDesBlocs)
        {
            var tag = extension[p] >> 5;
            var longueur = extension[p] & 0x1F;
            if (longueur == 0 || p + 1 + longueur > finDesBlocs)
                break; // en-tete incoherent : on arrete, on n'invente pas la suite
            var charge = extension.Slice(p + 1, longueur);
            p += 1 + longueur;

            if (tag != TagVendorSpecific || charge.Length < 3)
                continue;

            // L'OUI est petit-boutiste dans l'EDID.
            var oui = charge[2] << 16 | charge[1] << 8 | charge[0];
            if (oui == 0x00001A)
                return true; // AMD VSDB : la presence du bloc EST la declaration FreeSync
            if (oui == 0xC45DD8 && VrrHdmiValide(charge))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Règle HDMI 2.1 : le VRR compte comme supporté quand <c>VRRmin</c> tient dans 1-48 et que
    /// <c>VRRmax</c> vaut 0 (déduit du mode vidéo) ou ≥ 100. Sans ce contrôle, n'importe quel
    /// écran HDMI 2.1 passerait capable du seul fait d'avoir le bloc.
    /// </summary>
    private static bool VrrHdmiValide(ReadOnlySpan<byte> charge)
    {
        if (charge.Length < 11)
            return false;
        var vrrMin = charge[9] & 0x3F;
        var vrrMax = charge[10] | (charge[9] & 0xC0) << 2;
        return vrrMin is >= 1 and <= 48 && (vrrMax == 0 || vrrMax >= 100);
    }
}
