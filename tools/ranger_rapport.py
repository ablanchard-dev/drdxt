"""CONTRIB2 (08/10/2026) : range un fichier reçu d'un joueur là où les tests le rejouent.

- un rapport « Vérifier DrDXT sur mon PC » (texte, commence par « BILAN ») va dans tests/Shared/validations sous
  <architecture>-<carte>-<date>.txt ; UnePreuveDebloqueSonMaterielTests le lit ;
- un fichier « Exporter ma machine » (JSON) va dans tests/Shared/machines sous <nom>.json ; toute la suite qui
  parcourt les machines enregistrées le rejoue.

Rien n'est rangé si le fichier n'a pas la bonne forme, ou s'il garde une donnée qui aurait dû être retirée à
l'export (MachineExportee.CeQuiEstRetire) : courriel, SID, adresse MAC, dossier d'un compte autre que « joueur ».

    python tools/ranger_rapport.py verification-drdxt-xxx.txt --architecture Rdna3 --carte rx7800xt
    python tools/ranger_rapport.py ma-machine.json --nom amd-rx7800xt-pote
    python tools/ranger_rapport.py --autotest
"""
import argparse
import json
import re
import sys
from pathlib import Path

RACINE = Path(__file__).resolve().parent.parent
ARCHITECTURES = {"AdaOrOlder", "Rdna3", "Rdna4", "Blackwell"}
NOM_SUR = re.compile(r"^[a-z0-9][a-z0-9-]{2,60}$")
# La liste suit MachineExportee.CeQuiEstRetire ; un export d'une ancienne version qui en
# garde un se refuse. Ce qui ressemble sans l'être (C:\Users\Public, nvlddmkm@4.0, une version 1.459.153.0) passe.
OCTET = r"(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)"
IDENTIFIANTS = [
    ("un courriel", re.compile(r"[\w.+-]+@[\w-]+(?:\.[\w-]+)*\.[A-Za-z]{2,}\b")),
    ("un SID de compte", re.compile(r"S-1-5-21-\d+")),
    ("une adresse MAC", re.compile(r"\b[0-9A-Fa-f]{2}([-:])(?:[0-9A-Fa-f]{2}\1){4}[0-9A-Fa-f]{2}\b")),
    ("une adresse MAC", re.compile(r"(?<![-\w])[0-9A-F]{12}(?![-\w])")),
    ("une adresse IP", re.compile(rf"(?<![\d.]){OCTET}\.{OCTET}\.{OCTET}\.{OCTET}(?![\d.])")),
    ("une adresse IP", re.compile(r"(?i)\b(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}\b|\b(?:[0-9a-f]{1,4}:){1,6}:[0-9a-f]{0,4}\b")),
    ("le nom du PC", re.compile(r"\b(?:DESKTOP|LAPTOP)-[A-Z0-9]{5,}\b")),
    ("un identifiant de volume", re.compile(r"Volume\{[0-9a-fA-F-]{36}\}")),
    ("une organisation OneDrive", re.compile(r"OneDrive - [^\\/\"]+")),
    ("le dossier d'un compte", re.compile(r"Users(?:\\{1,2}|/)(?!(?:joueur|Public|Default)\b)[^\\/\"]+", re.IGNORECASE)),
]


VERSION_AVANT = re.compile(r"(?i)(pilote|version|driver|logiciel)[^\"\n]{0,25}$")


def identifiant_reste(texte):
    for nom, motif in IDENTIFIANTS:
        for m in motif.finditer(texte):
            # Une version à quatre nombres (« pilote NVIDIA 4.92.0.0 ») a la forme d'une adresse v4 : l'export la garde.
            if nom == "une adresse IP" and VERSION_AVANT.search(texte[max(0, m.start() - 40):m.start()]):
                continue
            return nom
    return None


def destination(texte, architecture=None, carte=None, nom=None):
    """Le chemin relatif où ranger ce texte, ou lève ValueError avec la raison."""
    if (reste := identifiant_reste(texte)) is not None:
        raise ValueError(f"le fichier garde {reste} : refaire l'export avec la version actuelle de DrDXT")
    if texte.lstrip().startswith("BILAN"):
        if architecture not in ARCHITECTURES:
            raise ValueError(f"--architecture attendue parmi {sorted(ARCHITECTURES)}")
        if not carte or not NOM_SUR.match(carte):
            raise ValueError("--carte attendue, en minuscules sans espace (ex. rx7800xt)")
        date = re.search(r"vérification du (\d{2})/(\d{2})/(\d{4})", texte)
        if date is None:
            raise ValueError("pas de date « vérification du jj/mm/aaaa » : ce n'est pas un rapport complet")
        return Path("tests/Shared/validations") / f"{architecture}-{carte}-{date[3]}-{date[2]}-{date[1]}.txt"
    try:
        machine = json.loads(texte)
    except json.JSONDecodeError:
        raise ValueError("ni un rapport de vérification (BILAN…), ni un export de machine (JSON)") from None
    if not isinstance(machine, dict) or not {"Inventaire", "Reglages"} <= machine.keys():
        raise ValueError("export de machine sans « Inventaire » ou « Reglages »")
    if not nom or not NOM_SUR.match(nom):
        raise ValueError("--nom attendu, en minuscules sans espace (ex. amd-rx7800xt-pote)")
    return Path("tests/Shared/machines") / f"{nom}.json"


