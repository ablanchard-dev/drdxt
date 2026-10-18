using DrDxt.Core;
using DrDxt.GpuNative.PawnIo;

namespace DrDxt.ElevatedHelper.Execution;

/// <summary>
/// La limite de puissance du processeur Intel (MSR 0x610) écrite par PawnIO, dans le helper élevé et
/// nulle part ailleurs. Le registre est relu juste avant : il doit porter exactement l'origine que l'op annonce (ou déjà
/// la valeur demandée : idempotent), sinon le monde a bougé et rien n'est écrit. Après l'écriture, le registre est relu :
/// un verrou du BIOS (bit 63) ou une carte qui refuse rendent un échec dit, jamais un succès sur parole. Volatil : au
/// redémarrage, le BIOS remet sa valeur.
/// </summary>
public sealed class PawnIoCpuPowerActions : ICpuPowerActions
{
    private readonly Func<PawnIoIntel.RegistresDeLimite?> _lire;
    private readonly Func<ulong, bool> _ecrire;
    private readonly Func<bool?> _microcodePerime;
    private readonly Func<ulong, ulong> _firmwareDuDemarrage;
    private readonly Func<int, ulong, ulong?> _origineTension;
    private readonly Func<ulong, ulong?> _boiteOc;
    private readonly Func<bool> _sgx;

    public PawnIoCpuPowerActions() : this(PawnIoIntel.LireRegistres, PawnIoIntel.EcrireLimites, MicrocodePerimeIci,
        courant => OrigineDuDemarrage.Retenir("Pl1Demarrage", courant) ?? courant, PawnIoIntel.EchangerBoiteOc, SgxPresent,
        (plan, lu) => OrigineDuDemarrage.Retenir("TensionDemarrage" + plan, lu)) { }

    /// <summary>Couture de test : lecture et écriture du registre, état du microcode, registre du firmware à ce démarrage (reçoit
    /// le registre courant, rend celui retenu au premier appel depuis le démarrage).</summary>
    internal PawnIoCpuPowerActions(Func<PawnIoIntel.RegistresDeLimite?> lire, Func<ulong, bool> ecrire, Func<bool?>? microcodePerime = null, Func<ulong, ulong>? firmwareDuDemarrage = null,
        Func<ulong, ulong?>? boiteOc = null, Func<bool>? sgx = null, Func<int, ulong, ulong?>? origineTension = null)
    {
        _lire = lire;
        _ecrire = ecrire;
        _microcodePerime = microcodePerime ?? (() => null);
        _firmwareDuDemarrage = firmwareDuDemarrage ?? (courant => courant);
        _boiteOc = boiteOc ?? (_ => null);
        _sgx = sgx ?? (() => true); // par défaut : refus (un exécutant sans lecture de CPUID n'écrit jamais de tension)
        _origineTension = origineTension ?? ((_, lu) => (ulong?)lu);
    }

    /// <summary>La garde Vmin relue ici, dans le processus élevé, et pas seulement dans l'App.</summary>
    private static bool? MicrocodePerimeIci()
    {
        try
        {
            using var cle = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var nom = (cle?.GetValue("ProcessorNameString") as string)?.Trim();
            var rev = MicrocodeVmin.Revision(cle?.GetValue("Update Revision") as byte[]);
            return MicrocodeVmin.Perime(nom, rev, (cle?.GetValue("Identifier") as string)?.Trim());
        }
        catch (Exception) { return null; }
    }

    /// <summary>CPUID.(EAX=7,ECX=0):EBX[2] : SGX. Plundervolt (CVE-2019-11157) n'attaque que SGX ; avec lui, la tension ne s'écrit pas.</summary>
    private static bool SgxPresent()
    {
        try { return System.Runtime.Intrinsics.X86.X86Base.IsSupported && ((System.Runtime.Intrinsics.X86.X86Base.CpuId(7, 0).Ebx >> 2) & 1) == 1; }
        catch (Exception) { return true; } // pas su lire : refus
    }

