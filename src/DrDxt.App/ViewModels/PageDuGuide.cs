using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>
/// L'adresse que le QR du guide BIOS ouvre sur le téléphone : la page statique de DrDXT (docs/guide-bios/index.html),
/// avec le guide entier dans le fragment (#…). Un navigateur n'envoie jamais le fragment au serveur : le guide reste
/// sur le téléphone, la page n'appelle rien. Format, version 1 : JSON (<see cref="Contenu"/>), compressé en deflate brut,
/// puis base64url sans remplissage ; la page le décode par DecompressionStream("deflate-raw").
///
/// N'importe qui peut forger une adresse : la page n'affiche donc aucun texte libre comme consigne. Chaque consigne, ligne
/// de secours ou note est un code d'une liste fermée (<see cref="ClesDeLaPage"/>, ses phrases sont écrites dans la page en
/// français et en anglais) ; le nom de la carte, les touches et les valeurs de menu suivent des règles strictes
/// (<see cref="Regles"/>, les mêmes dans la page) ; aucune ligne ne dit « vérifié », et une ligne fixe rappelle de ne
/// suivre que le QR scanné depuis DrDXT. Un guide que ces règles refusent garde le QR en texte brut.
/// Seul ce que le guide affiche déjà y entre. Aucun numéro de série, aucun nom de PC ni de compte.
/// </summary>
internal static class PageDuGuide
{
    /// <summary>La seule adresse de la page (GitHub Pages du dépôt public, dossier docs/).</summary>
    public const string Adresse = "https://ablanchard-dev.github.io/drdxt/guide-bios/";

    /// <summary>Faux tant que la page ne répond pas en ligne : le QR garde alors le texte brut, lisible sans réseau. À passer
    /// à vrai au moment de la publication, après avoir vu <see cref="Adresse"/> répondre.</summary>
    public const bool EnLigne = false;

    public const int Version = 1;

    /// <summary>Les phrases que la page connaît, par leur clé ; leur code est le dernier segment de la clé.</summary>
    public static readonly IReadOnlyList<string> ClesDeLaPage =
    [
        "Bios.Guide.Step.Xmp", "Bios.Guide.Step.Above4G", "Bios.Guide.Step.Csm", "Bios.Guide.Step.ReBar", "Bios.Guide.Step.X3dCppc",
        "Bios.Guide.Step.Pcie", "Bios.Guide.Step.BiosVersion", "Bios.Guide.Step.Generic",
        "Bios.Guide.Recovery.NoBootNormal", "Bios.Guide.Recovery.MemoryTraining", "Bios.Guide.Recovery.ClearCmos",
        "Bios.Guide.Recovery.LoadDefaults", "Bios.Guide.Recovery.OneChangeAtATime", "Bios.Guide.Recovery.RebarNeedsCsmOff",
        "Scan.Bios.Guide.CsmSuppressed",
    ];

    /// <summary>Les règles que la page applique, écrites telles quelles dans son bloc « regles » (un test les tient
    /// égales). Valeur : les caractères des chemins de menu des guides du dépôt, plus « → » et « = » de la composition.
    /// Lien et tension sont refusés partout : une adresse forgée ne fait pas lire « 1.55 V » ni une adresse.</summary>
    public static readonly IReadOnlyDictionary<string, string> Regles = new Dictionary<string, string>
    {
        ["valeur"] = "^[A-Za-z0-9 ()./>=→-]{1,300}$",
        ["carte"] = "^[A-Za-z0-9 ().,&/+_-]{0,120}$",
        // « Del or F2 (watch the boot screen) » : la touche du guide générique.
        ["touche"] = "^[A-Za-z0-9 ()/+-]{0,40}$",
        ["manuel"] = "^https://www\\.msi\\.com/Motherboard/[A-Za-z0-9+-]+/support$",
        ["lien"] = "https?|www",
        // Sans drapeau de casse : la page compile la règle telle quelle (« u »), la casse est donc dans le motif même.
        ["tension"] = "\\d\\s*[mM]?[vV](?:[oO][lL][tT][sS]?)?\\b",
    };

    public static string Code(string cle) => cle[(cle.LastIndexOf('.') + 1)..];

