using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// 17/09/2026 — audit du handshake. Tout est en ordre : l'authentification passe
// Avant que la moindre cle soit forgee, la fabrique ne rend jamais autre chose
// qu'un authentificateur durci (chemin + SHA-256) ou un « refuse tout », et la
// production n'appelle qu'elle (`Program.cs` → `BuildMulti`).
//
// Le risque restant n'est pas un defaut : c'est une arme posee sur la table.
// `DrDxtAppCallerAuthenticator` garde un constructeur public « chemin seul »,
// hérité du dev-grade, qui accepte n'importe quel binaire pose au bon endroit.
// Personne ne l'utilise aujourd'hui ; le jour ou quelqu'un l'appelle « juste
// pour depanner », rien ne rougit et l'identite du binaire n'est plus verifiee.
//
// Ce test fait de ce constat une garde : la fabrique reste la seule porte.
// -----------------------------------------------------------------------
public class UneSeulePorteVersLAuthentificationTests
{
    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln")))
            d = d.Parent;
        d.Should().NotBeNull();
        return d!;
    }

    private static IEnumerable<(string fichier, string ligne)> LignesDuProduit()
    {
        var src = Path.Combine(Racine().FullName, "src");
        foreach (var f in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            foreach (var l in File.ReadAllLines(f))
                yield return (Path.GetFileName(f), l);
        }
    }

    [Fact]
    public void Aucun_code_de_production_ne_construit_l_authentificateur_a_la_main()
    {
        // Le constructeur « chemin seul ». La forme durcie passe par `WithImageIdentity`,
        // et la production par la fabrique.
        //
        // La premiere version de ce motif exigeait `new` COLLE au nom de la classe.
        // Mutation du 17/09 : une ligne `new DrDxt.ElevatedHelper.Ipc.DrDxtAppCallerAuthenticator(...)`
        // ajoutee dans `Program.cs` est passee sans faire rougir quoi que ce soit — le test
        // NOMMAIT une garantie sans la tenir. Le prefixe qualifie est donc optionnel ici.
        var coupables = LignesDuProduit()
            .Where(x => Regex.IsMatch(x.ligne, @"new\s+([A-Za-z_][\w]*\s*\.\s*)*DrDxtAppCallerAuthenticator\s*\("))
            .Where(x => x.fichier != "CallerAuthentication.cs")   // ses propres constructeurs
            .Select(x => $"{x.fichier} : {x.ligne.Trim()}")
            .ToList();

        coupables.Should().BeEmpty(
            "l'identite du binaire appelant doit passer par HelperAuthenticatorFactory, "
            + "qui ne rend qu'un authentificateur durci ou un refus");
    }

    [Fact]
    public void Le_helper_construit_bien_son_authentificateur_par_la_fabrique()
    {
        // L'autre moitie : si `Program.cs` cessait d'appeler la fabrique, le test
        // precedent resterait vert en ne gardant plus rien.
        var programme = LignesDuProduit().Where(x => x.fichier == "Program.cs").Select(x => x.ligne);
        programme.Should().Contain(l => l.Contains("HelperAuthenticatorFactory.Build"));
    }
}
