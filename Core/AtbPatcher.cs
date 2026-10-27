using System.Text.Json;

namespace LANoireLauncher.Core;

public sealed record AtbSetCount(int Total, int Original, int Patched);

public sealed class AtbStatus
{
    public string Label { get; init; } = "";
    public bool CanApply { get; init; }
    public bool CanRevert { get; init; }
    public Dictionary<string, AtbSetCount> Sets { get; init; } = new();
}

/// <summary>
/// Reversible "remaster-style" attribute patches. The values live in out/attribute/root.atb.pc,
/// stored uncompressed in final\pc\out.wad.pc, and were found by comparing the PC and PS4 attribute
/// databases of the user's own copies. File size and layout never change. Every edit is verified
/// against the expected original bytes first; if anything differs nothing is written. Original
/// bytes go to a journal in %AppData%, so Revert restores them exactly.
///
/// Sets: "fog" (fog, horizon fog, high-LOD draw distance, DOF far plane), "lighting" (bloom,
/// light radius, brightness/contrast; experimental) and "vehicles" (high/medium-LOD vehicle counts).
/// Applying is declarative: afterwards exactly the chosen sets are patched and the others are original.
/// </summary>
public static class AtbPatcher
{
    private const string Target = "out/attribute/root.atb.pc";
    private const long TargetSize = 14502828;

    public static readonly string[] SetNames = { "fog", "lighting", "vehicles" };

    private sealed record Edit(string Set, string Property, long Offset, string Old, string New, string Safe);

