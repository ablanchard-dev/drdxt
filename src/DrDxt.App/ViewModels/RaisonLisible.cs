using System.Globalization;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>
/// Ce qu'un échec dit au joueur : la phrase de son code (« Erreur.&lt;code&gt; » dans les ressources), sinon une phrase
/// générique avec le code entre parenthèses, que le support relie au journal. Le message de l'échec, souvent l'anglais
/// technique du helper, ne va jamais à l'écran : il part au journal.
/// </summary>
public static class RaisonLisible
{
    /// <summary>Le journal des messages techniques, posé au lancement par l'App ; sans lui, rien n'est gardé.</summary>
    public static Action<string>? JournalParDefaut { get; set; }

    public static string Dire(Result r, Func<string, string> localiser, Action<string>? journal = null)
    {
        if (r.Error is not { } e) return string.Empty;
        (journal ?? JournalParDefaut)?.Invoke($"{e.Code} : {e.Message}");
        var cle = "Erreur." + e.Code;
        var phrase = localiser(cle);
        return !string.IsNullOrEmpty(phrase) && phrase != cle
            ? phrase
            : string.Format(CultureInfo.CurrentCulture, localiser("Erreur.Generique"), e.Code);
    }

    /// <summary>La phrase d'échec de l'appelant, sauf quand la réponse du helper manque après l'envoi : l'op a pu se faire,
    /// « échec » serait faux, et seule la phrase de l'incertitude se dit, après le nom du réglage s'il est donné.</summary>
    public static string OuIncertain(Result r, string phraseDEchec, Func<string, string> localiser, string? nom = null)
        => r.Error?.Code is not (ErrorCodes.HelperNoReply or ErrorCodes.HelperNoReplyJournalisee) ? phraseDEchec
            : nom is null ? Dire(r, localiser) : $"{nom}{Docteur.DeuxPoints}{Dire(r, localiser)}";

    /// <summary>« phrase : raison », ou la seule phrase de l'incertitude quand la réponse du helper manque.</summary>
    public static string Echec(Result r, string phrase, Func<string, string> localiser)
        => OuIncertain(r, $"{phrase}{Docteur.DeuxPoints}{Dire(r, localiser)}", localiser);
}
