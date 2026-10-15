using System.Diagnostics;

namespace DrDxt.Detection.Gpu;

/// <summary>La carte qui porte le jeu ; <paramref name="CarteDediee"/> nomme une vraie carte présente quand le jeu tourne
/// sur une puce intégrée, sinon <c>null</c>.</summary>
public sealed record CarteDuJeuLue(string Carte, bool Integree, string? CarteDediee);

/// <summary>
/// Sur un portable à deux puces graphiques, un jeu lancé sur la puce intégrée
/// perd l'essentiel de ses images, et rien ne le dit au joueur. Windows tient, par processus et par carte, la mémoire
/// graphique engagée (compteurs « GPU Process Memory », instances « pid_N_luid_…_phys_0 », ceux du Gestionnaire des
/// tâches) ; DXGI donne l'identifiant de chaque carte et sa mémoire dédiée. La carte où le jeu engage le plus est celle
/// qui le porte. Relevé le 07/10 sur la machine de référence : la RTX 4060 est luid_0x00000000_0x000107dd, le processus
/// d'affichage y engage 1,3 Go de mémoire dédiée ; un second identifiant des compteurs n'est pas une carte (DXGI ne le
/// donne pas) et ne compte pas. Lecture seule, une seule lecture, sans ouvrir le jeu.
/// </summary>
public static class CarteDuJeu
{
    internal static CarteDuJeuLue? Juger(int pid, IReadOnlyList<DxgiAdapter> cartes, IReadOnlyList<(string Instance, long Octets)> engage)
    {
        var prefixe = $"pid_{pid}_";
        var parCarte = cartes.Where(c => c.Luid is not null)
            .Select(c => (Carte: c, Octets: engage
                .Where(i => i.Instance.StartsWith(prefixe, StringComparison.OrdinalIgnoreCase)
                            && i.Instance.Contains(c.Luid!, StringComparison.OrdinalIgnoreCase))
                .Sum(i => i.Octets)))
            .Where(x => x.Octets > 0)
            .ToList();
        if (parCarte.Count == 0) return null;
        var porte = parCarte.MaxBy(x => x.Octets).Carte;
        var integree = porte.DedicatedVramBytes < MemoireVideoOccupee.CarteDedieeMin;
        var dediee = integree
            ? cartes.Where(c => c.DedicatedVramBytes >= MemoireVideoOccupee.CarteDedieeMin).Select(c => c.Description).FirstOrDefault()
            : null;
        return new CarteDuJeuLue(porte.Description, integree, dediee);
    }

    private static IReadOnlyList<DxgiAdapter>? _cartes;

    /// <summary>La carte du jeu maintenant, ou <c>null</c> si les compteurs ou les cartes ne se lisent pas.</summary>
    public static CarteDuJeuLue? Lire(int pid)
    {
        try
        {
            if (_cartes is null && new VorticeDxgiProvider().EnumerateAdapters() is { IsOk: true, Value: { Count: > 0 } lues })
                _cartes = lues;
            if (_cartes is null || !PerformanceCounterCategory.Exists("GPU Process Memory")) return null;
            var lot = new PerformanceCounterCategory("GPU Process Memory").ReadCategory();
            if (!lot.Contains("Total Committed")) return null;
            var engage = lot["Total Committed"].Values.Cast<InstanceData>().Select(i => (i.InstanceName, i.RawValue)).ToList();
            return Juger(pid, _cartes, engage);
        }
        catch (Exception) { return null; }
    }
}