    // Offsets are relative to the start of root.atb.pc. Old/New/Safe are the 4 raw bytes in hex.
    // Safe differs from New only where the remaster sets fog start == fog end (zero-length range).
    private static readonly Edit[] Edits =
    {
        new("lighting", "RadiusModifier", 31927, "0000803f", "3333333f", "3333333f"),
        new("lighting", "RadiusModifier", 42032, "0000803f", "3333333f", "3333333f"),
        new("lighting", "RadiusModifier", 47174, "0000803f", "3333333f", "3333333f"),
        new("lighting", "RadiusModifier", 51980, "0000803f", "3333333f", "3333333f"),
        new("lighting", "RadiusModifier", 58090, "0000803f", "3333333f", "3333333f"),
        new("lighting", "RadiusModifier", 67920, "0000803f", "3333333f", "3333333f"),
        new("lighting", "RadiusModifier", 72789, "0000803f", "3333333f", "3333333f"),
        new("vehicles", "MaxVehicleHighLod", 3996062, "05000000", "0a000000", "0a000000"),
        new("vehicles", "MaxVehicleMediumLod", 3996071, "0c000000", "18000000", "18000000"),
        new("lighting", "AmbientFalloffStart", 7877645, "00000000", "0000f041", "0000f041"),
        new("lighting", "BrightnessIntensity", 9092462, "6666263f", "cdcc4c3f", "cdcc4c3f"),
        new("lighting", "ContrastIntensity", 9092477, "9a99193f", "0000803f", "0000803f"),
        new("fog", "DofFarFocus", 9100221, "00004843", "00e0f944", "00e0f944"),
        new("fog", "DofFar", 9100230, "0000e143", "0000fa44", "0000fa44"),
        new("fog", "DofFarFocus", 9100755, "00004843", "00e0f944", "00e0f944"),
        new("fog", "DofFar", 9100764, "0000e143", "0000fa44", "0000fa44"),
        new("lighting", "BloomIntensity", 9107560, "0000803f", "7b142e3f", "7b142e3f"),
        new("lighting", "BloomThreshold", 9107569, "ec51b83d", "0ad7a33d", "0ad7a33d"),
        new("lighting", "BloomThreshold", 9108952, "00000000", "cdcc4c3e", "cdcc4c3e"),
        new("lighting", "BloomIntensity", 9109456, "6666663f", "6666263f", "6666263f"),
        new("lighting", "BloomThresholdFactor", 14235139, "e17a943e", "e17a943f", "e17a943f"),
        new("lighting", "BloomThresholdFactor", 14235913, "e17a943e", "e17a943f", "e17a943f"),
        new("lighting", "BloomThresholdFactor", 14236687, "e17a943e", "e17a943f", "e17a943f"),
        new("lighting", "BloomThresholdFactor", 14237958, "e17a943e", "e17a943f", "e17a943f"),
        new("lighting", "BloomThresholdFactor", 14238732, "e17a943e", "e17a943f", "e17a943f"),
        new("lighting", "BloomThresholdFactor", 14239506, "e17a943e", "e17a943f", "e17a943f"),
        new("fog", "HighLodDrawDistance", 14240996, "0000c842", "00004843", "00004843"),
        new("fog", "HighLodDrawDistance", 14241770, "0000c842", "00004843", "00004843"),
        new("lighting", "BloomThresholdFactor", 14242469, "0000803f", "00008040", "00008040"),
        new("fog", "HighLodDrawDistance", 14242544, "0000fa42", "00004843", "00004843"),
        new("lighting", "BloomThresholdFactor", 14243243, "0000803f", "00008040", "00008040"),
        new("fog", "HighLodDrawDistance", 14243318, "00001643", "00004843", "00004843"),
        new("lighting", "BloomThresholdFactor", 14244017, "0000803f", "00008040", "00008040"),
        new("lighting", "BloomThresholdFactor", 14244791, "0000803f", "00008040", "00008040"),
        new("lighting", "BloomThresholdFactor", 14245565, "0000803f", "00008040", "00008040"),
        new("lighting", "BloomThresholdFactor", 14246339, "0000803f", "00008040", "00008040"),
        new("fog", "FogEndDistance", 14246972, "0000fa44", "00409c46", "00409c46"),
        new("lighting", "BloomThresholdFactor", 14247113, "0000803f", "00008040", "00008040"),
        new("fog", "HighLodDrawDistance", 14247188, "00001643", "00004843", "00004843"),
        new("fog", "HighLodDrawDistance", 14247962, "0000c842", "00004843", "00004843"),
        new("lighting", "BloomThresholdFactor", 14251110, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14251884, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14252658, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14253432, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14254206, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14254980, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14255754, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14256528, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14257665, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14258439, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14259213, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14259987, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14260761, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14263366, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14264140, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14264914, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14265688, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14266462, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14267236, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14268010, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14268784, "3333b33e", "3333b33f", "3333b33f"),
        new("lighting", "BloomThresholdFactor", 14270119, "0000803f", "00008040", "00008040"),
        new("lighting", "BloomThresholdFactor", 14270893, "0000803f", "00008040", "00008040"),
        new("lighting", "BloomThresholdFactor", 14271667, "0000803f", "00008040", "00008040"),
        new("lighting", "BloomThresholdFactor", 14272441, "0000803f", "00008040", "00008040"),
        new("lighting", "BloomThresholdFactor", 14273215, "e17a943e", "e17a943f", "e17a943f"),
        new("lighting", "BloomThresholdFactor", 14273989, "e17a943e", "e17a943f", "e17a943f"),
        new("lighting", "BloomThresholdFactor", 14274763, "e17a943e", "e17a943f", "e17a943f"),
        new("lighting", "BloomThresholdFactor", 14275537, "0000803f", "00008040", "00008040"),
        new("lighting", "BloomThresholdFactor", 14276311, "0000803f", "00008040", "00008040"),
        new("fog", "FogStartDistance", 14277433, "00000000", "0080bb44", "0080bb44"),
        new("fog", "FogEndDistance", 14277442, "0000fa44", "0080bb44", "00a0bb44"),
        new("fog", "HorizonFogOffset", 14277468, "000080bf", "00000000", "00000000"),
        new("lighting", "BloomThresholdFactor", 14277583, "3333b33e", "3333b33f", "3333b33f"),
        new("fog", "FogStartDistance", 14278207, "0000c8c2", "0080bb44", "0080bb44"),
        new("fog", "FogEndDistance", 14278216, "00004844", "0080bb44", "00a0bb44"),
        new("fog", "HorizonFogOffset", 14278242, "000080bf", "00000000", "00000000"),
        new("lighting", "BloomThresholdFactor", 14278357, "3333b33e", "3333b33f", "3333b33f"),
        new("fog", "FogStartDistance", 14278981, "0000c8c2", "0080bb44", "0080bb44"),
        new("fog", "FogEndDistance", 14278990, "00004844", "0080bb44", "00a0bb44"),
        new("fog", "HorizonFogOffset", 14279016, "48e1fabe", "00000000", "00000000"),
        new("lighting", "BloomThresholdFactor", 14279131, "3333b33e", "3333b33f", "3333b33f"),
        new("fog", "FogStartDistance", 14279755, "0000c8c2", "0080bb44", "0080bb44"),
        new("fog", "FogEndDistance", 14279764, "00004844", "0080bb44", "00a0bb44"),
        new("fog", "HorizonFogOffset", 14279790, "000080bf", "00000000", "00000000"),
        new("lighting", "BloomThresholdFactor", 14279905, "3333b33e", "3333b33f", "3333b33f"),
        new("fog", "FogStartDistance", 14280529, "0000c8c2", "0080bb44", "0080bb44"),
        new("fog", "FogEndDistance", 14280538, "00001644", "0080bb44", "00a0bb44"),
        new("fog", "HorizonFogOffset", 14280564, "000080bf", "00000000", "00000000"),
        new("lighting", "BloomThresholdFactor", 14280679, "0000803f", "00008040", "00008040"),
        new("fog", "FogStartDistance", 14281303, "0000c8c2", "0080bb44", "0080bb44"),
        new("fog", "FogEndDistance", 14281312, "00800944", "0080bb44", "00a0bb44"),
        new("fog", "HorizonFogOffset", 14281338, "000000c0", "00000000", "00000000"),
        new("lighting", "BloomThresholdFactor", 14281453, "0000803f", "00008040", "00008040"),
        new("fog", "FogStartDistance", 14282077, "0000c8c2", "0080bb44", "0080bb44"),
        new("fog", "FogEndDistance", 14282086, "00800944", "0080bb44", "00a0bb44"),
        new("fog", "HorizonFogOffset", 14282112, "000000c0", "00000000", "00000000"),
        new("lighting", "BloomThresholdFactor", 14282227, "0000803f", "00008040", "00008040"),
        new("fog", "FogStartDistance", 14282851, "0000c8c2", "0080bb44", "0080bb44"),
        new("fog", "FogEndDistance", 14282860, "00001644", "0080bb44", "00a0bb44"),
        new("fog", "HorizonFogOffset", 14282886, "000000c0", "00000000", "00000000"),
        new("lighting", "BloomThresholdFactor", 14283001, "0000803f", "00008040", "00008040"),
        new("fog", "FogStartDistance", 14283625, "0000c8c2", "0080bb44", "0080bb44"),
        new("fog", "FogEndDistance", 14283634, "00001644", "0080bb44", "00a0bb44"),
        new("fog", "HorizonFogOffset", 14283660, "000000c0", "00000000", "00000000"),
        new("lighting", "BloomThresholdFactor", 14283775, "0000803f", "00008040", "00008040")
    };

