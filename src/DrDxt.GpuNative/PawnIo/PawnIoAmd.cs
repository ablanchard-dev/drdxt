namespace DrDxt.GpuNative.PawnIo;

/// <summary>
/// La frontière PawnIO pour un Ryzen, en LECTURE SEULE. Les modules AMDReset, AMDFamily17 et RyzenSMU de namazso/PawnIO.Modules,
/// publication 0.2.11 (LGPL-2.1+, third_party/pawnio-modules), embarqués tels quels et figés par leur empreinte, comme ceux
/// d'Intel (<see cref="PawnIoIntel"/>). PawnIO n'est jamais installé par DrDXT ; sans lui rien n'est lu.
///
/// Ces modules savent écrire (des MSR, des registres du SMU, n'importe quelle commande du SMU) : la lecture seule vient
/// d'ici. DrDXT n'appelle que les ioctl de <see cref="IoctlsAppeles"/>, en constantes ; un test lit ce fichier et refuse tout
/// autre nom. Les seules commandes qui partent vers le SMU sont celles que le module fixe lui-même pour chaque puce derrière
/// ces ioctl : la version du SMU, la version et l'adresse de la table PM, et sa copie en mémoire (les quatre lectures de
/// la liste fermée de DrDxt.Core.SmuAmd) ; aucune n'est choisie par DrDXT. Le module ne borne pas les adresses SMN : l'appelant donne la liste des adresses permises, et rien hors d'elle
/// n'est lu. Chaque accès SMN ou SMU prend le verrou partagé Access_PCI, que les modules demandent et que les autres outils
/// de lecture prennent aussi : deux lecteurs à la fois corrompraient la boîte aux lettres du SMU.
/// </summary>
public static class PawnIoAmd
{
    public const string EmpreinteReset = "5da2295732f8211e8ca16c72a5920b3c4ad7273a4c9097a07a7751f9ea920895";
    public const string EmpreinteFamille17 = "dae74615761b78bdf064dfb3e136252ddcc6fc727d88f14738d0e5800d427a91";
    public const string EmpreinteSmu = "301d9ca397108e09f31bfbd5ac4c9bb4f352a5de68532c32db3ba7ddcde93450";

    private const string IoctlReset = "ioctl_amd_reset_status";
    private const string IoctlLireSmn = "ioctl_read_smn";
    private const string IoctlResoudreTablePm = "ioctl_resolve_pm_table";
    private const string IoctlCopierTablePm = "ioctl_update_pm_table";
    private const string IoctlLireTablePm = "ioctl_read_pm_table";
    private const string IoctlNomDeCode = "ioctl_get_code_name";
    private const string IoctlVersionSmu = "ioctl_get_smu_version";

    /// <summary>La liste fermée : les seuls ioctl que DrDXT appelle. Aucun n'écrit ; aucun n'envoie une commande choisie.</summary>
    public static readonly IReadOnlyList<string> IoctlsAppeles =
        [IoctlReset, IoctlLireSmn, IoctlResoudreTablePm, IoctlCopierTablePm, IoctlLireTablePm, IoctlNomDeCode, IoctlVersionSmu];

    private static readonly TimeSpan DelaiDuVerrou = TimeSpan.FromMilliseconds(200);
    private static readonly object Verrou = new();
    private static nint _poigneeReset, _poigneeFamille17, _poigneeSmu;

    public static bool Installe() => PawnIoIntel.Installe();

