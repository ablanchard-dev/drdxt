using DrDxt.Reconciler.Manifest;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// Le service démarre à l'installation, avant que le helper crée la clé du manifeste ; une reprise en crée une neuve. Lue une
// seule fois, la clé refusait tout jusqu'au redémarrage du PC : elle se relit quand une vérification échoue.
public sealed class VerificateurReluTests
{
    // Un faux vérificateur : il vérifie une « signature » égale à la clé lue dans le fichier.
    private sealed class ParCle(byte cle) : IManifestVerifier
    {
        public bool Verify(ReadOnlySpan<byte> canonicalBytes, ReadOnlySpan<byte> signature) => signature.Length == 1 && signature[0] == cle;
    }

    private sealed class SansCle : IManifestVerifier
    {
        public bool Verify(ReadOnlySpan<byte> canonicalBytes, ReadOnlySpan<byte> signature) => false;
    }

    private byte? _cle;
    private int _lectures;

    private IManifestVerifier Charger(string _)
    {
        _lectures++;
        return _cle is { } c ? new ParCle(c) : new SansCle();
    }

    [Fact]
    public void Une_cle_posee_apres_le_demarrage_du_service_se_lit_sans_le_redemarrer()
    {
        var v = new VerificateurRelu("cle", Charger);
        v.Verify([1], [7]).Should().BeFalse("aucune clé encore");

        _cle = 7;

        v.Verify([1], [7]).Should().BeTrue("la clé posée par le premier commit se relit");
        var lectures = _lectures;
        v.Verify([1], [7]).Should().BeTrue();
        _lectures.Should().Be(lectures, "une vérification réussie ne relit pas le fichier");
    }

    [Fact]
    public void Une_cle_changee_par_une_reprise_se_lit_aussi()
    {
        _cle = 7;
        var v = new VerificateurRelu("cle", Charger);
        _cle = 9;

        v.Verify([1], [9]).Should().BeTrue();
    }

    [Fact]
    public void Un_manifeste_que_la_cle_relue_ne_verifie_pas_reste_refuse()
    {
        _cle = 7;
        var v = new VerificateurRelu("cle", Charger);

        v.Verify([1], [3]).Should().BeFalse();
        v.Verify([1], [7]).Should().BeTrue("la bonne clé reste en place");
    }
}
