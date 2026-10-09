# -*- coding: utf-8 -*-
"""Verifie que les sources citees par le produit repondent encore.

Lancer :  python tools/liens/verifie.py

Ce que ce script DIT, et ce qu'il ne dit pas :
 - `200` : la page repond.
 - `-> autre URL` : elle repond, mais ailleurs. Le lien n'est pas mort, il est
   PERIME : on le remplace par sa destination, sinon il mourra un jour sans bruit.
 - `403 / 429` : le site refuse les robots. **Ce n'est pas une page morte** --
   Intel a rendu 403 a ce script le 17/09/2026 alors que ses deux pages
   s'ouvraient parfaitement dans un navigateur. Toute conclusion demande un
   vrai navigateur.
 - `404 / 410` : la, c'est mort, et le constat qui s'appuie dessus perd sa preuve.

Le test hors reseau `SourcesCiteesTests` couvre ce qui se sait sans appeler
personne (https, URL absolue, domaines decides). Ce script couvre le reste.
"""
import pathlib
import re
import sys
import urllib.error
import urllib.request

RACINE = pathlib.Path(__file__).resolve().parents[2]
MOTIF = re.compile(r'"(https?://[^"\s]+)"')
NAVIGATEUR = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36"

# Toutes les URL du produit ne sont pas des SOURCES. Une base d'API n'a pas de page
# a sa racine : un 404 y est le comportement normal, pas une preuve disparue.
PAS_UNE_SOURCE = {
    "https://api.keygen.sh/": "base d'API de licence : la racine n'est pas un endpoint",
}


def urls():
    vues = {}
    for f in (RACINE / "src").rglob("*.cs"):
        if "obj" in f.parts or "bin" in f.parts:
            continue
        for u in MOTIF.findall(f.read_text(encoding="utf-8", errors="ignore")):
            vues.setdefault(u, f.relative_to(RACINE).as_posix())
    return vues


def etat(url):
    requete = urllib.request.Request(url, headers={"User-Agent": NAVIGATEUR})
    try:
        with urllib.request.urlopen(requete, timeout=25) as r:
            return r.status, r.url
    except urllib.error.HTTPError as e:
        return e.code, url
    except Exception as e:                      # DNS, TLS, timeout
        return 0, f"{type(e).__name__}: {e}"


def main():
    morts = 0
    for url, fichier in sorted(urls().items()):
        if url in PAS_UNE_SOURCE:
            print(f"hors    --   {url}")
            print(f"          -> {PAS_UNE_SOURCE[url]}")
            continue
        code, final = etat(url)
        if code in (404, 410) or code == 0:
            marque, morts = "MORT   ", morts + 1
        elif code in (403, 429):
            marque = "BLOQUE "   # a rouvrir dans un navigateur, pas une conclusion
        elif final.rstrip("/") != url.rstrip("/"):
            marque = "PERIME "
        else:
            marque = "ok     "
        print(f"{marque} {code}  {url}")
        if marque == "PERIME ":
            print(f"          -> {final}   ({fichier})")
    verifiees = len(urls().keys() - PAS_UNE_SOURCE.keys())
    print(f"\n{verifiees} sources verifiees, {morts} morte(s).")
    return 1 if morts else 0


if __name__ == "__main__":
    sys.exit(main())
