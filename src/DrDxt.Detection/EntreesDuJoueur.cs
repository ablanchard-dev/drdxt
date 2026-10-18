using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.Detection;

/// <summary>
/// Le joueur joue-t-il, là, maintenant ? Lu à la source, sans hook ni injection : l'instant de la
/// dernière entrée clavier/souris (<c>GetLastInputInfo</c>) et le numéro de paquet des manettes XInput, qui change
/// à chaque mouvement. Une seconde sans rien = une seconde d'attente (lobby, chargement, écran de mort). Le relevé
/// s'en sert pour ne compter que les minutes jouées. <c>null</c> = rien n'a pu être lu, ni pour ni contre.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EntreesDuJoueur
{
    /// <summary>Deux secondes : entre deux gestes au clavier en pleine partie il ne se passe jamais plus.</summary>
    public static readonly TimeSpan Recent = TimeSpan.FromSeconds(2);

    private readonly uint[] _paquets = new uint[4];

    public bool? ActiveRecemment()
    {
        bool? clavier = null;
        try
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (GetLastInputInfo(ref info))
                clavier = unchecked((uint)Environment.TickCount - info.dwTime) < Recent.TotalMilliseconds;
        }
        catch (Exception) { /* clavier reste null : non observé, ni pour ni contre */ }

        bool? manette = null;
        try
        {
            for (uint i = 0; i < _paquets.Length; i++)
            {
                if (XInputGetState(i, out var etat) != 0) continue;
                // Limite connue : le paquet bouge aussi sur un stick qui tremble au repos ; si des minutes d'attente
                // passent pour jouées, comparer les axes avec une zone morte plutôt que le numéro de paquet.
                var bouge = _paquets[i] != 0 && etat.dwPacketNumber != _paquets[i];
                _paquets[i] = etat.dwPacketNumber;
                manette = (manette ?? false) || bouge;
            }
        }
        catch (Exception) { /* xinput1_4 absent : seule la manette reste inconnue */ }

        return clavier is null && manette is null ? null : (clavier ?? false) || (manette ?? false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD { public ushort wButtons; public byte bLeftTrigger; public byte bRightTrigger; public short sThumbLX; public short sThumbLY; public short sThumbRX; public short sThumbRY; }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);
}
