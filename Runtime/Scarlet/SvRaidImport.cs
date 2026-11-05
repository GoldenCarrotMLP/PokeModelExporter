using System;
using System.Collections.Generic;
using System.Linq;
using PokeModelExporter.Formats;

namespace PokeModelExporter.Scarlet
{
    // Scarlet/Violet's Tera Raid tables (world/data/raid/*, decoded with their .bfbs schemas) in PokeSim's raid format
    // (PokeSim's Docs/tera-raid-research.md): one plain object, written by the caller as raids.json.
    //
    //   bosses: [ { stars, region (paldea | kitakami | blueberry), species (Showdown id), ver (scarlet | violet | ""),
    //               hpTimes, shield: { hp, time, cancel, rate, gemRate, otherRate } (null: none),
    //               actions: [ { by: hp|time, at, kind: move|reset-boss|reset-players|gem, move } ],
    //               clock, fixed, random (reward table ids) } ]
    //   fixed:  { <table id>: [ { item (the game's ITEMID_ name, or POKE / GEM), num, who: host|client|"" } ] }
    //   random: { <table id>: [ { item, num, rate, rare } ] }
    //   slots:  [ per star level 1-7: [ { num, rate } ] ]   how many random draws
    //   raidFrames: { <move number>: frames }   how long a move's effect plays in a raid (wazaeffecttable)
    // Time values are the game's: % of the time LEFT (Serebii's "x% Time Remaining").
    public sealed class SvRaidImport
    {
        readonly Func<string, byte[]> _read;
        readonly Func<int, int, string> _speciesName;  // national dex, form -> Showdown id (null = unknown)
        readonly Func<int, string> _moveName;          // move number -> Showdown move name (null = unknown)
        readonly Dictionary<string, BfbsSchema> _schemas = new Dictionary<string, BfbsSchema>();
        public readonly List<string> Log = new List<string>();

        public SvRaidImport(Func<string, byte[]> read, Func<int, int, string> speciesName, Func<int, string> moveName)
        {
            _read = read;
            _speciesName = speciesName;
            _moveName = moveName;
        }

        (object root, BfbsSchema schema) Decode(string binPath)
        {
            var data = _read(binPath);
            if (data == null) return (null, null);
            string schemaPath = binPath.Substring(0, binPath.Length - 4) + ".bfbs";
            if (!_schemas.TryGetValue(schemaPath, out var schema))
            {
                var bfbs = _read(schemaPath);
                if (bfbs == null) return (null, null);
                _schemas[schemaPath] = schema = new BfbsSchema(bfbs);
            }
            return (schema.Decode(data), schema);
        }

        static List<object> Values(object root) => root is Dictionary<string, object> d && d.TryGetValue("values", out var v) && v is List<object> l ? l : new List<object>();
        static Dictionary<string, object> Obj(object o, string key) => o is Dictionary<string, object> d && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;
        static string Str(Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out var v) ? v?.ToString() : null;
        static long Num(Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out var v) && v != null && long.TryParse(v.ToString(), out long n) ? n : 0;
        // An enum value's number: by its name, or the number itself (values the schema doesn't name come out as numbers).
        static bool EnumNumber(BfbsSchema schema, string value, out long number)
        {
            number = 0;
            if (string.IsNullOrEmpty(value) || schema == null) return false;
            return long.TryParse(value, out number) || schema.TryEnumNumber(value, out number);
        }

        static bool Bool(Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out var v) && v is bool b && b;

        public Dictionary<string, object> Run()
        {
            var bosses = new List<object>();
            var tables = new HashSet<string>();
            foreach (var (prefix, region) in new[] { ("raid_enemy", "paldea"), ("su1_raid_enemy", "kitakami"), ("su2_raid_enemy", "blueberry") })
                for (int stars = 1; stars <= 6; stars++)
                {
                    string path = $"world/data/raid/{prefix}_{stars:D2}/{prefix}_{stars:D2}_array.bin";
                    var (root, schema) = Decode(path);
                    if (root == null) continue;
                    int count = 0;
                    foreach (var row in Values(root))
                    {
                        var info = Obj(row, "raidEnemyInfo");
                        var para = Obj(info, "bossPokePara");
                        var desc = Obj(info, "bossDesc");
                        if (para == null || desc == null) continue;
                        string dev = Str(para, "devId");
                        if (!EnumNumber(schema, dev, out long national) || national <= 0) continue;
                        string species = _speciesName((int)national, (int)Num(para, "formId"));
                        if (species == null) continue;
                        var boss = new Dictionary<string, object>
                        {
                            ["stars"] = (long)stars, ["region"] = region, ["species"] = species,
                            ["ver"] = Str(info, "romVer") switch { "TYPE_A" => "scarlet", "TYPE_B" => "violet", _ => "" },
                            ["hpTimes"] = Math.Max(1, Num(desc, "hpCoef") / 100),
                            ["fixed"] = Num(info, "dropTableFix").ToString(), ["random"] = Num(info, "dropTableRandom").ToString(),
                        };
                        tables.Add((string)boss["fixed"]);
                        tables.Add((string)boss["random"]);
                        long cancel = Num(desc, "powerChargeCancelDamage");
                        boss["shield"] = cancel <= 0 ? null : new Dictionary<string, object>
                        {
                            ["hp"] = Num(desc, "powerChargeTrigerHp"), ["time"] = Num(desc, "powerChargeTrigerTime"), ["cancel"] = cancel,
                            ["rate"] = Num(desc, "powerChargeDamageRate"), ["gemRate"] = Num(desc, "powerChargeGemDamageRate"),
                            ["otherRate"] = Num(desc, "powerChargeChangeGemDamageRate"),
                        };
                        var actions = new List<object>();
                        for (int i = 1; i <= 6; i++)
                        {
                            var a = Obj(desc, "extraAction" + i);
                            string kind = Str(a, "action"), timing = Str(a, "timming");
                            if (kind == null || timing == null) continue;
                            string ours = kind switch
                            {
                                "WAZA" => "move", "BOSS_STATUS_RESET" => "reset-boss", "PLAYER_STATUS_RESET" => "reset-players", "GEM_COUNT" => "gem", _ => null,
                            };
                            if (ours == null) continue;
                            string move = null;
                            if (ours == "move")
                            {
                                string waza = Str(a, "wazano");
                                if (!EnumNumber(schema, waza, out long moveNo) || (move = _moveName((int)moveNo)) == null) continue;
                            }
                            actions.Add(new Dictionary<string, object> { ["by"] = timing == "TIME" ? "time" : "hp", ["at"] = Num(a, "value"), ["kind"] = ours, ["move"] = move ?? "" });
                        }
                        boss["actions"] = actions;
                        var time = Obj(info, "raidTimeData");
                        boss["clock"] = Num(time, "gameLimit");
                        bosses.Add(boss);
                        count++;
                    }
                    Log.Add($"{prefix}_{stars:D2}: {count} bosses");
                }
            return new Dictionary<string, object>
            {
                ["bosses"] = bosses,
                ["fixed"] = Rewards("world/data/raid/raid_fixed_reward_item/raid_fixed_reward_item_array.bin", false, tables),
                ["random"] = Rewards("world/data/raid/raid_lottery_reward_item/raid_lottery_reward_item_array.bin", true, tables),
                ["slots"] = Slots(),
                ["raidFrames"] = RaidFrames(),
                ["scales"] = Scales(),
            };
        }

