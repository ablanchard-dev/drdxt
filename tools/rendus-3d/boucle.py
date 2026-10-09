# Rendu de la boucle de verre liquide et des 5 billes des composants, sur la géométrie exacte de l'accueil de DrDXT
# (réseau dessiné sur 660 x 720, pôles à 37 % du centre, premier pôle en haut). Fond transparent, verre transparent :
# le fond de l'appli se voit au travers. Usage :
#   blender -b -P boucle.py -- <fichier.png> <echelle> <echantillons>
import bpy, math, sys, os

args = sys.argv[sys.argv.index("--") + 1:]
fichier, echelle, echantillons = args[0], float(args[1]), int(args[2])
L, H, POLES, RAYON = 660, 720, 5, 0.37

bpy.ops.wm.read_factory_settings(use_empty=True)
sc = bpy.context.scene
sc.render.engine = "CYCLES"
sc.cycles.device = "CPU"
sc.cycles.samples = echantillons
sc.cycles.use_denoising = True
sc.render.film_transparent = True
sc.cycles.film_transparent_glass = True
sc.render.resolution_x, sc.render.resolution_y = int(L * echelle), int(H * echelle)
sc.render.image_settings.file_format = "PNG"
sc.render.image_settings.color_mode = "RGBA"
sc.view_settings.view_transform = "Standard"


def px(x, y, z=0.0):
    """Un point de l'écran (pixels du réseau, y vers le bas) en unités de la scène (1 unité = 100 px)."""
    return ((x - L / 2) / 100, -(y - H / 2) / 100, z / 100)


# Le monde reflété : le même que celui de la bille (noir, traînées de lumière larges).
monde = bpy.data.worlds.new("Monde")
sc.world = monde
monde.use_nodes = True
n, l = monde.node_tree.nodes, monde.node_tree.links
n.clear()
coord = n.new("ShaderNodeTexCoord")
bruit = n.new("ShaderNodeTexNoise")
bruit.inputs["Scale"].default_value = 0.55
bruit.inputs["Detail"].default_value = 1.0
bruit.inputs["Distortion"].default_value = 1.2
rampe = n.new("ShaderNodeValToRGB")
e = rampe.color_ramp.elements
e[0].position, e[0].color = 0.46, (0, 0, 0, 1)
e[1].position, e[1].color = 0.53, (1, 1, 1, 1)
e3 = e.new(0.62); e3.color = (0.05, 0.05, 0.06, 1)
rampe.color_ramp.interpolation = "EASE"
sep = n.new("ShaderNodeSeparateXYZ")
haut = n.new("ShaderNodeMapRange")
haut.inputs["To Min"].default_value = 0.02
haut.inputs["To Max"].default_value = 0.12
ajout = n.new("ShaderNodeMix"); ajout.data_type = "RGBA"; ajout.blend_type = "ADD"; ajout.inputs["Factor"].default_value = 1.0
fond = n.new("ShaderNodeBackground"); fond.inputs["Strength"].default_value = 2.2
out = n.new("ShaderNodeOutputWorld")
l.new(coord.outputs["Generated"], bruit.inputs["Vector"])
l.new(bruit.outputs["Fac"], rampe.inputs["Fac"])
l.new(coord.outputs["Generated"], sep.inputs["Vector"])
l.new(sep.outputs["Z"], haut.inputs["Value"])
l.new(rampe.outputs["Color"], ajout.inputs[6])
l.new(haut.outputs["Result"], ajout.inputs[7])
l.new(ajout.outputs[2], fond.inputs["Color"])
l.new(fond.outputs["Background"], out.inputs["Surface"])

bpy.ops.object.light_add(type="POINT", location=(-2.4, 2.8, 3.4))
bpy.context.object.data.energy = 2600
bpy.context.object.data.shadow_soft_size = 0.45

# Deux bandes de lumière rasantes : elles tracent les liserés clairs le long des tubes de verre.
for nom, lieu, rot in [("Gauche", (-6.0, 0.5, 1.2), (0, math.radians(-78), 0)), ("Droite", (6.0, -0.5, 1.2), (0, math.radians(78), 0))]:
    bpy.ops.object.light_add(type="AREA", location=lieu, rotation=rot)
    o = bpy.context.object
    o.data.shape = "RECTANGLE"
    o.data.size, o.data.size_y = 1.2, 12.0
    o.data.energy = 3500

bpy.ops.object.camera_add(location=(0, 0, 10))
cam = bpy.context.object
cam.data.type = "ORTHO"
cam.data.ortho_scale = max(L, H) / 100
sc.camera = cam


