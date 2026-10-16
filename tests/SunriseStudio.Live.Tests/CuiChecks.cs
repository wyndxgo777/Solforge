using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using SunriseStudio.Live;

/** The CUI link mirrors the native SSCU layout; Studio shows only answers to its current requests. */
internal static class CuiChecks
{
    public const uint ScreenTag = 0x80801111, WidgetTag = 0x80802222;

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run()
    {
        Offsets();
        RoundTrip();
        Rejections();
        Connection();
        Console.WriteLine("live cui_offsets=checked cui_round_trip=checked cui_rejects=checked cui_connection=checked");
    }

    /** Expected numbers are the native header's; a change on either side must change both. */
    private static void Offsets()
    {
        Check(LiveCuiProtocol.Magic == 0x55435353 && LiveCuiProtocol.Version == 1, "CUI magic or version differs.");
        Check(LiveCuiProtocol.MappingBytes == 460736 && LiveCuiProtocol.TreeBytes == 458816, "CUI mapping size differs.");
        Check(LiveCuiProtocol.MappingName(42) == @"Local\SunriseStudio.Cui.42", "CUI mapping name differs.");
        int[] actual =
        [
            LiveCuiProtocol.ControlBytes, LiveCuiProtocol.ScreenRowBytes, LiveCuiProtocol.WidgetRowBytes,
            LiveCuiProtocol.ScreensOffset, LiveCuiProtocol.ScreensBytes, LiveCuiProtocol.DetailOffset, LiveCuiProtocol.DetailBytes,
            LiveCuiProtocol.TreeOffset, LiveCuiProtocol.TreeHeaderBytes,
            LiveCuiProtocol.MagicOffset, LiveCuiProtocol.VersionOffset, LiveCuiProtocol.BytesOffset,
            LiveCuiProtocol.EditorPidOffset, LiveCuiProtocol.HeartbeatOffset, LiveCuiProtocol.ClientVersionOffset, LiveCuiProtocol.BridgeFlagsOffset,
            LiveCuiProtocol.GamePidOffset, LiveCuiProtocol.CaptureMicrosOffset, LiveCuiProtocol.CheckedTimeOffset,
            LiveCuiProtocol.WatchRequestOffset, LiveCuiProtocol.WatchHierarchyOffset, LiveCuiProtocol.WatchOrdinalOffset,
            LiveCuiProtocol.WidgetRequestOffset, LiveCuiProtocol.WidgetHierarchyOffset, LiveCuiProtocol.WidgetOrdinalOffset,
            LiveCuiProtocol.WidgetNodeOffset, LiveCuiProtocol.SequenceOffset, LiveCuiProtocol.ResponseOffset,
            LiveCuiProtocol.ScreensCountOffset, LiveCuiProtocol.ScreensTimeOffset, LiveCuiProtocol.ScreensFlagsOffset,
            LiveCuiProtocol.Focus0Offset, LiveCuiProtocol.Focus1Offset, LiveCuiProtocol.ScreenRowsOffset,
            LiveCuiProtocol.ScreenHierarchyOffset, LiveCuiProtocol.ScreenTagOffset, LiveCuiProtocol.ScreenViewOffset, LiveCuiProtocol.ScreenPageOffset,
            LiveCuiProtocol.ScreenFlagsOffset, LiveCuiProtocol.ScreenStateOffset, LiveCuiProtocol.ScreenPriorityOffset,
            LiveCuiProtocol.ScreenLayerOffset, LiveCuiProtocol.ScreenOrdinalOffset,
            LiveCuiProtocol.DetailStatusOffset, LiveCuiProtocol.DetailFlagsOffset, LiveCuiProtocol.DetailTimeOffset,
            LiveCuiProtocol.DetailTypeOffset, LiveCuiProtocol.DetailHierarchyOffset, LiveCuiProtocol.DetailNameOffset,
            LiveCuiProtocol.DetailNodeOffset, LiveCuiProtocol.DetailParentOffset, LiveCuiProtocol.DetailStateOffset,
            LiveCuiProtocol.DetailSlotFlagsOffset, LiveCuiProtocol.DetailObjectFlagsOffset, LiveCuiProtocol.DetailChildCountOffset,
            LiveCuiProtocol.DetailSlotsOffset, LiveCuiProtocol.DetailTextHashOffset, LiveCuiProtocol.DetailStyleOffset,
            LiveCuiProtocol.DetailTextSourceOffset, LiveCuiProtocol.DetailTextStateOffset, LiveCuiProtocol.DetailTextLengthOffset,
            LiveCuiProtocol.DetailTextModeOffset, LiveCuiProtocol.DetailTextOffset,
            LiveCuiProtocol.TreeTimeOffset, LiveCuiProtocol.TreeStatusOffset, LiveCuiProtocol.TreeCountOffset,
            LiveCuiProtocol.TreeFlagsOffset, LiveCuiProtocol.TreeRevisionOffset, LiveCuiProtocol.TreeHierarchyOffset,
            LiveCuiProtocol.TreeScreenTagOffset, LiveCuiProtocol.TreeViewOffset, LiveCuiProtocol.TreeTotalOffset,
            LiveCuiProtocol.RowTypeOffset, LiveCuiProtocol.RowHierarchyOffset, LiveCuiProtocol.RowNameOffset, LiveCuiProtocol.RowNodeOffset,
            LiveCuiProtocol.RowParentOffset, LiveCuiProtocol.RowStateOffset, LiveCuiProtocol.RowFlagsOffset,
            LiveCuiProtocol.RowTextHashOffset, LiveCuiProtocol.RowVisibilityOffset, LiveCuiProtocol.RowXOffset,
            LiveCuiProtocol.RowYOffset, LiveCuiProtocol.RowWidthOffset, LiveCuiProtocol.RowHeightOffset,
        ];
        int[] expected =
        [
            128, 32, 56, 128, 1152, 1280, 640, 1920, 64,
            0, 4, 8, 12, 16, 24, 28, 32, 36, 40, 48, 52, 56, 60, 64, 68, 72, 0, 4,
            4, 8, 16, 20, 24, 32, 0, 4, 8, 12, 16, 20, 24, 28, 29,
            8, 12, 16, 24, 32, 36, 40, 42, 44, 48, 52, 54, 56, 96, 100, 104, 105, 106, 108, 112,
            8, 16, 20, 24, 28, 32, 36, 40, 44,
            0, 8, 12, 16, 18, 20, 24, 28, 32, 36, 40, 44, 48,
        ];
        Check(actual.SequenceEqual(expected), "A CUI offset differs from the native header.");
        // Property 0x201 drives state bit 0x2; bit 0x4 belongs to selector 0x202.
        Check(LiveCuiProtocol.StateVisible == 0x2, "The CUI visibility bit differs from the native header.");
    }

