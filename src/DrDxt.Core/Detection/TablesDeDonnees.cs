using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DrDxt.Core;

/// <summary>
/// Les tables que DrDXT peut recevoir sans changer de binaire : aujourd'hui le radar des régressions connues. Ce sont des
/// données, jamais du code : le schéma est fermé, une clé inconnue ou une ligne hors forme refusent tout le contenu, et
/// chaque ligne porte l'adresse de sa source et la date où elle a été relevée. La version vient du paquet signé
/// (<c>DrDxt.Infrastructure.Updates.DonneesSignees</c>) ; les tables livrées avec l'App sont la version 0.
/// </summary>
public sealed record TablesDeDonnees(int Version, DateOnly? Publie, IReadOnlyList<RegleDeRegression> Radar)
{
    /// <summary>Ce que l'App connaît sans rien recevoir. Le radar part vide : aucune régression n'y entre sans sa source
    /// primaire relue, et une table vide mais juste vaut mieux qu'une ligne sans source.</summary>
    public static TablesDeDonnees Embarquees { get; } = new(0, null, []);

    /// <summary>Au-delà, le contenu est refusé : un radar de mille lignes n'est plus une table relue ligne à ligne.</summary>
    public const int LignesMax = 500;

    /// <summary>Un texte du docteur tient en quelques phrases ; au-delà, ce n'est plus une phrase.</summary>
    public const int TexteMax = 600;

    private static readonly HashSet<string> ClesRacine = ["publie", "radar"];
    private static readonly HashSet<string> ClesRegle =
        ["id", "kb", "corrigeePar", "carte", "piloteDepuis", "piloteJusqua", "processeur", "fr", "en", "source", "releve"];
    private static readonly Regex Identifiant = new("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.Compiled);
    private static readonly Regex Kb = new("^KB[0-9]{6,8}$", RegexOptions.Compiled);
    private static readonly Regex VersionDePilote = new("^[0-9]{1,6}(\\.[0-9]{1,6}){1,3}$", RegexOptions.Compiled);

    /// <summary>Pur : le contenu d'un paquet, lu contre le schéma fermé. Le moindre écart refuse tout, sans lever.</summary>
    public static Result<TablesDeDonnees> Lire(string contenu, int version)
    {
        try
        {
            using var doc = JsonDocument.Parse(contenu, new JsonDocumentOptions { MaxDepth = 4 });
            var racine = doc.RootElement;
            if (racine.ValueKind != JsonValueKind.Object) return Refus("the root is not an object");
            foreach (var p in racine.EnumerateObject())
                if (!ClesRacine.Contains(p.Name)) return Refus($"unknown key at the root: {p.Name}");
            if (!racine.TryGetProperty("publie", out var publie) || Date(publie) is not { } datePublie)
                return Refus("publication date missing or unreadable");
            if (!racine.TryGetProperty("radar", out var radar) || radar.ValueKind != JsonValueKind.Array)
                return Refus("radar table missing");
            if (radar.GetArrayLength() > LignesMax) return Refus("radar table too long");

            var regles = new List<RegleDeRegression>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ligne in radar.EnumerateArray())
            {
                var lue = LireRegle(ligne);
                if (!lue.IsOk) return Result<TablesDeDonnees>.Fail(lue.Error!.Value.Code, lue.Error.Value.Message);
                if (!ids.Add(lue.Value!.Id)) return Refus($"duplicate id: {lue.Value.Id}");
                regles.Add(lue.Value);
            }
            return Result<TablesDeDonnees>.Ok(new TablesDeDonnees(version, datePublie, regles));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidOperationException)
        {
            return Refus("unreadable content: " + ex.Message);
        }
    }

