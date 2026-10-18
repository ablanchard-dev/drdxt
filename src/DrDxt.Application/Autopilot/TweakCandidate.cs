using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Autopilot;

/// <summary>
/// Un levier du produit, vu par l'autopilote.
///
/// Le moteur alterne a-b-b-a sur un <see cref="IAutopilotCandidate"/> synchrone ; le produit ne
/// connait que des <see cref="ITweak"/> asynchrones qui rendent un snapshot. Cet adaptateur fait le
/// pont, sans rien reecrire des leviers.
///
/// Le piege qu'il neutralise. A-b-b-a applique deux fois de suite. Or chaque `ApplyAsync`
/// recapture « l'original » avant d'ecrire : la seconde capture lirait la cible deja posee, et le
/// revert restaurerait la cible au lieu de l'etat de depart du joueur. Un levier deja en place
/// n'est donc jamais reapplique, et un revert sans rien d'applique ne fait rien.
///
/// La relecture passe par <see cref="AppliedStateVerifier"/> : seul `Applied` autorise la mesure,
/// la meme regle que le moteur de production.
/// </summary>
public sealed class TweakCandidate : IAutopilotCandidate
{
    private readonly ITweak _tweak;
    private readonly IRegistryReader _registre;
    private readonly INvApi? _nvApi;
    private readonly IPowerPlanReader? _alimentation;
    private readonly IServiceStateReader? _services;
    private readonly IRevertEnAttente _journal;
    private readonly IDesiredStateStore? _manifeste;
    private TweakSnapshot? _snapshot;

    /// <summary>L'instantané du levier tant qu'il est posé (gardé par la session), pour que l'App
    /// l'inscrive au journal des leviers : sans lui, « Annuler » et la garde d'incident ne le voyaient pas.</summary>
    public TweakSnapshot? SnapshotPose => _snapshot;

