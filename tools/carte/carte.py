# -*- coding: utf-8 -*-
"""La carte du depot : le graphe que lit un agent (ou un humain) avant de toucher au code.

Un agent qui corrige une fonction sans voir tous ses appelants corrige un chemin et laisse
les autres casses. Cette carte se RECALCULE depuis les sources a chaque appel : elle n'est
jamais commitee, donc jamais perimee.

Lecture seule. Ne compile rien, ne lance rien.

    python -I tools/carte/carte.py projets          # qui reference qui (csproj)
    python -I tools/carte/carte.py op SetBcdValueOp # une op de bout en bout : definition,
                                                    # liste blanche, journal, qui la cree, tests
    python -I tools/carte/carte.py ops              # toutes les ops privilegiees, en tableau
    python -I tools/carte/carte.py qui NomDeType    # tous les fichiers qui citent ce nom
    python -I tools/carte/carte.py ecrit [Projet]   # les sites d'ecriture sur la machine
    python -I tools/carte/carte.py json [FICHIER]   # tout le graphe, pour un outil
    python -I tools/carte/carte.py --autotest
"""
from __future__ import annotations

import importlib.util
import json
import re
import sys
from pathlib import Path

RACINE = Path(__file__).resolve().parents[2]

# Les motifs d'ecriture sont ceux de la table NON-NUISANCE : une seule liste, pas une copie.
_spec = importlib.util.spec_from_file_location("sites", RACINE / "tools" / "nonuisance" / "count-write-sites.py")
_sites = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_sites)

# Une op se declare souvent sur plusieurs lignes (ses parametres) : on lit le fichier entier.
OP = re.compile(r"\brecord\s+(\w+Op)\b[^;{]*?:\s*PrivilegedOp\b", re.S)
REF = re.compile(r'<ProjectReference\s+Include="([^"]+)"')
COMMENTAIRE = _sites.COMMENT


def sources(racine: Path, dossier: str):
    for p in sorted((racine / dossier).rglob("*.cs")):
        if not ({"bin", "obj"} & set(p.parts)):
            yield p


def lire(p: Path) -> list[str]:
    return p.read_text(encoding="utf-8", errors="replace").splitlines()


def rel(racine: Path, p: Path) -> str:
    return p.relative_to(racine).as_posix()


def projet(chemin: str) -> str:
    parts = chemin.split("/")
    return parts[1] if len(parts) > 2 else parts[0]


def projets(racine: Path) -> dict[str, list[str]]:
    graphe = {}
    for csproj in sorted(list((racine / "src").glob("*/*.csproj")) + list((racine / "tests").glob("*/*.csproj"))):
        refs = REF.findall(csproj.read_text(encoding="utf-8", errors="replace"))
        graphe[csproj.stem] = sorted(Path(r.replace("\\", "/")).stem for r in refs)
    return graphe


def citations(racine: Path, nom: str, dossiers=("src", "tests")) -> list[tuple[str, int, str]]:
    mot = re.compile(rf"\b{re.escape(nom)}\b")
    trouve = []
    for d in dossiers:
        for p in sources(racine, d):
            for i, ligne in enumerate(lire(p), 1):
                if mot.search(ligne) and not COMMENTAIRE.match(ligne):
                    trouve.append((rel(racine, p), i, ligne.strip()))
    return trouve


def ops(racine: Path) -> dict[str, dict]:
    """Chaque op privilegiee et ce qui la tient : la liste blanche, la classe du journal, qui la cree, ses tests."""
    defs = {}
    for p in sources(racine, "src"):
        texte = p.read_text(encoding="utf-8", errors="replace")
        for m in OP.finditer(texte):
            defs[m.group(1)] = f"{rel(racine, p)}:{texte.count(chr(10), 0, m.start()) + 1}"
    carte = {}
    for nom, ou in sorted(defs.items()):
        cit = citations(racine, nom)
        dans = lambda fichier: [f"{f}:{n}" for f, n, _ in cit if f.endswith(fichier)]
        journal = [t for f, _, t in cit if f.endswith("ClassesDuJournal.cs")]
        classe = next((m.group(1) for t in journal if (m := re.search(r"ClasseDuJournal\.(\w+)", t))), None)
        # Une op classee selon son contenu (ADLX : par genre de reglage) : `op is X a ? ...`.
        if classe is None and any(re.search(rf"\bis\s+{nom}\b", t) for t in journal):
            classe = "SelonLeContenu"
        carte[nom] = {
            "definition": ou,
            "liste_blanche": dans("OpAllowlist.cs"),
            "journal": classe,
            # `new X(` ou `X Geste() => new(` : la creation sans nom de type compte aussi.
            "cree_par": sorted({f for f, _, t in cit if f.startswith("src/") and re.search(rf"new\s+{nom}\b|\bnew\s*\(", t)}),
            "tests": sorted({f for f, _, _ in cit if f.startswith("tests/")}),
        }
    return carte


