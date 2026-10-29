using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution.Adlx;

// -----------------------------------------------------------------------
// IADLXTuningActions : la couture d'écriture AMD ADLX risquée (miroir de
// INvApiTuningActions).
//
// L'undervolt est une MaxFreq absolue (l'appelant convertit le décalage prudent ; l'inverse porte l'original, donc le
// retour rend la MaxFreq exacte) ; la tension n'est jamais touchée (RDNA4 n'a plus de tension manuelle). Tout passe par
// GET puis SET dans AdlxNative, l'unique copie des emplacements de vtable.
//
// Défense en profondeur, sans confiance en l'appelant (une App compromise ne pousse rien hors règle) : la MaxFreq est
// refusée hors de la plage du pilote, jamais bornée (la plage prudente en décalage [-200,+100] vit dans GpuSafeBounds,
// côté App) ; la PowerLimit est refusée hors de la bande GpuSafeBounds (AdlxTuningMath). Échec propre
// (HELPER.OP_FAILED) sans AMD, sans ADLX ou sur panne : jamais un faux Ok.
// -----------------------------------------------------------------------

/// <summary>Couture de réglage AMD ADLX risquée : le décalage d'undervolt d'horloge max GFX +
/// l'écriture de limite de puissance (GET puis SET via AdlxNative), avec une nouvelle
/// vérification GpuSafeBounds côté helper. Échec propre sur une machine sans AMD, sans ADLX, sur
/// panne du pilote ou type non pris en charge : ne lève jamais, jamais de faux Ok.</summary>
public interface IADLXTuningActions
{
    /// <summary>Applique une valeur de réglage ADLX au GPU d'index <paramref name="gpuIndex"/>.
    /// <para><see cref="AdlxTuningKind.Undervolt"/> → <paramref name="intValue"/> est une
    /// cible absolue d'horloge max GFX (MHz) ; validée contre la plage matérielle annoncée par le
    /// pilote (rejetée si hors plage, jamais bornée) et écrite en MaxFreq seule (tension inchangée).
    /// La plage de règle prudente en décalage est imposée plus tôt, dans la couche App.</para>
    /// <para><see cref="AdlxTuningKind.PowerLimit"/> → <paramref name="intValue"/> est une
    /// limite de puissance dans l'unité du pilote (% absolu ou décalage autour de l'usine) ; revérifiée
    /// contre la bande sûre (rejetée si hors bande), validée contre la plage du pilote.</para>
    /// <para><see cref="AdlxTuningKind.AntiLag"/> → 0 ou 1, écrit sur la carte dédiée ; toute autre valeur est refusée.
    /// Tout autre type → échec propre (HELPER.OP_FAILED).</para>
    /// Ne lève jamais.</summary>
    Result SetTuning(int gpuIndex, AdlxTuningKind kind, int intValue);
}
