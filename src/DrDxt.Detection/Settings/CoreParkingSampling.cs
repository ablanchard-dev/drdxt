namespace DrDxt.Detection.Settings;

/// <summary>
/// Reunit plusieurs lectures du parking de coeurs en un seul verdict.
///
/// Pourquoi ce fichier existe, mesure du 07/09/2026 : six scans d'affilee sur la machine de
/// reference ont rendu 11, 14, 14, 16 et 16 coeurs parques, et un scan lance pendant une
/// compilation en a rendu **0**, ce qui fait disparaitre le seul constat Minor du produit. Le meme
/// PC recevait donc deux verdicts selon l'heure du scan, sans que rien ne le dise au client.
///
/// Le levier n'a pas tort d'exiger un symptome reel. Ce qui a tort, c'est de le lire a un seul
/// instant : un coeur qui dort une seconde sur deux dort, et une photo prise pendant qu'il
/// travaille dit le contraire.
///
/// ══ la regle, et elle porte une asymetrie voulue ══
///   • parque dans un echantillon suffit a compter comme parque : c'est un symptome, pas une
///     moyenne ;
///   • mais si aucune lecture n'aboutit, le resultat reste <c>null</c>. « Je n'ai pas pu lire » ne
///     devient jamais « rien n'est parque » : cette confusion-la ferait proposer une ecriture
///     sur des machines parfaitement saines.
///
/// Pur et total : aucune I/O, jamais d'exception.
/// </summary>
internal static class CoreParkingSampling
{
    /// <summary>Nombre de photos prises, et l'attente entre deux. **Choix produit** : trois
    /// lectures sur environ une seconde et demie suffisent a attraper un coeur qui alterne, et
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
