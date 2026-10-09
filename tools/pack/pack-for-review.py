"""Fabrique le paquet complet du depot, pour relecture par un tiers.

Pourquoi un script plutot qu'une commande a la main : le contenu d'un zip envoye a quelqu'un
d'autre est une action VERS L'EXTERIEUR. Ce qui y entre doit etre decide par une liste ecrite,
relisible, et verifiee EN LISANT LE ZIP PRODUIT -- pas en faisant confiance a l'intention.

Ce qui est exclu, et pourquoi chacun :
  .git/            l'historique complet, y compris ce qui n'a jamais ete pousse
  .claude/         configuration locale d'outils, jamais suivie par git
  bin/ obj/        des artefacts de build, lourds et inutiles a relire
  installer/publish/  176 Mo de runtime .NET recopie
  installer/Output/   l'executable de 54 Mo -- il se reconstruit, il ne se relit pas
  tools/amd-harness/dist et publish, artifacts/   103 Mo de sortie de build, TROUVES en lisant
                   le zip produit et non en devinant
  *.user *.suo    des reglages d'editeur personnels
  .env *.key *.pfx  tout ce qui ressemble a un secret

Et une garde qui ne depend d'aucune liste : le script REFUSE de produire le zip s'il y trouve
un chemin interdit ou un motif de secret. Une exclusion qu'on n'a pas verifiee est une intention.

Usage : python tools/pack/pack-for-review.py [dossier de sortie]
"""
import os
import re
import sys
import zipfile
from datetime import date

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# `audits/` : non versionne, notes de revue internes. TROUVE en lisant le zip produit le 10/09/2026 :
# trois fichiers y etaient partis — le script parcourt le DISQUE, pas git, donc tout ce qui est non
# suivi part avec, sauf exclusion ecrite.
EXCLUDED_DIRS = {".git", ".claude", ".vs", "bin", "obj", "node_modules", "__pycache__", "audits"}
EXCLUDED_PATHS = {
    os.path.join("installer", "publish"),
    os.path.join("installer", "Output"),
    # CES TROIS-LA N'ONT PAS ETE DEVINEES, ELLES ONT ETE LUES DANS LE ZIP PRODUIT.
    # La premiere version du paquet pesait 150 Mo pour 1 160 fichiers : 103 Mo venaient d'ici,
    # trois zips de livraison et un runtime .NET complet recopie. Ma liste d'exclusions decrivait
    # ou je CROYAIS que vivait la sortie de build. Le depot en avait trois de plus.
    os.path.join("tools", "amd-harness", "dist"),
    os.path.join("tools", "amd-harness", "publish"),
    "artifacts",
}
EXCLUDED_SUFFIXES = (".user", ".suo", ".pfx", ".key", ".pem", ".snk")
EXCLUDED_NAMES = {".env"}

# Motifs qui ne doivent apparaitre dans AUCUN fichier du zip. Deliberement etroits : un motif
# large rendrait le controle bruyant, on l'apprendrait a ignorer, et le jour ou il a raison
# personne ne le verrait.
SECRET_PATTERNS = [
    # Un secret de webhook Lemon Squeezy. La regle porte sur la FORME, pas sur une liste de noms
    # autorises : une liste ecrite a la main pourrit au premier test ajoute, et ce depot a deja
    # paye ce defaut. Un vrai secret ne contient jamais le mot « test ».
    re.compile(rb"ls_whsec_(?![A-Za-z0-9_]*test)"),
    re.compile(rb"-----BEGIN (RSA |EC )?PRIVATE KEY-----"),
    re.compile(rb"ghp_[A-Za-z0-9]{20,}"),                     # jeton GitHub
    # Regle du projet « zero trace d'assistant » : un paquet qui sort ne nomme pas les outils de
    # revue internes. Ajoute le 10/09/2026 apres en avoir trouve dans des commentaires de src/.
    # Ce qui BLOQUE tient ; une consigne qui RAPPELLE s'oublie.
    re.compile(rb"(?i)\bfable\b"),
    re.compile(rb"(?i)\bopus\s?5\b"),
]

