using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Core.Models;
using Skua.Core.Services;

namespace Skua.Host;

/// <summary>Settings: same storage as the desktop client (UnifiedSettingsService).</summary>
public sealed class HeadlessSettingsService : ISettingsService
{
    private readonly UnifiedSettingsService _settings = new();

    public HeadlessSettingsService()
    {
        _settings.Initialize(AppRole.Client);
    }

    public T? Get<T>(string key) => _settings.Get<T>(key);
    public T Get<T>(string key, T defaultValue) => _settings.Get(key, defaultValue);
    public void Set<T>(string key, T value) => _settings.Set(key, value);
    public void Initialize(AppRole role) => _settings.Initialize(role);
    public SharedSettings GetShared() => _settings.GetShared();
    public ClientSettings GetClient() => _settings.GetClient();
    public ManagerSettings GetManager() => _settings.GetManager();
    public void SetApplicationVersion() => _settings.SetApplicationVersion();
    public void ReloadSettings() => _settings.ReloadSettings();
}

/// <summary>No UI thread: run inline. A UI host can replace this with its own dispatcher.</summary>
public sealed class InlineDispatcherService : IDispatcherService
{
    public void Invoke(Action action) => action();
}

/// <summary>
/// Unattended dialogs: nothing is shown. Messages go to the log; questions get the safe
/// answer (no / cancel), so a script never blocks waiting for a click.
/// </summary>
public sealed class HeadlessDialogService : IDialogService
{
    public static event Action<string>? Message;

    private static void Say(string text) => Message?.Invoke(text);

    public bool? ShowDialog<TViewModel>(TViewModel viewModel) where TViewModel : class
    {
        Say($"[dialog skipped] {typeof(TViewModel).Name}");
        return null;
    }

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, string Title) where TViewModel : class
    {
        Say($"[dialog skipped] {Title}");
        return null;
    }

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, Action<TViewModel> callback) where TViewModel : class
    {
        Say($"[dialog skipped] {typeof(TViewModel).Name}");
        return null;
    }

    public void ShowDialog(IOptionContainer optionContainer, Action<IOptionContainerViewModel> callback)
    {
        // Script options keep their saved/default values (same as SilentConfig on desktop).
        Say("[script options] using saved/default values");
    }

    public void ShowMessageBox(string message, string caption) => Say($"[{caption}] {message}");

    public bool? ShowMessageBox(string message, string caption, bool yesAndNo)
    {
        Say($"[{caption}] {message} -> No");
        return false;
    }

    public DialogResult ShowMessageBox(string message, string caption, params string[] buttons)
    {
        Say($"[{caption}] {message} -> Cancel");
        return DialogResult.Cancelled;
    }

    public IInputDialogViewModel CreateInputDialog(string title, string dialogHint) => new HeadlessInputDialog(title, dialogHint);

    private sealed class HeadlessInputDialog(string title, string hint) : IInputDialogViewModel
    {
        public bool NumberOnly => false;
        public string DialogTextInput => string.Empty;
        public string DialogHint => hint;
        public string TextBoxHint => title;
    }
}

public sealed class HeadlessWindowService : IWindowService
{
    public void ShowWindow<TViewModel>(int width, int height) where TViewModel : class { }
    public void ShowWindow<TViewModel>() where TViewModel : class { }
    public void ShowWindow<TViewModel>(TViewModel viewModel) where TViewModel : class { }
    public void ShowManagedWindow(string key) { }
    public void RegisterManagedWindow<TViewModel>(string key, TViewModel viewModel) where TViewModel : class, IManagedWindow { }
    public void RegisterManagedWindow<TViewModel>(string key) where TViewModel : class, IManagedWindow { }
    public void RegisterManagedWindow(string key, Func<IManagedWindow> factory) { }
}

public sealed class HeadlessScreenshotService : IScreenshotService
{
    public Task<byte[]> TakeScreenshotAsync() => Task.FromResult(Array.Empty<byte>());
}

public sealed class HeadlessHotKeyService : IHotKeyService
{
    public void Reload() { }
    public List<T> GetHotKeys<T>() where T : IHotKey, new() => new();
    public HotKey? ParseToHotKey(string keyGesture) => null;
}
