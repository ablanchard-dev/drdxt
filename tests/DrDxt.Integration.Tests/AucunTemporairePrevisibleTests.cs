using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// Un temporaire au nom prévisible (« <fichier>.tmp ») ouvert en écrasement suit un lien dur posé d'avance : l'App élevée,
// le helper ou le service réécrivaient alors un fichier protégé. Toute écriture atomique passe par FichierJsonAtomique
// (nom aléatoire, création exclusive) ; ce crible refuse un fichier source qui nomme « .tmp » et écrit en écrasement.
public sealed class AucunTemporairePrevisibleTests
{
    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    private static readonly Regex NommeUnTmp = new(@"""\.tmp""");
    private static readonly Regex EcritEnEcrasement = new(@"File\.WriteAll(Text|Bytes|Lines)\s*\(|FileMode\.Create\b(?!New)");

    /// <summary>Vrai pour une source qui nomme un temporaire « .tmp » et écrit en écrasement.</summary>
    private static bool Fautif(string source) => NommeUnTmp.IsMatch(source) && EcritEnEcrasement.IsMatch(source);

    [Fact]
    public void Le_crible_reconnait_un_temporaire_previsible_ouvert_en_ecrasement()
    {
        Fautif("var tmp = chemin + \".tmp\"; File.WriteAllText(tmp, json);").Should().BeTrue();
        Fautif("var tmp = chemin + \".tmp\"; new FileStream(tmp, FileMode.Create, FileAccess.Write);").Should().BeTrue();
        Fautif("var tmp = chemin + \".tmp\"; new FileStream(tmp, FileMode.CreateNew, FileAccess.Write);").Should().BeFalse();
    }

    [Fact]
    public void Aucune_source_n_ecrit_un_temporaire_previsible_en_ecrasement()
    {
        var sources = FichiersDuDepot.Sous(Path.Combine(Racine().FullName, "src"), "*.cs").ToList();
        var nommentUnTmp = sources.Where(f => NommeUnTmp.IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();

        // Témoin réel : le crible lit bien les sources, et y voit l'écriture atomique qui nomme ses temporaires.
        nommentUnTmp.Should().Contain("FichierJsonAtomique.cs");
        sources.Where(f => Fautif(File.ReadAllText(f))).Select(Path.GetFileName).Should().BeEmpty();
    }
}
