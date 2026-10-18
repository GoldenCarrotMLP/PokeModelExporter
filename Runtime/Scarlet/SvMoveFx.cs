using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using PokeModelExporter.Export;
using PokeModelExporter.Formats;

namespace PokeModelExporter.Scarlet
{
    // Turns one move's effect folder (effect/battle_ew/ew<move>/) into a self-contained JSON description an
    // engine can rebuild the effect from: timeline variants (objects + timed commands) and particle recipes
    // (emitter parameters, texture names, primitive ids). Nothing of the game's art is included: textures and
    // meshes are referenced by name only, for the engine to draw with its own.
    //
    // Variants: ew<move>.trtml, or one per hit (_1 first, _2 following hits) or per Tera type (_00..); named ones
    // for other moments: "attack" = the release turn of a charge move (Solar Beam, Dig, Fly...; the plain timeline is
    // the charge turn), "turn" = end-of-turn damage of trapping moves, "back" = sent back (Roar, U-turn)...
    // Conditions: groups that can't happen in a trainer's singles battle are dropped; the ones that depend on
    // the Pokemon involved (size class, Tera type, target count, perspective) are kept for the engine to check.
    public static class SvMoveFx
    {
        // Condition values that hold in a normal trainer singles battle (None always holds).
        static readonly Dictionary<string, string[]> Static = new Dictionary<string, string[]>
        {
            ["Mode"] = new[] { "Trainer", "NoRaid", "NoWild", "NoNushi", "NoDanBoss" },
            ["Rule"] = new[] { "Single", "NoDouble" },
            ["BattleMode"] = new[] { "CommandBattle" },
            ["Camera"] = new[] { "BattleCamera" },
            ["DanBoss"] = new[] { "AtkNoDanCar", "AtkNoDanPoke", "DefNoDanCar", "DefNoDanPoke" },
            ["PokeNushi"] = new[] { "AtkNoNushi", "DefNoNushi" },
            ["UUUType"] = new[] { "NoUUU" },
            ["PokeAnimeState"] = new[] { "Poke1Ground", "Poke2Ground" },
            ["PokeRare"] = new[] { "Poke1NoRare" },
            ["PlayerRide"] = new[] { "NoRide" }, // the Terastal cinematic: the trainer on foot (not riding Koraidon / Miraidon)
        };

        // Checked by the engine per use.
        // TargetPosition: which way the camera sees a Pokemon (AttackerBack = the user from behind, i.e. on the player's
        // side; AttackerForward = the opponent; Defender... the same for the target). Amnesia's "?" smoke turns to face it.
        static readonly HashSet<string> Runtime = new HashSet<string> { "PokeSizeCategory", "TargetMulti", "SelfOther", "PokeGem", "PokeGemType", "TargetPosition" };

        // Command types worth replaying (the camera too: CompositionCamera* framings, ResetBattleCamera); the rest drive
        // SV's UI, collision and debugging.
        static bool Keep(string type) =>
            type.StartsWith("Particle/", StringComparison.Ordinal) ||
            type.StartsWith("GameObject/", StringComparison.Ordinal) && type != "GameObject/DisableCulling" && type != "GameObject/DisableOcclusion" ||
            type.StartsWith("Audio/PostEvent", StringComparison.Ordinal) ||
            type.StartsWith("Title/BlendColor/", StringComparison.Ordinal) ||
            type.StartsWith("Title/Fade/", StringComparison.Ordinal) ||
            type.StartsWith("Title/PostEffect/", StringComparison.Ordinal) ||
            type.StartsWith("Light/", StringComparison.Ordinal) ||
            type.StartsWith("Model/", StringComparison.Ordinal) ||
            type == "Title/Battle/GaugeDamage" || type == "Title/Battle/AttackMotion" || type == "Title/Battle/CameraShake" ||
            type == "Title/Battle/CameraShakeFade" || type.StartsWith("Title/Battle/CompositionCamera", StringComparison.Ordinal) ||
            type == "Title/Battle/ResetBattleCamera" || type == "Title/Battle/AdjustScale" || type == "Title/Battle/AdjustScaleTarget" ||
            type == "Title/Battle/ResetPosition" || type == "Title/Battle/SetCenterPosition" || type == "Title/Battle/MigawariVisible" || type == "Title/Battle/TameVisible" ||
            type == "Animation/SetTrigger" || type == "Animation/SetInt" || type == "Animation/SetMasterSpeed" ||
            // The Terastal cinematic: camera cuts (.trcma, exported under "cameras"), the crystal skin and crown scale,
            // the battle camera handed over for the sequence.
            type == "CameraAnimation/Load" || type == "CameraAnimation/Play" || type == "Camera/SelectThis" ||
            type == "Title/Battle/ChangeGem" || type == "Title/Battle/GemLightEnable" || type == "Title/Battle/GemSRT" ||
            type == "Title/Battle/CameraControlEnable"; // (GaugeDispAll, the HUD, is in 705 moves: the stage hides the HUD itself)

