namespace DrDxt.Core;

// ════════════════════════════════════════════════════════════════════════════
// Docteur BIOS : la boucle fermée (vérification après redémarrage). Logique seule.
//
// La boucle a deux moitiés, toutes deux marqueur OS + relecture (jamais d'écriture BIOS) :
//
//   1. Avant que l'utilisateur redémarre dans le BIOS, on enregistre un marqueur
//      PendingBiosChange (quels réglages firmware il va changer, l'état attendu pour
//      chacun, quand, et un id machine léger). IPendingBiosChangeStore possède ce
//      marqueur : écriture atomique, lecture qui échoue proprement (absent = rien en
//      attente, corrompu = un Result.Fail honnête, jamais d'exception ni de faux « en attente »).
//
//   2. Au retour suivant de Windows, IBiosLoopVerifier prend ce marqueur + le SettingsState
//      frais relu par le scan et compare, réglage par réglage : Took / DidNotTake /
//      Unreadable. Honnêteté : un réglage qui n'a pas pris, ou qu'on ne peut pas lire,
//      est dit tel, avec une carte de secours (réessayer un changement à la fois,
//      Clear-CMOS, Load Optimized Defaults). Jamais présenté comme un faux succès.
//      Si tout a pris, le marqueur est effacé. Pas de marqueur → rien à faire (idempotent).
//
// Ce fichier ne contient que des contrats (records + interfaces + la couture d'horloge).
// Le rendu (HTML/QR/PDF/URL signée), les inspecteurs en direct et IBiosAutomationProvider
// n'y sont pas, exprès.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Horloge UTC injectable de la boucle BIOS : l'horodatage du marqueur ne passe que par
/// cette couture, pour qu'un FakeClock rende une valeur fixe et vérifiée. DateTime.Now /
/// DateTimeOffset.UtcNow sont interdits ici. Une ligne locale au module, comme dans
/// le reste du code (le reconciler, le helper élevé et la licence ont chacun la leur
/// plutôt que de coupler les projets).
/// </summary>
public interface IBiosClock
{
    /// <summary>L'instant UTC courant.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>Horloge de production : la vraie heure UTC.</summary>
public sealed class SystemBiosClock : IBiosClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Identité machine injectable, légère et non secrète, de la boucle BIOS. Le vérificateur
/// ne lit l'id de la machine courante que par cette couture et le compare au
/// <see cref="PendingBiosChange.MachineId"/> du marqueur : un marqueur copié d'un autre PC
/// ne doit pas être honoré ici. Une ligne locale au module, comme dans le reste du
/// code (voir <see cref="IBiosClock"/>).
/// </summary>
public interface IBiosMachineId
{
    /// <summary>L'identité légère et non secrète de la machine courante.</summary>
    string Current { get; }
}

/// <summary>Identité de production : le nom de machine de l'OS (léger, non secret).</summary>
public sealed class SystemBiosMachineId : IBiosMachineId
{
    /// <inheritdoc />
    public string Current => Environment.MachineName;
}

/// <summary>
/// Un réglage firmware que l'utilisateur va changer, avec l'état attendu à la
/// relecture après le redémarrage. La boucle compare le <see cref="SettingsState"/>
/// frais à cette attente, réglage par réglage.
/// </summary>
/// <param name="Setting">Le réglage firmware changé.</param>
/// <param name="ExpectedState">
/// L'état attendu après le changement (par exemple <see cref="TriState.On"/> pour « activer
/// XMP/ReBAR »). <see cref="TriState.Unknown"/> n'est pas une attente valide (un changement
/// en attente doit nommer une cible concrète) : le store le rejette comme corrompu.
/// </param>
/// <param name="ObservedBefore">
/// La grandeur mesurée pour ce réglage à l'écriture du marqueur (pour
/// <see cref="FirmwareSettingKind.XmpExpo"/> : l'horloge mémoire configurée, en MHz).
/// <c>null</c> = aucune valeur relevée, et la boucle se replie sur la comparaison TriState.
///
/// Pourquoi : la question de la boucle n'est pas « XMP est-il actif » mais « qu'est-ce qui a
/// changé à ce redémarrage ». 2133 -> 3800 est un fait ; probablement-On -> probablement-Off est
/// un pari sur un pari, et sur un kit qui dépasse le plancher JEDEC dans les deux états l'inférence
/// ne bouge pas alors que la machine bouge. Facultatif et en dernier : les constructions à 2 arguments compilent toujours.
/// </param>
public record PendingSettingTarget(
    FirmwareSettingKind Setting,
    TriState ExpectedState,
    long? ObservedBefore = null);

/// <summary>
/// Le marqueur sur disque « un changement firmware attend un redémarrage », écrit avant que
/// l'utilisateur entre dans le BIOS et lu au retour de Windows. Écrit atomiquement ; lu sans
/// risque (absent = rien en attente ; corrompu = un échec honnête, jamais cru).
/// </summary>
/// <param name="Targets">
/// Le ou les réglages changés, avec leur état attendu après le changement. Une liste
/// vide n'est pas un vrai changement en attente : elle est rejetée comme corrompue.
/// </param>
/// <param name="CreatedUtc">Quand le marqueur a été écrit (par <see cref="IBiosClock"/>, jamais DateTime.Now).</param>
/// <param name="MachineId">
/// Une identité machine légère et non secrète (un marqueur copié sur un autre PC n'y est pas
/// appliqué à l'aveugle). Le vérificateur la compare à <see cref="IBiosMachineId.Current"/> ;
/// si elles diffèrent, le marqueur étranger n'est jamais honoré : traité comme « rien en
/// attente » et laissé intact (ni effacé, ni évalué, jamais compté comme succès ou échec
/// pour cette machine).
/// </param>
public record PendingBiosChange(
    IReadOnlyList<PendingSettingTarget> Targets,
    DateTimeOffset CreatedUtc,
    string MachineId);

/// <summary>
/// Verdict par réglage de la boucle fermée, honnête par construction.
/// </summary>
public enum BiosSettingVerdict
{
    /// <summary>La relecture fraîche correspond à l'état attendu : le changement a pris.</summary>
    Took,

