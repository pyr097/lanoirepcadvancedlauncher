using System.Windows;
using System.Windows.Controls;

namespace LANoireLauncher;

public enum FieldKind { Flag01, FlagYesNo, Text, Combo }

/// <summary>One editable INI setting and the control that shows it.</summary>
public sealed class Field
{
    public string? Section { get; }
    public string Key { get; }
    private readonly string _label, _help;
    private readonly FieldKind _kind;
    private readonly string[] _options;
    private readonly Func<string, string?>? _validator;
    private CheckBox? _check;
    private TextBox? _text;
    private ComboBox? _combo;

    public Field(string? section, string key, string label, FieldKind kind, string help,
                 string[]? options = null, Func<string, string?>? validator = null)
    { Section = section; Key = key; _label = label; _kind = kind; _help = help; _options = options ?? Array.Empty<string>(); _validator = validator; }

    public FrameworkElement Build()
    {
        if (_kind is FieldKind.Flag01 or FieldKind.FlagYesNo)
        {
            _check = new CheckBox { Content = _label, ToolTip = _help, Margin = new Thickness(0, 5, 0, 5) };
            return _check;
        }
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4), ToolTip = _help };
        row.Children.Add(new TextBlock { Text = _label, Width = 240, VerticalAlignment = VerticalAlignment.Center });
        if (_kind == FieldKind.Text) { _text = new TextBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left }; row.Children.Add(_text); }
        else
        {
            _combo = new ComboBox { Width = 180, IsEditable = true, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var o in _options) _combo.Items.Add(o);
            row.Children.Add(_combo);
        }
        return row;
    }

    public void SetValue(string? v)
    {
        if (v == null) return;
        switch (_kind)
        {
            case FieldKind.Flag01: _check!.IsChecked = v == "1"; break;
            case FieldKind.FlagYesNo: _check!.IsChecked = v.Equals("YES", StringComparison.OrdinalIgnoreCase); break;
            case FieldKind.Text: _text!.Text = v; break;
            case FieldKind.Combo:
                if (!_combo!.Items.Contains(v)) _combo.Items.Add(v);
                _combo.Text = v; break;
        }
    }

    public string GetValue() => _kind switch
    {
        FieldKind.Flag01 => _check!.IsChecked == true ? "1" : "0",
        FieldKind.FlagYesNo => _check!.IsChecked == true ? "YES" : "NO",
        FieldKind.Text => _text!.Text.Trim(),
        _ => _combo!.Text.Trim(),
    };

    public string? Validate() { var e = _validator?.Invoke(GetValue()); return e == null ? null : _label + ": " + e; }
}
