using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// 17/09/2026 — la phrase du readme que rien ne tenait.
//
// La carte du code annonce : « `DrDxt.ElevatedHelper` — **le seul** processus qui écrit :
// exécuteur d'ops typées + allowlist ». C'est l'architecture de sûreté du produit : tout
// ce qui touche la machine passe par une op TYPÉE, allowlistée, réversible, journalisée.
//
// Mesuré ce jour : sur 4 191 tests, aucun ne tenait cette phrase. Les portes existantes
// couvrent les secrets (SecurityGateTests), le fail-open de la couche de confiance, la
// liste noire des mythes — mais un `RegistryKey.SetValue` ajouté dans `DrDxt.Detection`
// ou `DrDxt.App` serait passé en vert. L'invariant « réversible » en dépend entièrement :
// une écriture hors du helper n'a ni op inverse, ni point de restauration, ni journal.
//
// La porte est STRUCTURELLE, comme les autres de ce dossier : elle lit l'arbre source.
// Un garde de comportement se contourne par une nouvelle méthode ; un garde structurel
// non.
// ─────────────────────────────────────────────────────────────────────────────
public sealed class LeSeulProcessusQuiEcritGateTests
{
    // Le seul projet autorisé à écrire dans le registre. `DrDxt.Reconciler*` décrit l'état
    // désiré et détecte la dérive, mais c'est le helper qui exécute.
    private const string ProjetQuiEcrit = "DrDxt.ElevatedHelper";

    // Les API d'écriture de `Microsoft.Win32.RegistryKey`. `OpenSubKey(x, true)` est incluse :
    // ouvrir en écriture est déjà l'intention, même si l'écriture arrive deux lignes plus bas.
    private static readonly Regex EcritureRegistre = new(
        @"\.(SetValue|CreateSubKey|DeleteValue|DeleteSubKeyTree|DeleteSubKey)\s*\(",
        RegexOptions.Compiled);

    // `DependencyObject.SetValue` (WPF) porte le même nom que l'écriture registre. Sans ce
    // filtre, la porte accuserait `DrDxt.App/Effets/Controles.cs` — un faux positif qui
    // aurait fini en exception nommée, donc en trou. On n'inspecte que les fichiers qui
    // parlent réellement du registre.
    private static bool ParleDuRegistre(string texte)
        => texte.Contains("Microsoft.Win32", StringComparison.Ordinal)
        || texte.Contains("RegistryKey", StringComparison.Ordinal)
        || texte.Contains("RegistryHive", StringComparison.Ordinal);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "DrDXT.sln introuvable en remontant depuis " + AppContext.BaseDirectory);
    }

    private static bool EstGenere(string chemin)
        => chemin.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || chemin.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>
    /// Les écritures registre hors du helper, une ligne par faute. Vide = la phrase tient.
    /// Rendue publique au test de mutation : un garde qu'on ne peut pas faire tomber sur
    /// commande ne prouve rien.
    /// </summary>
    internal static IReadOnlyList<string> EcrituresHorsDuHelper(string racineSrc)
    {
        var fautes = new List<string>();
        foreach (var chemin in FichiersDuDepot.Sous(racineSrc, "*.cs")
                     .Where(p => !EstGenere(p))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            if (chemin.Contains(Path.DirectorySeparatorChar + ProjetQuiEcrit + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
                continue;

            var texte = File.ReadAllText(chemin);
            if (!ParleDuRegistre(texte))
                continue;

            var lignes = texte.Split('\n');
            for (var i = 0; i < lignes.Length; i++)
            {
                var ligne = lignes[i];
                var nue = ligne.TrimStart();
                if (nue.StartsWith("//", StringComparison.Ordinal) || nue.StartsWith("///", StringComparison.Ordinal))
                    continue;
                if (EcritureRegistre.IsMatch(ligne))
                    fautes.Add($"{Path.GetRelativePath(racineSrc, chemin)}:{i + 1}  {nue}");
            }
        }

        return fautes;
    }

    [Fact]
    [Trait("Category", "SecurityGate")]
    public void Aucun_projet_hors_du_helper_n_ecrit_dans_le_registre()
    {
        var src = Path.Combine(RepoRoot(), "src");
        Directory.Exists(src).Should().BeTrue();

        var fautes = EcrituresHorsDuHelper(src);

        fautes.Should().BeEmpty(
            "le README annonce que " + ProjetQuiEcrit + " est le SEUL processus qui ecrit ; "
            + "une ecriture ailleurs n'a ni op inverse, ni point de restauration, ni journal :\n"
            + string.Join("\n", fautes));
    }

    [Fact]
    [Trait("Category", "SecurityGate")]
    public void La_porte_VOIT_une_ecriture_greffee_dans_un_autre_projet()
    {
        // Une porte qui ne tombe jamais est decorative : celle-ci prouverait surtout que
        // personne n'ecrit `SetValue`. On lui donne un arbre source ou un autre projet ecrit.
        var faux = Path.Combine(Path.GetTempPath(), "DrDxtGarde_" + Guid.NewGuid().ToString("N"));
        var projet = Path.Combine(faux, "DrDxt.Detection");
        Directory.CreateDirectory(projet);
        try
        {
            File.WriteAllText(Path.Combine(projet, "SondeMutante.cs"), """
                using Microsoft.Win32;

                internal static class SondeMutante
                {
                    public static void Ecrire()
                    {
                        using var k = Registry.CurrentUser.CreateSubKey(@"Software\DrDxt");
                        k.SetValue("Test", 1);
                    }
                }
                """);

            var fautes = EcrituresHorsDuHelper(faux);

            fautes.Should().HaveCount(2, "la creation de cle ET l'ecriture de valeur comptent");
            fautes.Should().OnlyContain(f => f.Contains("SondeMutante.cs", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(faux, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "SecurityGate")]
    public void Le_SetValue_de_WPF_n_est_pas_confondu_avec_une_ecriture_registre()
    {
        // `DependencyObject.SetValue` porte le meme nom. S'il declenchait la porte, il aurait
        // fallu l'excepter par son chemin -- et une exception par chemin est un trou permanent.
        var faux = Path.Combine(Path.GetTempPath(), "DrDxtGarde_" + Guid.NewGuid().ToString("N"));
        var projet = Path.Combine(faux, "DrDxt.App");
        Directory.CreateDirectory(projet);
        try
        {
            File.WriteAllText(Path.Combine(projet, "Controle.cs"), """
                using System.Windows;

                internal static class Controle
                {
                    public static void SetSuivre(DependencyObject d, bool v) => d.SetValue(SuivreProperty, v);
                }
                """);

            EcrituresHorsDuHelper(faux).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(faux, recursive: true);
        }
    }
}