    /// <summary>Les modules ne connaissent que les registres AMD : ailleurs, rien n'est lu.</summary>
    public static bool ProcesseurAmd()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return k?.GetValue("VendorIdentifier") as string == "AuthenticAMD";
        }
        catch (Exception) { return false; }
    }

    /// <summary>FCH::PM::S5_RESET_STATUS, lu sans l'effacer ; null si non lu.</summary>
    public static uint? EtatDuReset()
        => Avec<uint>(ref _poigneeReset, "AMDReset", EmpreinteReset, avecVerrouPci: false,
            h => Executer(h, IoctlReset, [], 1) is { } o ? (uint)o[0] : null);

    /// <summary>Un registre SMN, seulement si <paramref name="autorisee"/> le permet ; null sinon, ou si non lu.</summary>
    public static uint? LireSmn(uint adresse, Func<uint, bool> autorisee)
    {
        if (!autorisee(adresse)) return null;
        return Avec<uint>(ref _poigneeFamille17, "AMDFamily17", EmpreinteFamille17, avecVerrouPci: true,
            h => Executer(h, IoctlLireSmn, [adresse], 1) is { } o ? (uint)o[0] : null);
    }

    /// <summary>Le nom de code de la puce selon le module, et la version du SMU ; null si non lus.</summary>
    public static (uint NomDeCode, uint VersionSmu)? Identite()
        => Avec(ref _poigneeSmu, "RyzenSMU", EmpreinteSmu, avecVerrouPci: true, h =>
            Executer(h, IoctlNomDeCode, [], 1) is { } code && Executer(h, IoctlVersionSmu, [], 1) is { } version
                ? ((uint)code[0], (uint)version[0]) : ((uint, uint)?)null);

    /// <summary>La table PM, copiée en mémoire par le SMU puis lue : sa version et ses <paramref name="octets"/> premiers
    /// octets ; null si non lue. La seule commande envoyée est la copie, fixée par le module.</summary>
    public static (uint Version, byte[] Table)? LireTablePm(int octets)
        => Avec(ref _poigneeSmu, "RyzenSMU", EmpreinteSmu, avecVerrouPci: true, h =>
        {
            if (Executer(h, IoctlResoudreTablePm, [], 2) is not { } resolue) return ((uint, byte[])?)null;
            if (Executer(h, IoctlCopierTablePm, [], 0) is null) return null;
            if (Executer(h, IoctlLireTablePm, [], (octets + 7) / 8) is not { } mots) return null;
            var table = new byte[octets];
            Buffer.BlockCopy(mots, 0, table, 0, octets);
            return ((uint)resolue[0], table);
        });

    /// <summary>Ferme les poignées : rien ne reste chargé entre deux lectures demandées.</summary>
    public static void FermerTout()
    {
        lock (Verrou)
        {
            Fermer(ref _poigneeReset);
            Fermer(ref _poigneeFamille17);
            Fermer(ref _poigneeSmu);
        }
    }

    private static ulong[]? Executer(nint h, string ioctl, ulong[] entree, int sortie)
    {
        var resultat = new ulong[sortie];
        return PawnIoIntel.pawnio_execute(h, ioctl, entree, (nuint)entree.Length, resultat, (nuint)sortie, out _) == 0 ? resultat : null;
    }

    private static T? Avec<T>(ref nint poignee, string nom, string empreinte, bool avecVerrouPci, Func<nint, T?> lire) where T : struct
    {
        if (!Installe() || !ProcesseurAmd()) return null;
        lock (Verrou)
        {
            Mutex? pci = null;
            try
            {
                if (avecVerrouPci)
                {
                    pci = new Mutex(false, @"Global\Access_PCI");
                    if (!PawnIoSuperIo.Prendre(pci, DelaiDuVerrou)) { pci.Dispose(); pci = null; return null; }
                }
                if (poignee == 0)
                {
                    if (PawnIoIntel.Module(nom, empreinte) is not { } module) return null;
                    if (PawnIoIntel.pawnio_open(out var h) != 0) return null;
                    if (PawnIoIntel.pawnio_load(h, module, (nuint)module.Length) != 0) { PawnIoIntel.pawnio_close(h); return null; }
                    poignee = h;
                }
                var lu = lire(poignee);
                if (lu is null) Fermer(ref poignee);
                return lu;
            }
            catch (Exception) { Fermer(ref poignee); return null; }
            finally
            {
                if (pci is not null) { pci.ReleaseMutex(); pci.Dispose(); }
            }
        }
    }

    private static void Fermer(ref nint poignee)
    {
        if (poignee == 0) return;
        try { PawnIoIntel.pawnio_close(poignee); } catch (Exception) { /* fermeture au mieux : la poignée est oubliée */ }
        poignee = 0;
    }
}
