using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// SeSystemEnvironmentPrivilege : etre administrateur ne suffit pas.
///
/// Mesure sur la machine de reference le 27/08/2026, en session ELEVEE :
/// `whoami /priv` montre le privilege PRESENT mais **Desactive**, et
/// GetFirmwareEnvironmentVariableExW rend win32=1314. Apres AdjustTokenPrivileges,
/// la meme lecture rend BootOrder (2 octets), BootCurrent (2) et SecureBoot (1).
/// Sans l'activation, la lecture firmware etait morte en production.
/// </summary>
public sealed class FirmwarePrivilegeTests
{
    /// <summary>
    /// Le test qui compte, et la raison d'etre de ce fichier.
    ///
    /// Si le LUID est declare avec un `long`, .NET aligne le champ sur 8 octets :
    /// TOKEN_PRIVILEGES pese alors **24** octets au lieu de **16**, le LUID tombe au mauvais
    /// offset, et AdjustTokenPrivileges rend ERROR_NOT_ALL_ASSIGNED (1300) **sur un
    /// utilisateur qui est administrateur**. Le symptome accuse les droits alors que le defaut
    /// est dans la disposition memoire -- on peut y perdre des heures.
    ///
    /// Ce test rougit a la seconde ou quelqu'un reintroduit le piege.
    /// </summary>
    [Fact]
    public void TokenPrivileges_IsSixteenBytes_SoTheLuidIsNotMisaligned()
    {
        FirmwarePrivilege.TokenPrivilegesSize.Should().Be(16,
            "un LUID declare avec un long donnerait 24 octets et ERROR_NOT_ALL_ASSIGNED (1300) " +
            "sur un compte pourtant administrateur -- un symptome qui accuse les droits a tort");
    }

    /// <summary>
    /// Enable() ne jette jamais, quel que soit le niveau de privilege de la session qui execute
    /// les tests (elevee sur une machine de dev, non elevee en CI). Le resultat depend donc de
    /// l'environnement et n'est deliberement pas asserte : ce qui est garanti ici, c'est
    /// l'absence d'exception et un motif lisible en cas d'echec.
    /// </summary>
    [Fact]
    public void Enable_NeverThrows_AndSaysWhyWhenItFails()
    {
        var act = () => FirmwarePrivilege.Enable();

        var result = act.Should().NotThrow().Subject;

        if (!result.IsOk)
        {
            result.Error!.Value.Message.Should().NotBeNullOrWhiteSpace(
                "un echec doit porter son motif -- notamment 1300 = privilege absent du token " +
                "(session non elevee), qui ne dit RIEN de ce que le firmware autoriserait");
            result.Error.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        }
    }
}
