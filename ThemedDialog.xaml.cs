using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MarkdownPad;

public partial class ThemedDialog : Window
{
    /// <summary>The id of the button the user chose (or "cancel" if dismissed).</summary>
    public string Choice { get; private set; } = "cancel";

    private Button? _primary;

    public ThemedDialog()
    {
        InitializeComponent();
        // Let the user drag the borderless dialog by its body.
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                try { DragMove(); } catch { }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Choice = "cancel"; Close(); }
            else if (e.Key == Key.Enter && _primary != null)
            { Choice = (string)_primary.Tag; Close(); }
        };
    }

    /// <summary>
    /// Shows a themed modal dialog. Each button is (label, id, isPrimary).
    /// Returns the id of the chosen button, or "cancel" if dismissed.
    /// </summary>
    public static string Show(Window? owner, string title, string message,
        (string label, string id, bool primary)[] buttons, bool warning = false)
    {
        var d = new ThemedDialog();
        if (owner != null && owner.IsLoaded) d.Owner = owner;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        d.TitleText.Text = title;
        d.MessageText.Text = message;
        if (warning)
            d.AccentDot.Background = new SolidColorBrush(Color.FromRgb(0xE5, 0xA5, 0x0A));

        foreach (var (label, id, primary) in buttons)
        {
            var btn = new Button
            {
                Content = label,
                MinWidth = 88,
                Margin = new Thickness(10, 0, 0, 0),
                Tag = id,
                Style = (Style)d.FindResource(primary ? "DlgPrimary" : "DlgNormal")
            };
            btn.Click += (_, _) => { d.Choice = id; d.Close(); };
            if (primary) { d._primary = btn; btn.IsDefault = true; }
            d.ButtonRow.Children.Add(btn);
        }

        d.ShowDialog();
        return d.Choice;
    }
}
