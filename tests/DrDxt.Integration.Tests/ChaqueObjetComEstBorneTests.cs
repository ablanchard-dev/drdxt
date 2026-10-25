using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Un objet COM du système (Planificateur de tâches, agent Windows Update) n'a pas de délai : un service bloqué figerait la
/// famille du docteur qui le lit, et l'audit qui les enchaîne. Chaque fichier de src qui crée un objet COM par son ProgID le
/// lit par LectureWmiBornee, la borne de temps des lectures du système.
/// </summary>
public sealed class ChaqueObjetComEstBorneTests
{
    [Fact]
    public void Chaque_lecteur_d_objet_com_passe_par_la_borne()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var lecteurs = FichiersDuDepot.Sous(Path.Combine(d!.FullName, "src"), "*.cs")
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => (Nom: Path.GetFileName(f), Texte: File.ReadAllText(f)))
            .Where(f => f.Texte.Contains("GetTypeFromProgID(", StringComparison.Ordinal))
            .ToList();

        lecteurs.Should().HaveCountGreaterThan(2, "témoin : les lecteurs COM connus sont trouvés");
        lecteurs.Where(f => !f.Texte.Contains("LectureWmiBornee.Lire", StringComparison.Ordinal)).Select(f => f.Nom).Should().BeEmpty();
    }
}
