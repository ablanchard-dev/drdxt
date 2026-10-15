using DrDxt.Core;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DrDxt.Detection.Settings;

/// <summary>Ce qu'un reglage MMCSS fait reellement, d'apres la documentation Microsoft.</summary>
/// <param name="Nom">Le reglage, tel que le registre le nomme.</param>
/// <param name="Valeur">Ce qui est lu, ou <c>null</c> si absent.</param>
/// <param name="Effet">Ce que cette valeur produit. Voir <see cref="MmcssEffet"/>.</param>
/// <param name="Phrase">L'explication, avec ce que la source etablit.</param>
public sealed record MmcssConstat(string Nom, string? Valeur, MmcssEffet Effet, string Phrase);

/// <summary>
/// L'effet d'un reglage, et non son ecart a un defaut.
///
/// C'est le contournement qui rend ce lecteur possible. La page Microsoft du MMCSS documente la
/// semantique de chaque valeur mais ne publie aucun defaut, ni pour `SystemResponsiveness`, ni
/// pour les champs de la tache `Games`. Sans defaut, impossible de dire « quelqu'un l'a change ».
///
/// Mais on n'en a pas besoin : dire qu'une valeur est **sans effet** ne demande pas de savoir d'ou
/// elle vient. « Ce reglage ne fait rien, et voici la phrase de Microsoft qui le dit » est une
/// affirmation complete, verifiable, et utile ; c'est meme la plus utile, puisqu'elle desamorce un
/// tweak que quelqu'un a peut-etre paye ou passe une soiree a appliquer.
/// </summary>
public enum MmcssEffet
{
    /// <summary>Le reglage n'a pas ete lu.</summary>
    NonLu,

    /// <summary>La valeur agit, et la phrase dit comment.</summary>
    Agit,

    /// <summary>La valeur est inerte : la documentation etablit qu'elle ne produit rien. Ce
    /// n'est pas un degat, c'est un placebo, et le dire est le service rendu.</summary>
    SansEffet,

    /// <summary>La valeur desactive une fonction du systeme.</summary>
    Desactive,
}

