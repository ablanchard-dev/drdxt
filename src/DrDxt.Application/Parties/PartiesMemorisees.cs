using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Application.Parties;

/// <summary>
/// Le choix « Relever mes parties » et les dernières parties relevées, gardés entre deux lancements.
/// Le relevé est le juge de tout gain ; éteint par défaut, il a relevé une partie en un
/// mois sur la machine de référence. Il est donc **allumé par défaut** : les parties restent sur ce PC, rien n'est envoyé,
/// et seul « éteint », écrit par le clic du joueur, l'éteint (un fichier absent ou abîmé n'est pas un choix). Une liste
/// abîmée n'invente toujours aucune partie.
/// </summary>
public sealed class PartiesMemorisees
{
    public const string CodeErreur = "PARTIES_MEMORISEES";
    private readonly FichierJsonAtomique _choix;
    private readonly FichierJsonAtomique _liste;
    private readonly FichierJsonAtomique _echec;
    private readonly FichierJsonAtomique _enCours;
    private readonly FichierJsonAtomique _interrompues;
    private readonly FichierJsonAtomique _essais;

    /// <summary>Les parties marquées par un essai en alternance, gardées à part. L'anneau des 20 dernières mêle tous les jeux :
    /// un joueur qui en alterne trois n'y gardait jamais les 9 parties d'un même jeu qu'un verdict demande, et le rang A-B-B-A
    /// se décalait à chaque partie évincée. Borné : un essai tranche bien avant.</summary>
    public const int PartiesDEssaiGardees = 100;

    /// <param name="cheminEchec">La dernière capture ratée. Par défaut à côté de la liste.</param>
    public PartiesMemorisees(string cheminChoix, string cheminListe, string? cheminEchec = null)
    {
        _choix = new FichierJsonAtomique(cheminChoix, CodeErreur);
        _liste = new FichierJsonAtomique(cheminListe, CodeErreur);
        _echec = new FichierJsonAtomique(cheminEchec
            ?? Path.Combine(Path.GetDirectoryName(cheminListe) ?? "", "releve-echec.json"), CodeErreur);
        _enCours = new FichierJsonAtomique(Path.Combine(Path.GetDirectoryName(cheminListe) ?? "", "partie-en-cours.json"), CodeErreur);
        _interrompues = new FichierJsonAtomique(Path.Combine(Path.GetDirectoryName(cheminListe) ?? "", "parties-interrompues.json"), CodeErreur);
        _essais = new FichierJsonAtomique(Path.Combine(Path.GetDirectoryName(cheminListe) ?? "", "parties-essai.json"), CodeErreur);
    }

    /// <summary>La partie que le relevé suit, réécrite à chaque regard ; null quand elle se clôt. Restée
    /// écrite au démarrage suivant, c'est une partie que le PC n'a pas laissé finir.</summary>
    public Result NoterEnCours(PartieInterrompue? partie)
        => _enCours.WriteAtomic(partie is null ? "" : JsonSerializer.Serialize(partie));

    /// <summary>Au démarrage du relevé : une marque restée ouverte passe dans les parties interrompues (les vingt dernières).
    /// La marque ne se lève qu'une fois la partie écrite : une liste illisible ou une écriture ratée la laisse au démarrage
    /// suivant, au lieu d'effacer une partie qui n'a été gardée nulle part.</summary>
    /// <param name="maintenant">L'heure du relevé : l'arrêt qui a interrompu la partie tombe entre son dernier regard et elle.</param>
    public void ReleverInterrompue(DateTimeOffset maintenant)
    {
        var texte = _enCours.Read();
        if (!texte.IsOk || string.IsNullOrWhiteSpace(texte.Value)) return;
        PartieInterrompue? p;
        try { p = JsonSerializer.Deserialize<PartieInterrompue>(texte.Value); }
        catch (JsonException) { p = null; } // une marque abîmée n'invente aucune partie
        if (p is not null)
        {
            if (_interrompues.LireListePourReecrire<PartieInterrompue>() is not { IsOk: true } lu) return;
            var ecrite = _interrompues.WriteAtomic(JsonSerializer.Serialize(lu.Value!.Prepend(p with { ReleveeLe = maintenant })
                .Take(ReleveDesParties.PartiesGardees).ToList()));
            if (!ecrite.IsOk) return;
        }
        _enCours.WriteAtomic("");
    }

