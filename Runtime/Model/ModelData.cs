using System.Collections.Generic;
using System.Numerics;

namespace PokeModelExporter.Model
{
    // An engine-independent model, filled by a game adapter and written out as GLB.
    // Y-up, metres, right-handed (glTF conventions).
    public sealed class ModelData
    {
        public string Name;
        // Vertex colours are plain glTF COLOR_0 (3DS models: the combiner's vertex colour), not Trinity's effect data.
        public bool StandardVertexColors;
        public List<NodeData> Nodes = new List<NodeData>();
        public List<MeshData> Meshes = new List<MeshData>();
        public List<MaterialData> Materials = new List<MaterialData>();
        public List<AnimationData> Animations = new List<AnimationData>();
    }

    // A skeletal clip: keyframes per bone (by node name). Channels a track lacks keep the rest pose.
    public sealed class AnimationData
    {
        public string Name;
        public float FramesPerSecond = 30;
        public int FrameCount;
        public bool Loop;
        public List<BoneTrackData> Tracks = new List<BoneTrackData>();
        public List<AnimationEvent> Events = new List<AnimationEvent>();
        public float RootSpeed; // ground speed the clip's root motion had (model units per second) before it was pinned in place
        public bool Borrowed;   // taken from another model of the species (a Mega's base form) by bone name, see SvExporter.BorrowClips
        // Non-skeletal tracks (SV .tracm): material parameters, mesh visibility, blend shapes.
        public List<MaterialTrackData> MaterialTracks = new List<MaterialTrackData>();
        public List<VisibilityTrackData> VisibilityTracks = new List<VisibilityTrackData>();
        public List<MorphTrackData> MorphTracks = new List<MorphTrackData>();
        public float Duration => FrameCount > 1 ? (FrameCount - 1) / FramesPerSecond : 0;
    }

    // A material parameter over time, per channel (e.g. UVScaleOffset: R,G = UV scale, B,A = UV offset;
    // SV moves eyes and scrolls flames this way). Channels may be keyed at different times.
    public sealed class MaterialTrackData
    {
        public string Mesh, Material, Parameter;
        public Keys<float> R, G, B, A;
    }

    // Mesh shown or hidden: Fixed for the whole clip, or Keys (step changes at frames).
    public sealed class VisibilityTrackData
    {
        public string Mesh;
        public bool? Fixed;
        public Keys<bool> Keys;
    }

    public sealed class MorphTrackData
    {
        public string Mesh, Shape;
        public Keys<float> Weight;
    }

    // An animation notify: something the game triggers at a frame of a clip, e.g.
    //   Audio/PokeVoiceEvent      the Pokemon's cry (JointName = root_jaw, VoiceType = SPATK...)
    //   Audio/PostEvent           a sound effect (EventName = PLAY_PM_COMMON_WHOOSH_S...)
    //   System/SetTriggerParameter  ParameterName = Pokemon_EffectStart: start the move's VFX
    //   System/SetBoolParameterRange  a parameter held on for Duration frames
    public sealed class AnimationEvent
    {
        public string Type;
        public int StartFrame, DurationFrames;
        public Dictionary<string, string> Parameters = new Dictionary<string, string>();
    }

    public sealed class BoneTrackData
    {
        public string Bone;
        public Keys<Vector3> Scale, Translation;
        public Keys<Quaternion> Rotation;
    }

    // Keyframes: Times in frames (not seconds), one value per time.
    public sealed class Keys<T>
    {
        public float[] Times;
        public T[] Values;
    }

    // A skeleton node with its local transform. SkinIndex is the joint number vertices use.
    public sealed class NodeData
    {
        public string Name;
        public int Parent = -1;
        public Vector3 Translation;
        public Quaternion Rotation = Quaternion.Identity;
        public Vector3 Scale = Vector3.One;
        public int SkinIndex = -1;
        // Segment scale compensation (Maya's): the parent's scale moves this node (its position) but doesn't scale or
        // skew it. glTF has no such flag; ScaleCompensation.Apply rebuilds it with helper nodes.
        public bool ScaleCompensate;
        // Effect locator (a named point VFX attach to, e.g. eff_mouth01), not a skinning bone.
        // LocatorSet tells sets apart when a model has several (SV: 00000, 10000, 20000).
        public bool IsLocator;
        public string LocatorSet;
    }

    // One skinned mesh: per-vertex arrays (null when absent) and index lists per material.
    public sealed class MeshData
    {
        public string Name;
        public bool Hidden; // hidden by default (e.g. alternative meshes like Braixen's hair_b)
        public Vector3[] Positions;
        public Vector3[] Normals;
        public Vector4[] Tangents;
        public List<Vector2[]> TexCoords = new List<Vector2[]>();
        public Vector4[] Colors;
        public int[] Joints;   // 4 per vertex, SkinIndex values
        public float[] Weights; // 4 per vertex
        public List<SubMeshData> SubMeshes = new List<SubMeshData>();
    }

    public sealed class SubMeshData
    {
        public string Material;
        public int[] Indices; // triangle list
    }

    public sealed class MaterialData
    {
        public string Name;
        public string Shader;
        public Dictionary<string, string> Textures = new Dictionary<string, string>();       // slot name -> texture file
        public Dictionary<string, float> Floats = new Dictionary<string, float>();
        public Dictionary<string, Vector4> Colors = new Dictionary<string, Vector4>();
        public Dictionary<string, string> ShaderOptions = new Dictionary<string, string>();
        // Per texture slot, how its sampler wraps U and V (trmtr UVWrapMode: 0 repeat, 1 clamp, 6 mirror, 7 mirror once).
        public Dictionary<string, (uint U, uint V)> Wraps = new Dictionary<string, (uint, uint)>();
        public string AlphaType;
    }
}