        // The boss's display scale in the den per species (raid_poke_scale: 0.55-8, mostly 1.75-4; Charizard 1.75,
        // Pikachu 4), by its Showdown id; a form's own row where it has one, else the species' first.
        Dictionary<string, object> Scales()
        {
            var result = new Dictionary<string, object>();
            var (root, schema) = Decode("world/data/raid/raid_poke_scale/raid_poke_scale_array.bin");
            foreach (var row in Values(root))
            {
                var d = row as Dictionary<string, object>;
                if (!EnumNumber(schema, Str(d, "monsno"), out long national) || national <= 0) continue;
                string species = _speciesName((int)national, (int)Num(d, "formno"));
                if (species == null || result.ContainsKey(species)) continue;
                if (d == null || !d.TryGetValue("scale", out var v) || !double.TryParse(v?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double scale) || scale <= 0) continue;
                result[species] = scale;
            }
            Log.Add($"raid_poke_scale: {result.Count} species");
            return result;
        }

        Dictionary<string, object> Rewards(string path, bool random, HashSet<string> wanted)
        {
            var result = new Dictionary<string, object>();
            foreach (var row in Values(Decode(path).root))
            {
                if (!(row is Dictionary<string, object> t)) continue;
                string id = Num(t, "table_name").ToString();
                var items = new List<object>();
                foreach (var kv in t.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!kv.Key.StartsWith("reward_item_", StringComparison.Ordinal) || !(kv.Value is Dictionary<string, object> e) || e.Count == 0) continue;
                    string item = Str(e, "itemID") ?? Str(e, "category");
                    if (item == null) continue;
                    var entry = new Dictionary<string, object> { ["item"] = item, ["num"] = Math.Max(1, Num(e, "num")) };
                    if (random) { entry["rate"] = Num(e, "rate"); entry["rare"] = Bool(e, "rare_item_flag"); }
                    else entry["who"] = Str(e, "subject_type") switch { "HOST" => "host", "CLIENT" => "client", _ => "" };
                    items.Add(entry);
                }
                result[id] = items;
            }
            Log.Add($"{(random ? "random" : "fixed")} rewards: {result.Count} tables ({result.Keys.Count(wanted.Contains)} used)");
            return result;
        }

        List<object> Slots()
        {
            var result = new List<object>();
            foreach (var row in Values(Decode("world/data/raid/raid_lottery_reward_slot/raid_lottery_reward_slot_array.bin").root))
            {
                var slots = new List<object>();
                if (row is Dictionary<string, object> r)
                    foreach (var kv in r.OrderBy(k => k.Key, StringComparer.Ordinal))
                        if (kv.Value is Dictionary<string, object> s && s.Count > 0)
                            slots.Add(new Dictionary<string, object> { ["num"] = Num(s, "slot_num"), ["rate"] = Num(s, "rate") });
                result.Add(slots);
            }
            return result;
        }

        Dictionary<string, object> RaidFrames()
        {
            var result = new Dictionary<string, object>();
            var (root, schema) = Decode("world/data/battle/wazaeffecttable/wazaeffecttable_array.bin");
            foreach (var row in Values(root))
            {
                if (!(row is Dictionary<string, object> r)) continue;
                long frames = Num(r, "raidFrameLength");
                string waza = Str(r, "waza");
                if (frames <= 0 || !EnumNumber(schema, waza, out long move) || move <= 0) continue;
                result[move.ToString()] = frames;
            }
            Log.Add($"raid frame lengths: {result.Count} moves");
            return result;
        }
    }
}
