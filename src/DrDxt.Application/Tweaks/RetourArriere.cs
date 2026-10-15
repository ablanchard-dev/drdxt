using DrDxt.Core;

namespace DrDxt.Application.Tweaks;

/// <summary>
/// Le retour arrière d'un levier à plusieurs écritures, quand l'une d'elles est refusée.
/// Les trois copies d'avant (Vsync et plafond, mode Jeu, X3D) avalaient l'échec d'un inverse : un levier à moitié posé
/// rendait le refus d'origine, l'appelant concluait « rien n'a changé » et levait son journal. Ici, un inverse refusé
/// change le code en <see cref="ErrorCodes.TweakPartiallyApplied"/>, que les appelants lisent pour garder le journal.
/// </summary>
internal static class RetourArriere
{
    /// <summary>Rejoue les inverses déjà posés, du dernier au premier, tous même si l'un échoue, puis rend l'échec.</summary>
    public static async Task<Result<TweakSnapshot>> EchecAsync(IElevatedClient eleve, IReadOnlyList<PrivilegedOp?> posees,
        string code, string message)
    {
        var rendu = true;
        for (var i = posees.Count - 1; i >= 0; i--)
        {
            if (posees[i] is not { } inverse) continue;
            try
            {
                if (!(await eleve.ExecuteAsync(inverse, CancellationToken.None).ConfigureAwait(false)).IsOk) rendu = false;
            }
            catch (Exception) { rendu = false; }
        }
        return rendu
            ? Result<TweakSnapshot>.Fail(code, message)
            : Result<TweakSnapshot>.Fail(ErrorCodes.TweakPartiallyApplied,
                Docteur.T("RA2.RetourEchoue", message));
    }
}
