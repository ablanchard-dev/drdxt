using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// L'ecriture atomique, une seule fois. Cinq magasins l'appellent (references memoire et CPU,
/// journal de performance, journal des constats, marqueur BIOS) ; trois la recopiaient. Ces tests
/// portent ce que les cinq supposent.
/// </summary>
public sealed class FichierJsonAtomiqueTests : IDisposable
{
    private readonly string _dossier =
        Path.Combine(Path.GetTempPath(), "drdxt-atomique-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { /* nettoyage au mieux */ }
    }

    [Fact]
    public void Cree_le_dossier_ecrit_et_ne_laisse_aucun_tmp()
    {
        var chemin = Path.Combine(_dossier, "sous", "f.json");

        new FichierJsonAtomique(chemin, ErrorCodes.IoFailure).WriteAtomic("[1]").IsOk.Should().BeTrue();

        File.ReadAllText(chemin).Should().Be("[1]");
        File.Exists(chemin + ".tmp").Should().BeFalse("le temporaire est renomme, jamais laisse");
    }

    [Fact]
    public void Une_seconde_ecriture_REMPLACE_le_contenu()
    {
        var chemin = Path.Combine(_dossier, "f.json");
        var fichier = new FichierJsonAtomique(chemin, ErrorCodes.IoFailure);

        fichier.WriteAtomic("[1]");
        fichier.WriteAtomic("[2]");

        fichier.Read().Value.Should().Be("[2]");
        File.Exists(chemin + ".tmp").Should().BeFalse();
    }

    [Fact]
    public void Un_fichier_absent_se_lit_null_et_pas_en_echec()
    {
        var r = new FichierJsonAtomique(Path.Combine(_dossier, "absent.json"), ErrorCodes.IoFailure).Read();

        r.IsOk.Should().BeTrue();
        r.Value.Should().BeNull();
    }

    [Fact]
    public void Un_echec_porte_le_code_DU_MAGASIN_et_ne_leve_rien()
    {
        // Le dossier parent est un fichier : impossible d'y creer quoi que ce soit.
        Directory.CreateDirectory(_dossier);
        var bloquant = Path.Combine(_dossier, "bloquant");
        File.WriteAllText(bloquant, "x");

        var r = new FichierJsonAtomique(Path.Combine(bloquant, "f.json"), ErrorCodes.BiosPendingChangeIo)
            .WriteAtomic("[1]");

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosPendingChangeIo,
            "un appelant qui distingue ses erreurs par code ne doit pas les voir fusionner");
    }
}
