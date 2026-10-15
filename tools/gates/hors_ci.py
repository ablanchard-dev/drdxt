# -*- coding: utf-8 -*-
"""Quels tests sortent de la CI, et est-ce toujours volontaire ?

18/09/2026. `ci.yml:37` lance toute la solution avec
`--filter "Category!=Machine"`. C'est justifie : ces tests lisent la VRAIE
machine (journal Systeme, WMI du BIOS, fenetres de la session, pilote NVIDIA) et
un runner GitHub n'en a aucune.

Mais la consequence merite d'etre dite en face : **poser
`[Trait("Category", "Machine")]` sur un test le retire de la CI, entierement et
sans bruit.** C'est la sortie de secours la moins chere du depot -- un test
genant devient invisible en une ligne, et rien ne le signale. Exactement la
classe de defaut que cette boucle traque : l'echec qui ne leve rien.

Mesure du 18/09, sans defaut trouve : 12 attributs sur 7 fichiers, tous
plausiblement lies a la machine. La discipline tenait a la main.

Ce crible ne juge pas : il fige l'INVENTAIRE. Un test qui quitte la CI devient
un geste conscient -- il faut venir ecrire ici pourquoi. Un test qui la rejoint
aussi.

Lecture seule. Ne compile rien, ne lance aucun test.

    python tools/gates/hors_ci.py            # 0 si l'inventaire tient, 1 sinon
    python tools/gates/hors_ci.py --autotest # se verifie lui-meme
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

RACINE = Path(__file__).resolve().parents[2]
TESTS = RACINE / "tests"

MARQUE = re.compile(r'Trait\(\s*"Category"\s*,\s*"Machine"\s*\)')

# Inventaire releve et fige le 18/09/2026 : chemin relatif a tests/ -> nombre d'attributs.
# La raison n'est pas decorative : c'est elle qu'on relira le jour ou un test
# disparaitra de la CI sans qu'on sache pourquoi.
ATTENDU = {
    "DrDxt.App.Tests/DisplayInfoSurLeVraiSystemeTests.cs": (1, "lit les ecrans de la vraie session"),
    "DrDxt.GpuNative.Tests/VentilateurNvmlSurLaVraieCarteTests.cs": (1, "lit le ventilateur de la vraie carte NVIDIA"),
    "DrDxt.ElevatedHelper.Tests/SessionUtilisateurSurLaVraieRucheTests.cs": (1, "ecrit puis rend une valeur de la vraie session Windows"),
    "DrDxt.ElevatedHelper.Tests/StartupApprovalMachineTests.cs": (1, "coupe, relit, retablit puis efface une entree de demarrage d'essai"),
    "DrDxt.Detection.Tests/TemperaturePawnIoTests.cs": (5, "lit la temperature, les limites de puissance, leur copie MMIO et l'etat du processeur par PawnIO quand il est installe, et un ventilateur CPU qui tourne pendant une seconde relevee"),
    "DrDxt.GpuNative.Tests/PawnIoSuperIoTests.cs": (1, "lit les ventilateurs de la vraie carte MSI (puce NCT6687) par PawnIO, session elevee"),
    "DrDxt.ElevatedHelper.Tests/AccelerationSourisSurLaVraieSessionTests.cs": (1, "ecrit puis rend l'acceleration du pointeur de la vraie session Windows"),
    "DrDxt.Detection.Tests/Network/DiagnosticDeRouteSurLaVraiePartieTests.cs": (1, "suit une vraie partie de Call of Duty et le vrai reseau"),
    "DrDxt.App.Tests/GameLocatorOnThisMachineTests.cs": (2, "interroge les fenetres de la vraie session"),
    "DrDxt.App.Tests/GameLocatorOverlayOnThisMachineTests.cs": (1, "ouvre une fenetre dans la vraie session"),
    "DrDxt.App.Tests/ScanAttendUnVraiJeuReduitOnThisMachineTests.cs": (2, "attend puis surveille une vraie fenetre plein ecran dans la vraie session"),
    "DrDxt.App.Tests/GameLocatorReduitOnThisMachineTests.cs": (2, "ouvre une fenetre plein ecran, reduite ou affichee, dans la vraie session"),
    "DrDxt.Application.Tests/Tweaks/Latency/NvidiaUltraLowLatencyTweakTests.cs": (2, "demande le pilote NVIDIA"),
    "DrDxt.Detection.Tests/HagsReaderSurLeVraiSystemeTests.cs": (1, "lance dxdiag et lit la vraie carte graphique"),
    "DrDxt.Detection.Tests/Bios/VendorBiosInterfaceProbeTests.cs": (1, "interroge le WMI du BIOS"),
    "DrDxt.Detection.Tests/Cpu/CpuFrequencyStateTests.cs": (1, "lit la frequence reelle du processeur"),
    "DrDxt.Measurement.Tests/Cpu/CpuBenchTests.cs": (1, "juge la dispersion du vrai processeur, qu'un runner partage ne tient pas (63 % mesures le 27/09)"),
    "DrDxt.Detection.Tests/Hardware/UnexpectedShutdownLogTests.cs": (1, "lit le journal Systeme"),
    # Trois tests lisent la vraie machine : la CI (runner vierge) les verrait rouges, et un echec plus tot
    # dans le meme job pourrait le masquer.
    "DrDxt.Detection.Tests/Repair/AvantLIncidentTests.cs": (1, "lit les vrais journaux d'installation de pilotes (setupapi)"),
    "DrDxt.Detection.Tests/Gpu/MagasinDePilotesTests.cs": (1, "lance le vrai pnputil et lit les pilotes installes"),
    "DrDxt.Detection.Tests/Network/EcouteEnvoisUdpSurLeVraiSystemeTests.cs": (1, "ecoute les vrais envois UDP par ETW (session noyau)"),
    "DrDxt.Detection.Tests/Network/LecteurWifiTests.cs": (1, "lit la vraie carte Wi-Fi par wlanapi (absente ou eteinte sur un runner CI)"),
    # Un runner vierge n'a aucun historique de Windows Update.
    "DrDxt.Detection.Tests/Repair/MisesAJourWindowsTests.cs": (1, "lit le vrai historique de Windows Update"),
    # Un runner vierge n'a aucun jeu installé.
    "DrDxt.Detection.Tests/Game/ReglagesUnrealTests.cs": (1, "lit les vrais réglages d'un jeu Unreal installé (JeuB)"),
    # Un runner n'a pas de box, et l'ICMP sortant peut y être coupé.
    "DrDxt.App.Tests/ReseauEnPartieSurLaMachineTests.cs": (1, "pingue la vraie box et 1.1.1.1"),
    # Le journal Système d'un runner neuf n'a pas l'historique d'un PC.
    "DrDxt.Detection.Tests/Hardware/DemarrageDuSystemeTests.cs": (1, "lit le vrai journal Système (démarrages)"),
    # Un runner n'a pas de vraie carte graphique.
    "DrDxt.Detection.Tests/Gpu/CarteDuJeuTests.cs": (1, "lit les vrais compteurs GPU et la vraie carte"),
}


def inventaire(racine: Path = TESTS) -> dict[str, int]:
    trouve: dict[str, int] = {}
    for fichier in racine.rglob("*.cs"):
        if f"{'obj'}" in fichier.parts or "bin" in fichier.parts:
            continue
        combien = len(MARQUE.findall(fichier.read_text(encoding="utf-8", errors="replace")))
        if combien:
            trouve[fichier.relative_to(racine).as_posix()] = combien
    return trouve


def derives(racine: Path = TESTS) -> list[str]:
    reel = inventaire(racine)
    attendu = {c: n for c, (n, _) in ATTENDU.items()}
    messages = []
    for chemin in sorted(set(reel) | set(attendu)):
        a, r = attendu.get(chemin), reel.get(chemin)
        if a == r:
            continue
        if a is None:
            messages.append(f"{chemin} : {r} test(s) sortent de la CI et ce fichier n'est pas a l'inventaire")
        elif r is None:
            messages.append(f"{chemin} : plus aucun test hors CI ici (etait {a}) — retire-le de l'inventaire")
        else:
            messages.append(f"{chemin} : {r} hors CI, l'inventaire en attend {a}")
    return messages


def autotest() -> int:
    assert MARQUE.search('[Trait("Category", "Machine")]')
    assert MARQUE.search('[Trait( "Category" , "Machine" )]'), "les espaces ne doivent pas aveugler le crible"
    assert not MARQUE.search('[Trait("Category", "SecurityGate")]'), "une autre categorie n'est pas une sortie de CI"
    assert not MARQUE.search('// on parle de Category et de Machine'), "une phrase n'est pas un attribut"

    import tempfile

    with tempfile.TemporaryDirectory() as d:
        faux = Path(d)
        (faux / "Silencieux.cs").write_text('[Trait("Category", "Machine")] public void T() {}', encoding="utf-8")
        assert inventaire(faux) == {"Silencieux.cs": 1}, inventaire(faux)
        assert derives(faux), "un fichier inconnu qui sort de la CI doit etre signale"
    print("autotest : ok")
    return 0


def main() -> int:
    if "--autotest" in sys.argv:
        return autotest()

    ecarts = derives()
    if not ecarts:
        total = sum(n for n, _ in ATTENDU.values())
        print(f"Inventaire tenu : {total} tests hors CI, sur {len(ATTENDU)} fichiers, tous justifies.")
        return 0

    print("L'inventaire des tests hors CI a derive :")
    for e in ecarts:
        print(f"  {e}")
    print("\nPoser `Category=Machine` retire un test de la CI entierement et sans bruit")
    print("(ci.yml:37, `--filter \"Category!=Machine\"`). Que ce soit un geste conscient.")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
