using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

/// <summary>
/// Une vitesse fixe du mode Expert ne suit pas la chaleur : à 45 % fixes, une carte qui monte à 90 °C ne refroidit plus comme
/// sa propre courbe le voudrait. Tant qu'un bail de ventilateur est tenu, la surveillance des baux du helper lit la
/// température (NVML) : dès <see cref="SeuilC"/> °C, ou si elle ne se lit pas, le ventilateur rend la main à la courbe de la
/// carte (le retour enregistré du bail). Aucune reprise de la vitesse fixe ensuite : le joueur la repose s'il le veut.
/// </summary>
public static class GardeThermiqueVentilateur
{
    /// <summary>Le seuil de la promesse « jamais une vitesse fixe au-dessus de 80 °C » (même seuil que le plancher à chaud de
    /// la liste blanche des courbes).</summary>
    public const uint SeuilC = 80;

    /// <param name="inverse">Le retour enregistré d'un bail.</param>
    /// <param name="temperature">La température de la carte d'index donné, en °C ; null si elle ne se lit pas.</param>
    public static bool DoitRendre(PrivilegedOp inverse, Func<int, uint?> temperature)
        => inverse is RestoreGpuFanAutoOp r && (temperature(r.GpuIndex) is not { } c || c >= SeuilC);
}