        // timelines: file stem ("ew0085", "ew0024_1") -> .trtml bytes; sceneObjects: stem -> .trsot bytes (may lack some);
        // readParticles: archive path -> bytes (null when missing).
        public static string Export(int move, IReadOnlyDictionary<string, byte[]> timelines, IReadOnlyDictionary<string, byte[]> sceneObjects,
            Func<string, byte[]> readParticles, out int emitterCount, Action<string, string, int, int> onTexture = null,
            Action<string, string, BfresPrimitives.Primitive> onPrimitive = null, string folder = null,
            IReadOnlyDictionary<string, IReadOnlyList<byte[]>> extraScenes = null, IReadOnlyDictionary<string, TrinityAnimationReader.CameraClip> cameras = null)
        {
            // extraScenes: more scene files per timeline (the Terastal cinematic's per-type objects live in 19 more);
            // cameras: camera cuts by file stem, written under "cameras".
            // The effect's folder and file stem: a move's (effect/battle_ew/ew0085) or a battle system effect's
            // (effect/battle_es/es008: stat up, heal...).
            folder ??= $"effect/battle_ew/ew{move:D4}";
            string stem = folder.Substring(folder.LastIndexOf('/') + 1);
            var w = new JsonWriter(indent: false);
            var effects = new SortedDictionary<string, string>(StringComparer.Ordinal); // effect name -> archive path
            w.BeginObject().Name("version").Value(1).Name("move").Value(move).Name("fps").Value(60);
            w.Name("variants").BeginArray();
            // Effects without a timeline (the field's weather): the game starts every effect of the scene from code.
            if (timelines.Count == 0)
                foreach (var sot in sceneObjects.Values)
                    foreach (var paths in SvSceneObjects.Read(sot).Values)
                        foreach (var path in paths) effects[System.IO.Path.GetFileNameWithoutExtension(path)] = path;
            foreach (var kv in timelines.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var timeline = SvMoveTimeline.Read(kv.Value);
                var objects = sceneObjects.TryGetValue(kv.Key, out var sot) ? SvSceneObjects.Read(sot) : new Dictionary<string, List<string>>();
                var models = sot != null ? SvSceneObjects.Models(sot) : new Dictionary<string, string>();
                if (extraScenes != null && extraScenes.TryGetValue(kv.Key, out var more))
                    foreach (var extra in more)
                    {
                        foreach (var o in SvSceneObjects.Read(extra)) if (!objects.ContainsKey(o.Key)) objects[o.Key] = o.Value;
                        foreach (var m in SvSceneObjects.Models(extra)) if (!models.ContainsKey(m.Key)) models[m.Key] = m.Value;
                    }
                // Every effect of the scene, also those no timeline object plays: the game starts some from code
                // (Transform's start_charge / end_impact around the model swap).
                foreach (var paths in objects.Values)
                    foreach (var path in paths) effects[System.IO.Path.GetFileNameWithoutExtension(path)] = path;
                w.BeginObject().Name("name").Value(kv.Key.Substring(stem.Length).TrimStart('_')).Name("frames").Value(timeline.Frames);
                w.Name("objects").BeginArray();
                foreach (var group in timeline.Groups)
                {
                    if (!Possible(group)) continue;
                    var commands = group.Tracks.SelectMany(t => t.Commands).Where(c => Keep(c.Type)).OrderBy(c => c.Start).ToList();
                    if (commands.Count == 0) continue;
                    w.BeginObject().Name("name").Value(group.Object);
                    var runtime = group.Conditions.Where(c => c.Value != "None" && Runtime.Contains(c.Key)).ToList();
                    if (runtime.Count > 0)
                    {
                        w.Name("if").BeginObject();
                        foreach (var c in runtime) w.Name(c.Key).Value(c.Value);
                        w.EndObject();
                    }
                    if (objects.TryGetValue(group.Object, out var paths))
                    {
                        w.Name("effects").BeginArray();
                        foreach (var path in paths)
                        {
                            string name = System.IO.Path.GetFileNameWithoutExtension(path);
                            effects[name] = path;
                            w.Value(name);
                        }
                        w.EndArray();
                    }
                    if (models.TryGetValue(group.Object, out var model)) w.Name("model").Value(model); // effect model (.trmdl)
                    w.Name("commands").BeginArray();
                    foreach (var c in commands)
                    {
                        w.BeginObject().Name("type").Value(c.Type).Name("start").Value(c.Start);
                        if (c.Ranged) w.Name("end").Value(c.End);
                        foreach (var p in c.Parameters) { w.Name(p.Key); Value(w, p.Value); }
                        w.EndObject();
                    }
                    w.EndArray().EndObject();
                }
                w.EndArray().EndObject();
            }
            w.EndArray();

            // Camera cuts: per key [frame, values...]; translation and aim (the look-at point) in metres, rotation as a
            // quaternion x y z w, fov in radians. Local to the object the timeline attaches the camera to.
            if (cameras != null && cameras.Count > 0)
            {
                w.Name("cameras").BeginObject();
                foreach (var cam in cameras.OrderBy(c => c.Key, StringComparer.Ordinal))
                {
                    var c = cam.Value;
                    w.Name(cam.Key).BeginObject().Name("frames").Value(c.FrameCount).Name("fps").Raw(N(c.FramesPerSecond));
                    if (c.Translation != null) Track(w, "translation", c.Translation.Times, c.Translation.Values.Select(v => new[] { v.X, v.Y, v.Z }));
                    if (c.Rotation != null) Track(w, "rotation", c.Rotation.Times, c.Rotation.Values.Select(q => new[] { q.X, q.Y, q.Z, q.W }));
                    if (c.Fov != null) Track(w, "fov", c.Fov.Times, c.Fov.Values.Select(v => new[] { v }));
                    if (c.Unknown3 != null) Track(w, "aim", c.Unknown3.Times, c.Unknown3.Values.Select(v => new[] { v.X, v.Y, v.Z }));
                    w.EndObject();
                }
                w.EndObject();
            }

            emitterCount = 0;
            w.Name("effects").BeginObject();
            foreach (var kv in effects)
            {
                // Some scene files name a folder that doesn't exist (Stellar Tera Blast: ew0851_18/): the file
                // is in the move's own folder.
                string path = kv.Value;
                var data = readParticles(path);
                if (data == null) data = readParticles(path = $"{folder}/{System.IO.Path.GetFileName(kv.Value)}");
                if (data == null) continue;
                var file = VfxbFile.Read(data);
                w.Name(kv.Key).BeginArray();
                foreach (var e in file.Sets.SelectMany(s => s.Emitters))
                {
                    emitterCount += Emitter(w, e, file, child: false);
                    if (onTexture != null) ReportTextures(e, file, path, onTexture);
                    if (onPrimitive != null) ReportPrimitives(e, file, path, onPrimitive);
                }
                w.EndArray();
            }
            w.EndObject().EndObject();
            return w.ToString();
        }

