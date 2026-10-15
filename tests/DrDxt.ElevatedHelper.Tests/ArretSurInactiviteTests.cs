using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>Un helper élevé lancé à la demande ne vit pas pour toujours. Il s'arrête après un délai sans
/// connexion, jamais tant qu'un bail est actif (il rendrait une valeur que l'App garde).</summary>
public sealed class ArretSurInactiviteTests
{
    private DateTimeOffset _maintenant = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private ArretSurInactivite Garde() => new(TimeSpan.FromMinutes(15), () => _maintenant);

    [Fact]
    public void Sans_connexion_pendant_le_delai_il_s_arrete()
    {
        var g = Garde();
        _maintenant += TimeSpan.FromMinutes(14);
        g.DoitSArreter(bauxActifs: 0).Should().BeFalse();
        _maintenant += TimeSpan.FromMinutes(1);
        g.DoitSArreter(bauxActifs: 0).Should().BeTrue();
    }

    [Fact]
    public void Une_connexion_repart_de_zero()
    {
        var g = Garde();
        _maintenant += TimeSpan.FromMinutes(14);
        g.Activite();
        _maintenant += TimeSpan.FromMinutes(14);
        g.DoitSArreter(bauxActifs: 0).Should().BeFalse();
    }

    // « à la fermeture de l'App » n'était pas tenu (le helper attendait 15 minutes). Le dernier
    // client authentifié parti, et aucun bail, il s'arrête tout de suite.
    [Fact]
    public void L_App_fermee_il_s_arrete_sans_attendre()
    {
        var g = Garde();
        g.ClientAuthentifie(4242);
        g.DoitSArreter(bauxActifs: 0, vivant: _ => true).Should().BeFalse("l'App tourne encore");
        g.DoitSArreter(bauxActifs: 0, vivant: pid => pid != 4242).Should().BeTrue("l'App est fermée");
        g.DoitSArreter(bauxActifs: 1, vivant: pid => pid != 4242).Should().BeFalse("un bail rendra la carte avant");
    }

    [Fact]
    public void Sans_client_connu_seul_le_delai_compte()
        => Garde().DoitSArreter(bauxActifs: 0, vivant: _ => false).Should().BeFalse();

    [Fact]
    public void Jamais_tant_qu_un_bail_est_actif()
    {
        var g = Garde();
        _maintenant += TimeSpan.FromHours(3);
        g.DoitSArreter(bauxActifs: 1).Should().BeFalse();
    }
}
