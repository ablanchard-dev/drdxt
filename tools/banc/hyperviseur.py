# -*- coding: utf-8 -*-
"""Y a-t-il, sur cette machine, de quoi tester un no-boot et sa recuperation ?

Lancer :  python tools/banc/hyperviseur.py
Autotest :  python tools/banc/hyperviseur.py --autotest

Pourquoi ce script existe. Le 06/09/2026, GATES.md concluait : « VirtualBox,
VMware et QEMU **absents** [...] il n'existe donc aucun moyen de tester un no-boot
puis sa recuperation », et renvoyait la balle au mainteneur (« une machine sacrifiable »).
Le 17/09, une seule commande a suffi a dementir la phrase : VirtualBox 7.2.4 est
installe, avec un invite **Windows 11** et un instantane nomme `Windows-propre`.

La ligne n'etait pas fausse quand elle a ete ecrite. Elle a vieilli, et personne
ne l'a relancee pendant onze jours — c'est le defaut que ce script repare : il
rend la reponse re-verifiable au lieu d'etre datee.

Ce script ne DEMARRE rien. Il lit. Demarrer une machine virtuelle ecrit sur le
disque de la machine et consomme quatre coeurs : c'est un geste du mainteneur, pas du script.
"""
import json
import os
import re
import shutil
import subprocess
import sys

VBOX = r"C:\Program Files\Oracle\VirtualBox\VBoxManage.exe"


def _vboxmanage() -> str | None:
    if os.path.isfile(VBOX):
        return VBOX
    return shutil.which("VBoxManage")


def _lire(exe: str, *args: str) -> str:
    try:
        return subprocess.run([exe, *args], capture_output=True, text=True, timeout=60).stdout
    except Exception:
        return ""


def machines(sortie_liste: str) -> list[str]:
    """Noms des machines, depuis la sortie de `VBoxManage list vms`."""
    return re.findall(r'^"([^"]+)"', sortie_liste, re.M)


def profil(info: str) -> dict:
    """Ce qui decide si une machine est un banc de no-boot exploitable."""
    def champ(cle):
        m = re.search(rf'^{cle}="?([^"\n]*)"?$', info, re.M)
        return m.group(1).strip() if m else ""

    instantanes = re.findall(r'^SnapshotName[^=]*="([^"]+)"$', info, re.M)
    return {
        "systeme": champ("ostype"),
        "etat": champ("VMState"),
        "processeurs": champ("cpus"),
        "instantanes": instantanes,
        "windows": "windows" in champ("ostype").lower(),
        "restaurable": bool(instantanes),
    }


def main() -> int:
    exe = _vboxmanage()
    if exe is None:
        print("VirtualBox : absent. Aucun banc de no-boot ici.")
        return 1

    print(f"VirtualBox {_lire(exe, '--version').strip()}  ({exe})")
    noms = machines(_lire(exe, "list", "vms"))
    if not noms:
        print("Aucune machine virtuelle definie.")
        return 1

    utilisables = 0
    for nom in noms:
        p = profil(_lire(exe, "showvminfo", nom, "--machinereadable"))
        marque = "BANC   " if p["windows"] and p["restaurable"] else "       "
        print(f"{marque} {nom:20} {p['systeme']:22} etat={p['etat']:16} "
              f"instantanes={p['instantanes'] or '-'}")
        utilisables += 1 if (p["windows"] and p["restaurable"]) else 0

    print()
    if utilisables:
        print(f"{utilisables} machine(s) Windows avec instantane : un no-boot peut etre "
              "provoque puis annule par restauration.")
        print("Ce script n'en demarre aucune — demarrer ecrit sur le disque, c'est la decision du mainteneur.")
        return 0
    print("Aucun invite Windows avec instantane : pas de banc de no-boot exploitable.")
    return 1


def _autotest() -> int:
    """Le parsing est la seule logique non triviale ici : il se verifie hors machine."""
    liste = '"frubuntu" {8893286c-908d-4c28-a418-79584779f6f0}\n"wzp" {20892e4a-6e4f}\n'
    assert machines(liste) == ["frubuntu", "wzp"], machines(liste)

    info_win = ('ostype="Windows 11 (64-bit)"\nmemory=8192\ncpus=4\n'
                'VMState="aborted-saved"\nSnapshotName="Windows-propre"\n')
    p = profil(info_win)
    assert p["windows"] is True and p["restaurable"] is True, p
    assert p["instantanes"] == ["Windows-propre"], p
    assert p["processeurs"] == "4", p

    info_linux = 'ostype="Ubuntu (64-bit)"\nVMState="poweroff"\n'
    q = profil(info_linux)
    assert q["windows"] is False, q
    # Un Linux sans instantane ne prouve rien sur un no-boot Windows : les deux
    # conditions doivent tomber, pas une seule.
    assert q["restaurable"] is False, q

    print("autotest : ok")
    return 0


if __name__ == "__main__":
    sys.exit(_autotest() if "--autotest" in sys.argv else main())
