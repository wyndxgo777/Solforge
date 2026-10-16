using System.Buffers.Binary;

namespace SunriseStudio.Live;

/** Whether the game has answered a screen request, and how (absent screen vs. capture unavailable). */
public enum LiveCuiStatus : uint { Pending, Complete, Absent, Unavailable }

/** One open screen: its hierarchy tag and the count of earlier open screens with that tag. */
public readonly record struct LiveCuiTarget(uint HierarchyTag, byte Ordinal);
/** One widget of one open screen, named by its node id in the hierarchy. */
public readonly record struct LiveCuiWidgetTarget(uint HierarchyTag, byte Ordinal, ushort NodeId);

/** One open screen as the game's screen list holds it. */
public sealed record LiveCuiScreen(uint HierarchyTag, uint ScreenTag, uint ViewHash, uint Page,
    uint ScreenFlags, uint StateFlags, int Priority, byte Layer, byte Ordinal)
{
    /** Flag 0x10; only a sort key in the code read, its meaning is unverified. */
    public bool SortLast => (ScreenFlags & LiveCuiProtocol.ScreenSortLast) != 0;
    /** The 0x201 visibility bit; whether it keeps the screen out of the draw is unverified. */
    public bool Visible => (StateFlags & LiveCuiProtocol.StateVisible) != 0;
    public LiveCuiTarget Target => new(HierarchyTag, Ordinal);
}

/** Open screens, layer 0 then layer 1, each in draw order. Focus values are row indexes of each layer's top active screen. */
public sealed record LiveCuiScreens(ulong Time, IReadOnlyList<LiveCuiScreen> Rows, int? Focus0, int? Focus1,
    bool Truncated, bool WalkFaulted)
{
    /** The top active screen of layer 1, else of layer 0. */
    public LiveCuiScreen? Top => Focus1 is { } top ? Rows[top] : Focus0 is { } bottom ? Rows[bottom] : null;
}

