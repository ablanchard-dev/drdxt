# -*- coding: utf-8 -*-
"""Tire d'un IFR de BIOS l'emplacement NVRAM d'un reglage, au format de la matrice.

Lancer :
    python tools/ifr/ifr2matrice.py <dump.ifr.txt> "Extreme Memory Profile(XMP)" "Re-Size BAR Support"
    python tools/ifr/ifr2matrice.py --test

Le dump vient d'IFRExtractor-RS (LongSoft, BSD-2) lance sur le module Setup du BIOS
officiel du constructeur : `ifrextractor body.bin` (le mode `all` casse sur la longueur
des chemins Windows). La sortie est ce que `BiosQualificationMatrix.Setup(...)` attend.

Ce que ce script ne decide pas :
 - Un meme libelle peut etre declare plusieurs fois (le BIOS MSI 1.H0 declare
   « Re-Size BAR Support » deux fois, « DRAM Frequency » cinq fois, des conditions
   masquant les autres selon le materiel). Toutes les occurrences sont imprimees ;
   choisir la bonne demande une lecture reelle recoupee avec Windows, comme pour
   0xD39 = 3800 MT/s sur la machine de reference.
 - Le chemin de menu et le risque de non-demarrage restent a ecrire a la main.
"""
import re
import sys

QUESTION = re.compile(
    r'^\s*OneOf Prompt: "(?P<prompt>[^"]*)".*?VarStoreId: (?P<store>0x[0-9A-F]+), '
    r'VarOffset: (?P<offset>0x[0-9A-F]+),.*?Size: (?P<size>\d+)')
OPTION = re.compile(r'^\s*OneOfOption Option: "(?P<libelle>[^"]*)" Value: (?P<valeur>\d+)(?P<drapeaux>.*)$')
DEFAUT = re.compile(r'^\s*Default DefaultId: 0x0 Value: (?P<valeur>\d+)')
STORE = re.compile(r'^\s*VarStore Guid: [0-9A-F-]+, VarStoreId: (?P<id>0x[0-9A-F]+), Size: 0x[0-9A-F]+, Name: "(?P<nom>[^"]*)"')


def questions(lignes, cherches):
    """Rend chaque OneOf dont le libelle est cherche : store, offset, taille, defaut, options."""
    stores, trouvees, courante, profondeur = {}, [], None, 0
    for ligne in lignes:
        if m := STORE.match(ligne):
            stores[m["id"]] = m["nom"]
        if courante is None:
            m = QUESTION.match(ligne)
            if m and m["prompt"] in cherches:
                courante = {"prompt": m["prompt"], "store": stores.get(m["store"], m["store"]),
                            "offset": int(m["offset"], 16), "taille": int(m["size"]),
                            "options": [], "defaut": None, "drapeau": None}
                profondeur = 0
            continue
        # Les options sont souvent emballees dans des SuppressIf ... End : on compte les
        # blocs ouverts pour savoir quel End ferme la question.
        if re.match(r"^\s*(SuppressIf|GrayOutIf|DisableIf)\b", ligne):
            profondeur += 1
        elif re.match(r"^\s*End\s*$", ligne):
            if profondeur == 0:
                trouvees.append(courante)
                courante = None
            else:
                profondeur -= 1
        elif m := OPTION.match(ligne):
            valeur = int(m["valeur"])
            courante["options"].append((valeur, m["libelle"]))
            # « Default » (standard) prime sur « MfgDefault » (usine) s'il n'y a pas de ligne Default.
            if re.search(r"\bDefault\b", m["drapeaux"]):
                courante["drapeau"] = valeur
            elif "MfgDefault" in m["drapeaux"] and courante["drapeau"] is None:
                courante["drapeau"] = valeur
        elif m := DEFAUT.match(ligne):
            courante["defaut"] = int(m["valeur"])
    return trouvees


def en_csharp(q):
    defaut = q["defaut"] if q["defaut"] is not None else q["drapeau"]
    valeurs = ", ".join(f'({v}, "{l}")' for v, l in q["options"])
    fabrique = "Cpu" if q["store"] == "CpuSetup" else "Setup"
    return f'{fabrique}(0x{q["offset"]:X}, Defaut: {defaut if defaut is not None else "?"}, {valeurs})'


