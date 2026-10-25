using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

// Les jeux de Battle.net, Riot, EA app et Ubisoft Connect, trouvés par l'éditeur inscrit dans la liste
// des programmes de Windows. Valeurs relevées sur la machine de référence pour Battle.net (Call of Duty, Overwatch).
public sealed class JeuxDesLanceursTests
{
    [Fact]
    public void Les_jeux_des_lanceurs_sont_trouves_et_les_lanceurs_eux_memes_ecartes()
    {
        var programmes = new (string?, string?, string?)[]
        {
            ("Call of Duty", "Blizzard Entertainment", @"C:\Program Files (x86)\Call of Duty"),
            ("Overwatch", "Blizzard Entertainment", @"D:\Jeux\Overwatch\"),
            ("Battle.net", "Blizzard Entertainment", @"C:\Program Files (x86)\Battle.net"),
            ("VALORANT", "Riot Games, Inc", @"C:\Riot Games\VALORANT\live"),
            ("Riot Client", "Riot Games, Inc", @"C:\Riot Games\Riot Client"),
            ("Riot Vanguard", "Riot Games, Inc.", @"C:\Program Files\Riot Vanguard"),
            ("Origin", "Electronic Arts", @"C:\Program Files (x86)\Origin"),
            ("7-Zip", "Igor Pavlov", @"C:\Program Files\7-Zip"),
            ("Rainbow Six Siege", "Ubisoft", null),
        };

        DossiersDeJeux.JeuxDesLanceurs(programmes).Should().Equal(
            ("Battle.net", @"C:\Program Files (x86)\Call of Duty"),
            ("Battle.net", @"D:\Jeux\Overwatch"),
            ("Riot", @"C:\Riot Games\VALORANT\live"));
    }

    // Une bibliothèque se parcourt sur 5 niveaux : jamais sur un partage réseau (connexion SMB sortante) ni une racine de disque.
    [Theory]
    [InlineData(@"D:\SteamLibrary", true)]
    [InlineData(@"C:\Program Files (x86)\Call of Duty", true)]
    [InlineData(@"\hote\partage\Jeux", false)]
    [InlineData(@"\\hote\partage\Jeux", false)]
    [InlineData(@"//hote/partage/Jeux", false)]
    [InlineData(@"\?\UNC\hote\partage", false)]
    [InlineData(@"\\?\UNC\hote\partage", false)]
    [InlineData(@"D:\", false)]
    [InlineData(@"D:", false)]
    [InlineData(@"Jeux\relatif", false)]
    public void Seuls_les_dossiers_locaux_sous_une_racine_se_parcourent(string chemin, bool parcourable)
        => DossiersDeJeux.Parcourable(chemin).Should().Be(parcourable);
}
