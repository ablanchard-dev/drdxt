namespace DrDxt.Core;

/// <summary>
/// Les valeurs qu'un réglage DRS peut porter, d'après NvApiDriverSettings.h (github.com/NVIDIA/nvapi). Partagé par la liste
/// blanche du helper et les leviers : un inverse rejoue la valeur lue chez le joueur, donc la borne est l'énumération
/// documentée et rien de plus strict (sinon l'origine ne se rejouerait pas) ; un levier dont l'origine sort de cette
/// énumération n'écrit pas.
/// </summary>
public static class ValeursDrs
{
    /// <summary>Documentée, et au profil de base jamais un limiteur d'images sous 20 i/s (1 i/s est documenté, et rend toute
    /// la machine inutilisable en jeu). Un levier dont l'origine au profil de base y tombe n'écrit pas : son inverse serait
    /// refusé.</summary>
    public static bool Admise(uint id, uint valeur, bool auProfilDeBase)
        => Documentee(id, valeur) && !(auProfilDeBase && id == 0x10835002 && valeur is > 0 and < 20);

    public static bool Documentee(uint id, uint valeur) => id switch
    {
        // FRL_FPS : DISABLED = MIN = 0, MAX = 0x3FF.
        0x10835002 => valeur <= 0x3FF,
        // VSYNCMODE : PASSIVE, FORCEOFF, FORCEON, FLIPINTERVAL2, FLIPINTERVAL3, FLIPINTERVAL4, VIRTUAL.
        0x00A879CF => valeur is 0x60925292 or 0x08416747 or 0x47814940 or 0x32610244 or 0x71271021 or 0x13245256 or 0x18888888,
        // PREFERRED_PSTATE : MIN = 0, MAX = 5.
        0x1057EB71 => valeur <= 5,
        // PRERENDERLIMIT : MIN = 0, MAX = 0xFF.
        0x007BA09E => valeur <= 0xFF,
        // NGX_DLSS_SR_OVERRIDE : OFF = 0, ON = 1.
        0x10E41E01 => valeur <= 1,
        // NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION : OFF = 0, RENDER_PRESET_A à _O = 1 à 15, Latest = 0x00FFFFFF.
        0x10E41DF3 => valeur <= 15 || valeur == 0x00FFFFFF,
        _ => false,
    };
}
