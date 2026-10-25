using DrDxt.Core;

namespace DrDxt.Detection.Wmi;

/// <summary>
/// Rejoue une lecture WMI qui a leve. Rien d'autre.
/// Mesure du 09/09 : sur des passes d'audit consecutives, toute la famille RAM et la carte
/// reseau ont disparu puis sont revenues sans qu'une ligne change. La panne est dans la
/// lecture, pas dans la machine. Un client qui lance deux audits a une minute d'ecart ne
/// doit pas voir une famille entiere apparaitre et disparaitre.
/// Ce qu'il s'interdit : rejouer une lecture reussie mais vide. Zero ligne est une reponse
/// de la machine, pas une panne ; la rejouer fabriquerait une difference entre deux passes
/// identiques, c'est-a-dire exactement le defaut qu'on repare. Ni un delai depasse (WmiTimeout) :
/// winmgmt fige, le rejouer doublerait l'attente et laisserait un second fil bloque.
/// </summary>
public sealed class RetryingWmiQuery(IWmiQuery inner, int attempts = 2, TimeSpan? delay = null) : IWmiQuery
{
    private readonly int _attempts = attempts < 1 ? 1 : attempts;
    private readonly TimeSpan _delay = delay ?? TimeSpan.FromMilliseconds(120);

    public Result<IReadOnlyList<WmiRow>> Query(string wql)
        => Run(() => inner.Query(wql));

    public Result<IReadOnlyList<WmiRow>> QueryInNamespace(string ns, string wql)
        => Run(() => inner.QueryInNamespace(ns, wql));

    private Result<IReadOnlyList<WmiRow>> Run(Func<Result<IReadOnlyList<WmiRow>>> read)
    {
        Result<IReadOnlyList<WmiRow>> last = read();
        for (var attempt = 2; attempt <= _attempts && !last.IsOk && last.Error!.Value.Code != ErrorCodes.WmiTimeout; attempt++)
        {
            if (_delay > TimeSpan.Zero) Thread.Sleep(_delay);
            last = read();
        }

        // Un delai depasse se dit tel quel : une seule tentative, son motif dit deja le delai.
        if (last.IsOk || last.Error!.Value.Code == ErrorCodes.WmiTimeout) return last;

        // Le motif porte le nombre de tentatives : en aval, un Unknown doit rester lisible.
        var err = last.Error!.Value;
        return Result<IReadOnlyList<WmiRow>>.Fail(
            err.Code, Docteur.T("WMI.Echec", _attempts, err.Message));
    }
}