/// <summary>
/// Le MMCSS, et les tweaks « gaming » que Microsoft documente comme inertes.
///
/// Source unique et citee : la page Win32 « Multimedia Class Scheduler Service »
/// (learn.microsoft.com/windows/win32/procthread/multimedia-class-scheduler-service, lue le
/// 08/09/2026). Rien ici ne vient d'un forum ni d'une memoire.
///
/// Ce qu'elle etablit, mot pour mot, et qui condamne quatre des reglages les plus recopies :
///
///   `SystemResponsiveness`   « Values below 10 and above 100 are clamped to 20 »
///                            ⇒ le fameux `= 0` ne fait rien. Il est ramene a 20.
///                            « A value of 100 disables MMCSS » ⇒ celui-la, si.
///   `GPU Priority`           « This priority is not yet used. »
///   `SFIO Priority`          « This value is not used. »
///   `Clock Rate`             la garantie existait sous Vista ; « Starting with Windows 7 [...]
///                            this guarantee was removed ». Sur Windows 11, le champ ne garantit
///                            plus rien.
///   `Priority` + categorie   « For tasks with a Scheduling Category of High, this value is
///                            always treated as 2 » ⇒ monter Priority a 8 en categorie High ne
///                            change rien.
///
/// Ce que ce lecteur ne dit pas. Il ne dit jamais « quelqu'un a modifie ceci » : la page ne
/// publie aucune valeur par defaut, et l'inventer serait la faute que ce depot traque. Il dit ce
/// que la valeur fait, ce qui ne demande pas de connaitre son origine, et suffit a desamorcer un
/// placebo.
///
/// Échec propre : ne leve jamais ; une lecture ratee rend `NonLu`, jamais une valeur fabriquee.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MmcssReader
{
    private const string Profil = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

    public const string Source =
        // « multimédia » avec accent n'est pas l'adresse (vu dans un rapport réel).
        "learn.microsoft.com/windows/win32/procthread/multimedia-class-scheduler-service";

    /// <summary>Lit le profil systeme et la tache demandee (`Games` par defaut).</summary>
    public IReadOnlyList<MmcssConstat> Lire(string tache = "Games")
    {
        var constats = new List<MmcssConstat> { Reactivite() };
        constats.AddRange(Tache(tache));
        return constats;
    }

    private static MmcssConstat Reactivite()
    {
        if (Dword(Profil, "SystemResponsiveness") is not { } v)
            return new MmcssConstat("SystemResponsiveness", null, MmcssEffet.NonLu,
                Docteur.T("MM.Absente"));

        // Les trois cas sont ceux que la page etablit explicitement. Tout le reste est decrit par
        // sa semantique : le pourcentage reserve aux taches de faible priorite.
        return v switch
        {
            100 => new MmcssConstat("SystemResponsiveness", "100", MmcssEffet.Desactive,
                // Sans capitales d'insistance, accents rétablis (règle d'écriture).
                Docteur.T("MM.Cent")),

            < 10 => new MmcssConstat("SystemResponsiveness", v.ToString(), MmcssEffet.SansEffet,
                Docteur.T("MM.SousDix", v)),

            _ => new MmcssConstat("SystemResponsiveness", Docteur.T("MM.Pourcent", v), MmcssEffet.Agit,
                Docteur.T("MM.Reserve", v) + (v % 10 != 0 ? Docteur.T("MM.Arrondi", v / 10 * 10) : "")),
        };
    }

    private static IEnumerable<MmcssConstat> Tache(string tache)
    {
        var chemin = $@"{Profil}\Tasks\{tache}";
        using var cle = Registry.LocalMachine.OpenSubKey(chemin);
        if (cle is null)
        {
            yield return new MmcssConstat(Docteur.T("MM.Nom.Tache", tache), null, MmcssEffet.NonLu,
                Docteur.T("MM.TacheAbsente", tache));
            yield break;
        }

        // Les trois champs que la documentation declare inertes. Leur valeur importe peu : quelle
        // qu'elle soit, elle ne produit rien. C'est ce qui permet de conclure sans connaitre le
        // defaut.
        yield return Inerte(cle, "GPU Priority", "« This priority is not yet used. »");
        yield return Inerte(cle, "SFIO Priority", "« This value is not used. »");
        yield return Inerte(cle, "Clock Rate",
            Docteur.T("MM.ClockRate"));

        // Le cas ou deux champs se contredisent : la categorie ecrase la priorite.
        var categorie = cle.GetValue("Scheduling Category")?.ToString();
        var priorite = cle.GetValue("Priority")?.ToString();
        if (categorie is not null && priorite is not null)
            yield return string.Equals(categorie, "High", StringComparison.OrdinalIgnoreCase)
                         && priorite != "2"
                ? new MmcssConstat($"{tache}\\Priority", priorite, MmcssEffet.SansEffet,
                    Docteur.T("MM.PrioriteHigh", priorite))
                : new MmcssConstat($"{tache}\\Priority", Docteur.T("MM.PrioriteValeur", priorite), MmcssEffet.Agit,
                    Docteur.T("MM.Priorite", priorite, categorie));
    }

    private static MmcssConstat Inerte(RegistryKey cle, string nom, string citation)
    {
        var v = cle.GetValue(nom)?.ToString();
        return v is null
            ? new MmcssConstat(nom, null, MmcssEffet.NonLu, Docteur.T("MM.ChampAbsent"))
            // `SansEffet` meme quand la valeur est celle d'origine : l'affirmation porte sur ce
            // que le champ fait, pas sur qui l'a ecrit. Y toucher ne rapportera rien, et c'est
            // vrai avant comme apres.
            : new MmcssConstat(nom, v, MmcssEffet.SansEffet,
                Docteur.T("MM.SansEffet", citation));
    }

    private static int? Dword(string chemin, string nom)
    {
        try
        {
            using var cle = Registry.LocalMachine.OpenSubKey(chemin);
            return cle?.GetValue(nom) is int v ? v : null;
        }
        catch { return null; }
    }
}