    public static int EditCount => Edits.Length;
    public static int SetSize(string set) => Edits.Count(e => e.Set == set);

    public static string WadPath(GameInstall g) => Path.Combine(g.Root, "final", "pc", "out.wad.pc");

    private static uint Crc32(string s)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes(s))
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
        }
        return ~c;
    }

    private static bool TryFindEntry(Stream fs, out long offset, out long size)
    {
        offset = 0; size = 0;
        var head = new byte[8];
        fs.Position = 0;
        if (fs.Read(head, 0, 8) != 8 || head[0] != 'W' || head[1] != 'A' || head[2] != 'D') return false;
        uint count = BitConverter.ToUInt32(head, 4);
        if (count == 0 || count > 100000) return false;
        var toc = new byte[count * 12];
        fs.ReadExactly(toc, 0, toc.Length);
        uint want = Crc32(Target);
        for (int i = 0; i < count; i++)
        {
            if (BitConverter.ToUInt32(toc, i * 12) != want) continue;
            offset = BitConverter.ToUInt32(toc, i * 12 + 4);
            size = BitConverter.ToUInt32(toc, i * 12 + 8);
            return true;
        }
        return false;
    }

    /// <summary>Finds root.atb.pc and checks it is the supported build. Throws with a readable message otherwise.</summary>
    private static long Locate(Stream fs)
    {
        if (!TryFindEntry(fs, out var off, out var size)) throw new InvalidOperationException("root.atb.pc was not found in out.wad.pc.");
        var magic = new byte[4];
        fs.Position = off;
        fs.ReadExactly(magic, 0, 4);
        if (size != TargetSize || magic[0] != 'A' || magic[1] != 'T' || magic[2] != 'B' || magic[3] != 4)
            throw new InvalidOperationException($"root.atb.pc is not the supported build (size {size:N0}). Refusing to touch it.");
        return off;
    }

    private static string ReadHex(Stream fs, long pos)
    {
        var b = new byte[4];
        fs.Position = pos;
        fs.ReadExactly(b, 0, 4);
        return Convert.ToHexString(b).ToLowerInvariant();
    }

    private static string JournalPath(string wad) => Path.Combine(LauncherConfig.Dir, "atb-journal",
        Crc32(Path.GetFullPath(wad).ToLowerInvariant()).ToString("x8") + ".json");

    private static Dictionary<string, string> LoadJournal(string wad)
    {
        try { var p = JournalPath(wad); if (File.Exists(p)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p)) ?? new(); }
        catch { /* unreadable journal -> fall back to built-in originals */ }
        return new();
    }

    private static void SaveJournal(string wad, Dictionary<string, string> j)
    {
        var p = JournalPath(wad);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, JsonSerializer.Serialize(j));
    }

    private static bool IsKnown(Edit ed, string cur) => cur == ed.Old || cur == ed.New || cur == ed.Safe;

    public static AtbStatus GetStatus(string wad)
    {
        if (!File.Exists(wad)) return new() { Label = "out.wad.pc not found at final\\pc\\out.wad.pc in the game folder." };
        try
        {
            using var fs = new FileStream(wad, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long baseOff;
            try { baseOff = Locate(fs); }
            catch (InvalidOperationException ex) { return new() { Label = ex.Message + " The patches are unavailable." }; }

            var counts = SetNames.ToDictionary(s => s, _ => new int[3]);   // total, original, patched
            int bad = 0;
            foreach (var ed in Edits)
            {
                var cur = ReadHex(fs, baseOff + ed.Offset);
                var c = counts[ed.Set];
                c[0]++;
                if (cur == ed.Old) c[1]++;
                else if (cur == ed.New || cur == ed.Safe) c[2]++;
                else bad++;
            }
            var sets = counts.ToDictionary(kv => kv.Key, kv => new AtbSetCount(kv.Value[0], kv.Value[1], kv.Value[2]));
            if (bad > 0)
                return new() { Sets = sets, Label = $"{bad} values are neither original nor patched (another mod?). The patches will not touch them." };

            int patched = sets.Values.Sum(s => s.Patched);
            string Describe(string name)
            {
                var s = sets[name];
                return s.Patched == 0 ? $"{name}: off" : s.Patched == s.Total ? $"{name}: on" : $"{name}: partly ({s.Patched}/{s.Total})";
            }
            return new()
            {
                Sets = sets,
                Label = string.Join("   ·   ", SetNames.Select(Describe)),
                CanApply = true,
                CanRevert = patched > 0,
            };
        }
        catch (Exception ex) { return new() { Label = "Could not read out.wad.pc: " + ex.Message }; }
    }

    /// <summary>
    /// Makes exactly the chosen sets patched and every other set original. An empty selection reverts everything.
    /// Returns how many values were written.
    /// </summary>
    public static int ApplySets(string wad, HashSet<string> chosen, bool exact, bool fullBackup)
    {
        if (chosen.Count > 0 && fullBackup && !File.Exists(wad + ".orig")) File.Copy(wad, wad + ".orig");
        using var fs = new FileStream(wad, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        long baseOff = Locate(fs);

        var states = Edits.Select(ed => (ed, cur: ReadHex(fs, baseOff + ed.Offset))).ToList();
        var bad = states.Where(s => !IsKnown(s.ed, s.cur)).ToList();
        if (bad.Count > 0)
            throw new InvalidOperationException($"{bad.Count} values are neither original nor patched ({bad[0].ed.Property} at {bad[0].ed.Offset}). Nothing was written.");

        var journal = LoadJournal(wad);
        foreach (var s in states) if (s.cur == s.ed.Old) journal[s.ed.Offset.ToString()] = s.ed.Old;
        SaveJournal(wad, journal);

        int written = 0;
        foreach (var s in states)
        {
            string target = chosen.Contains(s.ed.Set)
                ? (exact ? s.ed.New : s.ed.Safe)
                : (journal.TryGetValue(s.ed.Offset.ToString(), out var orig) ? orig : s.ed.Old);
            if (s.cur == target) continue;
            fs.Position = baseOff + s.ed.Offset;
            fs.Write(Convert.FromHexString(target), 0, 4);
            written++;
        }
        fs.Flush(true);
        return written;
    }

    public static int Revert(string wad) => ApplySets(wad, new HashSet<string>(), false, false);

    /// <summary>Deletes the optional out.wad.pc.orig copy, but only once nothing is patched any more.</summary>
    public static void RemoveBackupCopy(string wad)
    {
        var o = wad + ".orig";
        if (File.Exists(o) && !GetStatus(wad).CanRevert) File.Delete(o);
    }
}
