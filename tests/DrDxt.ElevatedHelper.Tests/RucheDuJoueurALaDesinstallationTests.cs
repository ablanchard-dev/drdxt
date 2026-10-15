using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// Désinstalleur élevé avec le compte d'un autre (UAC « par-dessus l'épaule ») : HKCU est la ruche de cet autre compte. Les
// origines de la session du joueur y étaient écrites, puis le manifeste mis de côté : ses réglages n'étaient jamais rendus.
// Compte différent (ou inconnu) : HKCU est refusé et dit, le reste passe ; le manifeste reste en place pour un rendu par
// le joueur lui-même.
public sealed class RucheDuJoueurALaDesinstallationTests
{
    private sealed class Enregistreur : IElevatedClient
    {
        public List<PrivilegedOp> Ops { get; } = [];
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) { Ops.Add(op); return Task.FromResult(Result.Ok()); }
    }

    private static readonly SetRegistryValueOp Hkcu = new(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", RegistryValueType.DWord, "1");
    private static readonly SetRegistryValueOp Hklm = new(RegistryHiveKind.LocalMachine, @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode", RegistryValueType.DWord, "1");
    private static readonly DeleteRegistryValueOp HkcuSuppr = new(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");

    [Fact]
    public async Task Un_autre_compte_ne_recoit_pas_les_origines_de_la_session_du_joueur()
    {
        var interne = new Enregistreur();
        var client = ModeDesinstallation.GarderLaRucheDuJoueur(interne, memeCompte: false);

        (await client.ExecuteAsync(Hkcu, default)).IsOk.Should().BeFalse();
        (await client.ExecuteAsync(HkcuSuppr, default)).IsOk.Should().BeFalse();
        (await client.ExecuteAsync(Hklm, default)).IsOk.Should().BeTrue();
        interne.Ops.Should().Equal(Hklm);
    }

    [Fact]
    public async Task Le_meme_compte_rend_sa_session()
    {
        var interne = new Enregistreur();
        var client = ModeDesinstallation.GarderLaRucheDuJoueur(interne, memeCompte: true);

        (await client.ExecuteAsync(Hkcu, default)).IsOk.Should().BeTrue();
        interne.Ops.Should().Equal(Hkcu);
    }
}
