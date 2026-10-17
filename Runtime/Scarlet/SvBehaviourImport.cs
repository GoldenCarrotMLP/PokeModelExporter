using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PokeModelExporter.Formats;

namespace PokeModelExporter.Scarlet
{
    // Converts Scarlet/Violet's overworld Pokemon AI tables (world/data/pokemon/{ai_action, wild, ai}, decoded with their
    // .bfbs schemas) into PokeSim's own behaviour format: plain objects (Dictionary/List/string/long/double/bool) per
    // output file, written by the caller as JSON. See PokeSim's Docs/sv-wild-ai-research.md for what the tables mean.
    //
    //   actions/<kind>.json       primitive presets by name: { "RANDOM_WALK_NEAR_RANGE_LAND": { moveType: "walk", ... } }
    //   conditions/<kind>.json    conditions by name
    //   trees/<GROUP>.json        composite behaviours by name (GROUP = the name's first word), as nodes:
    //                               "NAME"                          another tree, preset or condition by name
    //                               { seq: [ ... ] }                steps in order
    //                               { random: [ { weight, do } ] }  one, weighted
    //                               { if, then, else }              a condition picks
    //                               { while, do, then }             do while the condition holds, then `then`
    //                               { until, do, then }             do until the condition holds, then `then`
    //                               { scene: [ ... ] }              a two-Pokemon scene (who / do / target / motion ...)
    //   species/<id>.json         per species (Showdown id): wild profile (+ herd, special, female variants), partner AI
    //   wild.json, partner.json, timing.json   shared tables; unresolved.json  names used but defined nowhere (code-only)
    // Field names become camelCase and enum values lowercase; names of trees, presets and conditions stay as the game's.
    public sealed class SvBehaviourImport
    {
        readonly Func<string, byte[]> _read;
        readonly List<string> _names;
        readonly Func<int, int, string> _speciesName;  // national dex, form -> Showdown id (null = unknown)
        readonly Func<int, int> _national;             // the game's internal species number -> national dex (0 = none)
        readonly Dictionary<string, BfbsSchema> _schemas = new Dictionary<string, BfbsSchema>();

        public readonly Dictionary<string, object> Files = new Dictionary<string, object>();
        public readonly List<string> Log = new List<string>();
        readonly Dictionary<string, string> _defined = new Dictionary<string, string>();   // name -> where (kind / tree)
        readonly Dictionary<string, int> _references = new Dictionary<string, int>();      // name -> uses

        public SvBehaviourImport(Func<string, byte[]> read, IEnumerable<string> archiveNames, Func<int, int, string> speciesName, Func<int, int> national)
        {
            _read = read;
            _names = archiveNames.ToList();
            _speciesName = speciesName;
            _national = national;
        }

        public void Run()
        {
            Primitives("world/data/pokemon/ai_action/action/", "actions/", "ActionID");
            Primitives("world/data/pokemon/ai_action/trigger/", "conditions/", "TriggerID");
            Templates();
            Shared();
            Species();
            Unresolved();
        }

        // ---- Reading ----

        object Decode(string binPath)
        {
            var data = _read(binPath);
            if (data == null) return null;
            string schemaPath = binPath.Substring(0, binPath.Length - 4) + ".bfbs";
            if (!_schemas.TryGetValue(schemaPath, out var schema))
            {
                var bfbs = _read(schemaPath);
                if (bfbs == null) return null;
                _schemas[schemaPath] = schema = new BfbsSchema(bfbs);
            }
            return schema.Decode(data);
        }

        static List<object> Values(object root) => root is Dictionary<string, object> d && d.TryGetValue("values", out var v) && v is List<object> l ? l : new List<object>();

        IEnumerable<string> Bins(string prefix) => _names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".bin", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal);

        // ---- Presets and conditions ----

        void Primitives(string folder, string outFolder, string idField)
        {
            foreach (var path in Bins(folder))
            {
                string kind = path.Substring(folder.Length).Split('/')[0];
                var table = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (var row in Values(Decode(path)))
                {
                    if (!(row is Dictionary<string, object> r) || !(r.TryGetValue(idField, out var id) && id is string name)) continue;
                    var parameters = new Dictionary<string, object>();
                    foreach (var kv in r) if (kv.Key != idField) parameters[Camel(kv.Key)] = Clean(kv.Key, kv.Value);
                    table[name] = parameters;
                    _defined[name] = kind;
                }
                Files[outFolder + kind + ".json"] = new Dictionary<string, object>(table);
            }
            Log.Add($"{outFolder}: {Files.Keys.Count(k => k.StartsWith(outFolder))} kinds");
        }

