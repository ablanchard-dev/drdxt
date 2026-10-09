using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.Infrastructure.Tests;

/// <summary>
/// Retour du 16/09/2026 : les mots en capitales pour insister (« n'est PAS », « fréquences
/// Différentes ») font lire le texte comme genere, au meme titre que les tirets cadratins. Les sigles
/// sont declares ; les etiquettes courtes (moins de 40 caracteres) sont une mise en page, pas une phrase.
/// </summary>
public sealed class ResxSansCapitalesDInsistanceTests
{
    private static readonly HashSet<string> Sigles = new(StringComparer.Ordinal)
    {
        "BIOS", "GPU", "BAR", "XMP", "EXPO", "CSM", "FPS", "CCD", "NVIDIA", "CPU", "VRR", "UEFI", "CPPC",
        "AMD", "RAM", "DVR", "CMOS", "JEDEC", "SSD", "SYNC", "HDR", "DDU", "SAM", "RSS", "VBIOS", "BITS", "API", "PCI", "XDA",
        // 25/09/2026 : l'outil de reparation de Microsoft, nomme tel quel dans la carte « Réparer Windows ».
        "DISM",
        // 29/09/2026 : la marque de la carte mère, nommée avec son réglage « CPU Cooler Tuning » (page Processeur).
        "MSI",
        // 06/10/2026 : la marque dont les manuels demandent A2/B2 (« Finding.Memory.SlotsNotA2B2 »).
        "ASUS",
    };

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public void Aucune_phrase_affichee_n_insiste_en_capitales(string langue)
    {
        var manager = new ResourceManager("DrDxt.Infrastructure.Resources.Strings", typeof(ResxLocalizer).Assembly);
        var jeu = manager.GetResourceSet(new CultureInfo(langue), createIfNotExists: true, tryParents: false)
                  ?? throw new InvalidOperationException("jeu de ressources introuvable : " + langue);

        // Temoin de lecture : « aucun fautif » est aussi ce que rend un crible qui ne lit plus
        // rien. Sans cette ligne, un jeu de ressources vide -- ou un filtre de longueur qui ne
        // mord plus -- laisserait passer n'importe quelle phrase en capitales.
        var phrasesExaminees = jeu.Cast<DictionaryEntry>().Count(e => e.Value is string v && v.Length >= 40);
        phrasesExaminees.Should().BeGreaterThan(50, "le crible doit avoir lu de vraies phrases affichees");

        var fautifs = jeu.Cast<DictionaryEntry>()
            .Where(e => e.Value is string v && v.Length >= 40
                        && Regex.Matches(v, @"(?<![\w{])[A-ZÉÈÀÊÔÂÎÛÇ]{3,}(?![\w}])").Any(m => !Sigles.Contains(m.Value)))
            .Select(e => (string)e.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        fautifs.Should().BeEmpty("les capitales d'insistance font lire le texte comme genere");
    }
}
