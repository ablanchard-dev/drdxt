using System.Text.Json;

namespace DrDxt.Core;

/// <summary>
/// L'état du processeur servi par le service DrDXT (SYSTEM) à l'App, qui tourne sans droits et ne peut
/// pas lire les registres elle-même. Une connexion au tube = une lecture ; le tube ne lit rien du client (aucune entrée,
/// aucun paramètre : pas de surface d'attaque). Ensemble fermé : TjMax (0x1A2), paquet (0x1B1), cœur 0 (0x19C), et les
/// limites de puissance (0x606 unités, 0x610 limite, 0x614 puissances publiées, copie MMIO de la carte) ; jamais les
/// compteurs d'énergie RAPL (attaque Platypus, Intel les a retirés aux non-administrateurs exprès). Et les
/// ventilateurs de la carte mère (régime, cycle), lus par sa puce Super I/O.
/// </summary>
public sealed record EtatCpuServi(DateTimeOffset Le, EtatCpu? Etat, RegistresCpuLus? Limites = null, ulong? CopieMmio = null,
    IReadOnlyList<VentilateurLu>? Ventilateurs = null)
{
    public const string NomDuTube = "DrDXT.Processeur";
    public const string NomDuService = "DrDxtReconciler";

    /// <summary>Au-delà, une lecture est périmée : « non lue », jamais une vieille température.</summary>
    public static readonly TimeSpan Fraicheur = TimeSpan.FromSeconds(10);

    public string VersJson() => JsonSerializer.Serialize(this);

    /// <summary>Pur : l'état, s'il est lisible et frais (ni trop vieux, ni daté du futur au-delà d'une seconde).</summary>
    public static EtatCpu? Valider(string? json, DateTimeOffset maintenant) => Lire(json, maintenant)?.Etat;

    /// <summary>Pur : toute la lecture, si elle est lisible et fraîche ; sinon null.</summary>
    public static EtatCpuServi? Lire(string? json, DateTimeOffset maintenant)
    {
        try
        {
            return json is not null && JsonSerializer.Deserialize<EtatCpuServi>(json) is { } lu
                   && maintenant - lu.Le <= Fraicheur && lu.Le - maintenant <= TimeSpan.FromSeconds(1)
                ? lu : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>Un en-tête de ventilateur de la carte mère. <paramref name="Index"/> 0 = l'en-tête du processeur (CPU_FAN)
/// sur les cartes lues par leur puce ; à partir de <see cref="DebutHwinfo"/>, un ventilateur publié par HWiNFO (secours), avec
/// son <paramref name="Nom"/>. Régime 0 = arrêté ou rien de branché ; cycle PWM en %, null = inconnu (HWiNFO ne le donne pas).</summary>
public sealed record VentilateurLu(int Index, int TrMin, int? PwmPourcent, string? Nom = null)
{
    public const int DebutHwinfo = 100;
}
