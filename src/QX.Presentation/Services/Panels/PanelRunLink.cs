using Qx.Presentation.Threading;
using Qx.Scripting.Hosting;
using Qx.Scripting;

namespace Qx.Presentation.Services.Panels;

public sealed class PanelRunLink : IDisposable
{
    readonly PanelDocument _panel;
    readonly ScriptUiHost _host;
    readonly Queue<PendingUpdate> _pending = new();
    readonly Lock _pending_gate = new();
    PendingUpdate? _tail;
    readonly CoalescingSignal _drain;
    readonly CancellationToken _run_token;
    int _disposed;

    internal PanelRunLink(PanelDocument panel, ScriptUi ui, IUiDispatcher dispatcher, string? pressed_button, CancellationToken run_token)
    {
        _panel = panel;
        _host = ScriptUiHost.Of(ui);
        _run_token = run_token;
        _drain = new CoalescingSignal(dispatcher, Drain, UiPriority.Background);
        foreach ((string name, string value) in panel.Values())
            _host.Push(name, value);
        _host.SetClicked(pressed_button);
        _host.Logged += OnLogged;
        _host.Cleared += OnCleared;
        _host.Changed += OnChanged;
        _host.ProgressChanged += OnProgress;
        _host.ProgressDetailChanged += OnProgressDetail;
        _host.OptionsChanged += OnOptions;
        _host.ValidationChanged += OnValidation;
        _host.StatusChanged += OnStatus;
        _host.EnabledChanged += OnEnabled;
        _host.VisibilityChanged += OnVisibility;
        _host.RowAdded += OnRowAdded;
        _host.RowsSet += OnRowsSet;
        _host.Toasted += OnToasted;
        _host.BusyChanged += OnBusy;
        _host.Downloaded += OnDownloaded;
        _host.Confirm = OnConfirmAsync;
        _host.Prompt = OnPromptAsync;
        _panel.FieldEdited += Push;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _panel.FieldEdited -= Push;
        _host.Logged -= OnLogged;
        _host.Cleared -= OnCleared;
        _host.Changed -= OnChanged;
        _host.ProgressChanged -= OnProgress;
        _host.ProgressDetailChanged -= OnProgressDetail;
        _host.OptionsChanged -= OnOptions;
        _host.ValidationChanged -= OnValidation;
        _host.StatusChanged -= OnStatus;
        _host.EnabledChanged -= OnEnabled;
        _host.VisibilityChanged -= OnVisibility;
        _host.RowAdded -= OnRowAdded;
        _host.RowsSet -= OnRowsSet;
        _host.Toasted -= OnToasted;
        _host.BusyChanged -= OnBusy;
        _host.Downloaded -= OnDownloaded;
        Apply();
    }

    void Push(string name, string value)
    {
        if (Volatile.Read(ref _disposed) == 0)
            _host.Push(name, value);
    }

    void Enqueue(Action action, string? property = null, string? name = null)
    {
        lock (_pending_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            if (property is not null && _tail is { } tail && tail.Property == property && tail.Name == name)
                tail.Action = action;
            else
            {
                _tail = new PendingUpdate(action, property, name);
                _pending.Enqueue(_tail);
            }
        }
        _drain.Raise();
    }

    void Drain()
    {
        if (Volatile.Read(ref _disposed) == 0)
            Apply(256);
    }

    void Apply(int limit = int.MaxValue)
    {
        for (int count = 0; count < limit; count++)
        {
            Action action;
            lock (_pending_gate)
            {
                if (!_pending.TryDequeue(out PendingUpdate? update))
                    return;
                action = update.Action;
                if (_pending.Count == 0)
                    _tail = null;
            }
            action();
        }
        lock (_pending_gate)
            if (_pending.Count > 0)
                _drain.Raise();
    }

    void OnLogged(string box, string text) => Enqueue(() => _panel.AppendOutput(box, text));

    void OnCleared(string name) => Enqueue(() => _panel.ClearByScript(name));

    void OnChanged(string name, string value) => Enqueue(() => _panel.SetFieldValue(name, value));

    void OnProgress(string name, double fraction) => Enqueue(() => _panel.SetProgress(name, fraction), nameof(OnProgress), name);

    void OnProgressDetail(string name, bool indeterminate, string caption) => Enqueue(() => _panel.SetProgressDetail(name, indeterminate, caption), nameof(OnProgressDetail), name);

    void OnOptions(string name, IReadOnlyList<string> options, string selected) => Enqueue(() => _panel.SetOptions(name, options, selected));

    void OnValidation(string name, string message) => Enqueue(() => _panel.SetValidation(name, message), nameof(OnValidation), name);

    void OnStatus(string name, string text) => Enqueue(() => _panel.SetStatus(name, text), nameof(OnStatus), name);

    void OnEnabled(string name, bool enabled) => Enqueue(() => _panel.SetEnabled(name, enabled), nameof(OnEnabled), name);

    void OnVisibility(string name, bool visible) => Enqueue(() => _panel.SetVisible(name, visible), nameof(OnVisibility), name);

    void OnRowAdded(string table, IReadOnlyList<string> cells) => Enqueue(() => _panel.AddRow(table, cells));

    void OnRowsSet(string table, IReadOnlyList<IReadOnlyList<string>> rows) => Enqueue(() => _panel.SetRows(table, rows));

    void OnToasted(string text, bool problem) => Enqueue(() => _panel.Toast(text, problem));

    void OnBusy(string button, bool busy) => Enqueue(() => _panel.SetButtonBusy(button, busy), nameof(OnBusy), button);

    void OnDownloaded(string file_name, string content) =>
        Enqueue(() => _panel.DownloadAsync(file_name, content, _run_token).Observe("scripts"));

    Task<bool> OnConfirmAsync(string title, string message) =>
        Volatile.Read(ref _disposed) != 0 ? Task.FromResult(false) : _panel.ConfirmAsync(title, message, _run_token);

    Task<string?> OnPromptAsync(string title, string initial) =>
        Volatile.Read(ref _disposed) != 0 ? Task.FromResult<string?>(null) : _panel.PromptAsync(title, initial, _run_token);

    sealed class PendingUpdate(Action action, string? property, string? name)
    {
        public Action Action = action;
        public readonly string? Property = property;
        public readonly string? Name = name;
    }
}
