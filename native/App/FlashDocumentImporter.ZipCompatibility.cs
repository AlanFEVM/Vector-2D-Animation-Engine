using System.Buffers.Binary;

namespace VectorAnimationEngine;

internal static partial class FlashDocumentImporter
{
    // Some Animate FLA writers omit the stored mimetype entry from the directory,
    // but still count its 46 + 8 directory bytes in the EOCD size. Repair only that
    // field in a read-only view, after proving the exact orphan and physical layout.
    internal static Stream OpenCompatibleZipStream(string path)
    {
        var stream = File.OpenRead(path);
        try
        {
            var tailLength = (int)Math.Min(stream.Length, 65_557);
            var tail = new byte[tailLength];
            stream.Position = stream.Length - tailLength;
            stream.ReadExactly(tail);
            for (var index = tail.Length - 22; index >= 0; index--)
            {
                if (ZipUInt32(tail, index) != 0x06054b50
                    || index + 22 + ZipUInt16(tail, index + 20) != tail.Length) continue;
                var end = stream.Length - tailLength + index;
                var directoryOffset = ZipUInt32(tail, index + 16);
                var directorySize = ZipUInt32(tail, index + 12);
                var actualSize = end - directoryOffset;
                if (actualSize >= 0 && directorySize == actualSize + 54
                    && ZipUInt16(tail, index + 4) == 0 && ZipUInt16(tail, index + 6) == 0
                    && ZipUInt16(tail, index + 8) == ZipUInt16(tail, index + 10)
                    && ZipUInt16(tail, index + 10) is > 0 and <= 20_000
                    && directoryOffset != uint.MaxValue && directorySize != uint.MaxValue
                    && ValidateAnimateZipDirectory(stream, directoryOffset, end, ZipUInt16(tail, index + 10)))
                {
                    stream.Position = 0;
                    return new ZipSizeOverlayStream(stream, end + 12, checked((uint)actualSize));
                }
                if (actualSize >= 0 && directorySize == actualSize + 54)
                    throw new InvalidDataException("The Flash ZIP has an inconsistent directory size and does not match the validated Animate mimetype layout.");
                break;
            }
            // All other layouts retain their original bytes for ZipArchive's strict parser.
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static bool ValidateAnimateZipDirectory(Stream stream, long directoryOffset, long directoryEnd, int count)
    {
        var ranges = new List<(long Start, long End)>(count);
        var central = new byte[46];
        var local = new byte[30];
        var position = directoryOffset;
        var listedMimeCount = 0;
        for (var entry = 0; entry < count; entry++)
        {
            if (position < 0 || position > directoryEnd - central.Length) return false;
            stream.Position = position;
            stream.ReadExactly(central);
            var flags = ZipUInt16(central, 8);
            var method = ZipUInt16(central, 10);
            var compressed = ZipUInt32(central, 20);
            var expanded = ZipUInt32(central, 24);
            var nameLength = ZipUInt16(central, 28);
            var extraLength = ZipUInt16(central, 30);
            var commentLength = ZipUInt16(central, 32);
            var localOffset = ZipUInt32(central, 42);
            var next = position + central.Length + nameLength + extraLength + commentLength;
            if (ZipUInt32(central, 0) != 0x02014b50 || ZipUInt16(central, 6) > 20
                || (flags & ~0x0806) != 0 || method is not (0 or 8)
                || compressed == uint.MaxValue || expanded == uint.MaxValue || localOffset == uint.MaxValue
                || ZipUInt16(central, 34) != 0 || nameLength == 0 || next > directoryEnd) return false;
            var name = new byte[nameLength];
            stream.ReadExactly(name);
            var isMime = name.AsSpan().SequenceEqual("mimetype"u8);
            if (isMime && ++listedMimeCount > 1) return false;
            var extra = new byte[extraLength];
            stream.ReadExactly(extra);
            if (!ValidateClassicZipExtra(extra)) return false;
            if (localOffset > directoryOffset - local.Length) return false;
            stream.Position = localOffset;
            stream.ReadExactly(local);
            var localNameLength = ZipUInt16(local, 26);
            var localExtraLength = ZipUInt16(local, 28);
            var payloadEnd = (long)localOffset + local.Length + localNameLength + localExtraLength + compressed;
            if (ZipUInt32(local, 0) != 0x04034b50 || ZipUInt16(local, 4) != ZipUInt16(central, 6)
                || ZipUInt16(local, 6) != flags || ZipUInt16(local, 8) != method
                || ZipUInt32(local, 14) != ZipUInt32(central, 16)
                || ZipUInt32(local, 18) != compressed || ZipUInt32(local, 22) != expanded
                || localNameLength != nameLength || payloadEnd > directoryOffset) return false;
            var localName = new byte[localNameLength];
            stream.ReadExactly(localName);
            if (!name.AsSpan().SequenceEqual(localName)) return false;
            var localExtra = new byte[localExtraLength];
            stream.ReadExactly(localExtra);
            if (!ValidateClassicZipExtra(localExtra)) return false;
            // Real Animate archives may retain one listed copy in addition to the orphan.
            if (isMime && !ValidateAnimateMimeOrphan(stream, localOffset, payloadEnd)) return false;
            ranges.Add((localOffset, payloadEnd));
            position = next;
        }
        if (position != directoryEnd) return false;
        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        long previousEnd = 0;
        var orphanCount = 0;
        foreach (var range in ranges)
        {
            if (range.Start != previousEnd
                && (++orphanCount != 1 || !ValidateAnimateMimeOrphan(stream, previousEnd, range.Start))) return false;
            previousEnd = range.End;
        }
        if (previousEnd != directoryOffset
            && (++orphanCount != 1 || !ValidateAnimateMimeOrphan(stream, previousEnd, directoryOffset))) return false;
        return orphanCount == 1;
    }

    private static bool ValidateAnimateMimeOrphan(Stream stream, long start, long end)
    {
        if (end - start != 63) return false;
        Span<byte> bytes = stackalloc byte[63];
        stream.Position = start;
        stream.ReadExactly(bytes);
        return ZipUInt32(bytes, 0) == 0x04034b50 && ZipUInt16(bytes, 4) == 20
            && ZipUInt16(bytes, 6) == 0x0800 && ZipUInt16(bytes, 8) == 0
            && ZipUInt32(bytes, 14) == 0x86e192bb && ZipUInt32(bytes, 18) == 25
            && ZipUInt32(bytes, 22) == 25 && ZipUInt16(bytes, 26) == 8
            && ZipUInt16(bytes, 28) == 0 && bytes[30..38].SequenceEqual("mimetype"u8)
            && bytes[38..].SequenceEqual("application/vnd.adobe.xfl"u8);
    }

    private static bool ValidateClassicZipExtra(ReadOnlySpan<byte> extra)
    {
        var position = 0;
        while (position < extra.Length)
        {
            if (position > extra.Length - 4) return false;
            var kind = ZipUInt16(extra, position);
            var length = ZipUInt16(extra, position + 2);
            if (kind is 0x0001 or 0x9901 || position + 4 + length > extra.Length) return false;
            position += 4 + length;
        }
        return true;
    }

    private static ushort ZipUInt16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));

    private static uint ZipUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));

    private sealed class ZipSizeOverlayStream : Stream
    {
        private readonly Stream _source;
        private readonly long _offset;
        private readonly byte[] _size = new byte[4];

        internal ZipSizeOverlayStream(Stream source, long offset, uint size)
        {
            _source = source;
            _offset = offset;
            BinaryPrimitives.WriteUInt32LittleEndian(_size, size);
        }

        public override bool CanRead => _source.CanRead;
        public override bool CanSeek => _source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _source.Length;
        public override long Position { get => _source.Position; set => _source.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var start = _source.Position;
            var read = _source.Read(buffer);
            for (var position = Math.Max(start, _offset); position < Math.Min(start + read, _offset + 4); position++)
                buffer[(int)(position - start)] = _size[(int)(position - _offset)];
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => _source.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _source.Dispose();
            base.Dispose(disposing);
        }
    }
}
