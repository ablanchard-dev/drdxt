"""Recalcule l'age de chaque ligne de GATES.md depuis SA DATE.

Ce script existe parce que GATES.md portait une colonne << Jours >> ecrite a la main, et
qu'elle etait fausse sur les 19 lignes le 06/09/2026 : 27/08 affichait 9 pour 10 jours,
02/09 affichait 3 pour 4. Un age fige dans un fichier qu'on relit pour decider est faux des
le lendemain. La date, elle, ne bouge pas. La colonne a donc ete supprimee ; l'age se calcule.

Sortie non nulle si une ligne ne porte pas de date lisible.
"""
import datetime
import pathlib
import re
import sys

# 19/09/2026 — UNE LIGNE FERMEE N'ATTEND PLUS PERSONNE.
#
# Ce gate comptait TOUTES les lignes datees, barrees comprises. Mesure du jour : 56 lignes,
# dont 8 explicitement fermees, et le resume annoncait << 40 au-dela de 7 jours >>. Sur le
# seul fichier qui sert a dire au mainteneur ce qui l'attend, un chiffre qui melange les decisions
# en attente et les faits clos est un chiffre qu'on cesse de regarder.
#
# La convention est celle du fichier lui-meme : une ligne fermee est barree, ou porte ✅,
# ou dit CLOSE/FERMEE. Aucune liste tenue a la main.
FERMEE = re.compile(r"^\s*~~|✅|\b(CLOSE|CLOSEE|FERMEE|FERME|REQUALIFIEE)\b", re.IGNORECASE)


def close(rest: str) -> bool:
    return bool(FERMEE.search(rest[:80]))


ROW = re.compile(r"^\|\s*(\d{2})/(\d{2})\s*\|(.{0,120})", re.MULTILINE)
GATES = pathlib.Path(__file__).resolve().parents[2] / "GATES.md"


def autotest() -> int:
    """Le gate se verifie lui-meme, comme `compte_des_tests.py`.

    Ce qui est prouve n'est pas le comptage -- c'est la DISTINCTION. Un gate qui compte
    des lignes closes parmi ce qui attend quelqu'un publie un chiffre sur lequel personne
    ne peut agir, et un chiffre sur lequel on ne peut pas agir finit par ne plus etre lu.
    """
    assert close("~~ une ligne barree ~~"), "la barre est la convention du fichier"
    assert close(" ✅ faite le 12/09"), "la coche aussi"
    assert close("**FERMEE** le 18/09"), "et le mot, en toutes lettres"
    assert close("REQUALIFIEE en autre chose"), "requalifiee n'attend plus le mainteneur non plus"

    assert not close("Verifier que l'installeur s'installe"), "un geste a faire reste ouvert"
    assert not close("La fenetre ne tenait pas dans les ecrans"), (
        "un constat au passe n'est PAS une fermeture : la ligne du 18/09 decrivait un "
        "defaut corrige, et elle comptait quand meme comme ouverte tant qu'on ne l'avait "
        "pas barree -- c'est l'auteur qui ferme, pas le temps du verbe"
    )

    # Le contrepoids : un gate qui declarerait tout ferme ne dirait plus rien.
    assert not close("."), "une ligne vide de marqueur reste ouverte"

    print("autotest : ok")
    return 0


def main() -> int:
    if "--autotest" in sys.argv:
        return autotest()

    # La console Windows est en cp1252 : un seul emoji de GATES.md fait planter le script
    # AVANT sa derniere ligne, donc apres avoir deja affiche la moitie du tableau. Piege
    # deja rencontre deux fois dans ce depot ; il coute une ligne a fermer.
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    text = GATES.read_text(encoding="utf-8")
    rows = ROW.findall(text)
    if not rows:
        print("GATES.md : aucune ligne datee trouvee", file=sys.stderr)
        return 1

    today = datetime.date.today()
    ages = []
    ouverts = []
    for day, month, rest in rows:
        # Les lignes sont ecrites en JJ/MM sans annee ; une date future appartient donc a
        # l'annee precedente. Sans cette regle, une ligne de decembre lue en janvier
        # afficherait un age negatif -- et un age negatif se lit comme << recent >>.
        d = datetime.date(today.year, int(month), int(day))
        if d > today:
            d = datetime.date(today.year - 1, int(month), int(day))
        age = (today - d).days
        ages.append(age)
        if not close(rest):
            ouverts.append(age)
        marque = "   " if close(rest) else " ! "
        print(f"{d.isoformat()}  {age:>3} j {marque} {rest.strip()[:88]}")

    print()
    print(f"{len(ages)} lignes, dont {len(ouverts)} OUVERTES  ·  "
          f"la plus vieille ouverte a {max(ouverts) if ouverts else 0} jours  ·  "
          f"{sum(1 for a in ouverts if a > 7)} ouvertes au-dela de 7 jours")
    return 0


if __name__ == "__main__":
    sys.exit(main())
