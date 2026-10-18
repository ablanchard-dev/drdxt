#if DEBUG
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DrDxt.Core;

namespace DrDxt.App.Platform;

/// <summary>
/// Rejouer une machine enregistrée (tests/Shared/machines) dans l'App, sans son
/// matériel, pour dessiner les écrans sur une carte AMD, un portable ou un compte sans droits. DEBUG seulement : l'argument
/// « machine=&lt;nom ou chemin&gt; » remplace l'inventaire, les réglages, la santé, les disques et la mesure en jeu par ceux
/// du fichier ; le reste (parties, historique, mises à jour) lit encore ce PC.
/// </summary>
internal static class MachineDeTest
{
    /// <param name="Eleve">Lu avec les droits administrateur ; null (fichiers anciens) = oui. Sans ce
    /// champ, la machine « sans-admin » rejouée gardait les droits de ce PC et le bouton « relancer en administrateur »
    /// ne se dessinait jamais.</param>
    internal sealed record Machine(string Nom, HardwareInventory Inventaire, SettingsState Reglages, List<RepairFinding> Sante,
        bool? Eleve = null);

    public static Machine? Active { get; set; }

    /// <summary>
    /// Le premier rejeu (19:23) a écrit la machine AMD dans les vraies données du joueur : deux faux
    /// changements de mémoire dans changements.json, un faux scan dans repair-history.json. Activer une machine redirige
    /// donc d'office les données vers un dossier jetable (<see cref="DossierDonnees.VariableDeDeveloppement"/>), sauf si un
    /// dossier est déjà choisi.
    /// </summary>
    /// <returns>Le dossier où le rejeu écrit.</returns>
    public static string Activer(string nomOuChemin, Func<string, string?> lireVariable, Action<string, string> poserVariable)
    {
        Active = Charger(nomOuChemin);
        var dossier = lireVariable(DossierDonnees.VariableDeDeveloppement);
        if (string.IsNullOrWhiteSpace(dossier) || !Path.IsPathFullyQualified(dossier))
        {
            dossier = Path.Combine(Path.GetTempPath(), "drdxt-machine-" + Active.Nom);
            poserVariable(DossierDonnees.VariableDeDeveloppement, dossier);
        }
        return dossier;
    }

    /// <summary>Un constat enregistré dans l'autre langue du docteur (un testeur anglophone, relu en français) : ses phrases
    /// réécrites dans la langue de l'App, ce qui n'est pas reconnu tel quel (<see cref="Retraduction"/>).</summary>
    public static RepairFinding DansLaLangueDuDocteur(RepairFinding f) => f with
    {
        Explication = Retraduction.Retraduire(f.Explication)!,
        ValeurCourante = Retraduction.Retraduire(f.ValeurCourante),
        ValeurAttendue = Retraduction.Retraduire(f.ValeurAttendue),
        Remede = Retraduction.Retraduire(f.Remede),
    };

    /// <param name="nomOuChemin">Un chemin de fichier, ou un nom cherché dans tests/Shared/machines en remontant depuis l'App.</param>
    public static Machine Charger(string nomOuChemin)
    {
        var chemin = File.Exists(nomOuChemin) ? nomOuChemin : Chercher(nomOuChemin)
            ?? throw new FileNotFoundException($"machine de test introuvable : {nomOuChemin}");
        return JsonSerializer.Deserialize<Machine>(File.ReadAllText(chemin),
            new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!;
    }

    private static string? Chercher(string nom)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "tests", "Shared", "machines", nom + ".json");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    internal sealed class Detecteur(Machine m) : IHardwareDetector, ISettingsStateReader, IStorageDetector, IBenchmarkRunner
    {
        public Task<Result<HardwareInventory>> DetectAsync() => Task.FromResult(Result<HardwareInventory>.Ok(m.Inventaire));
        public Task<Result<SettingsState>> ReadAsync(HardwareInventory hw) => Task.FromResult(Result<SettingsState>.Ok(m.Reglages));
        public Result<IReadOnlyList<StorageDevice>> Detect() => Result<IReadOnlyList<StorageDevice>>.Ok(new List<StorageDevice>());
        public Task<Result<BenchmarkResult>> RunAsync(int targetPid, TimeSpan duration, int runs, CancellationToken ct)
            => Task.FromResult(Result<BenchmarkResult>.Fail(ErrorCodes.WmiFail, $"machine de test « {m.Nom} » : pas de mesure en jeu"));
    }
}
#endif