    private static void RoundTrip()
    {
        var screens = LiveCuiProtocol.ReadScreens(Screens(2));
        Check(screens.Rows.Count == 2 && screens.Focus0 == 0 && screens.Focus1 == 1 && screens.Top == screens.Rows[1]
            && screens.Rows[0] is { HierarchyTag: ScreenTag, ScreenTag: 0x80803333, ViewHash: 7, Page: 2, Priority: 100,
                Layer: 0, Ordinal: 0, Visible: true, SortLast: false }
            && screens.Rows[1] is { Layer: 1, Ordinal: 1, SortLast: true, Visible: false } && !screens.Truncated,
            "CUI screen rows differ from the native layout.");
        var unsorted = Screens(2);
        BinaryPrimitives.WriteInt32LittleEndian(unsorted.AsSpan(LiveCuiProtocol.ScreenRowsOffset + LiveCuiProtocol.ScreenRowBytes
            + LiveCuiProtocol.ScreenPriorityOffset), -1);
        Check(LiveCuiProtocol.ReadScreens(unsorted).Rows[1].Priority == -1, "The CUI screen priority lost its sign.");

        var tree = LiveCuiProtocol.ReadTree(Tree(9, 3));
        Check(tree is { Request: 9, Status: LiveCuiStatus.Complete, Revision: 5, HierarchyTag: ScreenTag, Total: 3 }
            && tree.Rows.Count == 3 && tree.Rows[0].ParentRow is null && tree.Rows[2].ParentRow == 1
            && tree.Rows[1] is { NodeId: 11, NameHash: 0x101, Visible: true, IsText: true, SlotsRead: true, X: 10,
                Y: 20, Width: 30, Height: 40, Visibility: 0.5f, TextHash: 0xAABB, TypeKey: 0x1234567890 },
            "CUI widget rows differ from the native layout.");
        var objects = Tree(9, 3);
        U(objects, Row(2) + LiveCuiProtocol.RowFlagsOffset, 0); U(objects, Row(2) + LiveCuiProtocol.RowStateOffset, 0);
        U(objects, Row(1) + LiveCuiProtocol.RowStateOffset, 0);
        var rows = LiveCuiProtocol.ReadTree(objects).Rows;
        Check(rows[2] is { IsWidget: false, Visible: true } && rows[1] is { IsWidget: true, Visible: false },
            "A row that is not a widget read as hidden, or a hidden widget read as shown.");

        var target = new LiveCuiWidgetTarget(ScreenTag, 0, 11);
        Check(LiveCuiProtocol.ReadDetail(Detail(4, "Hi"), 3, target) is null, "A CUI answer to another id was taken.");
        var detail = LiveCuiProtocol.ReadDetail(Detail(4, "Hi"), 4, target)!;
        Check(detail is { Status: LiveCuiStatus.Complete, Text: "Hi", IsText: true, NodeId: 11, ParentNode: null,
                TextHash: 0xAABB, StyleKey: 0x51, TextSource: 2, TextState: 1, TextMode: 3, ChildCount: 2,
                Visible: true, TypeKey: 0x1234567890 }
            && detail.Slots is [_, 10, 20, 30, 40, _, 0.5f, ..], "The CUI widget record differs from the native layout.");
    }

