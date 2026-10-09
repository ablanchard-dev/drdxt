using DrDxt.Core;
using DrDxt.Reconciler.Reseau;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

/// <summary>28/09/2026 : la tâche de fond du service qui porte la protection du ping. Elle retire un plafond resté
/// au démarrage, bat à intervalle régulier, et retire tout à l'arrêt du service.</summary>
public sealed class ProtectionDuPingWorkerTests
{
    private sealed class ClientFaux : IElevatedClient
    {
        public List<PrivilegedOp> Ordres { get; } = new();
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            lock (Ordres) Ordres.Add(op);
            return Task.FromResult(Result.Ok());
        }
    }

    [Fact]
    public async Task Le_service_retire_au_demarrage_pose_en_partie_et_retire_a_l_arret()
    {
        var client = new ClientFaux();
        var protection = new ProtectionDuPingParLeService(client, () => new PlafondRetenuDuService("cod.exe", 20_000_000), _ => true);
        var worker = new ProtectionDuPingWorker(protection, TimeSpan.FromMilliseconds(20));

        await worker.StartAsync(CancellationToken.None);
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < limite && !Pose(client)) await Task.Delay(20);
        await worker.StopAsync(CancellationToken.None);

        List<PrivilegedOp> ordres;
        lock (client.Ordres) ordres = client.Ordres.ToList();
        ordres.First().Should().BeOfType<RemoveUploadCapOp>("un plafond resté d'une vie précédente part d'abord");
        ordres.Should().Contain(new ApplyUploadCapOp("cod.exe", 20_000_000));
        ordres.Last().Should().BeOfType<RemoveUploadCapOp>("rien ne reste posé derrière le service");
    }

    private static bool Pose(ClientFaux c)
    {
        lock (c.Ordres) return c.Ordres.Any(o => o is ApplyUploadCapOp);
    }
}
