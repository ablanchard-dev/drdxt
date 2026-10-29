namespace DrDxt.GpuNative.NvApi;

// -----------------------------------------------------------------------
// INvApiNative : la frontière managée mince sur la surface native de NVAPI.
//
// Le seul accès à nvapi64.dll : l'entrée (QueryInterface, Initialize, EnumPhysicalGPUs), les ids de fonction, les
// structs et la recette GET puis SET, validés en réel, vivent dans NvApiNative, et le lecteur (Detection) comme les
// écritures élevées (NvApiTuningActions, NvApiDrsActions) passent par ici : aucune seconde copie. Comme IAdlxNative,
// remplacée par un faux en test. Contrat d'échec : IsAvailable faux (DLL absente, Initialize raté) ⇒ chaque Try* rend
// false ; aucun Try* ne lève ; sur false, les out sont à leur défaut.
// -----------------------------------------------------------------------

/// <summary>Frontière mince sur NVAPI natif. Ne lève jamais : rend false sur tout échec (DLL
/// absente, Initialize raté, id de fonction privée manquant, panne du pilote, etc.).</summary>
public interface INvApiNative
{
    /// <summary>Vrai quand NvAPI_Initialize a réussi (GPU NVIDIA + pilote présents).</summary>
    bool IsAvailable { get; }

    // ── Lectures (non élevées ; NvAPI_Initialize + GET marchent sans élévation) ───────

    /// <summary>Lit le décalage d'horloge cœur P0/Graphics appliqué (MHz) du GPU d'index
    /// <paramref name="gpuIndex"/> (GetPstates20 → delta graphique P0 → kHz→MHz).
    /// Rend false sur un mauvais index, sans GPU NVIDIA, ou sur une panne du pilote.</summary>
    bool TryReadCoreOffsetMhz(int gpuIndex, out int offsetMhz);

    /// <summary>Lit le décalage d'horloge P0/Memory appliqué (MHz) (GetPstates20, même GET que le cœur, sans écriture) :
    /// le retour fidèle d'un undervolt a besoin des deux décalages, jamais d'un 0 fabriqué. Rend false sur un mauvais
    /// index, sans GPU NVIDIA, sans domaine mémoire, ou sur une panne du pilote.</summary>
    bool TryReadMemoryOffsetMhz(int gpuIndex, out int offsetMhz);

    /// <summary>Lit la limite de puissance appliquée (% du TDP) du GPU d'index
    /// <paramref name="gpuIndex"/> (ClientPowerPoliciesGetStatus → entry[0] PCM→%).
    /// Rend false sur un mauvais index, sans GPU NVIDIA, ou sur une panne du pilote.</summary>
    bool TryReadPowerLimitPct(int gpuIndex, out int powerLimitPct);

    // ── Écritures (chemin élevé seulement ; jamais appelées pendant dotnet test) ──────────────

    /// <summary>
    /// GET puis SET : lit toute la struct Pstates20 actuelle, trouve l'entrée d'horloge P0/Graphics,
    /// vérifie qu'elle est modifiable, puis soumet une écriture minimale qui ne porte que ce seul delta
    /// d'horloge (SetPstates20) : jamais une struct construite à zéro, jamais une modification de
    /// tension. <paramref name="offsetMhz"/> est le delta appliqué absolu (MHz), converti en kHz pour le
    /// pilote. Rend false si indisponible, si l'id privé n'est pas exporté, si l'horloge n'est pas
    /// modifiable, ou sur toute panne du pilote. Ne lève jamais.
    /// </summary>
    bool TrySetCoreClockOffset(int gpuIndex, int offsetMhz);

    /// <summary>
    /// Le miroir de <see cref="TrySetCoreClockOffset"/> pour P0/Memory (PublicClockDomain MEMORY = 4, épinglé par le
    /// test de gel du chercheur), même GET puis SET minimal, jamais de tension. Rend false si indisponible, si l'entrée
    /// n'est pas modifiable, ou sur toute panne du pilote. Ne lève jamais.
    /// </summary>
    bool TrySetMemoryClockOffset(int gpuIndex, int offsetMhz);

