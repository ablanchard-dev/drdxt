namespace DrDxt.Detection.Cpu;

/// <summary>PAWN1 (09/10/2026) : la température du processeur, par la première source qui répond : HWiNFO s'il publie,
/// sinon PawnIO s'il est installé. Chaque lecture porte le nom de sa source ; aucune des deux ⇒ null, et le docteur dit
/// « non lue », jamais « saine ».</summary>
public static class TemperatureProcesseur
{
    public static TemperatureHwinfo.Lecture? Lire() => TemperatureHwinfo.Lire() ?? TemperaturePawnIo.Lire();
}
