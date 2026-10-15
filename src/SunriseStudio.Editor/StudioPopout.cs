using ImGuiNET;
using SunriseStudio.Rendering;

namespace SunriseStudio.Editor;

/** One editor in its own window. The main window's frame draws it; closing the window puts the editor back. */
internal sealed class StudioPopout : Form
{
    private WindowTarget? target;
    private ImGuiController? gui;

    public StudioPanelKind Panel { get; }
    public WindowTarget Target => target ?? throw new InvalidOperationException("The window is not attached.");
    public ImGuiController Gui => gui ?? throw new InvalidOperationException("The window is not attached.");
    public bool IsAttached => target is not null && gui is not null;
    /** Set when the user closes the window; the main frame then docks the editor again. */
    public bool PopInRequested { get; private set; }

    public void RequestPopIn() => PopInRequested = true;

    /** The themed frame; its close button docks the editor again. */
    public WindowChrome Chrome { get; }

    public StudioPopout(StudioPanelKind panel, string title, Icon icon, Rectangle? bounds)
    {
        Panel = panel;
        Chrome = new(this);
        Text = $"{title} - Solforge";
        Icon = icon;
        MinimumSize = new Size(320, 240);
        KeyPreview = true;
        ShowInTaskbar = false;
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        if (bounds is { } screen && Screen.AllScreens.Any(display => display.WorkingArea.IntersectsWith(screen)))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = screen;
        }
        else
        {
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(900, 640);
        }
    }

    /** Call after Show, on the window thread; the renderer and the shared controller must outlive the window. */
    public void Attach(Dx11Renderer renderer, ImGuiController shared, float scale)
    {
        // The caller may be inside the main window's frame, so its context stays current.
        var caller = ImGui.GetCurrentContext();
        try
        {
            target = renderer.CreateWindowTarget(Handle, ClientSize.Width, ClientSize.Height);
            gui = new ImGuiController(shared, this);
            StudioTheme.Apply(scale);
        }
        finally { ImGui.SetCurrentContext(caller); }
    }

    /** Reapplies the theme at a new DPI scale; the shared fonts are rebuilt by the main window. */
    public void Rescale(float scale)
    {
        if (gui is null) return;
        var caller = ImGui.GetCurrentContext();
        try { gui.MakeCurrent(); StudioTheme.Apply(scale); }
        finally { ImGui.SetCurrentContext(caller); }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Chrome.Attach();
    }

    protected override void WndProc(ref Message m)
    {
        if (Chrome.WndProc(ref m)) return;
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Tab or Keys.Left or Keys.Right or Keys.Up or Keys.Down
        || base.IsInputKey(keyData);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The user's close docks the editor; the main window disposes this one itself.
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; PopInRequested = true; }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            gui?.Dispose(); gui = null;
            target?.Dispose(); target = null;
        }
        base.Dispose(disposing);
    }
}
