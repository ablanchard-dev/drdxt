using System.Diagnostics;
using DrDxt.Detection.Cpu;
using FluentAssertions;
using Xunit.Abstractions;

namespace DrDxt.Detection.Tests.Cpu;

// -----------------------------------------------------------------------
// 19/09/2026. L'instrument du tour 122 accuse enfin quelqu'un.
//
// Le chronometre lecteur par lecteur, ecrit pour arreter de supposer, rend aujourd'hui :
//
//     PowerSettingsReader                 6,5 ms
//     CpuFrequencyReader               5483,3 ms      <-- 97,4 % du total
//     MmcssReader                         3,3 ms
//     VendorBiosInterfaceProbe           91,7 ms
//     NvmlHardwareProbe                  44,5 ms
//     TOTAL                            5629,4 ms
//
// Le tour precedent avait deja retire trois attentes sur quatre de ce lecteur. Il n'en reste
// qu'une, de 300 ms. Les 5 200 ms restantes ne sont donc pas l'attente — et c'etait toute
// l'hypothese de depart.
//
// Mon hypothese, et la mesure qui l'a rabotee. J'ai accuse `Localiser`, corrige, puis
// remesure : le total tombe a 4 996 ms. **490 ms gagnes, pas 5 200.** Attribution en trois
// phases dans un processus neuf : 427 ms pour resoudre cinq noms tables froides, 4 543 ms
// pour le premier `Lire()` une fois les noms resolus, 11,6 ms a chaud. Le plateau ouvert
// depuis le tour 116 est enfin nomme — c'est l'initialisation de perflib au premier
// `new PerformanceCounter` du processus, et elle n'est pas dans notre code.
//
// Ce que ce fichier garde reste vrai et utile : `Localiser` relisait tout, a chaque nom.
//   1. lit `Perflib\009\Counter` — la table COMPLETE des compteurs Windows en anglais ;
//   2. lit `Perflib\CurrentLanguage\Counter` — la meme table en francais ;
//   3. balaie la premiere pour trouver l'index, puis la seconde pour trouver le nom localise.
//
// Ces tables portent des dizaines de milliers d'entrees. Et `Lire()` appelle `Localiser`
// CINQ fois : une pour le jeu, une par compteur ouvert. Dix lectures de registre et cinq
// balayages imbriques pour resoudre cinq noms qui ne changent jamais de la vie du processus.
//
// Ce que paie l'utilisateur. Ce n'est pas un cout de test : c'est le scan du client qui
// attend, et `LireParProcesseur` — la boucle de surveillance, appelee en continu pendant une
// partie — refait le meme travail a chaque tour.
//
// ══ ce que ce fichier affirme, et qui ne depend d'aucun chronometre ══
// Une garde de duree serait une decoration : elle dependrait de la machine et de la charge.
// Ce qui se prouve, c'est le compte des lectures de registre, par une couture. La table est
// lue une fois par langue, pas une fois par nom demande.
// -----------------------------------------------------------------------
/// <summary>
/// Pourquoi cette classe tourne seule — une erreur a moi, trouvee en suite complete.
///
/// Ces tests echangent la source des tables, un etat STATIQUE partage par tout l'assemblage.
/// Quatorze classes de ce projet lisent les memes compteurs, et xunit fait tourner les
/// collections en parallele : seuls, mes six tests passaient ; dans la suite complete, deux
/// rougissaient — une autre classe relisait la table pendant que je comptais, ou lisait la
/// mienne pendant que je l'avais rendue muette. Le meme piege qu'au tour 125 : un test vert
/// isole et rouge avec les autres ne prouve pas ce qu'il dit.
/// </summary>
[CollectionDefinition(LaTableDesCompteursNeSeRelitPasAChaqueNomTests.NomDeLaCollection,
                      DisableParallelization = true)]
public sealed class CompteursDePerformanceCollection;