    /// <summary>Le réglage est lisible mais ne correspond pas à l'attente : il n'a pas pris.</summary>
    DidNotTake,

    /// <summary>
    /// Le réglage ne se lit pas dans <see cref="SettingsState"/> (tous les réglages firmware
    /// ne sont pas lisibles depuis l'OS). On dit « pas pu vérifier », jamais un faux succès.
    /// </summary>
    Unreadable,
}

/// <summary>
/// Le résultat d'un réglage attendu après le redémarrage : ce qu'on attendait, ce qu'on a
/// observé (<see cref="TriState.Unknown"/> si illisible) et le verdict honnête.
/// </summary>
/// <param name="Setting">Le réglage firmware évalué.</param>
/// <param name="ExpectedState">L'état attendu (repris du marqueur).</param>
/// <param name="ObservedState">L'état fraîchement relu, ou <see cref="TriState.Unknown"/> s'il est illisible.</param>
/// <param name="Verdict">Took / DidNotTake / Unreadable, jamais un succès inventé.</param>
public record BiosSettingOutcome(
    FirmwareSettingKind Setting,
    TriState ExpectedState,
    TriState ObservedState,
    BiosSettingVerdict Verdict);

/// <summary>
/// Le résultat de la boucle, rendu au retour suivant de Windows. Soit chaque réglage
/// attendu a pris (succès, marqueur effacé), soit au moins un n'a pas pris ou n'a pas pu
/// être vérifié : la carte de secours s'affiche et le marqueur est gardé (la boucle
/// pourra retourner après la prochaine tentative). Jamais un faux « OK ».
/// </summary>
/// <param name="HadPendingChange">
/// Faux quand il n'y avait aucun marqueur (« rien en attente », idempotent) ;
/// vrai quand un vrai changement en attente a été évalué.
/// </param>
/// <param name="AllTook">
/// Vrai seulement si <see cref="HadPendingChange"/> et que chaque résultat est
/// <see cref="BiosSettingVerdict.Took"/>. Un seul DidNotTake ou Unreadable le rend
/// faux (on n'affirme pas un succès sur un réglage invérifiable).
/// </param>
/// <param name="Outcomes">Les résultats par réglage (vide s'il n'y avait rien en attente).</param>
/// <param name="ShowRecoveryCard">
/// Vrai quand quelque chose n'a pas pris ou n'a pas pu être vérifié → l'UI doit montrer la
/// carte de secours (<see cref="RecoveryCardKeys"/>). Jamais vrai sur un succès net.
/// </param>
/// <param name="RecoveryCardKeys">
/// Clés resx ordonnées de la carte de secours (réessayer un changement à la fois, Clear-CMOS,
/// Load Optimized Defaults…). Vide en cas de succès.
/// </param>
/// <param name="MarkerCleared">
/// Vrai quand le marqueur a été retiré (succès net). Faux quand il a été gardé (échec ou
/// illisible, la boucle continue à la tentative suivante) ou quand rien n'était en attente.
/// </param>
public record BiosLoopResult(
    bool HadPendingChange,
    bool AllTook,
    IReadOnlyList<BiosSettingOutcome> Outcomes,
    bool ShowRecoveryCard,
    IReadOnlyList<string> RecoveryCardKeys,
    bool MarkerCleared,
    DateTimeOffset? DepartUtc = null)
{
    /// <summary>Le résultat idempotent « pas de marqueur, rien en attente ».</summary>
    public static BiosLoopResult NothingPending { get; } = new(
        HadPendingChange: false,
        AllTook: false,
        Outcomes: Array.Empty<BiosSettingOutcome>(),
        ShowRecoveryCard: false,
        RecoveryCardKeys: Array.Empty<string>(),
        MarkerCleared: false);
}

/// <summary>
/// Possède le marqueur sur disque « changement firmware en attente de redémarrage ». Écrit
/// atomiquement (temporaire + déplacement, jamais un fichier à moitié écrit) et lit sans
/// risque : un marqueur absent donne <c>Ok(null)</c> « rien en attente » ; un marqueur
/// corrompu donne l'échec <see cref="ErrorCodes.BiosPendingChangeCorrupt"/> (jamais cru, jamais
/// d'exception). N'écrit jamais le BIOS : c'est un simple fichier de l'OS.
/// </summary>
public interface IPendingBiosChangeStore
{
    /// <summary>
    /// Enregistre <paramref name="change"/> comme marqueur en attente, atomiquement.
    /// L'horodatage doit déjà venir de <see cref="IBiosClock"/> (le store ne lit pas
    /// l'horloge). Échoue proprement via <see cref="Result"/> sur un changement invalide ou
    /// une panne d'E/S ; ne lève jamais.
    /// </summary>
    Result Save(PendingBiosChange change);

