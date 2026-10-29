namespace DrDxt.Detection.Settings;

/// <summary>
/// Reunit plusieurs lectures du parking de coeurs en un seul verdict. Mesure du 07/09/2026 : six scans d'affilee sur la
/// machine de reference ont rendu 11, 14, 14, 16 et 16 coeurs parques, et un scan pendant une compilation **0** : deux
/// verdicts selon l'heure. Un coeur qui dort une seconde sur deux dort, meme photographie pendant qu'il travaille.
/// Asymetrie voulue :
///   • parque dans un echantillon suffit : un symptome, pas une moyenne ;
///   • aucune lecture aboutie ⇒ <c>null</c>, jamais « rien n'est parque », qui ferait proposer une ecriture sur une
///     machine saine.
///
/// Pur et total : aucune I/O, jamais d'exception.
/// </summary>
internal static class CoreParkingSampling
{
    /// <summary>Nombre de photos prises, et l'attente entre deux. **Choix produit** : trois
    /// lectures sur environ une seconde (deux pauses de 500 ms) suffisent a attraper un coeur qui alterne, et
    /// c'est negligeable devant les vingt secondes de mesure que le scan paie deja.</summary>
    public const int Echantillons = 3;
    public const int PauseMs = 500;

    public static IReadOnlyList<int>? Merge(IReadOnlyList<IReadOnlyList<int>?> echantillons)
    {
        var lus = echantillons.Where(e => e is not null).ToList();
        if (lus.Count == 0)
            return null;

        return lus.SelectMany(e => e!).Distinct().OrderBy(i => i).ToList();
    }
}
