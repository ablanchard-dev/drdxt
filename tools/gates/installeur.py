# -*- coding: utf-8 -*-
"""Le script de l'installeur compile-t-il encore ?

Aucun test ne compilait `installer/DrDXT.iss` : les tests le relisent comme du
texte. Un commentaire Pascal `{ ... {app} ... }` (la premiere accolade fermante
ferme le commentaire) a donc casse la construction de l'installeur sans qu'aucun
test ne tombe.

Ce gate compile le script avec ISCC, sans sortie (`/O-`) : rien n'est construit,
publie ni signe. Il compile une copie, dans un dossier temporaire, avec un
`publish` qui ne contient qu'un fichier temoin : le gate ne depend pas d'un
build local, et ne lit jamais celui qui serait la.

Il faut Inno Setup 6, cherche comme `build-installer.ps1` le cherche (PATH, puis
`%LOCALAPPDATA%\\Programs\\Inno Setup 6`), puis la ou une installation machine le pose
(`Program Files (x86)\\Inno Setup 6`, celle de la CI).

    python tools/gates/installeur.py   # 0 compile, 1 ne compile pas, 3 ISCC absent (rien n'est verifie)
"""
from __future__ import annotations

import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

RACINE = Path(__file__).resolve().parents[2]
ABSENT = 3
LANCES = ("service/DrDxt.ReconcilerHost.exe", "service/DrDxt.ReconcilerHost.dll", "PresentMonService.exe",
          "DrDxt.ElevatedHelper.exe", "DrDxt.ElevatedHelper.dll", "DrDxt.Lanceur.exe", "DrDxt.App.exe", "DrDxt.App.dll")


def iscc() -> str | None:
    trouve = shutil.which("ISCC.exe") or shutil.which("ISCC")
    if trouve:
        return trouve
    for base, sous in (("LOCALAPPDATA", ("Programs", "Inno Setup 6")), ("ProgramFiles(x86)", ("Inno Setup 6",))):
        if os.environ.get(base) and (chemin := Path(os.environ[base], *sous, "ISCC.exe")).is_file():
            return str(chemin)
    return None


def version() -> str:
    # La seule source de la version, comme pour build-installer.ps1.
    texte = (RACINE / "Directory.Build.props").read_text(encoding="utf-8-sig")
    m = re.search(r"<Version>\s*([^<\s]+)\s*</Version>", texte)
    if not m:
        raise SystemExit("Aucune <Version> dans Directory.Build.props.")
    return m.group(1)


def main() -> int:
    compilateur = iscc()
    if compilateur is None:
        print("ISCC (Inno Setup 6) introuvable : le script de l'installeur n'est PAS verifie. "
              "winget install JRSoftware.InnoSetup")
        return ABSENT

    with tempfile.TemporaryDirectory(prefix="drdxt-iscc-") as tmp:
        # Les chemins du script sont relatifs a installer/ : la copie garde la meme forme.
        dossier = Path(tmp) / "installer"
        (dossier / "publish").mkdir(parents=True)
        (dossier / "publish" / "temoin.txt").write_text("temoin du gate installeur.py\n", encoding="utf-8")
        # Les fichiers dont le script calcule l'empreinte a la compilation (EmpreintesConformes) : des temoins aussi.
        for lance in LANCES:
            (dossier / "publish" / lance).parent.mkdir(parents=True, exist_ok=True)
            (dossier / "publish" / lance).write_bytes(lance.encode("utf-8"))
        shutil.copy2(RACINE / "installer" / "DrDXT.iss", dossier)
        shutil.copy2(RACINE / "installer" / "README-testeur.txt", dossier)
        shutil.copy2(RACINE / "installer" / "README-tester.txt", dossier)
        shutil.copy2(RACINE / "LICENSE", Path(tmp))
        r = subprocess.run([compilateur, "/O-", "/Q", f"/DAppVer={version()}", str(dossier / "DrDXT.iss")],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")

    if r.returncode == 0:
        print("installer/DrDXT.iss compile.")
        return 0
    print(f"installer/DrDXT.iss ne compile pas (ISCC rend {r.returncode}) :")
    print((r.stdout + r.stderr).strip().replace(str(dossier), "installer"))
    return 1


if __name__ == "__main__":
    sys.exit(main())
