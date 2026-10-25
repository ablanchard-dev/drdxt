"""Recalcule les sites d'ecriture de src/ et les compare a docs/NON-NUISANCE.md.

Il RECALCULE : il ne recopie jamais un compte. Un chiffre ecrit a la main est une dette tant
qu'aucun outil ne le refait — c'est la regle qui a produit ce script.

Sortie non-zero si un compte diverge, si un fichier apparait, ou si un fichier disparait.
Usage : python tools/nonuisance/count-write-sites.py [--print]
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DOC = os.path.join(ROOT, "docs", "NON-NUISANCE.md")

# LIGNE ZERO. Une ecriture par une API absente de cette liste est INVISIBLE pour ce controle :
# WMI Put, SetupApi, DeviceIoControl, P/Invoke direct. Le trou est nomme, pas masque.
PATTERNS = [
    r"SetFirmwareEnvironmentVariable", r"NvAPI_GPU_Set\w*", r"Registry\.SetValue",
    # QUATRIEME TROU, et cette fois l'instrument SUR-comptait -- la faute que
    # l'en-tete de ce fichier nomme deja : un motif qui fait relire du vide finit par etre
    # ignore. `\.SetValue\(` attrapait `DependencyObject.SetValue`, le setter WPF de
    # `Effets/Controles.cs` : deux << sites d'ecriture >> qui ne touchent RIEN sur la
    # machine. Les vraies ecritures nomment leur destinataire -- `key.SetValue(`,
    # `_registry.SetValue(` -- et le motif le demande desormais.
    r"\w*(?:[Kk]ey|[Rr]egistry)\.SetValue\(", r"bcdedit\.exe", r"shutdown\.exe", r"sc\.exe",
    r"File\.Write\w*", r"File\.AppendAll\w*", r"Process\.Start",
    # Deuxieme trou trouve dans cet instrument : les ecritures GPU ne portent AUCUN des
    # motifs ci-dessus. Elles s'appellent TrySetCoreClockOffset, TrySetPowerLimitPercent,
    # TrySetDrsGlobalSetting, TrySetFanCurve — c'est-a-dire la seule surface qui touche au
    # MATERIEL, et elle etait invisible. Un motif d'INTENTION vaut mieux qu'un motif d'API quand
    # l'API est enveloppee.
    r"TrySet\w+",
    # Troisieme trou, ouvert par le produit lui-meme : la suppression d'un reglage DRS
    # au profil d'un jeu s'appelle TryDeleteDrsSettingForApplication. C'est une ECRITURE (elle
    # retire ce qui etait pose), et `TrySet\w+` ne la voit pas. Meme reponse : un
    # motif d'intention. La liste ne fait que grandir.
    r"TryDelete\w+",
    # L'ecriture d'un registre du processeur passe par PawnIO (pawnio_execute "ioctl_write_msr"), un
    # P/Invoke que rien ici ne voyait. Motif d'intention : le nom de l'ioctl d'ecriture, cite dans l'appel.
    r"ioctl_write_\w+",
    # L-ECRAN : le mode de l'ecran se pose par un P/Invoke direct de user32, que rien ici ne voyait. Le
    # motif demande la parenthese : un appel ou une declaration, pas le nom cite dans une source ou une phrase.
    r"ChangeDisplaySettings\w*\(",
    # L-VOISINS : la priorite d'un AUTRE processus (le calmeur de voisins) se pose par une propriete .NET,
    # qu'aucun motif ne voyait. Le `=` seul, pas `==` : une lecture ou une comparaison n'ecrit rien.
    r"\.PriorityClass\s*=(?!=)",
    # L-CPU : une valeur a l'interieur d'un plan d'alimentation (PowerWriteACValueIndex) et la reactivation du
    # plan (PowerSetActiveScheme), deux P/Invoke directs de powrprof que rien ne voyait, le changement de plan compris.
    r"Power(?:Write\w+|SetActiveScheme)\(",
    # Le mode d'alimentation de Windows 11, repose quand l'activation d'un plan l'a remis a
    # son defaut (PowerSetUserConfiguredACPowerMode), un troisieme P/Invoke de powrprof.
    r"PowerSetUserConfigured\w+\(",
    # Un fichier ecrit par un FileStream (creation exclusive, ajout) ne portait aucun motif ;
    # passer de File.WriteAllText a FileMode.CreateNew faisait disparaitre un site d'ecriture de la table.
    r"FileMode\.(?:Create\w*|Append|OpenOrCreate)",
    # Le ventilateur NVIDIA (NVML) s'ecrit par TryPoserVentilateur / TryRendreVentilateur, des noms
    # francais qu'aucun autre motif ne voit : meme reponse, un motif d'INTENTION.
    r"TryPoser\w+\(", r"TryRendre\w+\(",
    # Le ventilateur AMD (ADLX) s'ecrit par PoserVentilateurFixe / RendreVentilateurAuto, sans le prefixe Try.
    r"(?:PoserVentilateurFixe|RendreVentilateurAuto)\(",
    # Le service SYSTEM du temoin d'incident ecrit par TrustedManifestRoot.WriteTrustedFile et supprime par
    # File.Delete : sans ces motifs, la table se dirait « a jour » en les manquant.
    # Une suppression, un deplacement, une copie, un changement d'ACL ou de cle de registre sont des
    # ecritures ; les fichiers d'etat passent par des enveloppes (WriteAtomic, WriteTrustedFile, le Delete
    # de FichierJsonAtomique) : motifs d'INTENTION, comme TrySet.
    r"WriteTrustedFile\(", r"\.WriteAtomic\(", r"File\.(?:Delete|Move|Copy|Replace)\(",
    r"Directory\.(?:Delete|Move)\(", r"\.Delete\(\)", r"DeleteValue\(", r"DeleteSubKey\w*\(",
    r"CreateSubKey\(", r"\.SetAccessControl\(",
    # L'activation de la Restauration du systeme passe par une
    # methode WMI (InvokeMethod("Enable")), l'elagage des points par SRRemoveRestorePoint, trois reglages AMD par
    # AdlxNative.EcrireAntiLag/Frtc/Vsync, et la memoire volatile du demarrage par un SetValue sur une cle nommee `cle`.
    # Une methode WMI qui ne commence pas par Get est comptee comme une ecriture ; un SetValue qui porte un
    # RegistryValueKind est une ecriture de registre, quel que soit le nom de la cle (pas le setter WPF).
    # Les methodes de lecture de WMI s'appellent Get… ou, pour les classes du moniteur (root\wmi), WmiGet… :
    # WmiGetMonitorRawEEdidV1Block rend un bloc EDID et n'ecrit rien (Microsoft Learn, WmiMonitorRawEEdidV1Block).
    r"\.InvokeMethod\(\"(?!(?:Wmi)?Get)",r"SRRemoveRestorePoint\b", r"Ecrire(?:AntiLag|Frtc|Vsync)\(",
    r"\.SetValue\(.*RegistryValueKind",
    # La remise a l'usine de la carte AMD (IADLXGPUTuningServices.ResetToFactory), le seul retour au-dela de la bande.
    r"RemettreReglageUsine\(",
    # Les outils de Windows se lancent par un seul lanceur borne (Core/ProcessusBorne.cs) : son Process.Start compte
    # chez lui, et chaque appel compte chez l'appelant, la ou se decide ce que l'outil ecrit.
    r"ProcessusBorne\.Executer\(",
]
RX = re.compile("|".join(PATTERNS))

# Premiere version : le motif etait `shutdown` tout court, et il comptait le MOT — donc
# des commentaires expliquant `shutdown /r /fw`, une phrase sur le demarrage rapide, un « unwind
# cleanly on shutdown ». La table annoncait des sites d'ecriture qui n'en etaient pas.
# Un instrument qui SUR-compte trompe autant qu'un qui sous-compte : il fait relire du vide, puis
# on apprend a l'ignorer. La vraie invocation s'ecrit `shutdown.exe` — resserrer ne perd rien.
COMMENT = re.compile(r"^\s*(//|\*|/\*)")


def measured():
    found = {}
    for root, dirs, files in os.walk(os.path.join(ROOT, "src")):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        for name in (f for f in files if f.endswith(".cs")):
            path = os.path.join(root, name)
            with open(path, encoding="utf-8", errors="replace") as handle:
                # Les lignes de commentaire ne sont pas du code : une explication de `shutdown.exe`
                # n'appelle rien. On compte ce qui s'execute.
                hits = sum(len(RX.findall(line)) for line in handle
                           if not COMMENT.match(line))
            if hits:
                found[os.path.relpath(path, ROOT).replace(os.sep, "/")] = hits
    return found


def recorded():
    with open(DOC, encoding="utf-8") as handle:
        block = re.search(r"```counts\n(.*?)```", handle.read(), re.S)
    if not block:
        sys.exit("docs/NON-NUISANCE.md : bloc ```counts introuvable")
    return {p: int(n) for p, n in (l.split() for l in block.group(1).splitlines() if l.strip())}


def main():
    now = measured()
    # --print sert a CREER la table : il ne peut donc pas l'exiger. Premiere version de ce script :
    # il lisait le document AVANT de tester ce drapeau, et refusait donc de demarrer sur un depot
    # ou la table n'existait pas encore — l'outil ne pouvait pas produire ce qu'il reclamait.
    if "--print" in sys.argv:
        print("\n".join("%s %d" % kv for kv in sorted(now.items())))
        return 0
    doc = recorded()
    if not now:
        sys.exit("aucun site trouve : les motifs sont casses, pas le depot")
    drift = [f"{p} : table={doc.get(p, 0)} mesure={now.get(p, 0)}"
             for p in sorted(set(doc) | set(now)) if doc.get(p, 0) != now.get(p, 0)]
    if drift:
        print("SITES D'ECRITURE : la table ne decrit plus le code\n  " + "\n  ".join(drift))
        return 1
    print(f"sites d'ecriture : {sum(now.values())} dans {len(now)} fichiers — table a jour")
    return 0


if __name__ == "__main__":
    sys.exit(main())
