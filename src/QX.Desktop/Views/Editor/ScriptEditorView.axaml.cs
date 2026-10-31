using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit;
using Microsoft.CodeAnalysis;
using Qx.Desktop.Editor;
using Qx.Desktop.Input;
using Qx.Diagnostics;
using Qx.Presentation.Mvvm;
using Qx.Presentation.Services.Editor;
using Qx.Presentation.Services.Workspace;
using Qx.Presentation.Threading;
using RoslynPad.Editor;
using RoslynPad.Roslyn;

namespace Qx.Desktop.Views.Editor;

public sealed partial class ScriptEditorView : UserControl
{
    readonly UiDirectiveColorizer _directives;
    RoslynHostProvider? _hosts;
    EditorPreferences? _preferences;
    ScriptDocument? _document;
    EditorBufferAdapter? _buffer;
    TextEditor? _surface;
    bool _started;
    bool _released;
    bool _initializing;
    QxRoslynHost? _roslyn;
    DocumentId? _roslyn_document;
    AvalonEditTextContainer? _roslyn_text;
    RoslynCodeEditor? _code;

    public ScriptEditorView()
    {
        InitializeComponent();
        _directives = new UiDirectiveColorizer(DirectiveBrush);
        ActualThemeVariantChanged += OnThemeChanged;
    }

    public TextEditor? Surface => _surface;

    RoslynCodeEditor Code => _code ??= CreateCode();

    RoslynCodeEditor CreateCode()
    {
        var editor = new RoslynCodeEditor
        {
            Name = "Code",
            Classes = { "script" },
            IsVisible = false,
            IsReadOnly = true,
            ShowLineNumbers = true,
            IsCodeFoldingEnabled = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = _preferences?.FontSize ?? EditorPreferences.DefaultFontSize,
            WordWrap = _preferences?.WordWrap ?? false
        };
        Editors.Children.Add(editor);
        return editor;
    }

    public void Use(ScriptDocument document, RoslynHostProvider hosts, EditorPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(preferences);
        _document = document;
        _hosts = hosts;
        _preferences = preferences;
        DataContext = document;
        Apply(preferences);
        preferences.PropertyChanged += OnPreferencesChanged;
        Plain.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
        Plain.Document = new AvaloniaEdit.Document.TextDocument(document.Text);
        Adopt(Plain);
    }

    public void Release()
    {
        if (_released)
            return;
        _released = true;
        ActualThemeVariantChanged -= OnThemeChanged;
        if (_preferences is { } preferences)
            preferences.PropertyChanged -= OnPreferencesChanged;
        _preferences = null;
        if (_surface is { } editor)
        {
            editor.RemoveHandler(PointerWheelChangedEvent, OnWheel);
            editor.TextArea.TextView.LineTransformers.Remove(_directives);
        }
        _buffer?.Dispose();
        _buffer = null;
        _surface = null;
        _document = null;
        if (!_initializing)
            CloseDocument();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (_released || _started || _document is null || _hosts is null)
            return;
        _started = true;
        StartAsync(_document, _hosts).Observe("editor");
    }

    async Task StartAsync(ScriptDocument document, RoslynHostProvider hosts)
    {
        QxRoslynHost? host = null;
        try
        {
            host = await hosts.GetAsync(CancellationToken.None);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Diag.Warn($"Editor code-intelligence unavailable ({error.Message}); scripts still run.", "editor");
        }
        if (_released || document.IsClosed)
            return;
        if (host is not null)
        {
            _initializing = true;
            _roslyn = host;
            void CreateDocument(object? sender, CreatingDocumentEventArgs args)
            {
                _roslyn_text = new AvalonEditTextContainer(Plain.Document) { Editor = Plain };
                _roslyn_document = host.AddDocument(new DocumentCreationArgs(
                    _roslyn_text, hosts.WorkingDirectory, SourceCodeKind.Script,
                    _roslyn_text.UpdateText));
                args.DocumentId = _roslyn_document;
            }
            RoslynCodeEditor? code = null;
            try
            {
                code = Code;
                code.CreatingDocument += CreateDocument;
                Directory.CreateDirectory(hosts.WorkingDirectory);
                Code.TextArea.SelectionCornerRadius = 2;
                _ = await Code.InitializeAsync(host, ThemeColors(), hosts.WorkingDirectory, "", SourceCodeKind.Script);
                if (_released || document.IsClosed)
                    return;
                if (Code.CompletionProvider is { } completions)
                    Code.CompletionProvider = new GuardedCompletionProvider(new UiDirectiveCompletionProvider(completions, Code), Code);
                bool focused = Plain.IsKeyboardFocusWithin;
                int caret = Plain.CaretOffset;
                int selection = Plain.SelectionStart;
                int length = Plain.SelectionLength;
                double horizontal = Plain.HorizontalOffset;
                double vertical = Plain.VerticalOffset;
                Code.Document = Plain.Document;
                _roslyn_text!.Editor = Code;
                Adopt(Code);
                Code.Select(selection, length);
                Code.CaretOffset = caret;
                Code.ScrollToHorizontalOffset(horizontal);
                Code.ScrollToVerticalOffset(vertical);
                if (focused)
                    Code.TextArea.Focus();
                Plain.IsVisible = false;
                return;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                CloseDocument();
                Diag.Warn($"Editor code-intelligence unavailable ({error.Message}); scripts still run.", "editor");
            }
            finally
            {
                if (code is not null)
                    code.CreatingDocument -= CreateDocument;
                _initializing = false;
                if (_released || document.IsClosed)
                    CloseDocument();
            }
        }
        if (_released || document.IsClosed)
            return;
        Fallback.Notice = new Notice(NoticeSeverity.Warning, "Code completion is unavailable. Scripts still run.", DateTimeOffset.UtcNow);
        Fallback.IsVisible = true;
        if (_code is { } advanced)
            advanced.IsVisible = false;
    }

