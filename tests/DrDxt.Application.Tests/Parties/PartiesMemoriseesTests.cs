using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// Le choix et les parties survivent à la fermeture de DrDXT ; un fichier abîmé n'invente rien.
public sealed class PartiesMemoriseesTests : IDisposable
{
    private readonly string _dossier = Directory.CreateTempSubdirectory("drdxt-parties-").FullName;
    private PartiesMemorisees Memoire() => new(Path.Combine(_dossier, "choix.json"), Path.Combine(_dossier, "parties.json"));
    public void Dispose() => Directory.Delete(_dossier, true);

    private static PartieRelevee P(double fps) => new("cod.exe", DateTimeOffset.Now, DateTimeOffset.Now, 4, fps, fps * 0.7, BottleneckState.CpuBoundOrAppBound);

    [Fact]
    public void Le_choix_et_les_parties_se_relisent()
    {
        // Le relevé est le juge de tout gain ; éteint par défaut, il a relevé 1 partie
        // en un mois. Allumé par défaut (données locales seulement), éteint seulement par le choix du joueur.
        Memoire().EstAllume().Should().BeTrue("rien d'enregistré : allumé par défaut");
        Memoire().Eteindre();
        Memoire().EstAllume().Should().BeFalse("le joueur l'a éteint : ça tient");
        Memoire().Allumer();
        Memoire().Ajouter(P(120));
        Memoire().Ajouter(P(130));

        Memoire().EstAllume().Should().BeTrue();
        Memoire().Lire().Select(p => p.FpsMedian).Should().Equal(130, 120);
    }

    [Fact]
    public void Un_fichier_abime_ne_donne_aucune_partie_et_n_allume_rien()
    {
        // Témoin : le même stockage lit bien une partie quand le fichier est sain.
        Memoire().Ajouter(P(120));
        Memoire().Lire().Should().HaveCount(1);

        File.WriteAllText(Path.Combine(_dossier, "parties.json"), "{ abîmé");
        File.WriteAllText(Path.Combine(_dossier, "choix.json"), "peut-être");

        Memoire().Lire().Should().BeEmpty();
        // Un choix illisible n'est pas un « éteint » du joueur : le défaut (allumé) s'applique.
        Memoire().EstAllume().Should().BeTrue();
    }
    // La dernière capture ratée se relit d'un lancement à l'autre (le relevé de fond
    // n'a pas de fenêtre, l'App la dit à la prochaine ouverture) ; rien d'écrit ou un fichier abîmé ne disent rien.
    [Fact]
    public void La_derniere_capture_ratee_se_relit()
    {
        Memoire().DernierEchec().Should().BeNull();
        var quand = new DateTimeOffset(2026, 10, 7, 21, 0, 0, TimeSpan.FromHours(2));
        Memoire().NoterEchec(new PartiesMemorisees.EchecDeCapture(quand, "cod.exe : service refusé"));

        Memoire().DernierEchec().Should().Be(new PartiesMemorisees.EchecDeCapture(quand, "cod.exe : service refusé"));
        File.WriteAllText(Path.Combine(_dossier, "releve-echec.json"), "{abîmé");
        Memoire().DernierEchec().Should().BeNull();
    }
}
