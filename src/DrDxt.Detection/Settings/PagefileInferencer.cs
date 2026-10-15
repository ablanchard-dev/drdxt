using DrDxt.Core;

namespace DrDxt.Detection.Settings;

/// <summary>
/// Fiabilité de la RAM (Microsoft, https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/introduction-to-the-page-file). Classe le fichier d'échange de Windows à partir du
/// REG_MULTI_SZ brut HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PagingFiles :
///   • vide / que des blancs → Off   (fichier d'échange désactivé : un vrai risque de fiabilité ; à
///                                    la limite de validation, Windows manque de mémoire → gel / plantage),
///   • une vraie entrée      → On    (un fichier d'échange est configuré : géré par le système
///                                    « C:\pagefile.sys 0 0 » ou taille personnalisée ; un fichier présent est
///                                    un choix valide, peut-être voulu, qu'on ne remet jamais en cause),
///   • null (illisible)      → Unknown (jamais Off quand on ne peut pas lire).
/// Détection seule : nourrit un constat honnête de fiabilité, pas une promesse de « +FPS », et
/// n'écrit jamais (remettre « géré par le système » est une op WMI, hors du modèle d'ops typées).
/// Encodage de PagingFiles vérifié (« vide = désactivé » n'est donc pas un faux positif) : un
/// fichier « géré par le système » / « géré automatiquement » stocke une entrée sans tailles (par
/// exemple « ?:\pagefile.sys » ou « C:\pagefile.sys 0 0 »), non vide ; un fichier personnalisé est
/// « …sys min max » ; seule la désactivation laisse la valeur vide. Toute entrée non vide ⇒ présent (On).
/// Source : https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/introduction-to-the-page-file
/// </summary>
public static class PagefileInferencer
{
    public static TriState Infer(IReadOnlyList<string>? pagingFiles)
    {
        if (pagingFiles is null) return TriState.Unknown;
        return pagingFiles.Any(entry => !string.IsNullOrWhiteSpace(entry))
            ? TriState.On
            : TriState.Off;
    }
}
