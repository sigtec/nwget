using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Nwget;

/// <summary>
/// Binary .state file. Layout (little endian):
/// <code>
///  Offset  Size   Content
///   0      4      Magic "NWGS"
///   4      1      Format version (1)
///   5      4      Block size in bytes (Int32)
///   9      8      Last-Modified as UTC ticks (Int64, 0 = not present)
///  17      2      Length of the ETag in bytes (UInt16, 0 = not present)
///  19      n      ETag (UTF-8, including quotes or W/ prefix)
///  19+n    m      Bitmap, 1 bit per block: block i = bit (i % 8) of byte (i / 8); 1 = done
/// </code>
/// The file size is not stored – it is the length of the .part file.
/// A finished block changes exactly one byte of the bitmap (read-modify-write in memory,
/// writing that one byte, fsync).
/// </summary>
sealed class StateFile : IDisposable
{
    const byte FormatVersion = 1;
    const int FixedHeaderSize = 19;
    const long MaxFileSize = 64L * 1024 * 1024;
    static ReadOnlySpan<byte> Magic => "NWGS"u8;

    readonly FileStream _file;
    readonly SafeFileHandle _handle;
    readonly long _bitmapOffset;
    readonly byte[] _bitmap;
    readonly object _lock = new();

    public int BlockSize { get; }
    public int BlockCount { get; }
    public string? ETag { get; }
    public DateTimeOffset? LastModified { get; }

    StateFile(FileStream file, int blockSize, int blockCount, string? etag, DateTimeOffset? lastModified,
        long bitmapOffset, byte[] bitmap)
    {
        _file = file;
        _handle = file.SafeFileHandle;
        BlockSize = blockSize;
        BlockCount = blockCount;
        ETag = etag;
        LastModified = lastModified;
        _bitmapOffset = bitmapOffset;
        _bitmap = bitmap;
    }

    /// <summary>
    /// Creates a new state file with all blocks pending. Writes and flushes a temp file first,
    /// then renames it – so a half-written .state file never exists.
    /// </summary>
    public static StateFile Create(string path, int blockSize, int blockCount, string? etag, DateTimeOffset? lastModified)
    {
        byte[] etagBytes = Encoding.UTF8.GetBytes(etag ?? "");
        if (etagBytes.Length > ushort.MaxValue)
        {
            etag = null;
            etagBytes = [];
        }

        var header = new byte[FixedHeaderSize + etagBytes.Length];
        Magic.CopyTo(header);
        header[4] = FormatVersion;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5), blockSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(9), lastModified?.UtcTicks ?? 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(17), (ushort)etagBytes.Length);
        etagBytes.CopyTo(header.AsSpan(FixedHeaderSize));

        var bitmap = new byte[(blockCount + 7) / 8];

        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(header);
            fs.Write(bitmap);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);

        return new StateFile(OpenFile(path), blockSize, blockCount, etag, lastModified, header.Length, bitmap);
    }

    /// <summary>
    /// Opens an existing state file. Returns null if it is unreadable or its bitmap does not
    /// match the given file size (= length of the .part file).
    /// </summary>
    public static StateFile? TryOpen(string path, long totalSize)
    {
        FileStream? fs = null;
        try
        {
            fs = OpenFile(path);
            if (fs.Length < FixedHeaderSize || fs.Length > MaxFileSize)
                throw new InvalidDataException();

            var data = new byte[fs.Length];
            fs.ReadExactly(data);

            if (!data.AsSpan(0, 4).SequenceEqual(Magic) || data[4] != FormatVersion)
                throw new InvalidDataException();

            int blockSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(5));
            long ticks = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(9));
            int etagLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(17));
            if (blockSize <= 0 || data.Length < FixedHeaderSize + etagLength)
                throw new InvalidDataException();

            string? etag = etagLength == 0 ? null : Encoding.UTF8.GetString(data, FixedHeaderSize, etagLength);
            DateTimeOffset? lastModified = ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);

            int blockCount = checked((int)((totalSize + blockSize - 1) / blockSize));
            int bitmapOffset = FixedHeaderSize + etagLength;
            if (data.Length - bitmapOffset != (blockCount + 7) / 8)
                throw new InvalidDataException();

            byte[] bitmap = data[bitmapOffset..];
            return new StateFile(fs, blockSize, blockCount, etag, lastModified, bitmapOffset, bitmap);
        }
        catch (Exception)
        {
            fs?.Dispose();
            return null;
        }
    }

    public bool IsDone(int index)
    {
        lock (_lock)
            return (_bitmap[index >> 3] & (1 << (index & 7))) != 0;
    }

    public int DoneCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < BlockCount; i++)
                if (IsDone(i))
                    count++;
            return count;
        }
    }

    public bool AllDone => DoneCount == BlockCount;

    /// <summary>Sets a block's bit, writes exactly that one byte and flushes the file.</summary>
    public void MarkDone(int index)
    {
        lock (_lock)
        {
            int byteIndex = index >> 3;
            _bitmap[byteIndex] |= (byte)(1 << (index & 7));
            RandomAccess.Write(_handle, _bitmap.AsSpan(byteIndex, 1), _bitmapOffset + byteIndex);
            _file.Flush(flushToDisk: true);
        }
    }

    public void Dispose() => _file.Dispose();

    static FileStream OpenFile(string path) =>
        new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0);
}