    /// <summary>
    /// Lit le marqueur en attente. <c>Ok(null)</c> s'il n'y en a pas (rien à faire) ;
    /// <c>Ok(change)</c> s'il y en a un valide ; <c>Fail</c> sur un marqueur corrompu ou
    /// une panne d'E/S. Ne lève jamais.
    /// </summary>
    Result<PendingBiosChange?> Load();

    /// <summary>
    /// Retire le marqueur s'il existe (succès net, ou renoncement de l'utilisateur).
    /// Marqueur absent → <c>Ok</c> (idempotent). Échoue proprement sur une panne d'E/S ; ne lève jamais.
    /// </summary>
    Result Clear();
}

/// <summary>
/// Le vérificateur de la boucle après redémarrage. Prend le marqueur (via le store) et le
/// <see cref="SettingsState"/> frais relu par le scan, et produit un <see cref="BiosLoopResult"/>
/// honnête : par réglage Took / DidNotTake / Unreadable. Sur un succès net il efface le
/// marqueur ; sur tout DidNotTake / Unreadable il le garde et émet la carte de secours,
/// jamais un faux succès. Pas de marqueur → « rien en attente », idempotent.
/// N'écrit jamais le BIOS.
/// </summary>
public interface IBiosLoopVerifier
{
    /// <summary>
    /// Vérifie le changement firmware en attente (s'il y en a un) contre <paramref name="freshState"/>.
    /// Échoue proprement via <see cref="Result"/> sur un marqueur corrompu ou une panne d'E/S ; ne lève jamais.
    /// </summary>
    Result<BiosLoopResult> Verify(SettingsState freshState);
}
