using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PokeModelExporter.Ctr
{
    // Ultra Sun/Moon's sound archive (data/sound/niji_sound.bcsar, NintendoWare CTR "CSAR") rendered to PCM: a sound is
    // a sequence (CSEQ: notes, waits, program / volume / pan changes) played through an instrument bank (CBNK: program ->
    // key region -> velocity region -> wave, root key, volume, pan, pitch, envelope) whose waves live in wave archives
    // (CWAR of CWAV, DSP-ADPCM or PCM), or a single wave. Battle scripts name sounds as 0xC0000 + the sound's index
    // (Sound::PlaySE 787079 = sound 647, label SE_W084). Our own reading of the formats; streams (BGM) aren't rendered.
    public sealed class CtrSoundArchive
    {
        public const int OutputRate = 48000;

        readonly byte[] _d;
        readonly string _folder;
        readonly int _fileBlock;
        readonly int _sounds, _banks, _warcs, _files;
        readonly Dictionary<int, byte[]> _external = new Dictionary<int, byte[]>();

        public int SoundCount { get; }
        public static Action<string> Debug;

        public CtrSoundArchive(string path)
        {
            _d = System.IO.File.ReadAllBytes(path);
            _folder = Path.GetDirectoryName(path);
            if (_d.Length < 0x40 || Encoding.ASCII.GetString(_d, 0, 4) != "CSAR") throw new InvalidDataException("not a CSAR");
            int info = -1;
            for (int i = 0; i < 3; i++)
            {
                int type = U16(0x14 + 12 * i), off = I32(0x18 + 12 * i);
                if (type == 0x2001) info = off;
                if (type == 0x2002) _fileBlock = off;
            }
            if (info < 0) throw new InvalidDataException("CSAR: no INFO");
            int b = info + 8;
            for (int i = 0; i < 8; i++)
            {
                int type = U16(b + 8 * i), at = b + I32(b + 8 * i + 4);
                switch (type)
                {
                    case 0x2100: _sounds = at; break;
                    case 0x2101: _banks = at; break;
                    case 0x2103: _warcs = at; break;
                    case 0x2106: _files = at; break;
                }
            }
            SoundCount = I32(_sounds);
        }

        // The game's sound ids are (category << 16 | n); Sound::PlaySE (code.bin 0x3BCB18) looks n up in the category's
        // table of archive item ids (code.bin 0x6013F8: per category a count and a pointer; 24 categories). Battle
        // effects are category 0x0C, which is contiguous from item 0x204. Read from the player's code.bin when present.
        uint[][] _idTables;

        public void UseIdTables(byte[] codeBin)
        {
            const int Base = 0x100000, Table = 0x6013F8;
            try
            {
                var tables = new uint[24][];
                for (int c = 0; c < 24; c++)
                {
                    int n = BitConverter.ToInt32(codeBin, Table - Base + 8 * c), ptr = BitConverter.ToInt32(codeBin, Table - Base + 8 * c + 4);
                    if (ptr == 0 || n <= 0) continue;
                    if (n > 5000 || ptr - Base < 0 || ptr - Base + 4 * n > codeBin.Length) return;
                    tables[c] = new uint[n];
                    for (int i = 0; i < n; i++) tables[c][i] = BitConverter.ToUInt32(codeBin, ptr - Base + 4 * i);
                }
                if (tables[0xC] == null || tables[0xC][0] != 0x01000204) return; // not the version we know
                _idTables = tables;
            }
            catch (ArgumentException) { }
        }

        // A script's sound id -> the archive's sound index, -1 when unknown.
        public int IndexOf(int id)
        {
            int category = id >> 16 & 0xFF, n = id & 0xFFFF;
            if (_idTables != null && category < _idTables.Length && _idTables[category] != null)
                return n < _idTables[category].Length && _idTables[category][n] >> 24 == 1 ? (int)(_idTables[category][n] & 0xFFFFFF) : -1;
            return category == 0xC ? 0x204 + n : -1;
        }

        // ---- Tables ----
        int Entry(int table, int i) => i < 0 || i >= I32(table) ? -1 : table + I32(table + 4 + 8 * i + 4);

        // A file's bytes (inside the archive's FILE block, or an external file beside it), null when missing.
        byte[] File(int id)
        {
            int e = Entry(_files, id);
            if (e < 0) return null;
            int type = U16(e), at = e + I32(e + 4);
            if (type == 0x220C)
            {
                int off = I32(at + 4), size = I32(at + 8);
                if (off < 0 || size <= 0) return null;
                var o = new byte[size];
                Array.Copy(_d, _fileBlock + 8 + off, o, 0, size);
                return o;
            }
            if (type == 0x220D)
            {
                lock (_external)
                {
                    if (_external.TryGetValue(id, out var cached)) return cached;
                    int z = Array.IndexOf(_d, (byte)0, at);
                    string rel = Encoding.ASCII.GetString(_d, at, z - at).Replace('/', Path.DirectorySeparatorChar);
                    string full = Path.Combine(_folder, rel);
                    var data = System.IO.File.Exists(full) ? System.IO.File.ReadAllBytes(full) : null;
                    _external[id] = data;
                    return data;
                }
            }
            return null;
        }

        // One wave of a wave archive as mono samples (debugging).
        public float[] WaveSamples(int warc, int wave, out int rate)
        {
            var w = WaveOf(warc, wave);
            rate = w?.Rate ?? 0;
            return w?.Samples;
        }

        public string Label(int index)
        {
            var seq = SequenceOf(index, out _, out int start);
            return seq == null ? null : start >= 0 && start < seq.Names.Count ? seq.Names[start] : seq.Label;
        }

        // ---- Rendering ----
        // Stereo interleaved PCM at OutputRate, or null (a stream, or data we can't read).
        public float[] Render(int index, float maxSeconds = 12)
        {
            int e = Entry(_sounds, index);
            if (e < 0) return null;
            int fileId = I32(e);
            int detailType = U16(e + 0xC), detail = e + I32(e + 0xC + 4);
            if (detailType == 0x2202) return RenderWaveSound(fileId, detail);
            if (detailType != 0x2203) return null;
            var seq = SequenceOf(index, out var banks, out int start);
            if (seq == null) return null;
            var mix = new Mixer(this, banks, maxSeconds);
            mix.Run(seq, start);
            return mix.Output();
        }

        Cseq SequenceOf(int index, out int[] banks, out int start)
        {
            banks = Array.Empty<int>(); start = 0;
            int e = Entry(_sounds, index);
            if (e < 0 || U16(e + 0xC) != 0x2203) return null;
            int x = e + I32(e + 0xC + 4);
            int bt = x + I32(x + 4);
            int nb = I32(bt);
            banks = new int[nb];
            for (int k = 0; k < nb; k++) banks[k] = I32(bt + 4 + 4 * k) & 0xFFFFFF;
            start = I32(x + 8);
            var data = File(I32(e));
            return data != null ? Cseq.Read(data) : null;
        }

        float[] RenderWaveSound(int fileId, int detail)
        {
            // A wave sound: its CWSD names (wave archive, wave); rare in USUM's battle sounds, left out for now.
            return null;
        }

        readonly Dictionary<int, Cbnk> _bankCache = new Dictionary<int, Cbnk>();
        readonly Dictionary<long, Wave> _waveCache = new Dictionary<long, Wave>();

        internal Cbnk Bank(int index)
        {
            lock (_bankCache)
            {
                if (_bankCache.TryGetValue(index, out var b)) return b;
                int e = Entry(_banks, index);
                var data = e >= 0 ? File(I32(e)) : null;
                b = data != null ? Cbnk.Read(data) : null;
                _bankCache[index] = b;
                return b;
            }
        }

        internal Wave WaveOf(int warc, int wave)
        {
            long key = (long)warc << 32 | (uint)wave;
            lock (_waveCache)
            {
                if (_waveCache.TryGetValue(key, out var w)) return w;
                int e = Entry(_warcs, warc);
                var data = e >= 0 ? File(I32(e)) : null;
                w = data != null ? Wave.FromWarc(data, wave) : null;
                _waveCache[key] = w;
                return w;
            }
        }

        int I32(int o) => BitConverter.ToInt32(_d, o);
        int U16(int o) => BitConverter.ToUInt16(_d, o);

        // A 16-bit stereo WAV file.
        public static byte[] Wav(float[] stereo, int rate = OutputRate)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            int frames = stereo.Length / 2;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + frames * 4); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(frames * 4);
            foreach (var s in stereo) w.Write((short)Math.Max(-32768, Math.Min(32767, (int)Math.Round(s * 32767))));
            return ms.ToArray();
        }

        // ---- The sequencer ----
        // Two passes: the tracks' commands run first, collecting notes and each track's parameter timelines (pitch bend,
        // volume, pan; the "time" prefix A3 makes a change glide over ticks, e.g. the power-up's pitch sweeps); then every
        // note is rendered against its track's timelines.
        sealed class Mixer
        {
            readonly CtrSoundArchive _arc;
            readonly int[] _banks;
            readonly float[] _l, _r;
            readonly double _maxSeconds;
            int _length;
            float _tempo = 120, _timebase = 48, _mainVolume = 1;
            public int Unknown;

            public Mixer(CtrSoundArchive arc, int[] banks, float maxSeconds)
            {
                _arc = arc; _banks = banks; _maxSeconds = maxSeconds;
                int n = (int)(maxSeconds * OutputRate);
                _l = new float[n]; _r = new float[n];
            }

            // A parameter over time: steps and glides.
            sealed class Timeline
            {
                readonly List<(double Time, float From, float To, double Duration)> _segments = new List<(double, float, float, double)>();
                readonly float _initial;
                public Timeline(float initial) { _initial = initial; }
                public float At(double t)
                {
                    float v = _initial;
                    foreach (var s in _segments)
                    {
                        if (s.Time > t) break;
                        v = s.Duration <= 0 || t >= s.Time + s.Duration ? s.To : s.From + (s.To - s.From) * (float)((t - s.Time) / s.Duration);
                    }
                    return v;
                }
                public void Set(double time, float value) => _segments.Add((time, At(time), value, 0));
                public void GlideLast(double seconds)
                {
                    if (_segments.Count == 0) return;
                    var s = _segments[_segments.Count - 1];
                    _segments[_segments.Count - 1] = (s.Time, s.From, s.To, seconds);
                }
            }

            sealed class Track
            {
                public int Id, Pc, Wait; public bool Done;
                public int Program, Transpose, BendRange = 2;
                public readonly Timeline Bend = new Timeline(0), Volume = new Timeline(1), Volume2 = new Timeline(1), Pan = new Timeline(0);
                public Timeline LastSet;
                public int Attack = -1, Decay = -1, Sustain = -1, Release = -1;
                public bool NoteWait = true;
                public readonly Stack<int> Calls = new Stack<int>();
                public int LoopStart = -1, LoopCount;
            }

            sealed class Played
            {
                public Track Track; public double Time, Length; public int Key, Velocity; public Cbnk.Region Region; public Wave Wave;
                public int Attack, Decay, Sustain, Release;
            }

            readonly List<Played> _notes = new List<Played>();

            double TickSeconds => 60.0 / (_tempo * _timebase);

            public void Run(Cseq seq, int start)
            {
                var tracks = new List<Track> { new Track { Id = 0, Pc = seq.Offset(start) } };
                double time = 0;
                for (int guard = 0; guard < 100000 && tracks.Exists(t => !t.Done); guard++)
                {
                    foreach (var t in tracks.ToArray())
                        while (!t.Done && t.Wait <= 0)
                        {
                            try { Step(seq, t, tracks, time); }
                            catch (IndexOutOfRangeException) { t.Done = true; Unknown++; } // ran off the data: a command we read wrong
                        }
                    int step = int.MaxValue;
                    foreach (var t in tracks) if (!t.Done) step = Math.Min(step, t.Wait);
                    if (step == int.MaxValue) break;
                    foreach (var t in tracks) if (!t.Done) t.Wait -= step;
                    time += step * TickSeconds;
                    if (time >= _maxSeconds) break;
                }
                foreach (var n in _notes) Render(n);
            }

            void Step(Cseq seq, Track t, List<Track> tracks, double time)
            {
                var d = seq.Data;
                if (t.Pc < 0 || t.Pc >= d.Length) { t.Done = true; return; }
                int c = d[t.Pc++];
                // Prefixes change the next command's last argument: A0 random (min, max), A1 a variable, A2 a condition,
                // A3 over time (ticks), A4 / A5 time from random / variable. We keep the command's own value, gliding for A3.
                if (c >= 0xA0 && c <= 0xA5)
                {
                    t.LastSet = null;
                    Step(seq, t, tracks, time);
                    if (c == 0xA0 || c == 0xA4) t.Pc += 4;
                    else if (c == 0xA1 || c == 0xA5) t.Pc += 1;
                    else if (c == 0xA3)
                    {
                        int ticks = d[t.Pc] << 8 | d[t.Pc + 1]; t.Pc += 2;
                        t.LastSet?.GlideLast(ticks * TickSeconds);
                    }
                    return;
                }
                if (c < 0x80)
                {
                    int velocity = d[t.Pc++];
                    int length = VarLen(d, ref t.Pc);
                    Note(t, c, velocity, length, time);
                    if (t.NoteWait) t.Wait = length;
                    return;
                }
                switch (c)
                {
                    case 0x80: t.Wait = VarLen(d, ref t.Pc); return;
                    case 0x81: t.Program = VarLen(d, ref t.Pc); return;
                    case 0x88:
                    {
                        int n = d[t.Pc]; int off = U24(d, t.Pc + 1); t.Pc += 4;
                        // track 0 is the main track itself (it runs on into that code); the others start once
                        if (n != 0 && !tracks.Exists(x => x.Id == n)) tracks.Add(new Track { Id = n, Pc = seq.DataStart + off });
                        return;
                    }
                    case 0x89: t.Pc = seq.DataStart + U24(d, t.Pc); return;
                    case 0x8A: t.Calls.Push(t.Pc + 3); t.Pc = seq.DataStart + U24(d, t.Pc); return;
                    case 0xFD: if (t.Calls.Count > 0) t.Pc = t.Calls.Pop(); else t.Done = true; return;
                    case 0xFE: t.Pc += 2; return; // allocate tracks
                    case 0xFF: End(t, time); return;
                    case 0xFB: t.Attack = t.Decay = t.Sustain = t.Release = -1; return;
                    case 0xFC: // loop end: count 0 = forever, which ends a sound effect's track here
                        if (t.LoopStart >= 0 && t.LoopCount > 1) { t.LoopCount--; t.Pc = t.LoopStart; }
                        else if (t.LoopStart >= 0 && t.LoopCount == 0) t.Done = true;
                        else t.LoopStart = -1;
                        return;
                    case 0xF0: t.Pc += 1 + 3; return; // extended: sub-command + u8 + s16
                }
                if (c >= 0xB0 && c <= 0xDF)
                {
                    int v = d[t.Pc++];
                    switch (c)
                    {
                        case 0xB0: _timebase = Math.Max(1, v); break;
                        case 0xC0: t.Pan.Set(time, (v - 64) / 63f); t.LastSet = t.Pan; break;
                        case 0xC1: t.Volume.Set(time, v / 127f); t.LastSet = t.Volume; break;
                        case 0xC2: _mainVolume = v / 127f; break;
                        case 0xC3: t.Transpose = (sbyte)v; break;
                        case 0xC4: t.Bend.Set(time, (sbyte)v / 128f); t.LastSet = t.Bend; break;
                        case 0xC5: t.BendRange = v; break;
                        case 0xC7: t.NoteWait = v != 0; break;
                        case 0xD0: t.Attack = v; break;
                        case 0xD1: t.Decay = v; break;
                        case 0xD2: t.Sustain = v; break;
                        case 0xD3: t.Release = v; break;
                        case 0xD4: t.LoopStart = t.Pc; t.LoopCount = v; break;
                        case 0xD5: t.Volume2.Set(time, v / 127f); t.LastSet = t.Volume2; break;
                    }
                    return;
                }
                if (c >= 0xE0 && c <= 0xE7)
                {
                    int v = (short)(d[t.Pc] << 8 | d[t.Pc + 1]); t.Pc += 2;
                    if (c == 0xE1) _tempo = Math.Max(1, v);
                    return;
                }
                t.Done = true; Unknown++; Debug?.Invoke($"unknown command {c:X2} at {t.Pc - 1:X}"); // stop rather than read garbage
            }

            // A track that ends releases the notes it still holds: a note of length 0 lasts until its track's end (the
            // waits before `fin` are the sound's designed length), or until the script stops a looping sound whose track
            // never ends.
            void End(Track t, double time)
            {
                t.Done = true;
                foreach (var n in _notes)
                    if (n.Track == t && n.Length == double.MaxValue) n.Length = Math.Max(0, time - n.Time);
            }

            void Note(Track t, int key, int velocity, int lengthTicks, double time)
            {
                foreach (int b in _banks)
                {
                    var bank = _arc.Bank(b);
                    var region = bank?.Find(t.Program, key, velocity);
                    if (region == null || region.WaveIndex < 0 || region.WaveIndex >= bank.Waves.Count) continue;
                    var (warc, wave) = bank.Waves[region.WaveIndex];
                    var w = _arc.WaveOf(warc & 0xFFFFFF, wave);
                    if (w == null || w.Samples.Length == 0) return;
                    Debug?.Invoke($"t={time:0.000}s track {t.Id} key {key} vel {velocity} len {lengthTicks}: region key {region.OriginalKey} vol {region.Volume:0.00} pan {region.Pan:0.00} pitch {region.Pitch:0.000} adsr {region.Attack}/{region.Decay}/{region.Sustain}/{region.Release}, wave {w.Samples.Length} @ {w.Rate}{(w.LoopStart >= 0 ? " loop" : "")}");
                    _notes.Add(new Played
                    {
                        Track = t, Time = time, Key = key + t.Transpose, Velocity = velocity, Region = region, Wave = w,
                        Length = lengthTicks > 0 ? lengthTicks * TickSeconds : double.MaxValue, // 0: the whole wave (or until stopped)
                        Attack = t.Attack >= 0 ? t.Attack : region.Attack, Decay = t.Decay >= 0 ? t.Decay : region.Decay,
                        Sustain = t.Sustain >= 0 ? t.Sustain : region.Sustain, Release = t.Release >= 0 ? t.Release : region.Release,
                    });
                    return;
                }
            }

            void Render(Played n)
            {
                var w = n.Wave; var r = n.Region; var t = n.Track;
                float vel = n.Velocity / 127f;
                // Envelope times: our approximation of NintendoWare's rate tables (127 = instant).
                double attackSec = n.Attack >= 127 ? 0 : Math.Pow(2, (127 - n.Attack) / 16.0) * 0.002;
                double releaseSec = n.Release >= 127 ? 0.005 : Math.Pow(2, (127 - n.Release) / 12.0) * 0.004;
                double decaySec = n.Decay >= 127 ? 0.001 : Math.Pow(2, (127 - n.Decay) / 12.0) * 0.004;
                float sustainLevel = n.Sustain / 127f;
                int o = (int)(n.Time * OutputRate);
                double pos = 0, step = 0;
                float gl = 0, gr = 0;
                for (int i = o; i < _l.Length; i++)
                {
                    double now = i / (double)OutputRate, tsec = now - n.Time;
                    if (((i - o) & 63) == 0) // the track's parameters every 64 samples
                    {
                        double semis = n.Key - r.OriginalKey + t.Bend.At(now) * t.BendRange;
                        step = Math.Pow(2, semis / 12.0) * r.Pitch * w.Rate / OutputRate;
                        float tv = t.Volume.At(now);
                        float gain = vel * vel * tv * tv * t.Volume2.At(now) * _mainVolume * r.Volume;
                        float pan = Math.Max(-1, Math.Min(1, t.Pan.At(now) + r.Pan));
                        gl = gain * (float)Math.Sqrt((1 - pan) / 2); gr = gain * (float)Math.Sqrt((1 + pan) / 2);
                    }
                    if (pos >= w.Samples.Length - 1)
                    {
                        if (w.LoopStart < 0 || w.LoopEnd <= w.LoopStart) break;
                        pos = w.LoopStart + (pos - w.LoopStart) % (w.LoopEnd - w.LoopStart);
                    }
                    float env = attackSec > 0 && tsec < attackSec ? (float)(tsec / attackSec)
                        : sustainLevel + (1 - sustainLevel) * (float)Math.Exp(-(tsec - attackSec) / decaySec);
                    if (tsec >= n.Length)
                    {
                        double rel = (tsec - n.Length) / releaseSec;
                        if (rel >= 1) break;
                        env *= (float)(1 - rel);
                    }
                    int p = (int)pos; float f = (float)(pos - p);
                    float s = w.Samples[p] + (w.Samples[Math.Min(p + 1, w.Samples.Length - 1)] - w.Samples[p]) * f;
                    _l[i] += s * env * gl; _r[i] += s * env * gr;
                    _length = Math.Max(_length, i + 1);
                    pos += step;
                }
            }

            public float[] Output()
            {
                var o = new float[_length * 2];
                for (int i = 0; i < _length; i++) { o[2 * i] = _l[i]; o[2 * i + 1] = _r[i]; }
                return o;
            }

            static int VarLen(byte[] d, ref int p)
            {
                int v = 0;
                for (int k = 0; k < 4; k++) { int b = d[p++]; v = v << 7 | b & 0x7F; if ((b & 0x80) == 0) break; }
                return v;
            }

            static int U24(byte[] d, int p) => d[p] << 16 | d[p + 1] << 8 | d[p + 2]; // big-endian in the data
        }
    }

    // A sequence file: the command data and its labels.
    sealed class Cseq
    {
        public byte[] Data; public int DataStart; public string Label;
        readonly List<int> _labels = new List<int>();
        public readonly List<string> Names = new List<string>();

        public int Offset(int label) => label >= 0 && label < _labels.Count ? DataStart + _labels[label] : DataStart;

        public static Cseq Read(byte[] d)
        {
            if (Encoding.ASCII.GetString(d, 0, 4) != "CSEQ") return null;
            var s = new Cseq { Data = d };
            int blocks = BitConverter.ToUInt16(d, 0x10);
            for (int i = 0; i < blocks; i++)
            {
                int type = BitConverter.ToUInt16(d, 0x14 + 12 * i), off = BitConverter.ToInt32(d, 0x18 + 12 * i);
                if (type == 0x5000) s.DataStart = off + 8;
                if (type == 0x5001)
                {
                    int b = off + 8, n = BitConverter.ToInt32(d, b);
                    for (int k = 0; k < n; k++)
                    {
                        int e = b + BitConverter.ToInt32(d, b + 4 + 8 * k + 4);
                        s._labels.Add(BitConverter.ToInt32(d, e + 4)); // data reference (type 0x1f00, offset)
                        int len = BitConverter.ToInt32(d, e + 8);
                        s.Names.Add(len > 0 && e + 12 + len <= d.Length ? Encoding.ASCII.GetString(d, e + 12, len).TrimEnd('\0') : "");
                        if (k == 0) s.Label = s.Names[0];
                    }
                }
            }
            return s;
        }
    }

    // An instrument bank: per program, key regions, velocity regions, each naming a wave and its playback parameters.
    sealed class Cbnk
    {
        public sealed class Region { public int WaveIndex = -1, OriginalKey = 60, Attack = 127, Decay = 127, Sustain = 127, Release = 127; public float Volume = 1, Pan, Pitch = 1; }

        public readonly List<(int Warc, int Wave)> Waves = new List<(int, int)>();
        byte[] _d; int _instruments;

        public static Cbnk Read(byte[] d)
        {
            if (Encoding.ASCII.GetString(d, 0, 4) != "CBNK") return null;
            var b = new Cbnk { _d = d };
            int info = BitConverter.ToInt32(d, 0x18);
            int x = info + 8;
            int waveTable = x + BitConverter.ToInt32(d, x + 4);
            b._instruments = x + BitConverter.ToInt32(d, x + 12);
            int n = BitConverter.ToInt32(d, waveTable);
            for (int k = 0; k < n; k++) b.Waves.Add((BitConverter.ToInt32(d, waveTable + 4 + 8 * k), BitConverter.ToInt32(d, waveTable + 8 + 8 * k)));
            return b;
        }

        int I32(int o) => BitConverter.ToInt32(_d, o);
        int U16(int o) => BitConverter.ToUInt16(_d, o);

        public Region Find(int program, int key, int velocity)
        {
            int n = I32(_instruments);
            if (program < 0 || program >= n) return null;
            int type = U16(_instruments + 4 + 8 * program);
            if (type != 0x5900) return null;
            int inst = _instruments + I32(_instruments + 4 + 8 * program + 4);
            int keyRegion = Pick(inst, key, 0x5901);
            if (keyRegion < 0) return null;
            int velRegion = Pick(keyRegion, velocity, 0x5902);
            return velRegion < 0 ? null : ReadRegion(velRegion);
        }

        // A region table (direct / range / index) at `at` -> the chosen entry's address, -1 when none covers the value.
        int Pick(int at, int value, int entryType)
        {
            int kind = U16(at), body = at + I32(at + 4);
            switch (kind)
            {
                case 0x6000: return Target(body, 0);
                case 0x6001:
                {
                    int n = I32(body);
                    int refs = body + 4 + ((n + 3) & ~3);
                    for (int i = 0; i < n; i++) if (value <= _d[body + 4 + i]) return Target(refs, i);
                    return -1;
                }
                case 0x6002:
                {
                    int min = _d[body], max = _d[body + 1];
                    if (value < min || value > max) return -1;
                    return Target(body + 4, value - min);
                }
            }
            return -1;
        }

        // Entry i of a reference list at `at` (offsets relative to the list's start; a direct region is a list of one
        // whose offset is relative to itself, the same thing).
        int Target(int at, int i)
        {
            int type = U16(at + 8 * i);
            if (type == 0) return -1;
            return at + I32(at + 8 * i + 4);
        }

        Region ReadRegion(int at)
        {
            var r = new Region { WaveIndex = I32(at) };
            uint flags = (uint)I32(at + 4);
            int p = at + 8;
            if ((flags & 1) != 0) { r.OriginalKey = _d[p]; p += 4; }
            if ((flags & 2) != 0) { r.Volume = _d[p] / 127f; p += 4; }
            if ((flags & 4) != 0) { r.Pan = (_d[p] - 64) / 63f; p += 4; }
            if ((flags & 8) != 0) { r.Pitch = BitConverter.ToSingle(_d, p); p += 4; }
            if ((flags & 0x10) != 0) p += 4; // note-off / interpolation
            if ((flags & 0x200) != 0)
            {
                // an offset from the region to a reference (type, offset from itself) -> attack, decay, sustain, hold, release
                int env = at + I32(p);
                int e = env + I32(env + 4);
                if (e >= 0 && e + 5 <= _d.Length) { r.Attack = _d[e]; r.Decay = _d[e + 1]; r.Sustain = _d[e + 2]; r.Release = _d[e + 4]; }
            }
            return r;
        }
    }

    // One wave: mono samples (-1..1), its rate and loop.
    sealed class Wave
    {
        public float[] Samples = Array.Empty<float>(); public int Rate = 32728, LoopStart = -1, LoopEnd;

        public static Wave FromWarc(byte[] d, int index)
        {
            if (Encoding.ASCII.GetString(d, 0, 4) != "CWAR") return null;
            int info = -1, file = -1;
            for (int i = 0; i < 2; i++)
            {
                int type = BitConverter.ToUInt16(d, 0x14 + 12 * i), off = BitConverter.ToInt32(d, 0x18 + 12 * i);
                if (type == 0x6800) info = off; else if (type == 0x6801) file = off;
            }
            if (info < 0 || file < 0) return null;
            int n = BitConverter.ToInt32(d, info + 8);
            if (index < 0 || index >= n) return null;
            int e = info + 12 + 12 * index;
            int off2 = BitConverter.ToInt32(d, e + 4);
            return FromCwav(d, file + 8 + off2);
        }

        public static Wave FromCwav(byte[] d, int at)
        {
            if (at < 0 || at + 0x40 > d.Length || Encoding.ASCII.GetString(d, at, 4) != "CWAV") return null;
            int info = -1, data = -1;
            for (int i = 0; i < 2; i++)
            {
                int type = BitConverter.ToUInt16(d, at + 0x14 + 12 * i), off = BitConverter.ToInt32(d, at + 0x18 + 12 * i);
                if (type == 0x7000) info = at + off; else if (type == 0x7001) data = at + off;
            }
            if (info < 0 || data < 0) return null;
            int encoding = d[info + 8];
            bool loop = d[info + 9] != 0;
            var w = new Wave { Rate = BitConverter.ToInt32(d, info + 0xC) };
            int loopStart = BitConverter.ToInt32(d, info + 0x10), end = BitConverter.ToInt32(d, info + 0x14);
            if (loop) { w.LoopStart = loopStart; w.LoopEnd = end; }
            int chTable = info + 0x1C;
            int channels = BitConverter.ToInt32(d, chTable);
            if (channels < 1) return null;
            int ch = chTable + BitConverter.ToInt32(d, chTable + 4 + 4); // first channel (mixed down: we keep the left)
            int samples = data + 8 + BitConverter.ToInt32(d, ch + 4);
            int count = end;
            if (encoding == 2)
            {
                int adpcm = ch + BitConverter.ToInt32(d, ch + 12);
                var coef = new short[16];
                for (int k = 0; k < 16; k++) coef[k] = BitConverter.ToInt16(d, adpcm + 2 * k);
                w.Samples = DspAdpcm(d, samples, count, coef);
            }
            else if (encoding == 1)
            {
                w.Samples = new float[count];
                for (int k = 0; k < count && samples + 2 * k + 1 < d.Length; k++) w.Samples[k] = BitConverter.ToInt16(d, samples + 2 * k) / 32768f;
            }
            else if (encoding == 0)
            {
                w.Samples = new float[count];
                for (int k = 0; k < count && samples + k < d.Length; k++) w.Samples[k] = (sbyte)d[samples + k] / 128f;
            }
            else return null;
            return w;
        }

        // Nintendo DSP-ADPCM: frames of 8 bytes = header (predictor << 4 | scale) + 14 nibbles.
        static float[] DspAdpcm(byte[] d, int at, int count, short[] coef)
        {
            var o = new float[count];
            int h1 = 0, h2 = 0, n = 0;
            for (int f = at; n < count && f < d.Length; f += 8)
            {
                int header = d[f], scale = 1 << (header & 0xF), pred = header >> 4 & 7;
                int c1 = coef[pred * 2], c2 = coef[pred * 2 + 1];
                for (int k = 0; k < 14 && n < count; k++)
                {
                    int b = f + 1 + k / 2;
                    if (b >= d.Length) break;
                    int nib = (k & 1) == 0 ? d[b] >> 4 : d[b] & 0xF;
                    if (nib >= 8) nib -= 16;
                    int s = ((nib * scale) << 11) + 1024 + c1 * h1 + c2 * h2 >> 11;
                    s = Math.Max(-32768, Math.Min(32767, s));
                    h2 = h1; h1 = s;
                    o[n++] = s / 32768f;
                }
            }
            return o;
        }
    }
}
