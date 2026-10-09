namespace DrDxt.Core;

/// <summary>
/// Le plafond d'images qui garde un écran G-SYNC / FreeSync dans sa plage variable, sans retomber sur la latence de la Vsync.
///
/// 09/10/2026 (audit des gains) : « fréquence − 3 » est le MINIMUM donné par Blur Busters (« at least 3 FPS below »), pas
/// une cible. Aux hautes fréquences, les variations de temps d'image retouchent le plafond de la Vsync avec une marge de 3 :
/// NVIDIA Reflex, mesuré par Blur Busters, plafonne à ~138 sur 144 Hz et ~224 sur 240 Hz, soit fréquence − fréquence²/3600.
/// La cible prend la plus basse des deux. Source : https://blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/14/
///
/// Un plafond déjà posé entre cette cible et fréquence − 3 (une ancienne version de DrDXT, ou la règle de Blur Busters) est
/// tenu pour bon : il n'est ni reproposé, ni dit « calculé sur un autre écran ».
/// </summary>
public static class PlafondVrr
{
    public static int Cible(int rafraichissementHz)
    {
        if (rafraichissementHz <= 3) return 1;
        var reflex = (int)Math.Floor(rafraichissementHz - rafraichissementHz * (double)rafraichissementHz / 3600.0);
        // La formule n'est mesurée que jusqu'à 240 Hz et redescend au-delà de 1 800 Hz : jamais sous la moitié de la fréquence.
        reflex = Math.Max(reflex, rafraichissementHz / 2);
        return Math.Max(1, Math.Min(rafraichissementHz - 3, reflex));
    }

    public static bool EstBasseLatence(int plafond, int rafraichissementHz)
        => rafraichissementHz > 3 && plafond >= Cible(rafraichissementHz) && plafond <= rafraichissementHz - 3;
}