    /// <param name="journal">Le filet d'une session tuee : obligatoire. Un candidat sans journal
    /// laisserait son levier pose pour toujours si l'App mourait pendant la mesure.</param>
    /// <param name="manifeste">Le manifeste signe, ou <c>null</c> quand ce processus n'en a pas (l'outil
    /// de campagne). C'est lui que la desinstallation lit : un keep sans lui n'est rendu par personne,
    /// et <see cref="OrigineHorsManifeste"/> le dit.</param>
    public TweakCandidate(ITweak tweak, CandidateDescriptor descriptor, IRegistryReader registre,
        IRevertEnAttente journal, INvApi? nvApi = null, IPowerPlanReader? alimentation = null,
        IDesiredStateStore? manifeste = null, IServiceStateReader? services = null)
    {
        _services = services;
        _manifeste = manifeste;
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _tweak = tweak ?? throw new ArgumentNullException(nameof(tweak));
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        if (!string.Equals(descriptor.Id, tweak.Id, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Descriptor '{descriptor.Id}' does not describe lever '{tweak.Id}'.", nameof(descriptor));
        _registre = registre ?? throw new ArgumentNullException(nameof(registre));
        _nvApi = nvApi;
        _alimentation = alimentation;
    }

    public CandidateDescriptor Descriptor { get; }

    /// <summary>La derniere preuve de relecture : ce que l'ecran montre comme verdict de readback.</summary>
    public AppliedStateEvidence? DernierePreuve { get; private set; }

    public Result Apply()
    {
        if (_snapshot is not null) return Result.Ok();   // deja en place : ne jamais recapturer

        // Le journal avant la machine. Le snapshot n'existe qu'une fois l'ecriture faite (le levier
        // capture et ecrit d'un seul geste) : on pose donc d'abord « essai en cours », puis on le
        // complete. Sans journal, on n'ecrit pas.
        var pose = _journal.Poser(_tweak.Id, null);
        if (!pose.IsOk)
            return Result.Fail(pose.Error?.Code ?? FichierRevertEnAttente.CodeErreur,
                Docteur.T("GX.SansFilet", pose.Error?.Message));

        // Le journal ci-dessus ne connaît pas encore l'origine : le helper la garde jusqu'à ce qu'il la porte.
        using var portee = PorteeDePose.Ouvrir();
        var r = _tweak.ApplyAsync().GetAwaiter().GetResult();
        if (!r.IsOk || r.Value is null)
        {
            // Posé en partie et retour arrière raté : le journal reste (sans origine),
            // et le démarrage suivant dit de vérifier ce réglage, au lieu de croire que rien n'a changé.
            if (r.Error?.Code == ErrorCodes.TweakPartiallyApplied)
                return Result.Fail(ErrorCodes.TweakPartiallyApplied, r.Error?.Message ?? Docteur.T("TC.PoseEnPartie"));
            _journal.Lever();
            return Result.Fail(r.Error?.Code ?? ErrorCodes.TweakSdkUnavailable,
                r.Error?.Message ?? Docteur.T("TC.SansSnapshot"));
        }

        // Le journal doit porter de quoi revenir. S'il ne le peut pas, on ne garde pas un levier pose
        // sans filet : on le rend tout de suite, et l'essai echoue proprement.
        var complete = _journal.Poser(_tweak.Id, r.Value);
        if (!complete.IsOk)
        {
            var rendu = _tweak.RevertAsync(r.Value).GetAwaiter().GetResult();
            if (!rendu.IsOk)
            {
                _snapshot = r.Value;   // garde l'inverse en memoire : Revert() pourra reessayer
                return Result.Fail(complete.Error?.Code ?? FichierRevertEnAttente.CodeErreur,
                    Docteur.T("TC.JournalEtLevier"));
            }
            _journal.Lever();
            return Result.Fail(complete.Error?.Code ?? FichierRevertEnAttente.CodeErreur,
                Docteur.T("TC.JournalLevierRendu"));
        }

        _snapshot = r.Value;
        portee.FermerAsync().GetAwaiter().GetResult();
        return Result.Ok();
    }

    public Result Revert()
    {
        if (_snapshot is not { } s) return Result.Ok();   // rien d'applique : la reference est deja la

        var r = _tweak.RevertAsync(s).GetAwaiter().GetResult();
        // En echec, le snapshot est garde : un nouvel essai de revert reste possible, et oublier
        // l'inverse serait perdre le seul moyen de rendre la machine.
        if (r.IsOk)
        {
            _snapshot = null;
            _journal.Lever();
        }
        return r;
    }

    /// <summary>Le moteur a garde ce levier : il quitte l'essai du journal (le demarrage ne le rendra
    /// plus) et y reste avec sa valeur d'origine. Ecrit apres la decision, en une ecriture atomique ;
    /// tuee avant, la session laisse l'essai arme, et le demarrage rend le levier.</summary>
    public Result Garder()
    {
        if (_snapshot is not { } s)
            return Result.Fail(FichierRevertEnAttente.CodeErreur, Docteur.T("TC.RienAGarder"));

        var garde = _journal.Garder(_tweak.Id, s);
        if (!garde.IsOk) return garde;

        // L'origine entre au manifeste signe, le seul que la desinstallation lit. En
        // `RevertOnly` : rendue a la desinstallation, jamais reaffirmee au demarrage. Son echec ne defait
        // pas le keep (le gain est prouve, le journal local garde l'origine) : il est dit.
        OrigineHorsManifeste = InscrireOrigine(s);
        return garde;
    }

    /// <summary>Pourquoi l'origine du dernier keep n'est pas dans le manifeste signe, ou <c>null</c>
    /// quand elle y est. Non nul = la desinstallation ne rendra pas ce levier ; son origine ne vit que
    /// dans `pending-revert.json` (`Gardes`), que rien ne rejoue.</summary>
    public string? OrigineHorsManifeste { get; private set; }

    /// <summary>Le code du refus, a cote de son message : un manifeste illisible
    /// (<see cref="ErrorCodes.ManifestUnreadable"/>) ouvre la reprise explicite a l'ecran, une panne
    /// passagere non.</summary>
    public string? OrigineHorsManifesteCode { get; private set; }

    private string? InscrireOrigine(TweakSnapshot s)
    {
        OrigineHorsManifesteCode = null;
        if (_manifeste is null) return "no signed manifest in this process";
        try
        {
            // Simplification voulue : synchrone comme les leviers ci-dessus ; l'appelant est hors du fil d'interface.
            var r = _manifeste.CommitAsync(new DesiredState(Array.Empty<AppliedTweak>(), new[]
            {
                new AppliedTweak(_tweak.Id, s.AppliedValueJson ?? string.Empty, s.OriginalValueJson, DateTimeOffset.UtcNow),
            })).GetAwaiter().GetResult();
            if (r.IsOk) return null;
            OrigineHorsManifesteCode = r.Error?.Code;
            return r.Error?.Message ?? "commit refused / commit refuse";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public Result<bool> ReadBack()
    {
        if (_snapshot is not { } s) return Result<bool>.Ok(false);

        DernierePreuve = AppliedStateVerifier.Verifier(s, _registre, _nvApi, _alimentation, _services);
        return Result<bool>.Ok(DernierePreuve.AutoriseLaMesure);
    }
}
