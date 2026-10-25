using DrDxt.Application.Autopilot;
using DrDxt.Application.Parties;
using DrDxt.Application.Tweaks;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Autopilot;

// Le journal de l'essai et celui des leviers posés se lisent, se modifient et se réécrivent. Deux processus du compte (l'App,
// le banc, l'outil de validation) qui le font en même temps effaçaient la mise à jour de l'autre. Le verrou est celui des
// autres journaux du compte : un fichier voisin ouvert sans partage, que Windows rend à la mort du processus.
public sealed class LesJournauxSAttendentEntreProcessusTests : IDisposable
{
    private readonly string _chemin = Path.Combine(Path.GetTempPath(), $"journal-verrou-{Guid.NewGuid():N}.json");
    public void Dispose() { try { File.Delete(_chemin); } catch (IOException) { } }

    [Fact]
    public void Le_journal_de_l_essai_attend_le_verrou_d_un_autre_processus()
    {
        var journal = new FichierRevertEnAttente(_chemin, TimeSpan.FromMilliseconds(50));
        using (var autre = VerrouDuReleve.Prendre(_chemin + ".verrou"))
        {
            autre.Should().NotBeNull("témoin : le verrou est pris");
            journal.Poser("latency.hagsOn", null).IsOk.Should().BeFalse("un autre processus écrit");
        }

        journal.Poser("latency.hagsOn", null).IsOk.Should().BeTrue();
        journal.Lire().Value!.TweakId.Should().Be("latency.hagsOn");
    }

    [Fact]
    public void Le_journal_des_leviers_poses_attend_le_verrou_d_un_autre_processus()
    {
        File.WriteAllText(_chemin, "[]");
        var journal = new JournalDesLeviersAppliques(_chemin, TimeSpan.FromMilliseconds(50));
        using (var autre = VerrouDuReleve.Prendre(_chemin + ".verrou"))
        {
            autre.Should().NotBeNull("témoin : le verrou est pris");
            journal.Retirer("latency.hagsOn").Should().BeFalse("un autre processus écrit");
        }

        journal.Retirer("latency.hagsOn").Should().BeTrue();
    }
}
