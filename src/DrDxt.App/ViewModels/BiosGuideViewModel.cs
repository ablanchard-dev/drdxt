using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>
/// Une vue traduite du guide BIOS personnalisé, indépendante du rendu : le « comment »
/// derrière les constats, c'est-à-dire le chemin exact menu → réglage → valeur pour cette
/// machine, plus la section de secours obligatoire, une étiquette de confiance honnête et la
/// note sur le CSM.
///
/// Règles d'honnêteté que ce VM tient :
///   • un guide n'est que du texte de guidage : jamais une écriture dans le BIOS, jamais dans
///     le lot appliqué (le joueur change le réglage lui-même) ;
///   • <see cref="IsVerified"/> n'est vrai que si le générateur a trouvé la carte exacte
///     (<see cref="GuideConfidence.Verified"/>) ; un repli connu porte toujours l'étiquette
///     « pas encore vérifié pour ta carte exacte », et n'est jamais montré comme vérifié ;
///   • la section de secours (<see cref="RecoveryTop"/> / <see cref="RecoveryBottom"/>) est
///     obligatoire et affichée avant et après les étapes ;
///   • quand l'étape CSM coupé a été retirée parce que le système démarre en Legacy
///     (<see cref="CsmSuppressed"/>), une note honnête dit qu'on n'a pas conseillé de le couper.
/// </summary>
public sealed partial class BiosGuideViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    /// <summary>Ou en est le depart vers le BIOS lance depuis la carte.</summary>
    public enum EtatDepart { Repos, Confirmation, EnCours, Lance }

    private readonly Func<string, string> _localize;
    private readonly Func<Task<Result>>? _annulerDepart;
    // La couture « emmene-moi dans le BIOS ». Un delegue, pas IElevatedClient :
    // meme idiome que `localize`, et le VM reste ignorant de l'IPC. Optionnel/en queue pour
    // que toutes les constructions existantes restent valides.
    private readonly Func<int, Task<Result>>? _requestFirmwareReboot;

    public BiosGuideViewModel(
        PersonalizedGuide guide,
        Func<string, string> localize,
        Func<int, Task<Result>>? requestFirmwareReboot = null,
        Func<Task<Result>>? annulerDepart = null)
    {
        _requestFirmwareReboot = requestFirmwareReboot;
        _annulerDepart = annulerDepart;
        _localize = localize;
        // Confiance : Verified seulement pour la carte exacte ; sinon le repli étiqueté.
        // Jamais un repli étiqueté comme vérifié.
        IsVerified = guide.Confidence == GuideConfidence.Verified;
        ConfidenceLabel = localize(IsVerified
            ? "Scan.Bios.Guide.Verified"
            : "Scan.Bios.Guide.Unverified");

        BoardLabel = guide.DetectedBoardLabel ?? string.Empty;

        // Chaque étape : la consigne traduite et la valeur exacte brute (chemin menu → réglage →
        // = valeur), affichée telle quelle. ExactValue peut être null pour un repli qui n'a que le nom.
        Steps = guide.Steps
            .Select(s => new BiosGuideStepViewModel(localize(s.InstructionKey), s.ExactValue))
            .ToList();

        // Le secours est obligatoire : traduire les échelles du haut et du bas (texte seul, sans valeur exacte).
        RecoveryTop = guide.Recovery.Top
            .Select(s => localize(s.InstructionKey))
            .ToList();
        RecoveryBottom = guide.Recovery.Bottom
            .Select(s => localize(s.InstructionKey))
            .ToList();

        // Quand on a retiré l'étape CSM coupé (démarrage Legacy), le dire honnêtement.
        CsmSuppressed = guide.Recovery.CsmAdviceSuppressed;
        CsmSuppressedNote = CsmSuppressed ? localize("Scan.Bios.Guide.CsmSuppressed") : string.Empty;

        // Les touches d'entrée et de sauvegarde (reprises telles quelles du modèle trouvé). On
        // affiche la touche brute et une consigne traduite et honnête. Une touche vide donne une
        // consigne vide : on n'écrit jamais « Appuie sur  pour entrer » quand le modèle ne donne
        // pas de touche.
        EnterKey = guide.EnterKey ?? string.Empty;
        SaveKey = guide.SaveKey ?? string.Empty;
        EnterInstruction = FormatKey(localize("Scan.Bios.Guide.EnterKey"), EnterKey);
        SaveInstruction = FormatKey(localize("Scan.Bios.Guide.SaveKey"), SaveKey);

        // Une carte jamais vue n'a que des chemins « les mieux connus ». Le manuel de son constructeur
        // fait foi ; DrDXT ne l'invente pas, il y renvoie (un lien direct seulement là où l'adresse est prouvée).
        if (!IsVerified)
        {
            LienManuel = LienDuManuel(guide.Vendor, guide.BoardProduct);
            ManuelDeLaCarte = string.Format(CultureInfo.CurrentCulture, localize("Scan.Bios.Guide.Manuel"),
                guide.BoardProduct.Length > 0 ? guide.BoardProduct : BoardLabel);
        }

        TexteTelephone = Telephone(this, localize);
        AdressePage = PageDuGuide.UrlSiAcceptee(PageDuGuide.Depuis(guide, this, Docteur.Langue.TwoLetterISOLanguageName == "fr" ? "fr" : "en"));
        // Un QR montré à 180 points avec 93 à 165 modules tombait sous 2 pixels par module, illisible
        // par un téléphone. Il s'affiche maintenant à sa taille (4 pixels par module) et ne dépasse pas la version 25 ;
        // au-delà, pas d'image plutôt qu'une image qu'aucun téléphone ne lit. La page d'abord (lisible et mise en forme),
        // quand elle est en ligne et accepte ce guide ; sinon, ou si son adresse ne tient pas, le texte brut, comme avant.
        foreach (var contenu in PageDuGuide.EnLigne && AdressePage is { } page ? new[] { page, TexteTelephone } : [TexteTelephone])
        {
            try
            {
                using var donnees = new QRCoder.QRCodeGenerator().CreateQrCode(contenu, QRCoder.QRCodeGenerator.ECCLevel.L, forceUtf8: true);
                if (donnees.ModuleMatrix.Count > ModulesMaximum) continue;
                ModulesQr = donnees.ModuleMatrix.Count;
                QrTelephone = new QRCoder.PngByteQRCode(donnees).GetGraphic(PixelsParModule);
                QrPorteLaPage = ReferenceEquals(contenu, AdressePage);
                break;
            }
            catch (Exception) { /* trop long pour un QR : le suivant, ou le texte reste à l'écran */ }
        }
    }

    /// <summary>L'adresse de la page du guide au téléphone, le guide entier dans son fragment (<see cref="PageDuGuide"/>) ;
    /// null quand la page refuserait ce guide (une phrase qu'elle ne connaît pas, une valeur hors de ses règles).</summary>
    internal string? AdressePage { get; }

    /// <summary>La taille d'un QR de ce texte, marge comprise, ou null s'il ne tient dans aucun QR.</summary>
    internal static int? Modules(string texte)
    {
        try
        {
            using var donnees = new QRCoder.QRCodeGenerator().CreateQrCode(texte, QRCoder.QRCodeGenerator.ECCLevel.L, forceUtf8: true);
            return donnees.ModuleMatrix.Count;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Vrai quand le QR ouvre la page ; faux quand il porte le texte brut (adresse trop longue) ou rien.</summary>
    internal bool QrPorteLaPage { get; }

    /// <summary>La phrase qui renvoie au manuel du constructeur ; <c>null</c> pour une carte vérifiée.</summary>
    public string? ManuelDeLaCarte { get; }

    /// <summary>La page d'assistance de la carte, seulement quand son adresse suit un motif relevé sur le site du
    /// constructeur ; sinon <c>null</c> (la phrase dit où chercher, sans adresse inventée).</summary>
    public string? LienManuel { get; }

    public bool LienManuelVisible => LienManuel is not null;

    public string LabelOuvrirManuel => _localize("Scan.Bios.Guide.Manuel.Ouvrir");

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OuvrirManuel()
    {
        if (LienManuel is not { } lien) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(lien) { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { /* pas de navigateur : l'adresse reste écrite dans le QR et à l'écran */ }
    }

    /// <summary>MSI seulement, relevé le 07/10/2026 : msi.com/Motherboard/{modèle, espaces en tirets}/support répond pour
    /// « PRO B760-P WIFI DDR4 » (onglet « Manual &amp; Document ») ; la recherche du site, elle, ne trouvait pas ce nom.
    /// ASUS, Gigabyte et ASRock : nommage des pages non prouvé depuis le modèle lu par Windows, pas de lien.</summary>
    internal static string? LienDuManuel(BiosVendor vendeur, string produit)
    {
        // « PRO B760-P WIFI DDR4 (MS-7D98) » : le code interne entre parenthèses ne fait pas partie du nom de page.
        var nom = System.Text.RegularExpressions.Regex.Replace(produit, @"\s*\(MS-[0-9A-Z]+\)\s*$", "").Trim();
        // Beaucoup de cartes MSI ne disent que leur code (« MS-7C02 ») : ce n'est pas un nom de page.
        if (vendeur != BiosVendor.Msi || nom.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(nom, @"^[A-Za-z0-9 \-+]+$")
            || System.Text.RegularExpressions.Regex.IsMatch(nom, @"^MS-[0-9A-Z]+$"))
            return null;
        return "https://www.msi.com/Motherboard/" + nom.Replace(' ', '-') + "/support";
    }

    /// <summary>Le guide se lit pendant que le PC est dans le BIOS,
    /// donc ailleurs que sur son écran. Le texte entier tient dans le QR : le téléphone le lit sans réseau ni serveur.</summary>
    public string TexteTelephone { get; }

    /// <summary>Le QR de <see cref="AdressePage"/>, ou de <see cref="TexteTelephone"/> si l'adresse ne tient pas, en PNG ;
    /// <c>null</c> si aucun des deux ne tient.</summary>
    public byte[]? QrTelephone { get; }

    public bool QrVisible => QrTelephone is not null;

    /// <summary>Version 25 (117 modules) plus la marge de 4 de chaque côté que QRCoder ajoute.</summary>
    internal const int ModulesMaximum = 125;

    internal const int PixelsParModule = 4;

    /// <summary>La taille du code, en modules, marge comprise ; 0 si rien n'a été encodé.</summary>
    internal int ModulesQr { get; }

    /// <summary>La page demande du réseau au téléphone, une fois ; le texte brut se lit sans.</summary>
    public string LabelQr => _localize(QrPorteLaPage ? "Scan.Bios.Guide.Qr.Page" : "Scan.Bios.Guide.Qr");

    internal static string Telephone(BiosGuideViewModel g, Func<string, string> localize)
    {
        var lignes = new List<string> { string.Format(CultureInfo.CurrentCulture, localize("Scan.Bios.Guide.Qr.Titre"), g.BoardLabel) };
        if (g.EnterInstruction.Length > 0) lignes.Add(g.EnterInstruction);
        lignes.AddRange(g.Steps.Select((s, i) => s.ExactValue is { Length: > 0 } v ? $"{i + 1}. {s.Instruction.TrimEnd(' ', ':')}{Docteur.DeuxPoints}{v}" : $"{i + 1}. {s.Instruction}"));
        if (g.SaveInstruction.Length > 0) lignes.Add(g.SaveInstruction);
        // Le QR porte ce qui se fait dans le BIOS ; les phrases longues (confiance, renvoi au manuel, échelle de
        // secours complète) restent à l'écran, une ligne de secours suffit au téléphone.
        if (g.LienManuel is { } lien) lignes.Add(string.Format(CultureInfo.CurrentCulture, localize("Scan.Bios.Guide.Qr.Manuel"), lien));
        lignes.Add(localize("Scan.Bios.Guide.Qr.Secours"));
        return string.Join("\n", lignes.Where(l => l.Length > 0));
    }

    /// <summary>Vrai seulement si les chemins de menu sont vérifiés pour la carte exacte. Un
    /// repli n'est jamais vérifié : ses chemins sont les plus connus, et étiquetés comme tels.</summary>
    public bool IsVerified { get; }

    /// <summary>La phrase de confiance traduite : vérifié pour ta carte, ou l'étiquette honnête
    /// de repli « pas encore vérifié pour ta carte exacte ». Jamais vide.</summary>
    public string ConfidenceLabel { get; }

    /// <summary>Le nom lisible de la carte détectée (Manufacturer + Product).</summary>
    public string BoardLabel { get; }

    /// <summary>Les étapes de consigne, dans l'ordre, une par constat (le « comment »).</summary>
    public IReadOnlyList<BiosGuideStepViewModel> Steps { get; }

    /// <summary>L'échelle de secours obligatoire, affichée en haut du guide (jamais vide).</summary>
    public IReadOnlyList<string> RecoveryTop { get; }

    /// <summary>L'échelle de secours obligatoire, affichée en bas du guide (jamais vide).</summary>
    public IReadOnlyList<string> RecoveryBottom { get; }

    /// <summary>Vrai quand une étape CSM coupé a été retirée parce que le système démarre en
    /// Legacy (garde contre un PC qui ne démarre plus). Pilote la note honnête « on n'a pas
    /// conseillé de couper le CSM ».</summary>
    public bool CsmSuppressed { get; }

    /// <summary>La note honnête sur le CSM retiré ; vide si l'étape CSM coupé n'a pas été retirée.</summary>
    public string CsmSuppressedNote { get; }

    /// <summary>La touche brute pour entrer dans le BIOS (par exemple « Del », « F2 »), reprise
    /// telle quelle du modèle trouvé ; vide si le modèle n'en donne pas.</summary>
    public string EnterKey { get; }

    /// <summary>La touche brute pour sauvegarder et quitter le BIOS (par exemple « F10 »), reprise
    /// telle quelle du modèle trouvé ; vide si le modèle n'en donne pas.</summary>
    public string SaveKey { get; }

    /// <summary>La consigne traduite « appuie sur {touche} pour entrer dans le BIOS » ; vide si
    /// aucune touche d'entrée n'est donnée (on n'écrit jamais « Appuie sur  pour entrer »).</summary>
    public string EnterInstruction { get; }

    /// <summary>La consigne traduite « appuie sur {touche} pour sauvegarder et quitter » ; vide
    /// si aucune touche de sauvegarde n'est donnée.</summary>
    public string SaveInstruction { get; }

    /// <summary>
    /// True quand DrDXT peut emmener l'utilisateur dans le setup firmware lui-meme
    /// (<c>shutdown /r /fw</c>), au lieu de lui demander de marteler une touche au demarrage.
    /// </summary>
    public bool CanOpenFirmwareDirectly => _requestFirmwareReboot is not null;

    /// <summary>
    /// Le delai par defaut avant le redemarrage : assez pour que l'ecran dise ce qui va se
    /// passer et que l'utilisateur puisse se retracter, assez court pour qu'il fasse encore le
    /// lien entre son clic et le redemarrage. Reste dans la bande autorisee (0..300 s).
    /// </summary>
    public const int DefaultFirmwareRebootDelaySeconds = 10;

    /// <summary>
    /// Demande a Windows d'envoyer le prochain demarrage dans le setup firmware, puis de
    /// redemarrer. DrDXT n'ecrit aucune variable firmware : c'est Windows qui detient le bit
    /// <c>OsIndications</c>.
    ///
    /// <see cref="EnterInstruction"/> reste affiche quoi qu'il arrive : si le firmware refuse le boot-to-setup ou si la
    /// couture manque, la touche a presser reste sous les yeux.
    /// </summary>
    public Task<Result> OpenFirmwareSetupAsync(int delaySeconds = DefaultFirmwareRebootDelaySeconds)
        => _requestFirmwareReboot is null
            ? Task.FromResult(Result.Fail(
                ErrorCodes.BiosInputInvalid,
                "Opening the firmware setup directly is not available on this host."))
            : _requestFirmwareReboot(delaySeconds);

    // ── Le bouton de la carte ────────────────────────────────────────────────────
    // `OpenFirmwareSetupAsync` n'etait lie a aucune vue depuis sa creation : le marqueur n'etait jamais
    // ecrit depuis l'interface, donc la verification au retour ne tournait jamais. Deux clics, parce
    // qu'il redemarre la machine : le premier dit ce qui va se passer, le second le fait. La touche a
    // presser (EnterInstruction) reste affichee a cote, quoi qu'il arrive.

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(BoutonDepartVisible), nameof(ConfirmationVisible),
        nameof(DepartEnCoursVisible), nameof(DepartLanceVisible), nameof(AnnulationVisible))]
    private EtatDepart _depart = EtatDepart.Repos;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string? _erreurDepart;

    public bool BoutonDepartVisible => CanOpenFirmwareDirectly && Depart == EtatDepart.Repos;
    public bool ConfirmationVisible => Depart == EtatDepart.Confirmation;
    public bool DepartEnCoursVisible => Depart == EtatDepart.EnCours;
    public bool DepartLanceVisible => Depart == EtatDepart.Lance;
    public bool AnnulationVisible => Depart == EtatDepart.Lance && _annulerDepart is not null;
    public string LabelAnnulerRedemarrage => _localize("Bios.Depart.AnnulerRedemarrage");

    /// <summary>Se retracter pendant le delai : le redemarrage et le depart vers le firmware sont annules,
    /// le marqueur retire. Un echec le dit, et laisse l'etat « lance » : le redemarrage a peut-etre lieu.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task AnnulerRedemarrage()
    {
        if (Depart != EtatDepart.Lance || _annulerDepart is null) return;
        Result r;
        try { r = await _annulerDepart(); }
        // Une panne n'est pas une saisie invalide : son type, comme RaisonLisible.Dire(Exception).
        catch (Exception ex) { r = Result.Fail(ex.GetType().Name, ex.Message); }
        if (r.IsOk) { ErreurDepart = null; Depart = EtatDepart.Repos; return; }
        ErreurDepart = RaisonLisible.Echec(r, _localize("Bios.Depart.AnnulationEchec"), _localize);
    }

    public string LabelDepart => _localize("Bios.Depart.Bouton");
    public string LabelDepartConfirmation => string.Format(CultureInfo.CurrentCulture,
        _localize("Bios.Depart.Confirmation"), DefaultFirmwareRebootDelaySeconds);
    public string LabelDepartConfirmer => _localize("Bios.Depart.Confirmer");
    public string LabelDepartAnnuler => _localize("Bios.Depart.Annuler");
    public string LabelDepartEnCours => _localize("Bios.Depart.EnCours");
    public string LabelDepartLance => string.Format(CultureInfo.CurrentCulture,
        _localize("Bios.Depart.Lance"), DefaultFirmwareRebootDelaySeconds);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void DemanderDepart()
    {
        if (Depart != EtatDepart.Repos || !CanOpenFirmwareDirectly) return;
        ErreurDepart = null;
        Depart = EtatDepart.Confirmation;
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void AnnulerDepart()
    {
        if (Depart == EtatDepart.Confirmation) Depart = EtatDepart.Repos;
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task ConfirmerDepart()
    {
        if (Depart != EtatDepart.Confirmation) return;
        Depart = EtatDepart.EnCours;
        Result r;
        try { r = await OpenFirmwareSetupAsync(); }
        // Une panne n'est pas une saisie invalide : son type, comme RaisonLisible.Dire(Exception).
        catch (Exception ex) { r = Result.Fail(ex.GetType().Name, ex.Message); }
        if (r.IsOk) { Depart = EtatDepart.Lance; return; }
        ErreurDepart = RaisonLisible.Echec(r, _localize("Bios.Depart.Echec"), _localize);
        Depart = EtatDepart.Repos;
    }

    // Construit la consigne de touche avec le même idiome « format avec repli » qu'ailleurs
    // (FindingViewModel / BiosFindingViewModel) : si le modèle a un emplacement « {0} », on y
    // met la touche ; sinon on ajoute « <modèle> <touche> ». Touche vide → aucune consigne
    // (honnête : jamais « Appuie sur  pour entrer »).
    private static string FormatKey(string template, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;
        return template.Contains("{0}", StringComparison.Ordinal)
            ? string.Format(CultureInfo.CurrentCulture, template, key)
            : $"{template} {key}";
    }
}

/// <summary>
/// Une étape, dans l'ordre, d'un guide BIOS personnalisé : la consigne traduite et la valeur
/// de navigation exacte brute (la chaîne « chemin menu → réglage → = valeur »), affichée telle quelle.
/// </summary>
public sealed class BiosGuideStepViewModel
{
    public BiosGuideStepViewModel(string instruction, string? exactValue)
    {
        Instruction = instruction;
        ExactValue = exactValue;
    }

    /// <summary>La phrase traduite autour de la valeur exacte.</summary>
    public string Instruction { get; }

    /// <summary>La chaîne exacte « chemin menu → réglage → = valeur » ; null pour une étape
    /// purement descriptive ou un repli qui n'a que le nom, sans chemin.</summary>
    public string? ExactValue { get; }
}
