namespace DrDxt.Core;

/// <summary>
/// Une commande de la boîte aux lettres SMU qui ne fait que LIRE. On ne la construit pas : elle vient de la liste fermée
/// de <see cref="SmuAmd"/>, si bien qu'un futur lecteur ne peut envoyer qu'une commande de cette liste.
/// </summary>
public sealed class CommandeSmuLecture
{
    private CommandeSmuLecture(string nom, uint numero, string source) => (Nom, Numero, Source) = (nom, numero, source);

    public string Nom { get; }
    public uint Numero { get; }
    /// <summary>Le code public où ce numéro est relevé, pour cette génération.</summary>
    public string Source { get; }

    internal static CommandeSmuLecture Lecture(string nom, uint numero, string source) => new(nom, numero, source);
}

/// <summary>
/// Le SMU des Ryzen est en LECTURE SEULE. Seules les commandes de la boîte aux lettres RSMU qui
/// lisent (version du SMU, version et transfert de la table PM vers la DRAM, adresse de cette table, échelle PBO,
/// marge du Curve Optimizer) sont nommées, par génération exacte : un même numéro règle quelque chose sur une autre
/// génération (0x06 lit l'adresse de la table sur Zen 2 et Zen 3, et règle la marge d'un cœur du Curve Optimizer sur
/// Zen 4). Une génération absente n'a aucune commande. Aucune commande de réglage n'est nommée ici, et aucun code de
/// DrDXT n'envoie de commande : l'accès passe par un module PawnIO AMD que DrDXT n'embarque pas (GATES).
/// Sources, lues comme documentation : ryzen_smu (github.com/amkillam/ryzen_smu, commit d2983668, smu.c) et
/// ZenStates-Core (github.com/irusanov/ZenStates-Core, commit 869b7e82, Hardware/Smu/Settings/Zen2Settings.cs,
/// Zen3Settings.cs, Zen4Settings.cs, Zen5Settings.cs, Hardware/Smu/Mailbox.cs). AMD ne publie pas ces numéros.
/// </summary>
public static class SmuAmd
{
    private const string RyzenSmu = "ryzen_smu d2983668 smu.c";
    private const string ZenStates = "ZenStates-Core 869b7e82";

    private static IReadOnlyList<CommandeSmuLecture> Zen2Et3(string reglages) =>
    [
        CommandeSmuLecture.Lecture("GetSmuVersion", 0x02, $"{RyzenSmu} smu_get_version ; {ZenStates} Mailbox.cs"),
        CommandeSmuLecture.Lecture("TransferTableToDram", 0x05, $"{RyzenSmu} smu_transfer_table_to_dram ; {ZenStates} {reglages}"),
        CommandeSmuLecture.Lecture("GetDramBaseAddress", 0x06, $"{RyzenSmu} smu_get_dram_base_address ; {ZenStates} {reglages}"),
        CommandeSmuLecture.Lecture("GetTableVersion", 0x08, $"{RyzenSmu} smu_get_pm_table_version ; {ZenStates} {reglages}"),
        CommandeSmuLecture.Lecture("GetPBOScalar", 0x6C, $"{ZenStates} {reglages}"),
    ];

    private static IReadOnlyList<CommandeSmuLecture> Zen4Et5(string reglages) =>
    [
        CommandeSmuLecture.Lecture("GetSmuVersion", 0x02, $"{RyzenSmu} smu_get_version ; {ZenStates} Mailbox.cs"),
        CommandeSmuLecture.Lecture("TransferTableToDram", 0x03, $"{RyzenSmu} smu_transfer_table_to_dram ; {ZenStates} {reglages}"),
        CommandeSmuLecture.Lecture("GetDramBaseAddress", 0x04, $"{RyzenSmu} smu_get_dram_base_address ; {ZenStates} {reglages}"),
        CommandeSmuLecture.Lecture("GetTableVersion", 0x05, $"{RyzenSmu} smu_get_pm_table_version ; {ZenStates} {reglages}"),
        CommandeSmuLecture.Lecture("GetPBOScalar", 0x6D, $"{ZenStates} {reglages}"),
        CommandeSmuLecture.Lecture("GetDldoPsmMargin", 0xD5, $"{ZenStates} {reglages}"),
    ];

    // Une ligne par puce de bureau que les deux sources nomment au même couple famille / modèle CPUID que la table de
    // GenerationAmd : Matisse (17h 71h), Vermeer (19h 21h), Raphael (19h 61h), Granite Ridge (1Ah 44h).
    // ZenStates-Core marque « Not sure » la lecture de marge du Curve Optimizer sur Zen 2 : elle n'est pas reprise.
    private static readonly IReadOnlyDictionary<(int Famille, int Modele), IReadOnlyList<CommandeSmuLecture>> ParPuce =
        new Dictionary<(int, int), IReadOnlyList<CommandeSmuLecture>>
        {
            [(0x17, 0x71)] = Zen2Et3("Zen2Settings.cs"),
            [(0x19, 0x21)] = Zen2Et3("Zen3Settings.cs (hérite de Zen2Settings.cs)"),
            [(0x19, 0x61)] = Zen4Et5("Zen4Settings.cs"),
            [(0x1A, 0x44)] = Zen4Et5("Zen5Settings.cs (hérite de Zen4Settings.cs)"),
        };

    /// <summary>Les commandes de lecture de cette puce, exactement ; vide pour une génération non reconnue ou absente.</summary>
    public static IReadOnlyList<CommandeSmuLecture> Lectures(int famille, int modele) =>
        ParPuce.TryGetValue((famille, modele), out var l) ? l : [];

    /// <summary>Les puces qui ont une liste.</summary>
    public static IEnumerable<(int Famille, int Modele)> Puces => ParPuce.Keys;
}
