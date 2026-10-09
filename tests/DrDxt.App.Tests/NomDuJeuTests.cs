using DrDxt.App.ViewModels;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>UI1 (critique du 08/10, défaut 8) : « JeuBClient-Win64-Shipping » et « cod » s'affichaient tels quels dans
/// les parties. Un jeu se nomme comme le joueur le connaît ; un inconnu garde son exécutable plutôt qu'un nom inventé.</summary>
public sealed class NomDuJeuTests
{
    [Theory]
    [InlineData("cod.exe", "Call of Duty")]
    [InlineData(@"C:\Jeux\cs2.exe", "Counter-Strike 2")]
    [InlineData("r5apex.exe", "Apex Legends")]
    [InlineData("VALORANT-Win64-Shipping.exe", "Valorant")]
    [InlineData("FortniteClient-Win64-Shipping.exe", "Fortnite")]
    [InlineData("JeuBClient-Win64-Shipping.exe", "JeuB")]
    [InlineData("monjeu.exe", "monjeu")]
    public void Un_jeu_se_nomme_comme_le_joueur_le_connait(string exe, string nom)
        => ScanViewModel.NomDuJeu(exe).Should().Be(nom);
}
