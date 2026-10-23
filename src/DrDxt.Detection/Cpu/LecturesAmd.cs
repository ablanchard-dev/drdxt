using DrDxt.Core;
using DrDxt.GpuNative.PawnIo;

namespace DrDxt.Detection.Cpu;

/// <summary>
/// Les registres d'un Ryzen lus par PawnIO (<see cref="PawnIoAmd"/>, en lecture seule), décodés par le cœur. Rien n'est lu
/// sans PawnIO, sur un processeur qui n'est pas AMD, ou hors des adresses de <see cref="SmnAmd"/>. Non validé sur machine
/// AMD réelle : l'App ne s'en sert que sur une génération de <see cref="LecturesAmdProfondes.ValideesEnVie"/> (aucune
/// encore) ; le kit de validation, lui, les lit pour les confronter à un outil de référence.
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
    {
        var (_, signature) = Cpuid.Lire();
        if (signature is not { } s || PawnIoAmd.LireSmn(TemperatureAmd.AdresseTctl, SmnAmd.Autorisee) is not { } tctl) return null;
        return TemperatureAmd.Tctl(tctl) - TemperatureAmd.DecalageTdie(GenerationAmd.Decoder(s).Famille, nomDuProcesseur);
    }

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

    /// <summary>Les registres de timings de chaque canal mémoire actif, clés au canal 0 ; null si non lus. Un canal est
    /// tenu pour actif quand son premier registre de timings n'est ni nul ni à tous les bits levés : règle à confirmer sur
    /// machine réelle.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<uint, uint>>? CanauxMemoire()
    {
        var canaux = new List<IReadOnlyDictionary<uint, uint>>();
        for (var canal = 0; canal < SmnAmd.CanauxMax; canal++)
        {
            var registres = new Dictionary<uint, uint>();
            foreach (var r in TimingsMemoireAmd.Registres)
            {
                if (PawnIoAmd.LireSmn(TimingsMemoireAmd.Adresse(canal, r), SmnAmd.Autorisee) is not { } v) return null;
                registres[r] = v;
            }
            if (registres[TimingsMemoireAmd.Registres[1]] is not (0 or uint.MaxValue)) canaux.Add(registres);
        }
        return canaux;
    }

    /// <summary>La température par les registres, sur une génération validée seulement : sinon null, et la chaîne passe la
    /// main. Le nom vient du registre Windows du processeur.</summary>
    public static TemperatureHwinfo.Lecture? TemperatureSiValidee()
    {
        if (!PawnIoAmd.Installe() || Generation() is not { } g || !LecturesAmdProfondes.ValideesEnVie.Contains(g.Generation)) return null;
        return Temperature(NomDuProcesseur()) is { } t ? new TemperatureHwinfo.Lecture(t, Libelle) : null;
    }

    public static string Libelle => Docteur.T("TP.LibelleAmd");

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
