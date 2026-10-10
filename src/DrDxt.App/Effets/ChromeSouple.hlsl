// Les huit liens de la bille vers les composants (cinq jusqu'au 10/10/2026 : latence, réseau et BIOS les ont rejoints). 08/10/2026 (le mainteneur) : « mi-ruban, mi ce qu'on a, et détaché du fond »,
// puis « on voit le fond par-dessous le voile, faut pas » :
//   - un tuyau de verre sombre et PLEIN : le fond ne passe jamais au travers ; bords clairs, un liseré de lumière ;
//   - dedans, un ruban de chrome qui se vrille : sa largeur suit la torsion, des reflets nets coulent le long ;
//   - dessous, une ombre douce qui le détache du chrome clair du fond.
// Le tuyau et le ruban s'effilent vers leurs bouts au lieu de devenir transparents.
// 08/10/2026 (critique) : corps presque noir, la lumière seulement sur le ruban et le liseré ; ils naissent sous la bille
// même réduite après le scan (0,12), et s'arrêtent avant le libellé du composant.
// 08/10/2026 (le mainteneur : « processeur trop courte, GPU, Windows aussi, faut que ce soit cohérent ») : chaque tentacule vise
// l'icône de son composant (le pôle même, plus 0,25 plus bas) et s'y branche dans une douille de chrome : un disque de
// verre noir à liseré clair, comme un câble dans son port. Le tuyau garde son épaisseur jusqu'à la douille.
// Sortie en alpha prémultiplié. Compilé en ps_3_0 par Effets/compiler-shaders.ps1.
sampler2D Entree : register(s0);
float Temps : register(c0);
float4 Teinte : register(c1);  // la couleur du métal, pour le séparer du fond
float Activite : register(c2); // 0 au repos, 1 pendant le scan : des influx de lumière remontent des composants vers la bille
// 10/10/2026 (le mainteneur : « un effet par le dessous, mais maîtrisé ») : le composant sous la souris (-1 : aucun) et son
// éclat (0 à 1, monté en ~150 ms, descendu en ~250 ms par BranchesDeChrome) : sa douille et le bord de son tuyau s'allument.
float Survol : register(c3);
float Eclat : register(c4);

// Le canevas fait 760 x 720 (10/10/2026) : p va de -1 à 1 en largeur, de -0,947 à 0,947 en hauteur.
static const float Hauteur = 1.8947368;
// Les pôles : RayonX et RayonY 0,37 de la vue, ramenés à p (aucune carte ne touche le bord, sinon elle se décale de sa douille).
static const float2 Rayon = float2(.74, .66316);
// Les tailles d'avant (canevas de 660) ramenées au nouveau : 660 / 760.
static const float K = .868421;

