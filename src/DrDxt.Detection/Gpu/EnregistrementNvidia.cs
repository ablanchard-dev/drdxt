using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Detection.Gpu;

/// <summary>
/// L'Instant Replay de l'overlay NVIDIA enregistre en continu
/// les dernières minutes de jeu. Lu sur la machine de référence : allumé (<c>irEnabled</c> dans ShareSettings.json).
/// Coût publié : « environ 5 %, jusqu'à 10 % » selon NVIDIA (repris par PCWorld), 1 à 5 % dans les tests publiés, porté
/// surtout par la carte graphique (encodeur NVENC, copie des images). Sur un jeu limité par le processeur, le coût en
/// images peut être plus faible : la phrase ne promet aucun chiffre pour cette machine, elle dit comment le juger.
/// Lecture seule : DrDXT ne touche pas aux réglages de NVIDIA.
///
/// Écarté le même jour, faute de source : <c>HKCU\Software\NVIDIA Corporation\Ansel\FreestyleEnabled</c> semble porter
/// l'option « Game Filters and Photo Mode » (jusqu'à −15 % mesurés par Tom's Hardware, coupée par défaut depuis la
/// version 11.0.1), mais aucune source ne relie cette clé à l'option. Elle n'est pas lue tant que ce n'est pas prouvé.
/// </summary>
public static class EnregistrementNvidia
{
    public static string CheminParDefaut() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NVIDIA Corporation", "NVIDIA Overlay", "ShareSettings.json");

    /// <summary>Le fichier de réglages de l'overlay NVIDIA, ou <c>null</c> sans overlay ou illisible.</summary>
    public static string? LireReglages(string? chemin = null)
    {
        try
        {
            var c = chemin ?? CheminParDefaut();
            return File.Exists(c) ? File.ReadAllText(c) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Pur : l'état de l'Instant Replay, ou <c>null</c> s'il n'est pas lisible.</summary>
    public static bool? EtatInstantReplay(string? json)
    {
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("settings", out var s) && s.TryGetProperty("video", out var v)
                   && v.TryGetProperty("irEnabled", out var ir) && ir.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? ir.GetBoolean() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Pur. Instant Replay allumé : un constat « à regarder », son coût publié dans la valeur visible. Éteint, absent
    /// ou illisible : rien, l'overlay NVIDIA n'est pas sur toutes les machines et son absence n'est pas une information.</summary>
    public static IEnumerable<RepairFinding> Juger(string? json)
    {
        if (EtatInstantReplay(json) != true) yield break;

        yield return new RepairFinding("GPU DRIVER", "enregistrement continu NVIDIA", Docteur.T("EN.Valeur"), Docteur.T("EN.Attendu"),
            RepairStatus.UserChoiceProbable,
            Docteur.T("EN.Explication"),
            RepairAction.Recommandation,
            Remede: Docteur.T("EN.Remede"));
    }
}