    /// <summary>Le même décalage sur les cœurs et le cache (ils partagent la tension sur ces processeurs), relatif à la tension que
    /// le BIOS (ou un autre outil) avait mise à ce démarrage, retenue au premier passage : −30 = 30 mV sous cette origine, 0 = l'origine
    /// rendue telle quelle. Jamais au-dessus de l'origine, jamais sous −125 mV au total. Refusé avec SGX (Plundervolt, CVE-2019-11157)
    /// ou si la boîte aux lettres refuse la lecture. Chaque plan est relu : une écriture ignorée (Undervolt Protection, BIOS verrouillé)
    /// rend le code <see cref="ErrorCodes.TensionRefusee"/>, et un plan déjà posé est rendu à sa valeur d'avant. Volatil : rendu au
    /// redémarrage.</summary>
    public Result SetVoltageOffset(int decalageMv)
    {
        if (decalageMv > 0 || decalageMv < -TensionCpu.PlancherMv)
            return Result.Fail(ErrorCodes.HelperOpFailed, "CPU voltage offset is outside 0 to -125 mV; nothing written.");
        if (_sgx())
            return Result.Fail(ErrorCodes.HelperOpFailed, "This processor has SGX (Plundervolt, CVE-2019-11157): its voltage is never written.");
        var avant = new Dictionary<int, ulong>();
        var cibles = new Dictionary<int, int>();
        foreach (var plan in new[] { TensionCpu.PlanCoeurs, TensionCpu.PlanCache })
        {
            if (_boiteOc(TensionCpu.CommandeLecture(plan)) is not { } lu || TensionCpu.DecalageMv(lu) is null)
                return Result.Fail(ErrorCodes.HelperOpFailed, "CPU voltage could not be read from the OC mailbox; nothing written.");
            // L'origine n'a pas pu être retenue : sans elle, des paliers successifs s'empileraient ; rien n'est écrit.
            if (_origineTension(plan, lu) is not { } retenue || TensionCpu.DecalageMv(retenue) is not { } origine)
                return Result.Fail(ErrorCodes.HelperOpFailed, "The original CPU voltage of this boot could not be kept; nothing written.");
            if (origine > 0)
                return Result.Fail(ErrorCodes.HelperOpFailed, "An existing positive CPU voltage offset is left as is; nothing written.");
            var cible = origine + decalageMv;
            if (cible < -TensionCpu.PlancherMv)
                return Result.Fail(ErrorCodes.HelperOpFailed, "CPU voltage offset would go below -125 mV in total; nothing written.");
            avant[plan] = lu;
            cibles[plan] = cible;
        }
        var poses = new List<int>();
        foreach (var plan in avant.Keys)
        {
            var commande = TensionCpu.CommandeEcriture(plan, cibles[plan], avant[plan]);
            var relu = commande is { } c && _boiteOc(c) is not null ? _boiteOc(TensionCpu.CommandeLecture(plan)) : null;
            if (relu is { } r && TensionCpu.DecalageMv(r) is { } mv && Math.Abs(mv - cibles[plan]) <= 1) { poses.Add(plan); continue; }
            // Rendre ce qui a été posé, et ce plan-ci (l'écriture a pu prendre à moitié), puis le relire : « rien n'a changé » ne
            // se dit que relu ; sinon le code dit la tension laissée, que le redémarrage rendra.
            var rendus = true;
            foreach (var p in poses.Append(plan))
            {
                if (TensionCpu.CommandeRetour(p, avant[p]) is { } retour) _boiteOc(retour);
                rendus &= _boiteOc(TensionCpu.CommandeLecture(p)) is { } apres && TensionCpu.DecalageMv(apres) == TensionCpu.DecalageMv(avant[p]);
            }
            return rendus
                ? Result.Fail(ErrorCodes.TensionRefusee,
                    "CPU voltage offset was not read back: Undervolt Protection or the firmware refuses it; nothing is left changed.")
                : Result.Fail(ErrorCodes.TensionNonRendue,
                    "CPU voltage could not be restored after a refusal; it stays changed until the PC restarts, which restores it.");
        }
        return Result.Ok();
    }

