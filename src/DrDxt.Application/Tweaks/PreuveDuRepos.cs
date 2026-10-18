using DrDxt.Core;

namespace DrDxt.Application.Tweaks;

/// <summary><see cref="RetraitRate"/> : pas de preuve, mais le retrait a été refusé ; le réglage est toujours posé.</summary>
public enum EtatDeLaPreuve { Prouve, PasDeGain, NonMesure, RetraitRate }

/// <summary>Ce que la mesure du repos a établi : les médianes avant et après, en watts.</summary>
public sealed record VerdictDuRepos(EtatDeLaPreuve Etat, double? AvantW, double? ApresW)
{
    public double? BaisseW => AvantW is { } a && ApresW is { } b ? a - b : null;
}

/// <summary>Une preuve commencée : ce que l'annulation réécrira, la cible, et les watts lus avant l'écriture.</summary>
public sealed record PreuveEnAttente(string TweakId, string OriginalValueJson, string? AppliedValueJson, List<double> AvantW);

/// <summary>Le fichier qui garde une preuve commencée d'un lancement à l'autre. Illisible ou abîmé = rien n'attend :
/// l'annulation ne se joue jamais sur un fichier qu'on ne sait pas lire.</summary>
public sealed class PreuvesEnAttente(string chemin)
{
    public static PreuvesEnAttente ParDefaut() => new(FichierJsonAtomique.CheminLocal("preuve-repos-carte.json"));

