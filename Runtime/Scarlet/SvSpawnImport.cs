using System;
using System.Collections.Generic;
using System.Linq;
using PokeModelExporter.Formats;

namespace PokeModelExporter.Scarlet
{
    // Where Scarlet/Violet's wild Pokemon live (world/data/encount/*, decoded with their .bfbs schemas), in PokeSim's
    // spawn format (PokeSim's Docs/reference/world-kit.md, "Spawn rules"): one plain object, written by the caller as
    // spawns.json.
    //
    //   format: 1
    //   species: { <Showdown id>: { biomes: { <biome>: share }, media: [land, surface, underwater, air] } }
    //            share = the tag's weight over the species' strongest tag in the same table (1 = its main land), the
    //            best over Paldea, Kitakami and Blueberry; biomes are the tables' tags lowercased (grass, forest, town,
    //            mountain, rocky, snow, cave, mine, underground, cave_water, lake, river, swamp, beach, ocean, flower,
    //            desert, bamboo, olive, ruins, volcano, wasteland, denki_ishi)
    //   borders: { "<biome>|<biome>": contacts }   how often two biomes' spawn points touch on the maps (a 40 m grid:
    //            neighbouring cells of different biomes, and two biomes in one cell), Paldea + Kitakami + Blueberry
    //
    // Left out: Area Zero (its rows and its map: the crater mixes lands as nowhere else, a Frosmoth in its grass), and
    // the one placeholder table (pokedata_lc). Time of day, version and story flags are merged (both versions, all day).
    public sealed class SvSpawnImport
    {
        readonly Func<string, byte[]> _read;
        readonly Func<int, int, string> _speciesId;  // national dex, form -> Showdown id (null = unknown)
        readonly Func<int, int> _national;           // the game's internal species number -> national dex (0 = none)
        public readonly List<string> Log = new List<string>();

        public const float Cell = 40f;

        public SvSpawnImport(Func<string, byte[]> read, Func<int, int, string> speciesId, Func<int, int> national)
        {
            _read = read;
            _speciesId = speciesId;
            _national = national;
        }

        (object root, BfbsSchema schema) Decode(string binPath)
        {
            var data = _read(binPath);
            if (data == null) return (null, null);
            var bfbs = _read(binPath.Substring(0, binPath.Length - 4) + ".bfbs");
            if (bfbs == null) return (null, null);
            var schema = new BfbsSchema(bfbs);
            return (schema.Decode(data), schema);
        }

