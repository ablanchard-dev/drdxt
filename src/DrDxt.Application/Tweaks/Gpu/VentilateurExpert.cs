using DrDxt.Application.Autopilot;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

/// <summary>
/// Le ventilateur de la carte en mode Expert, sur geste du joueur seulement. Même filet que la
/// fréquence (<see cref="ReglageCarteExpert"/>) : l'essai est noté avant (le démarrage suivant rend l'automatique si
/// DrDXT plante), le bail du helper est armé avant l'écriture (si DrDXT s'arrête, le helper rend l'automatique seul),
/// puis la vitesse est posée. Le helper ne pose que vers le haut (voir NvmlVentilateurActions) ; ici, la borne basse
/// est le plancher à chaud de la liste blanche (40 %).
/// </summary>
public sealed class VentilateurExpert(IElevatedClient elevated, IRevertEnAttente journal)
{
    public const string IdLevier = "gpu.fan_curve";
    public const string IdBail = "gpu-ventilateur";
    public const int MinPourcent = 40, MaxPourcent = 100;

    /// <summary>Un PC fixe à une seule carte NVIDIA : NVML pilote l'index 0, et sur un portable le constructeur pilote le
    /// ventilateur.</summary>
    public static bool Possible(HardwareInventory hw) => ReglageCarteExpert.Possible(hw)
        && hw.Gpus.Count(g => g.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) == 1;

    public async Task<Result<IEssaiCarte>> PoserAsync(int pourcent, int gpuIndex = 0)
    {
        if (pourcent is < MinPourcent or > MaxPourcent)
            return Result<IEssaiCarte>.Fail(ErrorCodes.TweakNotApplicable,
                Docteur.T("GX.VitesseHorsBornes", MinPourcent, MaxPourcent));

        var retour = new RestoreGpuFanAutoOp(gpuIndex);
        var retourJson = PrivilegedOpCodec.Serialize(retour);
        if (!retourJson.IsOk || retourJson.Value is null)
            return Result<IEssaiCarte>.Fail(ErrorCodes.SnapshotInvalid, Docteur.T("GX.RetourAutoIllisible"));
        var snapshot = new TweakSnapshot(IdLevier, retourJson.Value);

        var note = journal.Poser(IdLevier, snapshot);
        if (!note.IsOk)
            return Result<IEssaiCarte>.Fail(note.Error?.Code ?? FichierRevertEnAttente.CodeErreur,
                Docteur.T("GX.SansFilet", note.Error?.Message));

        var bail = await elevated.ExecuteAsync(new EstablishLeaseOp(IdBail, retourJson.Value, ReglageCarteExpert.BailSecondes),
            CancellationToken.None).ConfigureAwait(false);
        if (!bail.IsOk)
        {
            journal.Lever();
            return Result<IEssaiCarte>.Fail(bail.Error?.Code ?? ErrorCodes.HelperOpFailed,
                Docteur.T("GX.BailVentilateur", bail.Error?.Message));
        }
        var battement = new HeartbeatSender(elevated, IdBail, ReglageCarteExpert.BailSecondes);
        battement.Start();

        var pose = await elevated.ExecuteAsync(new ApplyGpuFanCurveOp(gpuIndex, [new FanCurvePointDto(0, pourcent)]),
            CancellationToken.None).ConfigureAwait(false);
        if (!pose.IsOk)
        {
            // Le helper a déjà rendu l'automatique sur un échec ; s'il n'a pas pu, le journal reste et le bail rejouera.
            // Rendu : bail libéré (laissé, il rejouait l'automatique 120 s plus tard, par-dessus ce qui aurait été posé
            // entre-temps). Partiel : il reste, le helper rendra seul.
            if (pose.Error?.Code == ErrorCodes.TweakPartiallyApplied)
                await battement.DisposeAsync().ConfigureAwait(false);
            else
            {
                await battement.LibererAsync().ConfigureAwait(false);
                journal.Lever();
            }
            return Result<IEssaiCarte>.Fail(pose.Error!.Value.Code, pose.Error.Value.Message);
        }
        return Result<IEssaiCarte>.Ok(new EssaiVentilateur(elevated, journal, retour, battement));
    }
}

/// <summary>Une vitesse fixe gardée pour la session. Rendre remet l'automatique (relu par le helper) ; libérer sans
/// rendre laisse le bail expirer, et le helper rend l'automatique de lui-même. Si le helper meurt, sa garde à 80 °C meurt
/// avec lui : au premier battement qui ne l'atteint plus, l'automatique est demandé tout de suite (le helper est relancé
/// pour cela), et le journal n'est levé qu'une fois le retour relu.</summary>
public sealed class EssaiVentilateur : IEssaiCarte
{
    private readonly IElevatedClient _elevated;
    private readonly IRevertEnAttente _journal;
    private readonly RestoreGpuFanAutoOp _retour;
    private readonly HeartbeatSender _bail;

    public EssaiVentilateur(IElevatedClient elevated, IRevertEnAttente journal, RestoreGpuFanAutoOp retour, HeartbeatSender bail)
    {
        (_elevated, _journal, _retour, _bail) = (elevated, journal, retour, bail);
        SuiviDuBail.LeverAuBailPerdu(bail, journal, VentilateurExpert.IdLevier);
        bail.Perdu += () => BailPerdu?.Invoke();
        bail.Injoignable += () => RenduApresPerte = RendreSansHelperAsync();
    }

    /// <summary>Le retour à l'automatique lancé quand le helper ne répondait plus, ou null.</summary>
    public Task? RenduApresPerte { get; private set; }

    public event Action? BailPerdu;

    /// <summary>Le retour à l'automatique n'a pas pu se faire (invite Windows refusée, helper injoignable) : la vitesse fixe
    /// est restée, sans garde. L'échec du retour, que l'écran dit par sa raison lisible (jamais le message du helper) ; aucune
    /// nouvelle invite ne suit.</summary>
    public event Action<Result>? ResteFixe;

    private async Task RendreSansHelperAsync()
    {
        // Une annulation part hors portée de pose : marquée, le helper la rejouerait après un arrêt sec et remettrait le levier.
        using var horsPortee = PorteeDePose.Suspendre();
        Result rendu;
        try { rendu = await _elevated.ExecuteAsync(_retour, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { rendu = Result.Fail(ErrorCodes.HelperUnreachable, ex.Message); }
        // Une seule tentative, la seule à pouvoir ouvrir une invite. Refusée : le journal reste (le prochain lancement de l'App
        // rend l'automatique) et l'écran le dit.
        if (!rendu.IsOk) { ResteFixe?.Invoke(rendu); return; }
        _journal.Lever();
        await _bail.DisposeAsync().ConfigureAwait(false);
        BailPerdu?.Invoke();
    }

    public async Task<Result> RendreAsync()
    {
        // Une annulation part hors portée de pose : marquée, le helper la rejouerait après un arrêt sec et remettrait le levier.
        using var horsPortee = PorteeDePose.Suspendre();
        var rendu = await _elevated.ExecuteAsync(_retour, CancellationToken.None).ConfigureAwait(false);
        if (rendu.IsOk)
        {
            _journal.Lever();
            await _bail.LibererAsync().ConfigureAwait(false);
        }
        return rendu;
    }

    public ValueTask DisposeAsync() => _bail.DisposeAsync();
}
