using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Markdig;
using Microsoft.Win32;

namespace MarkdownPad;

public partial class MainWindow : Window
{
    private string? _currentFile;
    private bool _dirty;
    private bool _appDark = true;
    private bool _previewDark = true;
    private string _flavor = "GitHub";
    private bool _webReady;
    private bool _shellLoaded;

    private readonly DispatcherTimer _renderTimer;
    private IHighlightingDefinition? _hl;
    private TaskCompletionSource<bool>? _navTcs;

    private static readonly string[] Flavors =
        { "GitHub", "CommonMark", "Markdown Extra", "Extended (all)" };

    public MainWindow()
    {
        InitializeComponent();

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); Render(); };

        foreach (var f in Flavors) FlavorBox.Items.Add(f);
        FlavorBox.SelectedIndex = 0;

        LoadHighlighting();
        ApplyAppTheme();
        TuneEditorMargins();

        Editor.Text = SampleDocument;
        Editor.TextChanged += (_, _) =>
        {
            _dirty = true;
            UpdateTitle();
            UpdateStats();
            _renderTimer.Stop();
            _renderTimer.Start();
        };

        UpdateStats();
        Loaded += async (_, _) => await InitWebViewAsync();
        PreviewKeyDown += Window_PreviewKeyDown;
    }

    /// <summary>
    /// Gives the line-number gutter breathing room and pushes the editor text a
    /// little further right so it isn't jammed against the divider.
    /// </summary>
    private void TuneEditorMargins()
    {
        foreach (var m in Editor.TextArea.LeftMargins)
        {
            switch (m)
            {
                // Equal padding around the digits. The editor's own Padding adds
                // 8px to the left of the gutter, so left=0 here balances right=8.
                case ICSharpCode.AvalonEdit.Editing.LineNumberMargin lnm:
                    lnm.Margin = new Thickness(0, 0, 8, 0);
                    break;
                // The dotted separator: right margin = text indent past the gutter.
                case System.Windows.Shapes.Line line:
                    line.Margin = new Thickness(0, 0, 8, 0);
                    break;
            }
        }
    }

    // ---------- WebView ----------
    private async Task InitWebViewAsync()
    {
        try
        {
            var dataDir = Path.Combine(Path.GetTempPath(), "MarkdownPad.WebView2");
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment
                .CreateAsync(null, dataDir);
            await Preview.EnsureCoreWebView2Async(env);
            Preview.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Preview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Preview.CoreWebView2.NavigationCompleted += (_, _) => _navTcs?.TrySetResult(true);
            Preview.NavigationCompleted += (_, _) => _navTcs?.TrySetResult(true);
            // A click on a link inside the document would replace our persistent
            // preview shell. Intercept external links and open them in the browser.
            Preview.CoreWebView2.NavigationStarting += (_, e) =>
            {
                var uri = e.Uri ?? string.Empty;
                if (uri.StartsWith("http://") || uri.StartsWith("https://"))
                {
                    e.Cancel = true;
                    try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
                    catch { }
                }
            };
            _webReady = true;
            Render();
        }
        catch (Exception ex)
        {
            ThemedDialog.Show(this, "WebView2 unavailable",
                "The preview needs the Microsoft Edge WebView2 Runtime, which couldn't be started. " +
                "Install it from developer.microsoft.com/microsoft-edge/webview2 and reopen the app.\n\n" +
                ex.Message,
                new[] { ("OK", "ok", true) }, warning: true);
        }
    }

    // ---------- Syntax highlighting ----------
    private void LoadHighlighting()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("MarkdownPad.Markdown.xshd");
        if (stream == null) return;
        using var reader = new XmlTextReader(stream);
        _hl = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        Editor.SyntaxHighlighting = _hl;
    }

    private void ApplyEditorHighlightColors()
    {
        if (_hl == null) return;
        // Tune a couple of colors that read poorly on a light background.
        void Set(string name, string dark, string light)
        {
            var c = _hl.GetNamedColor(name);
            if (c?.Foreground == null) return;
            var hex = _appDark ? dark : light;
            c.Foreground = new SimpleHighlightingBrush(
                (Color)ColorConverter.ConvertFromString(hex));
        }
        Set("Heading", "#4FC1FF", "#0550AE");
        Set("Bold", "#E0E0E0", "#1F2328");
        Set("Italic", "#E0E0E0", "#1F2328");
        Set("Code", "#CE9178", "#A31515");
        Set("CodeBlock", "#CE9178", "#A31515");
        Set("Link", "#4EC9B0", "#0A7D6B");
        Set("Url", "#569CD6", "#0550AE");
        Set("Quote", "#6A9955", "#3E7A2E");
        Set("ListMarker", "#D7BA7D", "#8B6D00");
        Set("Rule", "#808080", "#6E7781");
        Set("Html", "#808080", "#6E7781");
        // Reassign to force the editor to repaint with new colors.
        Editor.SyntaxHighlighting = null;
        Editor.SyntaxHighlighting = _hl;
    }

    // ---------- Markdig flavors ----------
    private MarkdownPipeline BuildPipeline()
    {
        var b = new MarkdownPipelineBuilder();
        switch (_flavor)
        {
            case "CommonMark":
                break;
            case "Markdown Extra":
                b.UsePipeTables().UseGridTables().UseFootnotes()
                 .UseDefinitionLists().UseAbbreviations().UseEmphasisExtras();
                break;
            case "Extended (all)":
                b.UseAdvancedExtensions().UseEmojiAndSmiley()
                 .UseFootnotes().UseDefinitionLists().UseAbbreviations();
                break;
            default: // GitHub
                b.UseAdvancedExtensions().UseEmojiAndSmiley();
                break;
        }
        b.UseAutoLinks();
        return b.Build();
    }

    // ---------- Render preview ----------
    // The preview page (the "shell": CSS + theme + base href) is navigated only once.
    // On every edit we push the freshly rendered HTML into the existing DOM, so the
    // preview never reloads and the scroll position is preserved.
    private async void Render()
    {
        if (!_webReady) return;
        if (!_shellLoaded) { await RebuildShellAsync(); return; }
        await PushContentAsync();
    }

    private async Task RebuildShellAsync()
    {
        if (!_webReady) return;
        await NavigateAsync(WrapHtml(string.Empty));
        _shellLoaded = true;
        await PushContentAsync();
    }

    private async Task PushContentAsync()
    {
        try
        {
            var body = Markdown.ToHtml(Editor.Text ?? string.Empty, BuildPipeline());
            var json = JsonSerializer.Serialize(body); // safe JS string literal
            await Preview.CoreWebView2.ExecuteScriptAsync(
                "(function(){var c=document.getElementById('content');if(c){c.innerHTML=" + json + ";}})();");
        }
        catch { /* transient parse states while typing */ }
    }

    private Task NavigateAsync(string html)
    {
        _navTcs = new TaskCompletionSource<bool>();
        Preview.NavigateToString(html);
        return _navTcs.Task;
    }

    private string WrapHtml(string body)
    {
        var baseHref = "";
        if (_currentFile != null)
        {
            var dir = Path.GetDirectoryName(_currentFile);
            if (dir != null)
                baseHref = $"<base href=\"file:///{dir.Replace('\\', '/')}/\">";
        }
        var theme = _previewDark ? "dark" : "light";
        return $@"<!DOCTYPE html><html><head><meta charset=""utf-8"">{baseHref}
<style>{PreviewCss}</style></head>
<body class=""{theme}""><article id=""content"" class=""markdown-body"">{body}</article></body></html>";
    }

    // ---------- Themes ----------
    private void ApplyAppTheme()
    {
        var r = Application.Current.Resources;
        void B(string key, string hex) =>
            r[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        if (_appDark)
        {
            B("AppBg", "#1E1E1E"); B("PanelBg", "#252526"); B("AppFg", "#D4D4D4");
            B("MutedFg", "#9D9D9D"); B("Border", "#3C3C3C"); B("Accent", "#0E639C");
            B("AccentFg", "#FFFFFF"); B("Hover", "#37373D");
        }
        else
        {
            B("AppBg", "#FFFFFF"); B("PanelBg", "#F3F3F3"); B("AppFg", "#1F2328");
            B("MutedFg", "#6E7781"); B("Border", "#D0D7DE"); B("Accent", "#0969DA");
            B("AccentFg", "#FFFFFF"); B("Hover", "#E7ECF0");
        }

        // AvalonEdit doesn't pick up DynamicResource, set explicitly.
        var bg = _appDark ? "#1E1E1E" : "#FFFFFF";
        var fg = _appDark ? "#D4D4D4" : "#1F2328";
        var lineNo = _appDark ? "#858585" : "#6E7781";
        Editor.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
        Editor.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
        Editor.LineNumbersForeground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(lineNo));
        Editor.TextArea.SelectionBrush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(_appDark ? "#264F78" : "#ADD6FF"));

        ApplyEditorHighlightColors();
        if (AppThemeBtn != null) AppThemeBtn.Content = _appDark ? "App: Dark" : "App: Light";
    }

    // ---------- File operations ----------
    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var name = _currentFile != null ? Path.GetFileName(_currentFile) : "this document";
        var r = ThemedDialog.Show(this, "Unsaved changes",
            $"You have unsaved changes in {name}. Do you want to save them before continuing?",
            new[] { ("Save", "save", true), ("Don't Save", "discard", false), ("Cancel", "cancel", false) });
        if (r == "save") return DoSave();
        if (r == "discard") return true;
        return false; // cancel / dismissed
    }

    private void New_Click(object s, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        Editor.Text = "";
        _currentFile = null; _dirty = false; _shellLoaded = false;
        UpdateTitle(); Render(); Status("New document");
    }

    private void Open_Click(object s, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog
        {
            Filter = "Markdown|*.md;*.markdown;*.mdown;*.mkd;*.txt|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        Editor.Text = File.ReadAllText(dlg.FileName);
        _currentFile = dlg.FileName; _dirty = false; _shellLoaded = false;
        UpdateTitle(); Render(); Status($"Opened {Path.GetFileName(_currentFile)}");
    }

    private void Save_Click(object s, RoutedEventArgs e) => DoSave();
    private void SaveAs_Click(object s, RoutedEventArgs e) => DoSaveAs();

    private bool DoSave()
    {
        if (_currentFile == null) return DoSaveAs();
        File.WriteAllText(_currentFile, Editor.Text);
        _dirty = false; UpdateTitle(); Status($"Saved {Path.GetFileName(_currentFile)}");
        return true;
    }

    private bool DoSaveAs()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Markdown|*.md|All files|*.*",
            FileName = _currentFile != null ? Path.GetFileName(_currentFile) : "Untitled.md"
        };
        if (dlg.ShowDialog() != true) return false;
        File.WriteAllText(dlg.FileName, Editor.Text);
        _currentFile = dlg.FileName; _dirty = false; _shellLoaded = false;
        UpdateTitle(); Render(); Status($"Saved {Path.GetFileName(_currentFile)}");
        return true;
    }

    // ---------- Exports ----------
    private void ExportHtml_Click(object s, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "HTML|*.html", FileName = SuggestName(".html") };
        if (dlg.ShowDialog() != true) return;
        var body = Markdown.ToHtml(Editor.Text ?? "", BuildPipeline());
        File.WriteAllText(dlg.FileName, WrapHtml(body), Encoding.UTF8);
        Status($"Exported {Path.GetFileName(dlg.FileName)}");
    }

    private async void ExportPdf_Click(object s, RoutedEventArgs e)
    {
        if (!_webReady) { Status("Preview not ready"); return; }
        var dlg = new SaveFileDialog { Filter = "PDF|*.pdf", FileName = SuggestName(".pdf") };
        if (dlg.ShowDialog() != true) return;
        Status("Exporting PDF...");
        try
        {
            if (!_shellLoaded) await RebuildShellAsync(); else await PushContentAsync();
            var ok = await Preview.CoreWebView2.PrintToPdfAsync(dlg.FileName, null);
            Status(ok ? $"Exported {Path.GetFileName(dlg.FileName)}" : "PDF export failed");
        }
        catch (Exception ex) { Status("PDF export failed: " + ex.Message); }
    }

    private async void ExportWord_Click(object s, RoutedEventArgs e)
    {
        var pandoc = FindPandoc();
        if (pandoc == null)
        {
            ThemedDialog.Show(this, "pandoc not found",
                "Word export uses pandoc, which wasn't found on this PC. " +
                "Install it from pandoc.org/installing.html and try again.",
                new[] { ("OK", "ok", true) }, warning: true);
            return;
        }
        var dlg = new SaveFileDialog { Filter = "Word document|*.docx", FileName = SuggestName(".docx") };
        if (dlg.ShowDialog() != true) return;

        var tmp = Path.Combine(Path.GetTempPath(), "mdpad_" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(tmp, Editor.Text ?? "", new UTF8Encoding(false));
        var fmt = _flavor switch
        {
            "CommonMark" => "commonmark",
            "GitHub" => "gfm",
            _ => "markdown"
        };
        Status("Exporting Word document...");
        try
        {
            var ok = await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pandoc,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add(tmp);
                psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(fmt);
                psi.ArgumentList.Add("-o"); psi.ArgumentList.Add(dlg.FileName);
                using var p = Process.Start(psi)!;
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) throw new Exception(err);
                return true;
            });
            Status(ok ? $"Exported {Path.GetFileName(dlg.FileName)}" : "Word export failed");
        }
        catch (Exception ex) { Status("Word export failed: " + ex.Message); }
        finally { try { File.Delete(tmp); } catch { } }
    }

    private static string? FindPandoc()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            "pandoc",
            Path.Combine(home, "anaconda3", "Scripts", "pandoc.exe"),
            Path.Combine(home, "miniconda3", "Scripts", "pandoc.exe"),
            Path.Combine(home, "AppData", "Local", "Pandoc", "pandoc.exe"),
            @"C:\Program Files\Pandoc\pandoc.exe"
        };
        foreach (var c in candidates)
        {
            try
            {
                if (c == "pandoc")
                {
                    var psi = new ProcessStartInfo("pandoc", "--version")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                    using var p = Process.Start(psi);
                    if (p == null) continue;
                    p.WaitForExit(4000);
                    if (p.HasExited && p.ExitCode == 0) return "pandoc";
                }
                else if (File.Exists(c)) return c;
            }
            catch { }
        }
        return null;
    }

    // ---------- Formatting toolbar ----------
    private void Fmt_Click(object s, RoutedEventArgs e)
    {
        if (s is not System.Windows.Controls.Button b || b.Tag is not string tag) return;
        ApplyFormat(tag);
    }

    private void ApplyFormat(string tag)
    {
        switch (tag)
        {
            case "bold": Wrap("**", "**", "bold text"); break;
            case "italic": Wrap("*", "*", "italic text"); break;
            case "strike": Wrap("~~", "~~", "strikethrough"); break;
            case "code": Wrap("`", "`", "code"); break;
            case "h1": PrefixLine("# "); break;
            case "h2": PrefixLine("## "); break;
            case "h3": PrefixLine("### "); break;
            case "quote": PrefixLine("> "); break;
            case "ul": PrefixLine("- "); break;
            case "ol": PrefixLine("1. "); break;
            case "task": PrefixLine("- [ ] "); break;
            case "link": InsertSnippet("[", "](https://)", "link text", selectInner: true); break;
            case "image": InsertSnippet("![", "](https://)", "alt text", selectInner: true); break;
            case "hr": InsertBlock("\n---\n"); break;
            case "codeblock": InsertBlock("\n```\ncode\n```\n"); break;
            case "table":
                InsertBlock("\n| Column A | Column B |\n|----------|----------|\n| Cell 1   | Cell 2   |\n| Cell 3   | Cell 4   |\n");
                break;
        }
        Editor.Focus();
    }

    private void Wrap(string before, string after, string placeholder)
    {
        var sel = Editor.SelectedText;
        var text = string.IsNullOrEmpty(sel) ? placeholder : sel;
        var start = Editor.SelectionStart;
        Editor.Document.Replace(start, Editor.SelectionLength, before + text + after);
        Editor.SelectionStart = start + before.Length;
        Editor.SelectionLength = text.Length;
    }

    private void InsertSnippet(string before, string after, string placeholder, bool selectInner)
    {
        var sel = Editor.SelectedText;
        var text = string.IsNullOrEmpty(sel) ? placeholder : sel;
        var start = Editor.SelectionStart;
        Editor.Document.Replace(start, Editor.SelectionLength, before + text + after);
        Editor.SelectionStart = start + before.Length;
        Editor.SelectionLength = text.Length;
    }

    private void PrefixLine(string prefix)
    {
        var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
        Editor.Document.Insert(line.Offset, prefix);
    }

    private void InsertBlock(string block)
    {
        var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
        Editor.Document.Insert(line.Offset + line.Length, block);
    }

    // ---------- Theme / flavor toggles ----------
    private void Flavor_Changed(object s, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (FlavorBox.SelectedItem is string f) { _flavor = f; Render(); }
    }

    private void AppTheme_Click(object s, RoutedEventArgs e)
    {
        _appDark = !_appDark; ApplyAppTheme();
        Status($"App theme: {(_appDark ? "Dark" : "Light")}");
    }

    private async void PreviewTheme_Click(object s, RoutedEventArgs e)
    {
        _previewDark = !_previewDark;
        PreviewThemeBtn.Content = _previewDark ? "Preview: Dark" : "Preview: Light";
        if (_webReady && _shellLoaded)
            await Preview.CoreWebView2.ExecuteScriptAsync(
                "document.body.className=" + JsonSerializer.Serialize(_previewDark ? "dark" : "light") + ";");
        else
            Render();
        Status($"Preview theme: {(_previewDark ? "Dark" : "Light")}");
    }

    // ---------- Keyboard shortcuts ----------
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (e.Key)
            {
                case Key.S when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift): DoSaveAs(); e.Handled = true; break;
                case Key.S: DoSave(); e.Handled = true; break;
                case Key.O: Open_Click(sender, e); e.Handled = true; break;
                case Key.N: New_Click(sender, e); e.Handled = true; break;
                case Key.B: ApplyFormat("bold"); e.Handled = true; break;
                case Key.I: ApplyFormat("italic"); e.Handled = true; break;
                case Key.K: ApplyFormat("link"); e.Handled = true; break;
            }
        }
    }

    // ---------- Helpers ----------
    private void Status(string msg) => StatusText.Text = msg;

    private void UpdateStats()
    {
        var t = Editor.Text ?? "";
        var words = t.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
        StatsText.Text = $"{words} words   {t.Length} chars   |   {_flavor}";
    }

    private void UpdateTitle()
    {
        var name = _currentFile != null ? Path.GetFileName(_currentFile) : "Untitled";
        Title = $"{(_dirty ? "● " : "")}{name} — MarkdownPad";
    }

    private string SuggestName(string ext)
    {
        var baseName = _currentFile != null
            ? Path.GetFileNameWithoutExtension(_currentFile) : "Untitled";
        return baseName + ext;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
        base.OnClosing(e);
    }

    // ---------- Preview CSS (GitHub-like, dark + light) ----------
    private const string PreviewCss = @"
