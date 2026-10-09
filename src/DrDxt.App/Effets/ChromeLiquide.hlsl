// Chrome liquide : champ de métal qui coule, irisé.
//
// 08/10/2026 (PUB1) : réécrit. La version précédente reprenait la formule du fond « Liquid Chrome » de React Bits, publié
// sous MIT avec la Commons Clause, incompatible avec la GPL de DrDXT. Celle-ci est propre au projet : huit tours, chacun
// décale le plan par une onde sinus de fréquence croissante puis le tourne ; le chrome est lu sur les crêtes d'un cosinus
// du plan déformé. Comparé à l'ancien rendu image par image avant de le remplacer (même métal plissé et turbulent).
//
// Un échantillon par pixel ; le lissage est rendu par le rendu à échelle réduite côté WPF (BitmapCache). Irisation en
// couche mince (cyan, violet, magenta) mêlée au chrome, réglable.
//
// Compilé en ps_3_0 par fxc (Windows SDK) : voir Effets/compiler-shaders.ps1.

sampler2D Entree : register(s0);

float Temps       : register(c0);
float Rapport     : register(c1);  // largeur / hauteur de l'élément
float Amplitude   : register(c2);
float Frequence   : register(c3);
float Irisation   : register(c4);  // 0 = chrome pur, 1 = pleine couleur
float Luminosite  : register(c5);
float4 Teinte     : register(c6);  // couleur du métal au repos

static const float TAU = 6.2831853;

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 p = uv * 2.0 - 1.0;
    p.x *= Rapport;

    // Huit tours : une onde décale x par y et y par x, puis le plan tourne de 0,61 rad. La fréquence croît et l'amplitude
    // décroît d'un tour à l'autre : les grands plis d'abord, les tourbillons fins ensuite.
    const float c = cos(0.61), sn = sin(0.61);
    for (float k = 0.0; k < 8.0; k += 1.0)
    {
        float f = Frequence * (1.0 + 0.9 * k);
        float a = Amplitude * 0.95 / (1.0 + 0.9 * k);
        float2 d = float2(sin(p.y * f + Temps * 0.9 + 1.7 * k), sin(p.x * f - Temps * 0.8 + 0.9 * k));
        p += a * d;
        p = float2(c * p.x - sn * p.y, sn * p.x + c * p.y);
    }

    // Le chrome : des crêtes fines là où le sinus passe par zéro. Plancher lisse (racine) plutôt que
    // max() : la cassure nette du max faisait des bords en escalier blanc une fois le rendu réduit
    // agrandi (vu sur les photos de l'accueil, 29/09/2026). Pas de fwidth : en ps_3_0 il est constant
    // par bloc de 2x2 pixels, et ses marches se voyaient autant.
    float s = cos(1.1 * p.x + 0.7 * p.y - Temps);
    float crete = 0.11 / sqrt(s * s + 0.09 * 0.09);
    crete = smoothstep(0.0, 1.0, saturate(crete));

    // La couleur de couche mince, qui glisse avec la déformation, bornée aux trois teintes de
    // l'interface (cyan, violet, magenta) : le cosinus d'origine parcourait tout le cercle des
    // couleurs et passait par le jaune et le vert.
    float t = frac(p.x * 0.18 + p.y * 0.11 + Temps * 0.07) * 3.0;
    float3 cyan = float3(0.24, 0.90, 1.00), violet = float3(0.58, 0.44, 1.00), magenta = float3(1.00, 0.36, 0.78);
    float3 iris = t < 1.0 ? lerp(cyan, violet, t) : t < 2.0 ? lerp(violet, magenta, t - 1.0) : lerp(magenta, cyan, t - 2.0);

    float3 metal = Teinte.rgb * (0.30 + 0.70 * crete) + crete * crete * 1.05;
    float3 couleur = lerp(metal, metal * (0.2 + iris * 1.7), Irisation);

    // Courbe douce vers 0,9 plutôt que saturate : le blanc pur brûlait les étiquettes posées sur le
    // chrome, et un plafond net faisait un plateau aux bords durs.
    return float4(0.9 * (1.0 - exp(-1.6 * max(couleur * Luminosite, 0.0))), 1.0);
}
