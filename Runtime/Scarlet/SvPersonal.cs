using System.Collections.Generic;
using PokeModelExporter.Formats;

namespace PokeModelExporter.Scarlet
{
    // The game's species table (avalon/data/personal_array.bin, schema pkZukan/PokeDocs
    // SV/Flatbuffers/pml/personal.fbs). The catalog is keyed by the game's internal species number,
    // which differs from the National Dex for most of Gen 9; this table links the two.
    //   SpeciesInfo.species_index = internal number (the catalog key)
    //   SpeciesInfo.species_model = National Dex number (despite the schema's name)
    // Verified by base stats against Showdown for all 733 species in the game (732 match by
    // species_model, 0 of the 106 differing ones by species_index; Minior's stats depend on its form):
    // Houndstone #972 = internal 925, Varoom #965 = 942, Tinkaton #959 = 1002, Bombirdier #962 = 959.
    public sealed class SvPersonal
    {
        public const string Path = "avalon/data/personal_array.bin";
        const int StatsField = 22; // StatInfo hp/atk/def/spa/spd/spe (bytes); the schema's slot count is off by two

        readonly Dictionary<(int national, int form), int> _internal = new Dictionary<(int, int), int>();
        readonly Dictionary<(int national, int form), int[]> _stats = new Dictionary<(int, int), int[]>();
        readonly HashSet<(int national, int form)> _present = new HashSet<(int, int)>();

        public SvPersonal(byte[] data)
        {
            var entries = FbTable.Root(data).Vector(0);
            for (int i = 0; i < entries.Length; i++)
            {
                var e = entries.Table(i);
                int p = e.InlineStruct(0); // SpeciesInfo: species_index u16, species_form u16, species_model u16, ...
                if (p == 0) continue;
                var b = e.Buffer;
                int index = System.BitConverter.ToUInt16(b, p), form = System.BitConverter.ToUInt16(b, p + 2);
                var key = (System.BitConverter.ToUInt16(b, p + 4), form);
                if (!_internal.ContainsKey(key)) _internal[key] = index;
                int s = e.FieldPosition(StatsField);
                if (s != 0 && !_stats.ContainsKey(key)) _stats[key] = new int[] { b[s], b[s + 1], b[s + 2], b[s + 3], b[s + 4], b[s + 5] };
                if (e.Bool(1)) _present.Add(key);
            }
        }

        // The catalog species number for a National Dex number and form (the form's own entry, else the
        // base form's, else the number itself).
        public int CatalogSpecies(int national, int form = 0) =>
            _internal.TryGetValue((national, form), out int m) ? m
            : _internal.TryGetValue((national, 0), out m) ? m
            : national;

        // Base stats (hp, atk, def, spa, spd, spe) the game has for a National Dex number and form, or null.
        // Lets callers check the mapping against their own data (PokeSim compares them with Showdown's).
        public int[] Stats(int national, int form = 0) => _stats.TryGetValue((national, form), out var s) ? s : null;

        // Whether the species/form can appear in this game (is_present).
        public bool IsPresent(int national, int form = 0) => _present.Contains((national, form));
    }
}
