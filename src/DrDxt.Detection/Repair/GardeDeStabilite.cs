using DrDxt.Core;
using DrDxt.Detection.Hardware;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Ce qui dit « instable » aux gardes qui retiennent un conseil de surcadençage ou de baisse de tension (Curve Optimizer,
/// profil mémoire XMP/EXPO) : une seule source pour les deux, sinon l'une conseillerait ce que l'autre retient. Les
/// arrêts brutaux, et les autres incidents datés (rapport d'écran bleu, vidage écrit, erreur matérielle fatale ou
/// récupérable, hausse des arrêts dangereux d'un SSD au scan). Lecture seule.
/// </summary>
public static class GardeDeStabilite
{
    /// <summary>Le vrai, sur <see cref="CandidateSelector.FenetreInstabilite"/>. Une seule source non lue rend les arrêts à
    /// null : la garde dit alors « non lu », jamais « stable ».</summary>
    /// <param name="rapports">Les rapports d'écran bleu ; null = <see cref="EcransBleus.LireRapports"/>.</param>
    /// <param name="whea">Les erreurs matérielles sur un nombre de jours ; null = <see cref="ErreursMaterielles.Lire"/>.</param>
    public static (IReadOnlyList<ArretClasse>? Arrets, IReadOnlyList<DateTimeOffset> Autres) Lire(IUnexpectedShutdownLog arrets,
        IDumpCreationLog vidages, DateTimeOffset maintenant, Func<DateTimeOffset, IReadOnlyList<DateTimeOffset>?>? rapports = null,
        Func<int, IReadOnlyList<ErreursMaterielles.Enregistrement>?>? whea = null)
    {
        var depuis = maintenant - CandidateSelector.FenetreInstabilite;
        var bruts = StabiliteLue.ArretsBrutaux(arrets, maintenant);
        var rapportsLus = (rapports ?? EcransBleus.LireRapports)(depuis);
        var lus = vidages.Lire(depuis);
        var erreurs = (whea ?? (jours => ErreursMaterielles.Lire(jours)))((int)CandidateSelector.FenetreInstabilite.TotalDays);
        if (rapportsLus is null || !lus.IsOk || erreurs is null) return (null, []);
        return (bruts, [.. rapportsLus, .. lus.Value!.DernierVidage is { } v ? [v] : Array.Empty<DateTimeOffset>(),
            .. erreurs.Where(e => e.Gravite is 0 or 1).Select(e => e.Quand),
            .. SanteNvme.Hausses(DossierDonnees.Chemin(SanteNvme.Fichier))]);
    }

    /// <summary>Pur : toutes les dates d'incident ; <c>null</c> si une source n'a pas été lue.</summary>
    public static IReadOnlyList<DateTimeOffset>? Dates((IReadOnlyList<ArretClasse>? Arrets, IReadOnlyList<DateTimeOffset> Autres) lu)
        => lu.Arrets is null ? null : [.. lu.Arrets.Select(a => a.Quand), .. lu.Autres];
}
