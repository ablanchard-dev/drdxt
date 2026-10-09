# -*- coding: utf-8 -*-
"""Le README annonce un nombre de tests. Est-ce celui de la derniere execution ?

18/09/2026, et c'est la cinquieme fois. Le README porte, mot pour mot :

    **4 374 tests, 0 echec** -- mesure le 18/09/2026 sur 18 projets. Un chiffre
    ecrit a la main vieillit : celui-ci portait **2 957**, puis **4 205**, puis
    **4 325**, et se trompait a chaque fois de la meme facon -- vers le bas,
    parce qu'on ajoute des tests.

Le paragraphe DIT le probleme, le date du jour, et se trompe quand meme : la
mesure du jour donne **4 396**. La phrase qui se plaint d'un chiffre ecrit a la
main est elle-meme un chiffre ecrit a la main -- et c'est toujours le meme
ecart, vers le bas.

Ce gate ne reecrit rien. Il lit les journaux `.trx` de la derniere execution
complete et compare leur somme a ce que le README annonce. Il ne sait donc rien
dire sans une execution recente, et il le dit plutot que de rendre vert. En Release, comme la
CI : des tests n'existent qu'en Debug (`#if DEBUG`), et compter en Debug ajoutait 14 le 08/10/2026 :

    dotnet test DrDXT.sln -c Release --filter "Category!=Machine" \\
        --logger "trx" --results-directory TestResults
    python tools/gates/compte_des_tests.py            # 0 si le README dit vrai
    python tools/gates/compte_des_tests.py --autotest # se verifie lui-meme

Lecture seule. N'ecrit rien, ne construit rien, ne lance rien.

Piege mesure, et la premiere version de ce commentaire le decrivait mal.
Ce qui casse, ce n'est pas `LogFileName` seul : c'est `LogFileName` **plus**
`--results-directory` partage. Les 18 projets ecrivent alors le meme fichier et
s'ecrasent, et le journal final ne decrit qu'un assembly -- deux executions
completes perdues pour ca. Sans `--results-directory`, chaque projet ecrit dans
SON `TestResults/`, et la CI du depot (`LogFileName=suite-complete.trx`) est donc
correcte : 18 fichiers, 18 chemins. Ce gate cherche les deux dispositions.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

RACINE = Path(__file__).resolve().parents[2]
# UN dossier, UNE execution. Mesure du 18/09 : en balayant `**/TestResults/*.trx` le gate a
# trouve 54 journaux pour 12 223 tests -- des `.trx` de plusieurs executions differentes,
# empiles sous les `bin/` des projets depuis des jours. Une somme qui melange deux executions
# n'est pas une mesure. Le gate lit donc un seul dossier, celui qu'on vide avant de lancer.
#
# 19/09/2026 — et c'etait encore trop etroit. `dotnet test DrDXT.sln --logger "trx"`, la
# commande ecrite ci-dessus, n'ecrit PAS a la racine : chaque projet ecrit dans SON
# `TestResults/`. Le gate ne lisait donc que la racine, restee figee sur une execution
# d'une session precedente -- et il presentait ce vieux total comme la mesure du jour.
# Les deux dispositions sont lues ; ce qui empeche de melanger deux executions n'est plus
# le dossier, mais le TEMPS : un seul journal par dossier, le plus recent, et seulement
# ceux qui appartiennent a la meme execution que le plus recent de tous.
#
# Et les projets de test ne vivent pas tous sous `tests/` : il y en a un sous `server/`, un
# sous `tools/`, un sous `tools/amd-harness/`. En ne lisant que `tests/*`, le gate comptait
# 15 projets et annoncait un total ampute de trois projets entiers -- exactement le genre de
# chiffre faux qu'il existe pour empecher.
JOURNAUX = (
    "TestResults/*.trx",
    "*/*/TestResults/*.trx",
    "*/*/*/TestResults/*.trx",
)

# Duree maximale d'une execution complete : au-dela, deux journaux viennent de deux
# executions differentes. La suite complete prend environ deux minutes ; cette fenetre est
# large pour ne pas ecarter un projet lent, et bien plus courte qu'une session.
FENETRE_D_UNE_EXECUTION_S = 900
README = "README.md"

# <Counters total="703" executed="703" passed="703" failed="0" .../>
STOCKAGE = re.compile(r'storage="([^"]+)"')
COMPTEURS = re.compile(r'total="(\d+)"\s+executed="\d+"\s+passed="(\d+)"\s+failed="(\d+)"')

# **4 374 tests, 0 echec** : l'espace peut etre une espace fine insecable.
ANNONCE = re.compile(r"\*\*([\d   ]+) tests, (\d+) échec")


def journaux_de_la_derniere_execution(racine: Path = RACINE) -> list[Path]:
    """Un journal par dossier, le plus recent, et tous de la MEME execution.

    Deux filets, parce que deux choses differentes peuvent melanger des executions :
    un dossier qui accumule les journaux (on n'en garde que le dernier), et deux
    dossiers remplis a des heures differentes (la fenetre les separe).
    """
    # 24/09/2026 — la cle n'est plus le dossier mais le PROJET teste. Avec
    # `--results-directory TestResults` (la commande documentee plus haut), les 17 projets
    # ecrivent dans le MEME dossier : garder un journal par dossier en gardait un seul, et le
    # gate annoncait 736 tests pour 4 330 reels. Chaque journal nomme la dll qu'il a testee
    # (`storage="..."`) ; a defaut, on retombe sur le dossier, comme avant.
    par_dossier: dict[object, Path] = {}
    for motif in JOURNAUX:
        for journal in racine.glob(motif):
            # Un journal enfoui sous un `bin/` ou un `obj/` vient d'une copie de build, pas
            # d'une execution : le compter melangerait deux mesures.
            if "bin" in journal.parts or "obj" in journal.parts:
                continue
            # 27/09/2026 : la CI rejoue ensuite des SOUS-ENSEMBLES des memes dll (etapes
            # gate-revert, gate-reconciler, gate-securite : 5, 5, 1 et 11 tests) dans des
            # journaux `gate-*.trx`, plus recents. Gardes « le plus recent par dll », ils
            # remplacaient le compte complet de ces projets : 4 545 annonces pour 4 977
            # executes. Un journal de gate n'est pas une execution complete.
            if journal.name.startswith("gate-"):
                continue
            chemin = journal.resolve()
            dll = STOCKAGE.search(chemin.read_text(encoding="utf-8", errors="replace"))
            cle = dll.group(1).lower() if dll else chemin.parent
            actuel = par_dossier.get(cle)
            if actuel is None or chemin.stat().st_mtime > actuel.stat().st_mtime:
                par_dossier[cle] = chemin

    if not par_dossier:
        return []

    dernier = max(c.stat().st_mtime for c in par_dossier.values())
    return sorted(c for c in par_dossier.values()
                  if dernier - c.stat().st_mtime <= FENETRE_D_UNE_EXECUTION_S)


def mesure(racine: Path = RACINE) -> tuple[int, int, int]:
    """(projets, tests, echecs) lus dans les journaux de la derniere execution."""
    projets = tests = echecs = 0
    # Dedoublonne par CHEMIN, jamais par nom : sur la CI les 18 journaux s'appellent tous
    # `suite-complete.trx` et ne different que par leur dossier. Dedoublonner par nom n'en
    # aurait compte qu'un seul -- et le gate aurait accuse le README d'un ecart invente.
    for journal in journaux_de_la_derniere_execution(racine):
        trouve = COMPTEURS.search(journal.read_text(encoding="utf-8", errors="replace"))
        if trouve:
            projets += 1
            tests += int(trouve.group(1))
            echecs += int(trouve.group(3))
    return projets, tests, echecs


def perime(racine: Path = RACINE) -> str | None:
    """La raison pour laquelle ces journaux ne decrivent plus le depot, ou None.

    19/09/2026. Ce fichier promet, en tete : « Il ne sait donc rien dire sans une
    execution recente, et il le dit plutot que de rendre vert. » Il ne le disait pas.

    MESURE : deux tours de suite, six tests puis trois tests ajoutes, le gate a rendu le
    MEME total et un exit 0. Les journaux de `TestResults/` dataient de 00:57 ; les
    sources modifiees allaient jusqu'a 01:34. La commande documentee plus haut ecrit en
    realite dans le `TestResults/` de CHAQUE projet, pas a la racine -- la racine gardait
    une execution d'une session precedente, et le gate la presentait comme la mesure du
    jour. Un instrument immobile accuse l'instrument, et j'ai cru le chiffre deux fois.

    La regle qui ferme ca ne demande aucune horloge : un journal ne peut pas decrire un
    fichier ecrit APRES lui.
    """
    journaux = journaux_de_la_derniere_execution(racine)
    if not journaux:
        return f"aucun journal .trx sous {racine} : lancez la suite d'abord"

    dernier = max(c.stat().st_mtime for c in journaux)

    plus_recente = None
    for source in list(racine.glob("src/**/*.cs")) + list(racine.glob("tests/**/*.cs")):
        parties = source.parts
        if "obj" in parties or "bin" in parties:
            continue
        if source.stat().st_mtime > dernier:
            plus_recente = source
            break

    if plus_recente is not None:
        return (f"journaux perimes : {plus_recente.relative_to(racine)} a ete modifie APRES "
                "la derniere execution. Relancez la suite avant de croire ce chiffre")
    return None


def annonce(racine: Path = RACINE) -> tuple[int, int] | None:
    """(tests, echecs) tels que le README les annonce, ou None s'il n'annonce rien."""
    trouve = ANNONCE.search((racine / README).read_text(encoding="utf-8"))
    if not trouve:
        return None
    chiffre = re.sub(r"[^\d]", "", trouve.group(1))
    return int(chiffre), int(trouve.group(2))


def autotest() -> int:
    import tempfile

    with tempfile.TemporaryDirectory() as tmp:
        faux = Path(tmp)
        (faux / "TestResults").mkdir()
        (faux / "TestResults" / "a.trx").write_text(
            '<Counters total="10" executed="10" passed="9" failed="1" />', encoding="utf-8")
        # La disposition REELLE : un dossier par projet. `dotnet test` de la solution ecrit
        # dans le `TestResults/` de chaque projet, pas dans un dossier commun.
        (faux / "tests" / "Projet.Tests" / "TestResults").mkdir(parents=True)
        (faux / "tests" / "Projet.Tests" / "TestResults" / "b.trx").write_text(
            '<Counters total="5" executed="5" passed="5" failed="0" />', encoding="utf-8")
        assert mesure(faux) == (2, 15, 1), mesure(faux)

        # Un dossier qui ACCUMULE : seul le journal le plus recent compte. Sans ca, deux
        # executions d'affilee se sommeraient -- c'est arrive la nuit du 19/09.
        import os as _os
        import time as _time
        vieux_meme_dossier = faux / "tests" / "Projet.Tests" / "TestResults" / "a-avant.trx"
        vieux_meme_dossier.write_text(
            '<Counters total="999" executed="999" passed="999" failed="0" />', encoding="utf-8")
        _os.utime(vieux_meme_dossier, (_time.time() - 3600, _time.time() - 3600))
        assert mesure(faux) == (2, 15, 1), mesure(faux)

        # Disposition de la CI : un TestResults par projet, MEME nom de fichier partout.
        # Un journal enfoui sous un bin/ d'une execution precedente ne doit PAS etre compte :
        # melanger deux executions ne fait pas une mesure.
        vieux = faux / "tests" / "projet" / "bin" / "TestResults"
        vieux.mkdir(parents=True)
        (vieux / "suite-complete.trx").write_text(
            '<Counters total="999" executed="999" passed="999" failed="0" />', encoding="utf-8")
        assert mesure(faux) == (2, 15, 1), mesure(faux)

        # La disposition de la commande documentee : plusieurs projets dans UN dossier. Chaque
        # journal compte (un par dll) ; un journal plus ancien de la meme dll, non.
        commun = faux / "commun"
        (commun / "TestResults").mkdir(parents=True)
        for nom, total in (("x.trx", 7), ("y.trx", 3)):
            (commun / "TestResults" / nom).write_text(
                f'<UnitTest storage="c:/p/{nom}.dll" /><Counters total="{total}" executed="{total}" '
                f'passed="{total}" failed="0" />', encoding="utf-8")
        ancien = commun / "TestResults" / "x-avant.trx"
        ancien.write_text('<UnitTest storage="c:/p/x.trx.dll" /><Counters total="500" executed="500" '
                          'passed="500" failed="0" />', encoding="utf-8")
        _os.utime(ancien, (_time.time() - 60, _time.time() - 60))
        assert mesure(commun) == (2, 10, 0), mesure(commun)

        # 27/09/2026 : un journal de gate, PLUS RECENT, sur la meme dll, ne remplace pas le compte complet.
        gate = commun / "TestResults" / "gate-securite.trx"
        gate.write_text('<UnitTest storage="c:/p/x.trx.dll" /><Counters total="2" executed="2" '
                        'passed="2" failed="0" />', encoding="utf-8")
        _os.utime(gate, (_time.time() + 5, _time.time() + 5))
        assert mesure(commun) == (2, 10, 0), mesure(commun)

        (faux / README).write_text("bla **15 tests, 0 échec** bla", encoding="utf-8")
        assert annonce(faux) == (15, 0), annonce(faux)

        # L'espace fine insecable du README reel ne doit pas casser la lecture.
        (faux / README).write_text("**4 396 tests, 0 échec**", encoding="utf-8")
        assert annonce(faux) == (4396, 0), annonce(faux)

        # LA FRAICHEUR. Un journal ne peut pas decrire un fichier ecrit apres lui.
        import os
        import time

        assert perime(faux) is None, perime(faux)

        source = faux / "src" / "Projet" / "Chose.cs"
        source.parent.mkdir(parents=True)
        source.write_text("class Chose { }", encoding="utf-8")
        os.utime(source, (time.time() + 60, time.time() + 60))
        assert perime(faux) is not None, "une source plus recente que le journal doit alerter"

        # Et l'inverse : une source PLUS ANCIENNE ne doit pas faire crier au loup.
        os.utime(source, (time.time() - 600, time.time() - 600))
        assert perime(faux) is None, perime(faux)

        # Un `obj/` regenere a chaque build n'est pas une source : sans cette exclusion,
        # le gate serait perime en permanence et on apprendrait a l'ignorer.
        genere = faux / "src" / "Projet" / "obj" / "Chose.g.cs"
        genere.parent.mkdir(parents=True)
        genere.write_text("// genere", encoding="utf-8")
        os.utime(genere, (time.time() + 60, time.time() + 60))
        assert perime(faux) is None, perime(faux)

        # LE BRANCHEMENT, pas seulement la garde. Debrancher `perime` de la decision
        # laissait l'autotest vert : il prouvait la garde et pas le chemin qui l'emprunte.
        # Ce bloc passe par `verdict`, le meme chemin que l'utilisateur.
        (faux / README).write_text("**15 tests, 1 échec**", encoding="utf-8")
        assert verdict(faux) == (0, ["Le README dit vrai : 15 tests, 1 echec(s), sur 2 projets."]), \
            verdict(faux)

        os.utime(source, (time.time() + 60, time.time() + 60))
        code, lignes = verdict(faux)
        assert code == 1 and "perimes" in lignes[0], (code, lignes)
        os.utime(source, (time.time() - 600, time.time() - 600))

        # Un README qui se trompe : le gate doit dire le SENS mesure, pas celui qu'il croit.
        (faux / README).write_text("**99 tests, 1 échec**", encoding="utf-8")
        code, lignes = verdict(faux)
        assert code == 1 and any("vers le haut" in l for l in lignes), lignes

        # Aucun chiffre annonce : le gate doit le voir, pas l'inventer.
        (faux / README).write_text("le depot est teste", encoding="utf-8")
        assert annonce(faux) is None
        assert verdict(faux)[0] == 0
    print("autotest : ok")
    return 0


def verdict(racine: Path = RACINE) -> tuple[int, list[str]]:
    """(code de sortie, lignes a dire). Separe de `main` pour etre TESTABLE.

    19/09/2026 : la garde de fraicheur vivait dans `main`, et l'autotest appelait
    `perime()` directement. En la debranchant de `main`, l'autotest restait VERT —
    il prouvait la garde, pas son branchement. Un test peut nommer une garantie sans
    la tenir ; celui-ci passe maintenant par le meme chemin que l'utilisateur.
    """
    raison = perime(racine)
    if raison is not None:
        return 1, [raison,
                   "Sans mesure a jour, ce gate ne dit RIEN -- il ne rend pas vert par defaut."]

    projets, tests, echecs = mesure(racine)
    if projets == 0:
        return 1, ["Aucun journal .trx : lancer la suite avec --logger \"trx\" avant ce gate.",
                   "Sans mesure, ce gate ne dit RIEN -- il ne rend pas vert par defaut."]

    dit = annonce(racine)
    if dit is None:
        return 0, ["Le README n'annonce plus de nombre de tests : rien a tenir."]

    if dit == (tests, echecs):
        return 0, [f"Le README dit vrai : {tests} tests, {echecs} echec(s), sur {projets} projets."]

    sens = "vers le bas" if dit[0] < tests else "vers le haut"
    return 1, [
        f"Le README annonce {dit[0]} tests / {dit[1]} echec(s).",
        f"La derniere execution en a compte {tests} / {echecs}, sur {projets} projets.",
        f"\nLe README est {sens} de {abs(dit[0] - tests)} test(s).",
        "Le paragraphe du README qui explique ce piege est lui-meme un chiffre",
        "ecrit a la main.",
    ]


def main() -> int:
    if "--autotest" in sys.argv:
        return autotest()

    code, lignes = verdict()
    for ligne in lignes:
        print(ligne)
    return code


if __name__ == "__main__":
    raise SystemExit(main())
