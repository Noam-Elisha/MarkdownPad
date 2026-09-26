using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MarkdownPad;

/// <summary>
/// Editor side of the scroll sync between the editor and the live preview. The preview page
/// (Preview.js) does the mapping; this class renders the Markdown with source-line markers, tells
/// the page where those lines sit in the editor, and scrolls the editor when the preview leads.
/// Whichever pane the user last scrolled or typed in leads, so the two never fight.
/// </summary>
internal sealed class PreviewSync
{
    /// <summary>The preview page's script, embedded in the page shell.</summary>
    public static readonly string Script = LoadScript();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TextEditor _editor;
    private readonly WebView2 _preview;
    private int[] _anchorLines = Array.Empty<int>();
    private int _version;
    private bool _active;        // the page has content and is listening
    private bool _editorLeads = true;
    private bool _textChanged;   // edited since the last render, so _anchorLines are stale
    private bool _queued;
    private double _sentExtent = -1, _sentViewport = -1;

    public PreviewSync(TextEditor editor, WebView2 preview)
    {
        _editor = editor;
        _preview = preview;
        var view = editor.TextArea.TextView;
        view.ScrollOffsetChanged += (_, _) => QueueEditorState();
        view.VisualLinesChanged += (_, _) => QueueEditorState(); // wrapped lines change height once measured
        editor.SizeChanged += (_, _) => QueueEditorState();
        editor.PreviewMouseWheel += (_, _) => EditorTakesLead();
        editor.PreviewMouseDown += (_, _) => EditorTakesLead();
        editor.PreviewKeyDown += (_, _) => EditorTakesLead();
        editor.TextChanged += (_, _) => { _textChanged = true; EditorTakesLead(); };
    }

    public void Attach(CoreWebView2 core) => core.WebMessageReceived += OnWebMessage;

    /// <summary>Call before the page is (re)loaded; a fresh page starts out following the editor.</summary>
    public void Reset()
    {
        _active = false;
        _editorLeads = true;
    }

    /// <summary>Replaces the preview content; <paramref name="revealCaret"/> brings the edited line into view.</summary>
    public void PostContent(string markdown, MarkdownPipeline pipeline, bool revealCaret)
    {
        Post(Content(markdown, pipeline, revealCaret));
        _active = true;
    }

    /// <summary>Script that replaces the preview content, for callers that must await it (PDF export).</summary>
    public string ContentScript(string markdown, MarkdownPipeline pipeline)
    {
        var json = JsonSerializer.Serialize(Content(markdown, pipeline, revealCaret: false), JsonOptions);
        _active = true;
        return $"mdpad.receive({json});";
    }

    private ContentMessage Content(string markdown, MarkdownPipeline pipeline, bool revealCaret)
    {
        var html = RenderWithLineMarkers(markdown, pipeline, out _anchorLines);
        _textChanged = false;
        return new ContentMessage("content", ++_version, html,
            revealCaret ? CaretBand() : null, EditorState(withAnchors: true));
    }

    // The caret's visual line in editor pixels, for the preview to keep on screen after an edit - or
    // nothing when the caret is scrolled out of view (an edit can also land off-screen: toolbar, undo).
    // When only blank lines follow the caret, the band runs to the end so typing there shows the very bottom.
    private Band? CaretBand()
    {
        var area = _editor.TextArea;
        double top = area.TextView.GetVisualPosition(area.Caret.Position, VisualYPosition.LineTop).Y;
        double bottom = area.TextView.GetVisualPosition(area.Caret.Position, VisualYPosition.LineBottom).Y;
        if (bottom <= _editor.VerticalOffset || top >= _editor.VerticalOffset + _editor.ViewportHeight) return null;
        return new Band(top, OnlyBlankAfterCaret() ? _editor.ExtentHeight : bottom);
    }

    private bool OnlyBlankAfterCaret()
    {
        var doc = _editor.Document;
        var line = doc.GetLineByNumber(_editor.TextArea.Caret.Line);
        int last = doc.TextLength - 1;
        while (last >= line.EndOffset && char.IsWhiteSpace(doc.GetCharAt(last))) last--;
        return last < line.EndOffset;
    }

