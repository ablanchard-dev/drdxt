using System.Buffers.Binary;
using DrDxt.Core;
using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

// La page de santé NVMe (journal 02h), décodée aux offsets de la norme, puis croisée avec les arrêts brutaux de Windows.
public sealed class SanteNvmeTests
{
    private static readonly DateTimeOffset Maintenant = new(2026, 10, 16, 20, 0, 0, TimeSpan.FromHours(2));

    // Relevé le 16/10/2026 sur la machine de référence (Samsung 990 PRO 2 To) : 316 K, réserve 100 %, usure 1 %,
    // 383 mises sous tension, 3 965 heures, 13 arrêts dangereux.
    private static byte[] Page(UInt128? arrets = null, byte critique = 0, UInt128? media = null)
    {
        var p = new byte[512];
        p[0] = critique;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), 316);
        p[3] = 100; p[4] = 10; p[5] = 1;
        BinaryPrimitives.WriteUInt128LittleEndian(p.AsSpan(112), 383);
        BinaryPrimitives.WriteUInt128LittleEndian(p.AsSpan(128), 3965);
        BinaryPrimitives.WriteUInt128LittleEndian(p.AsSpan(144), arrets ?? 13);
        BinaryPrimitives.WriteUInt128LittleEndian(p.AsSpan(160), media ?? 0);
        return p;
    }

    [Fact]
    public void La_page_se_lit_aux_offsets_de_la_norme_et_un_compteur_de_128_bits_en_entier()
    {
        var grand = (UInt128)ulong.MaxValue + 5;
        var s = SanteNvmeLue.Decoder("SSD", Page(arrets: grand))!;

        s.TemperatureKelvin.Should().Be(316);
        s.Heures.Should().Be((UInt128)3965);
        s.Cycles.Should().Be((UInt128)383);
        s.ArretsDangereux.Should().Be(grand, "un compteur de 128 bits plafonné à 64 bits mentirait sans le dire");
        SanteNvmeLue.Decoder("SSD", new byte[100]).Should().BeNull();
    }

    private static RepairFinding Juger(byte[] page, SanteNvme.Reference? avant, IReadOnlyList<DateTimeOffset>? arrets)
        => SanteNvme.Juger([("SSD", SanteNvmeLue.Decoder("SSD", page))], avant is null ? [] : [avant], arrets, Maintenant).Single();

    [Fact]
    public void Un_arret_dangereux_nouveau_sans_arret_brutal_se_dit()
    {
        var c = Juger(Page(arrets: 15), new SanteNvme.Reference("SSD", "13", Maintenant.AddDays(-1), null), []);

        c.Explication.Should().Contain("2 arrêt(s) dangereux que Windows n'a pas notés");
    }

    [Fact]
    public void Un_arret_brutal_sans_arret_dangereux_designe_un_gel_plutot_qu_une_coupure()
    {
        var c = Juger(Page(arrets: 13), new SanteNvme.Reference("SSD", "13", Maintenant.AddDays(-1), null), [Maintenant.AddHours(-2)]);

        c.Explication.Should().Contain("le courant est resté");
    }

    [Fact]
    public void Les_deux_ensemble_designent_une_coupure_et_le_premier_scan_pose_la_reference()
    {
        Juger(Page(arrets: 14), new SanteNvme.Reference("SSD", "13", Maintenant.AddDays(-1), null), [Maintenant.AddHours(-2)])
            .Explication.Should().Contain("alimentation coupée");
        Juger(Page(), null, []).Explication.Should().Contain("Première lecture");
    }

    [Fact]
    public void L_avertissement_critique_et_les_erreurs_de_support_meritent_le_regard()
    {
        var c = Juger(Page(critique: 0b100, media: 3), null, []);

        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Explication.Should().Contain("fiabilité dégradée").And.Contain("3 erreur(s) de support");
        Juger(Page(), null, []).Statut.Should().Be(RepairStatus.Observed);
    }

    [Fact]
    public void Une_page_non_rendue_ou_une_liste_non_lue_se_disent_non_lues()
    {
        SanteNvme.Juger([("SSD", null)], [], [], Maintenant).Single().Statut.Should().Be(RepairStatus.Unknown);
        SanteNvme.Juger(null, [], [], Maintenant).Single().Statut.Should().Be(RepairStatus.Unknown);
    }

    [Fact]
    public void Deux_SSD_du_meme_modele_ne_partagent_pas_leur_releve()
    {
        // Le relevé se retrouve par le nom : deux modèles identiques comparaient le compteur de l'un à celui de l'autre.
        SanteNvme.Nommer([("Samsung SSD 990 PRO 2TB", "0"), ("Samsung SSD 990 PRO 2TB", "2"), ("WD SN850X", "1")])
            .Should().Equal("Samsung SSD 990 PRO 2TB #0", "Samsung SSD 990 PRO 2TB #2", "WD SN850X");
    }

    [Fact]
    public void Une_reponse_au_decalage_hors_du_tampon_rend_non_lu_sans_exception()
    {
        // Une exception ici remontait jusqu'à Lire() et effaçait tous les disques, pas seulement celui-ci.
        var tampon = new byte[8 + 40 + 512];
        BinaryPrimitives.WriteInt32LittleEndian(tampon, 48);
        BinaryPrimitives.WriteInt32LittleEndian(tampon.AsSpan(8 + 20), 512);
        BinaryPrimitives.WriteInt32LittleEndian(tampon.AsSpan(8 + 16), -100);
        SanteNvme.Extraire(tampon).Should().BeNull();
        BinaryPrimitives.WriteInt32LittleEndian(tampon.AsSpan(8 + 16), 40);
        SanteNvme.Extraire(tampon).Should().HaveCount(512);
    }

    // Le lecteur ouvre le disque sans droit d'accès, puis en lecture seule : jamais en écriture (GENERIC_WRITE 0x40000000).
    [Fact]
    public void Le_lecteur_n_ouvre_jamais_le_disque_en_ecriture()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var source = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.Detection", "Hardware", "SanteNvme.cs"));

        source.Should().NotContain("0x40000000").And.NotContain("0xC0000000").And.NotContain("GENERIC_WRITE");
    }

    [Fact]
    public void Le_compteur_garde_rend_la_date_de_la_derniere_hausse()
    {
        var fichier = Path.Combine(Path.GetTempPath(), $"sante-nvme-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(fichier, System.Text.Json.JsonSerializer.Serialize(new[] { new SanteNvme.Reference("SSD", "13", Maintenant, Maintenant) }));
            SanteNvme.Hausses(fichier).Should().Equal(Maintenant);
            SanteNvme.Hausses(fichier + ".absent").Should().BeEmpty();
        }
        finally { File.Delete(fichier); }
    }
    [Fact]
    public void Un_disque_non_lu_garde_son_releve_et_la_date_de_sa_derniere_hausse()
    {
        var hausse = Maintenant.AddDays(-3);
        var precedents = new[] { new SanteNvme.Reference("A", "13", Maintenant.AddDays(-1), hausse), new SanteNvme.Reference("B", "5", Maintenant.AddDays(-1), null) };

        var gardes = SanteNvme.Garder([("A", null), ("B", SanteNvmeLue.Decoder("B", Page(arrets: 6)))], precedents, Maintenant);

        gardes.Single(g => g.Disque == "A").Should().Be(precedents[0], "un scan raté n'efface pas la trace d'une coupure");
        gardes.Single(g => g.Disque == "B").DerniereHausse.Should().Be(Maintenant);
    }

    // Un relevé gardé valide mais creux ne fait pas tomber le scan de santé : ses entrées vides sont écartées.
    [Fact]
    public void Un_releve_garde_aux_entrees_vides_se_lit_sans_elles()
    {
        var fichier = Path.Combine(Path.GetTempPath(), $"nvme-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(fichier, "[null, {}, {\"Disque\":\"SSD\",\"ArretsDangereux\":\"13\",\"Quand\":\"2026-10-01T00:00:00+00:00\",\"DerniereHausse\":\"2026-10-02T00:00:00+00:00\"}]");
            SanteNvme.Hausses(fichier).Should().Equal(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
        }
        finally { File.Delete(fichier); }
    }

    [Fact]
    public void Une_fenetre_d_arrets_plus_courte_que_la_periode_ne_conclut_pas()
    {
        var avant = new SanteNvme.Reference("SSD", "13", Maintenant.AddDays(-120), null);
        var c = SanteNvme.Juger([("SSD", SanteNvmeLue.Decoder("SSD", Page(arrets: 15)))], [avant], [], Maintenant, Maintenant.AddDays(-90)).Single();

        c.Explication.Should().Contain("ne remonte pas").And.NotContain("que Windows n'a pas notés");
        SanteNvme.Juger([("SSD", SanteNvmeLue.Decoder("SSD", Page(arrets: 15)))], [avant with { Quand = Maintenant.AddDays(-1) }], [],
            Maintenant, Maintenant.AddDays(-90)).Single().Explication.Should().Contain("que Windows n'a pas notés");
    }
}
