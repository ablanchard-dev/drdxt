using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// Les arrets brutaux de la machine, dates. Un arret brutal est un `Kernel-Power 41` : Windows a
/// redemarre sans avoir ete arrete proprement : gel, coupure, reset, gardien materiel.
///
/// Pourquoi un seam a soi plutot que le seam WMI deja en place : mesure du 06/09/2026 sur la
/// machine de reference, meme resultat (15 lignes), **16 701 ms en WMI `Win32_NTLogEvent` contre
/// 79 ms** par le lecteur de journal. 211 fois. Reutiliser aurait ete plus paresseux a ecrire et
/// aurait mis seize secondes dans chaque scan.
/// </summary>
public interface IUnexpectedShutdownLog
{
    /// <summary>Les dates des arrets brutaux depuis <paramref name="earliest"/>, plus recentes
    /// d'abord. Une liste vide est une reponse ; un echec de lecture est un `Fail`, jamais une
    /// liste vide : la difference decide de ce que le produit a le droit de dire.</summary>
    Result<IReadOnlyList<DateTimeOffset>> Since(DateTimeOffset earliest);

    /// <summary>Les memes arrets, avec ce que l'evenement dit de chacun. Par defaut, un lecteur qui
    /// ne sait rendre que des dates rend des arrets sans detail : ni ecran bleu, ni bouton, ni
    /// transition : « pas lu » ne s'invente pas en ecran bleu.</summary>
    Result<IReadOnlyList<ArretBrutal>> Detail(DateTimeOffset earliest)
    {
        var dates = Since(earliest);
        return dates.IsOk
            ? Result<IReadOnlyList<ArretBrutal>>.Ok(dates.Value!.Select(d => new ArretBrutal(d, 0, false, false)).ToList())
            : Result<IReadOnlyList<ArretBrutal>>.Fail(ErrorCodes.DetectionFailed, dates.Error?.Message ?? "illisible");
    }
}

/// <summary>
/// Un arret brutal, avec ce que Windows en a note.
///
/// `Kernel-Power 41` porte trois champs qui changent tout
/// (doc Microsoft « Event ID 41 », version du 12/02/2026) :
/// - `BugcheckCode` non nul : un ecran bleu, et son code ;
/// - `PowerButtonTimestamp` non nul : quelqu'un a maintenu le bouton ;
/// - `SleepInProgress` non nul : une transition d'alimentation (veille, extinction) etait en cours.
///
/// Mesure du 16/09/2026 sur la machine de reference : onze evenements en 90 jours, dont 2 ecrans
/// bleus, 1 arret sans code, 1 appui long sur le bouton et 7 pendant une transition. Les compter
/// tous pareil ferait dire « nous ne savons pas distinguer » alors que le journal distingue.
/// </summary>
public sealed record ArretBrutal(DateTimeOffset Quand, int CodeArret, bool BoutonMaintenu, bool TransitionEnCours)
{
    public bool EcranBleu => CodeArret != 0;

    /// <summary>La famille probable, jamais une cause affirmee (voir <see cref="FamilleDArret"/>).</summary>
    public FamilleDArret Famille => ClasseurDArrets.Classer(CodeArret, TransitionEnCours);

    public ArretClasse Classe => new(Quand, Famille, CodeArret);

    /// <summary>Le code d'arret au format de la documentation Microsoft (huit chiffres hexadecimaux).</summary>
    public string CodeHexa => "0x" + CodeArret.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>Un appui long sur le bouton est un geste de l'utilisateur : il ne compte pas comme
    /// une instabilite. Le reste reste compte : une transition interrompue n'est pas innocente pour
    /// autant, et rien ici ne permet de l'affirmer.</summary>
    public static IReadOnlyList<ArretBrutal> HorsBouton(IEnumerable<ArretBrutal> arrets)
        => arrets.Where(a => !a.BoutonMaintenu).ToList();

    public static ArretBrutal DepuisXml(DateTimeOffset quand, string xml)
    {
        var donnees = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var d in XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "Data"))
                if (d.Attribute("Name")?.Value is { } nom) donnees[nom] = d.Value.Trim();
        }
        catch (System.Xml.XmlException)
        {
            // XML illisible : aucun champ lu, donc aucun ecran bleu ni bouton invente.
        }

        long Nombre(string nom) => donnees.TryGetValue(nom, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        bool Actif(string nom) => donnees.TryGetValue(nom, out var v)
                                  && (bool.TryParse(v, out var b) ? b : Nombre(nom) != 0);

        return new ArretBrutal(quand, (int)Nombre("BugcheckCode"), Nombre("PowerButtonTimestamp") != 0, Actif("SleepInProgress"));
    }
}

/// <summary>
/// La lecture reelle. Le journal `System` est lisible sans elevation (releve le 06/09/2026 sur la
/// machine de reference : 15 evenements rendus en session non elevee) : c'est ce qui rend ce
/// constat possible dans un produit qui ne demande l'UAC que pour ecrire.
/// </summary>
public sealed class WindowsUnexpectedShutdownLog : IUnexpectedShutdownLog
{
    // Kernel-Power 41 : « le systeme a redemarre sans s'etre arrete proprement au prealable ».
    private const int UnexpectedShutdownEventId = 41;

    // Borne dure : on ne rapatrie jamais tout le journal, meme si la fenetre demandee est large.
    private const int MaxEvents = 200;

    public Result<IReadOnlyList<DateTimeOffset>> Since(DateTimeOffset earliest)
    {
        var lus = Detail(earliest);
        return lus.IsOk
            ? Result<IReadOnlyList<DateTimeOffset>>.Ok(ArretBrutal.HorsBouton(lus.Value!).Select(a => a.Quand).ToList())
            : Result<IReadOnlyList<DateTimeOffset>>.Fail(ErrorCodes.DetectionFailed, lus.Error?.Message ?? "illisible");
    }

    public Result<IReadOnlyList<ArretBrutal>> Detail(DateTimeOffset earliest)
    {
        try
        {
            // Le filtre est fait par le journal, pas par nous : c'est lui qui rend la lecture
            // rapide. `TimeCreated` est en heure locale ; on le convertit, on ne le compare pas
            // a une date UTC sans conversion.
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[(EventID={UnexpectedShutdownEventId})]]")
            {
                ReverseDirection = true,
            };

            var arrets = new List<ArretBrutal>();
            using var reader = new EventLogReader(query);
            for (EventRecord? e = reader.ReadEvent(); e is not null && arrets.Count < MaxEvents; e = reader.ReadEvent())
            {
                using (e)
                {
                    if (e.TimeCreated is not { } t) continue;
                    var when = new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero);
                    if (when >= earliest) arrets.Add(ArretBrutal.DepuisXml(when, e.ToXml()));
                }
            }

            arrets.Sort((a, b) => b.Quand.CompareTo(a.Quand));
            return Result<IReadOnlyList<ArretBrutal>>.Ok(arrets);
        }
        catch (Exception ex)
        {
            // Journal absent, service arrete, acces refuse sur une machine durcie : on le dit.
            // Rendre une liste vide ferait passer « je n'ai pas pu lire » pour « tout va bien »,
            // et c'est precisement le mensonge que ce produit existe pour ne pas faire.
            return Result<IReadOnlyList<ArretBrutal>>.Fail(
                ErrorCodes.DetectionFailed, "Journal System illisible : " + ex.Message);
        }
    }
}
