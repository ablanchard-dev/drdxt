using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using DrDxt.Core;
using DrDxt.Reconciler.Manifest;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// DroitsDesServices : « Corriger les droits » et son Annuler.
//
// Un service qui tourne en SYSTEM depuis un fichier ou un dossier que le compte peut modifier (le service de Call of Duty
// sous ProgramData, dont le dossier appartient au compte ; un service Epic sous Program Files dont le fichier porte un
// droit d'écriture) laisse tout programme lancé sans droits remplacer le binaire, qui tourne ensuite en SYSTEM. La
// correction rend le fichier et son dossier aux administrateurs, comme sous Program Files.
//
// Ce que l'App envoie : le nom du service, rien d'autre. Le chemin est relu ici dans le registre des services ; un chemin
// ou un SDDL venus du fil donneraient l'écriture à n'importe qui sur n'importe quel binaire SYSTEM.
//
// Refusés sans rien écrire : service absent, compte autre que LocalSystem, pilote ou chemin qui n'est pas un .exe complet
// (variable d'environnement, chemin relatif ou réseau), binaire absent, binaire sous Windows, sous les profils ou dans le
// dossier de DrDXT, binaire posé à même un dossier partagé (ProgramData, Program Files : leur DACL se propagerait à tout ce
// qu'ils contiennent), lien symbolique, jonction ou lien dur sur le chemin. Déjà protégé : réussite sans écriture.
//
// L'origine (propriétaire et DACL du fichier et du dossier, en SDDL) est gardée avant toute écriture dans un fichier de
// confiance du helper, sous %ProgramData%\DrDxt, un par service, et jamais écrasée : une seconde correction garde la vraie.
// Le fichier et le dossier sont tenus par handle du contrôle à l'écriture, sans partage de suppression : ce qui est écrit est
// ce qui a été contrôlé. La DACL du dossier se pose sans propagation automatique (handle ouvert en MAXIMUM_ALLOWED), et
// l'héritage est rendu aux enfants par une descente qui ouvre chacun sans suivre de point de reparse : un lien planté par le
// compte dans ce dossier (jonction vers System32, lien symbolique) ne fait jamais réécrire sa cible. La correction qui en
// trouve un est refusée et l'origine rendue ; l'annulation le laisse intact. Un dossier au-dessus que le compte peut encore modifier laisse renommer le dossier corrigé : la
// correction ne fermerait rien, l'origine est rendue tout de suite et la raison dite. Les dossiers au-dessus ne sont jamais
// touchés.
//
// La désinstallation de DrDXT laisse la correction en place : c'est une correction de sécurité, pas un réglage de jeu.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class DroitsDesServices
{
    /// <summary>Ce que le registre des services dit d'un service : <c>ImagePath</c> brut (non développé) et
    /// <c>ObjectName</c>, le compte sous lequel il tourne.</summary>
    public sealed record ServiceLu(string? ImagePath, string? ObjectName);

    public const string NomDuDossier = "droits-des-services";
    private const string Prefixe = "service-", Extension = ".json";
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private static readonly SecurityIdentifier[] Confiance =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(TrustedInstaller),
    ];

    private readonly Func<string, ServiceLu?> _lire;
    private readonly string _racine;
    private readonly IReadOnlyList<string> _interdits;
    private readonly IReadOnlyList<string> _partages;
    private readonly Func<string, byte[], Result> _ecrireFiable;
    private readonly Func<string, Result> _fiable;
    private readonly string? _borne;

    /// <param name="interdits">Un binaire sous l'un d'eux n'est jamais touché (Windows, les profils, DrDXT).</param>
    /// <param name="partages">Un binaire posé à même l'un d'eux n'est jamais touché : réécrire leur DACL changerait tout ce
    /// qu'ils contiennent.</param>
    /// <param name="borne">Pour les tests seulement : la remontée des dossiers au-dessus s'arrête là (CheminProtege).</param>
    public DroitsDesServices(Func<string, ServiceLu?> lire, string racine, IReadOnlyList<string> interdits,
        IReadOnlyList<string> partages, Func<string, byte[], Result> ecrireFiable, Func<string, Result> fiable, string? borne = null)
        => (_lire, _racine, _interdits, _partages, _ecrireFiable, _fiable, _borne)
            = (lire, racine, interdits.Where(d => d.Length > 0).ToList(), partages.Where(d => d.Length > 0).ToList(), ecrireFiable, fiable, borne);

    public static DroitsDesServices ParDefaut()
    {
        static string D(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        // Les profils : le dossier qui contient celui du compte. Un service sous un profil a toujours un dossier au-dessus
        // que le compte possède ; le corriger ne fermerait rien.
        var profils = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(D(Environment.SpecialFolder.UserProfile))) ?? "";
        return new(LireDuRegistre, Racine(ProgramDataManifestStorage.DefaultRoot()),
            [D(Environment.SpecialFolder.Windows), profils, AppContext.BaseDirectory],
            [D(Environment.SpecialFolder.ProgramFiles), D(Environment.SpecialFolder.ProgramFilesX86),
             D(Environment.SpecialFolder.CommonApplicationData), D(Environment.SpecialFolder.CommonProgramFiles),
             D(Environment.SpecialFolder.CommonProgramFilesX86), profils],
            (chemin, octets) => TrustedManifestRoot.WriteTrustedFile(chemin, octets), TrustedManifestRoot.EnsureTrustedFile);
    }

    /// <summary>Le dossier des origines gardées sous une racine de données.</summary>
    public static string Racine(string racineDesDonnees) => Path.Combine(racineDesDonnees, NomDuDossier);

    public string CheminDeLOrigine(string nom) => Path.Combine(_racine, Prefixe + nom + Extension);

    /// <summary>Les services dont une origine est gardée : ceux que DrDXT a corrigés et pas rendus.</summary>
    public static IReadOnlyList<string> ServicesCorriges(string racine)
    {
        try
        {
            return Directory.Exists(racine)
                ? Directory.GetFiles(racine, Prefixe + "*" + Extension).Select(Path.GetFileName)
                    .Select(f => f![Prefixe.Length..^Extension.Length]).Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static ServiceLu? LireDuRegistre(string nom)
    {
        using var cle = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + nom);
        return cle is null ? null
            : new(cle.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, cle.GetValue("ObjectName") as string);
    }

    private sealed record Origine(string Service, string Exe, uint Volume, ulong Index, string Fichier, string Dossier);

    public Result Corriger(string nom)
    {
        try
        {
            var chemin = Binaire(nom, exigerSysteme: true);
            if (!chemin.IsOk) return Result.Fail(chemin.Error!.Value.Code, chemin.Error.Value.Message);
            var exe = chemin.Value!;

            var raison = CheminProtege.Verifier(exe, _borne);
            if (raison is null) return Result.Ok();
            if (CheminProtege.EstIllisible(raison))
                return Result.Fail(ErrorCodes.HelperOpFailed, $"{exe}: {raison}. Nothing was written.");

            var fichierOrigine = CheminDeLOrigine(nom);
            Origine? gardee = null;
            if (File.Exists(fichierOrigine))
            {
                var relue = Relire(nom, fichierOrigine);
                if (!relue.IsOk) return Result.Fail(relue.Error!.Value.Code, relue.Error.Value.Message);
                if (!string.Equals(relue.Value!.Exe, exe, StringComparison.OrdinalIgnoreCase))
                    return Result.Fail(ErrorCodes.NotAllowlisted,
                        $"An origin is kept for another path of {nom} ({relue.Value.Exe}): undo it first. Nothing was written.");
                gardee = relue.Value;
            }

            var tenus = Tenir(exe);
            if (!tenus.IsOk) return Result.Fail(tenus.Error!.Value.Code, tenus.Error.Value.Message);
            using var t = tenus.Value!;
            if (gardee is not null && (gardee.Volume, gardee.Index) != (t.Volume, t.Index))
                return Result.Fail(ErrorCodes.NotAllowlisted, $"The origin kept for {nom} belongs to another file: nothing was written.");

            var avant = (Fichier: Lire(t.Fichier), Dossier: Lire(t.Dossier));
            if (gardee is null)
            {
                var garder = _ecrireFiable(fichierOrigine, JsonSerializer.SerializeToUtf8Bytes(
                    new Origine(nom, exe, t.Volume, t.Index, avant.Fichier, avant.Dossier)));
                if (!garder.IsOk)
                    return Result.Fail(garder.Error!.Value.Code, $"The original permissions could not be kept: nothing was written. {garder.Error.Value.Message}");
            }

            try
            {
                // Le fichier d'abord : protégée, sa DACL ne bouge plus quand la descente rend l'héritage du dossier.
                Remettre(t.Fichier, Corrigee(avant.Fichier, conteneur: false));
                Remettre(t.Dossier, Corrigee(avant.Dossier, conteneur: true));
                var incomplet = false;
                RendreLHeritage(Path.GetDirectoryName(exe)!, refuserLesLiens: true, ref incomplet);
            }
            catch (LienDansLeDossier lien)
            {
                return Defaire(t, exe, avant, gardee is null ? fichierOrigine : null,
                    $"{lien.Chemin} is a link (junction, symbolic or hard link) inside the service folder, which a SYSTEM service should not have");
            }
            catch (Exception ex) when (ex is Win32Exception or ArgumentException or IOException or UnauthorizedAccessException)
            {
                return Defaire(t, exe, avant, gardee is null ? fichierOrigine : null, $"The permissions could not be written ({ex.Message})");
            }

            var reste = CheminProtege.Verifier(exe, _borne);
            return reste is null ? Result.Ok() : Defaire(t, exe, avant, gardee is null ? fichierOrigine : null, reste);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or JsonException)
        {
            return Result.Fail(ErrorCodes.HelperOpFailed, $"The permissions of service {nom} could not be fixed: {ex.Message}");
        }
    }

    /// <summary>Rend l'état qui précédait la correction des droits (<see cref="RemettreLOrigine"/>), et retire l'origine
    /// qu'elle avait gardée.</summary>
    private static Result Defaire(Tenus t, string exe, (string Fichier, string Dossier) avant, string? origineNouvelle, string raison)
    {
        try
        {
            if (!RemettreLOrigine(t, exe, avant.Fichier, avant.Dossier))
                return Result.Fail(ErrorCodes.TweakPartiallyApplied,
                    $"{raison}. Some files of the folder did not get their original permissions back; the origin stays kept.");
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or IOException or UnauthorizedAccessException)
        {
            // L'origine reste gardée : Annuler pourra la rendre.
            return Result.Fail(ErrorCodes.TweakPartiallyApplied, $"{raison}. The original permissions could not be put back ({ex.Message}).");
        }
        if (origineNouvelle is not null) File.Delete(origineNouvelle);
        return Result.Fail(ErrorCodes.HelperOpFailed, $"{raison}. The original permissions are back.");
    }

    /// <summary>Le dossier, puis l'héritage de ce qu'il contient (sans jamais suivre un lien, laissé intact), puis le fichier, qui
    /// reprend alors l'héritage de son dossier rendu. Faux quand un enfant n'a pas pu être rendu.</summary>
    private static bool RemettreLOrigine(Tenus t, string exe, string fichier, string dossier)
    {
        Remettre(t.Dossier, dossier);
        var incomplet = false;
        RendreLHeritage(Path.GetDirectoryName(exe)!, refuserLesLiens: false, ref incomplet);
        Remettre(t.Fichier, fichier);
        return !incomplet;
    }

    public Result Rendre(string nom)
    {
        try
        {
            var fichierOrigine = CheminDeLOrigine(nom);
            if (!File.Exists(fichierOrigine))
                return Result.Fail(ErrorCodes.HelperOpFailed, $"No original permissions are kept for service {nom}: nothing was written.");
            var relue = Relire(nom, fichierOrigine);
            if (!relue.IsOk) return Result.Fail(relue.Error!.Value.Code, relue.Error.Value.Message);
            var origine = relue.Value!;

            var chemin = Binaire(nom, exigerSysteme: false);
            if (!chemin.IsOk) return Result.Fail(chemin.Error!.Value.Code, chemin.Error.Value.Message);
            if (!string.Equals(chemin.Value, origine.Exe, StringComparison.OrdinalIgnoreCase))
                return Result.Fail(ErrorCodes.NotAllowlisted, $"Service {nom} no longer runs {origine.Exe}: nothing was written.");

            var tenus = Tenir(origine.Exe);
            if (!tenus.IsOk) return Result.Fail(tenus.Error!.Value.Code, tenus.Error.Value.Message);
            using var t = tenus.Value!;
            // Remplacé depuis la correction, ce n'est plus le fichier dont l'origine a été lue.
            if ((origine.Volume, origine.Index) != (t.Volume, t.Index))
                return Result.Fail(ErrorCodes.NotAllowlisted, $"{origine.Exe} was replaced since the fix: nothing was written.");

            if (!RemettreLOrigine(t, origine.Exe, origine.Fichier, origine.Dossier))
                return Result.Fail(ErrorCodes.TweakPartiallyApplied,
                    $"Some files of the folder of {nom} did not get their original permissions back; the origin stays kept.");
            File.Delete(fichierOrigine);
            return Result.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or JsonException)
        {
            return Result.Fail(ErrorCodes.HelperOpFailed, $"The original permissions of service {nom} could not be put back: {ex.Message}");
        }
    }

    private Result<Origine> Relire(string nom, string chemin)
    {
        if (_fiable(chemin) is { IsOk: false } refus)
            return Result<Origine>.Fail(ErrorCodes.SignatureInvalid, $"The kept origin of {nom} is not trusted: nothing was written. {refus.Error!.Value.Message}");
        var o = JsonSerializer.Deserialize<Origine>(File.ReadAllBytes(chemin));
        return o is not null && string.Equals(o.Service, nom, StringComparison.OrdinalIgnoreCase) && o.Exe.Length > 0
            ? Result<Origine>.Ok(o)
            : Result<Origine>.Fail(ErrorCodes.SignatureInvalid, $"The kept origin of {nom} is not one this helper wrote: nothing was written.");
    }

    /// <summary>Le binaire du service, relu dans le registre et jugé ; un refus dit pourquoi.</summary>
    private Result<string> Binaire(string nom, bool exigerSysteme)
    {
        static Result<string> Refus(string m) => Result<string>.Fail(ErrorCodes.NotAllowlisted, m + " Nothing was written.");

        if (_lire(nom) is not { } s) return Result<string>.Fail(ErrorCodes.HelperOpFailed, $"Service {nom} is not installed. Nothing was written.");
        if (exigerSysteme && !string.Equals(s.ObjectName, "LocalSystem", StringComparison.OrdinalIgnoreCase))
            return Refus($"Service {nom} does not run as LocalSystem.");
        // Développer une variable lirait l'environnement du compte, que ce processus élevé hérite : elle désignerait n'importe quel fichier.
        if (string.IsNullOrWhiteSpace(s.ImagePath) || s.ImagePath.Contains('%'))
            return Refus($"Service {nom} has no plain executable path.");
        var exe = CheminProtege.BinaireDuService(s.ImagePath);
        if (exe is null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || exe.Length < 3 || exe[1] != ':'
            || !Path.IsPathFullyQualified(exe) || !string.Equals(Path.GetFullPath(exe), exe, StringComparison.OrdinalIgnoreCase))
            return Refus($"Service {nom} does not start a local executable by its full long path (driver, relative, network or 8.3 short path).");
        if (!File.Exists(exe)) return Result<string>.Fail(ErrorCodes.HelperOpFailed, $"{exe} does not exist. Nothing was written.");
        if (_interdits.FirstOrDefault(d => Sous(exe, d)) is { } interdit)
            return Refus($"{exe} is under {interdit}, which DrDXT never changes.");
        var dossier = Path.GetDirectoryName(exe)!;
        if (string.Equals(Path.GetPathRoot(dossier), dossier, StringComparison.OrdinalIgnoreCase) || _partages.Any(d => Meme(dossier, d)))
            return Refus($"{exe} sits directly in {dossier}, whose permissions every program there shares.");
        return Result<string>.Ok(exe);
    }

    private static bool Meme(string a, string b)
        => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static bool Sous(string chemin, string dossier)
        => chemin.StartsWith(Path.TrimEndingDirectorySeparator(dossier) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // ── La correction ──────────────────────────────────────────────────────────────────

    /// <summary>Propriétaire Administrateurs (un propriétaire déjà de confiance reste : le rendre demanderait un privilège),
    /// DACL protégée : SYSTEM et Administrateurs en contrôle total, TrustedInstaller s'il y était, Utilisateurs en lecture et
    /// exécution. Héritable pour un dossier. Rien d'autre.</summary>
    private static string Corrigee(string avant, bool conteneur)
    {
        var sd = new RawSecurityDescriptor(avant);
        var proprio = sd.Owner is { } o && Confiance.Contains(o) ? o.Value : "BA";
        var avecTi = sd.DiscretionaryAcl?.OfType<CommonAce>()
            .Any(a => a.AceQualifier == AceQualifier.AccessAllowed && a.SecurityIdentifier.Value == TrustedInstaller) == true;
        var h = conteneur ? "OICI" : "";
        return $"O:{proprio}D:P(A;{h};FA;;;SY)(A;{h};FA;;;BA)" + (avecTi ? $"(A;{h};FA;;;{TrustedInstaller})" : "") + $"(A;{h};0x1200a9;;;BU)";
    }

    /// <summary>Repose un SDDL ; le propriétaire ne s'écrit que s'il change.</summary>
    private static void Remettre(SafeFileHandle h, string sddl)
        => Poser(h, sddl, ecrireProprio: new RawSecurityDescriptor(sddl).Owner?.Value != new RawSecurityDescriptor(Lire(h)).Owner?.Value);

    private static void Poser(SafeFileHandle h, string sddl, bool ecrireProprio)
    {
        var sd = new RawSecurityDescriptor(sddl);
        byte[]? proprio = null;
        if (ecrireProprio && sd.Owner is { } o) { proprio = new byte[o.BinaryLength]; o.GetBinaryForm(proprio, 0); }
        byte[]? dacl = null;
        if (sd.DiscretionaryAcl is { } a) { dacl = new byte[a.BinaryLength]; a.GetBinaryForm(dacl, 0); }
        // Non protégée, la DACL reprend l'héritage de son parent : ses entrées héritées sont recalculées par Windows.
        var info = DaclInfo | (sd.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected) ? ProtegeeInfo : NonProtegeeInfo)
                   | (proprio is not null ? ProprioInfo : 0);
        var err = SetSecurityInfo(h, FichierObjet, info, proprio, IntPtr.Zero, dacl, IntPtr.Zero);
        if (err != 0) throw new Win32Exception((int)err);
    }

    /// <summary>Pour les tests : la pose sur un dossier seul, telle que la correction la fait, sans la descente.</summary>
    internal static void PoserSurLeDossierSeul(string dossier, string sddl)
    {
        using var h = OuvrirUnDossier(dossier);
        Poser(h, sddl, ecrireProprio: true);
    }

    private sealed class LienDansLeDossier(string chemin) : Exception(chemin)
    {
        public string Chemin { get; } = chemin;
    }

    /// <summary>Rend à chaque enfant ordinaire d'un dossier ses entrées héritées, recalculées depuis son parent : sa DACL reposée
    /// non protégée, ses entrées explicites gardées (Windows remplace les héritées). Une DACL protégée n'hérite de rien : elle
    /// reste, et la descente ne va pas dessous. Chaque enfant est ouvert sans suivre de point de reparse et relu par son handle ;
    /// un lien (point de reparse, ou fichier à plusieurs liens durs) n'est jamais ouvert au-delà ni descendu.</summary>
    /// <param name="refuserLesLiens">Vrai pour la correction : un lien arrête tout (<see cref="LienDansLeDossier"/>). Faux
    /// pour rendre l'origine : le lien est laissé intact, la descente continue, et ce qui n'a pas pu être rendu se note.</param>
    private static void RendreLHeritage(string dossier, bool refuserLesLiens, ref bool incomplet)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };
        foreach (var entree in new DirectoryInfo(dossier).EnumerateFileSystemInfos("*", options))
        {
            try
            {
                if (entree.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (refuserLesLiens) throw new LienDansLeDossier(entree.FullName);
                    continue;
                }
                var estDossier = entree is DirectoryInfo;
                using var h = estDossier ? OuvrirUnDossier(entree.FullName) : OuvrirPourLesDroits(entree.FullName, OpenReparsePoint);
                if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeErrorMessage());
                if (!GetFileInformationByHandle(h, out var info)) throw new Win32Exception(Marshal.GetLastPInvokeErrorMessage());
                // Relu sur le handle : un enfant échangé contre un lien entre l'énumération et l'ouverture se voit ici.
                var lien = (info.Attributs & (uint)FileAttributes.ReparsePoint) != 0
                           || ((info.Attributs & (uint)FileAttributes.Directory) != 0) != estDossier
                           || (!estDossier && info.Liens != 1)
                           || !Meme(CheminFinal(h), entree.FullName);
                if (lien)
                {
                    if (refuserLesLiens) throw new LienDansLeDossier(entree.FullName);
                    continue;
                }
                var sddl = Lire(h);
                if (new RawSecurityDescriptor(sddl).ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)) continue;
                Poser(h, sddl, ecrireProprio: false);
                if (estDossier) RendreLHeritage(entree.FullName, refuserLesLiens, ref incomplet);
            }
            catch (Exception ex) when (!refuserLesLiens && ex is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
            {
                incomplet = true;
            }
        }
    }

    private static string Lire(SafeFileHandle h)
    {
        var err = GetSecurityInfo(h, FichierObjet, ProprioInfo | DaclInfo, out _, out _, out _, out _, out var sd);
        if (err != 0) throw new Win32Exception((int)err);
        try
        {
            var octets = new byte[GetSecurityDescriptorLength(sd)];
            Marshal.Copy(sd, octets, 0, octets.Length);
            return new RawSecurityDescriptor(octets, 0).GetSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        }
        finally { LocalFree(sd); }
    }

    // ── Ce qui est tenu ─────────────────────────────────────────────────────────────────

    private sealed class Tenus(SafeFileHandle fichier, SafeFileHandle dossier, uint volume, ulong index) : IDisposable
    {
        public SafeFileHandle Fichier => fichier;
        public SafeFileHandle Dossier => dossier;
        public uint Volume => volume;
        public ulong Index => index;
        public void Dispose() { fichier.Dispose(); dossier.Dispose(); }
    }

    /// <summary>Le dossier puis le fichier, ouverts sans suivre un point de reparse et sans partage de suppression (ni
    /// renommés ni remplacés tant qu'ils sont tenus), relus par leur handle : ni lien, ni jonction sur le chemin (le chemin
    /// final est celui du registre), un seul lien dur.</summary>
    private static Result<Tenus> Tenir(string exe)
    {
        var dossier = Path.GetDirectoryName(exe)!;
        var d = OuvrirUnDossier(dossier);
        if (d.IsInvalid) return Echec(d, null, $"{dossier} could not be opened to change its permissions ({Marshal.GetLastPInvokeErrorMessage()}).");
        var f = OuvrirPourLesDroits(exe, OpenReparsePoint);
        if (f.IsInvalid) return Echec(d, f, $"{exe} could not be opened to change its permissions ({Marshal.GetLastPInvokeErrorMessage()}).");
        if (!GetFileInformationByHandle(d, out var infoD) || !GetFileInformationByHandle(f, out var infoF))
            return Echec(d, f, $"{exe} could not be read ({Marshal.GetLastPInvokeErrorMessage()}).");
        if (((infoD.Attributs | infoF.Attributs) & (uint)FileAttributes.ReparsePoint) != 0 || (infoD.Attributs & (uint)FileAttributes.Directory) == 0
            || !Meme(CheminFinal(d), dossier) || !Meme(CheminFinal(f), exe))
            return Echec(d, f, $"{exe} is reached through a link, a junction or an 8.3 short name: nothing was written.");
        if (infoF.Liens != 1)
            return Echec(d, f, $"{exe} has other hard links: nothing was written.");
        return Result<Tenus>.Ok(new Tenus(f, d, infoF.Volume, ((ulong)infoF.IndexHaut << 32) | infoF.IndexBas));

        static Result<Tenus> Echec(SafeFileHandle d, SafeFileHandle? f, string m)
        {
            d.Dispose();
            f?.Dispose();
            return Result<Tenus>.Fail(ErrorCodes.HelperOpFailed, m);
        }
    }

    private static SafeFileHandle OuvrirPourLesDroits(string chemin, uint drapeaux)
        => CreateFileW(chemin, LireLesDroits | EcrireLaDacl | EcrireLeProprio, FileShare.Read | FileShare.Write, IntPtr.Zero,
            FileMode.Open, drapeaux, IntPtr.Zero);

    /// <summary>Un dossier ouvert en MAXIMUM_ALLOWED, sans suivre un point de reparse. Microsoft Learn, « SetSecurityInfo
    /// function » : « If the supplied handle was opened with an ACCESS_MASK value of MAXIMUM_ALLOWED, then the SetSecurityInfo
    /// function will not propagate ACEs to children. » Ouvert avec des droits nommés, la pose descendrait d'elle-même dans les
    /// enfants, liens compris ; ici, seule la descente de <see cref="RendreLHeritage"/> les touche.</summary>
    private static SafeFileHandle OuvrirUnDossier(string chemin)
        => CreateFileW(chemin, MaximumAutorise, FileShare.Read | FileShare.Write, IntPtr.Zero, FileMode.Open,
            BackupSemantics | OpenReparsePoint, IntPtr.Zero);

    /// <summary>Le chemin final d'un handle (liens résolus), sans le préfixe \\?\.</summary>
    private static string CheminFinal(SafeFileHandle h)
    {
        var tampon = new System.Text.StringBuilder(1024);
        var n = GetFinalPathNameByHandleW(h, tampon, (uint)tampon.Capacity, 0);
        if (n == 0 || n >= tampon.Capacity) throw new IOException(Marshal.GetLastPInvokeErrorMessage());
        var s = tampon.ToString();
        return Path.TrimEndingDirectorySeparator(s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + s[8..]
            : s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s);
    }

    private const uint LireLesDroits = 0x00020000, EcrireLaDacl = 0x00040000, EcrireLeProprio = 0x00080000, MaximumAutorise = 0x02000000,
        BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
    private const int FichierObjet = 1; // SE_FILE_OBJECT
    private const uint ProprioInfo = 0x1, DaclInfo = 0x4, ProtegeeInfo = 0x80000000, NonProtegeeInfo = 0x20000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct InfoFichier
    {
        public uint Attributs;
        public uint Creation1, Creation2, Acces1, Acces2, Ecriture1, Ecriture2;
        public uint Volume, TailleHaute, TailleBasse, Liens, IndexHaut, IndexBas;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string nom, uint acces, FileShare partage, IntPtr securite, FileMode creation,
        uint drapeaux, IntPtr modele);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out InfoFichier info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, System.Text.StringBuilder tampon, uint taille, uint drapeaux);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle h, int type, uint info, out IntPtr proprio, out IntPtr groupe,
        out IntPtr dacl, out IntPtr sacl, out IntPtr descripteur);

    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(SafeFileHandle h, int type, uint info, byte[]? proprio, IntPtr groupe, byte[]? dacl, IntPtr sacl);

    [DllImport("advapi32.dll")]
    private static extern int GetSecurityDescriptorLength(IntPtr descripteur);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr p);
}
