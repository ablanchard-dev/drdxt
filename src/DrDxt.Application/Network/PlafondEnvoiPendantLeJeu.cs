using DrDxt.Core;

namespace DrDxt.Application.Network;

/// <summary>
/// Le plafond d'envoi, pose tout seul quand le jeu tourne et retire quand il se ferme.
///
/// Consentement : ce mode n'existe que si le joueur l'a allume, et seulement apres qu'un scan a
/// mesure son bufferbloat (le plafond vient de ce verdict, jamais d'une valeur par defaut).
///
/// Filet de securite, et il ne depend pas de cette classe : un bail du helper est ouvert au
/// demarrage du mode, avec pour inverse <see cref="RemoveUploadCapOp"/>, et renouvele a chaque
/// battement. Si l'application meurt pendant la partie, le bail expire et le helper retire le
/// plafond lui-meme. Un bail pour toute la duree du mode, pas un par partie : un bail de la
/// partie precedente qui expirerait pendant la suivante retirerait le plafond en plein jeu.
/// Sans compter qu'un redemarrage efface de toute facon les politiques (ActiveStore).
/// </summary>
public sealed class PlafondEnvoiPendantLeJeu
{
    public const string IdBail = "upload-cap";
    public const int BailSecondes = 30;

    private readonly IElevatedClient _helper;
    private readonly Func<string, bool> _jeuEnCours;
    private readonly string _exe;
    private long _plafondBps;
    private bool _pose;
    // Une pose ratée a pu laisser une politique (la générale est créée avant l'exemption du jeu) : l'état est inconnu
    // jusqu'au prochain retrait réussi.
    private bool _poseIncertaine;
    private bool _bailOuvert;

    // Le battement et l'ajustement tournent dans deux boucles. Une pose d'ajustement en vol
    // pouvait arriver au helper apres le retrait du battement (jeu ferme) : plafond en place, `_pose`
    // faux, plus personne pour le retirer. Une operation a la fois, et chacune relit l'etat apres attente.
    private readonly SemaphoreSlim _uneALaFois = new(1, 1);

    public PlafondEnvoiPendantLeJeu(IElevatedClient helper, Func<string, bool> jeuEnCours, string exe, long plafondBps)
        => (_helper, _jeuEnCours, _exe, _plafondBps) = (helper, jeuEnCours, exe, plafondBps);

    // Le battement et l'ajustement tournent sans geste du joueur : ils ne lancent jamais le helper. Mort en pleine partie,
    // il ouvrait une invite Windows à chaque tour, revenue à chaque refus. Injoignable, le filet manque : rien ne se pose.
    // Seuls les gestes du joueur (allumer, éteindre le mode) peuvent le lancer.
    private IElevatedClient Fond => _helper.SansRelance;

    public bool PlafondPose => _pose;

    /// <summary>Le dernier battement n'a pas joint le helper (ni bail renouvelé, ni rouvert) : pas de filet, rien n'est posé.
    /// À dire à l'écran.</summary>
    public bool SansFilet { get; private set; }

    /// <summary>Ouvre le bail. Sans bail, le mode refuse de demarrer : un plafond sans filet
    /// pourrait survivre a un plantage de l'application.</summary>
    public async Task<Result> DemarrerAsync(CancellationToken ct)
    {
        var bail = await OuvrirBailAsync(_helper, ct).ConfigureAwait(false);
        _bailOuvert = bail.IsOk;
        // Un helper tue net emporte ses baux, et sa politique reste jusqu'au redemarrage.
        // Au demarrage, rien n'est pose a notre connaissance : on retire ce qui serait reste (retrait
        // idempotent). Un refus n'arrete pas le mode, le bail tient ; le menage n'est qu'un plus.
        if (bail.IsOk)
        {
            try { await _helper.ExecuteAsync(new RemoveUploadCapOp(), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* ménage facultatif : le bail tient */ }
        }
        return bail;
    }

    private static async Task<Result> OuvrirBailAsync(IElevatedClient client, CancellationToken ct)
    {
        var inverse = PrivilegedOpCodec.Serialize(new RemoveUploadCapOp());
        if (!inverse.IsOk || inverse.Value is null)
            return Result.Fail(ErrorCodes.SnapshotInvalid, "Could not serialize the upload-cap revert.");
        return await client.ExecuteAsync(new EstablishLeaseOp(IdBail, inverse.Value, BailSecondes), ct).ConfigureAwait(false);
    }

    /// <summary>Un battement : renouvelle le bail, pose le plafond si le jeu vient de demarrer,
    /// le retire s'il vient de se fermer. A appeler bien plus souvent que <see cref="BailSecondes"/>.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        await _uneALaFois.WaitAsync(ct).ConfigureAwait(false);
        try { await BattementAsync(ct).ConfigureAwait(false); }
        finally { _uneALaFois.Release(); }
    }

