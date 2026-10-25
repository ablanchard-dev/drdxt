using DrDxt.Core;
using DrDxt.Detection.Wmi;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.Detection.Tests.Wmi;

/// <summary>
/// La lecture WMI echoue par intermittence : sans rejeu, une famille entiere de l'audit (RAM,
/// reseau) disparaitrait puis reviendrait sans qu'une ligne change.
/// Ce que ce decorateur garantit, et ce qu'il s'interdit :
///  - il rejoue une lecture qui a LEVE (Result.Fail) ;
///  - il ne rejoue jamais une lecture reussie mais vide : zero ligne est une reponse,
///    pas une panne. La rejouer inventerait une difference entre deux passes identiques ;
///  - quand toutes les tentatives echouent, le motif porte leur nombre, pour qu'un
///    `Unknown` reste lisible en aval.
/// </summary>
public class RetryingWmiQueryTests
{
    private const string Wql = "SELECT * FROM Win32_PhysicalMemory";

    private static Result<IReadOnlyList<WmiRow>> Rows(params WmiRow[] rows)
        => Result<IReadOnlyList<WmiRow>>.Ok(rows);

    private static Result<IReadOnlyList<WmiRow>> Fail(string msg = "RPC server is unavailable")
        => Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.WmiFail, msg);

    private static RetryingWmiQuery Sut(IWmiQuery inner) => new(inner, attempts: 2, delay: TimeSpan.Zero);

    // Un délai dépassé n'est pas une panne passagère : winmgmt est figé. Le rejouer doublerait l'attente et laisserait un
    // second fil bloqué.
    [Fact]
    public void Un_delai_depasse_n_est_pas_rejoue()
    {
        var inner = Substitute.For<IWmiQuery>();
        inner.Query(Wql).Returns(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.WmiTimeout, "figé"));

        var r = Sut(inner).Query(Wql);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.WmiTimeout);
        inner.Received(1).Query(Wql);
    }

    [Fact]
    public void Une_lecture_qui_echoue_puis_reussit_rend_la_reussite()
    {
        var inner = Substitute.For<IWmiQuery>();
        var row = new WmiRow(new Dictionary<string, object?> { ["Speed"] = 3800u });
        inner.Query(Wql).Returns(Fail(), Rows(row));

        var r = Sut(inner).Query(Wql);

        r.IsOk.Should().BeTrue();
        r.Value!.Single().GetInt("Speed").Should().Be(3800);
        inner.Received(2).Query(Wql);
    }

    [Fact]
    public void Une_lecture_reussie_mais_vide_n_est_jamais_rejouee()
    {
        var inner = Substitute.For<IWmiQuery>();
        inner.Query(Wql).Returns(Rows());

        var r = Sut(inner).Query(Wql);

        r.IsOk.Should().BeTrue();
        r.Value!.Should().BeEmpty();
        inner.Received(1).Query(Wql);
    }

    [Fact]
    public void Une_lecture_reussie_du_premier_coup_n_est_pas_rejouee()
    {
        var inner = Substitute.For<IWmiQuery>();
        inner.Query(Wql).Returns(Rows(new WmiRow(new Dictionary<string, object?>())));

        Sut(inner).Query(Wql);

        inner.Received(1).Query(Wql);
    }

    [Fact]
    public void Quand_tout_echoue_le_motif_porte_le_nombre_de_tentatives()
    {
        var inner = Substitute.For<IWmiQuery>();
        inner.Query(Wql).Returns(Fail("RPC server is unavailable"));

        var r = Sut(inner).Query(Wql);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.WmiFail);
        r.Error!.Value.Message.Should().Contain("2 tentatives")
                                .And.Contain("RPC server is unavailable");
        inner.Received(2).Query(Wql);
    }

    [Fact]
    public void Le_namespace_est_transmis_tel_quel_et_rejoue_pareil()
    {
        const string ns = "root/wmi";
        var inner = Substitute.For<IWmiQuery>();
        inner.QueryInNamespace(ns, Wql).Returns(Fail(), Rows());

        var r = Sut(inner).QueryInNamespace(ns, Wql);

        r.IsOk.Should().BeTrue();
        inner.Received(2).QueryInNamespace(ns, Wql);
        inner.DidNotReceive().Query(Arg.Any<string>());
    }
}