    public Result SetPackagePowerLimit(ulong valeur, ulong origine)
    {
        var courant = _lire();
        if (courant is null)
            return Result.Fail(ErrorCodes.HelperOpFailed, "CPU power limit register not readable (PawnIO absent, not an Intel CPU, or read failed).");
        if (courant.PkgPowerLimit == valeur)
            return Result.Ok();
        if (courant.PkgPowerLimit != origine)
            return Result.Fail(ErrorCodes.HelperOpFailed, "CPU power limit register changed since it was read; nothing written.");
        // Monter la PL1 au-delà de ce que le firmware tenait à ce démarrage, sur un Core 13e/14e au microcode d'avant le
        // correctif Vmin : refusé, quel que soit l'appelant (guide 5.3). Baisser reste permis, et revenir jusqu'à la valeur
        // du firmware aussi (annuler un plafond est une remontée : la refuser laisserait le plafond collé).
        var firmware = _firmwareDuDemarrage(courant.PkgPowerLimit);
        // Le bit 15 éteint la PL1 : le processeur tient alors la PL2 sans fin (4095 W sur une carte « sans limite »), sans que
        // le champ ne « monte ». Aucun levier ne l'éteint (ComposerPl1 le pose toujours) ; seul le rendu exact des bits 15:0
        // du firmware de ce démarrage peut le laisser à 0.
        if ((valeur & (1UL << 15)) == 0 && (valeur & 0xFFFF) != (firmware & 0xFFFF))
            return Result.Fail(ErrorCodes.HelperOpFailed, "CPU power limit PL1 may not be disabled (bit 15); nothing written.");
        // Une PL1 jamais sous le cinquième de la PL2 que la carte porte (0,125 W briderait le processeur jusqu'au redémarrage, à la
        // demande de n'importe quel programme du compte), sauf la PL1 d'usine de ce démarrage, qui se rend toujours (un portable
        // OEM tient parfois 25 W sous une PL2 de 157 W). Non chiffré : un non-K rendu à 65 W sous 219 W (30 %) et le plafond
        // thermique (jamais sous 125 W) restent au-dessus.
        if ((valeur & 0x7FFF) * 5 < ((valeur >> 32) & 0x7FFF) && (valeur & 0x7FFF) != (firmware & 0x7FFF))
            return Result.Fail(ErrorCodes.HelperOpFailed, "CPU power limit PL1 may not go below a fifth of the PL2 the board holds; nothing written.");
        if ((valeur & 0x7FFF) > (firmware & 0x7FFF) && _microcodePerime() == true)
            return Result.Fail(ErrorCodes.HelperOpFailed, "CPU power limit raise refused: this Core 13th/14th gen runs a microcode older than Intel's Vmin fix; nothing written.");
        // Une montée au-delà du firmware n'a pour plafond que la PL2 du registre, que bien des cartes mettent à 0x7FFF
        // (4095 W, « sans limite ») : les bornes de la montée du levier (LimiteDePuissance.CibleDeMontee) sont relues ici,
        // quel que soit l'appelant. Jamais au-dessus de la puissance maximale que le processeur publie (0x614, bits 46:32,
        // même unité que la PL1) ; sans maximum publié, jamais sous une PL2 « sans limite ». Rendre la valeur du firmware
        // n'est pas une montée et passe toujours.
        if ((valeur & 0x7FFF) > (firmware & 0x7FFF))
        {
            var maximale = (courant.PkgPowerInfo >> 32) & 0x7FFF;
            if (maximale > 0 && (valeur & 0x7FFF) > maximale)
                return Result.Fail(ErrorCodes.HelperOpFailed, "CPU power limit raise refused: PL1 above the maximum power the processor publishes; nothing written.");
            if (maximale == 0 && ((valeur >> 32) & 0x7FFF) == 0x7FFF)
                return Result.Fail(ErrorCodes.HelperOpFailed, "CPU power limit raise refused: the board's PL2 says no limit and the processor publishes no maximum; nothing written.");
        }
        if (RegistreDeLimiteCpu.Verrou(courant.PkgPowerLimit))
            return Result.Fail(ErrorCodes.HelperOpFailed, "CPU power limit register is locked by the firmware until the next boot; nothing written.");
        return _ecrire(valeur)
            ? Result.Ok()
            : Result.Fail(ErrorCodes.HelperOpFailed, "CPU power limit write was not read back; the board refused it.");
    }
}

/// <summary>Ce que le firmware avait laissé à ce démarrage (la PL1 du registre 0x610, la tension de chaque plan), une valeur par nom,
/// retenue au premier passage du helper dans une clé volatile de HKLM : Windows l'efface à chaque redémarrage, donc sa présence
/// identifie le démarrage sans aucune horloge (un saut NTP, une sortie de veille ou un changement d'heure ne la trompent pas).
/// Écriture réservée aux administrateurs : un programme sans droits ne peut pas élargir les gardes. Une mémoire inaccessible rend
/// null : l'appelant refuse (la tension) ou reste aussi strict qu'avant (la PL1), jamais plus large. Limite dite : un outil tiers
/// qui a changé le registre avant le premier passage de DrDXT fait retenir sa valeur.</summary>
internal static class OrigineDuDemarrage
{
    private const string CleParDefaut = @"SOFTWARE\DrDXT\Helper";

    public static ulong? Retenir(string nom, ulong courant, Microsoft.Win32.RegistryKey? racine = null, string? chemin = null)
    {
        try
        {
            using var parent = (racine ?? Microsoft.Win32.Registry.LocalMachine).CreateSubKey(chemin ?? CleParDefaut, writable: true);
            foreach (var ancien in new[] { "Demarrage", "Pl1Demarrage" }) parent.DeleteValue(ancien, throwOnMissingValue: false);
            var cle = parent.CreateSubKey("CeDemarrage", Microsoft.Win32.RegistryKeyPermissionCheck.ReadWriteSubTree,
                Microsoft.Win32.RegistryOptions.Volatile);
            // CreateSubKey ouvre une clé existante sans changer sa nature : une clé permanente (créée par erreur) survivrait aux
            // redémarrages. Une clé volatile refuse les sous-clés permanentes : si la sonde passe, la clé est recréée volatile.
            try
            {
                cle.CreateSubKey("sonde", Microsoft.Win32.RegistryKeyPermissionCheck.Default, Microsoft.Win32.RegistryOptions.None)?.Dispose();
                cle.Dispose();
                parent.DeleteSubKeyTree("CeDemarrage", throwOnMissingSubKey: false);
                cle = parent.CreateSubKey("CeDemarrage", Microsoft.Win32.RegistryKeyPermissionCheck.ReadWriteSubTree,
                    Microsoft.Win32.RegistryOptions.Volatile);
            }
            catch (IOException) { } // refusée : la clé est bien volatile
            using var _ = cle;
            if (cle.GetValue(nom) is long retenu) return unchecked((ulong)retenu);
            cle.SetValue(nom, unchecked((long)courant), Microsoft.Win32.RegistryValueKind.QWord);
            return courant;
        }
        catch (Exception) { return null; }
    }
}
