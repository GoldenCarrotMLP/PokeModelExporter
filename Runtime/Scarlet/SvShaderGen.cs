using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PokeModelExporter.Formats;

namespace PokeModelExporter.Scarlet
{
    // Unity shaders made from an emitter's own compiled particle shader: its vertex and fragment programs translated
    // to HLSL (NvToHlsl) and wrapped for PokeSim's particle pass (Assets/Resources/Shaders/SvParticle*.hlsl), which
    // feeds them the game's inputs (by the names the shader's reflection gives each vertex input) and uniform blocks.
    // PokeSim commits the output (owner's decision, 2026-10-18: Assets/Resources/SvShaders), like the MoveFx data.
    public static class SvShaderGen
    {
        // Where .shader files go; null = don't make them (and don't tag emitters).
        public static string OutDir;

        // Vertex inputs by reflection name -> the field of the pass's SvInputs that carries it.
        static readonly Dictionary<string, string> Inputs = new Dictionary<string, string>
        {
            ["sysPosAttr"] = "pos", ["sysTexCoordAttr"] = "texCoord", ["sysNormalAttr"] = "normal", ["sysVertexColor0Attr"] = "vertexColor",
            ["sysLocalPosAttr"] = "localPos", ["sysLocalVecAttr"] = "localVec", ["sysLocalDiffAttr"] = "localDiff", ["sysScaleAttr"] = "scale",
            ["sysRandomAttr"] = "random", ["sysInitRotateAttr"] = "initRotate", ["sysEmtMat0Attr"] = "emtMat0", ["sysEmtMat1Attr"] = "emtMat1",
            ["sysEmtMat2Attr"] = "emtMat2", ["sysColor0Attr"] = "color0", ["sysColor1Attr"] = "color1",
        };

        static readonly Dictionary<string, string> Made = new Dictionary<string, string>(); // key -> problem (null = fine)

        // The shader key for an emitter (MD5 of its two programs), writing the shader the first time; null when the
        // emitter has no readable programs or something in them isn't supported.
        public static string For(VfxbFile file, VfxbEmitter e)
        {
            if (OutDir == null) return null;
            string vp = file.VertexProgram(e.ShaderIndex), fp = file.FragmentProgram(e.ShaderIndex);
            var refl = file.Reflection(e.ShaderIndex);
            if (vp == null || fp == null || refl == null) return null;
            string key;
            using (var md5 = System.Security.Cryptography.MD5.Create())
                key = BitConverter.ToString(md5.ComputeHash(Encoding.ASCII.GetBytes(vp + "\n" + fp))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            if (Made.TryGetValue(key, out var problem)) return problem == null ? key : null;
            try
            {
                var v = NvToHlsl.Translate(vp, "Sv_V");
                var f = NvToHlsl.Translate(fp, "Sv_F");
                foreach (int slot in f.Textures.Concat(v.Textures))
                    if (slot != 0 && slot != 1 && slot != 2 && slot != 3 && slot != 4 && slot != 6 && slot != 28) throw new NotSupportedException("texture slot " + slot);
                var bind = new StringBuilder();
                foreach (var (name, slot) in refl.Vertex[0])
                {
                    if (slot < 0) continue; // gl_VertexID
                    if (!Inputs.TryGetValue(name, out var field)) throw new NotSupportedException("vertex input " + name);
                    bind.Append($"    vin[{slot}] = asuint(s.{field});\n");
                }
                Directory.CreateDirectory(OutDir);
                File.WriteAllText(Path.Combine(OutDir, key + ".shader"), Shader(key, e.Name, v, f, bind.ToString()));
                Made[key] = null;
                return key;
            }
            catch (NotSupportedException ex)
            {
                Made[key] = ex.Message;
                Console.Error.WriteLine($"  {e.Name}: shader not translated ({ex.Message})");
                return null;
            }
        }

        // Bytes of the static uniform block the programs read: the record, with the flag word at 80 the game's runtime
        // packs from the emitter's settings (zero in every file). Bits, as the vertex programs test them:
        //   0-2    fluctuation wave (sine, sawtooth, square) = the fluctuation's wave byte >> 3 (8, 16, 32)
        //   4-7    texture 0's flipbook: fit to life, clamp, loop, random (pattern types 1-4); 8-11 / 12-15 textures 1 / 2
        //   16-18  random rotation direction per axis (X, Y, Z)
        //   19-24  random U / V flip of textures 0, 1, 2
        //   25-27  random loop start of textures 0, 1, 2
        //   30     offset towards the camera (offset type 1)
        public static string Record(VfxbEmitter e) => Convert.ToBase64String(Filled(e));

        public static byte[] Filled(VfxbEmitter e)
        {
            var r = (byte[])e.Record.Clone();
            uint flags = (uint)(r[e.FluctuationOffset + 3] >> 3) & 7;
            for (int i = 0; i < 3; i++)
            {
                var a = e.TextureAnims[i];
                if (a.PatternType >= 1 && a.PatternType <= 4) flags |= 16u << (a.PatternType - 1) << (4 * i);
                if (a.InvertRandomU) flags |= 1u << (19 + 2 * i);
                if (a.InvertRandomV) flags |= 1u << (20 + 2 * i);
                if (a.PatternLoopRandom) flags |= 1u << (25 + i);
            }
            for (int axis = 0; axis < 3; axis++)
                if (r[e.ParticleDataOffset + 8 + axis] != 0) flags |= 1u << (16 + axis);
            if (e.OffsetType == 1) flags |= 1u << 30;
            BitConverter.GetBytes(flags).CopyTo(r, 80);
            return r;
        }

        static string Shader(string key, string emitter, NvToHlsl.Result v, NvToHlsl.Result f, string bind) => $@"// Generated by pokemodelexporter (SvShaderGen) from the compiled particle shader of '{emitter}' (and every emitter
// sharing its two programs). Regenerate with `movefx --svshaders` rather than editing.
Shader ""Hidden/PokeSim/Sv/{key}""
{{
    SubShader
    {{
        Tags {{ ""RenderType"" = ""Transparent"" ""Queue"" = ""Transparent"" ""RenderPipeline"" = ""UniversalPipeline"" ""IgnoreProjector"" = ""True"" }}
        Pass
        {{
            Blend [_SrcBlend] [_DstBlend]
            BlendOp [_BlendOp]
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles gles3 webgpu
            #pragma vertex SvVert
            #pragma fragment SvFrag
            #include ""Assets/Resources/Shaders/SvParticle.hlsl""

{v.Code}
{f.Code}
void SvBind(SvInputs s, out uint4 vin[16])
{{
    [unroll] for (int i = 0; i < 16; i++) vin[i] = 0;
{bind}}}

            #include ""Assets/Resources/Shaders/SvParticlePass.hlsl""
            ENDHLSL
        }}
    }}
}}
";
    }
}
