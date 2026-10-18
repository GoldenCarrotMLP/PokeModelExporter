# Renders a GLB from the front and the side for a quick visual check.
#   blender --background --factory-startup --python render_preview.py -- <model.glb> <out.png> [clip name part] [frame]
import bpy, sys, math
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:]
glb, out = args[0], args[1]
clip = args[2] if len(args) > 2 else None
frame = int(args[3]) if len(args) > 3 else 0

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=glb)
scene = bpy.context.scene
if clip:
    action = next((a for a in bpy.data.actions if clip in a.name), None)
    print("ACTION", action.name if action else None, "of", len(bpy.data.actions))
    for o in scene.objects:
        if o.type == 'ARMATURE' and action:
            o.animation_data_create()
            o.animation_data.action = action
            print("FRAMES", action.frame_range[:])
scene.frame_set(frame)

# Blender's Principled BSDF has no ambient occlusion input; the glTF importer only keeps the
# occlusion map in its "glTF Material Output" group. For previews, multiply it into the color.
if "--show-ao" in sys.argv:
    for mat in bpy.data.materials:
        if not mat.use_nodes: continue
        nt = mat.node_tree
        bsdf = next((n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED'), None)
        group = next((n for n in nt.nodes if n.type == 'GROUP' and n.node_tree and 'glTF' in n.node_tree.name), None)
        if not bsdf or not group or not group.inputs.get('Occlusion') or not group.inputs['Occlusion'].links: continue
        ao_out = group.inputs['Occlusion'].links[0].from_socket
        base = bsdf.inputs['Base Color']
        mul = nt.nodes.new('ShaderNodeMix'); mul.data_type = 'RGBA'; mul.blend_type = 'MULTIPLY'
        mul.inputs['Factor'].default_value = 1.0
        if base.links: nt.links.new(base.links[0].from_socket, mul.inputs[6])
        else: mul.inputs[6].default_value = base.default_value
        nt.links.new(ao_out, mul.inputs[7])
        nt.links.new(mul.outputs[2], base)
        print("AO applied to", mat.name)

# (not the importer's bone display shape, an icosphere 2 m across, which throws the framing off)
meshes = [o for o in scene.objects if o.type == 'MESH' and not o.name.startswith('Icosphere')]
deps = bpy.context.evaluated_depsgraph_get()
pts = []
for o in meshes:
    ev = o.evaluated_get(deps)
    m = ev.to_mesh()
    pts += [ev.matrix_world @ v.co for v in m.vertices]
    ev.to_mesh_clear()
lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
center, size = (lo + hi) / 2, max((hi - lo).length, 0.01)
print(f"BOUNDS min={tuple(round(c,3) for c in lo)} max={tuple(round(c,3) for c in hi)}")

world = bpy.data.worlds.new("w"); world.color = (0.35, 0.37, 0.42); scene.world = world
sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", 'SUN')); sun.data.energy = 3
sun.rotation_euler = (math.radians(50), 0, math.radians(30)); scene.collection.objects.link(sun)
scene.render.engine = 'BLENDER_EEVEE_NEXT' if 'BLENDER_EEVEE_NEXT' in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties['engine'].enum_items] else 'BLENDER_EEVEE'  # renamed in Blender 4.2
scene.render.resolution_x, scene.render.resolution_y = 1000, 700
scene.render.film_transparent = False

cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam")); scene.collection.objects.link(cam)
cam.data.type = 'ORTHO'; cam.data.ortho_scale = size * 1.25; scene.camera = cam

def shoot(direction, path):
    cam.location = center + direction * size * 3
    cam.rotation_euler = (center - cam.location).to_track_quat('-Z', 'Y').to_euler()
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)

shoot(Vector((0, -1, 0.15)).normalized(), out.replace(".png", "_front.png"))
shoot(Vector((1, 0, 0.15)).normalized(), out.replace(".png", "_side.png"))

# Close-up of the top of the model (usually the face) from the front.
top = Vector((center.x, lo.y, hi.z - (hi.z - lo.z) * 0.12))
cam.data.ortho_scale = size * 0.3
cam.location = top + Vector((0, -1, 0.05)).normalized() * size * 3
cam.rotation_euler = (top - cam.location).to_track_quat('-Z', 'Y').to_euler()
scene.render.filepath = out.replace(".png", "_face.png")
bpy.ops.render.render(write_still=True)

# Tighter close-up for eye details.
cam.data.ortho_scale = size * 0.12
eyes = top + Vector((0, 0, -(hi.z - lo.z) * 0.02))
cam.location = eyes + Vector((0, -1, 0.05)).normalized() * size * 3
cam.rotation_euler = (eyes - cam.location).to_track_quat('-Z', 'Y').to_euler()
scene.render.filepath = out.replace(".png", "_eyes.png")
bpy.ops.render.render(write_still=True)
