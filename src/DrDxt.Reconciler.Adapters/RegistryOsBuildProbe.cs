using System.Globalization;
using DrDxt.Core;
using DrDxt.Reconciler;
using Microsoft.Win32;

namespace DrDxt.Reconciler.Adapters;

// RegistryOsBuildProbe : IOsBuildProbe sur HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion, CurrentBuild (chaîne) +
// UBR (dword) → « {CurrentBuild}.{UBR} » (par exemple « 22631.3447 ») ; un changement depuis l'apply est le signal
// WindowsReset principal. La lecture brute est derrière IRawOsVersionReader (la mise en forme testée avec un faux) ;
// Win32OsVersionReader, seul bord non testé, lit deux valeurs et emballe toute exception dans un Result.Fail.

/// <summary>Lecture brute interne des deux valeurs de CurrentVersion. Rend les valeurs telles que
/// lues (CurrentBuild peut être null/absent, UBR aussi) ; une vraie erreur de registre → Fail. Ce
/// n'est pas ici qu'on met en forme ou valide : c'est la sonde, qui reste ainsi testable sans registre.</summary>
internal interface IRawOsVersionReader
{
    Result<(string? CurrentBuild, uint? Ubr)> Read();
}

/// <summary>Le vrai lecteur mince du registre (bord non testé). Ouvre
/// HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion en lecture seule et lit CurrentBuild
/// + UBR. Toute exception à la frontière → Fail(RegistryFail), jamais levée.</summary>
internal sealed class Win32OsVersionReader : IRawOsVersionReader
{
    private const string CurrentVersionPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public Result<(string? CurrentBuild, uint? Ubr)> Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionPath, writable: false);
            if (key is null)
                return Result<(string?, uint?)>.Fail(
                    ErrorCodes.RegistryFail, $"Registry key not found: HKLM\\{CurrentVersionPath}.");

            var currentBuild = key.GetValue("CurrentBuild") as string;

            uint? ubr = key.GetValue("UBR") switch
            {
                int i => unchecked((uint)i),
                uint u => u,
                long l => unchecked((uint)l),
                _ => null,
            };

            return Result<(string?, uint?)>.Ok((currentBuild, ubr));
        }
        catch (Exception ex)
        {
            return Result<(string?, uint?)>.Fail(ErrorCodes.RegistryFail, ex.Message);
        }
    }
}

/// <summary>Lit le jeton de mise à jour de fonctionnalité Windows « {CurrentBuild}.{UBR} ».</summary>
public sealed class RegistryOsBuildProbe : IOsBuildProbe
{
    private readonly IRawOsVersionReader _reader;

    public RegistryOsBuildProbe() : this(new Win32OsVersionReader()) { }

    internal RegistryOsBuildProbe(IRawOsVersionReader reader) => _reader = reader;

    public Result<string> ReadCurrentBuild()
    {
        var read = _reader.Read();
        if (!read.IsOk)
            return Result<string>.Fail(read.Error!.Value.Code, read.Error.Value.Message);

        var (currentBuild, ubr) = read.Value;

        if (string.IsNullOrWhiteSpace(currentBuild))
            return Result<string>.Fail(ErrorCodes.RegistryFail, "CurrentBuild is missing.");
        if (ubr is null)
            return Result<string>.Fail(ErrorCodes.RegistryFail, "UBR is missing.");

        return Result<string>.Ok(
            $"{currentBuild.Trim()}.{ubr.Value.ToString(CultureInfo.InvariantCulture)}");
    }
}