    void CloseDocument()
    {
        if (_roslyn_document is { } id)
            _roslyn?.CloseDocument(id);
        _roslyn_text?.Dispose();
        _roslyn_text = null;
        _roslyn_document = null;
        _roslyn = null;
    }

    void Adopt(TextEditor editor)
    {
        if (_document is not { } document)
            return;
        if (_surface is { } previous)
        {
            previous.RemoveHandler(PointerWheelChangedEvent, OnWheel);
            previous.TextArea.TextView.LineTransformers.Remove(_directives);
        }
        _buffer?.Dispose();
        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.IndentationSize = 4;
        editor.Options.AllowScrollBelowDocument = true;
        editor.Options.HighlightCurrentLine = false;
        editor.TextArea.SelectionCornerRadius = 2;
        editor.TextArea.TextView.LineTransformers.Add(_directives);
        editor.TextArea.TextView.Margin = new Thickness(8, 0, 0, 0);
        foreach (Control margin in editor.TextArea.LeftMargins)
        {
            if (margin is LineNumberMargin)
                margin.Margin = new Thickness(8, 0, 0, 0);
        }
        editor.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        editor.ContextMenu = EditMenu(editor);
        if (_preferences is { } preferences)
            Apply(preferences);
        _buffer = new EditorBufferAdapter(editor, document);
        document.AttachBuffer(_buffer);
        _surface = editor;
        editor.IsReadOnly = false;
        editor.IsVisible = true;
    }

    void OnWheel(object? sender, PointerWheelEventArgs args)
    {
        if (_preferences is not { } preferences || !args.KeyModifiers.HasFlag(PrimaryModifier.Current))
            return;
        if (args.Delta.Y > 0)
            preferences.ZoomIn();
        else if (args.Delta.Y < 0)
            preferences.ZoomOut();
        args.Handled = true;
    }

    void OnPreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_preferences is { } preferences)
            Apply(preferences);
    }

    static ContextMenu EditMenu(TextEditor editor)
    {
        var hotkeys = Application.Current?.PlatformSettings?.HotkeyConfiguration;
        MenuItem cut = Entry("Cut", Primary(hotkeys?.Cut), editor.Cut);
        MenuItem copy = Entry("Copy", Primary(hotkeys?.Copy), editor.Copy);
        MenuItem paste = Entry("Paste", Primary(hotkeys?.Paste), editor.Paste);
        var menu = new ContextMenu();
        menu.Items.Add(cut);
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("Select all", Primary(hotkeys?.SelectAll), editor.SelectAll));
        menu.Opening += (_, _) =>
        {
            bool selected = !editor.TextArea.Selection.IsEmpty;
            cut.IsEnabled = selected && !editor.IsReadOnly;
            copy.IsEnabled = selected;
            paste.IsEnabled = !editor.IsReadOnly;
        };
        return menu;
    }

    static MenuItem Entry(string header, KeyGesture? gesture, Action action)
    {
        var item = new MenuItem { Header = header, InputGesture = gesture };
        item.Click += (_, _) => action();
        return item;
    }

    static KeyGesture? Primary(List<KeyGesture>? gestures) => gestures is { Count: > 0 } ? gestures[0] : null;

    void Apply(EditorPreferences preferences)
    {
        if (_code is { } code)
        {
            code.FontSize = preferences.FontSize;
            code.WordWrap = preferences.WordWrap;
        }
        Plain.FontSize = preferences.FontSize;
        Plain.WordWrap = preferences.WordWrap;
    }

    void OnThemeChanged(object? sender, EventArgs args)
    {
        if (_code is { } code && ReferenceEquals(_surface, code))
        {
            code.ClassificationHighlightColors = ThemeColors();
            code.TextArea.TextView.Redraw();
            return;
        }
        _surface?.TextArea.TextView.Redraw();
    }

    IClassificationHighlightColors ThemeColors() => new QxEditorColors(TokenColor);

    Color? TokenColor(string key) => TokenBrush(key) is ISolidColorBrush brush ? brush.Color : null;

    IBrush? DirectiveBrush(DirectiveRole role) => TokenBrush(role switch
    {
        DirectiveRole.Comment => "QxSyntaxCommentBrush",
        DirectiveRole.Keyword => "QxSyntaxKeywordBrush",
        DirectiveRole.String => "QxSyntaxStringBrush",
        DirectiveRole.Number => "QxSyntaxNumberBrush",
        DirectiveRole.Type => "QxSyntaxTypeBrush",
        DirectiveRole.Parameter => "QxSyntaxParameterBrush",
        _ => "QxSyntaxPunctuationBrush"
    });

    IBrush? TokenBrush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out object? found) && found is IBrush brush ? brush : null;
}
