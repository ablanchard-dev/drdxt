using DrDxt.Core;

namespace DrDxt.Application.Safety;

// -----------------------------------------------------------------------
// SafetyManager : la garde de réversibilité.
//
// EnsureRestorePointAsync : rien n'est jamais posé sans point de restauration vérifié.
//    Si la restauration est coupée et ne peut pas être activée (ou si l'utilisateur
//    refuse), on bloque, jamais d'avance silencieuse. Le point est créé par le helper
//    élevé via un CreateRestorePointOp typé, puis on vérifie qu'il existe vraiment (une
//    GPO ou l'édition peut annuler la création en silence). Limité à un point par
//    session ou lot, pour ne jamais en créer en trop.
//
// RevertAsync : reconstruit la PrivilegedOp inverse typée à partir de l'OriginalValueJson
//    typé du snapshot (la forme filaire du codec, qui porte la vraie valeur d'origine
//    relevée) et la renvoie par le client élevé. Le chemin générique bas niveau couvre les
//    retours de registre et de plan d'alimentation ; les retours propres à un levier
//    (par exemple une courbe V/F du GPU) vivent dans ITweak.RevertAsync.
//
// Chaque frontière externe est protégée → Result.Fail. Aucune exception ne sort jamais.
// -----------------------------------------------------------------------
public sealed class SafetyManager : ISafetyManager
{
    /// <summary>Une création sans point relu est retentée une fois. VSS échoue par
    /// intermittence (8193) et réussit à l'essai suivant (mesuré deux fois sur la machine de référence).</summary>
    public const int Creations = 2;

    /// <summary>La fenetre ou Windows refuse de creer un nouveau point : 24 h par defaut
    /// (Microsoft Learn, SRSetRestorePointW, cle SystemRestorePointCreationFrequency absente). Un point de
    /// cette fenetre est accepte et dit, plutot que de forcer la frequence a 0 : cela evincerait les points
    /// plus anciens de l'utilisateur (audit adverse du projet, non publie). Au-dela : on bloque.</summary>
    public static readonly TimeSpan FenetreDuRefusDeWindows = TimeSpan.FromHours(24);

    public DateTime? PointReutiliseUtc { get; private set; }

    private readonly IRestorePointService _restore;
    private readonly IConsentPrompt _consent;
    private readonly IElevatedClient _elevated;

    // Limite : dès qu'un point vérifié existe pour cette session ou ce lot, on n'en crée
    // pas d'autre (« un point par session ou lot »).
    private bool _pointEnsured;

    /// <summary>Lectures de la liste des points avant de conclure qu'aucun n'existe.</summary>
    public const int LecturesDuPoint = 8;

    /// <summary>**Non chiffré** au-delà de la mesure : sur la machine de référence, un point WMI est listé dès la fin de l'appel
    /// (9 à 19 s) ; 7 attentes de 3 s laissent 21 s de plus avant de dire « pas de point ».</summary>
    internal static readonly TimeSpan EntreDeuxLectures = TimeSpan.FromSeconds(3);

    private readonly Func<TimeSpan, Task> _attendre;

    public SafetyManager(IRestorePointService restore, IConsentPrompt consent, IElevatedClient elevated,
        Func<TimeSpan, Task>? attendre = null)
    {
        _restore = restore;
        _consent = consent;
        _elevated = elevated;
        _attendre = attendre ?? (d => Task.Delay(d));
    }

    // La demande en cours : « Optimiser » et le réglage de la carte lancés ensemble attendent le même point, au lieu
    // d'en créer deux.
    private readonly Lock _verrou = new();
    private Task<Result>? _enCours;

    public Task<Result> EnsureRestorePointAsync()
    {
        lock (_verrou)
            return _enCours is { IsCompleted: false } enCours ? enCours : _enCours = Assurer();
    }

