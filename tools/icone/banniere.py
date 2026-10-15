"""La bannière du README : la bille de l'App, rendue par le même shader que le logo animé, et le nom en Unbounded.

Rien de dessiné à la main : la bille sort de anime.bille (BilleLiquide.hlsl porté ligne pour ligne), la police est celle
de l'App (src/DrDxt.App/Assets/Fonts). Rendue en double taille pour les écrans denses ; GitHub la réduit à la largeur
de la page.

    python tools/icone/banniere.py   (écrit docs/images/banniere.png et docs/images/banniere-fr.png)
"""
import os
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

from anime import bille

ICI = os.path.dirname(os.path.abspath(__file__))
RACINE = os.path.join(ICI, "..", "..")
POLICES = os.path.join(RACINE, "src", "DrDxt.App", "Assets", "Fonts")
IMAGES = os.path.join(RACINE, "docs", "images")
TEXTES = {
    "banniere.png": ("The honest PC optimizer for gamers.",
                     "Measured on your matches  ·  Every change undoable  ·  Free and open source"),
    "banniere-fr.png": ("L'optimiseur PC honnête pour les joueurs.",
                        "Mesuré sur tes parties  ·  Chaque réglage se défait  ·  Libre et gratuit"),
}

LARGEUR, HAUTEUR = 2560, 800
OBSIDIENNE = np.array([6, 7, 10], dtype=np.float32)
# L'irisation de la DA (DESIGN.md) : un seul filet, le reste reste chrome.
IRIS = [(61, 245, 255), (139, 92, 255), (255, 79, 216), (255, 181, 71)]


def fond():
    """Obsidienne, avec la lumière du studio qui vient de la bille (à droite) et s'éteint vers le texte."""
    y, x = np.mgrid[0:HAUTEUR, 0:LARGEUR].astype(np.float32)
    d = np.hypot((x - LARGEUR * 0.78) / LARGEUR, (y - HAUTEUR * 0.5) / LARGEUR)
    halo = np.exp(-(d / 0.30) ** 2) * 22
    rgb = OBSIDIENNE + halo[..., None] * np.array([1.0, 1.0, 1.08])
    return Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), "RGB").convert("RGBA")


def filet_irise(largeur, epaisseur):
    """Un dégradé cyan, violet, magenta, ambre, qui s'efface aux deux bouts."""
    t = np.linspace(0, 1, largeur)
    pos = t * (len(IRIS) - 1)
    i = np.minimum(pos.astype(int), len(IRIS) - 2)
    f = (pos - i)[:, None]
    a, b = np.array(IRIS)[i], np.array(IRIS)[i + 1]
    rgb = a * (1 - f) + b * f
    alpha = np.clip(np.minimum(t, 1 - t) * 6, 0, 1) * 255
    ligne = np.column_stack([rgb, alpha])
    return Image.fromarray(np.repeat(ligne[None], epaisseur, 0).astype(np.uint8), "RGBA")


def texte_chrome(texte, police):
    """Le nom en dégradé de chrome vertical : clair en haut, plus sombre en bas, comme le titre de l'accueil."""
    x0, y0, x1, y1 = police.getbbox(texte)
    masque = Image.new("L", (x1, y1), 0)
    ImageDraw.Draw(masque).text((0, 0), texte, font=police, fill=255)
    v = np.linspace(0, 1, y1)[:, None]
    gris = 250 - 95 * np.clip((v - y0 / y1) / (1 - y0 / y1), 0, 1) ** 1.4
    rgb = np.repeat(np.repeat(gris, x1, 1)[..., None], 3, 2)
    im = Image.fromarray(rgb.astype(np.uint8), "RGB").convert("RGBA")
    im.putalpha(masque)
    return im.crop((x0, y0, x1, y1))


def rendre(fichier, slogan_texte, ligne_texte):
    im = fond()

    diametre = int(HAUTEUR * 0.86)
    b = bille(diametre, 2.6)
    cx, cy = int(LARGEUR * 0.79), HAUTEUR // 2
    ombre = Image.new("RGBA", im.size, (0, 0, 0, 0))
    ImageDraw.Draw(ombre).ellipse((cx - diametre * 0.56, cy - diametre * 0.56, cx + diametre * 0.56, cy + diametre * 0.56),
                                  fill=(200, 210, 230, 34))
    im.alpha_composite(ombre.filter(ImageFilter.GaussianBlur(90)))
    im.alpha_composite(b, (cx - diametre // 2, cy - diametre // 2))

    gauche = 150
    nom = texte_chrome("DrDXT", ImageFont.truetype(os.path.join(POLICES, "Unbounded-800.ttf"), 210))
    im.alpha_composite(nom, (gauche, 200))
    im.alpha_composite(filet_irise(nom.width, 4), (gauche, 200 + nom.height + 46))

    d = ImageDraw.Draw(im)
    slogan = ImageFont.truetype(os.path.join(POLICES, "Unbounded-400.ttf"), 52)
    d.text((gauche, 200 + nom.height + 92), slogan_texte, font=slogan, fill=(226, 228, 234))
    petit = ImageFont.truetype(os.path.join(POLICES, "Unbounded-400.ttf"), 30)
    d.text((gauche, 200 + nom.height + 176), ligne_texte,
           font=petit, fill=(140, 144, 156))

    os.makedirs(IMAGES, exist_ok=True)
    im.convert("RGB").save(os.path.join(IMAGES, fichier), optimize=True)


def main():
    for fichier, (slogan_texte, ligne_texte) in TEXTES.items():
        rendre(fichier, slogan_texte, ligne_texte)
        print(os.path.join(IMAGES, fichier))


if __name__ == "__main__":
    main()