def ranger(source, **choix):
    texte = Path(source).read_text(encoding="utf-8-sig")
    if texte.lstrip().startswith("{"):
        try:
            machine = json.loads(texte)
        except json.JSONDecodeError:
            raise ValueError("commence comme un export de machine, mais n'est pas du JSON valide") from None
        machine["Nom"] = choix.get("nom")
        texte = json.dumps(machine, ensure_ascii=False, indent=2)
    cible = RACINE / destination(texte, **choix)
    if cible.exists():
        raise ValueError(f"{cible.relative_to(RACINE)} existe déjà : choisir un autre nom")
    cible.write_text(texte, encoding="utf-8")
    return cible


def autotest():
    rapport = "BILAN : tout est remis d'origine.\n\nDrDXT 0.9.0, vérification du 08/10/2026 19:29\n"
    assert destination(rapport, architecture="Rdna3", carte="rx7800xt") == Path(
        "tests/Shared/validations/Rdna3-rx7800xt-2026-10-08.txt")
    machine = json.dumps({"Nom": "x", "Inventaire": {}, "Reglages": {"Chemin": "C:\\Users\\joueur\\Documents"}})
    assert destination(machine, nom="amd-pote") == Path("tests/Shared/machines/amd-pote.json")
    refus = [
        (rapport, {"architecture": "Turing", "carte": "rtx2060"}),
        (rapport, {"architecture": "Rdna3", "carte": "RX 7800"}),
        ("BILAN sans date", {"architecture": "Rdna3", "carte": "rx7800xt"}),
        (machine.replace("joueur", "alexandre"), {"nom": "amd-pote"}),
        (machine.replace("joueur", "joueur\\\\a@b.fr"), {"nom": "amd-pote"}),
        (json.dumps({"Inventaire": {}, "Reglages": {"Mac": "AA-BB-CC-DD-EE-FF"}}), {"nom": "amd-pote"}),
        (json.dumps({"Inventaire": {}}), {"nom": "amd-pote"}),
        ("pas un rapport", {"nom": "amd-pote"}),
        # Ce que l'ancienne liste laissait passer.
        (machine.replace("Documents", "Documents 192.168.1.20"), {"nom": "amd-pote"}),
        (machine.replace("Documents", "Documents fe80::1c2d:3e4f"), {"nom": "amd-pote"}),
        (machine.replace("Documents", "Documents DESKTOP-4KQ7ZPA"), {"nom": "amd-pote"}),
        (machine.replace("Documents", "Documents A4BB6D2E19F0"), {"nom": "amd-pote"}),
        (machine.replace("Documents", "Volume{6b29fc40-ca47-1067-b31d-00dd010662da}"), {"nom": "amd-pote"}),
        (machine.replace("Documents", "OneDrive - Contoso"), {"nom": "amd-pote"}),
        (machine.replace("C:\\\\Users\\\\joueur", "C:/Users/bob"), {"nom": "amd-pote"}),
    ]
    # Ce qui ressemble sans l'être passe.
    for propre in ["C:\\Users\\Public\\Documents", "nvlddmkm@4.0", "version 1.459.153.0", "il est 20:00:00",
                   "plan 381b4222-f694-41f0-9685-ff5bb260df2e"]:
        assert identifiant_reste(propre) is None, propre
    try:
        destination("{ abîmé", nom="amd-pote")
    except ValueError:
        pass
    for texte, choix in refus:
        try:
            destination(texte, **choix)
        except ValueError:
            continue
        raise AssertionError(f"aurait dû refuser : {texte[:60]!r} {choix}")
    for f in (RACINE / "tests/Shared/machines").glob("*.json"):
        assert identifiant_reste(f.read_text(encoding="utf-8-sig")) is None, f"{f.name} garde un identifiant"
    print("autotest OK")


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("source", nargs="?")
    p.add_argument("--architecture")
    p.add_argument("--carte")
    p.add_argument("--nom")
    p.add_argument("--autotest", action="store_true")
    a = p.parse_args()
    if a.autotest:
        autotest()
        sys.exit(0)
    if not a.source:
        p.error("un fichier à ranger, ou --autotest")
    try:
        print(f"rangé : {ranger(a.source, architecture=a.architecture, carte=a.carte, nom=a.nom).relative_to(RACINE)}")
    except ValueError as e:
        print(f"refusé : {e}")
        sys.exit(1)
