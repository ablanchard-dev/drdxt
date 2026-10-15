using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// Le disque systeme borne la restauration systeme que le helper accepte d'activer.
/// Il etait lu dans %SystemDrive%, que l'utilisateur peut redefinir dans ses propres variables et
/// que le processus eleve herite. Il vient maintenant de l'API.
/// </summary>
public class LecteurSystemeHorsEnvironnementTests
{
    [Fact]
    public void Une_variable_SystemDrive_changee_ne_deplace_pas_le_disque_systeme()
    {
        var attendu = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!
            .TrimEnd('\\').ToUpperInvariant();
        var avant = Environment.GetEnvironmentVariable("SystemDrive");
        // Temoin : la variable pointe bien ailleurs que le vrai disque pendant l'essai.
        var piege = attendu == "Z:" ? "Y:" : "Z:";
        try
        {
            Environment.SetEnvironmentVariable("SystemDrive", piege);
            Environment.GetEnvironmentVariable("SystemDrive").Should().Be(piege);

            OpAllowlist.ResolveSystemDriveLetter().Should().Be(attendu);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SystemDrive", avant);
        }
    }
}
