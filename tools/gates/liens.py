# -*- coding: utf-8 -*-
"""Les chemins cites par les documents de garde existent-ils vraiment ?

`GATES.md` est le fichier dont la fonction est de BLOQUER : il dit ce qui n'est
pas fini et, surtout, ce qu'il faut faire pour debloquer. Exemple d'instruction
morte :

    Double-cliquer `installer/Output/DrDXT-Setup.exe`

Ce fichier n'existe pas, et ne PEUT pas exister : `build-installer.ps1` ne retire
le suffixe `-NON-SIGNE` qu'avec `-Sign`, et rien n'est signe. La seule
instruction actionnable du document envoyait ouvrir un dossier et n'y rien
trouver -- le gate le plus ancien du projet, ouvert depuis le 27/08, tenait pour
partie a une chaine de caracteres.

Un document de garde qui cite un chemin mort ne bloque plus : il fait perdre son
temps a celui qui le suit, et la fois d'apres il n'est plus suivi.

Lecture seule. N'ecrit rien, ne construit rien, ne lance rien.

    python tools/gates/liens.py            # 0 si tout resout, 1 sinon
    python tools/gates/liens.py --autotest # se verifie lui-meme
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

RACINE = Path(__file__).resolve().parents[2]

DOCUMENTS = ["GATES.md", "README.md", "README.fr.md", "CONTRIBUTING.md", "CONTRIBUTING.fr.md", "ARCHITECTURE.md"]

# Un chemin du depot cite entre accents graves : au moins un dossier connu en
# tete, pour ne pas confondre avec un identifiant de code (`Result`, `Ok(vide)`).
DOSSIERS = ("docs", "tools", "installer", "server", "src", "tests")
CHEMIN = re.compile(
    r"`((?:" + "|".join(DOSSIERS) + r")/[A-Za-z0-9._/\\-]+)`"
)

# Ce qui est cite comme un MOTIF, pas comme un fichier : le verifier serait du
# bruit, et un crible qui crie au loup finit desactive.
# `...` est une ELISION (« tests/.../SourcesCiteesTests ») : le fichier existe, c'est
# le chemin qui est abrege. Premier essai sans cette entree : une accusation, a tort.
JOKERS = ("*", "{", "%", "...")


# Un lien Markdown ou une image vers un fichier du depot. Une adresse web, une ancre ou un courriel ne sont pas
# des fichiers ; l'ancre d'un lien vers un fichier (`docs/X.md#section`) tombe, le fichier reste.
LIEN = re.compile(r"\]\(([^)\s]+)\)|<img[^>]*\ssrc=\"([^\"]+)\"")
EXTERNE = ("http://", "https://", "mailto:", "#")


def chemins_cites(texte: str) -> list[str]:
    cites = [c.replace("\\", "/") for c in CHEMIN.findall(texte) if not any(j in c for j in JOKERS)]
    for lien, image in LIEN.findall(texte):
        cible = (lien or image).split("#")[0]
        if cible and not (lien or image).startswith(EXTERNE) and cible not in cites:
            cites.append(cible)
    return cites


def manquants(racine: Path = RACINE) -> list[tuple[str, str]]:
    """(document, chemin) pour chaque citation qui ne resout pas."""
    trouves = []
    for nom in DOCUMENTS:
        fichier = racine / nom
        if not fichier.exists():
            trouves.append((nom, "<le document lui-meme est introuvable>"))
            continue
        texte = fichier.read_text(encoding="utf-8")
        for chemin in chemins_cites(texte):
            if not (racine / chemin).exists():
                trouves.append((nom, chemin))
    return trouves


def autotest() -> int:
    """Le crible doit voir, et ne pas crier a tort."""
    assert chemins_cites("ouvrir `installer/Output/DrDXT-Setup.exe` puis") == [
        "installer/Output/DrDXT-Setup.exe"
    ]
    assert chemins_cites("le type `Result` est neutre") == [], "un identifiant n'est pas un chemin"
    assert chemins_cites("voir `tools/*` pour les smokes") == [], "un motif n'est pas un chemin"
    assert chemins_cites("`%LOCALAPPDATA%/x`") == [], "une variable d'environnement n'est pas un chemin"
    # Et il doit savoir accuser : un document temoin qui cite un fichier absent.
    import tempfile

    with tempfile.TemporaryDirectory() as d:
        faux = Path(d)
        for nom in DOCUMENTS:
            (faux / nom).write_text("rien", encoding="utf-8")
        (faux / "GATES.md").write_text("voir `docs/INEXISTANT.md`", encoding="utf-8")
        assert manquants(faux) == [("GATES.md", "docs/INEXISTANT.md")], manquants(faux)
        # Les liens et les images du README comptent autant que les chemins entre accents graves.
        (faux / "LICENSE").write_text("", encoding="utf-8")
        (faux / "README.md").write_text(
            "[licence](LICENSE) [plus bas](#build) [site](https://example.org/x.md)\n"
            "[absent](docs/ABSENT.md#section) <img src=\"docs/images/absente.png\" alt=\"\">", encoding="utf-8")
        assert manquants(faux) == [("GATES.md", "docs/INEXISTANT.md"), ("README.md", "docs/ABSENT.md"),
                                   ("README.md", "docs/images/absente.png")], manquants(faux)
    print("autotest : ok")
    return 0


def main() -> int:
    if "--autotest" in sys.argv:
        return autotest()

    morts = manquants()
    if not morts:
        print("Tous les chemins cites par les documents de garde existent.")
        return 0

    print("Chemins cites qui n'existent pas :")
    for document, chemin in morts:
        print(f"  {document} -> {chemin}")
    print("\nUn document de garde qui cite un chemin mort ne bloque plus : il fait")
    print("perdre son temps a celui qui le suit.")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
