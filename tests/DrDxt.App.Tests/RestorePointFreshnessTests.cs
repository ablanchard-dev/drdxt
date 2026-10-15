using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Le point de restauration qui protege un changement : le plus recent cree depuis le debut
// de la fenetre (24 h avant la demande, la ou Windows refuse d'en creer un nouveau). Format de date lu sur
// le poste : « 20260911222955.512634-000 ».
public sealed class RestorePointFreshnessTests
{
    private static readonly DateTime Depuis = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_most_recent_point_in_the_window_is_the_one_that_protects()
        => WmiRestorePointService.PlusRecentDepuis(
                new[] { "20260913180000.000000-000", "20260914090000.000000-000" }, Depuis)
            .Should().Be(new DateTime(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void A_point_older_than_the_window_protects_nothing()
        => WmiRestorePointService.PlusRecentDepuis(new[] { "20260912120000.000000-000" }, Depuis)
            .Should().BeNull("Windows en aurait cree un nouveau : son absence est un vrai echec");

    [Fact]
    public void An_unreadable_date_is_ignored_not_trusted()
        => WmiRestorePointService.PlusRecentDepuis(new[] { "pas une date", null }, Depuis)
            .Should().BeNull();

    [Fact]
    public void A_readable_point_still_counts_next_to_an_unreadable_one()
        => WmiRestorePointService.PlusRecentDepuis(new[] { "pas une date", "20260914090000.000000-000" }, Depuis)
            .Should().Be(new DateTime(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc));
}
