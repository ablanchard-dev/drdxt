using System.Text.Json;
using System.Text.Json.Serialization;
using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// Analyse et valide avec prudence le JSON de la bibliothèque de guides (valider toute donnée
/// externe avant de s'en servir). Les données de la bibliothèque sont un atout du moat ; un fichier
/// corrompu ou altéré doit échouer proprement (<see cref="ErrorCodes.BiosTemplateInvalid"/>) : jamais
/// de plantage, jamais de valeur non validée dans un guide.
///
/// Invariants validés :
///   • tableau JSON bien formé, au moins un modèle ;
///   • constructeur et chaque réglage s'analysent en un enum connu (aucun repli sur l'enum 0) ;
///   • un modèle ne peut être <c>isVerified</c> que s'il porte une clé de base de connaissance
///     carte + version BIOS (une entrée constructeur / génération d'interface ne peut jamais être « vérifiée ») ;
///   • les touches d'entrée / d'enregistrement sont présentes ;
///   • le <c>settingName</c> exact de chaque chemin est non vide (c'est le filet : le client doit
///     toujours avoir un nom à chercher, même sur un repli) ;
///   • le <c>targetValue</c> de chaque chemin va dans le sens sûr / voulu pour son réglage (CSM
///     désactivé / UEFI ; XMP/EXPO, ReBAR, Above-4G activés ; X3D CPPC Auto/Enabled) : une erreur
///     d'écriture dans le mauvais sens (par exemple <c>Csm="Enabled"</c>) est rejetée ;
///   • aucune valeur ne porte de marqueur d'injection HTML / de modèle (défense en profondeur pour
///     l'étape de rendu suivante).
/// </summary>
public static class GuideTemplateValidator
{
    // Marqueurs qui ne doivent jamais apparaître dans les données de la bibliothèque. Les valeurs sont
    // de simples libellés / touches de menu BIOS ; '>' n'est exprès pas ici, c'est le séparateur
    // légitime de chemin de menu (« Advanced > PCI Subsystem »). Les marqueurs ci-dessous attrapent
    // les balises HTML et la syntaxe de moteurs de modèles : altération ou erreur d'écriture, pas des
    // libellés normaux.
    private static readonly string[] InjectionMarkers =
        { "<", "{{", "}}", "&lt;", "&gt;", "${" };

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Analyse <paramref name="json"/> en modèles validés, ou échoue proprement.
    /// </summary>
    public static Result<IReadOnlyList<GuideTemplate>> ParseAndValidate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Fail("library JSON is empty");

