using DrDxt.Profiles;
using FluentAssertions;
using Xunit;

namespace DrDxt.Profiles.Tests;

/// <summary>
/// La vue payante de l'ecran de scan affiche, pour chaque levier recommande, `Claim` et `Source`
/// tels quels (ScanView, bloc « UNLOCKED »). Le 17/09/2026, ces textes etaient en anglais, avec du
/// jargon et des chemins de fichiers du depot en guise de source (« research/2026-06-22-… R3 »).
/// Un client qui paie ne lit ni l'anglais du depot ni ses chemins : ce gate tient les deux champs
/// en francais lisible, sans tiret cadratin (le tic du texte genere) et sans chemin interne.
/// </summary>
public class TextesDuCatalogueLisiblesParLeClientTests
{
    private static readonly string[] TellsAnglais = [" the ", " and ", " not ", " when ", " on: ", " only "];

    [Fact]
    public void Chaque_affirmation_et_chaque_source_du_catalogue_se_lit_en_francais_sans_chemin_interne()
    {
        var fautes = new List<string>();
        foreach (var profil in new ChipProfileCatalog().All())
        foreach (var sel in profil.Selections)
        foreach (var (champ, texte) in new[] { ("Claim", sel.Evidence.Claim), ("Source", sel.Evidence.Source) })
        {
            var ou = $"{profil.Id} / {sel.TweakId} / {champ}";
            if (texte.Contains('—')) fautes.Add(ou + " : tiret cadratin");
            if (texte.Contains(".md", StringComparison.OrdinalIgnoreCase) || texte.Contains('/') && texte.Contains("research", StringComparison.OrdinalIgnoreCase))
                fautes.Add(ou + " : chemin de fichier interne");
            foreach (var tell in TellsAnglais)
                if (texte.Contains(tell, StringComparison.Ordinal)) fautes.Add(ou + $" : anglais « {tell.Trim()} »");
        }

        fautes.Should().BeEmpty();
    }

    // 23/09/2026 — le chemin etait parti, le document interne etait reste. Vu sur un scan reel :
    // « Plan north star §2.6 et cahier §3 ». Le crible du dessus attrapait `research/…md`, pas le NOM
    // d'un document que personne hors du projet ne peut ouvrir. Une source que le lecteur ne peut pas
    // verifier n'est pas une source, c'est une autorite qu'on lui demande de croire.
    private static readonly System.Text.RegularExpressions.Regex ReferenceInterne = new(
        @"§|\bcahier\b|north star|revalidation|brief de recherche|\bplan north|\bR\d\b|d[ée]cision undervolt|option A contre B",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    [Fact]
    public void Le_crible_des_references_internes_mord_sur_la_phrase_vue_a_l_ecran()
        => ReferenceInterne.IsMatch("Plan north star §2.6 et cahier §3 : réglages système").Should().BeTrue();

    [Fact]
    public void Aucune_affirmation_ni_source_ne_renvoie_a_un_document_interne()
    {
        var fautes = new List<string>();
        var lus = 0;
        foreach (var profil in new ChipProfileCatalog().All())
        foreach (var sel in profil.Selections)
        foreach (var (champ, texte) in new[] { ("Claim", sel.Evidence.Claim), ("Source", sel.Evidence.Source) })
        {
            lus++;
            var m = ReferenceInterne.Match(texte);
            if (m.Success) fautes.Add($"{profil.Id} / {sel.TweakId} / {champ} : « {m.Value} »");
        }

        lus.Should().BeGreaterThan(5, "le crible doit avoir lu le catalogue");
        fautes.Should().BeEmpty("l'ecran montre ces textes tels quels a quelqu'un qui n'a pas le depot");
    }
}
