using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>
/// A localized, renderer-agnostic view of the PERSONALIZED BIOS guide (M3) — the "HOW"
/// behind the findings: the exact menu path → setting → value to set for this machine,
/// plus the mandatory recovery section, an HONEST confidence label, and the §16 CSM note.
///
/// HONESTY invariants this VM upholds:
///   • a guide is GUIDANCE-only text — it is never a BIOS write and never enters the apply
///     batch (the user changes the setting themselves);
///   • <see cref="IsVerified"/> is true only when the generator resolved the exact board
///     (<see cref="GuideConfidence.Verified"/>); a best-known fallback is always labelled
///     "not yet verified for your exact board" — a fallback is never shown as verified;
///   • the recovery section (<see cref="RecoveryTop"/> / <see cref="RecoveryBottom"/>) is
///     mandatory and surfaced both before and after the steps;
///   • when the CSM-off step was suppressed because the system boots Legacy
///     (<see cref="CsmSuppressed"/>), an honest note says we did not advise disabling it.
/// </summary>
public sealed partial class BiosGuideViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    /// <summary>Ou en est le depart vers le BIOS lance depuis la carte.</summary>
    public enum EtatDepart { Repos, Confirmation, EnCours, Lance }

    private readonly Func<string, string> _localize;
    private readonly Func<Task<Result>>? _annulerDepart;
    // M3 lot 2 — la couture « emmene-moi dans le BIOS ». Un DELEGUE, pas IElevatedClient :
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
        // Confidence: Verified only for the exact board; otherwise the labelled fallback.
        // Never label a fallback as verified (decision §9 honesty).
        IsVerified = guide.Confidence == GuideConfidence.Verified;
        ConfidenceLabel = localize(IsVerified
            ? "Scan.Bios.Guide.Verified"
            : "Scan.Bios.Guide.Unverified");

        BoardLabel = guide.DetectedBoardLabel ?? string.Empty;

        // Each step: the localized human instruction + the RAW exact value (menu path →
        // setting → = value), shown verbatim. ExactValue may be null for a name-only fallback.
        Steps = guide.Steps
            .Select(s => new BiosGuideStepViewModel(localize(s.InstructionKey), s.ExactValue))
            .ToList();

        // Recovery is mandatory: localize the top + bottom ladders (text-only, no exact value).
        RecoveryTop = guide.Recovery.Top
            .Select(s => localize(s.InstructionKey))
            .ToList();
        RecoveryBottom = guide.Recovery.Bottom
            .Select(s => localize(s.InstructionKey))
            .ToList();

        // §16: when we suppressed CSM-off (Legacy boot), say so honestly.
        CsmSuppressed = guide.Recovery.CsmAdviceSuppressed;
        CsmSuppressedNote = CsmSuppressed ? localize("Scan.Bios.Guide.CsmSuppressed") : string.Empty;

        // The enter/save keys (carried verbatim from the resolved template). Surface the RAW
        // key plus a localized, honest instruction. A blank key yields an empty instruction —
        // we never print "Press  to enter" when the template did not specify a key.
        EnterKey = guide.EnterKey ?? string.Empty;
        SaveKey = guide.SaveKey ?? string.Empty;
        EnterInstruction = FormatKey(localize("Scan.Bios.Guide.EnterKey"), EnterKey);
        SaveInstruction = FormatKey(localize("Scan.Bios.Guide.SaveKey"), SaveKey);

        // 07/10/2026 (D12) : une carte jamais vue n'a que des chemins « les mieux connus ». Le manuel de son constructeur
        // fait foi ; DrDXT ne l'invente pas, il y renvoie (un lien direct seulement là où l'adresse est prouvée).
        if (!IsVerified)
        {
            LienManuel = LienDuManuel(guide.Vendor, guide.BoardProduct);
            ManuelDeLaCarte = string.Format(CultureInfo.CurrentCulture, localize("Scan.Bios.Guide.Manuel"),
                guide.BoardProduct.Length > 0 ? guide.BoardProduct : BoardLabel);
        }

        TexteTelephone = Telephone(this, localize);
        // Revue F-48 : un QR montré à 180 points avec 93 à 165 modules tombait sous 2 pixels par module, illisible
        // par un téléphone. Il s'affiche maintenant à sa taille (4 pixels par module) et ne dépasse pas la version 25 ;
        // au-delà, pas d'image plutôt qu'une image qu'aucun téléphone ne lit.
        try
        {
            using var donnees = new QRCoder.QRCodeGenerator().CreateQrCode(TexteTelephone, QRCoder.QRCodeGenerator.ECCLevel.L, forceUtf8: true);
            ModulesQr = donnees.ModuleMatrix.Count;
            QrTelephone = ModulesQr <= ModulesMaximum ? new QRCoder.PngByteQRCode(donnees).GetGraphic(PixelsParModule) : null;
        }
        catch (Exception) { QrTelephone = null; /* trop long pour un QR : le texte reste à l'écran */ }
    }

    /// <summary>07/10/2026 (D12) : la phrase qui renvoie au manuel du constructeur ; <c>null</c> pour une carte vérifiée.</summary>
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
        // Revue F-51 : beaucoup de cartes MSI ne disent que leur code (« MS-7C02 ») : ce n'est pas un nom de page.
        if (vendeur != BiosVendor.Msi || nom.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(nom, @"^[A-Za-z0-9 \-+]+$")
            || System.Text.RegularExpressions.Regex.IsMatch(nom, @"^MS-[0-9A-Z]+$"))
            return null;
        return "https://www.msi.com/Motherboard/" + nom.Replace(' ', '-') + "/support";
    }

    /// <summary>07/10/2026 (chantier D11, demande du 21/06) : le guide se lit pendant que le PC est dans le BIOS,
    /// donc ailleurs que sur son écran. Le texte entier tient dans le QR : le téléphone le lit sans réseau ni serveur.</summary>
    public string TexteTelephone { get; }

    /// <summary>Le QR de <see cref="TexteTelephone"/>, en PNG ; <c>null</c> si le texte ne tient pas dans un QR.</summary>
    public byte[]? QrTelephone { get; }

    public bool QrVisible => QrTelephone is not null;

    /// <summary>Version 25 (117 modules) plus la marge de 4 de chaque côté que QRCoder ajoute.</summary>
    internal const int ModulesMaximum = 125;

    internal const int PixelsParModule = 4;

    /// <summary>La taille du code, en modules, marge comprise ; 0 si rien n'a été encodé.</summary>
    internal int ModulesQr { get; }

    public string LabelQr => _localize("Scan.Bios.Guide.Qr");

    internal static string Telephone(BiosGuideViewModel g, Func<string, string> localize)
    {
        var lignes = new List<string> { string.Format(CultureInfo.CurrentCulture, localize("Scan.Bios.Guide.Qr.Titre"), g.BoardLabel) };
        if (g.EnterInstruction.Length > 0) lignes.Add(g.EnterInstruction);
        lignes.AddRange(g.Steps.Select((s, i) => s.ExactValue is { Length: > 0 } v ? $"{i + 1}. {s.Instruction.TrimEnd(' ', ':')} : {v}" : $"{i + 1}. {s.Instruction}"));
        if (g.SaveInstruction.Length > 0) lignes.Add(g.SaveInstruction);
        // F-48 : le QR porte ce qui se fait dans le BIOS ; les phrases longues (confiance, renvoi au manuel, échelle de
        // secours complète) restent à l'écran, une ligne de secours suffit au téléphone.
        if (g.LienManuel is { } lien) lignes.Add(string.Format(CultureInfo.CurrentCulture, localize("Scan.Bios.Guide.Qr.Manuel"), lien));
        lignes.Add(localize("Scan.Bios.Guide.Qr.Secours"));
        return string.Join("\n", lignes.Where(l => l.Length > 0));
    }

    /// <summary>True only when the menu paths are verified for the exact board. A fallback is
    /// never verified — its paths are best-known and labelled as such.</summary>
    public bool IsVerified { get; }

    /// <summary>The localized confidence statement — verified-for-your-board vs the honest
    /// "not yet verified for your exact board" fallback label. Never empty.</summary>
    public string ConfidenceLabel { get; }

    /// <summary>Human label of the detected board (Manufacturer + Product).</summary>
    public string BoardLabel { get; }

    /// <summary>The ordered per-finding instruction steps (the "HOW").</summary>
    public IReadOnlyList<BiosGuideStepViewModel> Steps { get; }

    /// <summary>The mandatory recovery ladder shown at the TOP of the guide (non-empty).</summary>
    public IReadOnlyList<string> RecoveryTop { get; }

    /// <summary>The mandatory recovery ladder shown at the BOTTOM of the guide (non-empty).</summary>
    public IReadOnlyList<string> RecoveryBottom { get; }

    /// <summary>True when a CSM-off step was suppressed because the system boots Legacy (no-boot
    /// guard, §16). Drives the honest "we did NOT advise disabling CSM" note.</summary>
    public bool CsmSuppressed { get; }

    /// <summary>The honest CSM-suppressed note; empty when CSM-off was not suppressed.</summary>
    public string CsmSuppressedNote { get; }

    /// <summary>The RAW key to enter the BIOS (e.g. "Del", "F2"), verbatim from the resolved
    /// template; empty when the template did not specify one.</summary>
    public string EnterKey { get; }

    /// <summary>The RAW key to save &amp; exit the BIOS (e.g. "F10"), verbatim from the resolved
    /// template; empty when the template did not specify one.</summary>
    public string SaveKey { get; }

    /// <summary>The localized "press {key} to enter the BIOS" instruction; empty when no enter
    /// key was specified (we never print "Press  to enter").</summary>
    public string EnterInstruction { get; }

    /// <summary>The localized "press {key} to save and exit" instruction; empty when no save
    /// key was specified.</summary>
    public string SaveInstruction { get; }

    /// <summary>
    /// M3 lot 2 — true quand DrDXT peut emmener l'utilisateur dans le setup firmware lui-meme
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
    /// Demande a Windows d'envoyer le PROCHAIN demarrage dans le setup firmware, puis de
    /// redemarrer. DrDXT n'ecrit aucune variable firmware : c'est Windows qui detient le bit
    /// <c>OsIndications</c>.
    ///
    /// <see cref="EnterInstruction"/> reste affiche quoi qu'il arrive. Le bouton est un
    /// CONFORT, pas un remplacement : si le firmware refuse le boot-to-setup, ou si la couture
    /// n'est pas la, l'utilisateur doit toujours avoir sous les yeux la touche a presser. Un
    /// produit qui cache le chemin manuel parce qu'il croit avoir mieux laisse le client sans
    /// rien le jour ou le mieux ne marche pas.
    /// </summary>
    public Task<Result> OpenFirmwareSetupAsync(int delaySeconds = DefaultFirmwareRebootDelaySeconds)
        => _requestFirmwareReboot is null
            ? Task.FromResult(Result.Fail(
                ErrorCodes.BiosInputInvalid,
                "Opening the firmware setup directly is not available on this host."))
            : _requestFirmwareReboot(delaySeconds);

    // ── Le bouton de la carte (26/09/2026) ────────────────────────────────────────────────────
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
        catch (Exception ex) { r = Result.Fail(ErrorCodes.BiosInputInvalid, ex.Message); }
        if (r.IsOk) { ErreurDepart = null; Depart = EtatDepart.Repos; return; }
        ErreurDepart = $"{_localize("Bios.Depart.AnnulationEchec")} : {r.Error?.Message}";
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
        catch (Exception ex) { r = Result.Fail(ErrorCodes.BiosInputInvalid, ex.Message); }
        if (r.IsOk) { Depart = EtatDepart.Lance; return; }
        ErreurDepart = $"{_localize("Bios.Depart.Echec")} : {r.Error?.Message}";
        Depart = EtatDepart.Repos;
    }

    // Builds the localized key instruction with the same format-with-fallback idiom used
    // elsewhere (FindingViewModel / BiosFindingViewModel): if the template has a "{0}" slot,
    // format the key into it; otherwise append "<template> <key>". A blank key → no
    // instruction at all (honest: never "Press  to enter").
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
/// One ordered step of a personalized BIOS guide: the localized human instruction plus the
/// RAW exact navigation value (the "menu path → setting → = value" string), shown verbatim.
/// </summary>
public sealed class BiosGuideStepViewModel
{
    public BiosGuideStepViewModel(string instruction, string? exactValue)
    {
        Instruction = instruction;
        ExactValue = exactValue;
    }

    /// <summary>The localized human sentence around the exact value.</summary>
    public string Instruction { get; }

    /// <summary>The exact "menu path → setting → = value" string; null for a pure narrative step
    /// or a name-only fallback with no path.</summary>
    public string? ExactValue { get; }
}
