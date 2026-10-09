namespace DrDxt.Core;

/// <summary>
/// Les mots affiches pour les cles d'un constat. La cle reste celle du journal (deux photos se
/// comparent par Domaine + Nom) : seul l'affichage traduit. 17/09/2026 : descendu de l'App pour que
/// le rapport de diagnostic dise les memes mots que l'ecran.
/// </summary>
public static class RepairLibelles
{
    // Une cle inconnue s'affiche telle quelle plutot que de disparaitre.
    public static string Domaine(string cle) => cle switch
    {
        "STABILITE"  => "Stabilité",
        "POWER"      => "Alimentation",
        "CPU"        => "Processeur",
        "SERVICES"   => "Services",
        "TACHES"     => "Tâches planifiées",
        "MMCSS"      => "Priorité des jeux et du son",
        "STOCKAGE"   => "Stockage",
        "DISPLAY"    => "Écran",
        "GPU DRIVER" => "Pilote graphique",
        "GPU"        => "Carte graphique",
        "PAGEFILE"   => "Fichier d'échange",
        "THERMIQUE"  => "Température",
        "BCD"        => "Démarrage de Windows",
        "LATENCE"    => "Latence",
        "RESEAU"     => "Réseau",
        "PERIPHERIQUES" => "Périphériques",
        "USB" => "Ports USB",
        "DEFENDER" => "Microsoft Defender",
        "WINDOWS UPDATE" => "Mises à jour Windows",
        "DEMARRAGE" => "Vitesse de démarrage",
        _            => cle,
    };

    // Le nom est une cle du journal, ecrite sans accents ; seul l'affichage les remet. Un nom
    // inconnu (cle BCD, service, disque) s'affiche tel quel.
    // 24/09/2026 : une majuscule initiale partout (vu sur les photos du scan reel : des titres en
    // minuscule et un nom d'element BCD brut, « bootstatuspolicy », lisaient comme un journal de
    // debogage), et les elements BCD traduits en ce qu'ils font.
    public static string Nom(string cle) => Majuscule(NomBrut(cle));

    private static string Majuscule(string s)
        => string.IsNullOrEmpty(s) || !char.IsLower(s[0]) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string NomBrut(string cle) => cle switch
    {
        "bootstatuspolicy"        => "écran de récupération au démarrage",
        // 29/09/2026 : « magasin de composants » parlait à un technicien (troisième critique).
        "magasin de composants"   => "fichiers de réparation de Windows",
        "useplatformclock"        => "horloge de plateforme imposée",
        "useplatformtick"         => "minuterie de plateforme imposée",
        "disabledynamictick"      => "minuterie dynamique coupée",
        "tscsyncpolicy"           => "synchronisation des horloges imposée",
        "hypervisorlaunchtype"    => "hyperviseur au démarrage",
        "arrets brutaux"          => "arrêts brutaux",
        "incident noyau conserve" => "incident noyau conservé",
        "frequence effective"     => "fréquence effective",
        // 24/09/2026 : lus seuls dans leur domaine, ces noms ne disaient pas ce qu'ils mesurent
        // (« Fréquence : 200 Hz » sous la carte graphique, « Taille / pic » sous la mémoire).
        "frequence"               => "fréquence de l'écran principal",
        "gestion"                 => "fichier d'échange, gestion",
        "taille / pic"            => "fichier d'échange, taille et pic d'usage",
        "emplacement"             => "fichier d'échange, emplacement",
        "test memoire Windows"    => "test mémoire Windows",
        "echecs"                  => "échecs",
        "ecran principal"         => "écran principal",
        "carte mere / firmware"   => "carte mère / firmware",
        "temperature du GPU"      => "température du GPU",
        "temperature du processeur" => "température du processeur",
        "pertes a l'interface"    => "pertes à l'interface",
        "creation de vidage"      => "création de vidage",
        // 24/09/2026 : noms de registre et d'alimentation vus bruts sur la carte processeur.
        "SystemResponsiveness"    => "part du processeur réservée à l'arrière-plan",
        "Games\\Priority"         => "priorité processeur des jeux",
        "GPU Priority"            => "priorité GPU des jeux (champ inutilisé par Windows)",
        "SFIO Priority"           => "priorité disque des jeux (champ inutilisé par Windows)",
        "Clock Rate"              => "cadence multimédia (sans effet depuis Windows 7)",
        "preference energie/perf" => "préférence énergie / performance",
        "mode de boost"           => "mode de boost du processeur",
        "parking : coeurs min"    => "parking : cœurs actifs au minimum",
        "parking : coeurs max"    => "parking : cœurs actifs au maximum",
        // 25/09/2026 : vu en entier sur la photo de la carte graphique, un nom de registre brut.
        "Valeur de registre NVTweak LowLatencyMode" => "faible latence NVIDIA posée dans le registre (sans effet prouvé)",
        _                         => cle,
    };
}
