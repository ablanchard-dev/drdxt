using DrDxt.Core;
using DrDxt.Detection.Gpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

// Le plancher de pilote qu'un jeu fixe dans son propre fichier, contre la version en service de la carte présente.
public sealed class PiloteExigeParLeJeuTests
{
    // Relevé le 16/10/2026 sur une installation réelle (_retail_\cod25\driverVersions.json).
    private const string Releve = """
        {
            "driver_versions": {
                "nvidia":                 [591, 59],
                "quadro":                 [582,  8],
                "nvidia_maxwell_pascal":  [581, 80],

                "amd":                    [25, 12, 1],
                "amdpolarisvega":         [25,  8, 1],

                "intel":                  [32, 0, 101, 8331]
            }
        }
        """;

    private const string Chemin = @"C:\Program Files (x86)\Call of Duty\_retail_\cod25\driverVersions.json";

    [Fact]
    public void Le_fichier_releve_se_lit_par_famille()
    {
        var v = PiloteExigeParLeJeu.Lire(Releve)!;
        v["nvidia"].Should().Equal(591, 59);
        v["intel"].Should().Equal(32, 0, 101, 8331);
        PiloteExigeParLeJeu.Lire("{\"autre\": 1}").Should().BeNull();
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4060", "nvidia")]
    [InlineData("NVIDIA GeForce GTX 1070", "nvidia_maxwell_pascal")]
    [InlineData("AMD Radeon RX 580", "amdpolarisvega")]
    [InlineData("AMD Radeon RX 7800 XT", "amd")]
    [InlineData("Intel(R) UHD Graphics 770", "intel")]
    [InlineData("Microsoft Basic Display Adapter", null)]
    public void Chaque_carte_trouve_sa_famille(string modele, string? famille) => PiloteExigeParLeJeu.Famille(modele).Should().Be(famille);

    [Fact]
    public void Sous_le_plancher_est_un_constat_et_au_dessus_ne_l_est_pas()
    {
        // 32.0.15.9159 = NVIDIA 591.59 ; 32.0.15.9030 = 590.30.
        PiloteExigeParLeJeu.Juger([(Chemin, Releve)], [new("NVIDIA GeForce RTX 4060", "32.0.15.9030", null)]).Single()
            .Statut.Should().Be(RepairStatus.Suspicious);
        var ok = PiloteExigeParLeJeu.Juger([(Chemin, Releve)], [new("NVIDIA GeForce RTX 4060", "32.0.16.1742", null)]).Single();
        ok.Statut.Should().Be(RepairStatus.ExpectedDefault);
        ok.Nom.Should().Be("pilote exigé par Call of Duty");
    }

    // La version AMD vient de la carte présente ; sans elle, « non lu », jamais la clé d'une carte retirée.
    [Fact]
    public void Une_version_ou_un_fichier_illisible_se_dit_non_lu()
    {
        PiloteExigeParLeJeu.Juger([(Chemin, Releve)], [new("AMD Radeon RX 7800 XT", "31.0.24033.1003", null)]).Single()
            .Statut.Should().Be(RepairStatus.Unknown);
        PiloteExigeParLeJeu.Juger([(Chemin, null)], []).Single().Statut.Should().Be(RepairStatus.Unknown);
    }
    [Fact]
    public void Le_fichier_se_trouve_aux_emplacements_connus_steam_compris_sans_parcourir_la_bibliotheque()
    {
        var racine = Path.Combine(Path.GetTempPath(), $"pilote-exige-{Guid.NewGuid():N}");
        try
        {
            // Battle.net donne le dossier du jeu ; Steam donne la bibliothèque, le jeu est sous steamapps\common.
            var battle = Path.Combine(racine, "Call of Duty");
            var steam = Path.Combine(racine, "SteamLibrary");
            var dansBattle = Ecrire(Path.Combine(battle, "_retail_", "cod25"));
            var dansSteam = Ecrire(Path.Combine(steam, "steamapps", "common", "Call of Duty HQ", "_retail_", "cod25"));
            // Plus profond qu'un emplacement connu : jamais cherché.
            Ecrire(Path.Combine(steam, "steamapps", "common", "Autre", "a", "b", "c"));

            PiloteExigeParLeJeu.Trouver([new DossierDeJeux("Battle.net", battle, 0), new DossierDeJeux("Steam", steam, 0)])
                .Should().BeEquivalentTo([dansBattle, dansSteam]);
        }
        finally { Directory.Delete(racine, true); }

        static string Ecrire(string dossier)
        {
            Directory.CreateDirectory(dossier);
            var f = Path.Combine(dossier, PiloteExigeParLeJeu.Fichier);
            File.WriteAllText(f, Releve);
            return f;
        }
    }

    [Fact]
    public void Le_contenu_se_relit_seulement_quand_le_fichier_change()
    {
        var f = Path.Combine(Path.GetTempPath(), $"driverVersions-{Guid.NewGuid():N}.json");
        try
        {
            var quand = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            File.WriteAllText(f, "un");
            File.SetLastWriteTimeUtc(f, quand);
            PiloteExigeParLeJeu.Contenu(f).Should().Be("un");
            File.WriteAllText(f, "deux");
            File.SetLastWriteTimeUtc(f, quand);
            PiloteExigeParLeJeu.Contenu(f).Should().Be("un", "même chemin, même date : le relevé en cache");
            File.SetLastWriteTimeUtc(f, quand.AddMinutes(1));
            PiloteExigeParLeJeu.Contenu(f).Should().Be("deux");
            PiloteExigeParLeJeu.Contenu(f + ".absent").Should().BeNull();
        }
        finally { File.Delete(f); }
    }
}