        List<TemplateDto>? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<List<TemplateDto>>(json, Options);
        }
        catch (JsonException ex)
        {
            return Fail($"malformed JSON: {ex.Message}");
        }

        if (dtos is null || dtos.Count == 0)
            return Fail("no templates in library payload");

        var templates = new List<GuideTemplate>(dtos.Count);
        foreach (var dto in dtos)
        {
            var built = Validate(dto);
            if (!built.IsOk)
                return Result<IReadOnlyList<GuideTemplate>>.Fail(built.Error!.Value.Code, built.Error!.Value.Message);
            templates.Add(built.Value!);
        }

        return Result<IReadOnlyList<GuideTemplate>>.Ok(templates);
    }

    private static Result<GuideTemplate> Validate(TemplateDto dto)
    {
        if (dto.Vendor is null || !Enum.TryParse<BiosVendor>(dto.Vendor, ignoreCase: false, out var vendor))
            return FailT($"unknown or missing vendor '{dto.Vendor}'");
        if (IsBlankOrInjected(dto.BiosUiGeneration))
            return FailT("missing/invalid biosUiGeneration");
        if (IsBlankOrInjected(dto.EnterKey))
            return FailT("missing/invalid enterKey");
        if (IsBlankOrInjected(dto.SaveKey))
            return FailT("missing/invalid saveKey");
        if (dto.Paths is null || dto.Paths.Count == 0)
            return FailT("template has no setting paths");

        // Carte + version BIOS sont facultatives (un repli constructeur / génération d'interface les
        // omet), mais présentes, elles doivent être propres. Ensemble, elles forment la clé carte + BIOS.
        var board = dto.Board ?? string.Empty;
        if (ContainsInjection(board))
            return FailT("injection in board");
        var biosVersion = dto.BiosVersion ?? string.Empty;
        if (ContainsInjection(biosVersion))
            return FailT("injection in biosVersion");

        // Honnêteté : « vérifié pour votre carte exacte » ne peut être annoncé que par une vraie entrée
        // carte + version BIOS. Une entrée constructeur / génération d'interface qui se dit vérifiée ment
        // (et c'est un vecteur d'altération / d'erreur) → la rejeter.
        if (dto.IsVerified && (string.IsNullOrWhiteSpace(board) || string.IsNullOrWhiteSpace(biosVersion)))
            return FailT("a verified template must carry both a board and a biosVersion "
                + "(the board+BIOS KB key); vendor/UI-generation entries cannot be verified");

        var paths = new List<GuideSettingPath>(dto.Paths.Count);
        var seen = new HashSet<FirmwareSettingKind>();
        foreach (var p in dto.Paths)
        {
            if (p.Setting is null || !Enum.TryParse<FirmwareSettingKind>(p.Setting, ignoreCase: false, out var setting))
                return FailT($"unknown or missing setting '{p.Setting}'");
            if (!seen.Add(setting))
                return FailT($"duplicate setting '{setting}' in template");

            // MenuPath peut être vide (repli sur le seul nom) ; mais présent, il doit être propre.
            var menuPath = p.MenuPath ?? string.Empty;
            if (ContainsInjection(menuPath))
                return FailT($"injection in menuPath for '{setting}'");

            // SettingName est le filet : il ne peut jamais être vide, et doit être propre.
            if (IsBlankOrInjected(p.SettingName))
                return FailT($"missing/invalid settingName for '{setting}'");
            if (IsBlankOrInjected(p.TargetValue))
                return FailT($"missing/invalid targetValue for '{setting}'");

            // Garde sémantique de sens : un modèle valide en structure pourrait encore être écrit dans un sens
            // dangereux (par exemple Csm="Enabled" → risque de non-démarrage, ou XmpExpo="Disabled").
            // Vérifier que la cible va dans le sens sûr / voulu.
            var targetValue = p.TargetValue!.Trim();
            if (!IsExpectedDirection(setting, targetValue))
                return FailT($"targetValue '{targetValue}' for '{setting}' points the wrong direction "
                    + "(CSM must be Disabled/UEFI; XMP/EXPO, ReBAR and Above-4G must be enabled; "
                    + "X3D CPPC must be Auto/Enabled)");

            paths.Add(new GuideSettingPath(setting, menuPath, p.SettingName!.Trim(), targetValue));
        }

        return Result<GuideTemplate>.Ok(new GuideTemplate(
            vendor, dto.BiosUiGeneration!.Trim(), dto.IsVerified,
            board.Trim(), biosVersion.Trim(),
            dto.EnterKey!.Trim(), dto.SaveKey!.Trim(), paths));
    }

    // Sens attendu de la cible par réglage. Attrape une erreur d'écriture dangereuse qui passe tous
    // les contrôles de structure (par exemple une cible CSM « Enabled », ou une cible XMP « Disabled »).
    // Les jetons se comparent à la valeur cible sans tenir compte de la casse.
    private static bool IsExpectedDirection(FirmwareSettingKind setting, string targetValue)
    {
        var v = targetValue.ToLowerInvariant();
        bool Any(params string[] tokens) => tokens.Any(t => v.Contains(t, StringComparison.Ordinal));
        return setting switch
        {
            // Le profil mémoire doit être activé (enable / un profil / EXPO / XMP / DOCP).
            // Le nom du réglage dans la cible ne dit pas son sens : « XMP Disabled » contient « xmp ».
            FirmwareSettingKind.XmpExpo => Any("enable", "profile", "expo", "xmp", "docp") && !Any("disable"),
            // ReBAR et son prérequis Above 4G doivent être activés.
            FirmwareSettingKind.ReBar => Any("enable"),
            FirmwareSettingKind.Above4G => Any("enable"),
            // Le CSM doit être désactivé ou le mode de démarrage en UEFI, jamais activé
            // (garder le CSM annule ReBAR et mène à une machine qui ne démarre plus).
            FirmwareSettingKind.Csm => Any("disable", "uefi"),
            // La voie CPPC des X3D doit être une voie prise en charge (Auto, Enabled, Driver).
            FirmwareSettingKind.X3dCppc => Any("auto", "enable", "driver"),
            // Les autres réglages n'ont pas de sens imposé pour l'instant.
            _ => true,
        };
    }

    private static bool IsBlankOrInjected(string? s)
        => string.IsNullOrWhiteSpace(s) || ContainsInjection(s);

    private static bool ContainsInjection(string s)
        => InjectionMarkers.Any(m => s.Contains(m, StringComparison.Ordinal));

    private static Result<IReadOnlyList<GuideTemplate>> Fail(string why)
        => Result<IReadOnlyList<GuideTemplate>>.Fail(ErrorCodes.BiosTemplateInvalid, why);

    private static Result<GuideTemplate> FailT(string why)
        => Result<GuideTemplate>.Fail(ErrorCodes.BiosTemplateInvalid, why);

    // Les DTO sont nullables à dessein : un champ manquant est attrapé par la validation, pas
    // par une valeur par défaut de désérialisation qui produirait en silence un enum 0 ou une chaîne vide.
    private sealed class TemplateDto
    {
        public string? Vendor { get; init; }
        public string? BiosUiGeneration { get; init; }
        public bool IsVerified { get; init; }
        public string? Board { get; init; }
        public string? BiosVersion { get; init; }
        public string? EnterKey { get; init; }
        public string? SaveKey { get; init; }
        public List<PathDto>? Paths { get; init; }
    }

    private sealed class PathDto
    {
        public string? Setting { get; init; }
        public string? MenuPath { get; init; }
        public string? SettingName { get; init; }
        public string? TargetValue { get; init; }
    }
}