        static void Track(JsonWriter w, string name, float[] times, IEnumerable<float[]> values)
        {
            w.Name(name).BeginArray();
            int i = 0;
            foreach (var v in values)
            {
                w.BeginArray().Raw(N(times[i++]));
                foreach (var x in v) w.Raw(N(x));
                w.EndArray();
            }
            w.EndArray();
        }

        // Each texture an emitter (or its children) uses, with the flipbook grid it's cut into.
        static void ReportTextures(VfxbEmitter e, VfxbFile file, string path, Action<string, string, int, int> onTexture)
        {
            for (int i = 0; i < 3; i++)
            {
                var s = e.Samplers[i];
                if (!s.Used || !file.TextureNames.TryGetValue(s.TextureId, out var name)) continue;
                var div = e.Scrolls[i].UVDivisions;
                onTexture(path, name, Math.Max(1, (int)div[0]), Math.Max(1, (int)div[1]));
            }
            foreach (var c in e.Children) ReportTextures(c, file, path, onTexture);
        }

        static void ReportPrimitives(VfxbEmitter e, VfxbFile file, string path, Action<string, string, BfresPrimitives.Primitive> onPrimitive)
        {
            if (e.PrimitiveId != ulong.MaxValue && file.Primitives.TryGetValue(e.PrimitiveId, out var prim) && prim.Positions != null)
                onPrimitive(path, prim.Name, prim);
            foreach (var c in e.Children) ReportPrimitives(c, file, path, onPrimitive);
        }

