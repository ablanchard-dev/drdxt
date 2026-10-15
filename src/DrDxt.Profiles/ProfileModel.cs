using DrDxt.Core;

namespace DrDxt.Profiles;

// -----------------------------------------------------------------------
// Profils d'optimisation par puce (« réparer d'abord » ; la carte se règle par sa limite de
// puissance, jamais par sa tension).
//
// Un profil est un ensemble nommé, visant une puce, de sélections de leviers (avec leurs
// paramètres). Il ne pose rien et ne détient aucune primitive privilégiée : c'est la couche
// de recommandation, établie et auditable, devant le catalogue et le moteur. Chaque
// sélection renvoie à un levier existant du catalogue et porte sa provenance : tout effet
// annoncé cite une mesure ou une fourchette sourcée, avec une confiance et un niveau de
// sûreté. Aucun chiffre inventé (« ne jamais inventer de problème » ; « on prouve, on ne
// promet pas » : le vrai écart est prouvé par le re-banc A/B du moteur, jamais par cette table).
//
// Données pures : ni entrée-sortie, ni injection, testable unitairement. Les valeurs sont
// prudentes et restent soumises au recadrage de GpuSafeBounds en aval : un profil est une
// demande, pas une autorisation (même contrat que GpuTuningTarget).
// -----------------------------------------------------------------------

/// <summary>À quel point un effet annoncé est fondé : l'échelle d'honnêteté.
/// <list type="bullet">
/// <item><b>Measured</b> : observé dans un A/B sur du vrai matériel (noter l'échantillon).</item>
/// <item><b>Sourced</b> : fourchette publiée (banc ou recherche), citée dans Source.</item>
/// <item><b>Inferred</b> : heuristique prudente, pas un chiffre (rare).</item>
/// </list></summary>
public enum GainConfidence { Measured, Sourced, Inferred }

/// <summary>Sûreté d'une sélection à la pose (réglage agressif = Expert ou laboratoire).
/// Les profils de base sont tous <see cref="Safe"/> : dans les bornes, voies prises en charge,
/// entièrement réversibles.</summary>
public enum SafetyLevel { Safe, Moderate, Expert }

/// <summary>À quoi sert une sélection. Pilote l'ordre « réparer d'abord » (réparation avant
/// réglage) et le regroupement dans l'interface. L'ordre de l'énumération est la priorité de
/// pose : FixMisconfig → Latency → GpuTune.</summary>
public enum ProfileIntent { FixMisconfig, Latency, GpuTune }

/// <summary>Un paramètre nommé d'un levier (par exemple <c>CoreOffsetMhz = 75</c>). Se reporte
/// sur les cibles de réglage à la couture de consommation (<c>GpuTuningTarget.CoreOffsetMhz</c>,
/// <c>GpuPowerLimitTarget.PowerLimitPct</c>). Entiers seulement pour l'instant ; toujours
/// recadrés par GpuSafeBounds avant toute écriture.</summary>
public sealed record ProfileParameter(string Key, int Value, string Unit);

/// <summary>La provenance d'un effet annoncé : une description prudente, sans promesse, la
/// source exacte du dépôt dont il vient, sa confiance et sa sûreté. <see cref="Claim"/> ne doit
/// jamais se lire comme une promesse de FPS et <see cref="Source"/> jamais être vide
/// (vérifié par des tests).</summary>
public sealed record GainEvidence(
    string Claim,
    string Source,
    GainConfidence Confidence,
    SafetyLevel Safety);

/// <summary>La sélection d'un levier du catalogue par un profil : son identifiant, son but,
/// ses paramètres et la provenance du gain annoncé.</summary>
public sealed record ProfileTweakSelection(
    string TweakId,
    ProfileIntent Intent,
    IReadOnlyList<ProfileParameter> Parameters,
    GainEvidence Evidence);

/// <summary>La famille de puce visée par un profil. Prudente à dessein : la base
/// <see cref="GenericSafe"/> existe toujours ; les familles par puce ne s'ajoutent que quand
/// la détection est certaine (détection universelle, profils par vagues).</summary>
public enum ChipFamily { GenericSafe, AmdX3dDualCcd, NvidiaOptionA }

/// <summary>Un ensemble nommé de sélections de leviers visant une puce.
/// <see cref="Tested"/> dit si ce profil a été validé sur du vrai matériel (« profils testés
/// déployés par vagues ») ; un profil de base non validé reste recommandable, mais
/// l'interface le marque « non vérifié ».</summary>
public sealed record ChipProfile(
    string Id,
    string NameKey,
    ChipFamily Family,
    IReadOnlyList<ProfileTweakSelection> Selections,
    bool Tested,
    string Summary)
{
    /// <summary>Les identifiants de leviers du profil, réparations d'abord. Sert à composer un
    /// TweakPlan ; vérifié contre le vrai catalogue par des tests.</summary>
    public IReadOnlyList<string> OrderedTweakIds =>
        Selections
            .OrderBy(s => (int)s.Intent)
            .Select(s => s.TweakId)
            .ToList();
}