    private async Task BattementAsync(CancellationToken ct)
    {
        if (!_bailOuvert) return;
        // Un battement qui leve ne doit pas tuer la boucle de l'appelant. Le client du
        // helper n'attrape que IO/Timeout/acces, et GetProcessesByName peut lever : la boucle mourrait
        // en silence, l'ecran dirait « actif » et la partie suivante n'aurait plus de plafond. L'etat
        // reste celui d'avant, le battement suivant reessaie. L'annulation, elle, remonte.
        try
        {
            // Le resultat du renouvellement etait ignore. Un helper relance a perdu le
            // bail : on le rouvre. S'il ne se rouvre pas, le filet manque, et la regle de la classe
            // s'applique (pas de plafond sans filet) : le jeu est traite comme absent, rien ne se pose
            // et un plafond deja pose est retire.
            var filet = await Fond.RenewLease(IdBail, BailSecondes, ct).ConfigureAwait(false);
            // Un refus veut dire que l'etat reel est inconnu. Bail expire : le helper a
            // execute l'inverse, le plafond est parti. Helper relance : la politique est restee. On ne
            // se fie plus a `_pose` : jeu lance, on repose (la pose remplace sans empiler) ; jeu ferme,
            // on retire (le retrait est idempotent).
            var etatInconnu = !filet.IsOk;
            if (etatInconnu) filet = await OuvrirBailAsync(Fond, ct).ConfigureAwait(false);
            SansFilet = !filet.IsOk;

            var enJeu = filet.IsOk && _jeuEnCours(_exe);
            if (enJeu && (!_pose || etatInconnu))
            {
                _pose = (await Fond.ExecuteAsync(new ApplyUploadCapOp(_exe, _plafondBps), ct).ConfigureAwait(false)).IsOk;
                _poseIncertaine = !_pose;
            }
            else if (!enJeu && (_pose || _poseIncertaine || etatInconnu))
            {
                _pose = !(await Fond.ExecuteAsync(new RemoveUploadCapOp(), ct).ConfigureAwait(false)).IsOk;
                _poseIncertaine = _pose;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Rien a journaliser ici (la classe n'a pas de journal) : l'etat inchange suffit.
        }
    }

    public long PlafondBps => _plafondBps;

    /// <summary>La boucle fermee change le plafond en pleine partie. Pose : il est
    /// reapplique tout de suite (le helper retire l'ancienne politique avant de poser la nouvelle).
    /// Pas pose : la valeur servira a la prochaine pose. Un refus garde l'ancienne valeur.</summary>
    public async Task<Result> ChangerPlafondAsync(long plafondBps, CancellationToken ct)
    {
        await _uneALaFois.WaitAsync(ct).ConfigureAwait(false);
        try { return await ChangerAsync(plafondBps, ct).ConfigureAwait(false); }
        finally { _uneALaFois.Release(); }
    }

    private async Task<Result> ChangerAsync(long plafondBps, CancellationToken ct)
    {
        if (plafondBps == _plafondBps) return Result.Ok();
        if (!_pose)
        {
            _plafondBps = plafondBps;
            return Result.Ok();
        }
        var r = await Fond.ExecuteAsync(new ApplyUploadCapOp(_exe, plafondBps), ct).ConfigureAwait(false);
        if (r.IsOk) _plafondBps = plafondBps;
        else
        {
            // Une pose ratée a pu laisser une politique : plus « posé », mais état incertain (le retrait partira quand même), et
            // le battement suivant repose l'ancien plafond.
            _pose = false;
            _poseIncertaine = true;
        }
        return r;
    }

    /// <summary>Le joueur eteint le mode : on retire tout de suite. Le bail, lui, expirera et
    /// rejouera un retrait sans effet (le retrait est idempotent, prouve par tools/qossmoke).
    /// Le resultat est celui du retrait, que le helper relit (la politique doit avoir disparu) : un refus
    /// remonte, l'ecran ne dit pas « eteint » sans reserve. Le mode s'eteint quand meme (le battement ne repose plus rien) ;
    /// le retrait refuse reste a faire : un helper injoignable ne rejoue pas non plus celui du bail, l'arret suivant reessaie.</summary>
    public async Task<Result> ArreterAsync(CancellationToken ct)
    {
        await _uneALaFois.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_pose && !_poseIncertaine && !_bailOuvert) return Result.Ok();
            _bailOuvert = false;
            Result r;
            try { r = await _helper.ExecuteAsync(new RemoveUploadCapOp(), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { r = Result.Fail(ErrorCodes.HelperOpFailed, ex.Message); }
            _pose = !r.IsOk;
            _poseIncertaine = !r.IsOk;
            return r;
        }
        finally { _uneALaFois.Release(); }
    }

    /// <summary>Le jeu tel que Windows le voit : un processus de ce nom est-il vivant ?</summary>
    public static bool ProcessusVivant(string exe)
    {
        var nom = Path.GetFileNameWithoutExtension(exe);
        var procs = System.Diagnostics.Process.GetProcessesByName(nom);
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }
}
