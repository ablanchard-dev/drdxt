namespace DrDxt.Core;

/// <summary>
/// Le motif du limiteur, rendu depuis la clé du classifieur et ses grandeurs, dans la langue du docteur. Le classifieur ne
/// construit pas de phrase pour le joueur : il rend un fait et ses grandeurs, et le formatage des nombres (« 96 % »,
/// « 96% ») appartient au rendu. Clé absente ou inconnue : le motif technique anglais plutôt que rien, pour qu'un motif
/// manquant se voie.
/// </summary>
public static class MotifDuGoulot
{
    public static string Dire(BottleneckVerdict verdict)
    {
        var v = verdict.ReasonValues ?? Array.Empty<double>();
        string Pct(int i) => i < v.Count ? (v[i] * 100).ToString("F0", Docteur.Langue) : "?";
        string Ms(int i) => i < v.Count ? v[i].ToString("F1", Docteur.Langue) : "?";

        return verdict.ReasonKey switch
        {
            BottleneckReasonKeys.NoMeasurement => Docteur.T("Rapport.Motif.AucuneMesure"),
            BottleneckReasonKeys.GpuBusyUnmeasured => Docteur.T("Rapport.Motif.GpuBusyInconnu"),
            BottleneckReasonKeys.CommitPressure => Docteur.T("Rapport.Motif.Commit", Pct(0)),
            BottleneckReasonKeys.Capped => Docteur.T("Rapport.Motif.Plafonne"),
            BottleneckReasonKeys.Throttling => Docteur.T("Rapport.Motif.Bridage"),
            BottleneckReasonKeys.FramePacing => Docteur.T("Rapport.Motif.Cadence"),
            BottleneckReasonKeys.GpuBound => Docteur.T("Rapport.Motif.Gpu", Pct(0)),
            BottleneckReasonKeys.CpuBoundStrong => Docteur.T("Rapport.Motif.CpuFort", Pct(0)),
            BottleneckReasonKeys.CpuOrAppBound => Docteur.T("Rapport.Motif.CpuOuJeu", Pct(0)),
            BottleneckReasonKeys.MixedGpuLeaning => Docteur.T("Rapport.Motif.PlutotGpu", Pct(0)),
            BottleneckReasonKeys.LatencyOnly => Docteur.T("Rapport.Motif.Latence", Ms(0), Ms(1)),
            BottleneckReasonKeys.BetweenStates => Docteur.T("Rapport.Motif.Entre", Pct(0)),
            _ => verdict.Reason,
        };
    }

    /// <summary>L'état du goulot en mots, dans la langue du docteur : le rapport et la raison d'un levier retenu le disent
    /// pareil. Un état ajouté sans libellé se dit par son nom, pour qu'il se voie.</summary>
    public static string Libelle(BottleneckState state) => state switch
    {
        BottleneckState.UnknownPartial => Docteur.T("Rapport.Etat.NonClasse"),
        BottleneckState.GpuBound => Docteur.T("Rapport.Etat.Gpu"),
        BottleneckState.MixedGpuLeaning => Docteur.T("Rapport.Etat.PlutotGpu"),
        BottleneckState.CpuBoundOrAppBound => Docteur.T("Rapport.Etat.CpuOuJeu"),
        BottleneckState.CpuBoundStrong => Docteur.T("Rapport.Etat.CpuNet"),
        BottleneckState.RamCommitPressure => Docteur.T("Rapport.Etat.Memoire"),
        BottleneckState.FramePacing => Docteur.T("Rapport.Etat.Cadence"),
        BottleneckState.LatencyOnly => Docteur.T("Rapport.Etat.Latence"),
        BottleneckState.ThermalOrPowerThrottle => Docteur.T("Rapport.Etat.Bridee"),
        BottleneckState.CappedOrVsyncLimited => Docteur.T("Rapport.Etat.Plafonnee"),
        _ => state.ToString(),
    };
}
