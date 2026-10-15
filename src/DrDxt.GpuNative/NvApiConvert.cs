namespace DrDxt.GpuNative.NvApi;

// -----------------------------------------------------------------------
// NvApiConvert : l'arithmétique pure et testable dont se sert la recette de réglage NVAPI risquée
// pour exprimer les valeurs dans les unités du pilote. Elle vit dans la feuille, à côté de l'unique
// frontière P/Invoke (NvApiNative), parce qu'elle fait partie de la recette, pas de la règle de
// sécurité : les plages prudentes de décalage / de puissance restent dans le NvApiTuningMath du
// helper (repris de GpuSafeBounds) ; ce fichier ne fait que convertir.
//
//  * Le décalage d'horloge cœur s'exprime au pilote en kHz (recette §A) : +100 MHz = 100_000 kHz ;
//    le signe est gardé pour un décalage d'undervolt négatif.
//  * La limite de puissance s'exprime en pour cent mille (PCM, recette §B) : 100 % = 100_000.
//  * ClampPcm borne une cible PCM demandée au [min, max] annoncé par le pilote ; des bornes
//    inversées ou déchet rendent la valeur inchangée (l'appelant échoue alors proprement) plutôt
//    que de lever.
// -----------------------------------------------------------------------
internal static class NvApiConvert
{
    /// <summary>Décalage d'horloge cœur MHz → kHz (l'unité qu'attend SetPstates20). Signe gardé.</summary>
    internal static int MhzToKhz(int mhz) => mhz * 1000;

    /// <summary>Décalage d'horloge cœur kHz → MHz (troncature vers zéro).</summary>
    internal static int KhzToMhz(int khz) => khz / 1000;

    /// <summary>Limite de puissance en pour cent → pour cent mille (90 % → 90_000).</summary>
    internal static uint PercentToPcm(double percent) => (uint)Math.Round(percent * 1000.0);

    /// <summary>Limite de puissance en pour cent mille → pour cent (90_000 → 90).</summary>
    internal static int PcmToPercent(uint pcm) => (int)Math.Round(pcm / 1000.0);

    /// <summary>Borne une cible de puissance PCM au [min, max] du pilote. Des bornes inversées
    /// (max &lt; min) sont traitées comme invalides et la valeur est rendue inchangée pour que
    /// l'appelant puisse refuser l'écriture : ne lève jamais.</summary>
    internal static uint ClampPcm(uint value, uint min, uint max)
    {
        if (max < min) return value;
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
