using System.Globalization;
using DrDxt.Application.Parties;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// Les heures d'activité de Windows Update réglées sur les heures de jeu relevées.
//
// Microsoft (Learn, « Manage device restarts after updates ») : « Automatic restarts after an update occur outside of the
// active hours », 18 h au plus. Relevé le 06/10/2026 sur la machine de référence : un redémarrage de Windows Update à
// 10:15, hors de ses heures d'activité (15 h à 8 h), et sa seule partie relevée (8:23) elle aussi hors de ces heures.
//
// Ce qui est écrit : le réglage de l'utilisateur (Paramètres > Windows Update > Heures d'activité), deux DWORD de
// HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings, par le helper (deux feuilles exactes). Pas la stratégie : elle
// verrouille l'écran de Paramètres et Microsoft la range côté entreprise. L'emplacement du réglage utilisateur n'est pas
// documenté par Microsoft (seule la stratégie l'est, Policy CSP Update) : le constat suivant le relit.
//
// Jamais : désactiver Windows Update, reporter une mise à jour. Aucun gain d'images : c'est un levier de confort, posé au
// clic et jugé sur les redémarrages tombés pendant une partie (le constat du docteur), pas en session mesurée.
// -----------------------------------------------------------------------
/// <summary>Règle les heures d'activité de Windows Update sur les heures de jeu relevées, pour qu'un redémarrage automatique
/// ne tombe pas en pleine partie. Proposé quand elles sont lues en mode manuel et que les parties en demandent d'autres.</summary>
public sealed class HeuresActivesTweak : ITweak
{
    private readonly IRegistryReader _registry;
    private readonly IElevatedClient _elevated;
    private readonly Func<IReadOnlyList<PlageDeJeu>> _parties;
    private readonly TimeZoneInfo _fuseau;

    public HeuresActivesTweak(IRegistryReader registry, IElevatedClient elevated,
        Func<IReadOnlyList<PlageDeJeu>>? parties = null, TimeZoneInfo? fuseau = null)
    {
        (_registry, _elevated) = (registry, elevated);
        _parties = parties ?? (() => PartiesMemorisees.ParDefaut().Lire().Select(p => new PlageDeJeu(p.Debut, p.Fin)).ToList());
        _fuseau = fuseau ?? TimeZoneInfo.Local;
    }

    public string Id => "fixit.heures_actives";
    public TweakCategory Category => TweakCategory.FixIt;
    public bool RequiresElevation => true;
    public bool RequiresRestorePoint => true;
    public string SummaryKey => "tweak.fixit.heures_actives.summary";

    /// <summary>Seulement quand des parties sont relevées, qu'elles sortent des heures lues, que Windows ne les règle pas
    /// tout seul, et qu'une plage de 18 h au plus les couvre. Non lu ne déclenche rien.</summary>
    public bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        // Seulement « manuel » LU. Absent, Windows peut les régler seul et réécrire ce que
        // DrDXT poserait : « posé » à l'écran, effet nul. Sur la machine de référence la valeur est absente (15 h - 8 h).
        state.HeuresActives is { Automatique: false } h && state.HeuresDeJeu is { } parties
        && HeuresActives.Proposer(h, parties, _fuseau) is not null;

    public bool EtatNonLu(SettingsState state) => state.HeuresActives is null || state.HeuresDeJeu is null;

    public async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // L'original, relu juste avant d'écrire : c'est lui que l'annulation rend (absent ⇒ supprimé).
            var debut = _registry.ReadDword(RegistryHiveKind.LocalMachine, HeuresActives.Cle, HeuresActives.NomDebut);
            var fin = _registry.ReadDword(RegistryHiveKind.LocalMachine, HeuresActives.Cle, HeuresActives.NomFin);
            var auto = _registry.ReadDword(RegistryHiveKind.LocalMachine, HeuresActives.Cle, HeuresActives.NomAutomatique);
            if (!debut.IsOk || !fin.IsOk || !auto.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not read the current active hours.");
            if (HeuresActives.Depuis((int?)debut.Value, (int?)fin.Value, (int?)auto.Value) is not { Automatique: false } lues
                || HeuresActives.Proposer(lues, _parties(), _fuseau) is not { } cible)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Active hours are unread, automatic, or already cover the recorded games: nothing to write.");

            // Les deux heures ont été lues (sinon `lues` est null) : l'annulation les réécrit telles quelles.
            var inverses = new List<PrivilegedOp> { Poser(HeuresActives.NomDebut, lues.Debut), Poser(HeuresActives.NomFin, lues.Fin) };
            var original = CompositeSnapshotCodec.Encode(inverses);
            if (!original.IsOk || original.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert ops.");

            var avants = new List<PrivilegedOp> { Poser(HeuresActives.NomDebut, cible.Debut), Poser(HeuresActives.NomFin, cible.Fin) };
            for (var i = 0; i < avants.Count; i++)
            {
                var res = await _elevated.ExecuteAsync(avants[i], CancellationToken.None).ConfigureAwait(false);
                if (!res.IsOk)
                {
                    // Une moitié écrite ne reste pas : la première valeur est rendue.
                    for (var j = i - 1; j >= 0; j--)
                        await _elevated.ExecuteAsync(inverses[j], CancellationToken.None).ConfigureAwait(false);
                    return Result<TweakSnapshot>.Fail(res.Error!.Value.Code, res.Error!.Value.Message);
                }
            }
            var posees = CompositeSnapshotCodec.Encode(avants);
            return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, original.Value, posees.IsOk ? posees.Value : null));
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, $"Active hours apply boundary error: {ex.Message}");
        }
    }

    public async Task<Result> RevertAsync(TweakSnapshot snapshot)
    {
        var ops = CompositeSnapshotCodec.Decode(snapshot?.OriginalValueJson);
        if (!ops.IsOk || ops.Value is null)
            return Result.Fail(ErrorCodes.SnapshotInvalid, "Active hours snapshot is not a known composite of typed ops.");
        foreach (var op in ops.Value)
        {
            var res = await _elevated.ExecuteAsync(op, CancellationToken.None).ConfigureAwait(false);
            if (!res.IsOk) return res;
        }
        return Result.Ok();
    }

    private static PrivilegedOp Poser(string nom, int heure) =>
        new SetRegistryValueOp(RegistryHiveKind.LocalMachine, HeuresActives.Cle, nom, RegistryValueType.DWord,
            heure.ToString(CultureInfo.InvariantCulture));
}
