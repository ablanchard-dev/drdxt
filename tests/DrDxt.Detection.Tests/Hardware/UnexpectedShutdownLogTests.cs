using DrDxt.Core;
using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

/// <summary>
/// Sonde couplee a l'OS, comme les autres lectures materielles du depot. Elle ne peut pas affirmer
/// Combien d'arrets brutaux une machine a subis -- la machine d'integration n'en a peut-etre aucun,
/// et c'est une reponse valide. Elle affirme ce qui doit tenir partout : la lecture reussit, elle
/// respecte la fenetre demandee, elle n'invente pas de date future, et elle est triee.
///
/// Ce qu'elle attrape, verifie par mutation : une requete XPath malformee (le journal rend une
/// erreur, donc `Fail`), une fenetre ignoree, un tri absent.
///
/// ══ ce qu'elle n'attrape pas, et il faut le dire ══
/// Un `catch` qui rendrait `Ok(liste vide)` au lieu de `Fail` passerait ici sans rien faire
/// rougir : cette sonde ne sait pas fabriquer un journal illisible sur une machine ou il se lit.
/// Faire passer « je n'ai pas pu lire » pour « rien a signaler » reste donc garde par la relecture
/// seule. Ligne ouverte dans GATES.md -- l'ecrire vaut mieux que laisser croire le contraire.
/// </summary>
public sealed class UnexpectedShutdownLogTests
{
    // Un evenement 41 d'un autre fournisseur (une appli, un pilote) n'est pas un arret brutal.
    [Fact]
    public void La_requete_ne_retient_que_le_41_du_noyau()
        => WindowsUnexpectedShutdownLog.Requete.Should().Contain("Provider[@Name='Microsoft-Windows-Kernel-Power']").And.Contain("EventID=41");

    [Fact]
    [Trait("Category", "Machine")] // lit le vrai journal Systeme : vierge sur un runner CI
    public void TheSystemLogIsReadableWithoutElevationAndRespectsTheWindow()
    {
        var earliest = DateTimeOffset.UtcNow.AddDays(-3650);

        var r = new WindowsUnexpectedShutdownLog().Since(earliest);

        r.IsOk.Should().BeTrue("le journal System se lit sans elevation : " + r.Error?.Message);
        r.Value!.Should().OnlyContain(d => d >= earliest, "la fenetre est filtree, pas decorative");
        r.Value.Should().OnlyContain(d => d <= DateTimeOffset.UtcNow.AddMinutes(1),
            "un arret brutal dans le futur signale une conversion d'heure fausse");
        r.Value.Should().BeInDescendingOrder("le plus recent d'abord");
    }

    [Fact]
    public void AWindowOfOneMinuteReturnsAnEmptyButSUCCESSFULRead()
    {
        // La minute qui vient de s'ecouler ne contient aucun arret brutal -- si elle en contenait
        // un, la machine viendrait de geler et ce test ne tournerait pas. Une liste NON vide ici
        // signifie donc une seule chose : la fenetre n'est pas appliquee.
        var r = new WindowsUnexpectedShutdownLog().Since(DateTimeOffset.UtcNow.AddMinutes(-1));

        r.IsOk.Should().BeTrue();
        r.Value!.Should().BeEmpty("la fenetre filtre reellement, elle n'est pas decorative");
    }
}
