using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;
using Microsoft.Win32;

namespace DrDxt.ElevatedHelper.Tests;

// Le démarrage s'identifiait par l'horloge murale (UtcNow - TickCount64, 30 s de tolérance) ; un saut
// NTP ou une sortie de veille faisait croire à un nouveau démarrage, et la valeur COURANTE, déjà décalée par DrDXT, devenait
// l'origine. Une clé volatile (effacée par Windows à chaque redémarrage) identifie le démarrage sans aucune horloge.
public sealed class OrigineDuDemarrageTests : IDisposable
{
    private readonly string _chemin = @"Software\DrDXT-Tests\" + Guid.NewGuid().ToString("N");
    public void Dispose() { try { Registry.CurrentUser.DeleteSubKeyTree(_chemin, false); } catch { } }

    [Fact]
    public void La_premiere_valeur_de_ce_demarrage_est_retenue_et_rendue_ensuite()
    {
        OrigineDuDemarrage.Retenir("TensionDemarrage0", 5, Registry.CurrentUser, _chemin).Should().Be(5UL);
        OrigineDuDemarrage.Retenir("TensionDemarrage0", 9, Registry.CurrentUser, _chemin).Should().Be(5UL, "même démarrage : l'origine reste");
        OrigineDuDemarrage.Retenir("Pl1Demarrage", 7, Registry.CurrentUser, _chemin).Should().Be(7UL, "chaque nom a la sienne");
    }

    [Fact]
    public void La_cle_est_volatile_donc_effacee_au_redemarrage()
    {
        OrigineDuDemarrage.Retenir("TensionDemarrage0", 5, Registry.CurrentUser, _chemin);
        // Une clé volatile refuse les sous-clés non volatiles : c'est la preuve lisible qu'elle l'est.
        using var cle = Registry.CurrentUser.OpenSubKey(_chemin + @"\CeDemarrage", writable: true)!;
        var act = () => cle.CreateSubKey("permanente", RegistryKeyPermissionCheck.Default, RegistryOptions.None);
        act.Should().Throw<IOException>();
    }

    [Fact]
    public void Une_memoire_inaccessible_rend_null_jamais_la_valeur_courante()
        => OrigineDuDemarrage.Retenir("TensionDemarrage0", 5, Registry.CurrentUser, "\\chemin\0invalide").Should().BeNull();

    // Une clé « CeDemarrage » permanente (créée par erreur) survivrait aux redémarrages ; elle est
    // recréée volatile, et ce qu'elle portait est oublié.
    [Fact]
    public void Une_cle_permanente_creee_par_erreur_est_recreee_volatile()
    {
        using (var permanente = Registry.CurrentUser.CreateSubKey(_chemin + @"\CeDemarrage"))
            permanente.SetValue("TensionDemarrage0", 42L, RegistryValueKind.QWord);

        OrigineDuDemarrage.Retenir("TensionDemarrage0", 5, Registry.CurrentUser, _chemin).Should().Be(5UL, "l'ancienne valeur permanente est oubliée");
        using var cle = Registry.CurrentUser.OpenSubKey(_chemin + @"\CeDemarrage", writable: true)!;
        var act = () => cle.CreateSubKey("permanente", RegistryKeyPermissionCheck.Default, RegistryOptions.None);
        act.Should().Throw<IOException>("recréée volatile");
    }
}