/** One widget of the watched screen. Visible is the widget's own bit; a parent may still hide it. */
public sealed record LiveCuiWidget(int Row, ulong TypeKey, uint HierarchyTag, uint NameHash, ushort NodeId,
    int? ParentRow, uint StateFlags, uint RowFlags, uint TextHash)
{
    /** Slot values; zero unless SlotsRead. Visibility is slot 6, the 0x201 value. */
    public float Visibility { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
    /**
     * The 0x201 visibility bit; whether it keeps the widget out of the draw is unverified.
     * A row that is not a widget has no state word, so it never hides its children.
     */
    public bool Visible => !IsWidget || (StateFlags & LiveCuiProtocol.StateVisible) != 0;
    /** False for a CUI object whose type does not derive from the widget root type. */
    public bool IsWidget => (RowFlags & LiveCuiProtocol.RowWidget) != 0;
    public bool IsText => (RowFlags & LiveCuiProtocol.RowText) != 0;
    public bool SlotsRead => (RowFlags & LiveCuiProtocol.RowSlotsRead) != 0;
}

/** The widget tree of one watched screen; Rows are depth-first, a parent before its children. */
public sealed record LiveCuiTree(uint Request, LiveCuiStatus Status, ulong Time, uint Revision,
    uint HierarchyTag, uint ScreenTag, uint ViewHash, uint Total, bool Truncated, IReadOnlyList<LiveCuiWidget> Rows)
{
    public bool WalkFaulted { get; init; }
}

/** The game's one answer to one widget request; Pending until the game answers. */
public sealed record LiveCuiDetail(LiveCuiWidgetTarget Target, LiveCuiStatus Status, ulong Time)
{
    public ulong TypeKey { get; init; }
    public uint HierarchyTag { get; init; }
    public uint NameHash { get; init; }
    public ushort NodeId { get; init; }
    /** Null for the screen root. */
    public ushort? ParentNode { get; init; }
    public uint StateFlags { get; init; }
    public uint SlotFlags { get; init; }
    public ushort ObjectFlags { get; init; }
    public ushort ChildCount { get; init; }
    /** Null when the game did not read the slots. */
    public IReadOnlyList<float>? Slots { get; init; }
    public bool IsText { get; init; }
    public uint TextHash { get; init; }
    public uint StyleKey { get; init; }
    public byte TextSource { get; init; }
    public byte TextState { get; init; }
    /** The byte at +512 passed to line layout; its meaning is unverified. */
    public uint TextMode { get; init; }
    /** Null when the widget has no live text the game could copy. */
    public string? Text { get; init; }
    public bool TextTruncated { get; init; }
    /** The 0x201 visibility bit; whether it keeps the widget out of the draw is unverified. */
    public bool Visible => (StateFlags & LiveCuiProtocol.StateVisible) != 0;
}

/** Mirrors the game's CUI mapping; every offset here matches the native protocol header. */
public static class LiveCuiProtocol
{
    public const uint Magic = 0x55435353, Version = 1;
    public const int ScreenCapacity = 32, WidgetCapacity = 8192, SlotCount = 10, TextCapacity = 256;
    public const ushort NoRow = 0xFFFF;
    public const uint NoFocus = 0xFFFFFFFF;
    public const ulong AliveAfterMilliseconds = 2000;

    public const int ControlBytes = 128, ScreenRowBytes = 32, WidgetRowBytes = 56;
    public const int ScreensOffset = 128, ScreensBytes = 1152, DetailOffset = 1280, DetailBytes = 640;
    public const int TreeOffset = 1920, TreeHeaderBytes = 64;
    public const int TreeBytes = TreeHeaderBytes + WidgetCapacity * WidgetRowBytes;
    public const int MappingBytes = TreeOffset + TreeBytes;

    public const int MagicOffset = 0, VersionOffset = 4, BytesOffset = 8, EditorPidOffset = 12;
    public const int HeartbeatOffset = 16, ClientVersionOffset = 24, BridgeFlagsOffset = 28, GamePidOffset = 32;
    public const int CaptureMicrosOffset = 36, CheckedTimeOffset = 40;
    public const int WatchRequestOffset = 48, WatchHierarchyOffset = 52, WatchOrdinalOffset = 56;
    public const int WidgetRequestOffset = 60, WidgetHierarchyOffset = 64, WidgetOrdinalOffset = 68, WidgetNodeOffset = 72;

    public const int SequenceOffset = 0, ResponseOffset = 4;
    public const int ScreensCountOffset = 4, ScreensTimeOffset = 8, ScreensFlagsOffset = 16;
    public const int Focus0Offset = 20, Focus1Offset = 24, ScreenRowsOffset = 32;
    public const int ScreenHierarchyOffset = 0, ScreenTagOffset = 4, ScreenViewOffset = 8, ScreenPageOffset = 12;
    public const int ScreenFlagsOffset = 16, ScreenStateOffset = 20, ScreenPriorityOffset = 24;
    public const int ScreenLayerOffset = 28, ScreenOrdinalOffset = 29;
    public const int DetailStatusOffset = 8, DetailFlagsOffset = 12, DetailTimeOffset = 16, DetailTypeOffset = 24;
    public const int DetailHierarchyOffset = 32, DetailNameOffset = 36, DetailNodeOffset = 40, DetailParentOffset = 42;
    public const int DetailStateOffset = 44, DetailSlotFlagsOffset = 48, DetailObjectFlagsOffset = 52;
    public const int DetailChildCountOffset = 54, DetailSlotsOffset = 56, DetailTextHashOffset = 96;
    public const int DetailStyleOffset = 100, DetailTextSourceOffset = 104, DetailTextStateOffset = 105;
    public const int DetailTextLengthOffset = 106, DetailTextModeOffset = 108, DetailTextOffset = 112;
    public const int TreeTimeOffset = 8, TreeStatusOffset = 16, TreeCountOffset = 20, TreeFlagsOffset = 24;
    public const int TreeRevisionOffset = 28, TreeHierarchyOffset = 32, TreeScreenTagOffset = 36;
    public const int TreeViewOffset = 40, TreeTotalOffset = 44;
    public const int RowTypeOffset = 0, RowHierarchyOffset = 8, RowNameOffset = 12, RowNodeOffset = 16;
    public const int RowParentOffset = 18, RowStateOffset = 20, RowFlagsOffset = 24, RowTextHashOffset = 28;
    public const int RowVisibilityOffset = 32, RowXOffset = 36, RowYOffset = 40, RowWidthOffset = 44, RowHeightOffset = 48;

    public const uint ManagerReady = 1, WalkTruncated = 1, WalkFaulted = 2;
    /** StateVisible is the bit property 0x201 sets. */
    public const uint ScreenSortLast = 1u << 4, StateVisible = 1u << 1;
    /** DetailTextCopied: text holds textLength units from either source; TextSource tells which. */
    public const uint DetailText = 1, DetailSlotsRead = 2, DetailTextCopied = 4, DetailTextTruncated = 8;
    /** RowWidget: StateFlags is valid; a row without it is a CUI object that is not a widget. */
    public const uint RowSlotsRead = 1, RowText = 2, RowWidget = 4;

    /** Per-process shared-memory name for the UI channel. */
    public static string MappingName(int processId) => $@"Local\SunriseStudio.Cui.{processId}";

    /** True while the game's last capture is recent; checkedTime moves even when nothing changed. */
    public static bool Alive(ulong checkedTime, ulong now) =>
        checkedTime != 0 && checkedTime <= now && now - checkedTime <= AliveAfterMilliseconds;

    /** The row count of a tree header, capped so a torn header cannot overrun the copy. */
    public static int TreeCount(ReadOnlySpan<byte> header) => (int)Math.Min(U32(header, TreeCountOffset), (uint)WidgetCapacity);

    /** Reads the screens section. */
    public static LiveCuiScreens ReadScreens(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ScreensBytes) throw new InvalidDataException("The CUI screen list is incomplete.");
        CheckSequence(bytes);
        uint count = U32(bytes, ScreensCountOffset);
        if (count > ScreenCapacity) throw new InvalidDataException("The CUI screen list has too many rows.");
        var rows = new LiveCuiScreen[count];
        for (int i = 0; i < rows.Length; i++)
        {
            var row = bytes.Slice(ScreenRowsOffset + i * ScreenRowBytes, ScreenRowBytes);
            rows[i] = new(U32(row, ScreenHierarchyOffset), U32(row, ScreenTagOffset), U32(row, ScreenViewOffset),
                U32(row, ScreenPageOffset), U32(row, ScreenFlagsOffset), U32(row, ScreenStateOffset),
                I32(row, ScreenPriorityOffset), row[ScreenLayerOffset], row[ScreenOrdinalOffset]);
        }
        uint flags = U32(bytes, ScreensFlagsOffset);
        return new(U64(bytes, ScreensTimeOffset), rows, Focus(bytes, Focus0Offset, count), Focus(bytes, Focus1Offset, count),
            (flags & WalkTruncated) != 0, (flags & WalkFaulted) != 0);
    }

    /**
     * Reads the tree section.
     * @param bytes The header and at least `count` rows.
     */
    public static LiveCuiTree ReadTree(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < TreeHeaderBytes) throw new InvalidDataException("The CUI tree is incomplete.");
        CheckSequence(bytes);
        var status = Status(bytes, TreeStatusOffset);
        uint count = U32(bytes, TreeCountOffset), flags = U32(bytes, TreeFlagsOffset);
        if (count > WidgetCapacity || bytes.Length < TreeHeaderBytes + (long)count * WidgetRowBytes)
            throw new InvalidDataException("The CUI tree row count is invalid.");
        var rows = new LiveCuiWidget[status == LiveCuiStatus.Complete ? count : 0];
        for (int i = 0; i < rows.Length; i++) rows[i] = ReadWidget(bytes.Slice(TreeHeaderBytes + i * WidgetRowBytes, WidgetRowBytes), i);
        return new(U32(bytes, ResponseOffset), status, U64(bytes, TreeTimeOffset), U32(bytes, TreeRevisionOffset),
            U32(bytes, TreeHierarchyOffset), U32(bytes, TreeScreenTagOffset), U32(bytes, TreeViewOffset),
            U32(bytes, TreeTotalOffset), (flags & WalkTruncated) != 0, rows) { WalkFaulted = (flags & LiveCuiProtocol.WalkFaulted) != 0 };
    }

    /**
     * Reads the detail section.
     * @return Null when it answers another request id.
     */
    public static LiveCuiDetail? ReadDetail(ReadOnlySpan<byte> bytes, uint request, LiveCuiWidgetTarget target)
    {
        if (bytes.Length < DetailBytes) throw new InvalidDataException("The CUI widget record is incomplete.");
        CheckSequence(bytes);
        if (U32(bytes, ResponseOffset) != request) return null;
        var status = Status(bytes, DetailStatusOffset);
        var result = new LiveCuiDetail(target, status, U64(bytes, DetailTimeOffset));
        if (status != LiveCuiStatus.Complete) return result;
        uint flags = U32(bytes, DetailFlagsOffset);
        int length = U16(bytes, DetailTextLengthOffset);
        if (length > TextCapacity) throw new InvalidDataException("The CUI widget text is too long.");
        float[]? slots = null;
        if ((flags & DetailSlotsRead) != 0)
        {
            slots = new float[SlotCount];
            for (int i = 0; i < SlotCount; i++) slots[i] = Float(bytes, DetailSlotsOffset + i * 4);
        }
        ushort parent = U16(bytes, DetailParentOffset);
        string? text = null;
        if ((flags & DetailTextCopied) != 0)
        {
            var units = new char[length];
            for (int i = 0; i < length; i++) units[i] = (char)U16(bytes, DetailTextOffset + i * 2);
            text = new string(units);
        }
        return result with
        {
            TypeKey = U64(bytes, DetailTypeOffset), HierarchyTag = U32(bytes, DetailHierarchyOffset),
            NameHash = U32(bytes, DetailNameOffset), NodeId = U16(bytes, DetailNodeOffset),
            ParentNode = parent == NoRow ? null : parent, StateFlags = U32(bytes, DetailStateOffset),
            SlotFlags = U32(bytes, DetailSlotFlagsOffset), ObjectFlags = U16(bytes, DetailObjectFlagsOffset),
            ChildCount = U16(bytes, DetailChildCountOffset), Slots = slots, IsText = (flags & DetailText) != 0,
            TextHash = U32(bytes, DetailTextHashOffset), StyleKey = U32(bytes, DetailStyleOffset),
            TextSource = bytes[DetailTextSourceOffset], TextState = bytes[DetailTextStateOffset],
            TextMode = U32(bytes, DetailTextModeOffset), Text = text,
            TextTruncated = (flags & DetailTextTruncated) != 0
        };
    }

    /** One tree row; its parent row must come earlier. */
    private static LiveCuiWidget ReadWidget(ReadOnlySpan<byte> row, int index)
    {
        ushort parent = U16(row, RowParentOffset);
        if (index != 0 && parent >= index) throw new InvalidDataException("A CUI tree row names a later parent.");
        uint flags = U32(row, RowFlagsOffset);
        bool slots = (flags & RowSlotsRead) != 0;
        return new(index, U64(row, RowTypeOffset), U32(row, RowHierarchyOffset), U32(row, RowNameOffset),
            U16(row, RowNodeOffset), index == 0 ? null : parent, U32(row, RowStateOffset), flags, U32(row, RowTextHashOffset))
        {
            Visibility = slots ? Float(row, RowVisibilityOffset) : 0, X = slots ? Float(row, RowXOffset) : 0,
            Y = slots ? Float(row, RowYOffset) : 0, Width = slots ? Float(row, RowWidthOffset) : 0,
            Height = slots ? Float(row, RowHeightOffset) : 0
        };
    }

    private static void CheckSequence(ReadOnlySpan<byte> bytes)
    {
        if ((U32(bytes, SequenceOffset) & 1) != 0) throw new InvalidDataException("The CUI section was copied mid-write.");
    }

    /** A published answer is never Pending. */
    private static LiveCuiStatus Status(ReadOnlySpan<byte> bytes, int offset)
    {
        var status = (LiveCuiStatus)U32(bytes, offset);
        if (status is < LiveCuiStatus.Complete or > LiveCuiStatus.Unavailable)
            throw new InvalidDataException("The CUI answer has an unknown status.");
        return status;
    }

    private static int? Focus(ReadOnlySpan<byte> bytes, int offset, uint count)
    {
        uint row = U32(bytes, offset);
        if (row == NoFocus) return null;
        if (row >= count) throw new InvalidDataException("The CUI focus names a missing screen.");
        return (int)row;
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static int I32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    private static float Float(ReadOnlySpan<byte> bytes, int offset)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
        if (!float.IsFinite(value)) throw new InvalidDataException("A CUI slot value is not finite.");
        return value;
    }
}
