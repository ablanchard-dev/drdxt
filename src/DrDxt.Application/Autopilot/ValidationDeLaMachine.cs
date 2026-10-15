using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Autopilot;

/// <summary>
/// La validation
/// complète sur la vraie machine, branchée sur les leviers et les lecteurs du produit. Le banc AMD et l'App l'appellent
/// tous les deux. Chaque levier est relu par ce qui le lit vraiment : la carte pour la limite de puissance et les réglages
/// du pilote AMD, le registre, le plan d'alimentation ou le pilote NVIDIA pour le reste (<see cref="AppliedStateVerifier"/>).
/// </summary>
public sealed class ValidationDeLaMachine(
    ITweakCatalog catalogue, IHardwareDetector detecteur, ISettingsStateReader etat,
    IRegistryReader registre, INvApi nvApi, IPowerPlanReader alimentation,
    IAdlxGpuTuning adlxCarte, IAdlx latenceAmd, INvApiGpuTuning nvCarte)
{
    public Task<ValidationComplete.Remise> RemettreAsync(IRevertEnAttente journal)
        => ValidationComplete.RemettreAsync(journal, id => catalogue.Get(id) is { IsOk: true, Value: { } t } ? t : null, RevertAuDemarrage.InverseRejouable);

    public async Task<ValidationComplete.Resultat> ValiderAsync(IRevertEnAttente journal, Action<string>? ecrireBloc = null,
        Func<bool>? jeuOuvert = null)
    {
        var materiel = await detecteur.DetectAsync().ConfigureAwait(false);
        if (!materiel.IsOk) return new(Docteur.T("VM.Detection", materiel.Error?.Message), [Docteur.T("VM.Detection.RienEcrit")]);
        var lu = await etat.ReadAsync(materiel.Value!).ConfigureAwait(false);
        if (!lu.IsOk) return new(Docteur.T("VM.Etat", lu.Error?.Message), [Docteur.T("VM.Etat.RienEcrit")]);

        var leviers = ValidationComplete.Leviers
            .Select(id => catalogue.Get(id) is { IsOk: true, Value: { } t } ? t : null)
            .OfType<ITweak>().ToList();
        return await ValidationComplete.ValiderAsync(leviers, materiel.Value!, lu.Value!, journal, Relire, ecrireBloc, jeuOuvert)
            .ConfigureAwait(false);
    }

    /// <summary>Ce que la machine porte maintenant, face à l'origine gardée dans le snapshot.</summary>
    public ValidationComplete.Lecture Relire(TweakSnapshot s)
    {
        // La limite de puissance et Anti-Lag ne portent que leur inverse : relus par la carte, comparés à l'origine.
        if (PrivilegedOpCodec.Deserialize(s.OriginalValueJson) is { IsOk: true, Value: var op })
        {
            var (nom, origine, valeur) = op switch
            {
                ApplyAdlxTuningOp { Kind: AdlxTuningKind.PowerLimit } a => (Docteur.T("VM.LimitePuissance"), a.IntValue, adlxCarte.GetPowerLimitPct(a.GpuIndex)),
                ApplyAdlxTuningOp { Kind: AdlxTuningKind.AntiLag } a => ("Anti-Lag (0/1)", a.IntValue, latenceAmd.GetAntiLagState()),
                ApplyAdlxTuningOp { Kind: AdlxTuningKind.FrameRateTarget } a => (Docteur.T("VM.Frtc"), a.IntValue, latenceAmd.GetFrameRateTarget()),
                ApplyNvApiPowerLimitOp n => (Docteur.T("VM.LimitePuissance"), n.PowerLimitPercent, nvCarte.GetPowerLimitPct(n.GpuIndex)),
                _ => ("", 0, (Result<int>?)null),
            };
            if (valeur is not null)
                return valeur.IsOk
                    ? new(valeur.Value != origine, Docteur.T("VM.OrigineLu", nom, origine, valeur.Value))
                    : new(null, Docteur.T("VM.Illisible", nom, valeur.Error?.Message));
        }
        // Un composite de réglages AMD (Vsync du pilote + FRTC) : chaque feuille relue par la carte, comparée à son
        // origine. Posé = toutes ont bougé ; rendu = toutes sont à l'origine ; sinon, non conclu.
        if (CompositeSnapshotCodec.Decode(s.OriginalValueJson) is { IsOk: true, Value: { Count: > 0 } feuilles }
            && feuilles.All(f => f is ApplyAdlxTuningOp))
        {
            var lues = feuilles.Cast<ApplyAdlxTuningOp>().Select(f => (f.Kind, Origine: f.IntValue, Lu: f.Kind switch
            {
                AdlxTuningKind.VerticalRefresh => latenceAmd.GetVsyncMode(),
                AdlxTuningKind.FrameRateTarget => latenceAmd.GetFrameRateTarget(),
                _ => Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, Docteur.T("VM.GenreNonRelu")),
            })).ToList();
            var texte = string.Join(" ; ", lues.Select(l => Docteur.T("VM.OrigineLu", l.Kind, l.Origine, l.Lu.IsOk ? l.Lu.Value.ToString() : Docteur.T("VM.IllisibleMot"))));
            if (lues.Any(l => !l.Lu.IsOk)) return new(null, texte);
            if (lues.All(l => l.Lu.Value != l.Origine)) return new(true, texte);
            if (lues.All(l => l.Lu.Value == l.Origine)) return new(false, texte);
            return new(null, texte + Docteur.T("VM.EnPartie"));
        }
        var preuve = AppliedStateVerifier.Verifier(s, registre, nvApi, alimentation);
        bool? pose = preuve.Verdict switch { EtatApplique.Applied => true, EtatApplique.NotApplied => false, _ => null };
        return new(pose, Docteur.T("VM.Preuve", preuve.Verdict, preuve.OriginalState ?? Docteur.T("TE.Absente"), preuve.RequestedState ?? "?", preuve.ObservedState ?? Docteur.T("TE.Absente")));
    }
}
