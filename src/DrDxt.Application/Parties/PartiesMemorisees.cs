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

    /// <param name="cheminEchec">La dernière capture ratée. Par défaut à côté de la liste.</param>
    public PartiesMemorisees(string cheminChoix, string cheminListe, string? cheminEchec = null)
    {
        _choix = new FichierJsonAtomique(cheminChoix, CodeErreur);
        _liste = new FichierJsonAtomique(cheminListe, CodeErreur);
        _echec = new FichierJsonAtomique(cheminEchec
            ?? Path.Combine(Path.GetDirectoryName(cheminListe) ?? "", "releve-echec.json"), CodeErreur);
        _enCours = new FichierJsonAtomique(Path.Combine(Path.GetDirectoryName(cheminListe) ?? "", "partie-en-cours.json"), CodeErreur);
        _interrompues = new FichierJsonAtomique(Path.Combine(Path.GetDirectoryName(cheminListe) ?? "", "parties-interrompues.json"), CodeErreur);
    }

    /// <summary>La partie que le relevé suit, réécrite à chaque regard ; null quand elle se clôt. Restée
    /// écrite au démarrage suivant, c'est une partie que le PC n'a pas laissé finir.</summary>
    public Result NoterEnCours(PartieInterrompue? partie)
        => _enCours.WriteAtomic(partie is null ? "" : JsonSerializer.Serialize(partie));

    /// <summary>Au démarrage du relevé : une marque restée ouverte passe dans les parties interrompues (les vingt dernières).</summary>
    /// <param name="maintenant">L'heure du relevé : l'arrêt qui a interrompu la partie tombe entre son dernier regard et elle.</param>
    public void ReleverInterrompue(DateTimeOffset maintenant)
    {
        var texte = _enCours.Read();
        if (!texte.IsOk || string.IsNullOrWhiteSpace(texte.Value)) return;
        try
        {
            if (JsonSerializer.Deserialize<PartieInterrompue>(texte.Value) is { } p)
                _interrompues.WriteAtomic(JsonSerializer.Serialize(Interrompues().Prepend(p with { ReleveeLe = maintenant })
                    .Take(ReleveDesParties.PartiesGardees).ToList()));
        }
        catch (JsonException) { /* une marque abîmée n'invente aucune partie */ }
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

    /// <summary>Ajoute la partie en tête et garde les vingt dernières. Rend la liste écrite.</summary>
    public IReadOnlyList<PartieRelevee> Ajouter(PartieRelevee partie)
    {
        var liste = ReleveDesParties.Ajouter(Lire(), partie);
        _liste.WriteAtomic(JsonSerializer.Serialize(liste));
        return liste;
    }
}
