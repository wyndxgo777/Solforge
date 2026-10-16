using System.Buffers.Binary;
using System.Globalization;

namespace SunriseStudio.Formats;

/** Socket editing. */
public sealed partial class InvestmentCatalog
{
    /** Ordinary socket blocks hold 80-byte rows and at most 12 instance lanes. */
    private const uint SocketBlockClass = 0x808077C1, SocketRowClass = 0x808077C4;
    /** Embedded plug choices carry an item index and an expression array in 32 bytes. */
    private const uint SocketMemberClass = 0x80802E03, SocketExpressionClass = 0x80807D31;
    private const int SocketStride = 80, SocketMemberStride = 32, SocketMemberDescriptor = 64;

    /** Only embedded choices provide a template that stays inside this item. */
    public IReadOnlyList<ushort> SocketPlugTemplates(uint tag, ReadOnlyMemory<byte> current,
        InvestmentSocket socket)
    {
        var (reader, row) = ReadSocketForEdit(tag, current, socket);
        var members = ReadSocketMembers(reader, row);
        return Enumerable.Range(0, members.Count)
            .Select(index => checked((ushort)reader.U32(members.Data + index * SocketMemberStride)))
            .Distinct().ToArray();
    }

    /**
     * A private copy leaves previous socket members and their conditions unchanged.
     * @param preserveTemplateCondition False gives only the new member an empty condition.
     */
    public byte[] AddSocketPlug(uint tag, ReadOnlyMemory<byte> current, InvestmentSocket socket,
        ushort plugIndex, ushort templatePlug, bool preserveTemplateCondition = true)
    {
        RequireSocketPlugIndex(plugIndex);
        RequireSocketPlugIndex(templatePlug);
        var (reader, row) = ReadSocketForEdit(tag, current, socket);
        var members = ReadSocketMembers(reader, row);
        if (members.Count == 0)
            throw new InvalidDataException("This socket has no embedded plug choice to copy.");
        int[] templates = Enumerable.Range(0, members.Count)
            .Select(index => members.Data + index * SocketMemberStride)
            .Where(offset => reader.U32(offset) == templatePlug).ToArray();
        if (templates.Length != 1)
            throw new InvalidDataException("Choose one unique template from this socket's embedded plugs.");
        int existing = Enumerable.Range(0, members.Count).Select(index => members.Data + index * SocketMemberStride)
            .FirstOrDefault(offset => reader.U32(offset) == plugIndex, -1);
        if (existing >= 0)
        {
            if (!preserveTemplateCondition && reader.U64(existing + 8) != 0)
                throw new InvalidDataException("The existing plug has a condition. Choose a new plug identity.");
            return current.ToArray();
        }
        if (members.Count >= MaximumAuthoredRows)
            throw new InvalidDataException("This socket has reached the editor's plug limit.");

        int count = checked(members.Count + 1), header = checked((current.Length + 19) & ~15);
        byte[] output = new byte[checked(header + 16 + count * SocketMemberStride)];
        current.Span.CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(header - 4), 0x80809FBD);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(header), count);
        current.Span.Slice(members.Data - 8, 8).CopyTo(output.AsSpan(header + 8));
        for (int index = 0; index < count; index++)
        {
            int from = index < members.Count ? members.Data + index * SocketMemberStride : templates[0];
            int to = header + 16 + index * SocketMemberStride;
            current.Span.Slice(from, SocketMemberStride).CopyTo(output.AsSpan(to));
            int expression = reader.Relative(from + 16);
            if (expression >= 0)
                BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(to + 16), expression - to - 16L);
            if (index == members.Count)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(to), plugIndex);
                if (!preserveTemplateCondition) output.AsSpan(to + 8, 16).Clear();
            }
        }
        int field = row + SocketMemberDescriptor;
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(field), count);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(field + 8), header - field - 8L);
        BinaryPrimitives.WriteInt64LittleEndian(output, output.Length);
        return output;
    }

    /** The initial plug must already belong to the socket's declared choices. */
    public byte[] SetSocketInitialPlug(uint tag, ReadOnlyMemory<byte> current, InvestmentSocket socket,
        ushort plugIndex)
    {
        RequireSocketPlugIndex(plugIndex);
        ReadSocketForEdit(tag, current, socket);
        if (!socket.AllowedPlugs.Contains(plugIndex))
            throw new InvalidDataException("Add this plug to the socket before making it the initial plug.");
        return Edit(tag, current, socket.PlugField, plugIndex.ToString(CultureInfo.InvariantCulture));
    }

    /** A private socket choice replaces inherited members without changing another socket's pool. */
    public byte[] KeepSocketPlug(uint tag, ReadOnlyMemory<byte> current, InvestmentSocket socket,
        ushort plugIndex)
    {
        RequireSocketPlugIndex(plugIndex);
        var (reader, row) = ReadSocketForEdit(tag, current, socket);
        var members = ReadSocketMembers(reader, row);
        int[] matches = Enumerable.Range(0, members.Count)
            .Select(index => members.Data + index * SocketMemberStride)
            .Where(offset => reader.U32(offset) == plugIndex).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("The retained plug must have one embedded member in this socket.");
        if (members.Count == 1 && reader.U16(row + 12) == ushort.MaxValue
            && reader.U16(row + 32) == ushort.MaxValue && socket.InitialPlug == plugIndex)
            return current.ToArray();
        int header = checked((current.Length + 19) & ~15);
        byte[] output = new byte[checked(header + 16 + SocketMemberStride)];
        current.Span.CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(header - 4), 0x80809FBD);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(header), 1);
        current.Span.Slice(members.Data - 8, 8).CopyTo(output.AsSpan(header + 8));
        int target = header + 16, source = matches[0];
        current.Span.Slice(source, SocketMemberStride).CopyTo(output.AsSpan(target));
        int expression = reader.Relative(source + 16);
        if (expression >= 0)
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(target + 16), expression - target - 16L);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(row + 2), plugIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(row + 12), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(row + 32), ushort.MaxValue);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(row + SocketMemberDescriptor), 1);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(row + SocketMemberDescriptor + 8),
            header - row - SocketMemberDescriptor - 8L);
        BinaryPrimitives.WriteInt64LittleEndian(output, output.Length);
        return output;
    }

    /** The caller's socket must still describe the current native row. */
    private (TagReader Reader, int Row) ReadSocketForEdit(uint tag, ReadOnlyMemory<byte> current,
        InvestmentSocket socket)
    {
        if (FindItem(tag) is not { } item || item.Tag != tag)
            throw new InvalidDataException("Open an item definition to change its sockets.");
        RequireClass(catalog, tag, ItemClass);
        var reader = new TagReader(current);
        if (!reader.Has(0, 188) || reader.U64(0) != (ulong)current.Length)
            throw new InvalidDataException("Item size header is invalid.");
        int block = reader.Relative(104);
        if (block < 4 || !reader.Has(block, 32) || reader.U32(block - 4) != SocketBlockClass)
            throw new InvalidDataException("Item socket block has an unsupported layout.");
        var rows = reader.Array(block, SocketRowClass, SocketStride);
        if (rows.Count > 12 || socket.Index < 0 || socket.Index >= rows.Count)
            throw new InvalidDataException("Socket is outside the item's 12 ordinary lanes.");
        int row = rows.Data + socket.Index * SocketStride;
        var detail = ReadItem(tag, current);
        var actual = detail.Sockets.FirstOrDefault(value => value.Index == socket.Index);
        if (actual is null || actual.Type == ushort.MaxValue || actual.TypeField != socket.TypeField
            || actual.PlugField != socket.PlugField || actual.TypeField.Offset != row
            || actual.PlugField.Offset != row + 2 || actual.Type != socket.Type
            || actual.InitialPlug != socket.InitialPlug || !actual.AllowedPlugs.SequenceEqual(socket.AllowedPlugs))
            throw new InvalidDataException("This socket changed or is empty. Reopen its editor.");
        if (detail.Diagnostics.Any(message => message.StartsWith("Socket", StringComparison.Ordinal)))
            throw new InvalidDataException("The item's socket lists are malformed.");
        return (reader, row);
    }

    /** Moved members keep their expressions at the original byte offsets. */
    private (int Count, int Data, uint Class) ReadSocketMembers(TagReader reader, int row)
    {
        int field = row + SocketMemberDescriptor;
        var members = reader.Array(field, SocketMemberClass, SocketMemberStride);
        if (members.Count > MaximumAuthoredRows)
            throw new InvalidDataException("This socket has too many embedded plugs for the editor.");
        if (members.Count == 0 && reader.I64(field + 8) != 0)
            throw new InvalidDataException("An empty socket plug array has an unsupported pointer.");
        for (int index = 0; index < members.Count; index++)
        {
            int member = members.Data + index * SocketMemberStride;
            uint plug = reader.U32(member);
            if (plug >= InvestmentItemClone.MaximumIndexRows || plug >= Items.Count)
                throw new InvalidDataException("A socket choice names an unreachable item index.");
            var expression = reader.Array(member + 8, SocketExpressionClass, 8);
            if (expression.Count > MaximumAuthoredRows
                || expression.Count == 0 && reader.I64(member + 16) != 0)
                throw new InvalidDataException("A socket choice has an unsupported condition array.");
        }
        return members;
    }

    private void RequireSocketPlugIndex(ushort index)
    {
        if (index >= InvestmentItemClone.MaximumIndexRows || index >= Items.Count)
            throw new InvalidDataException("Choose a reachable item from this project's catalog.");
        RequireClass(catalog, Items[index].Tag, ItemClass);
    }
}
