using System.Management;
using DrDxt.Core;

namespace DrDxt.Detection.Wmi;

/// <summary>
/// Une requête WMI bornée par <see cref="LectureWmiBornee"/> : au-delà de son délai, un échec <see cref="ErrorCodes.WmiTimeout"/>.
/// </summary>
public sealed class ManagementWmiQuery : IWmiQuery
{
    public static readonly TimeSpan Delai = LectureWmiBornee.Delai;

    private readonly TimeSpan _delai;
    private readonly Func<string, string, Result<IReadOnlyList<WmiRow>>> _executer;

    public ManagementWmiQuery() : this(Delai, Executer) { }

    internal ManagementWmiQuery(TimeSpan delai, Func<string, string, Result<IReadOnlyList<WmiRow>>> executer)
        => (_delai, _executer) = (delai, executer);

    public Result<IReadOnlyList<WmiRow>> Query(string wql)
        => QueryInNamespace(@"\\.\root\cimv2", wql);

    public Result<IReadOnlyList<WmiRow>> QueryInNamespace(string ns, string wql)
        => LectureWmiBornee.Lire(() => _executer(ns, wql),
            () => Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.WmiTimeout, $"WMI did not answer within {_delai.TotalSeconds:0} s."), _delai);

    private static Result<IReadOnlyList<WmiRow>> Executer(string ns, string wql)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(ns), new ObjectQuery(wql));
            using var collection = searcher.Get();

            var rows = new List<WmiRow>();
            foreach (ManagementBaseObject obj in collection)
            {
                using (obj) rows.Add(Ligne(obj));
            }
            return Result<IReadOnlyList<WmiRow>>.Ok(rows);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.WmiFail, ex.Message);
        }
    }

    // Un objet imbriqué (le Principal ou les Actions d'une tâche planifiée) devient lui aussi une WmiRow,
    // lue tant que l'objet parent est vivant : l'appelant n'a jamais d'objet COM à libérer.
    private static WmiRow Ligne(ManagementBaseObject obj)
    {
        var props = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in obj.Properties)
            props[p.Name] = p.Value switch
            {
                ManagementBaseObject m => Ligne(m),
                ManagementBaseObject[] ms => ms.Select(Ligne).ToArray(),
                var v => v,
            };
        return new WmiRow(props);
    }
}