:root { color-scheme: light dark; }
html,body { margin:0; padding:0; }
.markdown-body {
  box-sizing:border-box; max-width:900px; margin:0 auto; padding:32px 40px;
  font-family:-apple-system,'Segoe UI',Helvetica,Arial,sans-serif; font-size:16px;
  line-height:1.6; word-wrap:break-word;
}
body.light { background:#ffffff; } body.light .markdown-body { color:#1f2328; }
body.dark  { background:#0d1117; } body.dark  .markdown-body { color:#e6edf3; }
.markdown-body h1,.markdown-body h2 { padding-bottom:.3em; border-bottom:1px solid; }
body.light h1,body.light h2 { border-color:#d0d7de; }
body.dark  h1,body.dark  h2 { border-color:#30363d; }
.markdown-body h1,.markdown-body h2,.markdown-body h3,.markdown-body h4 { font-weight:600; line-height:1.25; margin:24px 0 16px; }
.markdown-body h1{font-size:2em;} .markdown-body h2{font-size:1.5em;} .markdown-body h3{font-size:1.25em;}
.markdown-body a { text-decoration:none; }
body.light a { color:#0969da; } body.dark a { color:#2f81f7; }
.markdown-body a:hover { text-decoration:underline; }
.markdown-body code {
  font-family:'Cascadia Code',Consolas,monospace; font-size:85%;
  padding:.2em .4em; border-radius:6px;
}
body.light code { background:rgba(175,184,193,.2); }
body.dark  code { background:rgba(110,118,129,.4); }
.markdown-body pre {
  padding:16px; overflow:auto; border-radius:8px; line-height:1.45; font-size:85%;
}
body.light pre { background:#f6f8fa; } body.dark pre { background:#161b22; }
.markdown-body pre code { background:transparent; padding:0; font-size:100%; }
.markdown-body blockquote {
  margin:0 0 16px; padding:0 1em; border-left:.25em solid;
}
body.light blockquote { color:#59636e; border-color:#d0d7de; }
body.dark  blockquote { color:#9198a1; border-color:#30363d; }
.markdown-body table { border-collapse:collapse; margin:0 0 16px; display:block; overflow:auto; }
.markdown-body th,.markdown-body td { padding:6px 13px; border:1px solid; }
body.light th,body.light td { border-color:#d0d7de; }
body.dark  th,body.dark  td { border-color:#30363d; }
.markdown-body tr:nth-child(2n) { }
body.light tr:nth-child(2n) { background:#f6f8fa; }
body.dark  tr:nth-child(2n) { background:#161b22; }
.markdown-body img { max-width:100%; }
.markdown-body hr { height:.25em; border:0; margin:24px 0; }
body.light hr { background:#d0d7de; } body.dark hr { background:#30363d; }
.markdown-body ul,.markdown-body ol { padding-left:2em; margin:0 0 16px; }
.markdown-body li.task-list-item { list-style:none; }
.markdown-body li.task-list-item input { margin:0 .2em .25em -1.4em; }
.markdown-body p { margin:0 0 16px; }
";

    private const string SampleDocument = @"# Welcome to MarkdownPad

A fast, native Markdown editor. Type on the **left**, see it rendered on the **right**.

## Features
- Live preview with selectable **flavors** (GitHub by default)
- Independent **dark / light** themes for the app and the preview
- Export to **HTML**, **PDF**, and **Word**
- A formatting toolbar so you don't have to memorize syntax

> Tip: drag the divider between the panes to resize them.

### Code
```python
def hello(name):
    return f""Hello, {name}!""
```

### Table
| Feature | Supported |
|---------|:---------:|
| Tables  | yes |
| Tasks   | yes |

### Tasks
- [x] Build the editor
- [ ] Write something great

Made with care. Happy writing!
";
}
