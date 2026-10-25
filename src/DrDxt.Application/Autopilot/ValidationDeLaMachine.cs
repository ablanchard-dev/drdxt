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
    IAdlxGpuTuning adlxCarte, IAdlx latenceAmd, INvApiGpuTuning nvCarte, Func<string?>? levierDeLEssai = null,
    ICpuPowerLimitReader? cpu = null, IServiceStateReader? services = null)
{
    // L'essai en alternance en cours (App et banc lisent le même fichier) : son levier n'est pas touché.
    private readonly Func<string?> _levierDeLEssai = levierDeLEssai ?? (() => Parties.MemoireDeLEssai.ParDefaut().Lire()?.LevierId);

    public Task<ValidationComplete.Remise> RemettreAsync(IRevertEnAttente journal)
        => ValidationComplete.RemettreAsync(journal, id => catalogue.Get(id) is { IsOk: true, Value: { } t } ? t : null, RevertAuDemarrage.InverseRejouable,
            registre, Verdict);

    /// <param name="profond">Le banc profond : un levier déjà réglé qui sait s'exercer est mis hors de sa cible, posé, puis
    /// rendu à son origine exacte (<see cref="ITweak.PreparerExercice"/>).</param>
    public Task<ValidationComplete.Resultat> ValiderAsync(IRevertEnAttente journal, Action<string>? ecrireBloc = null,
        Func<bool>? jeuOuvert = null, Func<bool>? arretDemande = null, bool profond = false)
        => ValiderAsync(ValidationComplete.Leviers
            .Select(id => catalogue.Get(id) is { IsOk: true, Value: { } t } ? t : null)
            .OfType<ITweak>().ToList(), journal, ecrireBloc, jeuOuvert, arretDemande, profond);

    /// <summary>La fréquence maximale d'une carte AMD, geste à part de <see cref="ValiderAsync(IRevertEnAttente, Action{string}?, Func{bool}?, Func{bool}?, bool)"/>,
    /// qui n'écrit jamais de fréquence : une baisse posée, relue, rendue, relue (<see cref="FrequenceAmdDuBanc"/>).</summary>
    public Task<ValidationComplete.Resultat> ValiderFrequenceAsync(IRevertEnAttente journal, Action<string>? ecrireBloc = null,
        Func<bool>? jeuOuvert = null)
        => ValiderAsync(catalogue.Get("gpu.undervolt") is { IsOk: true, Value: Tweaks.Gpu.GpuUndervoltTweak levier }
            ? [new FrequenceAmdDuBanc(levier)] : [], journal, ecrireBloc, jeuOuvert, arretDemande: null);

    private async Task<ValidationComplete.Resultat> ValiderAsync(IReadOnlyList<ITweak> leviers, IRevertEnAttente journal,
        Action<string>? ecrireBloc, Func<bool>? jeuOuvert, Func<bool>? arretDemande, bool profond = false)
    {
        var materiel = await detecteur.DetectAsync().ConfigureAwait(false);
        if (!materiel.IsOk) return new(Docteur.T("VM.Detection", materiel.Error?.Message), [Docteur.T("VM.Detection.RienEcrit")]);
        var lu = await etat.ReadAsync(materiel.Value!).ConfigureAwait(false);
        if (!lu.IsOk) return new(Docteur.T("VM.Etat", lu.Error?.Message), [Docteur.T("VM.Etat.RienEcrit")]);
        var levierDeLEssai = _levierDeLEssai();
        return await ValidationComplete.ValiderAsync(leviers, materiel.Value!, lu.Value!, journal, Relire, ecrireBloc, jeuOuvert, arretDemande,
            tenuParUnEssai: id => id == levierDeLEssai, profond: profond).ConfigureAwait(false);
    }

    /// <summary>Les lecteurs de la carte et du processeur, ceux du vérificateur : une seule définition de leur lecture.</summary>
    public AppliedStateVerifier.LecteursCarte Carte => new(nvCarte, adlxCarte, latenceAmd, cpu);

    /// <summary>Le verdict d'une pose, relue par ce qui lit vraiment chaque réglage (l'essai en alternance s'en sert).</summary>
    public EtatApplique Verdict(TweakSnapshot s) => AppliedStateVerifier.Verifier(s, registre, nvApi, alimentation, services, Carte).Verdict;

    /// <summary>Ce que la machine porte maintenant, face à l'origine gardée dans le snapshot.</summary>
    public ValidationComplete.Lecture Relire(TweakSnapshot s)
    {
        // Un réglage de la carte qui ne porte que son inverse (le banc de fréquence) : relu par la carte, comparé à l'origine.
        if (string.IsNullOrWhiteSpace(s.AppliedValueJson))
        {
            if (PrivilegedOpCodec.Deserialize(s.OriginalValueJson) is { IsOk: true, Value: { } op }
                && AppliedStateVerifier.ValeurDeCarte(op) is { } origine && AppliedStateVerifier.LecteurDeCarte(op, Carte) is { } lire)
            {
                var nom = AppliedStateVerifier.NomDeCarte(op);
                var valeur = lire();
                return valeur.IsOk
                    ? new(valeur.Value != origine, Docteur.T("VM.OrigineLu", nom, origine, valeur.Value))
                    : new(null, Docteur.T("VM.Illisible", nom, valeur.Error?.Message));
            }
            // Un composite de réglages AMD (Vsync du pilote + FRTC) qui ne porte que ses inverses : chaque feuille relue
            // par la carte. Posé = toutes ont bougé ; rendu = toutes sont à l'origine ; sinon, non conclu.
            if (CompositeSnapshotCodec.Decode(s.OriginalValueJson) is { IsOk: true, Value: { Count: > 0 } feuilles }
                && feuilles.All(f => f is ApplyAdlxTuningOp))
            {
                var lues = feuilles.Select(f => (Nom: AppliedStateVerifier.NomDeCarte(f), Origine: AppliedStateVerifier.ValeurDeCarte(f),
                    Lu: AppliedStateVerifier.LecteurDeCarte(f, Carte) is { } l ? l() : Result<long>.Fail(ErrorCodes.TweakSdkUnavailable, Docteur.T("VM.GenreNonRelu")))).ToList();
                var texte = string.Join(" ; ", lues.Select(l => Docteur.T("VM.OrigineLu", l.Nom, l.Origine, l.Lu.IsOk ? l.Lu.Value.ToString() : Docteur.T("VM.IllisibleMot"))));
                if (lues.Any(l => !l.Lu.IsOk)) return new(null, texte);
                if (lues.All(l => l.Lu.Value != l.Origine)) return new(true, texte);
                if (lues.All(l => l.Lu.Value == l.Origine)) return new(false, texte);
                return new(null, texte + Docteur.T("VM.EnPartie"));
            }
        }
        var preuve = AppliedStateVerifier.Verifier(s, registre, nvApi, alimentation, services, Carte);
        bool? pose = preuve.Verdict switch { EtatApplique.Applied => true, EtatApplique.NotApplied => false, _ => null };
        // Une valeur à supprimer (l'origine « absente » d'un exercice) n'a pas de valeur demandée : elle se dit supprimée.
        var supprimee = PrivilegedOpCodec.Deserialize(s.AppliedValueJson ?? "") is { IsOk: true, Value: DeleteRegistryValueOp };
        var demande = preuve.RequestedState ?? (supprimee ? Docteur.T("VM.Supprimee") : "?");
        var observe = preuve.Verdict is EtatApplique.Unknown or EtatApplique.Unavailable ? null : preuve.ObservedState ?? Docteur.T("TE.Absente");
        return new(pose, Docteur.T("VM.Preuve", preuve.Verdict, preuve.OriginalState ?? Docteur.T("TE.Absente"), demande,
            preuve.ObservedState ?? Docteur.T("TE.Absente")), observe);
    }
}
