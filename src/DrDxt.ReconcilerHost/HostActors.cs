namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// Jetons stables, non localisés, que l'hôte écrit dans le journal des changements
// d'IDiagnosticsLog. Le journal est une trace d'audit lisible par machine (pas l'interface) :
// ce sont des identifiants fixes, jamais des chaînes montrées à l'utilisateur. Le texte
// d'interface montré à un humain passe par ILocalizer (clés resx), à part de ces jetons.
// -----------------------------------------------------------------------
internal static class HostActors
{
    /// <summary>Acteur de journal pour un événement de sécurité (manifeste altéré / invérifiable,
    /// réconciliation refusée). Permet à un balayage du journal de filtrer les entrées de sécurité de
    /// façon déterministe.</summary>
    public const string Security = "DrDXT/security";

    /// <summary>Le champ « tweak » du journal utilisé pour les événements de sécurité au niveau du
    /// manifeste (pas par levier) : la barrière du mauvais manifeste porte sur tout le manifeste.</summary>
    public const string ManifestTweakId = "manifest";
}
