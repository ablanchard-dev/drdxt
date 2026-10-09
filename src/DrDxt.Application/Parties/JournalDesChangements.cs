using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Application.Parties;

/// <summary>Un changement daté sur la machine : un réglage du BIOS, un levier posé. Ce qui se juge ensuite en jeu.
/// <paramref name="Depuis"/> : revue du 04/10 (P1), quand l'heure exacte n'est pas connue, la borne basse (pour un
/// réglage BIOS, la dernière mesure sous l'ancien réglage). Les parties entre <c>Depuis</c> et <c>Quand</c> ne comptent
/// d'aucun côté.
/// <paramref name="Cle"/> : 04/10/2026 (plan 2.3), l'identifiant stable du réglage et sa nouvelle valeur
/// (« windows.hags:on ») ; le libellé est traduit, la clé permet de relier le changement au levier qui l'a posé.</summary>
/// <paramref name="Fige"/> : 06/10/2026 (revue F-2), le bilan au moment où il a été tranché. Le relevé ne garde que
/// les 20 dernières parties : rejugé à chaque affichage, un gain prouvé redevenait « pas encore jugé » une fois ses parties
/// d'avant sorties de l'anneau. Une fois tranché, il ne bouge plus.
public sealed record Changement(DateTimeOffset Quand, string Libelle, DateTimeOffset? Depuis = null, string? Cle = null,
    BilanDuChangement? Fige = null);

/// <summary>
/// 04/10/2026 (plan 1.3 et 1.4) : rien ne gardait la DATE d'un changement. Le journal des leviers ne garde que leurs
/// valeurs, et le gain du réglage BIOS du 04/10 (+31 % de calcul tenu au banc) ne pouvait se juger en jeu faute de
/// savoir quelles parties étaient avant et lesquelles après. Fichier absent ou abîmé : aucun changement, jamais inventé.
/// </summary>
public sealed class JournalDesChangements
{
    public const string CodeErreur = "JOURNAL_CHANGEMENTS";
    public const int Gardes = 50;
    private readonly FichierJsonAtomique _fichier;

    private readonly string _nomDuVerrou;

    public JournalDesChangements(string chemin)
    {
        _fichier = new FichierJsonAtomique(chemin, CodeErreur);
        // 07/10/2026 (test instable sous charge, puis cause relue) : Ajouter et Figer lisent, modifient et réécrivent ; deux
        // écrivains en même temps (le relevé de fond et l'App, ou un retrait et l'affichage) perdaient l'écriture de
        // l'autre, et un verdict figé au retrait disparaissait. Un verrou nommé d'après le fichier, partagé entre processus.
        _nomDuVerrou = @"Local\DrDXT-journal-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(chemin).ToUpperInvariant())))[..32];
    }

    private Result SousVerrou(Func<Result> ecrire)
    {
        using var verrou = new Mutex(false, _nomDuVerrou);
        var pris = false;
        try
        {
            try { pris = verrou.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { pris = true; /* l'ancien détenteur est mort : le fichier, lui, est atomique */ }
            if (!pris) return Result.Fail(CodeErreur, "journal des changements occupé : écriture remise au prochain passage.");
            return ecrire();
        }
        finally { if (pris) verrou.ReleaseMutex(); }
    }

    public static JournalDesChangements ParDefaut() => new(FichierJsonAtomique.CheminLocal("changements.json"));

    /// <summary>Le plus récent d'abord.</summary>
    public IReadOnlyList<Changement> Lire()
    {
        var texte = _fichier.Read();
        if (!texte.IsOk || string.IsNullOrWhiteSpace(texte.Value)) return [];
        try { return JsonSerializer.Deserialize<List<Changement>>(texte.Value) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>Ajoute, sauf si le même libellé est déjà noté à la même minute (un scan relancé ne double rien).</summary>
    public Result Ajouter(Changement c) => SousVerrou(() =>
    {
        var existants = Lire();
        if (existants.Any(e => e.Libelle == c.Libelle && Math.Abs((e.Quand - c.Quand).TotalMinutes) < 1)) return Result.Ok();
        var liste = new[] { c }.Concat(existants).OrderByDescending(e => e.Quand).Take(Gardes).ToList();
        return _fichier.WriteAtomic(JsonSerializer.Serialize(liste));
    });

    /// <summary>Fige le bilan tranché d'un changement (même date, même libellé) ; un bilan déjà figé n'est jamais réécrit.</summary>
    public Result Figer(Changement c, BilanDuChangement bilan) => bilan.Verdict == VerdictEnJeu.PasEncoreJuge ? Result.Ok() : SousVerrou(() =>
    {
        var existants = Lire();
        var liste = existants.Select(e => e.Fige is null && e.Quand == c.Quand && e.Libelle == c.Libelle ? e with { Fige = bilan } : e).ToList();
        return liste.SequenceEqual(existants) ? Result.Ok() : _fichier.WriteAtomic(JsonSerializer.Serialize(liste));
    });
}
