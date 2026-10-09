using System.Diagnostics;
using System.Text.RegularExpressions;
using DrDxt.Detection.Cpu;
using FluentAssertions;
using Xunit.Abstractions;

namespace DrDxt.Detection.Tests.Cpu;

// -----------------------------------------------------------------------
// 19/09/2026. Le coupable des dix secondes, trouve par chronometre.
//
// Trois tours que la boucle poursuit un plateau : cinq tests d'audit a 28,3 s **a 0,2 s
// pres** en execution complete (tour 116), une premiere hypothese (WMI) **refutee par la
// mesure** au tour 118, et cinq lecteurs cables nommes sans qu'aucun ne soit accuse.
//
// Mesure du jour, un chronometre par lecteur :
//
//     PowerSettingsReader           6,0 ms
//     CpuFrequencyReader         6963,1 ms   <-- 98 % du total
//     MmcssReader                   3,4 ms
//     VendorBiosInterfaceProbe     88,3 ms
//     NvmlHardwareProbe            50,3 ms
//
// La cause, dans `CpuFrequencyReader.Lire()` : elle appelle `Mesurer` Quatre fois, et chaque
// appel ouvre son compteur, l'amorce, **dort 300 ms**, puis lit. Quatre attentes en serie
// pour quatre compteurs du meme jeu, qu'un seul intervalle suffirait a amorcer.
//
// Ce qui rend ce defaut particulier : la methode voisine `LireParProcesseur` porte deja
// la lecon, ecrite noir sur blanc dans son propre resume — « Un seul intervalle d'attente
// pour tous les compteurs : les amorcer un par un avec 300 ms chacun ferait durer une lecture
// 28 fois trop longtemps ». Deux surfaces, la meme lecon, une seule l'applique. Et ce n'est
// pas qu'une affaire de tests : c'est le SCAN du client qui attend.
// -----------------------------------------------------------------------
public sealed class UneSeuleAttentePourQuatreCompteursTests(ITestOutputHelper sortie)
{
    private static string Source()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.Detection", "Cpu",
            "CpuFrequencyReader.cs"));
    }

    [Fact]
    public void Le_crible_lit_bien_le_lecteur()
    {
        // Temoin de contenu : sans lui, un fichier deplace rendrait tout vert sur du vide.
        Source().Should().Contain("class CpuFrequencyReader").And.Contain("NextValue");
    }

    [Fact]
    public void Lire_n_attend_QU_UNE_SEULE_fois_pour_ses_quatre_compteurs()
    {
        // Premiere version de ce test, ecrite il y a dix minutes : elle comptait les lignes
        // `Thread.Sleep` du corps de `Lire()`. La mutation qu'elle devait attraper -- remettre
        // l'attente dans la boucle -- ne contient qu'une seule ligne et **passait**. Le test
        // nommait la garantie sans la prouver, exactement ce que cette boucle traque ailleurs.
        //
        // L'attente est donc injectee, et on compte les appels REELS.
        var appels = 0;

        new CpuFrequencyReader().Lire(attendre: _ => appels++);

        appels.Should().BeLessThanOrEqualTo(1,
            "quatre compteurs du meme jeu s'amorcent ensemble et se lisent apres UN seul "
            + "intervalle. Le resume de `LireParProcesseur` porte deja cette lecon : c'est "
            + "le scan du client qui attend pendant les trois autres");
    }

    [Fact]
    public void Le_defaut_reste_une_VRAIE_attente_pour_le_produit()
    {
        // Le contrepoids : une couture qui changerait le defaut ferait lire des compteurs non
        // amorces -- un taux rend 0 au premier appel, et ce 0 passerait pour une frequence nulle.
        Source().Should().Contain("attendre ?? Thread.Sleep",
            "le produit garde son attente reelle quand personne n'en injecte d'autre");
    }

    [Fact]
    public void Les_quatre_mesures_sont_toujours_rendues()
    {
        // Ce que l'optimisation ne doit pas couter : la lecture reste complete. Sur une machine
        // sans ces compteurs, les quatre valent `null` — c'est une reponse, pas une panne.
        var chrono = Stopwatch.StartNew();
        var etat = new CpuFrequencyReader().Lire();
        chrono.Stop();

        sortie.WriteLine($"CpuFrequencyReader.Lire() : {chrono.Elapsed.TotalMilliseconds:0.0} ms");

        // Premiere version : `_ = (etat.A, etat.B, ...)` -- aucune assertion. Le garde
        // `AucunTestNePasseSansRienProuver` l'a designe a la premiere execution complete. Un
        // test qui n'affirme rien passe quoi qu'il arrive au produit.
        etat.Should().NotBeNull("une machine sans ces compteurs rend des null, pas une panne");

        // Chaque champ est soit non lu (null), soit un nombre fini. Jamais NaN : une valeur
        // absente qui se presenterait comme un nombre serait affichee au client comme une mesure.
        foreach (var valeur in new[] { etat.FrequenceNominaleMhz, etat.PourcentageDeNominal,
                                       etat.PartDeLaLimite, etat.IndicateursDeLimite })
            if (valeur is { } v)
                double.IsFinite(v).Should().BeTrue("une mesure rendue doit etre un nombre reel");
    }
}
