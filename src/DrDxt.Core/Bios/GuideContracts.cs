namespace DrDxt.Core;

// ════════════════════════════════════════════════════════════════════════════
// Docteur BIOS : le générateur de guide (contenu). Pas le rendu.
//
// Le modèle du guide, la bibliothèque (IGuideLibrary : chemins de menu exacts par fabricant × génération d'UI, JSON
// validé) et le générateur (IGuideGenerator). Le rendu vit ailleurs. Carte couverte : chemin exact, Verified ; sinon
// un repli étiqueté « non vérifié pour ta carte exacte » qui porte toujours le nom exact du réglage, et Clear-CMOS.
// Sécurité : jamais « CSM off » en démarrage Legacy/MBR ; ReBAR dans l'ordre Above 4G → CSM OFF → Re-Size BAR ;
// secours en haut et en bas de chaque guide ; jamais d'écriture ni de script BIOS, seulement du texte.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Le fabricant du BIOS de la carte mère, clé du guide. Déduit de
/// <see cref="MotherboardInfo.Manufacturer"/> (sans tenir compte de la casse ni des espaces).
/// </summary>
public enum BiosVendor
{
    /// <summary>Fabricant inconnu → on prend le repli générique le plus prudent.</summary>
    Unknown,

    /// <summary>ASUS / ASUSTeK.</summary>
    Asus,

    /// <summary>MSI / Micro-Star.</summary>
    Msi,

    /// <summary>Gigabyte / Aorus.</summary>
    Gigabyte,

    /// <summary>ASRock.</summary>
    Asrock,
}

/// <summary>
/// Le mode de démarrage, déduit de <see cref="BiosInfo.IsUefi"/>. Conditionne le
/// conseil CSM-off : on ne coupe le CSM qu'en démarrage <see cref="Uefi"/>
/// (le couper sur une installation Legacy/MBR risque un PC qui ne démarre plus).
/// </summary>
public enum BootMode
{
    /// <summary>Démarrage UEFI : le conseil CSM-off est sans risque.</summary>
    Uefi,

    /// <summary>Démarrage Legacy/CSM : le conseil CSM-off est retiré (risque de non-démarrage).</summary>
    Legacy,

    /// <summary>Mode de démarrage indéterminé : traité comme risqué pour CSM-off.</summary>
    Unknown,
}

/// <summary>
/// Le degré de confiance des chemins de menu d'un <see cref="PersonalizedGuide"/>.
/// Honnêteté : un guide non vérifié est clairement marqué, le client sait que les chemins
/// sont un repli connu, non confirmé pour sa carte exacte.
/// </summary>
public enum GuideConfidence
{
    /// <summary>
    /// La carte et la version de BIOS exactes sont dans la base soignée et vérifiée au banc
    /// (la clé de la base est carte + version de BIOS, pas la marque) → les chemins de menu sont
    /// exacts pour cette machine. Une correspondance fabricant / génération d'UI n'est jamais
    /// Verified : ce n'est que le <see cref="UnverifiedFallback"/> étiqueté.
    /// </summary>
    Verified,

    /// <summary>
    /// La carte exacte n'est pas (encore) couverte → le guide prend le repli connu par
    /// fabricant (ou le générique le plus prudent), étiqueté « non vérifié pour ta carte
    /// exacte ». Il porte toujours le nom exact du réglage à chercher + Clear-CMOS.
    /// </summary>
    UnverifiedFallback,
}

/// <summary>
/// Une étape lisible, dans l'ordre, d'un guide BIOS personnalisé. C'est du texte pour un
/// humain, jamais une écriture ni un script.
/// </summary>
/// <param name="Order">Position dans sa section, à partir de 1 (les étapes sortent dans l'ordre).</param>
/// <param name="InstructionKey">Clé resx (FR+EN) de la consigne. Jamais de texte en dur.</param>
/// <param name="ExactValue">
/// La valeur de navigation exacte de l'étape quand elle est connue : le chemin de menu
/// (« Ai Tweaker &gt; Ai Overclock Tuner »), le nom de l'option (« EXPO I ») ou une touche
/// (« F10 »). Pour une étape de repli, c'est le nom exact du réglage à chercher (le client
/// n'a jamais besoin d'un tiers). Null seulement pour une étape de récit ou de secours.
/// </param>
/// <param name="Setting">Le réglage firmware visé par l'étape (pour grouper et ordonner).</param>
/// <param name="IsRecoveryRelated">Vrai pour une étape de sécurité ou de secours (rendue avec soin).</param>
public record GuideStep(
    int Order,
    string InstructionKey,
    string? ExactValue,
    FirmwareSettingKind Setting,
    bool IsRecoveryRelated);

