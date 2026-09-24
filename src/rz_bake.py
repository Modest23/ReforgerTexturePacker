# Reforger Texture Packer - model bake helper, run by Blender in the background:
#   blender --background --factory-startup --python rz_bake.py -- probe <model> <out.json>
#   blender --background --factory-startup --python rz_bake.py -- bake <model> <outdir> <w> <h> <tile> <mat1|mat2|...>
# Bakes, for the faces of the chosen materials inside one UDIM tile:
#   ao.png        Cycles ambient occlusion (the rest of the model still occludes)
#   normal.png    world-space normal, Blender Z-up (R,G,B = X,Y,Z * 0.5 + 0.5)
#   height.png    world position Z, 0 = lowest point of the whole model, 1 = highest
# Progress lines start with "RZBAKE ".
import bpy, bmesh, sys, json, os, math

def log(*a):
    print("RZBAKE", *a, flush=True)

argv = sys.argv[sys.argv.index("--") + 1:]
mode, model = argv[0], argv[1]

# ---- load ----------------------------------------------------------------------------
for o in list(bpy.data.objects):
    bpy.data.objects.remove(o, do_unlink=True)
ext = os.path.splitext(model)[1].lower()
if ext == ".fbx":
    bpy.ops.import_scene.fbx(filepath=model)
elif ext == ".obj":
    if hasattr(bpy.ops.wm, "obj_import"):
        bpy.ops.wm.obj_import(filepath=model)
    else:
        bpy.ops.import_scene.obj(filepath=model)
elif ext == ".blend":
    with bpy.data.libraries.load(model, link=False) as (src, dst):
        dst.objects = list(src.objects)
    for o in dst.objects:
        if o is not None:
            bpy.context.scene.collection.objects.link(o)
else:
    raise SystemExit("Unsupported model type: " + ext)

def usable(o):
    if o.type != "MESH" or not o.data.uv_layers:
        return False
    # skip hidden helpers / colliders from .blend files
    return not o.hide_render and not o.hide_get()

# ---- "mesh" mode: triangles for the tool's 3D preview ---------------------------------
tris = []  # (mat, tile, [(x,y,z, nx,ny,nz, u,v) * 3])

def collect_triangles(me):
    me.calc_loop_triangles()
    # shading normals incl. custom/split normals: API differs between Blender 3.x and 4.1+
    # (3.6 has an empty corner_normals collection, so check the length too)
    if hasattr(me, "corner_normals") and len(me.corner_normals) == len(me.loops):
        cn = [c.vector for c in me.corner_normals]
    else:
        me.calc_normals_split()
        cn = [l.normal for l in me.loops]
    uvd = me.uv_layers.active.data
    for t in me.loop_triangles:
        corners = []
        us = vs = 0.0
        for li in t.loops:
            co = me.vertices[me.loops[li].vertex_index].co
            n = cn[li]
            uvv = uvd[li].uv
            us += uvv.x; vs += uvv.y
            corners.append((co.x, co.y, co.z, n.x, n.y, n.z, uvv.x, uvv.y))
        tile = 1001 + int(math.floor(us / 3.0)) + 10 * int(math.floor(vs / 3.0))
        tris.append((t.material_index, tile, corners))

# ---- flatten everything into one world-space mesh (modifiers + armature pose applied) --
depsgraph = bpy.context.evaluated_depsgraph_get()
bm = bmesh.new()
mat_names = []
for o in [o for o in bpy.context.scene.objects if usable(o)]:
    ev = o.evaluated_get(depsgraph)
    me = bpy.data.meshes.new_from_object(ev, preserve_all_data_layers=True, depsgraph=depsgraph)
    me.transform(o.matrix_world)
    # remap material slots to a global name list
    remap = []
    for slot in o.material_slots:
        n = slot.material.name if slot.material else "(none)"
        if n not in mat_names:
            mat_names.append(n)
        remap.append(mat_names.index(n))
    if not remap:
        if "(none)" not in mat_names:
            mat_names.append("(none)")
        remap = [mat_names.index("(none)")]
    for p in me.polygons:
        p.material_index = remap[min(p.material_index, len(remap) - 1)]
    # the render UV map is the one the textures were painted on
    for uv in me.uv_layers:
        if uv.active_render:
            me.uv_layers.active = uv
            break
    first = me.uv_layers.active.name
    for uv in list(me.uv_layers):
        if uv.name != first:
            me.uv_layers.remove(uv)
    me.uv_layers.active.name = "UVMap"
    if mode == "mesh":
        collect_triangles(me)
    else:
        bm.from_mesh(me)
    bpy.data.meshes.remove(me)

if mode == "mesh":
    # binary: "RZM1", nMats, [len, utf8 name], nTris, [mat, tile, 3 x (pos3, nrm3, uv2)] - little endian, Blender Z-up
    import struct
    with open(argv[2], "wb") as fh:
        fh.write(b"RZM1")
        fh.write(struct.pack("<i", len(mat_names)))
        for n in mat_names:
            b = n.encode("utf-8")
            fh.write(struct.pack("<i", len(b)))
            fh.write(b)
        fh.write(struct.pack("<i", len(tris)))
        for m, tl, cs in tris:
            fh.write(struct.pack("<ii", m, tl))
            for c in cs:
                fh.write(struct.pack("<8f", *c))
    log("MESH", len(tris))
    sys.exit(0)

uv = bm.loops.layers.uv.get("UVMap") or bm.loops.layers.uv.active
if uv is None or not bm.faces:
    raise SystemExit("No UV-mapped mesh found in " + model)