    private static void Rejections()
    {
        var bytes = Screens(2); U(bytes, LiveCuiProtocol.ScreensCountOffset, 33);
        Rejects(() => LiveCuiProtocol.ReadScreens(bytes), "A screen count over capacity");
        bytes = Screens(2); U(bytes, LiveCuiProtocol.Focus1Offset, 2);
        Rejects(() => LiveCuiProtocol.ReadScreens(bytes), "A focus past the screen list");
        bytes = Screens(2); U(bytes, LiveCuiProtocol.SequenceOffset, 3);
        Rejects(() => LiveCuiProtocol.ReadScreens(bytes), "An odd screen sequence");
        bytes = Tree(1, 3); U(bytes, LiveCuiProtocol.TreeCountOffset, 8193);
        Rejects(() => LiveCuiProtocol.ReadTree(bytes), "A tree count over capacity");
        bytes = Tree(1, 3); U(bytes, LiveCuiProtocol.TreeStatusOffset, 4);
        Rejects(() => LiveCuiProtocol.ReadTree(bytes), "An unknown tree status");
        bytes = Tree(1, 3); U(bytes, LiveCuiProtocol.TreeStatusOffset, 0);
        Rejects(() => LiveCuiProtocol.ReadTree(bytes), "A pending tree");
        bytes = Tree(1, 3); F(bytes, Row(1) + LiveCuiProtocol.RowXOffset, float.NaN);
        Rejects(() => LiveCuiProtocol.ReadTree(bytes), "A non-finite widget slot");
        bytes = Tree(1, 3); H(bytes, Row(2) + LiveCuiProtocol.RowParentOffset, 2);
        Rejects(() => LiveCuiProtocol.ReadTree(bytes), "A widget that is its own parent");
        bytes = Tree(1, 3); H(bytes, Row(1) + LiveCuiProtocol.RowParentOffset, LiveCuiProtocol.NoRow);
        Rejects(() => LiveCuiProtocol.ReadTree(bytes), "A second root row");
        bytes = Detail(4, "Hi"); H(bytes, LiveCuiProtocol.DetailTextLengthOffset, 257);
        Rejects(() => LiveCuiProtocol.ReadDetail(bytes, 4, default), "Text over capacity");
        bytes = Detail(4, "Hi"); F(bytes, LiveCuiProtocol.DetailSlotsOffset + 4, float.PositiveInfinity);
        Rejects(() => LiveCuiProtocol.ReadDetail(bytes, 4, default), "A non-finite detail slot");
        bytes = Detail(4, "Hi"); U(bytes, LiveCuiProtocol.DetailStatusOffset, 9);
        Rejects(() => LiveCuiProtocol.ReadDetail(bytes, 4, default), "An unknown detail status");
    }

