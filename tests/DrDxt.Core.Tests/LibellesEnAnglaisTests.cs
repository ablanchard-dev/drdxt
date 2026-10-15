using System.Globalization;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Les titres des constats (domaine, nom) passent par les ressources du docteur : en anglais sur un Windows anglais, la
// clé du journal inchangée (deux photos se comparent toujours par Domaine + Nom).
public sealed class LibellesEnAnglaisTests
{
    [Fact]
    public void Les_titres_se_disent_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        RepairLibelles.Domaine("CPU").Should().Be("Processor");
        RepairLibelles.Domaine("WINDOWS UPDATE").Should().Be("Windows updates");
        RepairLibelles.Nom("en attente").Should().Be("Windows updates waiting", "seul dans une liste, « Pending » ne disait pas quoi");
        RepairLibelles.Nom("useplatformclock").Should().Be("Platform clock forced");
        RepairLibelles.Nom("une cle inconnue").Should().Be("Une cle inconnue", "une clé sans titre s'affiche telle quelle");
    }

    // « echecs » est le nom de deux constats, aux services et à Windows Update. Seul dans une liste, « Failures » ne dirait
    // pas de quoi : le titre tient compte de la famille quand elle en a un.
    [Fact]
    public void Un_nom_partage_par_deux_familles_dit_la_sienne()
    {
        using (Docteur.Portee(CultureInfo.GetCultureInfo("en-US")))
        {
            RepairLibelles.Nom("SERVICES", "echecs").Should().Be("Service failures");
            RepairLibelles.Nom("WINDOWS UPDATE", "echecs").Should().Be("Windows Update failures");
            RepairLibelles.Nom("CPU", "useplatformclock").Should().Be("Platform clock forced", "sans titre propre à sa famille, le titre général");
        }
        using (Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR")))
            RepairLibelles.Nom("SERVICES", "echecs").Should().Be("Échecs de services");
    }

    [Fact]
    public void En_francais_rien_ne_change()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));

        RepairLibelles.Domaine("CPU").Should().Be("Processeur");
        RepairLibelles.Nom("useplatformclock").Should().Be("Horloge de plateforme imposée");
        RepairLibelles.Nom("en attente").Should().Be("Mises à jour Windows en attente");
        RepairLibelles.Domaine("INCONNU").Should().Be("INCONNU");
    }
}
