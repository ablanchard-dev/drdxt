using System.Reflection;
using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// Bibliothèque de guides pilotée par les données : les <see cref="GuideTemplate"/> curés et validés.
/// Une vraie entrée de base de connaissance est indexée par <b>carte + version BIOS</b> et c'est la
/// seule chose qui se résout en <see cref="GuideTemplate.IsVerified"/> ; les entrées par constructeur
/// × génération d'interface BIOS et les entrées génériques ne sont que des meilleurs replis connus.
///
/// La précision fait le produit : une carte + version BIOS couvertes → modèle exact vérifié ; tout
/// le reste → un repli étiqueté (<see cref="GuideTemplate.IsVerified"/> = false) qui porte quand même
/// les noms exacts des réglages. <see cref="Resolve"/> ne laisse jamais l'utilisateur en plan : il
/// n'échoue que sur des données de bibliothèque corrompues en interne, rejetées à la construction
/// par <see cref="GuideTemplateValidator"/>.
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

        // 0) L'entrée carte + version BIOS de la base de connaissance : la seule correspondance qui puisse
        //    être Verified. Une entrée constructeur / génération d'interface ne porte jamais de clé de
        //    carte : elle ne peut donc jamais être rendue ici, ni annoncer « vérifié pour votre carte
        //    exacte ». Quand la base n'a aucune entrée pour cette carte (l'état actuel), on passe aux
        //    meilleurs replis connus et honnêtes ci-dessous.
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

        // La bibliothèque embarquée livre toujours une entrée Unknown×Generic : arriver ici veut dire que
        // les données sont incomplètes en interne → échec propre, ne jamais laisser l'appelant avec un
        // modèle null.
        return Result<GuideTemplate>.Fail(
            ErrorCodes.BiosTemplateInvalid, "guide library is missing the generic fallback template");
    }
}

/// <summary>
/// Charge les données de guide embarquées (<c>data/guides.json</c>) et construit une
/// <see cref="GuideLibrary"/> validée. Prudent : une ressource embarquée absente ou corrompue lève
/// au branchement du démarrage (un bug d'empaquetage qu'on veut bruyant), mais le contenu JSON
/// lui-même est validé par <see cref="GuideTemplateValidator"/> avant usage.
/// </summary>
public static class GuideLibraryFactory
{
    private const string ResourceName = "DrDxt.Bios.data.guides.json";

    /// <summary>Charge et valide la bibliothèque embarquée ; ne lève que sur une faute d'empaquetage.</summary>
    public static GuideLibrary CreateDefault()
    {
        var json = ReadEmbedded();
        var parsed = GuideTemplateValidator.ParseAndValidate(json);
        if (!parsed.IsOk)
            throw new InvalidOperationException(
                $"Embedded guide library is invalid ({parsed.Error!.Value.Code}): {parsed.Error!.Value.Message}");
        return new GuideLibrary(parsed.Value!);
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
