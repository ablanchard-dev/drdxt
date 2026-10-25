using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

// Secteur ou batterie, d'après ACLineStatus : 0 batterie, 1 secteur, 255 inconnu. Inconnu n'est ni l'un ni l'autre : le
// levier d'écran ne s'en sert pas pour refuser.
public sealed class SourceDAlimentationTests
{
    [Theory]
    [InlineData((byte)0, true)]
    [InlineData((byte)1, false)]
    [InlineData((byte)255, null)]
    public void ACLineStatus_dit_batterie_secteur_ou_inconnu(byte valeur, bool? attendu)
        => Win32SourceDAlimentation.Interpreter(valeur).Should().Be(attendu);
}