    /** Drives the internal connection against its own mapping, as the game would answer it. */
    private static void Connection()
    {
        var type = typeof(LiveConnection).Assembly.GetType("SunriseStudio.Live.LiveCuiConnection")!;
        using var connection = (IDisposable)Activator.CreateInstance(type, Environment.ProcessId)!;
        using var mapping = MemoryMappedFile.OpenExisting(LiveCuiProtocol.MappingName(Environment.ProcessId));
        using var view = mapping.CreateViewAccessor();
        void Poll() => type.GetMethod("Poll")!.Invoke(connection, [(ulong)Environment.TickCount64]);
        void Watch(LiveCuiTarget? target) => type.GetMethod("Watch")!.Invoke(connection, [target]);
        void Request(LiveCuiWidgetTarget? target) => type.GetMethod("Request")!.Invoke(connection, [target]);
        T? Get<T>(string name) => (T?)type.GetProperty(name)!.GetValue(connection);
        Check(view.ReadUInt32(LiveCuiProtocol.MagicOffset) == LiveCuiProtocol.Magic
            && view.ReadUInt32(LiveCuiProtocol.VersionOffset) == LiveCuiProtocol.Version
            && view.ReadUInt32(LiveCuiProtocol.BytesOffset) == LiveCuiProtocol.MappingBytes
            && view.ReadInt32(LiveCuiProtocol.EditorPidOffset) == Environment.ProcessId, "The CUI header was not written.");

        view.Write(LiveCuiProtocol.ClientVersionOffset, LiveCuiProtocol.Version + 1); Poll();
        Check(Get<string>("Diagnostic")!.Contains("version differs"), "A CUI version mismatch lacks a note.");
        view.Write(LiveCuiProtocol.ClientVersionOffset, LiveCuiProtocol.Version); Poll();
        Check(Get<string>("Diagnostic")!.Contains("not built its screen list") && Get<bool>("Settling"),
            "A missing screen manager lacks a note or reads as a broken link.");
        view.Write(LiveCuiProtocol.BridgeFlagsOffset, LiveCuiProtocol.ManagerReady);
        view.Write(LiveCuiProtocol.CheckedTimeOffset, Environment.TickCount64);
        view.WriteArray(LiveCuiProtocol.ScreensOffset, Screens(2), 0, LiveCuiProtocol.ScreensBytes); Poll();
        Check(Get<string>("Diagnostic") == "" && Get<bool>("Alive") && !Get<bool>("Settling") && Get<LiveCuiScreens>("Screens")!.Rows.Count == 2,
            "A live CUI screen list was not read.");

        var target = new LiveCuiTarget(ScreenTag, 0);
        Watch(target);
        uint watch = view.ReadUInt32(LiveCuiProtocol.WatchRequestOffset);
        Check(watch != 0 && view.ReadUInt32(LiveCuiProtocol.WatchHierarchyOffset) == ScreenTag, "The watch did not write its target.");
        Watch(target);
        Check(view.ReadUInt32(LiveCuiProtocol.WatchRequestOffset) == watch, "Watching the same screen issued a new id.");
        var tree = Tree(watch + 1, 3);
        view.WriteArray(LiveCuiProtocol.TreeOffset, tree, 0, tree.Length); Poll();
        Check(Get<LiveCuiTree>("Tree") is null, "A tree for another watch was shown.");
        tree = Tree(watch, 3); U(tree, LiveCuiProtocol.SequenceOffset, 4);
        view.WriteArray(LiveCuiProtocol.TreeOffset, tree, 0, tree.Length); Poll();
        Check(Get<LiveCuiTree>("Tree") is { Rows.Count: 3 }, "The tree for the current watch was not read.");
        Watch(new LiveCuiTarget(ScreenTag, 1));
        Check(Get<LiveCuiTree>("Tree") is null, "A tree outlived its watch.");
        Watch(null);
        Check(view.ReadUInt32(LiveCuiProtocol.WatchRequestOffset) == 0, "Stopping the watch did not clear its id.");

        Request(new LiveCuiWidgetTarget(ScreenTag, 0, 11));
        uint request = view.ReadUInt32(LiveCuiProtocol.WidgetRequestOffset);
        Check(request != 0 && view.ReadUInt32(LiveCuiProtocol.WidgetNodeOffset) == 11
            && Get<LiveCuiDetail>("Detail") is { Status: LiveCuiStatus.Pending }, "The widget request was not written.");
        view.WriteArray(LiveCuiProtocol.DetailOffset, Detail(request, "Hi"), 0, LiveCuiProtocol.DetailBytes); Poll();
        Check(Get<LiveCuiDetail>("Detail") is { Status: LiveCuiStatus.Complete, Text: "Hi" }, "The widget answer was not read.");

        Request(new LiveCuiWidgetTarget(ScreenTag, 0, 11));
        request = view.ReadUInt32(LiveCuiProtocol.WidgetRequestOffset);
        var bad = Detail(request, "Hi"); U(bad, LiveCuiProtocol.SequenceOffset, 4);
        F(bad, LiveCuiProtocol.DetailSlotsOffset, float.NaN);
        view.WriteArray(LiveCuiProtocol.DetailOffset, bad, 0, LiveCuiProtocol.DetailBytes); Poll();
        // A bad widget answer marks only that answer; the screens and the tree still work.
        Check(Get<LiveCuiDetail>("Detail") is { Status: LiveCuiStatus.Unavailable } && Get<string>("Diagnostic") == "",
            "A bad widget answer kept the wait or broke the link status.");
        var screens = Screens(2); U(screens, LiveCuiProtocol.SequenceOffset, 4);
        U(screens, LiveCuiProtocol.ScreensCountOffset, 33);
        view.WriteArray(LiveCuiProtocol.ScreensOffset, screens, 0, LiveCuiProtocol.ScreensBytes); Poll();
        Check(Get<string>("Diagnostic")!.Contains("too many rows") && !Get<bool>("Settling"), "A bad screen list lacks a note.");
    }