    private static readonly HashSet<string> Codes = ClesDeLaPage.Select(Code).ToHashSet(StringComparer.Ordinal);

    /// <summary>Le guide tel que la page le lit. Noms courts : chaque octet compte dans un QR.</summary>
    internal sealed record Contenu(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("l")] string Langue,
        [property: JsonPropertyName("c")] string Carte,
        [property: JsonPropertyName("e")] string ToucheEntree,
        [property: JsonPropertyName("s")] string ToucheSauvegarde,
        [property: JsonPropertyName("p")] IReadOnlyList<string[]> Etapes,
        [property: JsonPropertyName("h")] IReadOnlyList<string> SecoursHaut,
        [property: JsonPropertyName("b")] IReadOnlyList<string>? SecoursBas,
        [property: JsonPropertyName("n")] string? NoteCsm,
        [property: JsonPropertyName("m")] string? Manuel);

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Ce que le guide affiche, rien de plus, chaque phrase par son code. Le secours du bas n'est porté que s'il
    /// diffère de celui du haut. Une phrase hors de la liste garde sa clé : <see cref="Accepte"/> la refusera.</summary>
    public static Contenu Depuis(PersonalizedGuide guide, BiosGuideViewModel g, string langue)
    {
        var haut = guide.Recovery.Top.Select(s => Code(s.InstructionKey)).ToList();
        var bas = guide.Recovery.Bottom.Select(s => Code(s.InstructionKey)).ToList();
        return new(Version, langue, g.BoardLabel, g.EnterKey, g.SaveKey,
            guide.Steps.Select(s => s.ExactValue is { Length: > 0 } v ? new[] { Code(s.InstructionKey), v } : [Code(s.InstructionKey)]).ToList(),
            haut,
            bas.SequenceEqual(haut) ? null : bas,
            g.CsmSuppressed ? Code("Scan.Bios.Guide.CsmSuppressed") : null,
            g.LienManuel);
    }

    private static bool Suit(string regle, string texte, RegexOptions options = RegexOptions.None)
        => Regex.IsMatch(texte, Regles[regle], options | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static bool Propre(string texte) => !Suit("lien", texte, RegexOptions.IgnoreCase) && !Suit("tension", texte);

    /// <summary>Les mêmes règles que la page : un contenu qu'elle refuserait n'est jamais encodé.</summary>
    public static bool Accepte(Contenu c)
        => c.V == Version && c.Langue is "fr" or "en"
           && Suit("carte", c.Carte) && Propre(c.Carte) && Suit("touche", c.ToucheEntree) && Propre(c.ToucheEntree)
           && Suit("touche", c.ToucheSauvegarde) && Propre(c.ToucheSauvegarde)
           && c.Etapes.Count <= 20
           && c.Etapes.All(e => e.Length is 1 or 2 && Codes.Contains(e[0]) && (e.Length == 1 || (Suit("valeur", e[1]) && Propre(e[1]))))
           && c.SecoursHaut.Count is > 0 and <= 20 && c.SecoursHaut.All(Codes.Contains)
           && (c.SecoursBas is null || (c.SecoursBas.Count is > 0 and <= 20 && c.SecoursBas.All(Codes.Contains)))
           && (c.NoteCsm is null || Codes.Contains(c.NoteCsm))
           && (c.Manuel is null || Suit("manuel", c.Manuel));

    public static string Fragment(Contenu contenu)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(contenu, Options);
        using var sortie = new MemoryStream();
        using (var deflate = new DeflateStream(sortie, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(json);
        return Convert.ToBase64String(sortie.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>L'adresse de la page, ou null quand la page refuserait ce guide.</summary>
    public static string? UrlSiAcceptee(Contenu contenu) => Accepte(contenu) ? Adresse + "#" + Fragment(contenu) : null;

    /// <summary>L'inverse exact de <see cref="Fragment"/>, selon la même spécification que la page ; null si invalide.</summary>
    public static Contenu? Lire(string fragment)
    {
        try
        {
            var b64 = fragment.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var entree = new DeflateStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
            using var json = new MemoryStream();
            entree.CopyTo(json);
            return JsonSerializer.Deserialize<Contenu>(json.ToArray(), Options) is { } c && Accepte(c) ? c : null;
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or JsonException or ArgumentException) { return null; }
    }
}
