namespace DrDxt.App.Tests;

/// <summary>
/// Les tests qui realisent une vue montent une <c>Application</c> WPF sur un thread STA.
/// WPF n'en admet QU'UNE par processus : trois classes le faisaient, aucune n'etait
/// serialisee, et xUnit les lancait donc en parallele.
///
/// Mesure du 18/09/2026, deux fois de suite : la suite complete s'arretait sur
/// « Plantage du processus hote de test », **704 tests executes au lieu de 715** —
/// et l'outil annoncait quand meme « Reussi ». Onze tests n'etaient jamais lances,
/// sans que rien ne rougisse. Les memes tests, lances par un filtre, passaient en une
/// seconde : c'est la cohabitation qui tuait l'hote, pas les tests.
///
/// Le verrou existant protege la CREATION de l'Application, pas la cohabitation de
/// deux threads STA et de leurs dispatchers. Cette collection les met en file.
/// </summary>
[CollectionDefinition(Nom, DisableParallelization = true)]
public sealed class CollectionWpf
{
    public const string Nom = "WPF — une seule Application par processus";
}
