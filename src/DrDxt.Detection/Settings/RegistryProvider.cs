using DrDxt.Core;
using Microsoft.Win32;

namespace DrDxt.Detection.Settings;

public interface IRegistryProvider
{
    TriState GetHags();
    TriState GetGameMode();
    /// <summary>MOUSE1 : l'accélération du pointeur (MouseSpeed « 1 » ⇒ On, « 0 » ⇒ Off, autre ⇒ Unknown).</summary>
    TriState? GetAccelerationSouris();
    /// <summary>Index des coeurs logiques parques, liste vide si aucun, <c>null</c> si le
    /// compteur n'a pas pu etre lu. Ne jamais rendre une liste vide pour un echec de lecture.</summary>
    IReadOnlyList<int>? GetParkedCoreIndexes();
    // Bascule « Taux de rafraîchissement variable » de Windows (Paramètres d'affichage → VRR).
    // L'état G-Sync/FreeSync du fabricant vient de NVAPI/ADLX : Unknown ici.
    TriState GetVrr();
    // Fiabilité de la RAM : fichier d'échange présent (On) / désactivé (Off) / illisible (Unknown),
    // lu dans le REG_MULTI_SZ PagingFiles. Détection seule : rien n'est écrit ici.
    TriState GetPagefile();

    // Le fichier d'échange à rendre au disque système (FichierDEchangeADeplacer). Unknown par défaut : une doublure
    // qui ne le lit pas n'affirme rien.
    TriState GetFichierDEchangeADeplacer() => TriState.Unknown;

    // --- Entrées de l'état de redémarrage (pour RestartStateInferencer ; voir sa doc pour les deux pièges) ---
    /// <summary>Vrai si l'une des clés de maintenance existe (leur existence est le drapeau, par
    /// conception de Microsoft), faux si les deux ont été lues absentes, null si la lecture a échoué.</summary>
    bool? GetServicingRebootPending();

    /// <summary>Entrées de PendingFileRenameOperations ; vide si la valeur est absente, null si
    /// illisible. Brut exprès : c'est un signal faible, et c'est l'inférence qui décide, pas ce lecteur.</summary>
    IReadOnlyList<string>? GetPendingFileRenames();

    /// <summary>HiberbootEnabled (Fast Startup).</summary>
    TriState GetFastStartup();

    // --- Les degats deja infliges par un « optimiseur » precedent -----------------------
    /// <summary>SysMain encore actif (On) / desactive par quelqu'un (Off) / non lu (Unknown).</summary>
    TriState GetSysMain();

    /// <summary>MMCSS intact (On) / coupe par `SystemResponsiveness` a 100 (Off) / non lu (Unknown).</summary>
    TriState GetMmcssIntact();

    /// <summary>Les heures d'activité de Windows Update, <c>null</c> si non lues.</summary>
    HeuresActivesLues? GetHeuresActives() => null;
}

public sealed class RegistryProvider : IRegistryProvider
{
    public bool? GetServicingRebootPending()
    {
        try
        {
            // L'existence est le drapeau pour les deux clés : on n'y lit jamais de valeur.
            using var cbs = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (cbs is not null) return true;
            using var wu = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            return wu is not null;
        }
        catch (Exception) { return null; }   // illisible n'est pas « rien en attente »
    }

