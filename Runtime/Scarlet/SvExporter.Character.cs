using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PokeModelExporter.Export;
using PokeModelExporter.Formats;
using PokeModelExporter.Model;

namespace PokeModelExporter.Scarlet
{
    public sealed partial class SvExporter
    {
        // A person's skeleton and clips (PokeSim retargets the trainer's motions onto players' VRM avatars): the model
        // (Scarlet/Violet's player body chara/model_pc_base/model/p0_base.trmdl is the skeleton alone, 75 bones, no
        // meshes) with the clips its motion folder's resource file lists (chara/motion_pc/base/p0_base.tracr) that the
        // filter keeps (by clip name: p0_00_01320_ballthrow01), each with its notifies (.traef). Root motion is left in
        // (a throw's step, a hop), segment scale compensation applied like on Pokemon. Null when the model isn't there.
        // resource: the motion folder's resource file by name when it isn't the model's (an NPC body playing the
        // player's clips: "p0_base").
        public static byte[] ExportCharacter(Func<string, byte[]> readArchive, string modelPath, string motionFolder, Func<string, bool> filter, out List<string> clips, string resource = null)
        {
            clips = new List<string>();
            int slash = modelPath.LastIndexOf('/');
            string folder = modelPath.Substring(0, slash + 1), file = modelPath.Substring(slash + 1);
            Func<string, byte[]> read = f => readArchive(folder + f);
            if (read(file) == null) return null;
            var model = TrinityModelReader.Read(file, read);
            if (!motionFolder.EndsWith("/")) motionFolder += "/";
            string named = resource ?? model.Name;
            var tracr = readArchive(motionFolder + named + ".tracr") ?? readArchive(motionFolder + named + "_base.tracr");
            var files = tracr != null ? TrinityAnimationReader.ClipNamesFromResource(tracr) : new List<string>();
            foreach (var clipFile in files)
            {
                string name = Path.GetFileNameWithoutExtension(clipFile);
                if (filter != null && !filter(name)) continue;
                var data = readArchive(motionFolder + Path.GetFileName(clipFile));
                if (data == null) continue;
                AnimationData anim;
                try { anim = TrinityAnimationReader.Read(data, name); }
                catch (Exception) { continue; }
                var traef = readArchive(motionFolder + Path.ChangeExtension(Path.GetFileName(clipFile), ".traef"));
                if (traef != null) try { anim.Events = TrinityAnimationReader.ReadEvents(traef); } catch (Exception) { }
                model.Animations.Add(anim);
                clips.Add(name);
            }
            ScaleCompensation.Apply(model);
            var baked = SvMaterialBaker.Bake(model, read);
            return GlbWriter.Write(model, baked.Textures, m => baked.Mappings[m.Name], null);
        }
    }
}