    private async Task<Result> Assurer()
    {
        if (_pointEnsured)
            return Result.Ok();

        try
        {
            // Les lectures WMI et l'activation partent sur le pool : l'appelant est le fil de l'interface. La question au
            // joueur, elle, revient sur son fil (ConfigureAwait(true)) pour garder sa fenêtre propriétaire.
            if (!await Task.Run(_restore.IsEnabled).ConfigureAwait(true))
            {
                // Interdite par stratégie : demander au joueur ne mènerait à rien, la raison se dit.
                if (_restore.InterditeParStrategie())
                    return Result.Fail(ErrorCodes.RestorePolicy, "System Restore is turned off by a Windows policy (DisableSR).");
                // Restauration coupée : on ne peut avancer qu'en l'activant avec un accord explicite.
                // Pas d'accord → refusé ; accord mais activation impossible → désactivée.
                if (!_consent.AskEnableRestore())
                    return Result.Fail(ErrorCodes.RestoreDeclined,
                        "System Restore is disabled and the user declined enabling it.");

                if (!await Task.Run(_restore.TryEnable).ConfigureAwait(false))
                    return Result.Fail(ErrorCodes.RestoreDisabled,
                        "System Restore could not be enabled (GPO/SKU/locked).");
            }

            // Un point de moins de
            // 24 h, de DrDXT ou de Windows, protège déjà ; on le réutilise et l'écran le dit.
            var demandeUtc = DateTime.UtcNow;
            if (await Task.Run(() => _restore.DernierPointDepuis(demandeUtc - FenetreDuRefusDeWindows)).ConfigureAwait(false) is { } recent)
            {
                PointReutiliseUtc = recent;
                _pointEnsured = true;
                return Result.Ok();
            }

            // Crée le point par le chemin élevé typé et sur liste blanche, sous le nom fixe de DrDXT.
            // Le retour de Windows ne prouve pas le point (plan 4.5) : la liste est relue avant de conclure.
            // L'échec VSS intermittent remonte en refus ; il est retenté lui aussi.
            DateTime? trouve = null;
            string? dernierRefus = null;
            for (var essai = 0; essai < Creations && trouve is null; essai++)
            {
                var create = await _elevated
                    .ExecuteAsync(new CreateRestorePointOp(CreateRestorePointOp.NomDrDxt), CancellationToken.None)
                    .ConfigureAwait(false);
                if (!create.IsOk)
                {
                    dernierRefus = create.Error?.Message ?? create.Error?.Code ?? "?";
                    continue;
                }
                dernierRefus = null;

                for (var i = 0; i < LecturesDuPoint && trouve is null; i++)
                {
                    if (i > 0) await _attendre(EntreDeuxLectures).ConfigureAwait(false);
                    trouve = _restore.DernierPointDepuis(demandeUtc - FenetreDuRefusDeWindows);
                }
            }
            if (trouve is not { } point)
                return dernierRefus is not null
                    ? Result.Fail(ErrorCodes.RestoreFailed, dernierRefus)
                    : Result.Fail(ErrorCodes.RestoreNotVerified, $"no restore point read back after {Creations} attempts");
            PointReutiliseUtc = point < demandeUtc ? point : null;

            _pointEnsured = true;
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.RestoreFailed, $"Restore-point boundary error: {ex.Message}");
        }
    }

    public async Task<Result> RevertAsync(TweakSnapshot snapshot)
    {
        // Une annulation part hors portée de pose : marquée, le helper la rejouerait après un arrêt sec et remettrait le levier.
        using var horsPortee = PorteeDePose.Suspendre();
        // Validation prudente du JSON reçu : un snapshot malformé ou de type inconnu ne doit
        // jamais lever ni lancer une op arbitraire ; échec propre avec SAFETY.SNAPSHOT_INVALID.
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.OriginalValueJson))
            return Result.Fail(ErrorCodes.SnapshotInvalid, "Snapshot has no original-value payload.");

        Result<PrivilegedOp> decoded;
        try
        {
            decoded = PrivilegedOpCodec.Deserialize(snapshot.OriginalValueJson);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.SnapshotInvalid, $"Snapshot decode error: {ex.Message}");
        }

        if (!decoded.IsOk || decoded.Value is null)
            return Result.Fail(ErrorCodes.SnapshotInvalid,
                "Snapshot original value is not a known typed privileged op.");

        try
        {
            // Renvoie l'op d'origine relevée par le client élevé pour remettre exactement la valeur
            // d'avant. Le résultat du helper est rendu tel quel.
            return await _elevated.ExecuteAsync(decoded.Value, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.HelperOpFailed, $"Revert boundary error: {ex.Message}");
        }
    }
}
