using System.Windows;

namespace MarkdownPad;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Opening a .md file from Explorer launches us with its path as the first argument ("%1").
        new MainWindow(e.Args.Length > 0 ? e.Args[0] : null).Show();
    }
}
