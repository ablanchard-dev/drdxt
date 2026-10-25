using DrDxt.Core;
using DrDxt.GpuNative.PawnIo;

namespace DrDxt.Detection.Cpu;

/// <summary>
/// Les registres d'un Ryzen lus par PawnIO (<see cref="PawnIoAmd"/>, en lecture seule), décodés par le cœur. Rien n'est lu
/// sans PawnIO, sur un processeur qui n'est pas AMD, ou hors des adresses de <see cref="SmnAmd"/>. Non validé sur machine
/// AMD réelle : l'App ne s'en sert (température, timings, registre de redémarrage) que sur une génération de
/// <see cref="LecturesAmdProfondes.ValideesEnVie"/> (aucune encore) ; le kit de validation, lui, les lit pour les confronter
/// à un outil de référence.
/// </summary>
public static class LecturesAmd
{
    /// <summary>La génération reconnue de ce processeur, ou null (pas AMD, CPUID non lu, génération absente de la table).</summary>
    public static GenerationAmd.Ligne? Generation()
    {
        var (fabricant, signature) = Cpuid.Lire();
        return signature is { } s ? GenerationAmd.Reconnaitre(fabricant, s) : null;
    }

    /// <summary>Tctl, moins le décalage des modèles qui l'affichent au-dessus de la vraie température ; null si non lue.</summary>
    public static double? Temperature(string? nomDuProcesseur)
        => Cpuid.Lire().Signature is { } s ? Temperature(nomDuProcesseur, s, Smn) : null;

    /// <summary>Couture de test : <paramref name="smn"/> lit un registre SMN brut ; seules les adresses de
    /// <see cref="SmnAmd"/> lui sont demandées.</summary>
    internal static double? Temperature(string? nomDuProcesseur, uint signature, Func<uint, uint?> smn)
    {
        if (Borne(smn)(TemperatureAmd.AdresseTctl) is not { } tctl || TemperatureAmd.Tctl(tctl) is not { } t) return null;
        return t - TemperatureAmd.DecalageTdie(GenerationAmd.Decoder(signature).Famille, nomDuProcesseur);
    }

    private static uint? Smn(uint adresse) => PawnIoAmd.LireSmn(adresse, SmnAmd.Autorisee);

    private static Func<uint, uint?> Borne(Func<uint, uint?> smn) => a => SmnAmd.Autorisee(a) ? smn(a) : null;

    /// <summary>La porte des lectures profondes pour ce processeur : sa génération est validée en vie. Porte vide : la
    /// génération n'est même pas lue.</summary>
    public static bool PorteOuverte(IReadOnlySet<string> validees, Func<GenerationAmd.Ligne?> generation)
        => validees.Count > 0 && generation() is { } g && validees.Contains(g.Generation);

    /// <summary>L'étage AMD lu dans l'App, en administrateur (sans droits, PawnIO refuse à chaque fois : on ne le retente
    /// pas chaque seconde).</summary>
    public static TemperatureHwinfo.Lecture? TemperatureDirecte()
        => DerrierePorte(LecturesAmdProfondes.ValideesEnVie, Generation,
            () => PawnIoAmd.Installe() && PawnIoAmd.ProcesseurAmd() && TemperaturePawnIo.Eleve.Value, () => Temperature(NomDuProcesseur()));

    /// <summary>La même lecture servie par le service DrDXT (App sans droits, ou lecture directe ratée).</summary>
    public static TemperatureHwinfo.Lecture? TemperatureParLeService()
        => DerrierePorte(LecturesAmdProfondes.ValideesEnVie, Generation,
            () => PawnIoAmd.Installe() && PawnIoAmd.ProcesseurAmd(), () => ProcesseurParLeService.LireTout()?.TemperatureAmdC);

    /// <summary>Ce que le service lit pour la servir : la même porte, côté service.</summary>
    public static double? TemperaturePourLeService()
        => PorteOuverte(LecturesAmdProfondes.ValideesEnVie, Generation) && PawnIoAmd.Installe() && PawnIoAmd.ProcesseurAmd()
            ? Temperature(NomDuProcesseur()) : null;

    /// <summary>Couture de test : porte, puis PawnIO prêt, puis la lecture ; rien n'est appelé après une étape fermée.</summary>
    internal static TemperatureHwinfo.Lecture? DerrierePorte(IReadOnlySet<string> validees, Func<GenerationAmd.Ligne?> generation,
        Func<bool> pret, Func<double?> lire)
        => PorteOuverte(validees, generation) && pret() && lire() is { } t ? new TemperatureHwinfo.Lecture(t, TemperaturePawnIo.Libelle) : null;

    /// <summary>La température de chaque CCD présent ; null si la puce n'est pas dans la table ou si rien n'est lu.</summary>
    public static IReadOnlyList<double>? TemperaturesCcd()
    {
        var (_, signature) = Cpuid.Lire();
        if (signature is not { } s) return null;
        var (famille, modele) = GenerationAmd.Decoder(s);
        if (TemperatureAmd.Ccd(famille, modele) is not { } ccd) return null;
        var lues = new List<double>();
        for (var n = 0; n < ccd.Nombre; n++)
        {
            if (PawnIoAmd.LireSmn(TemperatureAmd.AdresseCcd(ccd.Decalage, n), SmnAmd.Autorisee) is not { } r) return null;
            if (TemperatureAmd.TemperatureCcd(r) is { } t) lues.Add(t);
        }
        return lues;
    }

    /// <summary>Les registres de timings de chaque canal mémoire actif, avec son numéro physique ; null si non lus. Un canal
    /// est tenu pour actif quand son premier registre de timings n'est ni nul ni à tous les bits levés : règle à confirmer sur
    /// machine réelle. Les canaux vides sont sautés : la lettre d'un canal vient de son numéro, jamais de son rang.</summary>
    public static IReadOnlyList<(int Canal, IReadOnlyDictionary<uint, uint> Registres)>? CanauxMemoire() => CanauxMemoire(Smn);

    /// <summary>Couture de test : <paramref name="smn"/> lit un registre SMN brut, borné aux adresses de <see cref="SmnAmd"/>.</summary>
    internal static IReadOnlyList<(int Canal, IReadOnlyDictionary<uint, uint> Registres)>? CanauxMemoire(Func<uint, uint?> smn)
    {
        var lire = Borne(smn);
        var canaux = new List<(int, IReadOnlyDictionary<uint, uint>)>();
        for (var canal = 0; canal < SmnAmd.CanauxMax; canal++)
        {
            var registres = new Dictionary<uint, uint>();
            foreach (var r in TimingsMemoireAmd.Registres)
            {
                if (lire(TimingsMemoireAmd.Adresse(canal, r)) is not { } v) return null;
                registres[r] = v;
            }
            if (registres[TimingsMemoireAmd.Registres[1]] is not (0 or uint.MaxValue)) canaux.Add((canal, registres));
        }
        return canaux;
    }

    public static string? NomDuProcesseur()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch (Exception) { return null; }
    }
}
