using System.Text;
using System.Text.Json;
using DrDxt.Core;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.ElevatedHelper.Execution;

/// <summary>Un programme que DrDXT a coupé au démarrage, et les octets exacts qu'il a écrits sous StartupApproved (en
/// hexadécimal, horodatage compris).</summary>
/// <param name="Origine">Les octets lus juste avant la coupure, en hexadécimal ; vide : la valeur n'existait pas ; null : une
/// trace écrite avant que l'origine soit notée, rendue « actif » comme avant.</param>
public sealed record CoupureDuDemarrage(RegistryHiveKind Ruche, string Liste, string Nom, string Octets, string? Origine = null);

/// <summary>
/// La trace des programmes que DrDXT a coupés au démarrage, pour que « Réactiver » survive à l'App. Elle est notée avant
/// l'écriture, par le processus qui écrit : une App tuée juste après connaît déjà l'annulation. Retirée si l'écriture échoue,
/// et à la réactivation.
/// Deux magasins, un par ruche, chacun cru pour la sienne seulement. La ruche de la machine : le helper, dans la racine des
/// données en écriture admin, lue seulement si elle est de confiance. La ruche du joueur : l'App, qui l'écrit avec son
/// jeton, dans ses données ; un programme du compte qui forgerait ce fichier peut déjà réécrire HKCU lui-même.
/// </summary>
public sealed class TraceDesCoupures(string chemin, bool confiance, RegistryHiveKind ruche)
{
    public const string NomFichier = "coupures-demarrage.json";

    /// <summary>La ruche de la machine, tenue par le helper.</summary>
    public static TraceDesCoupures DeLaMachine()
        => new(Path.Combine(ProgramDataManifestStorage.DefaultRoot(), NomFichier), confiance: true, RegistryHiveKind.LocalMachine);

    /// <summary>La ruche du joueur, tenue par l'App.</summary>
    public static TraceDesCoupures DeLaSession()
        => new(DossierDonnees.Chemin(NomFichier), confiance: false, RegistryHiveKind.CurrentUser);

    /// <summary>L'écriture d'une coupure ou d'une réactivation, dans l'ordre qui garde l'annulation connue. Sans trace (null),
    /// l'écriture seule.</summary>
    public static Result Autour(TraceDesCoupures? trace, CoupureDuDemarrage coupure, bool actif, Func<Result> ecrire)
    {
        if (trace is null) return ecrire();
        if (actif)
        {
            var r = ecrire();
            if (r.IsOk) trace.Retirer(coupure.Ruche, coupure.Liste, coupure.Nom);
            return r;
        }
        if (trace.Noter(coupure) is { IsOk: false } note)
            return Result.Fail(note.Error!.Value.Code, $"The cut could not be recorded first: {note.Error.Value.Message}");
        var e = ecrire();
        if (!e.IsOk) trace.Retirer(coupure.Ruche, coupure.Liste, coupure.Nom);
        return e;
    }

    // Une trace illisible n'est jamais lue vide puis écrasée : les autres coupures qu'elle porte perdraient leur Réactiver.
    public Result Noter(CoupureDuDemarrage c) => Modifier(l => [.. l.Where(x => !Meme(x, c.Ruche, c.Liste, c.Nom)), c]);

    public Result Retirer(RegistryHiveKind r, string liste, string nom) => Modifier(l => l.Where(x => !Meme(x, r, liste, nom)).ToList());

    private Result Modifier(Func<List<CoupureDuDemarrage>, List<CoupureDuDemarrage>> changer)
    {
        var t = Tout();
        return t.IsOk ? Ecrire(changer(t.Value!)) : Result.Fail(t.Error!.Value.Code, t.Error.Value.Message);
    }

    /// <summary>Les coupures de sa ruche, pour les proposer ; un fichier absent, illisible ou hors confiance n'en donne aucune.</summary>
    public IReadOnlyList<CoupureDuDemarrage> Lire() => Tout() is { IsOk: true } t ? t.Value!.Where(c => c.Ruche == ruche).ToList() : [];

    private static bool Meme(CoupureDuDemarrage c, RegistryHiveKind r, string liste, string nom)
        => c.Ruche == r && string.Equals(c.Liste, liste, StringComparison.OrdinalIgnoreCase)
           && string.Equals(c.Nom, nom, StringComparison.OrdinalIgnoreCase);

    /// <summary>Seul un fichier absent vaut une trace vide ; illisible ou hors confiance, c'est un échec.</summary>
    private Result<List<CoupureDuDemarrage>> Tout()
    {
        try
        {
            if (!File.Exists(chemin)) return Result<List<CoupureDuDemarrage>>.Ok([]);
            if (confiance && TrustedManifestRoot.EnsureTrustedFile(chemin) is { IsOk: false } refus)
                return Result<List<CoupureDuDemarrage>>.Fail(refus.Error!.Value.Code, refus.Error.Value.Message);
            return JsonSerializer.Deserialize<List<CoupureDuDemarrage>>(File.ReadAllText(chemin)) is { } l && l.TrueForAll(c => c is not null)
                ? Result<List<CoupureDuDemarrage>>.Ok(l)
                : Result<List<CoupureDuDemarrage>>.Fail(ErrorCodes.IoFailure, "Unreadable startup cut trace.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return Result<List<CoupureDuDemarrage>>.Fail(ErrorCodes.IoFailure, $"Unreadable startup cut trace: {ex.Message}");
        }
    }

    private Result Ecrire(List<CoupureDuDemarrage> coupures)
    {
        var octets = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(coupures));
        if (confiance) return TrustedManifestRoot.WriteTrustedFile(chemin, octets);
        try
        {
            return new FichierJsonAtomique(chemin, "COUPURES_DEMARRAGE").WriteAtomic(Encoding.UTF8.GetString(octets));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Result.Fail(ErrorCodes.IoFailure, ex.Message);
        }
    }
}
