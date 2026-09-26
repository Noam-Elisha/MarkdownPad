using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace MarkdownPad;

/// <summary>
/// Find and replace bar above the editor (Ctrl+F / Ctrl+H), with the options VS Code has: match case,
/// whole word and regular expressions. The matching itself lives in <see cref="SearchQuery"/>.
/// </summary>
public partial class FindReplaceBar : UserControl
{
    private const int MaxHits = 10000;
    private static readonly Brush ErrorBrush = Frozen(Color.FromRgb(0xF1, 0x4C, 0x4C));

    private TextEditor _editor = null!;
    private MatchHighlighter _highlighter = null!;
    private readonly DispatcherTimer _refresh;
    private SearchQuery? _query;
    private List<SearchHit> _hits = new();
    private int _anchor; // where searching starts: the caret when the bar opened, then the current match

    /// <summary>Raised after the bar selected a match in the editor and scrolled to it.</summary>
    public event Action? Navigated;

    /// <summary>A message for the status bar, e.g. how many occurrences were replaced.</summary>
    public event Action<string>? Status;

    public FindReplaceBar()
    {
        InitializeComponent();
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _refresh.Tick += (_, _) => Search(select: false);
        FindBox.TextChanged += (_, _) => Search(select: true);
        // Checked/Unchecked rather than Click: toggles also change from shortcuts and accessibility tools.
        foreach (var option in new[] { MatchCaseToggle, WholeWordToggle, RegexToggle })
        {
            option.Checked += (_, _) => Search(select: true);
            option.Unchecked += (_, _) => Search(select: true);
        }
        ReplaceToggle.Checked += (_, _) => ShowReplace(true);
        ReplaceToggle.Unchecked += (_, _) => ShowReplace(false);
        PrevButton.Click += (_, _) => FindPrevious();
        NextButton.Click += (_, _) => FindNext();
        CloseButton.Click += (_, _) => Close();
        ReplaceButton.Click += (_, _) => ReplaceOne();
        ReplaceAllButton.Click += (_, _) => ReplaceAll();
        PreviewKeyDown += OnKeyDown;
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public void Attach(TextEditor editor)
    {
        _editor = editor;
        _highlighter = new MatchHighlighter(editor.TextArea.TextView, editor.Document);
        editor.TextArea.TextView.BackgroundRenderers.Add(_highlighter);
        // Edits move the highlights along with the text; the matches themselves refresh shortly after.
        editor.TextChanged += (_, _) =>
        {
            if (!IsOpen) return;
            _refresh.Stop();
            _refresh.Start();
        };
    }

    /// <summary>Opens the bar (Ctrl+F), or with the replace row showing (Ctrl+H).</summary>
    public void Open(bool replace)
    {
        bool fromEditor = !IsKeyboardFocusWithin;
        Visibility = Visibility.Visible;
        if (replace) ShowReplace(true);
        if (fromEditor)
        {
            _anchor = _editor.SelectionStart;
            if (SeedText() is string seed)
                FindBox.Text = RegexToggle.IsChecked == true ? Regex.Escape(seed) : seed;
        }
        UpdateLayout(); // the boxes can only take focus once they're laid out
        var box = replace && FindBox.Text.Length > 0 ? ReplaceBox : FindBox;
        box.Focus();
        box.SelectAll();
        Search(select: true);
    }

    public void Close()
    {
        Visibility = Visibility.Collapsed;
        _refresh.Stop();
        _hits = new();
        _highlighter.Show(_hits);
        _editor.TextArea.Focus();
    }

    public void FindNext()
    {
        if (!IsOpen) { Open(replace: false); return; }
        if (_refresh.IsEnabled) Search(select: false);
        if (_hits.Count == 0) return;
        int current = CurrentIndex();
        int next = current >= 0 ? current + 1 : FirstAtOrAfter(_editor.SelectionStart);
        Go(next < _hits.Count ? next : 0);
    }

    public void FindPrevious()
    {
        if (!IsOpen) { Open(replace: false); return; }
        if (_refresh.IsEnabled) Search(select: false);
        if (_hits.Count == 0) return;
        int current = CurrentIndex();
        int previous = (current >= 0 ? current : FirstAtOrAfter(_editor.SelectionStart)) - 1;
        Go(previous >= 0 ? previous : _hits.Count - 1);
    }

    private void Search(bool select)
    {
        if (_editor is null || !IsOpen) return;
        _refresh.Stop();
        try
        {
            _query = new SearchQuery(FindBox.Text, MatchCaseToggle.IsChecked == true,
                WholeWordToggle.IsChecked == true, RegexToggle.IsChecked == true);
            _hits = _query.FindAll(_editor.Text, MaxHits);
        }
        catch (ArgumentException ex) { Fail("Invalid regex", ex.Message); return; }
        catch (RegexMatchTimeoutException) { Fail("Search timed out", "The regular expression took too long on this document."); return; }

        FindFrame.Tag = null;
        FindFrame.ToolTip = null;
        CountText.SetResourceReference(ForegroundProperty, "MutedFg");
        _highlighter.Show(_hits);
        if (select && _hits.Count > 0)
        {
            int first = FirstAtOrAfter(_anchor);
            Go(first < _hits.Count ? first : 0);
        }
        else UpdateCount();
    }

    private void Fail(string summary, string detail)
    {
        _query = null;
        _hits = new();
        _highlighter.Show(_hits);
        FindFrame.Tag = "error";
        FindFrame.ToolTip = detail;
        CountText.Text = summary;
        CountText.Foreground = ErrorBrush;
    }

    // Select a match and bring it into view.
    private void Go(int index)
    {
        var hit = _hits[index];
        _editor.Select(hit.Offset, hit.Length);
        var at = _editor.Document.GetLocation(hit.Offset);
        _editor.ScrollTo(at.Line, at.Column);
        _anchor = hit.Offset;
        UpdateCount();
        Navigated?.Invoke();
    }

    private void ReplaceOne()
    {
        if (_refresh.IsEnabled) Search(select: false);
        if (_query is null || _hits.Count == 0) return;
        int current = CurrentIndex();
        if (current < 0) { FindNext(); return; } // as in VS Code, the first press moves onto a match
        var hit = _hits[current];
        var replacement = _query.ReplacementFor(hit, ReplaceBox.Text, NewlineAt(hit.Offset));
        _editor.Document.Replace(hit.Offset, hit.Length, replacement);
        _anchor = hit.Offset + replacement.Length; // carry on after what was just inserted
        Search(select: true);
    }

    private void ReplaceAll()
    {
        if (_refresh.IsEnabled) Search(select: false);
        if (_query is null || _query.IsEmpty) return;
        List<SearchHit> hits;
        try { hits = _query.FindAll(_editor.Text, int.MaxValue); }
        catch (RegexMatchTimeoutException) { Fail("Search timed out", "The regular expression took too long on this document."); return; }
        if (hits.Count == 0) return;

        var document = _editor.Document;
        document.BeginUpdate(); // one undo step for all of it
        try
        {
            for (int i = hits.Count - 1; i >= 0; i--) // last first, so the earlier offsets stay valid
                document.Replace(hits[i].Offset, hits[i].Length,
                    _query.ReplacementFor(hits[i], ReplaceBox.Text, NewlineAt(hits[i].Offset)));
        }
        finally { document.EndUpdate(); }
        Status?.Invoke($"Replaced {hits.Count} occurrence{(hits.Count == 1 ? "" : "s")}");
        Search(select: false);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (key == Key.Enter)
        {
            if (ReplaceBox.IsKeyboardFocused)
            {
                if (modifiers == (ModifierKeys.Control | ModifierKeys.Alt)) ReplaceAll(); else ReplaceOne();
            }
            else if (modifiers.HasFlag(ModifierKeys.Shift)) FindPrevious();
            else FindNext();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Alt && (key is Key.C or Key.W or Key.R))
        {
            var option = key == Key.C ? MatchCaseToggle : key == Key.W ? WholeWordToggle : RegexToggle;
            option.IsChecked = option.IsChecked != true; // searches again via Checked/Unchecked
            e.Handled = true;
        }
    }

    private void ShowReplace(bool show)
    {
        ReplaceToggle.IsChecked = show;
        ReplaceToggle.Content = show ? "" : ""; // chevron down / right
        ReplaceFrame.Visibility = ReplaceButtons.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCount()
    {
        int count = _hits.Count, current = CurrentIndex();
        string total = count >= MaxHits ? $"{MaxHits}+" : count.ToString();
        CountText.Text = FindBox.Text.Length == 0 ? ""
            : count == 0 ? "No results"
            : current >= 0 ? $"{current + 1} of {total}"
            : $"{total} result{(count == 1 ? "" : "s")}";
        PrevButton.IsEnabled = NextButton.IsEnabled = count > 0;
    }

    // What Ctrl+F starts with: the selection if it's on one line, otherwise the word at the caret.
    private string? SeedText()
    {
        var selected = _editor.SelectedText;
        if (selected.Length > 0) return selected.Contains('\n') || selected.Contains('\r') ? null : selected;
        var document = _editor.Document;
        int start = _editor.CaretOffset, end = start;
        while (start > 0 && IsWordChar(document.GetCharAt(start - 1))) start--;
        while (end < document.TextLength && IsWordChar(document.GetCharAt(end))) end++;
        return end > start ? document.GetText(start, end - start) : null;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // The match the editor's selection is on, or -1.
    private int CurrentIndex()
    {
        int start = _editor.SelectionStart, i = FirstAtOrAfter(start);
        return i < _hits.Count && _hits[i].Offset == start && _hits[i].Length == _editor.SelectionLength ? i : -1;
    }

    // Index of the first match starting at or after an offset (the count when there is none).
    private int FirstAtOrAfter(int offset)
    {
        int lo = 0, hi = _hits.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_hits[mid].Offset < offset) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private string NewlineAt(int offset) =>
        TextUtilities.GetNewLineFromDocument(_editor.Document, _editor.Document.GetLineByOffset(offset).LineNumber);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Paints every match in the visible part of the editor.</summary>
    private sealed class MatchHighlighter : IBackgroundRenderer
    {
        private static readonly Brush Fill = Frozen(Color.FromArgb(0x66, 0xEA, 0x5C, 0x00));
        private readonly TextView _view;
        private readonly TextSegmentCollection<TextSegment> _matches;

        public MatchHighlighter(TextView view, TextDocument document)
        {
            _view = view;
            _matches = new TextSegmentCollection<TextSegment>(document); // offsets follow edits
        }

        public KnownLayer Layer => KnownLayer.Selection;

        public void Show(IEnumerable<SearchHit> hits)
        {
            _matches.Clear();
            foreach (var hit in hits) _matches.Add(new TextSegment { StartOffset = hit.Offset, Length = hit.Length });
            _view.InvalidateLayer(Layer);
        }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (_matches.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
            int start = textView.VisualLines[0].FirstDocumentLine.Offset;
            int end = textView.VisualLines[^1].LastDocumentLine.EndOffset;
            var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
            foreach (var match in _matches.FindOverlappingSegments(start, end - start))
            {
                if (match.Length > 0) { builder.AddSegment(textView, match); continue; }
                // An empty match (like ^) shows as a thin bar where it sits.
                var at = new TextViewPosition(textView.Document.GetLocation(match.StartOffset));
                var top = textView.GetVisualPosition(at, VisualYPosition.LineTop) - textView.ScrollOffset;
                var bottom = textView.GetVisualPosition(at, VisualYPosition.LineBottom) - textView.ScrollOffset;
                drawingContext.DrawRectangle(Fill, null, new Rect(top.X - 1, top.Y, 2, bottom.Y - top.Y));
            }
            if (builder.CreateGeometry() is Geometry geometry) drawingContext.DrawGeometry(Fill, null, geometry);
        }
    }
}
