using System;
using System.Collections.Generic;
using System.Drawing;

namespace System.Windows.Forms;

public delegate void MethodInvoker();

public delegate void PaintEventHandler(object? sender, PaintEventArgs e);

public class PaintEventArgs : EventArgs
{
}

public enum DockStyle { None, Top, Bottom, Left, Right, Fill }

public enum FormStartPosition { Manual, CenterScreen, WindowsDefaultLocation, WindowsDefaultBounds, CenterParent }

public enum FormBorderStyle { None, FixedSingle, Fixed3D, FixedDialog, Sizable, FixedToolWindow, SizableToolWindow }

public enum ProgressBarStyle { Blocks, Continuous, Marquee }

public enum DialogResult { None, OK, Cancel, Abort, Retry, Ignore, Yes, No, TryAgain, Continue }

public class Control : IDisposable
{
    private readonly List<Control> _controls = new();

    public string Text { get; set; } = string.Empty;
    public Size Size { get; set; }
    public int Width { get => Size.Width; set => Size = new Size(value, Size.Height); }
    public int Height { get => Size.Height; set => Size = new Size(Size.Width, value); }
    public DockStyle Dock { get; set; }
    public Color ForeColor { get; set; }
    public Color BackColor { get; set; }
    public bool Visible { get; set; }
    public bool IsDisposed { get; private set; }
    public ControlCollection Controls => new(_controls);

    public event PaintEventHandler? Paint;
    public event EventHandler? Click;

    /// <summary>No UI thread here: run the delegate inline.</summary>
    public object? Invoke(Delegate method) => method.DynamicInvoke();

    public object? Invoke(Delegate method, params object?[]? args) => method.DynamicInvoke(args);

    public IAsyncResult BeginInvoke(Delegate method) => System.Threading.Tasks.Task.Run(() => method.DynamicInvoke());

    public void Show() => Visible = true;

    public void Hide() => Visible = false;

    public void Refresh() { }

    public void Invalidate() { }

    public virtual void Dispose()
    {
        IsDisposed = true;
        GC.SuppressFinalize(this);
    }

    public sealed class ControlCollection
    {
        private readonly List<Control> _list;

        internal ControlCollection(List<Control> list) => _list = list;

        public int Count => _list.Count;

        public void Add(Control control) => _list.Add(control);

        public void Remove(Control control) => _list.Remove(control);

        public void Clear() => _list.Clear();
    }
}

public class Form : Control
{
    public FormStartPosition StartPosition { get; set; }
    public FormBorderStyle FormBorderStyle { get; set; }
    public bool MaximizeBox { get; set; }
    public bool MinimizeBox { get; set; }
    public bool TopMost { get; set; }
    public bool ShowInTaskbar { get; set; }

    public event EventHandler? Shown;
    public event EventHandler? FormClosed;

    internal void RaiseShown() => Shown?.Invoke(this, EventArgs.Empty);

    public void Close() => FormClosed?.Invoke(this, EventArgs.Empty);

    public DialogResult ShowDialog()
    {
        RaiseShown();
        return DialogResult.Cancel;
    }
}

public class ProgressBar : Control
{
    public int Minimum { get; set; }
    public int Maximum { get; set; } = 100;
    public int Value { get; set; }
    public ProgressBarStyle Style { get; set; }
}

public static class Application
{
    /// <summary>Nothing is displayed; the form's Shown handlers run, then Run returns.</summary>
    public static void Run(Form form) => form.RaiseShown();

    public static void DoEvents() { }

    public static void Exit() { }
}

public static class MessageBox
{
    public static event Action<string, string>? Shown;

    public static DialogResult Show(string text) => Show(text, string.Empty);

    public static DialogResult Show(string text, string caption)
    {
        Shown?.Invoke(text, caption);
        return DialogResult.OK;
    }
}
