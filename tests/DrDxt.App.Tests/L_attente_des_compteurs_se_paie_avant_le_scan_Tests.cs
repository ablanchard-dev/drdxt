using System.IO;
using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// 19/09/2026. 4,5 secondes que le scan payait pour rien.
//
// Chronometre en trois phases, processus neuf :
//
//     1. resoudre cinq noms de compteurs, tables froides      427 ms
//     2. premier `CpuFrequencyReader.Lire()`                4 543 ms
//     3. le suivant, tout chaud                               11,6 ms
//
// L'ecart n'est pas notre code : c'est l'initialisation de perflib au premier
// `new PerformanceCounter` du processus. Elle se paie une fois, quoi qu'on fasse. La
// seule question est quand — et c'etait pendant le scan, donc pendant que quelqu'un
// regarde une barre avancer.
//
// Ce qui se prouve ici n'est pas une duree : une garde de duree dependrait de la
// machine et de la charge, et se ferait desactiver au premier jour de malchance. Ce
// qui se prouve, c'est que l'amorce a lieu, qu'elle n'a lieu que sur un fil de fond,
// qu'un echec ne fait pas tomber le lancement — et qu'elle est BRANCHEE.
//
// Le branchement, parce que la lecon du meme jour l'exige : une garde qui prouve la
// mecanique sans prouver qu'on l'emprunte reste verte quand on la debranche.
// -----------------------------------------------------------------------
public sealed class L_attente_des_compteurs_se_paie_avant_le_scan_Tests : IDisposable
{
    private readonly Action _origine = AmorceDesCompteurs.Amorcer;

    public void Dispose() => AmorceDesCompteurs.Amorcer = _origine;

    [Fact]
    public async Task L_amorce_a_bien_lieu()
    {
        var appels = 0;
        AmorceDesCompteurs.Amorcer = () => appels++;

        await AmorceDesCompteurs.AuLancementAsync();

        appels.Should().Be(1, "sans elle, les 4,5 s d'initialisation retombent sur le scan");
    }

    [Fact]
    public async Task Une_amorce_qui_echoue_ne_fait_pas_tomber_le_lancement()
    {
        // Contrepoids, et il pese plus lourd que le gain : ceci est un confort. Sur une
        // machine ou les compteurs sont illisibles, le produit doit demarrer exactement
        // comme avant. Un lancement perdu coute infiniment plus que 4,5 secondes.
        AmorceDesCompteurs.Amorcer = () => throw new InvalidOperationException("compteurs muets");

        var geste = async () => await AmorceDesCompteurs.AuLancementAsync();

        await geste.Should().NotThrowAsync();
    }

    [Fact]
    public async Task L_amorce_ne_bloque_pas_le_fil_appelant()
    {
        // Second contrepoids : amorcer sur le fil d'interface deplacerait l'attente du scan
        // vers l'ouverture de la fenetre — la meme seconde perdue, montree plus tot.
        //
        // 10/10/2026 : ce test comparait des numeros de fil, et a rougi en CI (fil 55 des deux
        // cotes) sans aucun defaut : apres un `await`, le pool peut reprendre le fil appelant
        // pour y faire tourner la tache. Ce qui compte n'est pas le numero du fil, c'est que
        // l'appelant reprend la main AVANT que l'amorce ait fini. On le prouve avec une barriere.
        using var barriere = new ManualResetEventSlim(false);
        AmorceDesCompteurs.Amorcer = () => barriere.Wait(TimeSpan.FromSeconds(10));

        var amorce = AmorceDesCompteurs.AuLancementAsync();

        amorce.IsCompleted.Should().BeFalse("une fenetre qui s'ouvre ne doit rien attendre : l'appelant a la main pendant que l'amorce tourne");
        barriere.Set();
        await amorce;
    }

    [Fact]
    public void L_amorce_est_BRANCHEE_au_demarrage()
    {
        // Une mecanique parfaite que personne n'appelle ne fait rien gagner du tout.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) dir = dir.Parent;
        var demarrage = File.ReadAllText(Path.Combine(dir!.FullName, "src", "DrDxt.App", "App.xaml.cs"));

        demarrage.Should().Contain("AmorceDesCompteurs.AuLancementAsync()",
            "le gain vient du MOMENT ou l'attente se paie : sans l'appel au lancement, "
            + "elle retombe integralement sur le premier scan");
    }
}