    public PreuveEnAttente? Charger()
    {
        // Même garde que les réglages retenus : un retrait raté rejoue cette annulation par le helper élevé.
        try
        {
            return File.Exists(chemin) && System.Text.Json.JsonSerializer.Deserialize<PreuveEnAttente>(File.ReadAllText(chemin)) is { } p
                   && DrDxt.Application.Autopilot.RevertAuDemarrage.AnnulationSure(p.TweakId, p.OriginalValueJson) ? p : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    public void Garder(PreuveEnAttente p)
    {
        // Rejoué par le helper élevé : écrit au temporaire aléatoire, jamais par-dessus un lien posé d'avance. Un échec lève,
        // comme avant : l'appelant ne ferme pas la portée de pose, et le helper garde l'origine.
        var ecrit = new FichierJsonAtomique(chemin, ErrorCodes.IoFailure).WriteAtomic(System.Text.Json.JsonSerializer.Serialize(p));
        if (!ecrit.IsOk) throw new IOException(ecrit.Error!.Value.Message);
    }

    public void Effacer()
    {
        try { File.Delete(chemin); } catch (IOException) { }
    }
}

/// <summary>Un réglage gardé au repos, et depuis quand. Il reste sous surveillance en jeu : le retirer demande
/// l'annulation gardée ici.</summary>
public sealed record ReglageRetenu(string TweakId, string OriginalValueJson, string? AppliedValueJson, DateTimeOffset Le);

public sealed class ReglagesRetenus(string chemin)
{
    public static ReglagesRetenus ParDefaut() => new(FichierJsonAtomique.CheminLocal("reglage-carte-retenu.json"));

    public ReglageRetenu? Charger()
    {
        // Fichier du compte, rejoue par le helper eleve au retrait : seule l'annulation de son levier est rendue.
        try
        {
            return File.Exists(chemin) && System.Text.Json.JsonSerializer.Deserialize<ReglageRetenu>(File.ReadAllText(chemin)) is { } r
                   && DrDxt.Application.Autopilot.RevertAuDemarrage.AnnulationSure(r.TweakId, r.OriginalValueJson) ? r : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    public void Garder(ReglageRetenu r)
    {
        var ecrit = new FichierJsonAtomique(chemin, ErrorCodes.IoFailure).WriteAtomic(System.Text.Json.JsonSerializer.Serialize(r));
        if (!ecrit.IsOk) throw new IOException(ecrit.Error!.Value.Message);
    }

    public void Effacer()
    {
        try { File.Delete(chemin); } catch (IOException) { }
    }
}

/// <summary>
/// Un réglage de la carte ne reste que prouvé. Au repos, la
/// preuve est la consommation de la carte lue avant, puis après le réglage.
///
/// Prouvé = séparation nette : toutes les lectures d'après sous toutes celles d'avant. Pas de seuil en watts
/// inventé : c'est la dispersion mesurée des deux séries qui décide. Tout le reste (chevauchement, watts
/// illisibles) retire le réglage. Sans watts lisibles au départ, rien n'est écrit.
/// </summary>
public static class PreuveDuRepos
{
    /// <summary>Le pas entre deux lectures et l'attente après l'écriture, le temps que le pilote redescende.
    /// Des durées de protocole, pas des faits sur la carte : la mesure dit si elles ont suffi.</summary>
    public static readonly TimeSpan Pas = TimeSpan.FromSeconds(1), Stabilisation = TimeSpan.FromSeconds(15);

    public static async Task<VerdictDuRepos> ProuverAsync(ITweak levier, IHardwareProbe sonde, int echantillons,
        Func<TimeSpan, Task> attendre)
    {
        var avant = await LireAsync(sonde, echantillons, attendre).ConfigureAwait(false);
        if (avant.Count == 0) return new(EtatDeLaPreuve.NonMesure, null, null);

        var ecrit = await levier.ApplyAsync().ConfigureAwait(false);
        if (!ecrit.IsOk || ecrit.Value is null) return new(EtatDeLaPreuve.NonMesure, Mediane(avant), null);

        await attendre(Stabilisation).ConfigureAwait(false);
        var apres = await LireAsync(sonde, echantillons, attendre).ConfigureAwait(false);

        return await JugerAsync(levier, ecrit.Value, avant, apres).ConfigureAwait(false);
    }

    /// <summary>Mesure réelle : le global écrit ne change la carte qu'au redémarrage (chaque programme lit
    /// le profil à son lancement, le pilote suit le plus exigeant). Premier temps : l'avant et l'écriture sont gardés.
    /// Rend <c>false</c> si rien n'a été écrit (watts illisibles, levier refusé).</summary>
    public static async Task<bool> EcrireAsync(ITweak levier, IHardwareProbe sonde, int echantillons,
        Func<TimeSpan, Task> attendre, PreuvesEnAttente attente)
    {
        var avant = await LireAsync(sonde, echantillons, attendre).ConfigureAwait(false);
        if (avant.Count == 0) return false;
        // Rien ne garde encore l'origine : le helper la garde jusqu'à ce que la preuve en attente la porte.
        using var portee = PorteeDePose.Ouvrir();
        var ecrit = await levier.ApplyAsync().ConfigureAwait(false);
        if (!ecrit.IsOk || ecrit.Value is null) return false;
        attente.Garder(new PreuveEnAttente(ecrit.Value.TweakId, ecrit.Value.OriginalValueJson, ecrit.Value.AppliedValueJson, avant));
        await portee.FermerAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Second temps, au lancement suivant : mesure le repos, garde le réglage seulement sur séparation nette,
    /// sinon le retire. La preuve en attente est effacée, sauf si le retrait a raté (RetraitRate) : elle reste pour retenter le
    /// retrait au lancement suivant. <c>null</c> = rien n'attendait.</summary>
    public static async Task<VerdictDuRepos?> ConclureAsync(ITweak levier, IHardwareProbe sonde, int echantillons,
        Func<TimeSpan, Task> attendre, PreuvesEnAttente attente)
    {
        if (attente.Charger() is not { } p || p.TweakId != levier.Id) return null;
        var apres = await LireAsync(sonde, echantillons, attendre).ConfigureAwait(false);
        var verdict = await JugerAsync(levier, new TweakSnapshot(p.TweakId, p.OriginalValueJson, p.AppliedValueJson),
            p.AvantW, apres).ConfigureAwait(false);
        // Un retrait refusé laisse le réglage posé : la preuve reste, pour que le retrait se retente au prochain lancement.
        if (verdict.Etat != EtatDeLaPreuve.RetraitRate) attente.Effacer();
        return verdict;
    }

    private static async Task<VerdictDuRepos> JugerAsync(ITweak levier, TweakSnapshot ecrit, List<double> avant, List<double> apres)
    {
        var etat = apres.Count == 0 ? EtatDeLaPreuve.NonMesure
            : apres.Max() < avant.Min() ? EtatDeLaPreuve.Prouve
            : EtatDeLaPreuve.PasDeGain;
        if (etat != EtatDeLaPreuve.Prouve && !(await levier.RevertAsync(ecrit).ConfigureAwait(false)).IsOk)
            etat = EtatDeLaPreuve.RetraitRate;
        return new(etat, Mediane(avant), apres.Count == 0 ? null : Mediane(apres));
    }

    private static async Task<List<double>> LireAsync(IHardwareProbe sonde, int n, Func<TimeSpan, Task> attendre)
    {
        var watts = new List<double>();
        for (var i = 0; i < n; i++)
        {
            if (i > 0) await attendre(Pas).ConfigureAwait(false);
            if (sonde.Lire().GpuPowerW is { } w && w > 0 && double.IsFinite(w)) watts.Add(w);
        }
        return watts;
    }

    private static double Mediane(List<double> v)
    {
        var tri = v.Order().ToList();
        return tri[tri.Count / 2];
    }
}
