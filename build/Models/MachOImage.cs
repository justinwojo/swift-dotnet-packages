using System.Buffers.Binary;

namespace SwiftBindings.Build.Models;

/// <summary>
/// Reads the <c>LC_UUID</c> load command out of a Mach-O image — the native
/// analogue of a managed assembly's module version id.
///
/// <para><b>Why a UUID and not a file hash.</b> A framework does not reach an
/// <c>.app</c> byte-for-byte as it left the package: the iOS SDK thins the fat
/// slice down to the target architecture and then re-signs it, so the file's
/// bytes, its size and its signature blob all change. <c>LC_UUID</c> does not:
/// the linker stamps it from the linked image's content, and neither
/// <c>lipo -thin</c> nor <c>codesign</c> rewrites it. So the UUID set of an
/// embedded binary is a subset of the UUID set of the slice it came out of,
/// which is exactly the "is this the same build?" question a native-provenance
/// gate has to answer.</para>
///
/// <para>Implemented by parsing the headers rather than shelling out to
/// <c>dwarfdump</c>/<c>otool</c>: the parse is pure, so the harness self-test
/// can pin its behaviour on synthetic bytes instead of on whatever Xcode is
/// installed.</para>
///
/// <para>A file that carries no <c>LC_UUID</c> — a static archive, a text stub,
/// anything that is not a Mach-O — yields an empty list rather than throwing.
/// Callers treat "no UUID" as "not a linked dynamic image", which is the honest
/// reading and keeps a static framework from being demanded in an app bundle it
/// was never going to appear in.</para>
/// </summary>
public static class MachOImage
{
    // Fat (universal) headers are big-endian by definition; thin headers carry
    // their own byte order in the magic. Only the little-endian thin forms are
    // parsed: every architecture Apple still ships (arm64, arm64e, x86_64) is
    // little-endian, and a big-endian PowerPC slice simply contributes no UUID.
    const uint FatMagic32 = 0xCAFEBABEu;
    const uint FatMagic64 = 0xCAFEBABFu;
    const uint MachMagic64 = 0xFEEDFACFu;
    const uint MachMagic32 = 0xFEEDFACEu;

    const uint LcUuid = 0x1Bu;

    // A fat header claiming a preposterous arch count is a corrupt or
    // non-Mach-O file, not something to loop over.
    const uint MaxArchitectures = 64;
    const uint MaxLoadCommands = 4096;

    /// <summary>
    /// Every <c>LC_UUID</c> in the file at <paramref name="path"/>, in file
    /// order, de-duplicated. An unreadable or non-Mach-O file yields an empty
    /// list — the caller decides what that means for its gate.
    /// </summary>
    public static IReadOnlyList<string> ReadUuids(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return ReadUuids(stream);
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Every <c>LC_UUID</c> in a seekable stream positioned anywhere (the read
    /// starts from offset 0). Malformed input yields whatever was recovered
    /// before the malformation rather than an exception: this runs over vendor
    /// binaries the harness does not control, and a parse that throws would turn
    /// "we could not read this" into "the lane failed".
    /// </summary>
    public static IReadOnlyList<string> ReadUuids(Stream stream)
    {
        var uuids = new List<string>();
        try
        {
            ReadImage(stream, 0, uuids, allowFat: true);
        }
        catch (EndOfStreamException) { }
        catch (IOException) { }

        return uuids.Distinct(StringComparer.Ordinal).ToList();
    }

    static void ReadImage(Stream stream, long offset, List<string> uuids, bool allowFat)
    {
        if (offset < 0 || offset + 8 > stream.Length) return;

        var header = ReadExactly(stream, offset, 8);
        var magicBigEndian = BinaryPrimitives.ReadUInt32BigEndian(header);
        var magicLittleEndian = BinaryPrimitives.ReadUInt32LittleEndian(header);

        if (allowFat && (magicBigEndian == FatMagic32 || magicBigEndian == FatMagic64))
        {
            ReadFat(stream, offset, magicBigEndian == FatMagic64, uuids);
            return;
        }

        if (magicLittleEndian is MachMagic64 or MachMagic32)
            ReadThin(stream, offset, is64: magicLittleEndian == MachMagic64, uuids);
    }

    static void ReadFat(Stream stream, long offset, bool is64, List<string> uuids)
    {
        var head = ReadExactly(stream, offset, 8);
        var count = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
        if (count == 0 || count > MaxArchitectures) return;

        // Collect every slice offset before recursing: the recursion seeks, so
        // reading the table lazily would read from wherever the last slice left
        // the stream.
        var entrySize = is64 ? 32 : 20;
        var sliceOffsets = new List<long>((int)count);
        for (var i = 0; i < count; i++)
        {
            var entryStart = offset + 8 + ((long)i * entrySize);
            if (entryStart + entrySize > stream.Length) break;
            var entry = ReadExactly(stream, entryStart, entrySize);
            sliceOffsets.Add(is64
                ? (long)BinaryPrimitives.ReadUInt64BigEndian(entry.AsSpan(8))
                : BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(8)));
        }

        foreach (var sliceOffset in sliceOffsets)
            ReadImage(stream, sliceOffset, uuids, allowFat: false);
    }

    static void ReadThin(Stream stream, long offset, bool is64, List<string> uuids)
    {
        // mach_header{,_64}: magic, cputype, cpusubtype, filetype, ncmds,
        // sizeofcmds, flags [, reserved (64-bit only)].
        var headerSize = is64 ? 32 : 28;
        if (offset + headerSize > stream.Length) return;

        var header = ReadExactly(stream, offset, headerSize);
        var commandCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        if (commandCount == 0 || commandCount > MaxLoadCommands) return;

        var position = offset + headerSize;
        for (var i = 0; i < commandCount; i++)
        {
            if (position + 8 > stream.Length) return;
            var command = ReadExactly(stream, position, 8);
            var kind = BinaryPrimitives.ReadUInt32LittleEndian(command);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(4));
            if (size < 8) return;

            if (kind == LcUuid && size >= 24 && position + 24 <= stream.Length)
                uuids.Add(Format(ReadExactly(stream, position + 8, 16)));

            position += size;
        }
    }

    static byte[] ReadExactly(Stream stream, long offset, int count)
    {
        stream.Position = offset;
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
        return buffer;
    }

    /// <summary>Canonical uppercase 8-4-4-4-12 form, matching <c>dwarfdump --uuid</c>.</summary>
    static string Format(byte[] raw) =>
        string.Concat(
            Convert.ToHexString(raw, 0, 4), "-",
            Convert.ToHexString(raw, 4, 2), "-",
            Convert.ToHexString(raw, 6, 2), "-",
            Convert.ToHexString(raw, 8, 2), "-",
            Convert.ToHexString(raw, 10, 6));
}
