using Microsoft.Win32;

namespace DrDxt.Detection.Display;

/// <summary>
/// Retrouve l'EDID d'un moniteur à partir du nom d'interface que rend <c>EnumDisplayDevices</c>.
///
/// Pourquoi ce fichier existe : <see cref="EdidVrrCapabilite"/> sait lire une capacité VRR dans
/// des octets, mais encore faut-il lui donner les octets du bon écran. Le registre en conserve
/// plusieurs : mesure du 18/09/2026 sur la machine de référence, <b>quatre</b> entrées portent
/// un EDID pour <b>deux</b> écrans réellement branchés, les autres étant des branchements
/// mémorisés. Prendre « le premier trouvé » aurait lu un écran débranché.
///
/// Le pont est donc le nom d'interface de l'écran que Windows pilote maintenant :
/// <c>\\?\DISPLAY#AUS2413#5&amp;3ac6cfcf&amp;0&amp;UID4354#{guid}</c>, dont les deux premiers
/// champs sont exactement les deux niveaux de clé sous <c>Enum\DISPLAY</c>.
///
/// Lecture seule, sans élévation, sans dépendance nouvelle.
/// </summary>
internal static class EdidDuMoniteur
{
    private const string RacineEnum = @"SYSTEM\CurrentControlSet\Enum\DISPLAY";

    /// <summary>
    /// Le chemin de registre d'un nom d'interface, ou <c>null</c> s'il n'a pas la forme attendue.
    ///
    /// Pur exprès : c'est la seule partie qui peut se tromper en silence, donc la seule qui a
    /// besoin d'être testée sans machine.
    /// </summary>
    internal static string? CheminRegistre(string? nomInterface)
    {
        if (string.IsNullOrWhiteSpace(nomInterface))
            return null;

        // Forme attendue : \\?\DISPLAY#<pnp>#<instance>#{guid}
        var champs = nomInterface.Split('#');
        if (champs.Length < 4)
            return null;
        if (!champs[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase))
            return null;

        var pnp = champs[1];
        var instance = champs[2];
        if (string.IsNullOrWhiteSpace(pnp) || string.IsNullOrWhiteSpace(instance))
            return null;
        // Un champ qui contient un separateur de cle ferait sortir du sous-arbre vise.
        if (pnp.Contains('\\') || instance.Contains('\\'))
            return null;

        return $@"{RacineEnum}\{pnp}\{instance}\Device Parameters";
    }

    /// <summary>« DISPLAY\pnp\instance », le début du nom d'instance WMI du moniteur
    /// (WmiMonitorConnectionParams), ou null.</summary>
    internal static string? Instance(string? nomInterface)
    {
        var chemin = CheminRegistre(nomInterface);
        return chemin is null ? null : chemin[(RacineEnum.Length - "DISPLAY".Length)..^@"\Device Parameters".Length];
    }

    private const int TailleBloc = 128;

    /// <summary>
    /// L'EDID avec les extensions que son octet 126 annonce et que le registre n'a pas gardées. Mesure du 25/10/2026 sur la
    /// machine de référence : le registre du Samsung ne porte que le bloc de base, alors qu'il annonce une extension CTA, et
    /// c'est là que vit un bloc VRR. Pur : <paramref name="bloc"/> rend le bloc n (128 octets) ou <c>null</c>. Un bloc
    /// manquant ou d'une autre taille laisse l'EDID tel quel, que le lecteur dit « non lu ».
    /// </summary>
    internal static byte[]? Completer(byte[]? edid, Func<int, byte[]?> bloc)
    {
        if (edid is null || edid.Length < TailleBloc) return edid;
        var presentes = edid.Length / TailleBloc - 1;
        int annoncees = edid[126];
        if (presentes >= annoncees) return edid;
        var complet = new List<byte>(edid.AsSpan(0, TailleBloc * (presentes + 1)).ToArray());
        for (var i = presentes + 1; i <= annoncees; i++)
        {
            if (bloc(i) is not { Length: TailleBloc } b) return edid;
            complet.AddRange(b);
        }
        return complet.ToArray();
    }

    /// <summary>Le bloc n de l'EDID par WMI (root\wmi, WmiMonitorDescriptorMethods.WmiGetMonitorRawEEdidV1Block), sur
    /// l'instance de ce moniteur (« DISPLAY\pnp\instance_0 ») ; lecture seule. <c>null</c> si rien n'est lisible.</summary>
    private static byte[]? BlocWmi(string instance, int n) => DrDxt.Core.LectureWmiBornee.Lire(() => BlocWmiNu(instance, n), () => null);

    private static byte[]? BlocWmiNu(string instance, int n)
    {
        try
        {
            using var classe = new System.Management.ManagementClass(@"root\wmi", "WmiMonitorDescriptorMethods", null);
            foreach (System.Management.ManagementObject o in classe.GetInstances())
                using (o)
                {
                    if (o["InstanceName"] is not string nom || !nom.StartsWith(instance + "_", StringComparison.OrdinalIgnoreCase)) continue;
                    using var entree = o.GetMethodParameters("WmiGetMonitorRawEEdidV1Block");
                    entree["BlockId"] = (byte)n;
                    using var sortie = o.InvokeMethod("WmiGetMonitorRawEEdidV1Block", entree, null);
                    return sortie?["BlockContent"] as byte[];
                }
            return null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Les octets d'EDID de ce moniteur, ou <c>null</c> si rien n'est lisible. Les extensions annoncées que le
    /// registre n'a pas gardées sont lues par WMI.</summary>
    internal static byte[]? Lire(string? nomInterface)
    {
        var chemin = CheminRegistre(nomInterface);
        if (chemin is null)
            return null;
        byte[]? edid;
        try
        {
            using var cle = Registry.LocalMachine.OpenSubKey(chemin);
            edid = cle?.GetValue("EDID") as byte[];
        }
        catch (Exception)
        {
            // Une cle refusee ou absente est un « je n'ai pas lu », jamais un « pas de VRR ».
            return null;
        }
        return Instance(nomInterface) is { } instance ? Completer(edid, n => BlocWmi(instance, n)) : edid;
    }
}
