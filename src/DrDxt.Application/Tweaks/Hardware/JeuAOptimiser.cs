using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Hardware;

/// <summary>Le jeu que le joueur a choisi d'optimiser : le nom de son exe, et le chemin complet appris en le voyant tourner
/// (le pilote ne trouve un profil que par le chemin complet).</summary>
public sealed record JeuChoisi(string Exe, string? Chemin = null);

/// <summary>Le choix du joueur, dans ses données. Absent, vide, illisible ou incohérent : « automatique » (le jeu ouvert).</summary>
public sealed class MemoireDuJeuChoisi
{
    public const string CodeErreur = "JEU_CHOISI";
    private readonly FichierJsonAtomique _fichier;

    public MemoireDuJeuChoisi(string chemin) => _fichier = new FichierJsonAtomique(chemin, CodeErreur);

    public static MemoireDuJeuChoisi ParDefaut() => new(FichierJsonAtomique.CheminLocal("jeu-choisi.json"));

    /// <summary><c>null</c> = automatique. On écrit « rien » plutôt que de supprimer : même chemin atomique.</summary>
    public Result Choisir(JeuChoisi? jeu) => _fichier.WriteAtomic(jeu is null ? "null" : JsonSerializer.Serialize(jeu));

    /// <summary>Le chemin complet d'un processus vu tourner. Retenu seulement s'il est celui du jeu choisi (même nom d'exe,
    /// chemin complet, hors du dossier de Windows) et nouveau ; un autre processus n'apprend jamais rien.</summary>
    public void Apprendre(string? chemin)
    {
        if (Lire() is { } choix && EstSonChemin(chemin, choix) && !string.Equals(choix.Chemin, chemin, StringComparison.OrdinalIgnoreCase))
            Choisir(choix with { Chemin = chemin });
    }

    public static bool EstSonChemin(string? chemin, JeuChoisi choix)
        => chemin is not null && Path.IsPathFullyQualified(chemin)
           && string.Equals(Path.GetFileName(chemin), choix.Exe, StringComparison.OrdinalIgnoreCase)
           && NomExeDeJeu.EstUnJeuPossible(choix.Exe, chemin);

    public JeuChoisi? Lire()
    {
        var texte = _fichier.Read();
        if (!texte.IsOk || string.IsNullOrWhiteSpace(texte.Value)) return null;
        try
        {
            return JsonSerializer.Deserialize<JeuChoisi>(texte.Value) is { Exe.Length: > 0 } j && NomExeDeJeu.EstUnJeuPossible(j.Exe) ? j : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Où en est le jeu choisi, pour la ligne sous Optimiser.</summary>
public enum EtatDuJeuChoisi { Automatique, Ouvert, Ferme, SansChemin, CheminDisparu }

/// <summary>
/// Le jeu que visent les leviers au profil d'un jeu et la lecture des réglages. Automatique : le jeu ouvert, comme avant.
/// Un jeu choisi : son profil, qu'il tourne ou non, jamais celui d'un autre jeu ouvert. Son chemin s'apprend quand un
/// processus du même nom d'exe tourne, et se relit à chaque usage : disparu ou incomplet, rien n'est visé. Ses modules (d'où
/// se lit l'API graphique) ne se lisent que jeu ouvert. « Un jeu tourne-t-il ? » reste le vrai jeu ouvert (<see cref="JeuTourne"/>).
/// </summary>
public sealed class JeuAOptimiser(IGameExecutable enCours, MemoireDuJeuChoisi memoire, Func<string, bool> existe) : IGameExecutable
{
    public JeuAOptimiser(IGameExecutable enCours, MemoireDuJeuChoisi memoire) : this(enCours, memoire, File.Exists) { }

    public string? RunningGameExecutablePath()
    {
        var tourne = enCours.RunningGameExecutablePath();
        if (memoire.Lire() is not { } choix) return tourne;
        if (!EstLeChoix(tourne, choix)) return CheminValide(choix);
        memoire.Apprendre(tourne);
        return tourne;
    }

    public IReadOnlyList<string>? ModulesDuJeu()
        => memoire.Lire() is not { } choix || EstLeChoix(enCours.RunningGameExecutablePath(), choix) ? enCours.ModulesDuJeu() : null;

    public bool JeuTourne() => enCours.JeuTourne();

    /// <summary>Le jeu choisi qui tourne apprend son chemin ; sinon rien n'est écrit.</summary>
    public EtatDuJeuChoisi Etat()
    {
        if (memoire.Lire() is not { } choix) return EtatDuJeuChoisi.Automatique;
        if (EstLeChoix(enCours.RunningGameExecutablePath(), choix))
        {
            RunningGameExecutablePath();
            return EtatDuJeuChoisi.Ouvert;
        }
        if (choix.Chemin is null) return EtatDuJeuChoisi.SansChemin;
        return CheminValide(choix) is null ? EtatDuJeuChoisi.CheminDisparu : EtatDuJeuChoisi.Ferme;
    }

    private static bool EstLeChoix(string? chemin, JeuChoisi choix) => MemoireDuJeuChoisi.EstSonChemin(chemin, choix);

    private string? CheminValide(JeuChoisi choix)
        => choix.Chemin is { } c && EstLeChoix(c, choix) && existe(c) ? c : null;
}