TEXTUAL = (".cs", ".csproj", ".json", ".yml", ".yaml", ".md", ".xaml", ".resx", ".iss",
           ".ps1", ".py", ".txt", ".sln", ".props", ".config")


# Un binaire compile n'a rien a faire dans un paquet de RELECTURE : il ne se lit pas, il se
# reconstruit. Seul `third_party/` y echappe -- ce sont des dependances vendorisees dont le build
# a besoin et qu'on ne peut pas regenerer.
#
# REGLE DE FORME, PAS LISTE DE NOMS. La premiere version excluait `publish` et `dist` : elle a
# laisse passer `tools/drsprobe/guet/`, 24 DLL et PDB de nos propres assemblies, parce qu'il porte
# un troisieme nom. Une liste de dossiers decrit ou l'on CROIT que vit le build ; une regle sur
# l'extension decrit ce qu'est un binaire.
BUILD_ARTIFACT_SUFFIXES = (".dll", ".pdb", ".exe", ".lib", ".so", ".dylib")
VENDORED_ROOT = "third_party/"


def excluded(rel):
    slash = rel.replace("\\", "/")
    if slash.lower().endswith(BUILD_ARTIFACT_SUFFIXES) and not slash.startswith(VENDORED_ROOT):
        return True
    parts = slash.split("/")
    if any(p in EXCLUDED_DIRS for p in parts):
        return True
    if any(rel.replace("\\", "/").startswith(p.replace("\\", "/")) for p in EXCLUDED_PATHS):
        return True
    if rel.endswith(EXCLUDED_SUFFIXES) or parts[-1] in EXCLUDED_NAMES:
        return True
    return False


def collect():
    for root, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in EXCLUDED_DIRS]
        for name in files:
            full = os.path.join(root, name)
            rel = os.path.relpath(full, ROOT)
            if not excluded(rel):
                yield full, rel


def scan_for_secrets(paths):
    hits = []
    for full, rel in paths:
        if not rel.endswith(TEXTUAL):
            continue
        # Ce fichier-ci contient les motifs eux-memes : sans cette ligne, le scanner se
        # denonce. Troisieme controle textuel de la soiree a se faire piéger par sa propre
        # prose -- apres le commentaire XAML et la tranche trop courte.
        if rel.replace("\\", "/").startswith("tools/pack/"):
            continue
        try:
            with open(full, "rb") as handle:
                blob = handle.read()
        except OSError:
            continue
        for pattern in SECRET_PATTERNS:
            if pattern.search(blob):
                hits.append("%s  (%s)" % (rel, pattern.pattern.decode("ascii", "replace")))
    return hits


def main():
    out_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.expanduser("~"), "Downloads")
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    files = sorted(collect(), key=lambda t: t[1])
    if not files:
        sys.exit("aucun fichier collecte : le balayage est casse, pas le depot")

    secrets = scan_for_secrets(files)
    if secrets:
        print("SECRET POSSIBLE, zip NON produit :\n  " + "\n  ".join(secrets))
        return 2

    name = "DrDXT-source-%s.zip" % date.today().isoformat()
    target = os.path.join(out_dir, name)
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for full, rel in files:
            zf.write(full, os.path.join("DrDXT", rel))

    # LA VERIFICATION QUI COMPTE : on relit le zip PRODUIT. Une exclusion prouvee par la liste
    # qui l'a ecrite ne prouve rien -- c'est le contenu reel qui fait foi.
    with zipfile.ZipFile(target) as zf:
        names = zf.namelist()
    forbidden = [n for n in names
                 if excluded(os.path.relpath(n, "DrDXT").replace("\\", "/"))]
    if forbidden:
        os.remove(target)
        print("CHEMIN INTERDIT DANS LE ZIP, supprime :\n  " + "\n  ".join(forbidden[:20]))
        return 3

    size = os.path.getsize(target)
    print("zip      : %s" % target)
    print("entrees  : %d" % len(names))
    print("taille   : %.1f Mo" % (size / 1024 / 1024))
    print("verifie  : aucun chemin exclu dans le CONTENU du zip, aucun motif de secret")
    return 0


if __name__ == "__main__":
    sys.exit(main())
