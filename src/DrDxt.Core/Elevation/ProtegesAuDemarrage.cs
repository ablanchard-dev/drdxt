namespace DrDxt.Core;

/// <summary>
/// Les programmes au démarrage que DrDXT ne coupe jamais : la sécurité de Windows, les antivirus et les contrôles parentaux.
/// Couper l'interface d'un antivirus ou un contrôle parental n'est pas un gain de confort, et un appelant compromis du helper
/// pouvait le faire. Partagé par la liste blanche du helper, le constat et les gestes du docteur. Reconnus par un fragment de
/// leur nom d'entrée (liste fermée) : un autre programme pris pour l'un d'eux reste simplement allumé.
/// </summary>
public static class ProtegesAuDemarrage
{
    private static readonly string[] Fragments =
    [
        "SecurityHealth", "Windows Defender", "Avast", "AVG", "Avira", "Bitdefender", "Kaspersky", "AVP", "ESET", "egui",
        "Norton", "McAfee", "Malwarebytes", "MBAM", "Sophos", "Trend Micro", "Webroot", "F-Secure", "Panda", "Comodo",
        "ZoneAlarm", "G DATA", "Family Safety", "Qustodio", "Net Nanny", "Kaspersky Safe Kids", "Bark",
    ];

    public static bool Est(string? nom)
        => nom is not null && Fragments.Any(f => nom.Contains(f, StringComparison.OrdinalIgnoreCase));
}