    /** Two screens: a visible one on layer 0 and a flag-0x10 one on layer 1 with the same tag. */
    public static byte[] Screens(int count)
    {
        var bytes = new byte[LiveCuiProtocol.ScreensBytes];
        U(bytes, LiveCuiProtocol.SequenceOffset, 2); U(bytes, LiveCuiProtocol.ScreensCountOffset, (uint)count);
        Q(bytes, LiveCuiProtocol.ScreensTimeOffset, 1000);
        U(bytes, LiveCuiProtocol.Focus0Offset, 0); U(bytes, LiveCuiProtocol.Focus1Offset, 1);
        for (int i = 0; i < count; i++)
        {
            int at = LiveCuiProtocol.ScreenRowsOffset + i * LiveCuiProtocol.ScreenRowBytes;
            U(bytes, at, ScreenTag); U(bytes, at + LiveCuiProtocol.ScreenTagOffset, 0x80803333);
            U(bytes, at + LiveCuiProtocol.ScreenViewOffset, 7); U(bytes, at + LiveCuiProtocol.ScreenPageOffset, 2);
            U(bytes, at + LiveCuiProtocol.ScreenFlagsOffset, i == 1 ? LiveCuiProtocol.ScreenSortLast : 0);
            U(bytes, at + LiveCuiProtocol.ScreenStateOffset, i == 0 ? LiveCuiProtocol.StateVisible : 0);
            U(bytes, at + LiveCuiProtocol.ScreenPriorityOffset, 100);
            bytes[at + LiveCuiProtocol.ScreenLayerOffset] = (byte)i; bytes[at + LiveCuiProtocol.ScreenOrdinalOffset] = (byte)i;
        }
        return bytes;
    }

