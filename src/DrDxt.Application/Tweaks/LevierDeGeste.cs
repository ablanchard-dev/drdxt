using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks;

/// <summary>
/// Un geste du docteur qui écrit sur la machine (retirer un vieux paquet de pilote d'affichage, passer un jeu au dernier
/// modèle DLSS, corriger les droits du fichier d'un service), gardé comme un levier : son inverse entre au journal des leviers, et « Annuler » dans « Ce que DrDXT a posé »
/// le rend, même après une relance. Il n'est ni au catalogue, ni en essai, ni sous la garde d'incident ou le retrait
/// automatique : c'est un choix du joueur, posé sur son clic.
/// Le journal vit sous le profil du joueur, que tout programme du compte peut écrire : seuls les inverses que le geste
/// écrit lui-même se rendent (le paquet de ce nom, remis depuis l'export que le helper garde ; les deux réglages DLSS du
/// profil de ce jeu ; l'origine des droits de ce service, que le helper garde). Tout autre inverse est refusé avant d'atteindre le helper, qui le filtre aussi par sa liste blanche.
/// </summary>
public sealed class LevierDeGeste(string id, IElevatedClient eleve) : ITweak
{
    private const string Pilote = "geste.pilote|", Dlss = "geste.dlss|", Droits = "geste.droits|";

    public static string IdPilote(string publie) => Pilote + publie;
    public static string IdDlss(string jeu) => Dlss + jeu;
    public static string IdDroits(string service) => Droits + service;

    /// <summary>Un identifiant de geste gardé : seules les familles connues.</summary>
    public static bool EstUnGeste(string id)
        => id.StartsWith(Pilote, StringComparison.Ordinal) || id.StartsWith(Dlss, StringComparison.Ordinal)
           || id.StartsWith(Droits, StringComparison.Ordinal);

    /// <summary>Le paquet de pilote d'un identifiant de geste, ou null.</summary>
    public static string? PaquetDe(string id) => id.StartsWith(Pilote, StringComparison.Ordinal) ? id[Pilote.Length..] : null;

    /// <summary>Le jeu d'un identifiant de geste DLSS, ou null.</summary>
    public static string? JeuDe(string id) => id.StartsWith(Dlss, StringComparison.Ordinal) ? id[Dlss.Length..] : null;

    /// <summary>Le service d'un identifiant de geste sur les droits, ou null.</summary>
    public static string? ServiceDe(string id) => id.StartsWith(Droits, StringComparison.Ordinal) ? id[Droits.Length..] : null;

    /// <summary>Ce que le journal garde : les inverses, dans l'ordre où ils se rejouent, et ce qui a été posé.</summary>
    public static TweakSnapshot Snapshot(string id, IReadOnlyList<PrivilegedOp> inverses, IReadOnlyList<PrivilegedOp> poses)
        => new(id, CompositeSnapshotCodec.Encode(inverses).Value!, CompositeSnapshotCodec.Encode(poses).Value);

    public string Id => id;
    public TweakCategory Category => TweakCategory.FixIt;
    public bool RequiresElevation => true;
    public bool RequiresRestorePoint => false;
    public string SummaryKey => string.Empty;
    public bool AppliesTo(HardwareInventory hw, SettingsState state) => false;

    /// <summary>Un geste se pose sur sa carte, jamais d'ici.</summary>
    public Task<Result<TweakSnapshot>> ApplyAsync()
        => Task.FromResult(Result<TweakSnapshot>.Fail(ErrorCodes.TweakNotApplicable, "A doctor's gesture is applied from its card."));

    public async Task<Result> RevertAsync(TweakSnapshot snapshot)
    {
        using var horsPortee = PorteeDePose.Suspendre();
        if (snapshot.TweakId != id || Inverses(id, snapshot.OriginalValueJson) is not { } inverses)
            return Result.Fail(ErrorCodes.SnapshotInvalid, "This gesture's revert is not one the gesture writes: nothing sent.");
        return await RetourArriere.ToutRendreAsync(eleve, inverses).ConfigureAwait(false);
    }

    /// <summary>Les inverses d'un geste gardé, s'ils sont tous ceux que ce geste écrit lui-même ; sinon null. Le journal des
    /// leviers s'en sert à la lecture (<c>RevertAuDemarrage.AnnulationSure</c>) : une entrée forgée n'y entre même pas.</summary>
    public static IReadOnlyList<PrivilegedOp>? Inverses(string id, string? json)
    {
        if (!EstUnGeste(id) || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return CompositeSnapshotCodec.Decode(json) is { IsOk: true, Value: { Count: > 0 } ops } && ops.All(op => Admis(id, op)) ? ops : null;
        }
        catch (Exception) { return null; }
    }

    private static bool Admis(string id, PrivilegedOp op) => op switch
    {
        RestoreDriverPackageOp r => PaquetDe(id) == r.PublishedName,
        RendreDroitsDuServiceOp d => ServiceDe(id) == d.NomDuService,
        ApplyNvApiDrsSettingOp a => JeuDe(id) == a.ProfileName && a.SettingId is SurcoucheDlss.Override or SurcoucheDlss.Preset,
        DeleteNvApiDrsSettingOp d => JeuDe(id) == d.ProfileName && d.SettingId is SurcoucheDlss.Override or SurcoucheDlss.Preset,
        _ => false,
    };
}