/// <summary>
/// La section obligatoire de sécurité et de secours. Elle est en haut (le client est
/// rassuré avant de toucher au BIOS) et en bas (elle est là quand un redémarrage
/// tourne mal). Elle comprend le message rassurant sur le non-démarrage, l'attente de
/// l'entraînement mémoire, Clear-CMOS, Load Optimized Defaults et l'échelle « un changement à la fois ».
/// </summary>
/// <param name="Top">Étapes de secours en haut du guide (jamais vide).</param>
/// <param name="Bottom">Étapes de secours en bas du guide (jamais vide).</param>
/// <param name="CsmAdviceSuppressed">
/// Vrai quand une étape CSM-off a été retirée parce que le système ne démarre pas en UEFI
/// (une étape d'avertissement la remplace). Pilote la note honnête « on n'a pas conseillé CSM-off ».
/// </param>
public record RecoverySection(
    IReadOnlyList<GuideStep> Top,
    IReadOnlyList<GuideStep> Bottom,
    bool CsmAdviceSuppressed);

/// <summary>
/// Le contenu complet du guide BIOS, ciblé sur la machine (sans rendu). Il ne montre que
/// ce qui est mal réglé ici, avec les chemins de menu exacts (ou un repli étiqueté),
/// l'ordre des dépendances ReBAR et la section de secours obligatoire en haut et en bas.
/// </summary>
/// <param name="Vendor">Le fabricant de BIOS trouvé (pour le nom de la carte et le repli).</param>
/// <param name="BiosUiGeneration">La génération d'UI BIOS trouvée (par exemple « Click BIOS 5 »).</param>
/// <param name="DetectedBoardLabel">Le nom lisible de la carte détectée (Manufacturer + Product).</param>
/// <param name="Confidence">Verified (carte couverte) ou UnverifiedFallback (repli connu).</param>
/// <param name="BootMode">Le mode de démarrage trouvé (conditionne CSM-off).</param>
/// <param name="Findings">Les constats à faire que traite ce guide (classe Guide seulement).</param>
/// <param name="Steps">Les étapes de consigne, dans l'ordre, par constat (le secours vit dans <see cref="Recovery"/>).</param>
/// <param name="Recovery">La section de secours obligatoire, en haut et en bas.</param>
/// <param name="EnterKey">La touche pour entrer dans le BIOS (par exemple « Del », « F2 »), reprise telle quelle du modèle trouvé. Peut être vide si le modèle n'en donne pas.</param>
/// <param name="SaveKey">La touche pour sauvegarder et quitter (par exemple « F10 »), reprise telle quelle du modèle trouvé. Peut être vide si le modèle n'en donne pas.</param>
/// <param name="BoardProduct">Le modèle seul, tel que Windows le lit (Win32_BaseBoard.Product), pour
/// pointer le manuel du constructeur.</param>
public record PersonalizedGuide(
    BiosVendor Vendor,
    string BiosUiGeneration,
    string DetectedBoardLabel,
    GuideConfidence Confidence,
    BootMode BootMode,
    IReadOnlyList<BiosFinding> Findings,
    IReadOnlyList<GuideStep> Steps,
    RecoverySection Recovery,
    string EnterKey,
    string SaveKey,
    string BoardProduct = "");

// ── Modèle de données de la bibliothèque (lu depuis le JSON embarqué, validé par prudence). ──

/// <summary>
/// La navigation de menu par réglage pour un fabricant × génération d'UI BIOS, qui sert à
/// construire <see cref="GuideStep.ExactValue"/>. Chaque réglage porte le chemin de menu
/// exact et le nom exact du réglage à chercher (le repli peut toujours donner le nom,
/// même quand le chemin est générique).
/// </summary>
/// <param name="Setting">Le réglage firmware vers lequel mène l'entrée.</param>
/// <param name="MenuPath">Le chemin de menu BIOS exact (par exemple « Ai Tweaker &gt; Ai Overclock Tuner »). Peut être vide pour un repli qui n'a que le nom.</param>
/// <param name="SettingName">Le nom exact du réglage à l'écran (par exemple « EXPO », « Re-Size BAR Support »). Jamais vide.</param>
/// <param name="TargetValue">La valeur exacte à poser (par exemple « EXPO I », « Enabled », « Disabled »).</param>
public record GuideSettingPath(
    FirmwareSettingKind Setting,
    string MenuPath,
    string SettingName,
    string TargetValue);

