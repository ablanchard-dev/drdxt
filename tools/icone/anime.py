"""Le logo de DrDXT animé : la même bille sur la même tuile, le chrome qui bouge comme dans l'App.

L'icône animée (Discord) : la même, avec ses effets. La bille n'est plus une capture figée : le shader
de l'App (src/DrDxt.App/Effets/BilleLiquide.hlsl) est porté ici ligne pour ligne, avec la même texture de studio, et rendu à
chaque instant ; puis le même traitement que l'icône (icone.py : contraste, liseré) sur la même tuile. Le temps avance comme
dans l'App au repos (0,9 unité par seconde). Le shader ne se répète jamais : la boucle est un aller-retour adouci, chaque image un vrai rendu.

    python tools/icone/anime.py   (écrit tools/icone/logo-anime.gif et tools/icone/logo-anime-apercu.png)
"""
import os
import numpy as np
from PIL import Image, ImageDraw, ImageEnhance

from icone import tuile, L

ICI = os.path.dirname(os.path.abspath(__file__))
STUDIO = np.asarray(Image.open(os.path.join(ICI, "..", "..", "src", "DrDxt.App", "Assets", "studio-chrome.png")).convert("RGB"),
                    dtype=np.float32)[..., 0] / 255.0
COTE, IPS, DUREE = 384, 20, 8.0
VITESSE = 0.9  # unités de temps du shader par seconde, la bille au repos (BilleDeChrome.Avancer)


def studio(u, v):
    """tex2D bilinéaire : u se répète (frac), v borné."""
    h, w = STUDIO.shape
    x = (u % 1.0) * w - 0.5
    y = np.clip(v, 0, 1) * h - 0.5
    x0 = np.floor(x).astype(int); y0 = np.floor(y).astype(int)
    fx = x - x0; fy = y - y0
    x0m, x1m = x0 % w, (x0 + 1) % w
    y0c, y1c = np.clip(y0, 0, h - 1), np.clip(y0 + 1, 0, h - 1)
    haut = STUDIO[y0c, x0m] * (1 - fx) + STUDIO[y0c, x1m] * fx
    bas = STUDIO[y1c, x0m] * (1 - fx) + STUDIO[y1c, x1m] * fx
    return haut * (1 - fy) + bas * fy


def saturer(x):
    return np.clip(x, 0.0, 1.0)


def smoothstep(a, b, x):
    t = saturer((x - a) / (b - a))
    return t * t * (3 - 2 * t)


def bille_shader(n, temps, luminosite=1.0):
    """BilleLiquide.hlsl, pixel pour pixel (taille = n, comme Taille = largeur réelle dans l'App). Rend RGBA prémultiplié."""
    v, u = (np.mgrid[0:n, 0:n] + 0.5) / n
    p = np.stack([u * 2 - 1, v * 2 - 1], -1)
    c = p / 0.955
    d = (c * c).sum(-1)
    alpha = saturer((1 - np.sqrt(d)) * n * 0.5)
    z = np.sqrt(saturer(1 - d))
    torsion = 0.55 * np.sin(temps * 0.28) + 1.3 * (1 - z)
    qx = c[..., 0] * np.cos(torsion) - c[..., 1] * np.sin(torsion)
    qy = c[..., 0] * np.sin(torsion) + c[..., 1] * np.cos(torsion)
    ox = np.sin(qy * 3.8 + temps * 0.55) + 0.38 * np.sin(qx * 6 - temps * 0.38)
    oy = np.cos(qx * 3.7 - temps * 0.48) + 0.35 * np.cos(qy * 5.5 + temps * 0.4)
    nrm = np.stack([c[..., 0] + ox * 0.32 * z, c[..., 1] + oy * 0.32 * z, z], -1)
    nrm /= np.linalg.norm(nrm, axis=-1, keepdims=True) + 1e-9
    # reflect(I, N) = I - 2 dot(N, I) N, I = (0, 0, -1)
    i = np.array([0.0, 0.0, -1.0])
    r = i - 2 * (nrm @ i)[..., None] * nrm
    turn = temps * 0.12
    rx = r[..., 0] * np.cos(turn) + r[..., 2] * np.sin(turn)
    ry = r[..., 1]
    rz = -r[..., 0] * np.sin(turn) + r[..., 2] * np.cos(turn)
    env_u = (np.arctan2(rz, rx) / 6.2831853 + 0.5) % 1.0
    env_v = np.arccos(np.clip(ry, -1, 1)) / 3.1415926
    reflet = studio(env_u, env_v)
    ride = ry + 0.42 * np.sin(rx * 3.4 + temps * 0.55) + 0.18 * np.cos(rz * 4.2 - temps * 0.4)
    voile = smoothstep(-0.38, -0.12, ride) - smoothstep(0.08, 0.34, ride)
    lisere = np.exp(-(((ride + 0.40) / 0.026) ** 2))
    metal = 0.18 + 0.18 * saturer(reflet) ** 1.2 + 0.38 * voile + 0.12 * lisere
    rim = 0.10 * (1 - z) ** 5
    lum = np.array([-0.36, -0.52, 0.82]); lum /= np.linalg.norm(lum)
    spec = 0.28 * saturer(nrm @ lum) ** 42
    valeur = saturer((metal * (0.7 + 0.3 * z) + rim + spec) * luminosite)
    rgb = valeur * alpha
    return np.dstack([rgb, rgb, rgb, alpha])