def materiau_bille(phase):
    """La matière de la bille (laque noire et chrome le long d'un tourbillon en S), comme bille.py."""
    mat = bpy.data.materials.new("Bille")
    mat.use_nodes = True
    n, l = mat.node_tree.nodes, mat.node_tree.links
    n.clear()
    tc = n.new("ShaderNodeTexCoord")
    sp = n.new("ShaderNodeSeparateXYZ"); l.new(tc.outputs["Object"], sp.inputs["Vector"])
    def m(op, a=None, b=None, va=None, vb=None):
        x = n.new("ShaderNodeMath"); x.operation = op
        if a is not None: l.new(a, x.inputs[0])
        if b is not None: l.new(b, x.inputs[1])
        if va is not None: x.inputs[0].default_value = va
        if vb is not None: x.inputs[1].default_value = vb
        return x.outputs[0]
    ang = m("ARCTAN2", sp.outputs["Y"], sp.outputs["X"])
    r = m("SQRT", m("ADD", m("MULTIPLY", sp.outputs["X"], sp.outputs["X"]), m("MULTIPLY", sp.outputs["Y"], sp.outputs["Y"])))
    tors = m("MULTIPLY", m("POWER", m("SUBTRACT", None, r, va=1.0), vb=2.0), vb=3.2)
    phi = m("ADD", m("ADD", ang, tors), vb=phase)
    sa = m("ABSOLUTE", m("SINE", phi))
    bras = n.new("ShaderNodeMapRange"); bras.interpolation_type = "SMOOTHSTEP"
    bras.inputs["From Min"].default_value, bras.inputs["From Max"].default_value = 0.22, 0.42
    bras.inputs["To Min"].default_value, bras.inputs["To Max"].default_value = 1.0, 0.0
    l.new(sa, bras.inputs["Value"])
    coeur = n.new("ShaderNodeMapRange"); coeur.interpolation_type = "SMOOTHSTEP"
    coeur.inputs["From Min"].default_value, coeur.inputs["From Max"].default_value = 0.05, 0.2
    l.new(r, coeur.inputs["Value"])
    masque = m("MULTIPLY", bras.outputs["Result"], coeur.outputs["Result"])
    laque = n.new("ShaderNodeBsdfPrincipled")
    laque.inputs["Base Color"].default_value = (0.012, 0.013, 0.016, 1)
    laque.inputs["Roughness"].default_value = 0.035
    chrome = n.new("ShaderNodeBsdfPrincipled")
    chrome.inputs["Base Color"].default_value = (0.92, 0.93, 0.95, 1)
    chrome.inputs["Metallic"].default_value = 1.0
    chrome.inputs["Roughness"].default_value = 0.08
    mix = n.new("ShaderNodeMixShader")
    l.new(masque, mix.inputs["Fac"]); l.new(laque.outputs["BSDF"], mix.inputs[1]); l.new(chrome.outputs["BSDF"], mix.inputs[2])
    o = n.new("ShaderNodeOutputMaterial"); l.new(mix.outputs["Shader"], o.inputs["Surface"])
    return mat


# Le verre liquide : clair, poli, un rien fumé.
verre = bpy.data.materials.new("Verre")
verre.use_nodes = True
pb = verre.node_tree.nodes["Principled BSDF"]
pb.inputs["Base Color"].default_value = (0.9, 0.91, 0.93, 1)
pb.inputs["Metallic"].default_value = 1.0
pb.inputs["Roughness"].default_value = 0.05
pb.inputs["IOR"].default_value = 1.9

# Les pôles, comme Orbite.Position : premier en haut, sens horaire à l'écran.
centre = (L / 2, H / 2)
poles = []
for i in range(POLES):
    a = math.radians(-90 + 360 * i / POLES)
    poles.append((centre[0] + math.cos(a) * L * RAYON, centre[1] + math.sin(a) * H * RAYON))


def cubique(a, c1, c2, b, t):
    u = 1 - t
    return tuple(u ** 3 * a[k] + 3 * u * u * t * c1[k] + 3 * u * t * t * c2[k] + t ** 3 * b[k] for k in range(2))


# La boucle : chaque tube part de sa bille vers le centre, puis s'incurve vers la voisine (comme TubesDeChrome).
for i in range(POLES):
    a, b = poles[i], poles[(i + 1) % POLES]
    c1 = (a[0] + (centre[0] - a[0]) * 0.3, a[1] + (centre[1] - a[1]) * 0.3)
    c2 = (b[0] + (centre[0] - b[0]) * 0.3, b[1] + (centre[1] - b[1]) * 0.3)
    courbe = bpy.data.curves.new(f"Tube{i}", "CURVE")
    courbe.dimensions = "3D"
    courbe.bevel_depth = 0.078
    courbe.bevel_resolution = 8
    courbe.use_fill_caps = True
    spl = courbe.splines.new("POLY")
    N = 120
    spl.points.add(N)
    for k in range(N + 1):
        t = k / N
        x, y = cubique(a, c1, c2, b, t)
        # Bombé vers l'écran au milieu ; gonflé juste avant chaque bille, comme du verre coulé.
        spl.points[k].co = (*px(x, y, 18 * math.sin(math.pi * t)), 1)
        spl.points[k].radius = 1.0 + 0.55 * abs(2 * t - 1) ** 6
    tube = bpy.data.objects.new(f"Tube{i}", courbe)
    sc.collection.objects.link(tube)
    tube.data.materials.append(verre)
    bpy.context.view_layer.objects.active = tube

# Les billes des composants, chacune tournée autrement.
for i, p in enumerate(poles):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=96, ring_count=48, radius=0.24, location=px(p[0], p[1], 6))
    o = bpy.context.object
    bpy.ops.object.shade_smooth()
    o.data.materials.append(materiau_bille(i * 1.3))

for o in sc.objects:
    if o.type == "CURVE":
        o.select_set(True)
sc.render.filepath = fichier
bpy.ops.render.render(write_still=True)
print("FINI")
