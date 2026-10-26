using System.Text;

namespace LANoireLauncher.Core;

/// <summary>
/// Line-preserving INI editor. Comments, ordering, blank lines, line endings and BOM
/// are kept exactly as found; only the value of the keys we touch changes.
/// Works for both "key=value" (lanvp.ini) and "Key:Value" (settings.ini) styles.
/// </summary>
public sealed class IniDocument
{
    private readonly List<string> _lines;
    private readonly char _sep;
    private readonly string _eol;
    private readonly bool _bom;
    public string FilePath { get; }

    private IniDocument(string path, char sep, List<string> lines, string eol, bool bom)
    { FilePath = path; _sep = sep; _lines = lines; _eol = eol; _bom = bom; }

    public static IniDocument Load(string path, char separator)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            throw new NotSupportedException("UTF-16 INI files are not supported: " + path);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        int skip = bom ? 3 : 0;
        var text = new UTF8Encoding(false).GetString(bytes, skip, bytes.Length - skip);
        string eol = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        return new IniDocument(path, separator, lines, eol, bom);
    }

    private int Find(string key, string? section)
    {
        string? cur = null;
        for (int i = 0; i < _lines.Count; i++)
        {
            var t = _lines[i].Trim();
            if (t.Length == 0 || t[0] == ';' || t[0] == '#') continue;
            if (t[0] == '[' && t[^1] == ']') { cur = t[1..^1]; continue; }
            if (section != null && !string.Equals(cur, section, StringComparison.OrdinalIgnoreCase)) continue;
            int s = t.IndexOf(_sep);
            if (s <= 0) continue;
            if (string.Equals(t[..s].Trim(), key, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>All key names in the file (used to discover options in a generated lanvp.ini).</summary>
    public IEnumerable<string> Keys()
    {
        foreach (var l in _lines)
        {
            var t = l.Trim();
            if (t.Length == 0 || t[0] == ';' || t[0] == '#' || t[0] == '[') continue;
            int s = t.IndexOf(_sep);
            if (s > 0) yield return t[..s].Trim();
        }
    }

    public string? Get(string key, string? section = null)
    {
        int i = Find(key, section);
        if (i < 0) return null;
        var t = _lines[i].Trim();
        return t[(t.IndexOf(_sep) + 1)..].Trim();
    }

    public void Set(string key, string value, string? section = null)
    {
        int i = Find(key, section);
        if (i >= 0)
        {
            var line = _lines[i];
            int s = line.IndexOf(_sep);
            _lines[i] = line[..(s + 1)] + value;
            return;
        }

        string newLine = key + _sep + value;
        int secStart = -1;
        if (section != null)
            for (int k = 0; k < _lines.Count; k++)
                if (string.Equals(_lines[k].Trim(), "[" + section + "]", StringComparison.OrdinalIgnoreCase)) { secStart = k; break; }

        if (secStart < 0)
        {
            int end = EndIndex();
            if (section != null) { _lines.Insert(end, ""); _lines.Insert(end + 1, "[" + section + "]"); end += 2; }
            _lines.Insert(end, newLine);
            return;
        }

        int lastKey = secStart;
        for (int k = secStart + 1; k < _lines.Count; k++)
        {
            var t = _lines[k].Trim();
            if (t.Length > 0 && t[0] == '[' && t[^1] == ']') break;
            if (t.Length > 0 && t[0] != ';' && t[0] != '#' && t.IndexOf(_sep) > 0) lastKey = k;
        }
        _lines.Insert(lastKey + 1, newLine);
    }

    private int EndIndex() => _lines.Count > 0 && _lines[^1].Length == 0 ? _lines.Count - 1 : _lines.Count;

    public void Save()
    {
        var data = new UTF8Encoding(_bom).GetPreamble()
            .Concat(new UTF8Encoding(false).GetBytes(string.Join(_eol, _lines))).ToArray();
        File.WriteAllBytes(FilePath, data);
    }
}