        // A program decoded by hand is recognized by its operation sequence (opcode, constant-buffer slots and literal
        // constants of each line before the output stage, registers ignored), hashed: only an exact match uses the
        // decoded formula. (A looser signature also caught rock models, smoke and meteors that compute other things.)
        static readonly Dictionary<string, string> Programs = new Dictionary<string, string>
        {
            ["a0b7812329521b250fbc27d99fd842f8"] = "jewel", // effect/battle_ej/ej_transform_start/ej_transform01_c03_crystal02 core*
            ["02e4abb1ab6e6ca4ac2ec0d2c2355293"] = "sheen", // its surface* children
        };

        static string Program(string fp)
        {
            if (fp == null) return null;
            var lines = new List<string>();
            foreach (var raw in fp.Split('\n'))
            {
                string l = raw.Trim();
                if (l.Length == 0 || l.StartsWith("#") || l.StartsWith("OPTION") || l.StartsWith("CBUFFER") || l.StartsWith("ATTRIB") || l.StartsWith("TEMP")
                    || l.StartsWith("LONG") || l.StartsWith("SHORT") || l.StartsWith("OUTPUT") || l.StartsWith("!!")) continue;
                if (l.StartsWith("REP.S")) break;
                var parts = new List<string> { l.Split(' ')[0] };
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(l, @"buf\d+\[\d+\]")) parts.Add(m.Value);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(l, @"\{[^}]*\}")) parts.Add(m.Value);
                lines.Add(parts[0] + " " + string.Join(" ", parts.Skip(1))); // "OP " when nothing follows, as hashed
            }
            using var md5 = System.Security.Cryptography.MD5.Create();
            string hash = BitConverter.ToString(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines)))).Replace("-", "").ToLowerInvariant();
            return Programs.TryGetValue(hash, out var name) ? name : null;
        }

        static bool Possible(TimelineGroup group)
        {
            foreach (var c in group.Conditions)
            {
                if (c.Value == "None" || Runtime.Contains(c.Key)) continue;
                if (!Static.TryGetValue(c.Key, out var ok) || Array.IndexOf(ok, c.Value) < 0) return false;
            }
            return true;
        }

        static void Value(JsonWriter w, object v)
        {
            switch (v)
            {
                case int i: w.Value(i); break;
                case float f: w.Raw(N(f)); break;
                case bool b: w.Value(b); break;
                case string s: w.Value(s); break;
                case Vector3 x: w.BeginArray().Raw(N(x.X)).Raw(N(x.Y)).Raw(N(x.Z)).EndArray(); break;
                default: w.Null(); break;
            }
        }

        // Compact numbers: 4 decimals is plenty for effect parameters.
        static string N(float f) => float.IsNaN(f) || float.IsInfinity(f) ? "0" : Math.Round(f, 4).ToString("0.####", CultureInfo.InvariantCulture);

        static void Floats(JsonWriter w, string name, float[] v)
        {
            w.Name(name).BeginArray();
            foreach (var f in v) w.Raw(N(f));
            w.EndArray();
        }

        static void FloatsIf(JsonWriter w, string name, float[] v, float fallback)
        {
            if (v.Any(f => Math.Abs(f - fallback) > 1e-6f)) Floats(w, name, v);
        }

        static void Keys(JsonWriter w, string name, VfxbKey[] keys, int components)
        {
            if (keys == null || keys.Length == 0) return;
            w.Name(name).BeginArray(); // [time, x(, y, z)] per key
            foreach (var k in keys)
            {
                w.BeginArray().Raw(N(k.Time)).Raw(N(k.X));
                if (components > 1) w.Raw(N(k.Y)).Raw(N(k.Z));
                w.EndArray();
            }
            w.EndArray();
        }

        static int Emitter(JsonWriter w, VfxbEmitter e, VfxbFile file, bool child)
        {
            int count = 1;
            w.BeginObject().Name("name").Value(e.Name);
            if (child)
            {
                // A child emitter emits from each of its parent's particles and may take over some of its state.
                w.Name("inherit").BeginObject().Name("velocity").Value(e.InheritVelocity).Name("scale").Value(e.InheritScale)
                    .Name("rotate").Value(e.InheritRotate).Name("color0").Value(e.InheritColor0).Name("color1").Value(e.InheritColor1)
                    .Name("alpha0").Value(e.InheritAlpha0).Name("alpha1").Value(e.InheritAlpha1)
                    .Name("velocityRate").Raw(N(e.InheritVelocityRate)).Name("scaleRate").Raw(N(e.InheritScaleRate)).EndObject();
                // On a child: whether its parent draws its own particles (Growl's "null" parents only spawn rings).
                if (!e.EnableEmitterParticle) w.Name("showParent").Value(false);
            }
            if (!e.Draw) w.Name("draw").Value(false); // only spawns its children
            // Emission (frames at 60 fps)
            w.Name("oneTime").Value(e.OneTime).Name("start").Value(e.EmitStart).Name("duration").Value(e.EmitDuration)
                .Name("rate").Raw(N(e.Rate)).Name("interval").Value(e.Interval);
            if (e.RateRandom != 0) w.Name("rateRandom").Raw(N(e.RateRandom));
            if (e.IntervalRandom != 0) w.Name("intervalRandom").Raw(N(e.IntervalRandom));
            w.Name("life").Value(e.Life);
            if (e.LifeRandom != 0) w.Name("lifeRandom").Value(e.LifeRandom);
            if (e.InfiniteLife) w.Name("infiniteLife").Value(true);
            w.Name("follow").Value(e.FollowType); // 0 all (SRT), 1 none (world), 2 translation only
            if (e.FadeOnEmitEnd) w.Name("fadeOnStop").Value(true);
            // Particle/Stop with Fade: emission stops (fadeOnStop) and, with these flags, alpha / scale go to 0 over
            // alphaFadeTime frames instead of the particles living out their life.
            if (e.AlphaFadeOnFade) w.Name("alphaFadeOnStop").Value(true);
            if (e.ScaleFadeOnFade) w.Name("scaleFadeOnStop").Value(true);
            if (e.AlphaFadeTime != 0) w.Name("alphaFadeTime").Value(e.AlphaFadeTime);
            if (e.AlphaFadeIn) w.Name("alphaFadeIn").Value(e.FadeInTime);
            if (e.ScaleFadeIn) w.Name("scaleFadeIn").Value(e.FadeInTime);
            // Emitter transform
            FloatsIf(w, "translate", e.Translate, 0); FloatsIf(w, "translateRandom", e.TranslateRandom, 0);
            FloatsIf(w, "rotate", e.Rotate, 0); FloatsIf(w, "rotateRandom", e.RotateRandom, 0);
            FloatsIf(w, "scale", e.Scale, 1);
            FloatsIf(w, "emitterColor0", e.EmitterColor0, 1); FloatsIf(w, "emitterColor1", e.EmitterColor1, 1);
            // Shape: 0 point, 1 circle, 2 circle (divided), 3 circle (filled), 4 sphere, 5 sphere (div), 6 sphere (div64),
            // 7 sphere (filled), 8 cylinder, 9 cylinder (filled), 10 box, 11 box (filled), 12 line, 13 line (div), 14 rectangle, 15 primitive
            w.Name("shape").Value(e.VolumeType);
            if (e.VolumeType != 0)
            {
                Floats(w, "radius", e.VolumeRadius);
                FloatsIf(w, "shapeScale", e.VolumeFormScale, 1);
                if (Math.Abs(e.SweepLongitude - 2 * Math.PI) > 1e-3) w.Name("sweep").Raw(N(e.SweepLongitude));
                if (e.SweepStart != 0) w.Name("sweepStart").Raw(N(e.SweepStart));
                if (e.CaliberRatio != 1) w.Name("caliber").Raw(N(e.CaliberRatio));
                if (e.VolumeType == 12 || e.VolumeType == 13) w.Name("lineLength").Raw(N(e.LineLength)).Name("lineCenter").Raw(N(e.LineCenter));
                if (e.CircleDivisions > 1) w.Name("divisions").Value(e.CircleDivisions);
                if (Math.Abs(e.SweepLatitude - Math.PI) > 1e-3) w.Name("latitude").Raw(N(e.SweepLatitude));
            }
            // Velocity (units per frame), gravity (per frame^2), air resistance (per-frame velocity multiplier)
            if (e.AllDirection != 0) w.Name("speed").Raw(N(e.AllDirection));
            if (e.DirectionalScale != 0) { w.Name("dirSpeed").Raw(N(e.DirectionalScale)); Floats(w, "direction", e.Direction); }
            if (e.DiffusionAngle != 0) w.Name("diffusion").Raw(N(e.DiffusionAngle));
            if (e.VelocityRandom != 0) w.Name("speedRandom").Raw(N(e.VelocityRandom));
            if (e.PositionRandom != 0) w.Name("positionRandom").Raw(N(e.PositionRandom));
            FloatsIf(w, "gravity", e.Gravity, 0);
            if (e.AirResistance != 1) w.Name("air").Raw(N(e.AirResistance));
            if (e.MomentumRandom != 0) w.Name("momentumRandom").Raw(N(e.MomentumRandom));
            if (e.EmitterVelocityInherit != 0) w.Name("emitterVelocity").Raw(N(e.EmitterVelocityInherit));
            // Drawing: billboard 0 camera, 1 plate XY, 2 plate XZ, 3 Y-directional, 4 directional polygon, 5 stripe,
            // 6 complex stripe, 7 primitive, 8 Y-billboard. Blend 0 alpha, 1 additive, 2 subtractive, 3 multiply, 4 screen.
            w.Name("billboard").Value(e.BillboardType).Name("pivot").Value(e.OffsetType).Name("blend").Value(e.BlendType);
            if (!e.DepthTest) w.Name("noDepthTest").Value(true);
            if (e.DisplaySide != 0) w.Name("side").Value(e.DisplaySide);
            // Alpha test: the fragment programs discard pixels failing alpha <func> threshold, whatever the blend
            // (screen-blended bubbles are shaped only by it). func: GL order, 4 = greater.
            if (e.AlphaTest) w.Name("alphaTest").BeginArray().Value(e.AlphaFunc).Raw(N(e.AlphaThreshold)).EndArray();
            if (e.SortType != 0) w.Name("sort").Value(e.SortType);
            w.Name("colorCombiner").Value(e.ColorCombiner).Name("alphaCombiner").Value(e.AlphaCombiner);
            // Colour intensity: multiplies colour 0 and 1 (often far above 1: the game blooms them into white-hot light).
            if (e.ColorScale != 1) w.Name("colorScale").Raw(N(e.ColorScale));
            // The game's particle shader set: 0 the standard combiner, 1 SV's own, which compiles to the same combiner
            // unless its CSDP alpha gain is set ("slash": erosion, alpha gain, edge colour), 2 rare. CSDP = that
            // shader's per-emitter constants (alpha gain at 48, texture add at 52, edge colour + weight at 64..76,
            // found in its compiled fragment programs).
            w.Name("customShader").Value(e.CustomShaderIndex);
            // Programs of SV's own shader (custom 1) told apart by their compiled fragment program (not by CSDP, laid out
            // the same for all): "jewel" (the Terastal crystals: two normal-offset samples of texture 0 lerp texture 1 x a0
            // toward c0, times (texture 2 + a1); facing^P.x keeps the body, c1 x (1-facing)^P.y lights the edges; opaque)
            // and "sheen" (light flowing over them: texture 1 x lerp(c1, c0, texture 0.r), UVs pushed by texture 2 and a
            // camera fresnel). P = CSDP bytes 48..60.
            if (e.CustomShaderIndex == 1 && Program(file.FragmentProgram(e.ShaderIndex)) is string program) w.Name("program").Value(program);
            // Stripes (emitter plugins EP02 "stripe", EP03 "super stripe"): each particle draws a ribbon through its last
            // `history` frames of positions instead of its own quad (String Shot's threads, trails behind projectiles).
            // EP02 = 4 enums, a float, then the history length; EP03 = 6 enums, then the history length.
            if (e.AttributeData.TryGetValue("EP02", out var ep02) && ep02.Length > 5) w.Name("stripe").BeginArray().Value(2).Raw(N(ep02[5])).EndArray();
            else if (e.AttributeData.TryGetValue("EP03", out var ep03) && ep03.Length > 6) w.Name("stripe").BeginArray().Value(3).Raw(N(ep03[6])).EndArray();
            // EP04: drawn in front of the camera (Blizzard's white-out, Tera Blast's screen fire): distance = -float 10.
            if (e.AttributeData.TryGetValue("EP04", out var ep04) && ep04.Length > 10) w.Name("camera").Raw(N(Math.Abs(ep04[10])));
            // FSPN (spin field): particles turn around the emitter's axis (int at 4: 0 X, 1 Y, 2 Z) at radians per frame (0)
            // and drift outward per frame (8); keyed curves follow (count at 24 and 172, then { x, y, z, time } keys), not
            // used yet. Confusion's chicks: 0.026 rad/frame around Y.
            if (e.AttributeData.TryGetValue("FSPN", out var fspn) && fspn.Length >= 3 && (fspn[0] != 0 || fspn[2] != 0))
                w.Name("spinField").BeginArray().Raw(N(fspn[0])).Value(BitConverter.SingleToInt32Bits(fspn[1])).Raw(N(fspn[2])).EndArray();
            if (e.AttributeData.TryGetValue("CSDP", out var csdp) && csdp.Length >= 20)
                w.Name("csdp").BeginArray().Raw(N(csdp[12])).Raw(N(csdp[13])).Raw(N(csdp[16])).Raw(N(csdp[17])).Raw(N(csdp[18])).Raw(N(csdp[19]))
                    .Raw(N(csdp[14])).Raw(N(csdp[15])).EndArray(); // then 56, 60: shader set 2's lit-texture weight and its offset
            if (e.ParamKeys != null && e.ParamKeys.Length > 0)
                w.Name("param").BeginArray().Raw(N(e.ParamKeys[0].X)).Raw(N(e.ParamKeys[0].Y)).Raw(N(e.ParamKeys[0].Z)).EndArray();
            if (e.ShaderType != 0) w.Name("shaderType").Value(e.ShaderType);
            // Shader type 2 distorts texture 1 by texture 0 (strength per u, v; times the param curve's X).
            // Blend 5 refracts the scene behind by texture 0 with the same strengths (compiled fragment programs).
            if (e.ShaderType == 2 || e.BlendType == 5) w.Name("distortion").BeginArray().Raw(N(e.DistortionStrength[0])).Raw(N(e.DistortionStrength[1])).EndArray();
            if (e.SoftParticle) w.Name("soft").Value(true);
            // Primitive meshes by model name (shapes in primitives.json).
            if (e.PrimitiveId != ulong.MaxValue)
                w.Name("primitive").Value(file.Primitives.TryGetValue(e.PrimitiveId, out var prim) ? prim.Name : e.PrimitiveId.ToString("X16"));
            if (e.Texture1ColorBlend != 0 || e.Texture2ColorBlend != 0 || e.Texture1AlphaBlend != 0 || e.Texture2AlphaBlend != 0)
                w.Name("textureBlend").BeginArray().Value(e.Texture1ColorBlend).Value(e.Texture2ColorBlend).Value(e.Texture1AlphaBlend).Value(e.Texture2AlphaBlend).EndArray();
            // What each texture slot feeds the combiner (from the compiled programs): colour 0 rgb, 1 one, 2 1 - rgb,
            // 3 alpha, 4 1 - alpha; alpha 0 alpha, 1 red, 2 one, 3 1 - alpha, 4 1 - red.
            if (e.TextureColorInput.Any(v => v != 0) || e.TextureAlphaInput.Any(v => v != 0))
            {
                w.Name("textureInput").BeginArray();
                foreach (var v in e.TextureColorInput) w.Value(v);
                foreach (var v in e.TextureAlphaInput) w.Value(v);
                w.EndArray();
            }
            w.Name("textures").BeginArray();
            for (int i = 0; i < 3; i++)
            {
                var s = e.Samplers[i];
                if (!s.Used) continue;
                w.BeginObject().Name("slot").Value(i)
                    .Name("name").Value(file.TextureNames.TryGetValue(s.TextureId, out var n) ? n : s.TextureId.ToString("X16"))
                    .Name("wrap").BeginArray().Value(s.WrapU).Value(s.WrapV).EndArray();
                var p = e.Patterns[i];
                var sc = e.Scrolls[i];
                var anim = e.TextureAnims[i];
                if (sc.UVDivisions[0] > 1 || sc.UVDivisions[1] > 1) Floats(w, "divisions", sc.UVDivisions);
                if (anim.PatternType != 0)
                {
                    w.Name("pattern").Value(anim.PatternType).Name("patternCount").Raw(N(p.Count)).Name("patternFrames").Raw(N(p.Frequency));
                    if (p.CountRandom != 0) w.Name("patternRandom").Raw(N(p.CountRandom));
                    if (anim.PatternType == 3 || anim.PatternType == 4)
                    {
                        w.Name("patternTable").BeginArray();
                        for (int k = 0; k < Math.Max(1, (int)p.Count) && k < 32; k++) w.Value(p.Table[k]);
                        w.EndArray();
                    }
                }
                if (anim.Repeat != 0) w.Name("repeat").Value(anim.Repeat);
                // The texture's UV animation switches (scroll, rotate, scale): with any of them on, the game compiles
                // the emitter's shader with the full UV transform and uses every value (Whirlwind's windroll scrolls
                // with only "scale" on); with all three off it compiles the static path (the plain quad corner times
                // the repeat) and the scroll / rotation / scale values are never used (Grassy Terrain's circles carry
                // a scroll and rotation random they don't show).
                bool animated = anim.Scroll || anim.Rotate || anim.Scale;
                FloatsIf(w, "uvScale", sc.Scale, 1);
                if (animated)
                {
                    FloatsIf(w, "uvScaleAdd", sc.ScaleAdd, 0);
                    FloatsIf(w, "uvScaleRandom", sc.ScaleRandom, 0);
                    FloatsIf(w, "scrollAdd", sc.ScrollAdd, 0);
                    FloatsIf(w, "scroll", sc.Scroll, 0);
                    FloatsIf(w, "scrollRandom", sc.ScrollRandom, 0);
                    if (sc.RotationAdd != 0 || sc.Rotation != 0 || sc.RotationRandom != 0)
                        w.Name("uvRotateAdd").Raw(N(sc.RotationAdd)).Name("uvRotate").Raw(N(sc.Rotation)).Name("uvRotateRandom").Raw(N(sc.RotationRandom));
                }
                if (anim.InvertRandomU || anim.InvertRandomV) w.Name("flipRandom").BeginArray().Value(anim.InvertRandomU).Value(anim.InvertRandomV).EndArray();
                w.EndObject();
            }
            w.EndArray();
            // Per-particle colour, alpha and scale: 0 constant, 1 random, 2 animated 8-key ("keys": [time 0..1 of life, x, y, z])
            w.Name("color0Type").Value(e.Color0Type); Floats(w, "color0", e.Color0); Keys(w, "color0Keys", e.Color0Keys, 3);
            w.Name("alpha0Type").Value(e.Alpha0Type).Name("alpha0").Raw(N(e.Alpha0)); Keys(w, "alpha0Keys", e.Alpha0Keys, 1);
            w.Name("color1Type").Value(e.Color1Type); Floats(w, "color1", e.Color1); Keys(w, "color1Keys", e.Color1Keys, 3);
            w.Name("alpha1Type").Value(e.Alpha1Type).Name("alpha1").Raw(N(e.Alpha1)); Keys(w, "alpha1Keys", e.Alpha1Keys, 1);
            Floats(w, "size", e.ParticleScale); FloatsIf(w, "sizeRandom", e.ParticleScaleRandom, 0); Keys(w, "sizeKeys", e.ScaleKeys, 3);
            // Rotation in radians (per frame for "spin")
            w.Name("rotationType").Value(e.RotationType);
            FloatsIf(w, "rotation", e.RotateInit, 0); FloatsIf(w, "rotationRandom", e.RotateInitRandom, 0);
            FloatsIf(w, "spin", e.RotateAdd, 0); FloatsIf(w, "spinRandom", e.RotateAddRandom, 0);
            if (e.RotateResistance != 1 && e.RotateResistance != 0) w.Name("spinResistance").Raw(N(e.RotateResistance));
            if (e.Animations.Count > 0)
            {
                // Emitter animations over the emitter's life: EASL scale, EATR translation, EARO? rotation, EAES emitter
                // shape scale, EAER emission rate, EAET particle life... (keys: [frame, x, y, z]).
                w.Name("emitterAnimations").BeginObject();
                foreach (var a in e.Animations.Where(a => a.Value.Enabled))
                {
                    w.Name(a.Key).BeginObject().Name("loop").Value(a.Value.Loop);
                    Keys(w, "keys", a.Value.Keys, 3);
                    w.EndObject();
                }
                w.EndObject();
            }
            if (e.Children.Count > 0)
            {
                w.Name("children").BeginArray();
                foreach (var c in e.Children) count += Emitter(w, c, file, child: true);
                w.EndArray();
            }
            w.EndObject();
            return count;
        }
    }
}
