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

    // Un « null » dans la liste (fichier écrit à la main, ou par une version qui en laissait) n'est pas une partie : il
    // n'atteint aucun lecteur, et l'ajout suivant ne le réécrit pas.
    [Fact]
    public void Un_null_dans_le_fichier_n_est_ni_lu_ni_reecrit()
    {
        File.WriteAllText(Path.Combine(_dossier, "parties.json"), "[null]");
        Memoire().Lire().Should().BeEmpty();

        Memoire().Ajouter(P(120));

        Memoire().Lire().Should().ContainSingle().Which.FpsMedian.Should().Be(120);
        using var ecrit = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_dossier, "parties.json")));
        ecrit.RootElement.EnumerateArray().Select(e => e.ValueKind).Should().Equal(System.Text.Json.JsonValueKind.Object);
    }

    // Une partie non écrite sur le disque serait perdue au relancement : l'ajout le dit, la liste la porte quand même.
    [Fact]
    public void Une_partie_non_ecrite_se_dit()
    {
        var fichier = Path.Combine(_dossier, "pas-un-dossier");
        File.WriteAllText(fichier, "");
        var memoire = new PartiesMemorisees(Path.Combine(_dossier, "choix.json"), Path.Combine(fichier, "parties.json"));

        var ajout = memoire.Ajouter(P(120));

        ajout.Ecrite.IsOk.Should().BeFalse();
        ajout.Liste.Should().ContainSingle();
        Memoire().Ajouter(P(120)).Ecrite.IsOk.Should().BeTrue("témoin : un dossier écrivable");
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

    // Une liste abîmée (zéros après une coupure) n'est pas écrasée : mise de côté, sa trace reste.
    [Fact]
    public void Une_liste_abimee_est_mise_de_cote_avant_le_prochain_ajout()
    {
        var chemin = Path.Combine(_dossier, "parties.json");
        File.WriteAllBytes(chemin, new byte[64]);

        Memoire().Ajouter(P(140)).Ecrite.IsOk.Should().BeTrue();

        Directory.GetFiles(_dossier, "parties.json.mis-de-cote-*").Should().ContainSingle();
        Memoire().Lire().Should().ContainSingle();
    }

    // Les parties se relisent des mois plus tard : le goulot s'écrit par son nom, qui ne bouge pas quand l'énumération
    // grandit. Les parties écrites en entiers se relisent encore.
    [Fact]
    public void Le_goulot_s_ecrit_par_son_nom_et_l_ancien_entier_se_relit()
    {
        Memoire().Ajouter(P(120) with { Goulot = BottleneckState.CpuBoundStrong });
        var fichier = Path.Combine(_dossier, "parties.json");

        File.ReadAllText(fichier).Should().Contain("\"Goulot\":\"CpuBoundStrong\"");

        // 4 : l'entier qu'écrivaient les anciennes versions, en dur ; recalculé depuis l'énumération, il suivrait un
        // réordonnancement et ne protégerait plus les vieux fichiers.
        File.WriteAllText(fichier, File.ReadAllText(fichier).Replace("\"Goulot\":\"CpuBoundStrong\"", "\"Goulot\":4"));
        Memoire().Lire().Single().Goulot.Should().Be(BottleneckState.CpuBoundStrong);
    }
}
