using System.Collections.Generic;
using PokeModelExporter.Formats;

namespace PokeModelExporter.Scarlet
{
    // Scarlet/Violet's own species -> model table (pokemon/catalog/catalog/poke_resource_table.trpmcatalog,
    // schema pkZukan/PokeDocs SV/Flatbuffers/resources/trpmcatalog.fbs). Model folders use internal
    // numbers, not the National Dex (Braixen #654 lives in pm0718), so this is the only reliable map.
    public sealed class SvCatalog
    {
        public const string Path = "pokemon/catalog/catalog/poke_resource_table.trpmcatalog";
        public const string DataRoot = "pokemon/data/";

        public sealed record Entry(int Species, int Form, int Gender, string ModelPath, string MaterialTablePath,
            string ConfigPath, IReadOnlyList<string> AnimationPaths, IReadOnlyList<string> LocatorPaths, string IconPath)
        {
            // Folder holding the model and its animations, under pokemon/data/ ("pm0718/pm0718_00_00/").
            public string Folder => ModelPath.Substring(0, ModelPath.LastIndexOf('/') + 1);

            // The catalog names locator sets logically ("pm0718_00_00_00000.trskl"); the files are
            // "locators/pm0718_00_00_00000_eff.trskl", relative to Folder.
            public IEnumerable<string> LocatorFiles()
            {
                foreach (var p in LocatorPaths)
                {
                    string name = p.Substring(p.LastIndexOf('/') + 1);
                    yield return "locators/" + name.Substring(0, name.Length - ".trskl".Length) + "_eff.trskl";
                }
            }
        }

        readonly List<Entry> _entries = new List<Entry>();

        public IReadOnlyList<Entry> Entries => _entries;

        public SvCatalog(byte[] data)
        {
            var root = FbTable.Root(data);
            var table = root.Vector(1);
            for (int i = 0; i < table.Length; i++)
            {
                var e = table.Table(i);
                var sp = e.Table(0);
                var anims = new List<string>();
                var animVector = e.Vector(4);
                for (int a = 0; a < animVector.Length; a++) anims.Add(animVector.Table(a).String(1));
                var locators = new List<string>();
                var locVector = e.Vector(5);
                for (int l = 0; l < locVector.Length; l++) locators.Add(locVector.Table(l).String(2));
                _entries.Add(new Entry(sp.U16(0), sp.U16(1), sp.U8(2), e.String(1), e.String(2), e.String(3), anims, locators, e.String(6)));
            }
        }

        // The entry for a National Dex number, form and gender, falling back to gender 0, then form 0.
        public Entry Find(int species, int form = 0, int gender = 0)
        {
            Entry sameForm = null, baseForm = null;
            foreach (var e in _entries)
            {
                if (e.Species != species) continue;
                if (e.Form == form && e.Gender == gender) return e;
                if (e.Form == form && sameForm == null) sameForm = e;
                if (e.Form == 0 && baseForm == null) baseForm = e;
            }
            return sameForm ?? baseForm;
        }
    }
}
