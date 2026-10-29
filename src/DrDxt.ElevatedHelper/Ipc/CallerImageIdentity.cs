using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// Identité d'image de l'appelant : résout l'image de processus d'un pair qui se connecte en un
// couple (chemin, empreinte du contenu) pour l'auth durcie de l'appelant (« Authenticode appelant »).
//
// Chemin + SHA-256 de l'image : un binaire remplacé au même chemin ne passe pas. Tant que l'App n'est pas signée,
// l'empreinte tient lieu d'Authenticode (WinVerifyTrust WINTRUST_ACTION_GENERIC_VERIFY_V2 + épinglage du certificat
// d'éditeur viendra à la même couture) ; au moindre doute, refus.
// -----------------------------------------------------------------------

/// <summary>L'image résolue d'un pair qui se connecte : son chemin complet et le SHA-256 des
/// octets du fichier (null si l'empreinte est incalculable → identité improuvable).</summary>
public readonly record struct CallerImage(string Path, byte[]? Sha256);

/// <summary>Couture : relie un PID client à son <see cref="CallerImage"/> (ou null si le
/// processus, le chemin ou le fichier ne se résout pas). Les tests injectent un faux.</summary>
public interface ICallerImageIdentity
{
    CallerImage? Resolve(int pid);
}

/// <summary>Vrai résolveur : PID → chemin du module principal → SHA-256 des octets du fichier.
/// Tout échec (processus disparu, accès refusé, fichier illisible) remonte en null (chemin)
/// ou en empreinte null, deux cas que l'authentificateur traite comme un refus.</summary>
[SupportedOSPlatform("windows")]
public sealed class Win32CallerImageIdentity : ICallerImageIdentity
{
    public CallerImage? Resolve(int pid)
    {
        // Le chemin vient du noyau (QueryFullProcessImageNameW) : la liste des modules (le module principal du Process) vit dans
        // la mémoire de l'appelant, qui peut la réécrire.
        var path = PipeServerIdentity.TryGetProcessImagePath(pid); // processus terminé / accès refusé → null

        if (string.IsNullOrWhiteSpace(path))
            return null;

        byte[]? hash = TryHashFile(path);
        return new CallerImage(path, hash);
    }

    /// <summary>SHA-256 des octets du fichier, ou null s'il est illisible.</summary>
    public static byte[]? TryHashFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return SHA256.HashData(stream);
        }
        catch
        {
            return null; // illisible → l'appelant tient l'identité pour improuvable → refus
        }
    }
}
