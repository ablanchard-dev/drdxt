"""Verifie que chaque test CITE par docs/NON-NUISANCE.md existe vraiment sous tests/.

La table de non-nuisance porte, en face de chaque garde, le nom du test qui la prouve. Un nom
recopie a la main est une dette : une methode renommee, un fichier deplace, et le document continue
d'affirmer une preuve qui n'existe plus. Ce script refait la verification.

Il ne juge PAS ce que le test assure — c'est le travail des mutations. Il verifie qu'il existe.

Sortie non-zero si un nom cite est introuvable, ou si le document ne cite plus aucun test (la table
aurait perdu sa colonne de preuve sans que rien ne le dise).

Usage : python tools/nonuisance/check-guard-tests.py [doc.md ...]
        Sans argument : docs/NON-NUISANCE.md. Avec : les documents donnes.
        Etendu le 06/09/2026 pour servir aussi docs/RAPPORT-VERIFICATION.md — un rapport dont
        chaque affirmation nomme son test n'a de valeur que si les noms se resolvent.
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_DOC = os.path.join(ROOT, "docs", "NON-NUISANCE.md")

# Un nom cite s'ecrit entre accents graves, sous deux formes :
#   `NomDeClasseTests`                       -> la classe entiere prouve la garde
#   `NomDeClasseTests.Nom_de_methode`        -> une methode precise
#   `...._suite_du_nom`                      -> abreviation d'une methode de la classe precedente
# La forme abregee est volontairement ACCEPTEE sans etre resolue : elle sert la lecture humaine, et
# la classe qui la porte est verifiee juste avant elle dans la meme cellule.
CITED = re.compile(r"`([A-Za-z][A-Za-z0-9]*Tests)(?:\.([A-Za-z_][A-Za-z0-9_]*))?`")


def sources():
    out = {}
    # Les tests du SERVEUR vivent hors de tests/ : sans eux, un rapport qui cite une garantie
    # serveur echouerait a la resoudre alors que le test existe.
    for base in (os.path.join(ROOT, "tests"), os.path.join(ROOT, "server")):
      for root, dirs, files in os.walk(base):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        for name in (f for f in files if f.endswith(".cs")):
            path = os.path.join(root, name)
            with open(path, encoding="utf-8", errors="replace") as handle:
                out[path] = handle.read()
    return out


def main():
    docs = sys.argv[1:] or [DEFAULT_DOC]

    cited = []
    for doc in docs:
        path = doc if os.path.isabs(doc) else os.path.join(ROOT, doc)
        with open(path, encoding="utf-8") as handle:
            found = CITED.findall(handle.read())
        if not found:
            sys.exit("%s ne cite AUCUN test : la colonne de preuve a disparu" % doc)
        cited.extend(found)

    tree = sources()
    if not tree:
        sys.exit("aucun fichier de test trouve : le balayage est casse, pas le depot")

    missing = []
    for klass, method in cited:
        if not any(("class " + klass) in text for text in tree.values()):
            missing.append(klass)
            continue
        if method and not any((method + "(") in text for text in tree.values()):
            missing.append(klass + "." + method)

    if missing:
        print("NOMS CITES INTROUVABLES sous tests/\n  " + "\n  ".join(sorted(set(missing))))
        return 1

    print("gardes citees : %d references, toutes resolues" % len(cited))
    return 0


if __name__ == "__main__":
    sys.exit(main())
