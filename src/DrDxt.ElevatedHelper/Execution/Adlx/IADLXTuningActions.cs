using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution.Adlx;

// -----------------------------------------------------------------------
// IADLXTuningActions : la couture d'écriture AMD ADLX risquée (miroir de
// INvApiTuningActions).
//
// C'est le pendant AMD de INvApiTuningActions : le chemin d'écriture élevé des deux réglages
// ADLX qu'applique DrDXT :
//   * Undervolt   : une cible absolue d'horloge max GFX (MHz). L'appelant change le décalage
//                   prudent de l'utilisateur en cible absolue (MaxFreq actuelle + décalage) et
//                   l'op inverse porte l'original absolu : application→retour rend la MaxFreq
//                   d'origine exacte (comme PowerLimit). Le GPU tourne plus frais / plus
//                   efficace ; la tension n'est jamais touchée (RDNA4 n'a plus de tension
//                   manuelle : renvoyer la tension actuelle fait sauter à AdlxNative toute
//                   l'écriture de tension, MaxFreq seule).
//   * PowerLimit  : une limite de puissance absolue (% du TDP).
//
// Les deux suivent la règle d'or GET puis SET (via AdlxNative) : lire l'état actuel, valider
// la cible contre la plage annoncée par le pilote lui-même, n'écrire que le seul bouton visé.
// La frontière ADLX native est l'unique copie vérifiée, dans DrDxt.GpuNative (aucun
// emplacement de vtable dupliqué).
//
// Défense en profondeur : l'écriture élevée impose elle-même les bornes, sans jamais faire
// confiance à son appelant :
//   * Undervolt porte une MaxFreq absolue : le helper la valide contre la plage matérielle
//     annoncée par le pilote et refuse (échec propre, jamais de bornage) tout ce qui en sort.
//     La plage de règle prudente en décalage [-200,+100] vit dans la couche App
//     (GpuSafeBounds) : une plage de décalage n'a pas de sens sur une valeur absolue.
//   * La plage en % de PowerLimit est absolue : elle reprend toujours GpuSafeBounds via
//     AdlxTuningMath et est refusée ici hors plage. Une op IPC brute (une future version de
//     l'App compromise) ne peut donc jamais pousser une valeur hors règle sur le vrai matériel.
//
// SetTuning rend un Result et ne lève jamais à travers l'IPC. Sur une machine sans AMD, sans
// ADLX, ou sur toute panne du pilote, il échoue proprement (HELPER.OP_FAILED), jamais un faux
// Ok. Anti-Lag (0 ou 1) s'écrit ici aussi ; tout autre type échoue proprement et honnêtement,
// jamais un succès placebo.
// -----------------------------------------------------------------------

/// <summary>Couture de réglage AMD ADLX risquée : le décalage d'undervolt d'horloge max GFX +
/// l'écriture de limite de puissance absolue (GET puis SET via AdlxNative), avec une nouvelle
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
    /// limite de puissance absolue (% du TDP) ; revérifiée contre la plage sûre (rejetée si hors
    /// plage), validée contre la plage du pilote.</para>
    /// <para><see cref="AdlxTuningKind.AntiLag"/> → 0 ou 1, écrit sur la carte dédiée ; toute autre valeur est refusée.
    /// Tout autre type → échec propre (HELPER.OP_FAILED).</para>
    /// Ne lève jamais.</summary>
    Result SetTuning(int gpuIndex, AdlxTuningKind kind, int intValue);
}