        static string Str(Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out var v) ? v?.ToString() : null;
        static double Num(Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out var v) && v != null
            && double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n) ? n : 0;
        static Dictionary<string, object> Obj(Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;
        static bool Flag(Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out var v) && v is bool b && b;

        // The tables' and the maps' names for a biome, as one lowercase name.
        public static string Biome(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string b = raw.ToLowerInvariant();
            switch (b)
            {
                case "prairie": return "grass";
                case "snowfield": return "snow";
                case "rocky_area": return "rocky";
                case "bamboo_forest": return "bamboo";
                case "riverside": return "river";
                case "osean": return "ocean";
                case "none": case "null": return null;
                default: return b;
            }
        }

        // Area Zero's rows (owner's research 2026-11-08: a special place): every place they name is the crater's (area 23,
        // its locations a_w23_*).
        static bool AreaZeroOnly(string area, string locations)
        {
            var areas = (area ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()).ToList();
            var places = (locations ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()).ToList();
            if (areas.Count == 0 && places.Count == 0) return false;
            return areas.All(a => a == "23") && places.All(p => p.StartsWith("a_w23", StringComparison.Ordinal));
        }

        public Dictionary<string, object> Run()
        {
            // species id -> biome -> best share; species id -> media
            var shares = new SortedDictionary<string, SortedDictionary<string, double>>(StringComparer.Ordinal);
            var media = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            int rows = 0, skipped = 0, unknown = 0;
            foreach (var (table, paldea) in new[] { ("pokedata", true), ("pokedata_su1", false), ("pokedata_su2", false) })
            {
                var (root, schema) = Decode($"world/data/encount/pokedata/{table}/{table}_array.bin");
                if (root == null) { Log.Add($"{table}: missing"); continue; }
                var values = (root as Dictionary<string, object>)?.TryGetValue("values", out var v) == true ? v as List<object> : null;
                // This table's tags per species, before the shares (a species may have several rows: forms, places, times).
                var tags = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
                foreach (var o in values ?? new List<object>())
                {
                    if (!(o is Dictionary<string, object> row)) continue;
                    string dev = Str(row, "devid");
                    if (string.IsNullOrEmpty(dev) || dev == "DEV_NULL") continue;
                    if (paldea && AreaZeroOnly(Str(row, "area"), Str(row, "locationName"))) { skipped++; continue; }
                    if (!schema.TryEnumNumber(dev, out long internalNo) && !long.TryParse(dev, out internalNo)) { unknown++; continue; }
                    int national = _national((int)internalNo);
                    string id = national > 0 ? _speciesId(national, (int)Num(row, "formno")) : null;
                    if (id == null) { unknown++; continue; }
                    rows++;
                    if (!tags.TryGetValue(id, out var mine)) tags[id] = mine = new Dictionary<string, double>(StringComparer.Ordinal);
                    for (int k = 1; k <= 4; k++)
                    {
                        string biome = Biome(Str(row, "biome" + k));
                        double weight = Num(row, "lotvalue" + k);
                        if (biome == null || weight <= 0) continue;
                        mine[biome] = Math.Max(mine.TryGetValue(biome, out double had) ? had : 0, weight);
                    }
                    var on = Obj(row, "enabletable");
                    if (!media.TryGetValue(id, out var m)) media[id] = m = new SortedSet<string>(StringComparer.Ordinal);
                    if (Flag(on, "land")) m.Add("land");
                    if (Flag(on, "up_water")) m.Add("surface");
                    if (Flag(on, "underwater")) m.Add("underwater");
                    if (Flag(on, "air1") || Flag(on, "air2")) m.Add("air");
                }
                foreach (var kv in tags)
                {
                    if (kv.Value.Count == 0) continue;
                    double top = kv.Value.Values.Max();
                    if (!shares.TryGetValue(kv.Key, out var best)) shares[kv.Key] = best = new SortedDictionary<string, double>(StringComparer.Ordinal);
                    foreach (var t in kv.Value)
                        best[t.Key] = Math.Max(best.TryGetValue(t.Key, out double had) ? had : 0, Math.Round(t.Value / top, 2));
                }
                Log.Add($"{table}: {values?.Count ?? 0} rows, {tags.Count} species");
            }
            // (plain dictionaries filled in sorted order: BfbsSchema.ToJson writes those, in that order)
            var species = new Dictionary<string, object>();
            foreach (var kv in shares)
            {
                var biomes = new Dictionary<string, object>();
                foreach (var b in kv.Value) biomes[b.Key] = b.Value;
                species[kv.Key] = new Dictionary<string, object>
                {
                    ["biomes"] = biomes,
                    ["media"] = (media.TryGetValue(kv.Key, out var m) ? m.ToList() : new List<string>()).Cast<object>().ToList(),
                };
            }
            Log.Add($"{rows} rows -> {species.Count} species ({skipped} Area Zero rows left out, {unknown} not matched to a species)");

            // Which biomes border which, from the maps' spawn points (Area Zero's map, encount_data_atlantis, left out).
            var borders = new SortedDictionary<string, long>(StringComparer.Ordinal);
            void Count(string a, string b, long n = 1)
            {
                if (a == b) return;
                string key = string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;
                borders[key] = (borders.TryGetValue(key, out long had) ? had : 0) + n;
            }
            foreach (var map in new[] { "100000", "su1", "su2" })
            {
                var (root, _) = Decode($"world/data/encount/point_data/point_data/encount_data_{map}.bin");
                var list = (root as Dictionary<string, object>)?.TryGetValue("encount_point_list", out var l) == true ? l as List<object> : null;
                if (list == null) { Log.Add($"encount_data_{map}: missing"); continue; }
                var cells = new Dictionary<(int, int), Dictionary<string, int>>();
                foreach (var o in list)
                {
                    if (!(o is Dictionary<string, object> p)) continue;
                    string biome = Biome(Str(p, "biome"));
                    var at = Obj(p, "position");
                    if (biome == null || at == null) continue;
                    var key = ((int)Math.Floor(Num(at, "x") / Cell), (int)Math.Floor(Num(at, "z") / Cell));
                    if (!cells.TryGetValue(key, out var c)) cells[key] = c = new Dictionary<string, int>(StringComparer.Ordinal);
                    c[biome] = (c.TryGetValue(biome, out int n) ? n : 0) + 1;
                }
                // Each cell's main biome (most points; ties by name, so every machine gets the same).
                var main = new Dictionary<(int, int), string>();
                foreach (var kv in cells)
                    main[kv.Key] = kv.Value.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key;
                foreach (var kv in main)
                    foreach (var (dx, dz) in new[] { (1, 0), (0, 1), (1, 1), (1, -1) })
                        if (main.TryGetValue((kv.Key.Item1 + dx, kv.Key.Item2 + dz), out var other)) Count(kv.Value, other);
                foreach (var c in cells.Values)
                {
                    var names = c.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
                    for (int i = 0; i < names.Count; i++)
                        for (int j = i + 1; j < names.Count; j++) Count(names[i], names[j]);
                }
                Log.Add($"encount_data_{map}: {list.Count} points, {cells.Count} cells");
            }
            var borderOut = new Dictionary<string, object>();
            foreach (var kv in borders) borderOut[kv.Key] = kv.Value;
            return new Dictionary<string, object> { ["format"] = 1L, ["species"] = species, ["borders"] = borderOut };
        }
    }
}