    public IReadOnlyList<PartieInterrompue> Interrompues()
    {
        var texte = _interrompues.Read();
        if (!texte.IsOk || string.IsNullOrWhiteSpace(texte.Value)) return [];
        try { return (JsonSerializer.Deserialize<List<PartieInterrompue?>>(texte.Value) ?? []).OfType<PartieInterrompue>().ToList(); }
        catch (JsonException) { return []; }
    }

    /// <summary>Une capture ratée en cours de partie : sa raison et son heure, la dernière seulement.</summary>
    public sealed record EchecDeCapture(DateTimeOffset Quand, string Raison);

    /// <summary>Le relevé de fond n'a pas de fenêtre. Sans ce fichier, un service de mesure
    /// refusé (antivirus, PresentMon absent) donnait des semaines de « relevé allumé » et zéro partie, sans un mot.</summary>
    public Result NoterEchec(EchecDeCapture echec) => _echec.WriteAtomic(JsonSerializer.Serialize(echec));

    public EchecDeCapture? DernierEchec()
    {
        var texte = _echec.Read();
        if (!texte.IsOk || string.IsNullOrWhiteSpace(texte.Value)) return null;
        try { return JsonSerializer.Deserialize<EchecDeCapture>(texte.Value); }
        catch (JsonException) { return null; }
    }

    public static PartiesMemorisees ParDefaut() =>
        new(FichierJsonAtomique.CheminLocal("releve-parties.json"), FichierJsonAtomique.CheminLocal("parties.json"));

    public Result Allumer() => _choix.WriteAtomic("true");
    public Result Eteindre() => _choix.WriteAtomic("false");

    public bool EstAllume()
    {
        var texte = _choix.Read();
        return !(texte.IsOk && texte.Value?.Trim() == "false");
    }

    public IReadOnlyList<PartieRelevee> Lire()
    {
        var texte = _liste.Read();
        if (!texte.IsOk || string.IsNullOrWhiteSpace(texte.Value)) return [];
        // Un « null » de la liste n'est pas une partie : écarté ici, il n'est ni rendu ni réécrit par Ajouter.
        try { return (JsonSerializer.Deserialize<List<PartieRelevee?>>(texte.Value) ?? []).OfType<PartieRelevee>().ToList(); }
        catch (JsonException) { return []; }
    }

    /// <summary>Ajoute la partie en tête et garde les vingt dernières. Rend la liste avec la nouvelle partie, et l'écriture
    /// sur le disque : non écrite, la partie est montrée mais perdue au relancement, et l'appelant le dit.</summary>
    public PartiesAjoutees Ajouter(PartieRelevee partie)
    {
        // Une liste qui ne se lit pas n'est pas une liste vide : rien n'est écrit. Abîmée ou d'une autre forme : mise de côté
        // (sa trace reste), elle repart de cette partie.
        if (_liste.LireListePourReecrire<PartieRelevee>() is not { IsOk: true } lu)
            return new PartiesAjoutees([partie], Result.Fail(CodeErreur, "The match list could not be read: nothing written."));
        var liste = ReleveDesParties.Ajouter(lu.Value!, partie);
        var ecrite = _liste.WriteAtomic(JsonSerializer.Serialize(liste));
        if (partie.Essai is not null && _essais.LireListePourReecrire<PartieRelevee>() is { IsOk: true } essais)
            _essais.WriteAtomic(JsonSerializer.Serialize(new[] { partie }.Concat(essais.Value!).Take(PartiesDEssaiGardees).ToList()));
        return new PartiesAjoutees(liste, ecrite);
    }

    /// <summary>Les parties marquées par un essai, la plus récente d'abord, au-delà de l'anneau des 20.</summary>
    public IReadOnlyList<PartieRelevee> PartiesDEssai()
    {
        var texte = _essais.Read();
        if (!texte.IsOk || string.IsNullOrWhiteSpace(texte.Value)) return [];
        try { return (JsonSerializer.Deserialize<List<PartieRelevee?>>(texte.Value) ?? []).OfType<PartieRelevee>().ToList(); }
        catch (JsonException) { return []; }
    }
}

/// <param name="Liste">Les parties, la nouvelle comprise.</param>
/// <param name="Ecrite">L'écriture sur le disque.</param>
public sealed record PartiesAjoutees(IReadOnlyList<PartieRelevee> Liste, Result Ecrite);