    /** A root, a text child and a grandchild; row 1 carries slots and text. */
    public static byte[] Tree(uint response, int count)
    {
        var bytes = new byte[LiveCuiProtocol.TreeHeaderBytes + count * LiveCuiProtocol.WidgetRowBytes];
        U(bytes, LiveCuiProtocol.SequenceOffset, 2); U(bytes, LiveCuiProtocol.ResponseOffset, response);
        Q(bytes, LiveCuiProtocol.TreeTimeOffset, 1000);
        U(bytes, LiveCuiProtocol.TreeStatusOffset, (uint)LiveCuiStatus.Complete);
        U(bytes, LiveCuiProtocol.TreeCountOffset, (uint)count); U(bytes, LiveCuiProtocol.TreeRevisionOffset, 5);
        U(bytes, LiveCuiProtocol.TreeHierarchyOffset, ScreenTag); U(bytes, LiveCuiProtocol.TreeTotalOffset, (uint)count);
        for (int i = 0; i < count; i++)
        {
            int at = Row(i);
            Q(bytes, at, 0x1234567890); U(bytes, at + LiveCuiProtocol.RowHierarchyOffset, ScreenTag);
            U(bytes, at + LiveCuiProtocol.RowNameOffset, 0x100u + (uint)i);
            H(bytes, at + LiveCuiProtocol.RowNodeOffset, (ushort)(10 + i));
            H(bytes, at + LiveCuiProtocol.RowParentOffset, i == 0 ? LiveCuiProtocol.NoRow : (ushort)(i - 1));
            U(bytes, at + LiveCuiProtocol.RowStateOffset, LiveCuiProtocol.StateVisible);
            U(bytes, at + LiveCuiProtocol.RowFlagsOffset, LiveCuiProtocol.RowWidget);
        }
        int text = Row(1);
        U(bytes, text + LiveCuiProtocol.RowFlagsOffset, LiveCuiProtocol.RowWidget | LiveCuiProtocol.RowSlotsRead | LiveCuiProtocol.RowText);
        U(bytes, text + LiveCuiProtocol.RowTextHashOffset, 0xAABB);
        F(bytes, text + LiveCuiProtocol.RowVisibilityOffset, 0.5f); F(bytes, text + LiveCuiProtocol.RowXOffset, 10);
        F(bytes, text + LiveCuiProtocol.RowYOffset, 20); F(bytes, text + LiveCuiProtocol.RowWidthOffset, 30);
        F(bytes, text + LiveCuiProtocol.RowHeightOffset, 40);
        return bytes;
    }

    /** A text widget record with slots read and a short live text. */
    public static byte[] Detail(uint response, string text)
    {
        var bytes = new byte[LiveCuiProtocol.DetailBytes];
        U(bytes, LiveCuiProtocol.SequenceOffset, 2); U(bytes, LiveCuiProtocol.ResponseOffset, response);
        U(bytes, LiveCuiProtocol.DetailStatusOffset, (uint)LiveCuiStatus.Complete);
        U(bytes, LiveCuiProtocol.DetailFlagsOffset,
            LiveCuiProtocol.DetailText | LiveCuiProtocol.DetailSlotsRead | LiveCuiProtocol.DetailTextCopied);
        Q(bytes, LiveCuiProtocol.DetailTimeOffset, 1000); Q(bytes, LiveCuiProtocol.DetailTypeOffset, 0x1234567890);
        U(bytes, LiveCuiProtocol.DetailHierarchyOffset, ScreenTag); U(bytes, LiveCuiProtocol.DetailNameOffset, 0x101);
        H(bytes, LiveCuiProtocol.DetailNodeOffset, 11); H(bytes, LiveCuiProtocol.DetailParentOffset, LiveCuiProtocol.NoRow);
        U(bytes, LiveCuiProtocol.DetailStateOffset, LiveCuiProtocol.StateVisible);
        H(bytes, LiveCuiProtocol.DetailChildCountOffset, 2);
        F(bytes, LiveCuiProtocol.DetailSlotsOffset + 4, 10); F(bytes, LiveCuiProtocol.DetailSlotsOffset + 8, 20);
        F(bytes, LiveCuiProtocol.DetailSlotsOffset + 12, 30); F(bytes, LiveCuiProtocol.DetailSlotsOffset + 16, 40);
        F(bytes, LiveCuiProtocol.DetailSlotsOffset + 24, 0.5f);
        U(bytes, LiveCuiProtocol.DetailTextHashOffset, 0xAABB); U(bytes, LiveCuiProtocol.DetailStyleOffset, 0x51);
        bytes[LiveCuiProtocol.DetailTextSourceOffset] = 2; bytes[LiveCuiProtocol.DetailTextStateOffset] = 1;
        H(bytes, LiveCuiProtocol.DetailTextLengthOffset, (ushort)text.Length);
        U(bytes, LiveCuiProtocol.DetailTextModeOffset, 3);
        for (int i = 0; i < text.Length; i++) H(bytes, LiveCuiProtocol.DetailTextOffset + i * 2, text[i]);
        return bytes;
    }

    private static int Row(int index) => LiveCuiProtocol.TreeHeaderBytes + index * LiveCuiProtocol.WidgetRowBytes;
    private static void H(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
    private static void U(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void Q(byte[] bytes, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), value);
    private static void F(byte[] bytes, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);

    private static void Rejects(Action read, string what)
    {
        try { read(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException($"{what} was accepted.");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