def face_tile(f):
    u = sum(l[uv].uv.x for l in f.loops) / len(f.loops)
    v = sum(l[uv].uv.y for l in f.loops) / len(f.loops)
    return 1001 + int(math.floor(u)) + 10 * int(math.floor(v))

# ---- probe: list materials and the UDIM tiles they use --------------------------------
if mode == "probe":
    info = {}
    for f in bm.faces:
        n = mat_names[f.material_index] if f.material_index < len(mat_names) else "(none)"
        e = info.setdefault(n, {"faces": 0, "tiles": set()})
        e["faces"] += 1
        e["tiles"].add(face_tile(f))
    out = [{"name": k, "faces": v["faces"], "tiles": sorted(v["tiles"])} for k, v in info.items()]
    with open(argv[2], "w", encoding="utf-8") as fh:
        json.dump(out, fh)
    log("PROBED", len(out))
    sys.exit(0)

outdir, W, H, tile = argv[2], int(argv[3]), int(argv[4]), int(argv[5])
wanted = set(argv[6].split("|")) if len(argv) > 6 and argv[6] not in ("", "*") else None
os.makedirs(outdir, exist_ok=True)

zs = [v.co.z for v in bm.verts]
zmin, zmax = min(zs), max(zs)
zsize = max(zmax - zmin, 1e-6)

# target = chosen materials inside the tile, shifted into 0..1; occluder = everything else
tu, tv = (tile - 1001) % 10, (tile - 1001) // 10
target_bm = bm.copy()
tuv = target_bm.loops.layers.uv.get("UVMap") or target_bm.loops.layers.uv.active
drop = []
for f in target_bm.faces:
    n = mat_names[f.material_index] if f.material_index < len(mat_names) else "(none)"
    u = sum(l[tuv].uv.x for l in f.loops) / len(f.loops)
    v = sum(l[tuv].uv.y for l in f.loops) / len(f.loops)
    in_tile = int(math.floor(u)) == tu and int(math.floor(v)) == tv
    if not in_tile or (wanted is not None and n not in wanted):
        drop.append(f)
bmesh.ops.delete(target_bm, geom=drop, context="FACES")
if not target_bm.faces:
    raise SystemExit("No faces of the chosen materials in tile %d" % tile)
for f in target_bm.faces:
    for l in f.loops:
        l[tuv].uv.x -= tu
        l[tuv].uv.y -= tv

occ_bm = bm.copy()
ouv = occ_bm.loops.layers.uv.get("UVMap") or occ_bm.loops.layers.uv.active
keep_drop = []
for f in occ_bm.faces:
    n = mat_names[f.material_index] if f.material_index < len(mat_names) else "(none)"
    u = sum(l[ouv].uv.x for l in f.loops) / len(f.loops)
    v = sum(l[ouv].uv.y for l in f.loops) / len(f.loops)
    in_tile = int(math.floor(u)) == tu and int(math.floor(v)) == tv
    if in_tile and (wanted is None or n in wanted):
        keep_drop.append(f)
bmesh.ops.delete(occ_bm, geom=keep_drop, context="FACES")

for o in list(bpy.data.objects):
    bpy.data.objects.remove(o, do_unlink=True)

def make_obj(name, b):
    me = bpy.data.meshes.new(name)
    b.to_mesh(me)
    me.materials.clear()
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    return ob

target = make_obj("RZ_Target", target_bm)
if occ_bm.faces:
    make_obj("RZ_Occluder", occ_bm)
log("TARGET_FACES", len(target_bm.faces))

# keep the original smooth shading: new_from_object kept custom normals where present
for p in target.data.polygons:
    p.use_smooth = True

# ---- bake material ---------------------------------------------------------------------
mat = bpy.data.materials.new("RZ_Bake")
mat.use_nodes = True
nt = mat.node_tree
nt.nodes.clear()
out = nt.nodes.new("ShaderNodeOutputMaterial")
bsdf = nt.nodes.new("ShaderNodeBsdfDiffuse")
emit = nt.nodes.new("ShaderNodeEmission")
geo = nt.nodes.new("ShaderNodeNewGeometry")
sep = nt.nodes.new("ShaderNodeSeparateXYZ")
mr = nt.nodes.new("ShaderNodeMapRange")
mr.inputs["From Min"].default_value = zmin
mr.inputs["From Max"].default_value = zmax
nt.links.new(geo.outputs["Position"], sep.inputs[0])
nt.links.new(sep.outputs["Z"], mr.inputs["Value"])
nt.links.new(mr.outputs["Result"], emit.inputs["Color"])
img_node = nt.nodes.new("ShaderNodeTexImage")
nt.nodes.active = img_node
target.data.materials.append(mat)

scene = bpy.context.scene
scene.render.engine = "CYCLES"
scene.cycles.device = "CPU"
scene.render.bake.margin = 16
bpy.ops.object.select_all(action="DESELECT")
target.select_set(True)
bpy.context.view_layer.objects.active = target

def bake(name, btype, shader, samples, **kw):
    log("STEP", name)
    img = bpy.data.images.new("RZ_" + name, W, H, alpha=False, float_buffer=False)
    img.colorspace_settings.name = "Non-Color"
    img_node.image = img
    nt.links.new(shader.outputs[0], out.inputs["Surface"])
    scene.cycles.samples = samples
    bpy.ops.object.bake(type=btype, use_clear=True, **kw)
    img.filepath_raw = os.path.join(outdir, name + ".png")
    img.file_format = "PNG"
    img.save()

bake("height", "EMIT", emit, 1)
bake("normal", "NORMAL", bsdf, 1, normal_space="OBJECT", normal_r="POS_X", normal_g="POS_Y", normal_b="POS_Z")
bake("ao", "AO", bsdf, 64)
log("DONE")
