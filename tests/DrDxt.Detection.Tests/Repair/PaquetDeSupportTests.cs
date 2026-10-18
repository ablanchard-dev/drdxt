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

    [Fact]
    public void La_taille_montree_porte_l_unite_de_la_langue()
        => PaquetDeSupport.Ligne("crash.log", new string('x', 3000), "KB").Should().Be("crash.log (3 KB)");
}