    public IReadOnlyList<string>? GetPendingFileRenames()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager");
            return k?.GetValue("PendingFileRenameOperations") is string[] v
                ? v
                : Array.Empty<string>();     // clé lue, valeur absente = rien en file
        }
        catch (Exception) { return null; }
    }

    public TriState GetFastStartup()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            var v = k?.GetValue("HiberbootEnabled");
            return v is int i ? (i == 1 ? TriState.On : TriState.Off) : TriState.Unknown;
        }
        catch (Exception) { return TriState.Unknown; }
    }

    public TriState GetHags()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            var v = k?.GetValue("HwSchMode");
            return v is int i ? (i == 2 ? TriState.On : TriState.Off) : TriState.Unknown;
        }
        catch (Exception) { return TriState.Unknown; }
    }

    public TriState GetGameMode()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(GameModeLecture.CheminGameBar);
            // Les deux noms, comme `GameModeOnTweak` les ecrit (voir `GameModeLecture`).
            return GameModeLecture.Interpreter(
                k?.GetValue(GameModeLecture.NomModerne),
                k?.GetValue(GameModeLecture.NomAncien));
        }
        catch (Exception) { return TriState.Unknown; }
    }

    public TriState? GetAccelerationSouris()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse");
            return k?.GetValue("MouseSpeed") switch { "1" => TriState.On, "0" => TriState.Off, _ => TriState.Unknown };
        }
        catch (Exception) { return TriState.Unknown; }
    }

    public IReadOnlyList<int>? GetParkedCoreIndexes()
    {
        // L'API CPU Sets. null = non lu ; liste vide = lu, aucun coeur parque (toujours vide, elle ferait proposer une
        // ecriture sur des machines saines). Plusieurs photos, pas une : voir CoreParkingSampling (mesure du 07/09/2026,
        // de 11 a 16 coeurs parques sur six scans, 0 pendant une compilation).
        var echantillons = new List<IReadOnlyList<int>?>(CoreParkingSampling.Echantillons);
        for (var i = 0; i < CoreParkingSampling.Echantillons; i++)
        {
            if (i > 0) Thread.Sleep(CoreParkingSampling.PauseMs);
            echantillons.Add(CoreParkingReader.Read());
        }

        return CoreParkingSampling.Merge(echantillons);
    }

    public TriState GetVrr()
    {
        // La bascule « Taux de rafraîchissement variable » de Windows :
        // HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\EnableVRR (DWORD, 1=On 0=Off), présente sur Windows 11
        // 22H2+ avec un écran compatible ; absente → Unknown. G-Sync/FreeSync du fabricant : NVAPI/ADLX, lu ailleurs.
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            var v = k?.GetValue("EnableVRR");
            return v is int i ? (i == 1 ? TriState.On : TriState.Off) : TriState.Unknown;
        }
        catch (Exception) { return TriState.Unknown; }
    }

    public TriState GetSysMain()
    {
        // Le type de demarrage d'un service se lit dans le registre, sans SCM ni elevation.
        // HKLM\SYSTEM\CurrentControlSet\Services\SysMain\Start (DWORD ; 4 = desactive)
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\SysMain");
            return WindowsDefaultsInferencer.InferSysMain(k?.GetValue("Start"));
        }
        catch (Exception) { return TriState.Unknown; }
    }

    public TriState GetMmcssIntact()
    {
        // HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile
        //   \SystemResponsiveness (DWORD ; Windows ecrit 20, le mythe ecrit 0)
        //
        // L'ouverture de la cle passe a part de la valeur : valeur absente = defaut de Windows (intact), cle illisible =
        // Unknown ; sinon presque toute machine saine ressortirait « non lue ».
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile");
            return WindowsDefaultsInferencer.InferMmcss(k?.GetValue("SystemResponsiveness"), k is not null);
        }
        catch (Exception) { return TriState.Unknown; }
    }

    public TriState GetPagefile()
    {
        // Le REG_MULTI_SZ HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PagingFiles, classé par
        // PagefileInferencer (testé à part) : vide = Off, une entrée = On ; absente, mauvais type, illisible = Unknown.
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            return PagefileInferencer.Infer(k?.GetValue("PagingFiles") as string[]);
        }
        catch (Exception) { return TriState.Unknown; }
    }

    public TriState GetFichierDEchangeADeplacer()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            var systeme = Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } racine ? racine[0] : 'C';
            var entrees = k?.GetValue("PagingFiles") as string[];
            // Le type des disques (deux requêtes WMI) ne se lit que s'il peut changer la réponse : la plupart des PC sont en
            // gestion automatique (« ?:\pagefile.sys »), où la réponse se passe des disques.
            var nommees = entrees is { Length: > 0 } && entrees.Where(e => e.Trim().Length > 0).All(FichierDEchangeADeplacer.EntreeGereeParLeSysteme);
            return FichierDEchangeADeplacer.Juger(entrees,
                nommees ? DrDxt.Detection.Hardware.DossiersDeJeux.TypesParLettre() : new Dictionary<char, int>(), systeme);
        }
        catch (Exception) { return TriState.Unknown; }
    }

    // Le réglage de Paramètres > Windows Update > Heures d'activité, lisible sans droits.
    public HeuresActivesLues? GetHeuresActives()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(HeuresActives.Cle);
            return HeuresActives.Depuis(k?.GetValue(HeuresActives.NomDebut), k?.GetValue(HeuresActives.NomFin),
                k?.GetValue(HeuresActives.NomAutomatique));
        }
        catch (Exception) { return null; }
    }
}
