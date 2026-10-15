"""L'icône de DrDXT : la bille de chrome du bouton central, sur une tuile noire arrondie.

La bille sur tuile. La bille est la vraie : capturée dans l'App
(bille.png, 212 px, détourée), contraste relevé pour tenir en petit. La tuile noire la détache sur une barre des tâches
claire comme sombre (Microsoft, « Design guidelines for Windows app icons » : contraste 3:1 sur les deux thèmes,
lumière en haut à gauche, aucun texte).

    python tools/icone/icone.py   (écrit src/DrDxt.App/Assets/drdxt.ico et tools/icone/apercu.png)
"""
import os
import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageEnhance

ICI = os.path.dirname(os.path.abspath(__file__))
SORTIE = os.path.join(ICI, "..", "..", "src", "DrDxt.App", "Assets", "drdxt.ico")
TAILLES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
L = Image.Resampling.LANCZOS
N = 1024


def bille(n):
    """La bille capturée, contraste relevé, avec un liseré clair plus fort en haut à gauche."""
    src = Image.open(os.path.join(ICI, "bille.png")).convert("RGBA")
    rgb = ImageEnhance.Brightness(ImageEnhance.Contrast(src.convert("RGB")).enhance(1.35)).enhance(1.12)
    im = rgb.convert("RGBA")
    im.putalpha(src.getchannel("A"))
    im = im.resize((n, n), L)
    lis = Image.new("L", (n * 4, n * 4), 0)
    ImageDraw.Draw(lis).ellipse((6, 6, n * 4 - 7, n * 4 - 7), outline=255, width=max(4, n // 40))
    lis = lis.resize((n, n), L)
    y, x = np.mgrid[0:n, 0:n]
    poids = np.clip(0.35 + 0.65 * ((n - x) + (n - y)) / (2 * n), 0, 1)
    clair = Image.new("RGBA", (n, n), (235, 235, 235, 0))
    clair.putalpha(Image.fromarray((np.array(lis) * poids).astype(np.uint8)))
    return Image.alpha_composite(im, clair)


def tuile(n, rayon=0.22):
    """Tuile noire arrondie, dégradé graphite à 120°, liseré fin gris."""
    y, x = np.mgrid[0:n, 0:n] / n
    v = (np.clip(0.11 - 0.07 * (0.6 * x + 0.8 * y), 0, 1) * 255).astype(np.uint8)
    fond = Image.fromarray(np.dstack([v, v, v + 2, np.full_like(v, 255)]).astype(np.uint8), "RGBA")
    m = Image.new("L", (n * 4, n * 4), 0)
    ImageDraw.Draw(m).rounded_rectangle((0, 0, n * 4 - 1, n * 4 - 1), radius=int(n * 4 * rayon), fill=255)
    t = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    t.paste(fond, (0, 0), m.resize((n, n), L))
    bord = Image.new("L", (n * 4, n * 4), 0)
    ImageDraw.Draw(bord).rounded_rectangle((2, 2, n * 4 - 3, n * 4 - 3), radius=int(n * 4 * rayon), outline=255,
                                           width=max(4, n // 64))
    clair = Image.new("RGBA", (n, n), (120, 120, 125, 0))
    clair.putalpha(ImageChops.multiply(bord.resize((n, n), L), Image.new("L", (n, n), 150)))
    return Image.alpha_composite(t, clair)


def icone(n):
    fond = tuile(N)
    b = bille(int(N * 0.74))
    fond.alpha_composite(b, ((N - b.width) // 2, (N - b.height) // 2))
    return fond.resize((n, n), L)


def main():
    images = [icone(n) for n in TAILLES]
    images[-1].save(SORTIE, format="ICO", sizes=[(n, n) for n in TAILLES], append_images=images[:-1])
    largeur = sum(TAILLES) + 12 * len(TAILLES)
    apercu = Image.new("RGBA", (largeur, 2 * 256 + 12), (0, 0, 0, 0))
    for fond, y0 in (((32, 32, 32, 255), 0), ((243, 243, 243, 255), 268)):
        bande = Image.new("RGBA", (largeur, 256), fond)
        x = 0
        for im in images:
            bande.paste(im, (x, (256 - im.height) // 2), im)
            x += im.width + 12
        apercu.paste(bande, (0, y0))
    apercu.save(os.path.join(ICI, "apercu.png"))


if __name__ == "__main__":
    main()