EXTRAIT = '''\tVarStore Guid: EC87D643-EBA4-4BB5-A1E5-3F3E36B20DA9, VarStoreId: 0xF101, Size: 0x13D9, Name: "Setup"
\t\t\t\tOneOf Prompt: "Extreme Memory Profile(XMP)", Help: "x", QuestionFlags: 0x14, QuestionId: 0x2973, VarStoreId: 0xF101, VarOffset: 0xE65, Flags: 0x10, Size: 8, Min: 0x0, Max: 0x5, Step: 0x0
\t\t\t\t\tOneOfOption Option: "Disabled" Value: 0, MfgDefault
\t\t\t\t\tSuppressIf
\t\t\t\t\t\tEqIdValList QuestionId: 0x162D, Values: [2, 4]
\t\t\t\t\t\tOneOfOption Option: "Profile 1" Value: 1
\t\t\t\t\tEnd
\t\t\t\t\tDefault DefaultId: 0x0 Value: 0
\t\t\t\tEnd
\t\t\tOneOf Prompt: "Re-Size BAR Support", Help: "y", QuestionFlags: 0x14, QuestionId: 0x2C6D, VarStoreId: 0xF101, VarOffset: 0x1234, Flags: 0x10, Size: 8, Min: 0x0, Max: 0x1, Step: 0x0
\t\t\t\tOneOfOption Option: "Disabled" Value: 0
\t\t\t\tOneOfOption Option: "Enabled" Value: 1, Default
\t\t\tEnd
\t\t\tOneOf Prompt: "Autre", Help: "z", QuestionFlags: 0x10, QuestionId: 0x1, VarStoreId: 0xF101, VarOffset: 0x1, Flags: 0x10, Size: 8, Min: 0x0, Max: 0x1, Step: 0x0
\t\t\tEnd
'''


def test():
    qs = questions(EXTRAIT.splitlines(), {"Extreme Memory Profile(XMP)", "Re-Size BAR Support"})
    assert [q["prompt"] for q in qs] == ["Extreme Memory Profile(XMP)", "Re-Size BAR Support"], qs
    assert qs[0]["store"] == "Setup"
    # L'option dans le SuppressIf appartient a la question : le End du SuppressIf ne la ferme pas.
    assert en_csharp(qs[0]) == 'Setup(0xE65, Defaut: 0, (0, "Disabled"), (1, "Profile 1"))', en_csharp(qs[0])
    # Sans ligne Default, c'est le drapeau de l'option qui donne le defaut.
    assert en_csharp(qs[1]) == 'Setup(0x1234, Defaut: 1, (0, "Disabled"), (1, "Enabled"))', en_csharp(qs[1])
    print("ok")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    if sys.argv[1:] == ["--test"]:
        test()
        sys.exit(0)
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    with open(sys.argv[1], encoding="utf-8", errors="replace") as f:
        trouvees = questions(f, set(sys.argv[2:]))
    for q in trouvees:
        taille = "" if q["taille"] == 8 else f"   (taille {q['taille']} bits : Setup() lit un octet, a adapter)"
        print(f'// « {q["prompt"]} », store {q["store"]}{taille}')
        print(en_csharp(q))
    # Deux declarations sur le meme octet ecrivent la meme variable : la table doit connaitre
    # l'union de leurs valeurs, sinon une valeur de l'autre fait jeter la lecture (Package C
    # State Limit, 254 « Cpu Default », trouve le 25/09/2026).
    par_octet = {}
    for q in trouvees:
        par_octet.setdefault((q["store"], q["offset"]), set()).update(v for v, _ in q["options"])
    for q in trouvees:
        manque = par_octet[(q["store"], q["offset"])] - {v for v, _ in q["options"]}
        if manque:
            print(f'// ATTENTION « {q["prompt"]} » 0x{q["offset"]:X} : une autre declaration '
                  f'ajoute {sorted(manque)}, reprendre l\'union', file=sys.stderr)
    manquants = set(sys.argv[2:]) - {q["prompt"] for q in trouvees}
    for p in sorted(manquants):
        print(f"// « {p} » : absent de cet IFR", file=sys.stderr)