def bille(n, temps):
    """La bille rendue, puis le traitement de l'icône (icone.bille) : contraste, luminosité, liseré clair en haut à gauche."""
    f = bille_shader(n, temps)
    a = f[..., 3]
    droit = np.where(a[..., None] > 0, f[..., :3] / np.maximum(a[..., None], 1e-6), 0)  # dé-prémultiplier
    im = Image.fromarray((saturer(droit) * 255).astype(np.uint8), "RGB")
    im = ImageEnhance.Brightness(ImageEnhance.Contrast(im).enhance(1.35)).enhance(1.12).convert("RGBA")
    im.putalpha(Image.fromarray((a * 255).astype(np.uint8), "L"))
    lis = Image.new("L", (n * 4, n * 4), 0)
    ImageDraw.Draw(lis).ellipse((6, 6, n * 4 - 7, n * 4 - 7), outline=255, width=max(4, n // 40))
    lis = lis.resize((n, n), L)
    y, x = np.mgrid[0:n, 0:n]
    poids = np.clip(0.35 + 0.65 * ((n - x) + (n - y)) / (2 * n), 0, 1)
    clair = Image.new("RGBA", (n, n), (235, 235, 235, 0))
    clair.putalpha(Image.fromarray((np.array(lis) * poids).astype(np.uint8)))
    return Image.alpha_composite(im, clair)


def image(fond, temps, sur=3):
    """Une image du logo : rendue en triple taille puis réduite (bords fins)."""
    n = COTE * sur
    b = bille(int(n * 0.74), temps)
    f = fond.copy()
    f.alpha_composite(b, ((n - b.width) // 2, (n - b.height) // 2))
    return f.resize((COTE, COTE), L)


def en_gris_exact(im):
    """Le logo est gris : 255 niveaux de gris exacts et un index transparent, sans tramage (une palette choisie par
    quantification tramait les dégradés du chrome : flou, bruit)."""
    a = np.asarray(im.convert("RGBA"), dtype=np.float32)
    gris = (0.299 * a[..., 0] + 0.587 * a[..., 1] + 0.114 * a[..., 2]) * 254 / 255
    idx = np.where(a[..., 3] < 128, 255, np.rint(gris)).astype(np.uint8)
    p = Image.fromarray(idx, "P")
    p.putpalette([v for k in range(255) for v in (round(k * 255 / 254),) * 3] + [0, 0, 0])
    p.info["transparency"] = 255
    return p


def main():
    fond = tuile(COTE * 3)
    # Aller-retour adouci : le chrome ralentit, s'arrête et repart en sens inverse ; chaque image est un vrai rendu du
    # shader (un fondu entre deux instants superposait deux images : un fantôme).
    aller = int(DUREE * IPS / 2)
    amplitude = VITESSE * DUREE / 2 * 2 / np.pi * 2  # même vitesse moyenne que l'App sur l'aller
    temps = [amplitude * (1 - np.cos(np.pi * k / aller)) / 2 for k in range(aller + 1)]
    rendues = [image(fond, t) for t in temps]
    boucle = rendues + rendues[-2:0:-1]
    gifs = [en_gris_exact(im) for im in boucle]
    sortie = os.path.join(ICI, "logo-anime.gif")
    # Compression : seuls les pixels qui changent d'une image à l'autre sont réécrits (palette inchangée, aucun tramage).
    gifs[0].save(sortie, save_all=True, append_images=gifs[1:], duration=int(1000 / IPS), loop=0, disposal=1,
                 optimize=False)
    planche = Image.new("RGBA", (COTE * 4, COTE), (32, 32, 32, 255))
    for i, k in enumerate((0, aller // 3, 2 * aller // 3, aller)):
        planche.paste(boucle[k], (i * COTE, 0), boucle[k])
    planche.save(os.path.join(ICI, "logo-anime-apercu.png"))
    print(sortie, f"{os.path.getsize(sortie) / 1e6:.1f} Mo", f"{len(boucle)} images, {len(boucle) / IPS:g} s")


if __name__ == "__main__":
    main()
