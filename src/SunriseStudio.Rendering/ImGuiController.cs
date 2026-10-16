using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace SunriseStudio.Rendering;

/** Render ImGui last because its backend owns the active graphics state. */
public sealed unsafe class ImGuiController : IDisposable
{
    private readonly Dx11Renderer renderer;
    private readonly Control window;
    private readonly IntPtr imguiContext;
    private static int popouts;

    /** The name UiAuto gives this window's controls: "main", or "popout1", "popout2"... */
    public string AutomationSurface { get; set; } = "main";
    private ID3D11VertexShader vertexShader = null!;
    private ID3D11PixelShader pixelShader = null!;
    private ID3D11InputLayout inputLayout = null!;
    private ID3D11Buffer projectionBuffer = null!;
    private ID3D11BlendState blendState = null!;
    private ID3D11RasterizerState rasterizerState = null!;
    private ID3D11DepthStencilState depthState = null!;
    private ID3D11SamplerState sampler = null!;
    // Null in a controller that shares another controller's fonts.
    private GpuTexture? fontTexture;
    // Set in a controller that shares another controller's fonts; fonts are read through it.
    private readonly ImGuiController? owner;
    private ImFontPtr monospace, title;
    private ID3D11Buffer? vertices;
    private ID3D11Buffer? indices;
    private int vertexCapacity;
    private int indexCapacity;
    private bool frameStarted;
    private bool disposed;

    /**
     * Optional host faces (Solforge's bundled fonts). Called first on every atlas build with the body
     * pixel size; its first font becomes the default. Returns the title face, or null to keep the built-in faces.
     */
    public static Func<ImFontAtlasPtr, float, ImFontPtr?>? HostFonts { get; set; }

    public bool WantsMouse => ImGui.GetIO().WantCaptureMouse;
    public bool WantsKeyboard => ImGui.GetIO().WantCaptureKeyboard;
    public bool IsInputActive => IsActive();
    // Read each frame: a font rebuild replaces both.
    public ImFontPtr MonospaceFont => owner?.MonospaceFont ?? monospace;
    public ImFontPtr TitleFont => owner?.TitleFont ?? title;

    /** The window and renderer must outlive this controller. */
    public ImGuiController(Dx11Renderer renderer, Control window, string? fontPath = null,
        float fontSize = 16)
    {
        this.renderer = renderer;
        this.window = window;
        imguiContext = ImGui.CreateContext();
        ImGui.SetCurrentContext(imguiContext);
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.HasMouseCursors;
        io.NativePtr->IniFilename = null;
        AddFonts(io.Fonts, fontPath, fontSize);
        ImGui.StyleColorsDark();
        CreatePipeline();
        UploadFonts(io.Fonts);
        Subscribe();
    }

    /**
     * Rebuilds the fonts at a new size, for example after the window moves to a monitor with another
     * DPI. Call between frames; windows that share these fonts pick them up on their next frame.
     */
    public void RebuildFonts(string? fontPath, float fontSize)
    {
        if (owner is not null) throw new InvalidOperationException("Rebuild the fonts on the controller that owns them.");
        if (frameStarted) throw new InvalidOperationException("Render the current ImGui frame first.");
        var caller = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(imguiContext);
        try
        {
            var fonts = ImGui.GetIO().Fonts;
            fonts.Clear();
            AddFonts(fonts, fontPath, fontSize);
            var old = fontTexture;
            UploadFonts(fonts);
            old?.Dispose();
        }
        finally { ImGui.SetCurrentContext(caller); }
    }

    private void AddFonts(ImFontAtlasPtr fonts, string? fontPath, float fontSize)
    {
        var hostTitle = HostFonts?.Invoke(fonts, fontSize);
        if (hostTitle is null)
        {
            if (fontPath is not null && File.Exists(fontPath)) fonts.AddFontFromFileTTF(fontPath, fontSize);
            else fonts.AddFontDefault();
        }
        AddMonospace(fonts, fontSize);
        if (hostTitle is { } hosted) { title = hosted; return; }
        // The title font is Segoe UI Semibold when installed, else the body face, at 1.4 times the body size.
        string titlePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguisb.ttf");
        string? titleFace = File.Exists(titlePath) ? titlePath : fontPath is not null && File.Exists(fontPath) ? fontPath : null;
        title = titleFace is not null ? fonts.AddFontFromFileTTF(titleFace, fontSize * 1.4f) : fonts.AddFontDefault();
    }