        // ---- Composite behaviours ----

        void Templates()
        {
            const string folder = "world/data/pokemon/ai_action/template/";
            var groups = new SortedDictionary<string, SortedDictionary<string, object>>(StringComparer.Ordinal);
            int count = 0;
            foreach (var path in Bins(folder))
            {
                var m = Regex.Match(path, @"action_template_(\d+)/");
                int template = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                foreach (var row in Values(Decode(path)))
                {
                    if (!(row is Dictionary<string, object> r) || !(r.TryGetValue("ActionID", out var id) && id is string name)) continue;
                    var data = r.TryGetValue("ActionData", out var ad) ? ad as Dictionary<string, object> : null;
                    object node = template switch
                    {
                        15 => Loop("while", data),
                        17 => Loop("until", data),
                        _ => Node(data, null),
                    };
                    string group = name.Split('_')[0];
                    if (!groups.TryGetValue(group, out var g)) groups[group] = g = new SortedDictionary<string, object>(StringComparer.Ordinal);
                    g[name] = node ?? "RETURN_SUCCESS";
                    _defined[name] = "tree";
                    count++;
                }
            }
            foreach (var g in groups) Files["trees/" + g.Key + ".json"] = new Dictionary<string, object>(g.Value);
            Log.Add($"trees: {count} in {groups.Count} files");
        }

        // A template 15 / 17 loop: { while|until: condition, do: Success|Failure, then: Failure|Success }.
        object Loop(string kind, Dictionary<string, object> d)
        {
            if (d == null) return null;
            string condition = TriggerOf(d);
            Reference(condition);
            object success = Node(Child(d, "SuccessAction"), condition), failure = Node(Child(d, "FailureAction"), condition);
            var loop = new Dictionary<string, object> { [kind] = condition };
            // while: repeat Success while it holds, then Failure; until: repeat Failure until it holds, then Success.
            var body = kind == "while" ? success : failure;
            var after = kind == "while" ? failure : success;
            if (body != null) loop["do"] = body;
            if (after != null && !IsNothing(after)) loop["then"] = after;
            return loop;
        }

        object Node(Dictionary<string, object> d, string inherited)
        {
            if (d == null || d.Count == 0) return null;
            if (d.TryGetValue("ActionID", out var id) && id is string name) { Reference(name); return name; }
            if (d.Keys.Any(k => Regex.IsMatch(k, @"^Action_\d+$"))) return Scene(d);
            string condition = TriggerOf(d) ?? inherited;

            if (d.ContainsKey("SuccessAction") || d.ContainsKey("FailureAction"))
            {
                Reference(condition);
                var then = Node(Child(d, "SuccessAction"), condition);
                var otherwise = Node(Child(d, "FailureAction"), condition);
                if (condition == null || condition == "JUDGE_THROUGH" || Same(then, otherwise)) return then;
                var node = new Dictionary<string, object> { ["if"] = condition };
                if (then != null && !IsNothing(then)) node["then"] = then;
                if (otherwise != null && !IsNothing(otherwise)) node["else"] = otherwise;
                return node;
            }

            var combos = Ordered(d, "ComboAction_");
            if (combos.Count > 0)
            {
                var steps = new List<object>();
                foreach (var c in combos)
                {
                    var step = Node(c, condition);
                    if (step == null || IsNothing(step)) continue;
                    if (step is Dictionary<string, object> s && s.Count == 1 && s.TryGetValue("seq", out var inner) && inner is List<object> il) steps.AddRange(il);
                    else steps.Add(step);
                }
                return steps.Count == 0 ? "RETURN_SUCCESS" : steps.Count == 1 ? steps[0] : new Dictionary<string, object> { ["seq"] = steps };
            }

            var branches = Ordered(d, "BranchAction_");
            if (branches.Count > 0)
            {
                var options = new List<object>();
                foreach (var b in branches)
                {
                    // The weight: the branch's own, else its condition's or first step's (where the game keeps it).
                    long weight = Number(b, "Probability") ?? Number(Child(b, "Trigger"), "Probability") ?? Number(Child(b, "ComboAction_1"), "Probability") ?? 100;
                    var option = Node(b, condition);
                    if (option == null) continue;
                    options.Add(new Dictionary<string, object> { ["weight"] = weight, ["do"] = option });
                }
                if (options.Count == 0) return null;
                if (options.Count == 1) return ((Dictionary<string, object>)options[0])["do"];
                return new Dictionary<string, object> { ["random"] = options };
            }
            return null;
        }

