using DrDxt.Core;

namespace DrDxt.Infrastructure.Repair;

/// <summary>Ce que le journal a a dire du scan qui vient d'avoir lieu.</summary>
/// <param name="Avertissement">Ce qu'il faut lire avant les changements : les deux photos n'ont pas
/// ete prises dans le meme etat. <c>null</c> quand elles sont comparables.</param>
/// <param name="Nouvelles">Les changements qui meritent d'etre lus : jugements, apparitions,
/// disparitions. Jamais les valeurs mesurees qui varient.</param>
public sealed record JournalDeSante(string? Avertissement, IReadOnlyList<RepairDelta> Nouvelles);

/// <summary>
/// Ce qui a change depuis la derniere fois : le pilier maintenir, cote produit.
///
/// ══ pourquoi il existe ══
/// Une photo isolee ne prouve rien dans le temps : la degradation lente, celle qu'un scan isole ne
/// voit jamais, ne se voit qu'en comparant la photo a la precedente.
///
/// ══ ce que ce service ne montre pas, et c'est sa valeur ══
/// Il ecarte les valeurs mesurees qui bougent. Deux passes a une minute d'ecart produisent
/// quatre « changements » (cœurs parques, frequence CPU, debit RAM, temperature GPU), tous exacts
/// et tous sans interet : ce sont des mesures, elles varient par nature. Les melanger aux vrais
/// changements donne un diff qui trouve toujours quelque chose, donc un diff qu'on cesse de lire.
///
/// Et l'avertissement de conditions passe avant tout le reste : « le multi-canal n'est plus
/// prouve » peut venir d'une compilation qui tourne pendant la lecture, sans que la machine ait
/// rien perdu. Le constat serait juste et sa lecture fausse.
/// </summary>
public sealed class RepairJournalService
{
    private readonly RepairHistoryStore _magasin;
    private readonly Func<ConditionsDeMesure?> _conditions;
    private readonly Func<DateTimeOffset> _maintenant;
    private readonly string? _empreinteLecteur;
    private readonly bool? _eleve;
    private readonly string? _lecteur;

    public RepairJournalService(
        RepairHistoryStore magasin,
        Func<ConditionsDeMesure?> conditions,
        Func<DateTimeOffset>? maintenant = null,
        string? empreinteLecteur = null,
        bool? eleve = null,
        string? lecteur = null)
    {
        _magasin = magasin;
        _conditions = conditions;
        _maintenant = maintenant ?? (() => DateTimeOffset.Now);
        _empreinteLecteur = empreinteLecteur;
        _eleve = eleve;
        _lecteur = lecteur;
    }

    /// <summary>Enregistre la photo du jour et rend ce qui a change depuis la precedente.
    /// <c>null</c> quand il n'y a rien a comparer : une premiere photo n'est pas un changement.</summary>
    public JournalDeSante? EnregistrerEtComparer(IReadOnlyList<RepairFinding>? constats)
    {
        if (constats is null || constats.Count == 0) return null;

        var photo = new RepairSnapshot(
            _maintenant(),
            // Les familles examinees voyagent avec la photo. Sans elles, le jour ou l'audit
            // apprend a lire une famille de plus, le diff annonce une degradation de la machine
            // alors que c'est le produit qui a appris a regarder.
            constats.Select(c => c.Domaine).Distinct().ToList(),
            constats.Select(c => new RepairEtat(c.Domaine, c.Nom, c.ValeurCourante, c.Statut)).ToList(),
            Conditions(),
            _empreinteLecteur,
            _eleve,
            _lecteur);

        // La photo de référence est celle du même lecteur, pas la dernière du fichier.
        var photos = _magasin.Lire();
        var precedente = photos.IsOk
            ? Result<RepairSnapshot?>.Ok(RepairHistory.PhotoComparable(photos.Value!, photo))
            : Result<RepairSnapshot?>.Fail(photos.Error!.Value.Code, photos.Error.Value.Message);

        // L'enregistrement se fait dans tous les cas : meme sans photo d'avant, celle-ci servira la
        // prochaine fois. Un echec d'ecriture ne doit pas empecher de rendre le diff qu'on a.
        _magasin.Enregistrer(photo);

        // Un journal illisible n'est pas un journal vide. Si les deux rendaient `null`, l'ecran
        // afficherait la meme chose dans les deux cas : rien. « On ne sait pas lire l'historique »
        // passerait pour « rien n'a change depuis la derniere fois », un Unknown peint en sain,
        // exactement ce que le magasin prend soin de distinguer une couche plus bas.
        if (!precedente.IsOk)
            return new JournalDeSante(
                Docteur.T("RJS.HistoriqueNonLu", precedente.Error!.Value.Message),
                []);

        if (precedente.Value is not { } avant) return null;

        var nouvelles = RepairHistory.Comparer(avant, photo)
            .Where(d => d.Quoi != RepairChangement.ValeurChangee)
            // Droits perdus entre les deux photos ⇒ ce qui disparait ou devient
            // inconnu est une lecture refusee, pas un changement. Le dire serait accuser la machine.
            .Where(d => !(avant.Eleve == true && photo.Eleve == false
                          && (d.Quoi == RepairChangement.Disparu
                              || (d.Quoi == RepairChangement.StatutChange && d.Apres == nameof(RepairStatus.Unknown)))))
            .ToList();

        // La mise a jour du lecteur et l'agitation de la machine NUANCENT des
        // changements. Sans changement, elles s'afficheraient seules sous un compteur a 0 : une mise
        // en garde sur rien. Les droits, eux, disent qu'une lecture a ete refusee : ils restent.
        var avertissements = new[]
        {
            nouvelles.Count > 0 ? RepairHistory.AvertissementDeLecteur(avant, photo) : null,
            RepairHistory.AvertissementDeDroits(avant, photo),
            nouvelles.Count > 0 ? RepairHistory.AvertissementDeConditions(avant, photo) : null,
        }.Where(a => a is not null).ToList();
        return new JournalDeSante(
            avertissements.Count == 0 ? null : string.Join(" ", avertissements),
            nouvelles);
    }

    /// <summary>Les conditions, ou <c>null</c> si les lire echoue. Ne pas connaitre l'etat de la
    /// machine est un resultat ; faire echouer le scan pour ca n'en est pas un.</summary>
    private ConditionsDeMesure? Conditions()
    {
        try { return _conditions(); }
        catch (Exception) { return null; }
    }
}
