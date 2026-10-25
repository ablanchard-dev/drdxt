using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

// NOTIFYICONDATAW, la forme complete (avec guidItem et hBalloonIcon) : 976 octets en 64 bits (shellapi.h). Une structure qui
// n'a pas cette taille fait refuser chaque appel a Shell_NotifyIcon. Aucune notification n'est montree par ce test.
public sealed class NotificationDeBureauTests
{
    [Fact]
    public void La_structure_a_la_taille_que_Windows_attend()
        => NotificationDeBureau.TailleDeLaStructure.Should().Be(IntPtr.Size == 8 ? 976 : 956);
}
