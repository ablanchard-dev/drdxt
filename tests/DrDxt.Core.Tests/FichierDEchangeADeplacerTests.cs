using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Le fichier d'échange géré par le système sur un disque dur, alors que Windows est sur un SSD : rendre la gestion à
// Windows le remet sur le disque système. Tout autre cas reste au joueur.
public sealed class FichierDEchangeADeplacerTests
{
    private static readonly Dictionary<char, int> CSsdDDisqueDur = new() { ['C'] = JeuxSurDisqueDur.Ssd, ['D'] = JeuxSurDisqueDur.DisqueDur };

    [Theory]
    [InlineData(@"D:\pagefile.sys 0 0")]
    [InlineData(@"D:\pagefile.sys")]
    [InlineData("C:\\pagefile.sys 0 0\nD:\\pagefile.sys 0 0")]
    public void Gere_par_le_systeme_sur_le_disque_dur_avec_windows_sur_ssd(string valeur)
        => FichierDEchangeADeplacer.Juger(valeur.Split('\n'), CSsdDDisqueDur, 'C').Should().Be(TriState.On);

    [Theory]
    [InlineData(@"?:\pagefile.sys")]          // déjà géré par Windows
    [InlineData(@"C:\pagefile.sys 0 0")]       // déjà sur le SSD
    public void Rien_a_deplacer(string valeur)
        => FichierDEchangeADeplacer.Juger([valeur], CSsdDDisqueDur, 'C').Should().Be(TriState.Off);

    [Fact]
    public void Une_taille_choisie_a_la_main_reste_au_joueur()
        => FichierDEchangeADeplacer.Juger([@"D:\pagefile.sys 4096 8192"], CSsdDDisqueDur, 'C').Should().Be(TriState.Unknown);

    [Fact]
    public void Windows_sur_un_disque_dur_rien_n_est_propose()
        => FichierDEchangeADeplacer.Juger([@"D:\pagefile.sys 0 0"],
            new Dictionary<char, int> { ['C'] = JeuxSurDisqueDur.DisqueDur, ['D'] = JeuxSurDisqueDur.DisqueDur }, 'C').Should().Be(TriState.Off);

    [Fact]
    public void Un_type_de_disque_non_dit_n_affirme_rien()
    {
        FichierDEchangeADeplacer.Juger([@"D:\pagefile.sys 0 0"], new Dictionary<char, int> { ['C'] = JeuxSurDisqueDur.Ssd }, 'C')
            .Should().Be(TriState.Unknown);
        FichierDEchangeADeplacer.Juger(null, CSsdDDisqueDur, 'C').Should().Be(TriState.Unknown);
        FichierDEchangeADeplacer.Juger([], CSsdDDisqueDur, 'C').Should().Be(TriState.Unknown, "désactivé : c'est fixit.sane_pagefile");
    }
}