[Collection(LaTableDesCompteursNeSeRelitPasAChaqueNomTests.NomDeLaCollection)]
public sealed class LaTableDesCompteursNeSeRelitPasAChaqueNomTests(ITestOutputHelper sortie)
    : IDisposable
{
    internal const string NomDeLaCollection = "compteurs de performance";

    private readonly Func<string, string[]?> _source = CpuFrequencyReader.SourceDesTables;

    public void Dispose()
    {
        CpuFrequencyReader.SourceDesTables = _source;
        CpuFrequencyReader.OublierLesTables();
    }

    /// <summary>Compte les lectures de table pendant un geste, table oubliee au depart.</summary>
    private int LecturesPendant(Action geste)
    {
        var vraie = _source;
        var lectures = 0;
        CpuFrequencyReader.OublierLesTables();
        CpuFrequencyReader.SourceDesTables = langue => { lectures++; return vraie(langue); };
        geste();
        return lectures;
    }

    [Fact]
    public void Le_temoin_les_noms_se_resolvent_vraiment_sur_cette_machine()
    {
        // Sans lui, une table illisible rendrait `null` partout et tout le reste du fichier
        // serait vert sur du vide : zero nom resolu, donc zero relecture a compter.
        CpuFrequencyReader.OublierLesTables();

        CpuFrequencyReader.Localiser("Processor Information").Should().NotBeNull(
            "ce depot lit les compteurs par leur nom anglais et les relit localises ; si cette "
            + "table n'est pas lisible, aucun des tests de ce fichier ne prouve quoi que ce soit");
    }

    [Fact]
    public void Cinq_noms_demandes_ne_font_pas_dix_lectures_de_registre()
    {
        var lectures = LecturesPendant(() =>
        {
            CpuFrequencyReader.Localiser("Processor Information");
            CpuFrequencyReader.Localiser("Processor Frequency");
            CpuFrequencyReader.Localiser("% Processor Performance");
            CpuFrequencyReader.Localiser("% of Maximum Frequency");
            CpuFrequencyReader.Localiser("Performance Limit Flags");
        });

        sortie.WriteLine($"cinq noms demandes -> {lectures} lecture(s) de table");

        lectures.Should().BeLessThanOrEqualTo(2,
            "il y a DEUX tables — l'anglaise et la localisee — et elles ne changent pas de la "
            + "vie du processus. Avant correctif : dix lectures, deux par nom demande");
    }

    [Fact]
    public void Un_second_scan_ne_relit_plus_rien()
    {
        // La boucle de surveillance rappelle ces lecteurs en continu pendant une partie.
        LecturesPendant(() => CpuFrequencyReader.Localiser("Processor Information"));

        var lectures = 0;
        var vraie = _source;
        CpuFrequencyReader.SourceDesTables = langue => { lectures++; return vraie(langue); };

        CpuFrequencyReader.Localiser("Processor Information");
        CpuFrequencyReader.Localiser("Processor Frequency");

        lectures.Should().Be(0, "un nom deja resolu ne redemande rien au registre");
    }

    [Fact]
    public void Une_table_illisible_n_est_PAS_retenue_comme_une_reponse()
    {
        // Contrepoids, et le vrai risque de tout cache : si une lecture ratee etait memorisee,
        // un incident passager condamnerait le lecteur pour toute la duree du processus —
        // l'application afficherait « frequence non lue » jusqu'au prochain redemarrage.
        CpuFrequencyReader.OublierLesTables();
        CpuFrequencyReader.SourceDesTables = _ => null;

        CpuFrequencyReader.Localiser("Processor Information").Should().BeNull();

        CpuFrequencyReader.SourceDesTables = _source;

        CpuFrequencyReader.Localiser("Processor Information").Should().NotBeNull(
            "la table redevenue lisible doit etre relue : un echec n'est pas une reponse");
    }

    [Fact]
    public void Un_nom_inconnu_rend_toujours_null_et_ne_contamine_pas_les_autres()
    {
        // Second contrepoids : retenir les reponses ne doit pas se mettre a INVENTER un nom,
        // ni rendre faux un nom voisin. UNKNOWN reste UNKNOWN.
        CpuFrequencyReader.OublierLesTables();

        CpuFrequencyReader.Localiser("Ce Compteur N Existe Nulle Part").Should().BeNull();
        CpuFrequencyReader.Localiser("Processor Information").Should().NotBeNull();
    }

    [Fact]
    public void Ce_que_coute_une_lecture_de_frequence_avant_et_apres()
    {
        // Instrument, pas une garde : la duree depend de la machine et de la charge. Il rend
        // la question mesurable au lieu de la laisser ouverte un tour de plus.
        CpuFrequencyReader.OublierLesTables();
        var lecteur = new CpuFrequencyReader();

        // Trois phases separees, pour ATTRIBUER le cout au lieu de le deviner :
        //   1. resoudre les cinq noms, tables froides — c'est ce que ce tour corrige ;
        //   2. le premier `Lire()` une fois les noms resolus — il reste l'ouverture des
        //      compteurs eux-memes, que ce tour ne touche pas ;
        //   3. le second `Lire()`, tout chaud.
        var chrono = Stopwatch.StartNew();
        foreach (var nom in new[] { "Processor Information", "Processor Frequency",
                                    "% Processor Performance", "% of Maximum Frequency",
                                    "Performance Limit Flags" })
            CpuFrequencyReader.Localiser(nom);
        var resolution = chrono.Elapsed.TotalMilliseconds;

        chrono.Restart();
        lecteur.Lire(_ => { });          // attente neutralisee : elle n'est pas le sujet
        var premier = chrono.Elapsed.TotalMilliseconds;

        chrono.Restart();
        lecteur.Lire(_ => { });
        var second = chrono.Elapsed.TotalMilliseconds;

        sortie.WriteLine($"1. cinq noms, tables froides  {resolution,9:0.0} ms");
        sortie.WriteLine($"2. Lire() noms deja resolus   {premier,9:0.0} ms");
        sortie.WriteLine($"3. Lire() tout chaud          {second,9:0.0} ms");
        sortie.WriteLine("(mesure indicative : la duree depend de la machine et de la charge)");

        // Ce test n'AFFIRMAIT rien : trois chronometres et pas une assertion. Le crible de
        // non-vacuite du tour 113 l'a pris en suite complete — il a fait son travail sur moi.
        // Ce qui se tient sans dependre d'un chronometre : les trois phases ont mesure quelque
        // chose de reel, donc les noms se sont resolus et le lecteur rend un etat.
        CpuFrequencyReader.Localiser("Processor Information").Should().NotBeNull();
        lecteur.Lire(_ => { }).Should().NotBeNull("un chronometre sur du vide ne mesure rien");
    }
}
