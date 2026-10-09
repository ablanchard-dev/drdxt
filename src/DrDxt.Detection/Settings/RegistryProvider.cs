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
    // Windows "Variable refresh rate" toggle (Display settings → VRR).
    // G-Sync/FreeSync vendor state deferred to M2 (NVAPI/ADLX) — Unknown here.
    TriState GetVrr();
    // NS-4 (RAM reliability): page file present (On) vs DISABLED (Off) vs unreadable (Unknown),
    // read from the PagingFiles REG_MULTI_SZ. Detection only — never written here.
    TriState GetPagefile();

    // --- Restart state inputs (feed RestartStateInferencer; see its doc for the two traps) ---
    /// <summary>True if either servicing key EXISTS (their existence is the flag, by Microsoft's
    /// design), false when both were read and absent, null when the read itself failed.</summary>
    bool? GetServicingRebootPending();

    /// <summary>PendingFileRenameOperations entries; empty when the value is absent, null when
    /// unreadable. Deliberately raw: it is a WEAK signal and the inferencer decides, not us.</summary>
    IReadOnlyList<string>? GetPendingFileRenames();

    /// <summary>HiberbootEnabled (Fast Startup).</summary>
    TriState GetFastStartup();

    // --- L1.5 : les degats deja infliges par un « optimiseur » precedent -----------------------
    /// <summary>SysMain encore actif (On) / desactive par quelqu'un (Off) / non lu (Unknown).</summary>
    TriState GetSysMain();

    /// <summary>MMCSS intact (On) / `SystemResponsiveness` mis a 0 (Off) / non lu (Unknown).</summary>
    TriState GetMmcssIntact();

    /// <summary>06/10/2026 (L-WU) : les heures d'activité de Windows Update, <c>null</c> si non lues.</summary>
    HeuresActivesLues? GetHeuresActives() => null;
}

public sealed class RegistryProvider : IRegistryProvider
{
    public bool? GetServicingRebootPending()
    {
        try
        {
            // Existence IS the flag for both keys — we never read a value out of them.
            using var cbs = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (cbs is not null) return true;
            using var wu = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            return wu is not null;
        }
        catch (Exception) { return null; }   // unreadable is not "nothing pending"
    }

    public IReadOnlyList<string>? GetPendingFileRenames()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager");
            return k?.GetValue("PendingFileRenameOperations") is string[] v
                ? v
                : Array.Empty<string>();     // key read, value absent = nothing queued
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
            // Les deux noms : `GameModeOnTweak` ecrit les deux, la detection n'en lisait qu'un.
            // Voir `GameModeLecture` pour le pourquoi et la mesure.
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
        // Lecture reelle par l'API CPU Sets. null = non lu ; liste vide = lu, aucun
        // coeur parque. Le bouchon precedent rendait toujours une liste vide, ce qui
        // faisait dire au bareme "machine saine" et a l'heuristique X3D "l'optimiseur
        // ne route pas", donc proposait une ecriture sur des machines saines.
        //
        // Plusieurs photos, pas une. Mesure du 07/09/2026 : six scans d'affilee ont rendu
        // 11, 14, 14, 16 et 16 coeurs parques, et un scan lance pendant une compilation en a
        // rendu 0 -- ce qui faisait DISPARAITRE le seul constat Minor du produit. Le meme PC
        // recevait deux verdicts selon l'heure du scan. Un coeur qui dort une seconde sur deux
        // dort ; une photo prise pendant qu'il travaille dit le contraire.
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
        // Windows "Variable refresh rate" global toggle lives at:
        // HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\EnableVRR (DWORD, 1=On 0=Off)
        // This key is present on Windows 11 22H2+ with a VRR-capable display.
        // If absent (older Windows, no capable display, or key simply not written) → Unknown.
        // G-Sync/FreeSync vendor state requires NVAPI/ADLX → deferred to M2.
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
        // Le type de demarrage d'un service vit dans le registre : pas besoin du SCM, donc pas
        // besoin d'elevation ni d'un service en cours d'execution pour savoir qu'il est desactive.
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
        // On passe a l'inferenceur le fait que la CLE se soit ouverte, separement de la valeur :
        // une valeur absente est le defaut de Windows (donc intact), une cle illisible est un
        // Unknown. Sans cette distinction, presque toute machine saine ressortirait « non lue ».
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
        // Page file config lives in the PagingFiles REG_MULTI_SZ:
        // HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PagingFiles
        // → string[]. Empty = DISABLED (Off); any entry = a page file exists (On). Value absent
        // / wrong kind / unreadable → Unknown (never claim Off blindly). Classification is the
        // pure PagefileInferencer so it stays unit-tested; this method is only the OS read.
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            return PagefileInferencer.Infer(k?.GetValue("PagingFiles") as string[]);
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
