using DrDxt.Core;

namespace DrDxt.Application.Tweaks;

/// <summary>
/// Le retour arrière d'un levier à plusieurs écritures : sa pose quand l'une d'elles est refusée (<see cref="EchecAsync"/>),
/// et son annulation (<see cref="ToutRendreAsync"/>). Dans les deux cas, chaque inverse est tenté.
/// Les trois copies d'avant (Vsync et plafond, mode Jeu, X3D) avalaient l'échec d'un inverse : un levier à moitié posé
/// rendait le refus d'origine, l'appelant concluait « rien n'a changé » et levait son journal. Ici, un inverse refusé
/// change le code en <see cref="ErrorCodes.TweakPartiallyApplied"/>, que les appelants lisent pour garder le journal.
/// Un « Non » à l'invite de Windows, ou un helper injoignable, ne se redemande pas à chaque inverse : les suivants partent sans
/// geste du joueur (<see cref="SansGesteDuJoueur"/>, aucune invite). Ceux que l'App écrit elle-même (la session) passent
/// encore ; ceux du helper échouent tout de suite, et le premier échec est rendu.
/// </summary>
internal static class RetourArriere
{
    private static bool SansNouvelleInvite(string? code) => code is ErrorCodes.ElevationDeclined or ErrorCodes.HelperUnreachable;

    /// <summary>L'annulation d'un levier à plusieurs écritures : chaque inverse est tenté, même après un refus ou une
    /// exception (s'arrêter au premier laisserait les feuilles suivantes posées), puis le premier échec est rendu, ou Ok.</summary>
    public static async Task<Result> ToutRendreAsync(IElevatedClient eleve, IReadOnlyList<PrivilegedOp> inverses)
    {
        Result? premier = null;
        IDisposable? sansGeste = null;
        try
        {
            foreach (var inverse in inverses)
            {
                Result r;
                try { r = await eleve.ExecuteAsync(inverse, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { r = Result.Fail(ErrorCodes.HelperOpFailed, $"Revert boundary error: {ex.Message}"); }
                if (!r.IsOk) premier ??= r;
                if (sansGeste is null && SansNouvelleInvite(r.Error?.Code)) sansGeste = SansGesteDuJoueur.Ouvrir();
            }
        }
        finally { sansGeste?.Dispose(); }
        return premier ?? Result.Ok();
    }

    /// <summary>Rejoue les inverses déjà posés, du dernier au premier, tous même si l'un échoue, puis rend l'échec.</summary>
    public static async Task<Result<TweakSnapshot>> EchecAsync(IElevatedClient eleve, IReadOnlyList<PrivilegedOp?> posees,
        string code, string message)
    {
        var rendu = true;
        // Ces écritures annulent une pose : elles partent nues, jamais journalisées, donc jamais rejouées.
        IDisposable? sansGeste = SansNouvelleInvite(code) ? SansGesteDuJoueur.Ouvrir() : null;
        try
        {
            using (PorteeDePose.Suspendre())
            {
                for (var i = posees.Count - 1; i >= 0; i--)
                {
                    if (posees[i] is not { } inverse) continue;
                    try
                    {
                        var r = await eleve.ExecuteAsync(inverse, CancellationToken.None).ConfigureAwait(false);
                        if (!r.IsOk) rendu = false;
                        if (sansGeste is null && SansNouvelleInvite(r.Error?.Code)) sansGeste = SansGesteDuJoueur.Ouvrir();
                    }
                    catch (Exception) { rendu = false; }
                }
            }
        }
        finally { sansGeste?.Dispose(); }
        return rendu
            ? Result<TweakSnapshot>.Fail(code, message)
            : Result<TweakSnapshot>.Fail(ErrorCodes.TweakPartiallyApplied,
                Docteur.T("RA2.RetourEchoue", message));
    }
}