    private static Result<RegleDeRegression> LireRegle(JsonElement l)
    {
        if (l.ValueKind != JsonValueKind.Object) return RefusRegle("a row is not an object");
        foreach (var p in l.EnumerateObject())
        {
            if (!ClesRegle.Contains(p.Name)) return RefusRegle($"unknown key in a row: {p.Name}");
            if (p.Value.ValueKind != JsonValueKind.String) return RefusRegle($"{p.Name} is not a string");
            if (!TexteSain(p.Value.GetString())) return RefusRegle($"{p.Name}: empty, too long, or with a control character");
        }
        string? S(string cle) => l.TryGetProperty(cle, out var v) ? v.GetString() : null;

        var id = S("id");
        if (id is null || !Identifiant.IsMatch(id)) return RefusRegle("id missing or malformed");
        var kb = S("kb");
        var corrigee = S("corrigeePar");
        if ((kb is not null && !Kb.IsMatch(kb)) || (corrigee is not null && !Kb.IsMatch(corrigee)))
            return RefusRegle($"{id}: malformed KB number");
        var depuis = S("piloteDepuis");
        var jusqua = S("piloteJusqua");
        if ((depuis is not null && !VersionDePilote.IsMatch(depuis)) || (jusqua is not null && !VersionDePilote.IsMatch(jusqua)))
            return RefusRegle($"{id}: malformed driver version");
        var carte = S("carte");
        if ((depuis is not null || jusqua is not null) && carte is null)
            return RefusRegle($"{id}: a driver version without a card maker");
        var processeur = S("processeur");
        if (kb is null && carte is null && processeur is null) return RefusRegle($"{id}: the rule targets nothing");
        if (S("fr") is not { } fr || S("en") is not { } en) return RefusRegle($"{id}: both the French and the English text are required");
        if (S("source") is not { } source || !Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return RefusRegle($"{id}: source missing or not https");
        if (!l.TryGetProperty("releve", out var r) || Date(r) is not { } releve) return RefusRegle($"{id}: read date missing");
        return Result<RegleDeRegression>.Ok(new RegleDeRegression(id, kb, corrigee, carte, depuis, jusqua, processeur, fr, en, source, releve));
    }

    private static bool TexteSain(string? t) => !string.IsNullOrWhiteSpace(t) && t.Length <= TexteMax && !t.Any(char.IsControl);

    private static DateOnly? Date(JsonElement e)
        => e.ValueKind == JsonValueKind.String
           && DateOnly.TryParseExact(e.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static Result<TablesDeDonnees> Refus(string pourquoi) => Result<TablesDeDonnees>.Fail(ErrorCodes.DonneesHorsSchema, pourquoi);
    private static Result<RegleDeRegression> RefusRegle(string pourquoi) => Result<RegleDeRegression>.Fail(ErrorCodes.DonneesHorsSchema, pourquoi);
}

/// <summary>
/// Une régression publiée par Microsoft ou un fabricant : une mise à jour de Windows (par son numéro KB), une plage de
/// versions d'un pilote, ou un processeur, qui coûte des performances ou de la stabilité. Chaque critère donné doit
/// correspondre ; <see cref="CorrigeePar"/> installé met la machine hors de cause.
/// </summary>
public sealed record RegleDeRegression(
    string Id, string? Kb, string? CorrigeePar, string? Carte, string? PiloteDepuis, string? PiloteJusqua, string? Processeur,
    string Fr, string En, string Source, DateOnly Releve);

/// <summary>
/// Les tables en service dans cette App : celles livrées avec elle, remplacées par un paquet signé plus récent quand il y
/// en a un. Une version n'en remplace jamais une plus récente.
/// </summary>
public static class DonneesEnService
{
    private sealed class Boite { public TablesDeDonnees Tables = TablesDeDonnees.Embarquees; }

    private static readonly Boite s_globale = new();
    private static readonly AsyncLocal<Boite?> s_portee = new();
    private static readonly Lock s_verrou = new();

    private static Boite Courante => s_portee.Value ?? s_globale;

    public static TablesDeDonnees Courantes { get { lock (s_verrou) return Courante.Tables; } }

    /// <summary>Rend vrai si <paramref name="tables"/> est plus récente que ce qui est en service, et l'installe.</summary>
    public static bool Installer(TablesDeDonnees tables)
    {
        lock (s_verrou)
        {
            if (tables.Version <= Courante.Tables.Version) return false;
            Courante.Tables = tables;
            return true;
        }
    }

    /// <summary>Pour un test : des données en service propres à ce fil d'exécution, reparties des tables embarquées.</summary>
    public static IDisposable Portee()
    {
        var avant = s_portee.Value;
        s_portee.Value = new Boite();
        return new Retour(() => s_portee.Value = avant);
    }

    private sealed class Retour(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
