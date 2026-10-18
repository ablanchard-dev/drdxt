using System.Runtime.Intrinsics.X86;
using System.Text;

namespace DrDxt.Detection.Cpu;

/// <summary>
/// Le fabricant et la signature du processeur, lus par l'instruction CPUID dans le processus de l'App : sans droits,
/// sans pilote, rien n'est écrit. Sert de portail aux lectures propres à AMD (<see cref="DrDxt.Core.GenerationAmd"/>).
/// </summary>
public static class Cpuid
{
    /// <summary>(fabricant, EAX de la fonction 1), ou (null, null) sans CPUID x86.</summary>
    public static (string? Fabricant, uint? Signature) Lire()
    {
        try
        {
            if (!X86Base.IsSupported) return (null, null);
            var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
            // Fonction 0 : les douze caractères du fabricant dans EBX, EDX, ECX, dans cet ordre.
            var octets = new byte[12];
            BitConverter.TryWriteBytes(octets.AsSpan(0, 4), ebx);
            BitConverter.TryWriteBytes(octets.AsSpan(4, 4), edx);
            BitConverter.TryWriteBytes(octets.AsSpan(8, 4), ecx);
            return (Encoding.ASCII.GetString(octets), (uint)X86Base.CpuId(1, 0).Eax);
        }
        catch (Exception) { return (null, null); }
    }
}
