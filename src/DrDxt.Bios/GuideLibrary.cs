using System.Reflection;
using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// Bibliothèque de guides pilotée par les données : les <see cref="GuideTemplate"/> curés et validés. Seule une entrée
/// <b>carte + version BIOS</b> se résout en <see cref="GuideTemplate.IsVerified"/> ; constructeur × génération
/// d'interface et génériques sont des replis étiquetés (IsVerified = false) qui portent quand même les noms exacts des
/// réglages. <see cref="Resolve"/> n'échoue que sur des données corrompues, rejetées à la construction par
/// <see cref="GuideTemplateValidator"/>.
/// </summary>
public sealed class GuideLibrary : IGuideLibrary
{
    private readonly IReadOnlyList<GuideTemplate> _templates;

    /// <summary>
    /// Construit une bibliothèque à partir de modèles déjà validés. Utiliser
    /// <see cref="GuideLibraryFactory"/> pour charger les données embarquées avec prudence.
    /// </summary>
    public GuideLibrary(IReadOnlyList<GuideTemplate> templates)
        => _templates = templates ?? throw new ArgumentNullException(nameof(templates));

    /// <inheritdoc/>
    public Result<GuideTemplate> Resolve(
        BiosVendor vendor, string biosUiGeneration, string? board = null, string? biosVersion = null)
    {
        var gen = string.IsNullOrWhiteSpace(biosUiGeneration)
            ? BiosUiGenerationDetector.Generic
            : biosUiGeneration.Trim();

        // 0) L'entrée carte + version BIOS, la seule qui puisse être Verified (une entrée constructeur / génération ne porte
        //    pas de clé de carte). Sans entrée pour cette carte (l'état actuel), les replis ci-dessous.
        if (!string.IsNullOrWhiteSpace(board) && !string.IsNullOrWhiteSpace(biosVersion))
        {
            var b = board.Trim();
            var v = biosVersion.Trim();
            var boardExact = _templates.FirstOrDefault(t =>
                t.Vendor == vendor &&
                !string.IsNullOrWhiteSpace(t.Board) &&
                !string.IsNullOrWhiteSpace(t.BiosVersion) &&
                string.Equals(t.Board, b, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.BiosVersion, v, StringComparison.OrdinalIgnoreCase));
            if (boardExact is not null)
                return Result<GuideTemplate>.Ok(boardExact);
        }

        // 1) Meilleure entrée connue pour ce constructeur × génération d'interface (un repli étiqueté,
        //    jamais vérifié ; les chemins de menu sont les meilleurs disponibles, pas confirmés pour la carte).
        var exact = _templates.FirstOrDefault(t =>
            t.Vendor == vendor &&
            string.IsNullOrWhiteSpace(t.Board) &&
            string.Equals(t.BiosUiGeneration, gen, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return Result<GuideTemplate>.Ok(exact);

        // 2) Per-vendor generic fallback (labelled unverified).
        var vendorGeneric = _templates.FirstOrDefault(t =>
            t.Vendor == vendor &&
            string.IsNullOrWhiteSpace(t.Board) &&
            string.Equals(t.BiosUiGeneration, BiosUiGenerationDetector.Generic, StringComparison.OrdinalIgnoreCase));
        if (vendorGeneric is not null)
            return Result<GuideTemplate>.Ok(vendorGeneric);

        // 3) Cross-vendor generic fallback (most prudent; Unknown × Generic).
        var generic = _templates.FirstOrDefault(t =>
            t.Vendor == BiosVendor.Unknown &&
            string.IsNullOrWhiteSpace(t.Board) &&
            string.Equals(t.BiosUiGeneration, BiosUiGenerationDetector.Generic, StringComparison.OrdinalIgnoreCase));
        if (generic is not null)
            return Result<GuideTemplate>.Ok(generic);

        // La bibliothèque embarquée livre toujours une entrée Unknown×Generic : ici, données incomplètes → échec propre,
        // jamais un modèle null.
        return Result<GuideTemplate>.Fail(
            ErrorCodes.BiosTemplateInvalid, "guide library is missing the generic fallback template");
    }
}

/// <summary>
/// Charge les données de guide embarquées (<c>data/guides.json</c>) en <see cref="GuideLibrary"/> validée. Une ressource
/// absente ou corrompue lève au démarrage (un bug d'empaquetage, bruyant) ; le contenu JSON passe par
/// <see cref="GuideTemplateValidator"/>.
/// </summary>
public static class GuideLibraryFactory
{
    private const string ResourceName = "DrDxt.Bios.data.guides.json";

    /// <summary>Charge et valide la bibliothèque embarquée ; ne lève que sur une faute d'empaquetage.</summary>
    public static GuideLibrary CreateDefault() => new(Modeles());

    /// <summary>Les modèles embarqués, validés ; ne lève que sur une faute d'empaquetage.</summary>
    public static IReadOnlyList<GuideTemplate> Modeles()
    {
        var parsed = GuideTemplateValidator.ParseAndValidate(ReadEmbedded());
        if (!parsed.IsOk)
            throw new InvalidOperationException(
                $"Embedded guide library is invalid ({parsed.Error!.Value.Code}): {parsed.Error!.Value.Message}");
        return parsed.Value!;
    }

    private static string ReadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded guide resource '{ResourceName}' not found.");
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }
}