def ecritures(racine: Path) -> dict[str, list[str]]:
    sites = {}
    for p in sources(racine, "src"):
        for i, ligne in enumerate(lire(p), 1):
            if not COMMENTAIRE.match(ligne) and _sites.RX.search(ligne):
                sites.setdefault(rel(racine, p), []).append(f"{i}: {ligne.strip()[:110]}")
    return sites


def main(argv: list[str]) -> int:
    cmd, args = (argv[0], argv[1:]) if argv else ("", [])
    if cmd == "--autotest":
        return autotest()
    if cmd == "projets":
        for p, refs in projets(RACINE).items():
            print(f"{p} -> {', '.join(refs) or '(rien)'}")
    elif cmd == "ops":
        for nom, o in ops(RACINE).items():
            print(f"{nom:38} journal={o['journal'] or '-':14} liste_blanche={len(o['liste_blanche'])} "
                  f"cree_par={len(o['cree_par'])} tests={len(o['tests'])}")
    elif cmd == "op" and args:
        o = ops(RACINE).get(args[0])
        if o is None:
            print(f"aucune op {args[0]}"); return 1
        print(json.dumps(o, indent=2, ensure_ascii=False))
    elif cmd == "qui" and args:
        par = {}
        for f, n, t in citations(RACINE, args[0]):
            par.setdefault(projet(f), []).append(f"  {f}:{n}  {t[:100]}")
        for p, lignes in sorted(par.items()):
            print(p); print("\n".join(lignes))
        if not par:
            print(f"personne ne cite {args[0]}"); return 1
    elif cmd == "ecrit":
        for f, lignes in ecritures(RACINE).items():
            if not args or projet(f) == args[0]:
                print(f); print("\n".join("  " + l for l in lignes))
    elif cmd == "json":
        graphe = {"projets": projets(RACINE), "ops": ops(RACINE), "ecritures": ecritures(RACINE)}
        texte = json.dumps(graphe, indent=1, ensure_ascii=False)
        if args:
            Path(args[0]).write_text(texte, encoding="utf-8")
        else:
            print(texte)
    else:
        print(__doc__); return 2
    return 0


def autotest() -> int:
    import tempfile
    with tempfile.TemporaryDirectory() as d:
        r = Path(d)
        (r / "src" / "A").mkdir(parents=True); (r / "src" / "B").mkdir(parents=True); (r / "tests" / "A.Tests").mkdir(parents=True)
        (r / "src" / "A" / "A.csproj").write_text('<ProjectReference Include="..\\B\\B.csproj" />', encoding="utf-8")
        (r / "src" / "B" / "B.csproj").write_text("", encoding="utf-8")
        (r / "src" / "B" / "Ops.cs").write_text("public sealed record PoserOp(int V) : PrivilegedOp;\n"
                                              "public sealed record LongueOp(\n    int V,\n    int W) : PrivilegedOp;\n", encoding="utf-8")
        (r / "src" / "B" / "OpAllowlist.cs").write_text("if (op is PoserOp) return Ok;\n", encoding="utf-8")
        (r / "src" / "A" / "Levier.cs").write_text("// new PoserOp(0) en commentaire\nvar op = new PoserOp(1);\nFile.WriteAllText(p, t);\n", encoding="utf-8")
        (r / "src" / "A" / "Geste.cs").write_text("public LongueOp Geste() => new(1, 2);\n", encoding="utf-8")
        (r / "src" / "B" / "ClassesDuJournal.cs").write_text("[typeof(PoserOp)] = ClasseDuJournal.Journalisee,\n"
                                                           "op is LongueOp l ? ParGenre(l) : null;\n", encoding="utf-8")
        (r / "tests" / "A.Tests" / "LevierTests.cs").write_text("new PoserOp(2);\n", encoding="utf-8")
        assert projets(r) == {"A": ["B"], "B": []}, projets(r)
        o = ops(r)["PoserOp"]
        assert o["definition"] == "src/B/Ops.cs:1", o
        longue = ops(r)["LongueOp"]
        assert longue["definition"] == "src/B/Ops.cs:2", longue
        assert longue["cree_par"] == ["src/A/Geste.cs"] and longue["journal"] == "SelonLeContenu", longue
        assert o["liste_blanche"] == ["src/B/OpAllowlist.cs:1"], o
        assert o["journal"] == "Journalisee", o
        assert o["cree_par"] == ["src/A/Levier.cs"], o
        assert o["tests"] == ["tests/A.Tests/LevierTests.cs"], o
        assert list(ecritures(r)) == ["src/A/Levier.cs"] and len(ecritures(r)["src/A/Levier.cs"]) == 1, ecritures(r)
    # Temoin reel : un crible qui ne trouve rien sur le vrai depot est casse, pas le depot.
    vraies = ops(RACINE)
    assert len(vraies) >= 20 and vraies.get("SetBcdValueOp", {}).get("liste_blanche"), "aucune op reelle lue"
    print("autotest carte : ok")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