    private EditorMessage EditorState(bool withAnchors)
    {
        double extent = _editor.ExtentHeight, viewport = _editor.ViewportHeight;
        double[]? tops = null;
        if (withAnchors)
        {
            var view = _editor.TextArea.TextView;
            int lineCount = _editor.Document.LineCount;
            tops = new double[_anchorLines.Length];
            for (int i = 0; i < tops.Length; i++)   // a block ending on the last line "ends" at the very bottom
                tops[i] = _anchorLines[i] < lineCount ? view.GetVisualTopByDocumentLine(_anchorLines[i] + 1) : extent;
            _sentExtent = extent;
            _sentViewport = viewport;
        }
        return new EditorMessage("editor", _editor.VerticalOffset, Math.Max(0, extent - viewport), viewport,
            extent, withAnchors ? _anchorLines : null, tops);
    }

    // Scroll and layout events arrive in bursts; send one update once layout has settled. Nothing is sent
    // between an edit and its render: the page's anchors still describe the old text, and mapping the
    // edited editor through them would send the preview into the wrong content. The render re-syncs.
    private void QueueEditorState()
    {
        if (_queued || !_active || _textChanged) return;
        _queued = true;
        _editor.Dispatcher.InvokeAsync(SendEditorState, DispatcherPriority.Loaded);
    }

    private void SendEditorState()
    {
        _queued = false;
        if (!_active || _textChanged) return;
        bool resized = _editor.ExtentHeight != _sentExtent || _editor.ViewportHeight != _sentViewport;
        Post(EditorState(withAnchors: resized));
    }

    private void EditorTakesLead()
    {
        if (_editorLeads) return;
        _editorLeads = true;
        if (_active) Post(new LeadMessage("lead"));
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var msg = json.RootElement;
            switch (msg.GetProperty("t").GetString())
            {
                case "lead": // the user started scrolling the preview
                    _editorLeads = false;
                    break;
                case "scroll" when !_editorLeads:
                    var top = msg.GetProperty("top").GetDouble();
                    if (double.IsFinite(top)) _editor.ScrollToVerticalOffset(top);
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
    }

    private void Post(object message)
    {
        try { _preview.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions)); }
        catch { /* the page is closing or being replaced */ }
    }

    /// <summary>
    /// Renders like Markdown.ToHtml, but marks each block with the source line it starts on (data-line)
    /// and the line just after it (data-end), so the preview can line up both edges of every block.
    /// </summary>
    private static string RenderWithLineMarkers(string markdown, MarkdownPipeline pipeline, out int[] lines)
    {
        var document = Markdown.Parse(markdown, pipeline);
        var lineStarts = LineStarts(markdown);
        var found = new SortedSet<int>();
        foreach (var block in document.Descendants<Block>())
        {
            var attributes = block.GetAttributes();
            attributes.AddPropertyIfNotExist("data-line", block.Line.ToString(CultureInfo.InvariantCulture));
            found.Add(block.Line);
            int end = LineOf(lineStarts, block.Span.End) + 1;
            if (end > block.Line)
            {
                attributes.AddPropertyIfNotExist("data-end", end.ToString(CultureInfo.InvariantCulture));
                found.Add(end);
            }
        }
        lines = found.ToArray();

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    // Where each line starts; "\r\n", "\n" and "\r" all end a line, as in the editor and in Markdig.
    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            if (text[i] == '\n' || text[i] == '\r') starts.Add(i + 1);
        }
        return starts;
    }

    private static int LineOf(List<int> lineStarts, int offset)
    {
        int i = lineStarts.BinarySearch(offset);
        return i >= 0 ? i : ~i - 1;
    }

    private static string LoadScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MarkdownPad.Preview.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record EditorMessage(string T, double Top, double Max, double Vh, double Total,
        int[]? AnchorLines, double[]? AnchorTops);

    private sealed record Band(double Top, double Bottom);

    private sealed record ContentMessage(string T, int V, string Html, Band? Reveal, EditorMessage Editor);

    private sealed record LeadMessage(string T);
}
