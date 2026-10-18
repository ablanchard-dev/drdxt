namespace DrDxt.Core;

/// <summary>Une condition de visibilité relevée dans l'IFR (suppressif / grayoutif) : la copie n'est montrée au joueur
/// que si l'octet <paramref name="Offset"/> de la variable vaut l'une des <paramref name="Valeurs"/>.</summary>
public sealed record ConditionDeVisibilite(string VarName, string VendorGuid, int Offset, IReadOnlySet<byte> Valeurs);

/// <summary>Une copie d'un réglage : un même réglage peut vivre à plusieurs endroits (« Power Down » en a trois sur
/// certaines cartes AMD), et seule celle que le menu montre dit ce que le joueur a réglé. <paramref name="Visible"/> est une
/// conjonction ; vide = toujours montrée.</summary>
public sealed record CopieDeReglage(string CheminMenu, NvramLocation Emplacement, IReadOnlyList<ConditionDeVisibilite> Visible);

/// <summary>Un réglage nommé, et toutes ses copies déclarées par l'IFR.</summary>
public sealed record ReglageAmd(string Nom, IReadOnlyList<CopieDeReglage> Copies);

/// <summary>Une variable UEFI lue par la table, avec sa taille IFR et l'empreinte SHA-256 (hexadécimal minuscule) de son
/// enregistrement dans le magasin d'usine de l'image publique : la preuve que le firmware lu est celui d'où vient la table.</summary>
public sealed record VariableAmd(string Nom, string Guid, int Taille, string EmpreinteUsine);

/// <summary>Une table construite par DrDXT depuis les formulaires IFR du fichier BIOS public d'un constructeur, pour une
/// carte et une version exactes. <paramref name="Source"/> nomme le fichier, son adresse et l'outil d'extraction.</summary>
public sealed record TableBiosAmd(string Carte, string VersionBios, string Source, IReadOnlyList<VariableAmd> Variables,
    IReadOnlyList<ReglageAmd> Reglages);

/// <summary>
/// Les réglages du BIOS d'une carte AMD (PBO, Curve Optimizer, EXPO, Memory Context Restore, Power Down, FCLK, Global
/// C-state), nommés pour une carte et une version de BIOS exactes. Chaque table se construit depuis l'image publique du
/// constructeur, avec les dépendances de visibilité, et se valide sur une vraie carte avant d'entrer ici. Aucune table
/// n'est encore entrée : aucune image AMD n'a été extraite et aucune carte AMD relevée (GATES). Lecture seule, pur ;
/// la lecture des variables vit dans DrDxt.Bios.
/// </summary>
public static class TablesBiosAmd
{
    // Vide tant qu'aucune table n'a son image publique extraite et son relevé sur carte ; le format et la
    // lecture tout ou rien sont prêts et testés sur des tables synthétiques.
    public static readonly IReadOnlyList<TableBiosAmd> Connues = [];

    /// <summary>Les noms qu'une table peut donner : ceux que lisent le réglé contre appliqué (mémoire) et le Curve
    /// Optimizer. Un nom hors de cette liste n'aurait aucun lecteur.</summary>
    public static readonly IReadOnlySet<string> NomsReconnus = new HashSet<string>(StringComparer.Ordinal)
    {
        "PBO", "PBO Scalar", "Curve Optimizer", "Curve Optimizer Sign", "Curve Optimizer Magnitude",
        "EXPO", "Memory Frequency", "FCLK Frequency", "Memory Context Restore", "Power Down Enable", "Gear Down Mode",
        "Command Rate", "tCL", "tRCD", "tRP", "tRAS", "tRC", "tRFC", "Global C-state Control",
    };

    /// <summary>
    /// La table de cette carte et de cette version, à l'égalité exacte des deux chaînes SMBIOS (espaces de bord retirés).
    /// Jamais par « contient » : une variante « WIFI » prendrait la table de la carte sans WIFI, qui n'a pas le même BIOS.
    /// </summary>
    public static TableBiosAmd? Pour(string? carte, string? version, IReadOnlyList<TableBiosAmd>? tables = null)
    {
        if (carte is null || version is null) return null;
        return (tables ?? Connues).SingleOrDefault(t =>
            string.Equals(t.Carte, carte.Trim(), StringComparison.Ordinal) && string.Equals(t.VersionBios, version.Trim(), StringComparison.Ordinal));
    }

    /// <summary>Pur : le constat sur une machine AMD reconnue. Rien hors AMD ; « non nommés » quand la carte et la version
    /// n'ont pas de table, plutôt qu'un nom tiré d'une carte voisine.</summary>
    public static IEnumerable<RepairFinding> Juger(GenerationAmd.Ligne? generation, string? carte, string? version,
        IReadOnlyList<TableBiosAmd>? tables = null)
    {
        if (generation is null) yield break;
        var identite = $"{carte?.Trim() ?? "?"} / {version?.Trim() ?? "?"}";
        yield return Pour(carte, version, tables) is { } t
            ? new RepairFinding("BIOS", "reglages BIOS AMD nommes", identite, null, RepairStatus.Observed, Docteur.T("TBA.Table", identite, t.Source))
            : new RepairFinding("BIOS", "reglages BIOS AMD nommes", identite, null, RepairStatus.Unknown, Docteur.T("TBA.SansTable", identite));
    }
}