    /// <summary>
    /// GET puis SET : lit les bornes PCM [min, max] du pilote (ClientPowerPoliciesGetInfo), borne le
    /// pourcentage demandé dans cette plage, lit l'état actuel, ne modifie sur place que la cible de
    /// puissance d'entry[0], puis le réécrit (ClientPowerPoliciesSetStatus).
    /// Rend false si indisponible, si les bornes sont invalides, ou sur toute panne du pilote. Ne lève jamais.
    /// </summary>
    bool TrySetPowerLimitPercent(int gpuIndex, float pct);

    // ── DRS : l'écriture publique et sûre d'un réglage du pilote (partage l'entrée NVAPI) ───────

    /// <summary>
    /// Applique un seul réglage global du pilote NVIDIA (DWORD) par la séquence de session DRS :
    /// CreateSession → LoadSettings → GetBaseProfile → SetSetting → SaveSettings →
    /// DestroySession (DestroySession tourne toujours). <paramref name="settingId"/> est déjà validé
    /// par l'allowlist chez l'appelant. Rend false si NVAPI est indisponible ou si une étape de la
    /// séquence de session échoue. Ne lève jamais.
    /// </summary>
    bool TrySetDrsGlobalSetting(uint settingId, uint value);

    /// <summary>
    /// Lit un seul réglage global du pilote NVIDIA (DWORD) par la séquence de session DRS
    /// (CreateSession → LoadSettings → GetBaseProfile → GetSetting → DestroySession). Non élevé (une
    /// simple lecture). Rend false quand NVAPI est indisponible, que le réglage n'est pas dans le
    /// profil de base (NVAPI_SETTING_NOT_FOUND : la valeur est au défaut du pilote, jamais un 0
    /// fabriqué), ou qu'une étape tombe en panne. Ne lève jamais.
    /// </summary>
    bool TryGetDrsGlobalSetting(uint settingId, out uint value);

    /// <summary>
    /// La même lecture, en gardant les deux champs que NVIDIA remplit à chaque lecture DRS et que la
    /// surcharge simple jette : <c>settingLocation</c> et <c>isCurrentPredefined</c>. Sans eux, une
    /// valeur livrée par le pilote ne se distingue pas d'une valeur choisie par un humain ; sur une
    /// machine qui porte des milliers de profils d'application prédéfinis, « qu'ai-je vraiment
    /// changé ? » devient sans réponse. Même contrat que la surcharge simple : false si indisponible /
    /// absent / en panne, ne lève jamais, jamais un 0 fabriqué.
    /// </summary>
    bool TryReadDrsGlobalSetting(uint settingId, out DrsReading reading);

    /// <summary>Vrai seulement quand le réglage est absent du profil de base (NVAPI_SETTING_NOT_FOUND) : il est au défaut du
    /// pilote. Une panne de lecture rend faux. Par défaut : faux, le cas prudent.</summary>
    bool DrsGlobalAuDefaut(uint settingId) => false;

    /// <summary>
    /// Lit un réglage dans le profil que le pilote applique vraiment à un exécutable
    /// (CreateSession -> LoadSettings -> FindApplicationByName -> GetSetting -> DestroySession).
    /// La hiérarchie du pilote est Application &gt; Global courant &gt; Base (PG-12072-001 p.2) :
    /// la lecture du seul profil de base peut rapporter une valeur que le jeu ne voit jamais.
    ///
    /// <paramref name="applicationPath"/> doit être le chemin complet : NVIDIA rend
    /// NVAPI_EXECUTABLE_PATH_IS_AMBIGUOUS pour un nom de fichier nu. Quand l'exécutable n'a pas de
    /// profil à lui, le pilote rend NVAPI_EXECUTABLE_NOT_FOUND et il n'y a aucun repli automatique sur
    /// le profil de base : la méthode rend false, et l'appelant ne doit pas y substituer la valeur
    /// globale, qui répondrait à une question que personne n'a posée. Ne lève jamais.
    /// </summary>
    bool TryReadDrsSettingForApplication(string applicationPath, uint settingId, out DrsReading reading);

