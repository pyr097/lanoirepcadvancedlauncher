using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace LANoireLauncher.Core;

/// <summary>
/// Startup check for what the launcher itself needs besides .NET. A missing item is listed with a clickable link
/// to Microsoft's download page. (If the .NET Desktop Runtime itself is missing, no managed code can run at all;
/// Windows' own .NET start-up dialog then offers the Microsoft download page instead.)
/// To check something else, add a line to <see cref="All"/>.
/// </summary>
public static class DependencyCheck
{
    private sealed record Dependency(string Name, string Needed, string Url, Func<bool> IsPresent);

    private static readonly Dependency[] All =
    {
        new("Microsoft Visual C++ Redistributable (x64)",
            "needed by the FPS overlay",
            "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist",
            HasVcRuntime),
    };

    private static bool HasVcRuntime()
    {
        try
        {
            var sys = Environment.SystemDirectory;
            return File.Exists(Path.Combine(sys, "vcruntime140.dll"))
                && File.Exists(Path.Combine(sys, "vcruntime140_1.dll"))
                && File.Exists(Path.Combine(sys, "msvcp140.dll"));
        }
        catch { return true; }      // if the check itself fails, don't nag
    }

    /// <summary>Returns true when the launcher should carry on (nothing missing, or the user chose to continue).</summary>
    public static bool Run()
    {
        try
        {
            var missing = All.Where(d => !d.IsPresent()).ToList();
            return missing.Count == 0 || Show(missing);
        }
        catch { return true; }
    }

    private static bool Show(List<Dependency> missing)
    {
        Brush Res(string key, Brush fallback) => Application.Current.TryFindResource(key) as Brush ?? fallback;

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock
        {
            Text = missing.Count == 1 ? "A required component is missing" : "Required components are missing",
            FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Install it from Microsoft (click the link), then start the launcher again:",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        foreach (var d in missing)
        {
            var link = new Hyperlink(new Run(d.Name))
            {
                NavigateUri = new Uri(d.Url),
                Foreground = Res("AccentHover", Brushes.OrangeRed),
            };
            link.RequestNavigate += (_, e) =>
            {
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                e.Handled = true;
            };
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            line.Inlines.Add(new Run("•  "));
            line.Inlines.Add(link);
            line.Inlines.Add(new Run("  (" + d.Needed + ")") { Foreground = Res("Muted", Brushes.Gray) });
            panel.Children.Add(line);
        }

        var w = new Window
        {
            Title = "L.A. Noire Advanced Launcher",
            Width = 470, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = true,
            Background = Res("Bg", Brushes.Black), Foreground = Res("Ink", Brushes.White),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 13,
        };

        var cont = new Button { Content = "Continue anyway", IsDefault = true, Padding = new Thickness(18, 7, 18, 7) };
        if (Application.Current.TryFindResource("PrimaryButton") is Style primary) cont.Style = primary;
        cont.Click += (_, _) => w.DialogResult = true;
        var exit = new Button { Content = "Exit", IsCancel = true, Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(8, 0, 0, 0) };
        exit.Click += (_, _) => w.DialogResult = false;
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0),
            Children = { cont, exit },
        });

        w.Content = panel;
        return w.ShowDialog() == true;
    }
}