/// <summary>
/// Un modèle de guide soigné. La plupart des modèles sont des replis connus par fabricant
/// (× génération d'UI BIOS). Une vraie entrée de la base porte en plus <see cref="Board"/> +
/// <see cref="BiosVersion"/>, la seule clé qui peut être
/// <see cref="IsVerified"/>. Le validateur rejette tout modèle <see cref="IsVerified"/>
/// sans cette clé carte + version de BIOS : une entrée fabricant / génération d'UI ne peut
/// jamais se dire « vérifiée pour ta carte exacte ». Lu depuis le JSON embarqué, validé
/// avant usage.
/// </summary>
/// <param name="Vendor">Le fabricant visé par ce modèle.</param>
/// <param name="BiosUiGeneration">La génération d'UI BIOS (par exemple « Click BIOS 5 »), ou « Generic ».</param>
/// <param name="IsVerified">
/// Vrai seulement pour une entrée carte + version de BIOS vérifiée au banc ; faux pour tout
/// repli par fabricant, génération d'UI ou générique. Imposé par le validateur : vrai sans
/// <see cref="Board"/> + <see cref="BiosVersion"/> est rejeté comme corrompu.
/// </param>
/// <param name="Board">
/// La carte exacte sur laquelle l'entrée a été vérifiée au banc (par exemple « ROG STRIX B650-A GAMING WIFI »),
/// comparée à <see cref="MotherboardInfo.Product"/>. Vide pour un repli fabricant ou générique.
/// </param>
/// <param name="BiosVersion">
/// La version de BIOS exacte vérifiée pour cette entrée (comparée à
/// <see cref="BiosInfo.Version"/>). Vide pour un repli fabricant ou générique. Avec
/// <see cref="Board"/>, elle forme la clé carte + version de BIOS de la base.
/// </param>
/// <param name="EnterKey">La touche pour entrer dans le BIOS (par exemple « Del », « F2 »).</param>
/// <param name="SaveKey">La touche pour sauvegarder et quitter (par exemple « F10 »).</param>
/// <param name="Paths">Les entrées de navigation par réglage.</param>
public record GuideTemplate(
    BiosVendor Vendor,
    string BiosUiGeneration,
    bool IsVerified,
    string Board,
    string BiosVersion,
    string EnterKey,
    string SaveKey,
    IReadOnlyList<GuideSettingPath> Paths);

/// <summary>
/// Trouve le <see cref="GuideTemplate"/> soigné d'une machine. Réussit toujours avec un
/// modèle utilisable. Ordre de priorité :
/// <list type="number">
///   <item>une entrée <b>carte + version de BIOS</b> vérifiée au banc, seule correspondance
///   qui peut être <see cref="GuideTemplate.IsVerified"/> (« vérifiée pour ta carte exacte ») ;</item>
///   <item>le repli connu par fabricant × génération d'UI BIOS (jamais vérifié) ;</item>
///   <item>le repli générique du fabricant ;</item>
///   <item>le repli générique commun à tous les fabricants.</item>
/// </list>
/// Un repli étiqueté (<see cref="GuideTemplate.IsVerified"/> = false) porte toujours les
/// noms exacts des réglages : le client n'est jamais laissé seul. L'échec franc est réservé
/// aux données de bibliothèque corrompues (validation prudente), jamais à une carte non couverte.
/// </summary>
public interface IGuideLibrary
{
    /// <summary>
    /// Rend le meilleur modèle pour la machine. Quand <paramref name="board"/> +
    /// <paramref name="biosVersion"/> correspondent à une entrée vérifiée, c'est elle (Verified)
    /// qui est rendue ; sinon le repli connu par fabricant ou générique (jamais
    /// vérifié). N'échoue que sur des données de bibliothèque corrompues.
    /// </summary>
    /// <param name="vendor">Le fabricant de BIOS trouvé.</param>
    /// <param name="biosUiGeneration">La génération d'UI BIOS (clé de repli).</param>
    /// <param name="board">Le modèle de carte détecté (la clé de la base, avec <paramref name="biosVersion"/>) ; facultatif.</param>
    /// <param name="biosVersion">La version de BIOS détectée (la clé de la base, avec <paramref name="board"/>) ; facultatif.</param>
    Result<GuideTemplate> Resolve(
        BiosVendor vendor, string biosUiGeneration, string? board = null, string? biosVersion = null);
}

/// <summary>
/// Assemble le <see cref="PersonalizedGuide"/> à partir des constats à faire
/// (<see cref="BiosMatrixClass.Guide"/>) + l'identité carte/BIOS. Ne montre que ce qui
/// est mal réglé, avec les chemins exacts (ou un repli étiqueté), l'ordre des
/// dépendances ReBAR, la section de secours obligatoire en haut et en bas, et les gardes
/// (Legacy → pas de CSM-off ; carte non couverte → Unverified ; les constats ReBAR NVIDIA
/// Unknown ne sont pas Guide et ne donnent aucune étape d'écriture). N'écrit jamais.
/// </summary>
public interface IGuideGenerator
{
    /// <summary>
    /// Construit le guide personnalisé de <paramref name="hw"/> à partir des
    /// <paramref name="findings"/> (seuls les constats de classe Guide donnent des étapes).
    /// Échoue proprement via <see cref="Result"/> sur des données de bibliothèque corrompues.
    /// </summary>
    Result<PersonalizedGuide> Generate(HardwareInventory hw, IReadOnlyList<BiosFinding> findings);
}