    /// <summary>Ce que la lecture au profil d'un jeu a vraiment rencontre.</summary>
    enum LectureAuJeu { Lu, PasDeProfil, Illisible }

    /// <summary>La meme lecture, qui separe « cet executable n'a pas de profil » (le pilote
    /// lui applique le global) de « le profil existe mais ne se lit pas ». Les confondre laisserait
    /// ecrire au global pendant que le pilote applique au jeu un profil illisible.
    /// Par defaut : tout echec est Illisible, le cas prudent, jamais un repli devine.</summary>
    LectureAuJeu LireDrsAuJeu(string applicationPath, uint settingId, out DrsReading reading) =>
        TryReadDrsSettingForApplication(applicationPath, settingId, out reading)
            ? LectureAuJeu.Lu
            : LectureAuJeu.Illisible;

    /// <summary>Supprime un reglage du profil de cet executable
    /// (NvAPI_DRS_DeleteProfileSetting : « deletes a setting or sets it back to predefined
    /// value »). Eleve. Meme sequence que l'ecriture par application ; aucun profil n'est cree,
    /// aucun repli sur le profil de base. false sur toute panne. Par defaut : false ; une frontiere
    /// qui ne sait pas supprimer le dit, elle ne fait pas semblant.</summary>
    bool TryDeleteDrsSettingForApplication(string applicationPath, uint settingId) => false;

    /// <summary>Le VRR (G-Sync) de l'ecran nomme Win32 (`\\.\DISPLAY1`), lu au pilote.
    /// Lecture, non elevee. false = non lu (pas de NVIDIA, ecran pilote par une autre carte, panne) :
    /// jamais « pas de VRR ». Par defaut : false.</summary>
    bool TryReadVrr(string displayName, out VrrReading reading)
    {
        reading = default;
        return false;
    }

    /// <summary>
    /// Ecrit un reglage dans le profil de l'executable, pas dans le profil de base
    /// (CreateSession -> LoadSettings -> FindApplicationByName -> SetSetting -> SaveSettings ->
    /// DestroySession). C'est la moitie ecriture de la lecture par application ci-dessus : la
    /// hierarchie du driver est Application &gt; Global courant &gt; Base (PG-12072-001 p.2), donc
    /// une ecriture globale peut ne jamais atteindre le jeu qui a son propre profil.
    ///
    /// <paramref name="applicationPath"/> doit etre le chemin complet : NVIDIA rend
    /// NVAPI_EXECUTABLE_PATH_IS_AMBIGUOUS sur un nom nu.
    ///
    /// Ne cree jamais de profil (NVAPI_EXECUTABLE_NOT_FOUND ⇒ false) : ce serait une entree permanente dans la base du
    /// driver, et un revert qui devrait supprimer un profil, pas seulement restaurer une valeur.
    ///
    /// NVIDIA documente que la sauvegarde d'une session DRS ecrase ce qu'un autre processus a
    /// modifie entre-temps, sans notification (PG-12072-001 p.11). La session est donc la plus
    /// courte possible : rien entre LoadSettings et SaveSettings que la seule ecriture.
    /// Ne jette jamais.
    /// </summary>
    bool TrySetDrsSettingForApplication(string applicationPath, uint settingId, uint value);
}

/// <summary>Une lecture DRS, avec les champs de provenance gardés.</summary>
/// <param name="Value">Le DWORD en vigueur pour le profil lu.</param>
/// <param name="SettingLocation">NVDRS_SETTING_LOCATION : 0 profil courant, 1 global, 2 base,
/// 3 défaut du pilote (PG-12072-001).</param>
/// <param name="IsCurrentPredefined">Non nul quand la valeur actuelle est celle que livre NVIDIA.</param>
/// <param name="IsPredefinedValid">Si la valeur livrée elle-même a pu être lue.</param>
public readonly record struct DrsReading(
    uint Value, uint SettingLocation, uint IsCurrentPredefined, uint IsPredefinedValid);