// 10/10/2026 : chaque carte est posée par son icône (PanneauOrbital.CentreIcone) : l'icône est SUR son pôle, avant comme
// après le scan. Plus de recalage mesuré à la main, qui se décalait dès que le texte d'une carte changeait de hauteur.

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 p = (uv - .5) * float2(2.0, Hauteur);
    float ombre = 0.0, tuyau = 0.0, verre = 0.0, ruban = 0.0, metal = 0.0, douille = 0.0, acier = 0.0;
    [loop] for (int i = 0; i < 8; i++)
    {
        float a = -1.5707963 + float(i) * .78539816;
        float2 cible = float2(cos(a), sin(a)) * Rayon;
        float longueur = length(cible);
        float2 axe = cible / longueur;
        float u = dot(p, axe), v = dot(p, float2(-axe.y, axe.x));
        float s = saturate((u - .105) / (longueur - .105));
        float ouverture = sin(s * 3.14159265);
        float phase = float(i) * 1.73;
        float vise = Eclat * step(abs(float(i) - Survol), .5);
        // Né sous la bille, il court jusqu'au centre de la douille, qui le recouvre.
        float effile = smoothstep(.105, .19, u) * (1.0 - smoothstep(longueur - .01, longueur, u));
        float centre = ouverture * (.08 * sin(s * 4.2 - Temps * .38 + phase) + .04 * cos(s * 7.0 + Temps * .24 + phase));
        float ecart = v - centre;

        // Le tuyau : plein, sombre au cœur, éclairé vers ses bords, un liseré net juste avant le bord.
        float rayonTube = K * (.022 + .036 * ouverture + .006 * sin(s * 6.0 + Temps * .5 + phase)) * effile + .0001;
        float dt = abs(ecart) / rayonTube;
        float t = (1.0 - smoothstep(.90, 1.0, dt)) * step(.02, effile);
        // L'influx : une lueur qui part du composant (s = 1) et rejoint la bille (s = 0), deux par tuyau, décalées.
        float p1 = 1.0 - frac(Temps * .22 + phase * .37), p2 = 1.0 - frac(Temps * .22 + phase * .37 + .5);
        float influx = Activite * (exp(-pow((s - p1) / .05, 2)) + exp(-pow((s - p2) / .05, 2)));
        float bordTube = .015 + .10 * pow(saturate(dt), 6.0) + (.40 + .35 * vise) * exp(-pow((dt - .94) / .03, 2)) + .45 * influx;
        if (t > tuyau) { tuyau = t; verre = bordTube; }
        ombre = max(ombre, exp(-pow(ecart / (rayonTube * 1.8 + .03), 2)) * effile);

        // Le ruban : de face il est large et clair, de profil il s'amincit et s'assombrit.
        float torsion = sin(s * 5.5 - Temps * .65 + phase);
        float demi = rayonTube * (.22 + .48 * abs(torsion));
        float d = ecart / demi;
        float corps = (1.0 - smoothstep(.82, 1.0, abs(d))) * step(.02, effile);
        float face = .5 + .5 * torsion;
        float coule = sin(s * 16.0 - Temps * 1.1 + d * 2.5 + phase);
        float eclat = .05 + .12 * face * (1.0 - .7 * d * d) + .70 * exp(-pow((d - .35 * torsion) / .12, 2)) * (.5 + .5 * face)
                    + .14 * smoothstep(.6, 1.0, coule);
        float lisere = exp(-pow((abs(d) - .88) / .07, 2));
        float m = saturate(eclat + .35 * lisere + .6 * influx);
        if (corps > ruban) { ruban = corps; metal = m; }

        // La douille : verre noir, un liseré net, un reflet en haut à gauche ; elle s'éclaire quand un influx en part.
        float ds = length(p - cible), R = .050 * K;
        float couvre = 1.0 - smoothstep(R - .003, R + .003, ds);
        float bord = exp(-pow((ds - R + .006) / .006, 2));
        float2 q = (p - cible) / R;
        float reflet = exp(-pow((length(q - float2(-.35, -.45)) - .55) / .12, 2)) * saturate(-q.y - q.x);
        float c = .02 + .08 * vise + (.55 + .45 * vise) * bord + .20 * reflet + .35 * Activite * (.5 + .5 * sin(Temps * 2.4 + phase)) * bord;
        if (couvre > douille) { douille = couvre; acier = c; }
        ombre = max(ombre, exp(-pow(ds / (R * 1.7), 2)));
    }

    // L'ombre (noire), puis le tuyau plein, puis le ruban, chacun posé sur le précédent.
    float alpha = .65 * ombre;
    float couleur = 0.0;
    couleur = couleur * (1.0 - tuyau) + tuyau * verre;
    alpha = alpha * (1.0 - tuyau) + tuyau;
    couleur = couleur * (1.0 - ruban) + ruban * metal;
    alpha = alpha * (1.0 - ruban) + ruban;
    couleur = couleur * (1.0 - douille) + douille * acier;
    alpha = alpha * (1.0 - douille) + douille;
    // L'ombre reste noire ; seul ce qui brille prend la teinte.
    return float4(couleur * Teinte.rgb, alpha);
}