        object Scene(Dictionary<string, object> d)
        {
            var steps = new List<object>();
            foreach (var step in Ordered(d, "Action_"))
            {
                var s = new Dictionary<string, object>();
                foreach (var kv in step)
                {
                    string key = kv.Key switch
                    {
                        "Probability" => "weight", "ActionObjectType" => "who", "ActionType" => "do", "TargetObjectType" => "target",
                        "MotionType" => "motion", "IsWaitForEnd" => "waitForEnd", _ => Camel(kv.Key),
                    };
                    s[key] = Clean(kv.Key, kv.Value);
                }
                steps.Add(s);
            }
            return new Dictionary<string, object> { ["scene"] = steps };
        }

        static List<Dictionary<string, object>> Ordered(Dictionary<string, object> d, string prefix) =>
            d.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Value is Dictionary<string, object> v && v.Count > 0)
                .OrderBy(kv => Regex.Matches(kv.Key.Substring(prefix.Length), @"\d+").Cast<Match>().Select(x => int.Parse(x.Value)).FirstOrDefault())
                .Select(kv => (Dictionary<string, object>)kv.Value).ToList();

        static Dictionary<string, object> Child(Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        static string TriggerOf(Dictionary<string, object> d) =>
            Child(d, "Trigger") is Dictionary<string, object> t && t.TryGetValue("TriggerID", out var id) ? id as string : null;

        static long? Number(Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out var v) && v is long n ? n : (long?)null;

        static bool IsNothing(object node) => node is string s && s == "RETURN_SUCCESS";

        static bool Same(object a, object b) => BfbsSchema.ToJson(a, false) == BfbsSchema.ToJson(b, false);

        void Reference(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            _references[name] = _references.TryGetValue(name, out int n) ? n + 1 : 1;
        }

        // ---- Shared tables ----

        void Shared()
        {
            var timing = new Dictionary<string, object>();
            foreach (var path in Bins("world/data/pokemon/ai_action/define/"))
                timing[Camel(path.Split('/')[5])] = Clean("", Decode(path));
            foreach (var path in Bins("world/data/pokemon/ai/pokemon_time_zone_data/"))
                timing["timeZones"] = Clean("", Decode(path));
            Files["timing.json"] = timing;

            var wild = new Dictionary<string, object>();
            foreach (var name in new[] { "wild_pokemon_common_data", "wild_pokemon_action_data", "wild_pokemon_action_probability_data" })
                foreach (var path in Bins($"world/data/pokemon/wild/{name}/"))
                    wild[Camel(name.Replace("wild_pokemon_", ""))] = Clean("", Decode(path));
            Files["wild.json"] = wild;

            // The interest-driven AI of the player's own Pokemon (picnic, partner): natures, presets, pair scenes.
            var partner = new Dictionary<string, object>();
            foreach (var name in new[] { "pokemon_ai_nature_table", "pokemon_action_preset_data", "pokemon_action_loop_data", "pokemon_interest_common_param",
                         "pokemon_nature_categorize", "pokemon_between_action_table", "pokemon_species_cant_action" })
                foreach (var path in Bins($"world/data/pokemon/ai/{name}/"))
                    partner[Camel(name.Replace("pokemon_", ""))] = Clean("", Decode(path));
            Files["partner.json"] = partner;
        }

        // ---- Species ----

        void Species()
        {
            var profiles = new SortedDictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            var unknown = new SortedSet<string>(StringComparer.Ordinal);
            void Put(string folder, string section, bool skipPlaceholders)
            {
                foreach (var path in Bins(folder))
                {
                    var m = Regex.Match(path, @"/(\d{4})_(\d{2})_(\d{2})\.bin$");
                    if (!m.Success) continue;
                    int internalNo = int.Parse(m.Groups[1].Value), form = int.Parse(m.Groups[2].Value), variant = int.Parse(m.Groups[3].Value);
                    int national = _national(internalNo);
                    string id = national > 0 ? _speciesName(national, form) : null;
                    if (id == null) { unknown.Add($"{internalNo}_{form}_{variant}"); continue; }
                    var record = Decode(path) as Dictionary<string, object>;
                    if (record == null) continue;
                    if (skipPlaceholders && IsPlaceholder(record)) continue;
                    var value = Profile(record);
                    if (!profiles.TryGetValue(id, out var p))
                        profiles[id] = p = new Dictionary<string, object> { ["species"] = id, ["dex"] = (long)national, ["form"] = (long)form };
                    if (variant == 0) p[section] = value;
                    else if (!p.TryGetValue(section, out var male) || !Same(male, value))
                        p[section + (variant == 1 ? "Female" : "Variant" + variant)] = value;
                }
            }
            Put("world/data/pokemon/wild/wild_pokemon_single_data/", "wild", false);
            Put("world/data/pokemon/wild/wild_pokemon_group_data/", "herd", true);
            Put("world/data/pokemon/wild/wild_pokemon_special_data/", "special", true);
            Put("world/data/pokemon/ai/pokemon_species_table/", "partner", false);
            Put("world/data/pokemon/ai/pokemon_species_behavior_data/", "clipSets", false);
            foreach (var p in profiles) Files["species/" + p.Key + ".json"] = p.Value;
            Log.Add($"species: {profiles.Count} profiles" + (unknown.Count > 0 ? $"; {unknown.Count} records without a species ({string.Join(", ", unknown.Take(10))}...)" : ""));
        }

        // A herd / special record that only holds defaults (Pikachu's herd file: a dummy root, no senses).
        static bool IsPlaceholder(Dictionary<string, object> r)
        {
            var roots = Child(r, "BasicAction");
            return roots == null || roots.Values.All(v => v is string s && (s.StartsWith("DUMMY_", StringComparison.Ordinal) || s == "NONE"));
        }

        // The wild record in our words (other records: their fields camelCased).
        Dictionary<string, object> Profile(Dictionary<string, object> r)
        {
            var p = new Dictionary<string, object>();
            foreach (var kv in r)
            {
                switch (kv.Key)
                {
                    case "BasicAction":
                        var routine = new Dictionary<string, object>();
                        foreach (var t in (Dictionary<string, object>)kv.Value)
                        {
                            Reference(t.Value as string);
                            routine[Camel(t.Key.Replace("ActionID", ""))] = t.Value;
                        }
                        p["routine"] = routine; // the root tree per time of day
                        break;
                    case "InstinctAction":
                        var needs = new Dictionary<string, object>();
                        foreach (var n in (Dictionary<string, object>)kv.Value)
                        {
                            var v = (Dictionary<string, object>)n.Value;
                            var need = new Dictionary<string, object>();
                            if (v.TryGetValue("AddSpeed", out var add)) need["rise"] = add;
                            if (v.TryGetValue("SubSpeed", out var sub)) need["fall"] = sub;
                            if (v.TryGetValue("ActionID", out var tree)) { need["tree"] = tree; Reference(tree as string); }
                            if (!need.ContainsKey("tree")) continue; // a need it doesn't have
                            needs[n.Key.Replace("Action", "").Replace("ing", "").ToLowerInvariant() switch { "eat" => "eat", "rest" => "rest", "sleep" => "sleep", var x => x }] = need;
                        }
                        p["needs"] = needs;
                        break;
                    case "ObjectRecognition":
                        var senses = (Dictionary<string, object>)kv.Value;
                        var sense = new Dictionary<string, object>();
                        if (Child(senses, "Conscious") is Dictionary<string, object> aware && aware.Count > 0) sense["aware"] = Clean("", aware);
                        if (Child(senses, "Congnitive") is Dictionary<string, object> notice && notice.Count > 0) sense["notice"] = Clean("", notice);
                        if (Child(senses, "PlayerReaction") is Dictionary<string, object> player && player.Count > 0) sense["player"] = Reaction(player);
                        var others = new List<object>();
                        for (int i = 1; i <= 4; i++)
                            if (Child(senses, "PokemonReaction_" + i) is Dictionary<string, object> other && other.Count > 0) others.Add(Reaction(other));
                        if (others.Count > 0) sense["pokemon"] = others;
                        p["senses"] = sense;
                        break;
                    case "PopProbability":
                        // The chance to appear already eating / resting / sleeping, per time of day (-1 = no change).
                        var spawn = new Dictionary<string, object>();
                        foreach (var zone in (Dictionary<string, object>)kv.Value)
                        {
                            var chances = new Dictionary<string, object>();
                            if (zone.Value is Dictionary<string, object> z)
                                foreach (var c in z)
                                    if (c.Value is long n && n >= 0) chances[c.Key.Replace("Probability", "").Replace("ing", "").ToLowerInvariant()] = n;
                            if (chances.Count > 0) spawn[Camel(zone.Key.Replace("Probability", ""))] = chances;
                        }
                        if (spawn.Count > 0) p["spawnState"] = spawn;
                        break;
                    default:
                        if (kv.Value is Dictionary<string, object> empty && empty.Count == 0) break;
                        p[Camel(kv.Key)] = Clean(kv.Key, kv.Value);
                        break;
                }
            }
            return p;
        }

        Dictionary<string, object> Reaction(Dictionary<string, object> r)
        {
            var o = new Dictionary<string, object>();
            foreach (var kv in r)
            {
                switch (kv.Key)
                {
                    case "ThreatLevel": o["attitude"] = Clean("", kv.Value); break;
                    case "DevNo": o["species"] = Clean(kv.Key, kv.Value); break;
                    case "FormNo": o["form"] = kv.Value; break;
                    case "ConsciousActionID": o["onAware"] = kv.Value; Reference(kv.Value as string); break;
                    case "CongnitiveActionID": o["onNotice"] = kv.Value; Reference(kv.Value as string); break;
                    default: o[Camel(kv.Key)] = Clean(kv.Key, kv.Value); break;
                }
            }
            return o;
        }

        // ---- Unresolved names ----

        void Unresolved()
        {
            // Names used in other tables' fields (…ActionID / …ReactionID / …ID) count too.
            foreach (var file in Files.Values) CountFieldReferences(file);
            var missing = _references.Where(kv => !_defined.ContainsKey(kv.Key) && kv.Key != "NONE")
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => (object)(long)kv.Value);
            Files["unresolved.json"] = missing;
            Log.Add($"defined: {_defined.Count} names; used but defined nowhere: {missing.Count}");
        }

        void CountFieldReferences(object o)
        {
            if (o is Dictionary<string, object> d)
                foreach (var kv in d)
                {
                    if (kv.Value is string s && Regex.IsMatch(kv.Key, @"(Id|Action|Reaction|tree|onAware|onNotice)$") && Regex.IsMatch(s, "^[A-Z0-9_]+$")
                        && !_defined.ContainsKey(s)) Reference(s);
                    else CountFieldReferences(kv.Value);
                }
            else if (o is List<object> l) foreach (var x in l) CountFieldReferences(x);
        }

        // ---- Names and values ----

        // "MoveSpeedRate" -> "moveSpeedRate", "ActionID" -> "actionId", "IsBlink" -> "isBlink".
        static string Camel(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            // snake_case table names: "ai_nature_table" -> "aiNatureTable"
            if (key.Contains("_") && key == key.ToLowerInvariant())
                key = string.Concat(key.Split('_').Select((w, i) => i == 0 || w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w.Substring(1)));
            key = Regex.Replace(key, "ID$", "Id");
            key = key.Replace("ID_", "Id_");
            return char.ToLowerInvariant(key[0]) + key.Substring(1);
        }

        // Field values in our style: enum names lowercase; names that point at trees / presets / conditions as they are;
        // species (DevID names) as Showdown ids.
        object Clean(string key, object value)
        {
            switch (value)
            {
                case Dictionary<string, object> d:
                    var o = new Dictionary<string, object>();
                    foreach (var kv in d) o[Camel(kv.Key)] = Clean(kv.Key, kv.Value);
                    return o;
                case List<object> l:
                    return l.Select(x => Clean(key, x)).ToList();
                case string s:
                    if (s.StartsWith("DEV_", StringComparison.Ordinal)) return SpeciesOf(s);
                    if (Regex.IsMatch(key ?? "", "(ID|Id|ActionID|TriggerID)$") || key == "Pokemons") return s;
                    // A behaviour's name in a list (the partner tables' FavoriteActionList) stays a name.
                    if (_defined.ContainsKey(s) || _references.ContainsKey(s) || s.StartsWith("DUMMY_", StringComparison.Ordinal)) return s;
                    return Regex.IsMatch(s, "^[A-Z0-9_]+$") ? s.ToLowerInvariant() : s;
                default:
                    return value;
            }
        }

        object SpeciesOf(string devName)
        {
            if (devName == "DEV_NULL") return null;
            foreach (var schema in _schemas.Values)
                if (schema.TryEnumNumber(devName, out long number))
                {
                    int national = _national((int)number);
                    return (national > 0 ? _speciesName(national, 0) : null) ?? devName;
                }
            return devName;
        }
    }
}