    /**
     * The atlas keeps the range pointer until it is cleared, and fonts are rebuilt on DPI changes,
     * so the ranges live for the process: Latin through Cyrillic, punctuation, currency and letterlike
     * symbols to Misc Technical, and box drawing through Braille plus Misc Symbols and Arrows (TUI rules,
     * spinners, dingbats, stars). Neither face has U+FFFD, so missing glyphs fall back to '?'.
     */
    private static readonly ushort* TerminalRanges = MakeRanges(
        [0x0020, 0x052F, 0x2000, 0x206F, 0x20A0, 0x23FF, 0x2500, 0x28FF, 0x2B00, 0x2BFF]);

    private static ushort* MakeRanges(ReadOnlySpan<ushort> pairs)
    {
        var ranges = (ushort*)NativeMemory.AllocZeroed((nuint)(pairs.Length + 1), sizeof(ushort));
        pairs.CopyTo(new Span<ushort>(ranges, pairs.Length));
        return ranges;
    }

    /**
     * Consolas for code and the AI terminal, with Segoe UI Symbol merged in for the symbols Consolas
     * lacks. Merged glyphs are pinned to the Consolas advance so terminal columns stay on the grid.
     */
    private void AddMonospace(ImFontAtlasPtr fonts, float fontSize)
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string monoPath = Path.Combine(folder, "consola.ttf"), symbolPath = Path.Combine(folder, "seguisym.ttf");
        if (!File.Exists(monoPath)) { monospace = fonts.AddFontDefault(); return; }
        ImFontConfigPtr config = ImGuiNative.ImFontConfig_ImFontConfig();
        // Whole-pixel advances keep terminal columns, run text and cell fills on one grid at every DPI scale.
        config.PixelSnapH = true;
        monospace = fonts.AddFontFromFileTTF(monoPath, fontSize, config, (IntPtr)TerminalRanges);
        if (File.Exists(symbolPath) && CellAdvance(monoPath) is { } advance)
        {
            config.MergeMode = true;
            config.GlyphMinAdvanceX = config.GlyphMaxAdvanceX = advance * fontSize;
            fonts.AddFontFromFileTTF(symbolPath, fontSize, config, (IntPtr)TerminalRanges);
        }
        config.Destroy();
    }

    /**
     * A monospaced face's advance as a fraction of ImGui's pixel size (hhea ascent minus descent),
     * read from the first hmtx entry. Null when the tables are missing.
     */
    private static float? CellAdvance(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            int Read16(int at) => data[at] << 8 | data[at + 1];
            int Table(string tag)
            {
                for (int i = 0, count = Read16(4); i < count; i++)
                {
                    int entry = 12 + 16 * i;
                    if (System.Text.Encoding.ASCII.GetString(data, entry, 4) == tag)
                        return data[entry + 8] << 24 | data[entry + 9] << 16 | data[entry + 10] << 8 | data[entry + 11];
                }
                return -1;
            }
            int hhea = Table("hhea"), hmtx = Table("hmtx");
            if (hhea < 0 || hmtx < 0) return null;
            int height = (short)Read16(hhea + 4) - (short)Read16(hhea + 6);
            return height > 0 ? Read16(hmtx) / (float)height : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    private void UploadFonts(ImFontAtlasPtr fonts)
    {
        fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height);
        fontTexture = renderer.CreateTexture(width, height,
            new ReadOnlySpan<byte>((void*)pixels, checked(width * height * 4)));
        fonts.SetTexID(fontTexture.ImGuiId);
        fonts.ClearTexData();
    }

    /**
     * A controller for a second window on the same renderer. It shares the fonts of the first,
     * which must outlive it, and has its own ImGui context, input and style.
     */
    public ImGuiController(ImGuiController shared, Control window)
    {
        renderer = shared.renderer;
        this.window = window;
        owner = shared;
        AutomationSurface = "popout" + Interlocked.Increment(ref popouts);
        ImGui.SetCurrentContext(shared.imguiContext);
        var fonts = ImGui.GetIO().Fonts;
        imguiContext = ImGui.CreateContext(fonts);
        ImGui.SetCurrentContext(imguiContext);
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.HasMouseCursors;
        io.NativePtr->IniFilename = null;
        ImGui.StyleColorsDark();
        CreatePipeline();
        Subscribe();
    }

    /** Makes this controller's context current, so style and font calls apply to its window. */
    public void MakeCurrent() => ImGui.SetCurrentContext(imguiContext);

    private void Subscribe()
    {
        window.KeyDown += OnKeyDown;
        window.KeyUp += OnKeyUp;
        window.KeyPress += OnKeyPress;
        window.MouseWheel += OnMouseWheel;
        window.MouseDown += OnMouseDown;
        window.MouseUp += OnMouseUp;
        window.GotFocus += OnFocus;
        window.LostFocus += OnBlur;
    }

    private void CreatePipeline()
    {
        var device = renderer.Device;
        var vsCode = Dx11Renderer.Compile(Shader, "VSMain", "vs_5_0");
        vertexShader = device.CreateVertexShader(vsCode.Span);
        pixelShader = device.CreatePixelShader(Dx11Renderer.Compile(Shader, "PSMain", "ps_5_0").Span);
        inputLayout = device.CreateInputLayout(
        [
            new("POSITION", 0, Format.R32G32_Float, 0, 0),
            new("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
            new("COLOR", 0, Format.R8G8B8A8_UNorm, 16, 0)
        ], vsCode.Span);
        projectionBuffer = device.CreateBuffer((uint)sizeof(Matrix4x4), BindFlags.ConstantBuffer);
        blendState = device.CreateBlendState(new BlendDescription(Blend.SourceAlpha,
            Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha));
        rasterizerState = device.CreateRasterizerState(new RasterizerDescription(CullMode.None,
            FillMode.Solid)
        { ScissorEnable = true });
        depthState = device.CreateDepthStencilState(new DepthStencilDescription(false,
            DepthWriteMask.Zero, ComparisonFunction.Always));
        sampler = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear,
            TextureAddressMode.Clamp));
    }

    /** Feed input before building the menu with ImGui calls. */
    public void NewFrame(float deltaSeconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ImGui.SetCurrentContext(imguiContext);
        if (frameStarted) throw new InvalidOperationException("Render the previous ImGui frame first.");
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(renderer.Width, renderer.Height);
        io.DisplayFramebufferScale = Vector2.One;
        io.DeltaTime = Math.Clamp(deltaSeconds, 0.0001f, 0.25f);
        // Mouse state is global: read it while this window is active or directly under the pointer,
        // so an inactive window still hovers and takes its first click. Modifiers need focus.
        bool active = IsActive();
        if (active || PointerOver())
        {
            var cursor = window.PointToClient(Control.MousePosition);
            io.AddMousePosEvent(cursor.X, cursor.Y);
        }
        else
        {
            io.AddMousePosEvent(-float.MaxValue, -float.MaxValue);
            for (int button = 0; button < 5; button++) io.AddMouseButtonEvent(button, false);
        }
        UpdateModifiers(io, active);
        ImGui.NewFrame();
        UiAuto.BeginFrame(AutomationSurface);
        frameStarted = true;
    }

    /** Texture IDs are borrowed shader resource view pointers kept alive by their owners. */
    public void Render()
    {
        if (!frameStarted) return;
        ImGui.SetCurrentContext(imguiContext);
        ImGui.Render();
        frameStarted = false;
        ApplyCursor();
        var drawData = ImGui.GetDrawData();
        if (drawData.TotalVtxCount == 0 || drawData.DisplaySize.X <= 0 || drawData.DisplaySize.Y <= 0)
            return;
        EnsureBuffers(drawData.TotalVtxCount, drawData.TotalIdxCount);
        UploadDrawLists(drawData);
        SetRenderState(drawData);

        int vertexOffset = 0;
        uint indexOffset = 0;
        var context = renderer.Context;
        for (int listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
        {
            var list = drawData.CmdLists[listIndex];
            for (int commandIndex = 0; commandIndex < list.CmdBuffer.Size; commandIndex++)
            {
                var command = list.CmdBuffer[commandIndex];
                if (command.UserCallback != IntPtr.Zero)
                {
                    if (command.UserCallback == new IntPtr(-8)) SetRenderState(drawData);
                    else throw new NotSupportedException("Custom ImGui draw callbacks are not supported.");
                    continue;
                }
                var clip = command.ClipRect;
                var offset = drawData.DisplayPos;
                var scale = drawData.FramebufferScale;
                int left = Math.Max(0, (int)((clip.X - offset.X) * scale.X));
                int top = Math.Max(0, (int)((clip.Y - offset.Y) * scale.Y));
                int right = Math.Min(renderer.Width, (int)((clip.Z - offset.X) * scale.X));
                int bottom = Math.Min(renderer.Height, (int)((clip.W - offset.Y) * scale.Y));
                if (right <= left || bottom <= top || command.TextureId == IntPtr.Zero) continue;
                context.RSSetScissorRect(left, top, right - left, bottom - top);
                Marshal.AddRef(command.TextureId);
                using var textureView = new ID3D11ShaderResourceView(command.TextureId);
                context.PSSetShaderResource(0, textureView);
                context.DrawIndexed(command.ElemCount, indexOffset + command.IdxOffset,
                    vertexOffset + (int)command.VtxOffset);
            }
            vertexOffset += list.VtxBuffer.Size;
            indexOffset += (uint)list.IdxBuffer.Size;
        }
    }

    /** Dynamic buffers grow together with the draw data and persist across frames. */
    private void EnsureBuffers(int vertexCount, int indexCount)
    {
        if (vertexCount > vertexCapacity)
        {
            vertices?.Dispose();
            vertexCapacity = checked(vertexCount + 5000);
            vertices = renderer.Device.CreateBuffer((uint)(vertexCapacity * sizeof(ImDrawVert)),
                BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        }
        if (indexCount > indexCapacity)
        {
            indices?.Dispose();
            indexCapacity = checked(indexCount + 10000);
            indices = renderer.Device.CreateBuffer((uint)(indexCapacity * sizeof(ushort)),
                BindFlags.IndexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        }
    }

    /** ImGui.NET uses 16-bit indices with a separate vertex offset for each command. */
    private void UploadDrawLists(ImDrawDataPtr drawData)
    {
        var context = renderer.Context;
        var mappedVertices = context.Map(vertices!, 0, MapMode.WriteDiscard, MapFlags.None);
        try
        {
            var mappedIndices = context.Map(indices!, 0, MapMode.WriteDiscard, MapFlags.None);
            try
            {
                byte* vertexDestination = (byte*)mappedVertices.DataPointer;
                byte* indexDestination = (byte*)mappedIndices.DataPointer;
                for (int i = 0; i < drawData.CmdListsCount; i++)
                {
                    var list = drawData.CmdLists[i];
                    int vertexBytes = list.VtxBuffer.Size * sizeof(ImDrawVert);
                    int indexBytes = list.IdxBuffer.Size * sizeof(ushort);
                    Buffer.MemoryCopy((void*)list.VtxBuffer.Data, vertexDestination,
                        vertexBytes, vertexBytes);
                    Buffer.MemoryCopy((void*)list.IdxBuffer.Data, indexDestination,
                        indexBytes, indexBytes);
                    vertexDestination += vertexBytes;
                    indexDestination += indexBytes;
                }
            }
            finally { context.Unmap(indices!, 0); }
        }
        finally { context.Unmap(vertices!, 0); }
    }

    /** Projection uses the draw data origin so clipped windows keep their coordinates. */
    private void SetRenderState(ImDrawDataPtr drawData)
    {
        var position = drawData.DisplayPos;
        var extent = drawData.DisplaySize;
        var projection = Matrix4x4.CreateOrthographicOffCenter(position.X, position.X + extent.X,
            position.Y + extent.Y, position.Y, 0, 1);
        var context = renderer.Context;
        context.UpdateSubresource(in projection, projectionBuffer);
        context.IASetInputLayout(inputLayout);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetVertexBuffer(0, vertices!, (uint)sizeof(ImDrawVert));
        context.IASetIndexBuffer(indices!, Format.R16_UInt, 0);
        context.VSSetShader(vertexShader);
        context.VSSetConstantBuffer(0, projectionBuffer);
        context.PSSetShader(pixelShader);
        context.PSSetSampler(0, sampler);
        context.RSSetState(rasterizerState);
        context.RSSetViewport(new Viewport(renderer.Width, renderer.Height));
        context.OMSetBlendState(blendState);
        context.OMSetDepthStencilState(depthState);
    }

    private bool IsActive() => Form.ActiveForm is { } form && form == window.FindForm();

    /** True when no other window covers this one at the pointer. */
    private bool PointerOver()
    {
        var at = Control.MousePosition;
        return window.IsHandleCreated && WindowFromPoint(new() { X = at.X, Y = at.Y }) == window.Handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(ScreenPoint point);

    /** The cursor the frame asked for becomes the window's; Windows shows it over the client area. */
    private void ApplyCursor()
    {
        var wanted = ImGui.GetMouseCursor() switch
        {
            ImGuiMouseCursor.TextInput => Cursors.IBeam,
            ImGuiMouseCursor.ResizeAll => Cursors.SizeAll,
            ImGuiMouseCursor.ResizeNS => Cursors.SizeNS,
            ImGuiMouseCursor.ResizeEW => Cursors.SizeWE,
            ImGuiMouseCursor.ResizeNESW => Cursors.SizeNESW,
            ImGuiMouseCursor.ResizeNWSE => Cursors.SizeNWSE,
            ImGuiMouseCursor.Hand => Cursors.Hand,
            ImGuiMouseCursor.NotAllowed => Cursors.No,
            _ => Cursors.Default
        };
        if (window.Cursor != wanted) window.Cursor = wanted;
    }

    private static void UpdateModifiers(ImGuiIOPtr io, bool active)
    {
        var modifiers = active ? Control.ModifierKeys : Keys.None;
        io.AddKeyEvent(ImGuiKey.ModCtrl, (modifiers & Keys.Control) != 0);
        io.AddKeyEvent(ImGuiKey.ModShift, (modifiers & Keys.Shift) != 0);
        io.AddKeyEvent(ImGuiKey.ModAlt, (modifiers & Keys.Alt) != 0);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e) => SendKey(e.KeyCode, true);
    private void OnKeyUp(object? sender, KeyEventArgs e) => SendKey(e.KeyCode, false);
    private void OnKeyPress(object? sender, KeyPressEventArgs e)
    {
        if (!IsActive()) return;
        ImGui.SetCurrentContext(imguiContext);
        ImGui.GetIO().AddInputCharacter(e.KeyChar);
    }
    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        // Windows sends the wheel to an inactive window under the cursor.
        if (!IsActive()) return;
        ImGui.SetCurrentContext(imguiContext);
        ImGui.GetIO().AddMouseWheelEvent(0, e.Delta / 120f);
    }
    // Buttons are queued per transition; a per-frame sample loses a click that fits between frames.
    private void OnMouseDown(object? sender, MouseEventArgs e) => SendButton(e.Button, true);
    private void OnMouseUp(object? sender, MouseEventArgs e) => SendButton(e.Button, false);
    private void SendButton(MouseButtons button, bool down)
    {
        // The click that activates the window still reaches the item under it.
        if (down && !IsActive() && !PointerOver()) return;
        int index = button switch
        {
            MouseButtons.Left => 0, MouseButtons.Right => 1, MouseButtons.Middle => 2,
            MouseButtons.XButton1 => 3, MouseButtons.XButton2 => 4, _ => -1
        };
        if (index < 0) return;
        ImGui.SetCurrentContext(imguiContext);
        var io = ImGui.GetIO();
        // The press moves the pointer before the button, so the click lands where it happened.
        var cursor = window.PointToClient(Control.MousePosition);
        io.AddMousePosEvent(cursor.X, cursor.Y);
        io.AddMouseButtonEvent(index, down);
    }
    private void OnFocus(object? sender, EventArgs e)
    {
        ImGui.SetCurrentContext(imguiContext);
        ImGui.GetIO().AddFocusEvent(true);
    }
    private void OnBlur(object? sender, EventArgs e)
    {
        ImGui.SetCurrentContext(imguiContext);
        ImGui.GetIO().AddFocusEvent(false);
    }
    private void SendKey(Keys key, bool down)
    {
        if (down && !IsActive()) return;
        ImGui.SetCurrentContext(imguiContext);
        var imguiKey = MapKey(key);
        if (imguiKey != ImGuiKey.None) ImGui.GetIO().AddKeyEvent(imguiKey, down);
    }

    /** WinForms key codes need explicit mapping to ImGui's named key range. */
    private static ImGuiKey MapKey(Keys key)
    {
        if (key >= Keys.A && key <= Keys.Z) return ImGuiKey.A + (key - Keys.A);
        if (key >= Keys.D0 && key <= Keys.D9) return ImGuiKey._0 + (key - Keys.D0);
        if (key >= Keys.F1 && key <= Keys.F12) return ImGuiKey.F1 + (key - Keys.F1);
        if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return ImGuiKey.Keypad0 + (key - Keys.NumPad0);
        return key switch
        {
            Keys.Tab => ImGuiKey.Tab,
            Keys.Left => ImGuiKey.LeftArrow,
            Keys.Right => ImGuiKey.RightArrow,
            Keys.Up => ImGuiKey.UpArrow,
            Keys.Down => ImGuiKey.DownArrow,
            Keys.PageUp => ImGuiKey.PageUp,
            Keys.PageDown => ImGuiKey.PageDown,
            Keys.Home => ImGuiKey.Home,
            Keys.End => ImGuiKey.End,
            Keys.Insert => ImGuiKey.Insert,
            Keys.Delete => ImGuiKey.Delete,
            Keys.Back => ImGuiKey.Backspace,
            Keys.Space => ImGuiKey.Space,
            Keys.Enter => ImGuiKey.Enter,
            Keys.Escape => ImGuiKey.Escape,
            Keys.OemQuotes => ImGuiKey.Apostrophe,
            Keys.Oemcomma => ImGuiKey.Comma,
            Keys.OemMinus => ImGuiKey.Minus,
            Keys.OemPeriod => ImGuiKey.Period,
            Keys.OemQuestion => ImGuiKey.Slash,
            Keys.OemSemicolon => ImGuiKey.Semicolon,
            Keys.Oemplus => ImGuiKey.Equal,
            Keys.OemOpenBrackets => ImGuiKey.LeftBracket,
            Keys.OemPipe => ImGuiKey.Backslash,
            Keys.OemCloseBrackets => ImGuiKey.RightBracket,
            Keys.Oemtilde => ImGuiKey.GraveAccent,
            Keys.CapsLock => ImGuiKey.CapsLock,
            Keys.Scroll => ImGuiKey.ScrollLock,
            Keys.NumLock => ImGuiKey.NumLock,
            Keys.PrintScreen => ImGuiKey.PrintScreen,
            Keys.Pause => ImGuiKey.Pause,
            Keys.Decimal => ImGuiKey.KeypadDecimal,
            Keys.Divide => ImGuiKey.KeypadDivide,
            Keys.Multiply => ImGuiKey.KeypadMultiply,
            Keys.Subtract => ImGuiKey.KeypadSubtract,
            Keys.Add => ImGuiKey.KeypadAdd,
            Keys.LShiftKey or Keys.ShiftKey => ImGuiKey.LeftShift,
            Keys.RShiftKey => ImGuiKey.RightShift,
            Keys.LControlKey or Keys.ControlKey => ImGuiKey.LeftCtrl,
            Keys.RControlKey => ImGuiKey.RightCtrl,
            Keys.LMenu or Keys.Menu => ImGuiKey.LeftAlt,
            Keys.RMenu => ImGuiKey.RightAlt,
            Keys.LWin => ImGuiKey.LeftSuper,
            Keys.RWin => ImGuiKey.RightSuper,
            Keys.Apps => ImGuiKey.Menu,
            _ => ImGuiKey.None
        };
    }

    /** Unbind window events before destroying the native ImGui context. */
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        window.KeyDown -= OnKeyDown;
        window.KeyUp -= OnKeyUp;
        window.KeyPress -= OnKeyPress;
        window.MouseWheel -= OnMouseWheel;
        window.MouseDown -= OnMouseDown;
        window.MouseUp -= OnMouseUp;
        window.GotFocus -= OnFocus;
        window.LostFocus -= OnBlur;
        indices?.Dispose();
        vertices?.Dispose();
        fontTexture?.Dispose();
        sampler.Dispose();
        depthState.Dispose();
        rasterizerState.Dispose();
        blendState.Dispose();
        projectionBuffer.Dispose();
        inputLayout.Dispose();
        pixelShader.Dispose();
        vertexShader.Dispose();
        ImGui.DestroyContext(imguiContext);
    }

    private const string Shader = """
        cbuffer Projection : register(b0) { row_major float4x4 Matrix; };
        Texture2D FontOrImage : register(t0);
        SamplerState ImageSampler : register(s0);
        struct PSInput { float4 Position : SV_POSITION; float2 UV : TEXCOORD0; float4 Color : COLOR0; };
        PSInput VSMain(float2 position : POSITION, float2 uv : TEXCOORD0, float4 color : COLOR0) {
            PSInput output;
            output.Position = mul(float4(position, 0, 1), Matrix);
            output.UV = uv;
            output.Color = color;
            return output;
        }
        float4 PSMain(PSInput input) : SV_TARGET {
            return input.Color * FontOrImage.Sample(ImageSampler, input.UV);
        }
        """;
}
