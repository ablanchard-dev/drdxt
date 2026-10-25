using System.IO.Compression;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Le paquet d'aide est une liste blanche : ce qui n'y est pas nommé n'entre jamais, et tout passe par l'anonymiseur.
public sealed class PaquetDeSupportTests
{
    [Fact]
    public void Seule_la_liste_blanche_entre_anonymisee_et_rien_d_inconnu()
    {
        var dossier = Path.Combine(Path.GetTempPath(), $"paquet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dossier);
        try
        {
            File.WriteAllText(Path.Combine(dossier, "parties.json"), "{\"jeu\":\"cod\",\"pc\":\"PC-DE-TEST\"}");
            File.WriteAllText(Path.Combine(dossier, "variables-uefi.bin"), "table brute");
            File.WriteAllText(Path.Combine(dossier, "bios-lu.json"), "{}");

            var contenu = PaquetDeSupport.Contenu(dossier, @"Rapport de C:\Users\alex\Documents, PC-DE-TEST", ["PC-DE-TEST"]);

            contenu.Select(c => c.Nom).Should().Equal(PaquetDeSupport.NomDuRapport, "parties.json");
            string.Concat(contenu.Select(c => c.Texte)).Should().NotContain("PC-DE-TEST").And.NotContain("alex");

            var zip = Path.Combine(dossier, "aide.zip");
            PaquetDeSupport.Ecrire(zip, contenu);
            using var lu = ZipFile.OpenRead(zip);
            lu.Entries.Select(e => e.FullName).Should().Equal(PaquetDeSupport.NomDuRapport, "parties.json");
        }
        finally { Directory.Delete(dossier, true); }
    }

    // Les fichiers JSON du paquet sont écrits avec l'encodeur par défaut : antislashs doublés, accents en \u00E9. Chaque valeur
    // est anonymisée décodée, comme dans l'export de la machine : un champ chemin ajouté un jour ne sort pas en clair.
    [Fact]
    public void Un_chemin_ou_un_nom_accentue_dans_une_valeur_json_sort_masque()
    {
        var dossier = Path.Combine(Path.GetTempPath(), $"paquet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dossier);
        try
        {
            File.WriteAllText(Path.Combine(dossier, "parties.json"), System.Text.Json.JsonSerializer.Serialize(
                new[] { new { jeu = "cod", dossier = @"C:\Users\Jean Dupont\Jeux\cod.exe", joueur = "Jérôme" } }));

            var json = PaquetDeSupport.Contenu(dossier, "rapport", ["Jérôme"]).Single(c => c.Nom == "parties.json").Texte;

            json.Should().NotContain("Dupont").And.NotContain("Jérôme").And.NotContain(@"J\u00E9r");
            json.Should().Contain(@"C:\\Users\\joueur\\Jeux\\cod.exe").And.Contain("\"cod\"");
        }
        finally { Directory.Delete(dossier, true); }
    }

    // Un fichier de la liste blanche présent mais qui n'entre pas (verrouillé, illisible, anonymisation trop lente) ne part
    // jamais brut, et ne disparaît plus en silence : le paquet porte une note qui le nomme, visible dans l'aperçu.
    [Fact]
    public void Un_fichier_laisse_dehors_est_nomme_dans_le_paquet()
    {
        var dossier = Path.Combine(Path.GetTempPath(), $"paquet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dossier);
        try
        {
            var crash = Path.Combine(dossier, "crash.log");
            File.WriteAllText(crash, "plantage");
            using (new FileStream(crash, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var contenu = PaquetDeSupport.Contenu(dossier, "rapport", []);

                contenu.Select(c => c.Nom).Should().Equal(PaquetDeSupport.NomDuRapport, PaquetDeSupport.NomDeLaNote);
                contenu.Single(c => c.Nom == PaquetDeSupport.NomDeLaNote).Texte.Should().Contain("crash.log");
            }
        }
        finally { Directory.Delete(dossier, true); }
    }

    // Un dictionnaire dont les clés sont des chemins (par jeu, par fichier) : la clé est anonymisée comme une valeur.
    [Fact]
    public void Une_cle_d_objet_json_s_anonymise_aussi()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, int> { [@"C:\Users\Jean Dupont\Jeux\cod.exe"] = 3 });

        var propre = MachineExportee.AnonymiserJson(json, [@"C:\Users\Jean Dupont"]);

        propre.Should().NotContain("Dupont").And.Contain(@"C:\\Users\\joueur\\Jeux\\cod.exe").And.Contain("3");
    }

    [Fact]
    public void Tout_est_entre_aucune_note()
    {
        var dossier = Path.Combine(Path.GetTempPath(), $"paquet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dossier);
        try
        {
            File.WriteAllText(Path.Combine(dossier, "crash.log"), "plantage");

            PaquetDeSupport.Contenu(dossier, "rapport", []).Select(c => c.Nom).Should().Equal(PaquetDeSupport.NomDuRapport, "crash.log");
        }
        finally { Directory.Delete(dossier, true); }
    }

    [Fact]
    public void La_taille_montree_porte_l_unite_de_la_langue()
        => PaquetDeSupport.Ligne("crash.log", new string('x', 3000), "KB").Should().Be("crash.log (3 KB)");
}
