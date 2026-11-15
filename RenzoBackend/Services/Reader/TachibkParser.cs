using System.Buffers.Binary;
using System.IO.Compression;

namespace RenzoBackend.Services.Reader;

public class TachibkManga
{
    public string Title = "";
    public string Url = "";
    public List<TachibkChapter> Chapters = [];
}

/// <summary>The backup inflates past <see cref="TachibkParser.MaxDecompressedBytes"/>, or holds more entries than the caps.</summary>
public class BackupTooLargeException : IOException
{
    public BackupTooLargeException(int limitBytes) : base($"Backup expands past {limitBytes / (1024 * 1024)} MB") { }
    public BackupTooLargeException(string message) : base(message) { }
}

public class TachibkChapter
{
    public decimal Number = -1;
    public string Name = "";
    public bool Read;
    public bool Bookmark;
    public int LastPageRead;
}

/// <summary>
/// Minimal protobuf wire-format reader for Tachiyomi/Suwayomi .tachibk backups
/// (gzip-compressed Backup message). Reads only the fields needed for read-state
/// sync; everything else is skipped by wire type. Field numbers verified against
/// a real Suwayomi 2.x backup:
///   Backup.backupManga = 1 (repeated message)
///   BackupManga: 2=url, 3=title, 16=chapters (repeated message)
///   BackupChapter: 2=name, 4=read (varint), 5=bookmark (varint),
///                  6=lastPageRead (varint), 9=chapterNumber (float32)
/// </summary>
public static class TachibkParser
{
    /// <summary>
    /// Ceiling on the DECOMPRESSED backup. The upload limit only bounds the
    /// gzip, and gzip expands ~1000:1 on repetitive input, so a 200 MB upload
    /// (any signed-in user may send one) could inflate past the container's
    /// memory. A real backup is read-state and URLs — a few MB even for a large
    /// library — so 64 MB is generous.
    /// </summary>
    public const int MaxDecompressedBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Entry caps. 64 MB of 10-byte chapter messages is 6.5M chapters (~650 MB of
    /// objects) and an O(n²) merge after it, so the byte cap alone isn't a bound.
    /// Only chapters carrying read state are kept, one per number per series.
    /// </summary>
    public const int MaxManga = 20_000;
    public const int MaxChaptersPerManga = 10_000;
    public const int MaxChaptersTotal = 500_000;

    /// <summary>
    /// Longest title kept. Titles feed a Levenshtein match against every series
    /// in the library (O(n*m) per pair); no real title is this long, so an entry
    /// with a longer one is dropped rather than matched.
    /// </summary>
    public const int MaxTitleLength = 512;

    public static List<TachibkManga> Parse(Stream tachibk, int maxDecompressedBytes = MaxDecompressedBytes)
    {
        using var gz = new GZipStream(tachibk, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        // Streamed in chunks and stopped at the cap, instead of CopyTo-ing the
        // whole inflated payload into memory before looking at its size.
        byte[] buffer = new byte[81920];
        int read;
        while ((read = gz.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + read > maxDecompressedBytes)
                throw new BackupTooLargeException(maxDecompressedBytes);
            ms.Write(buffer, 0, read);
        }
        ReadOnlySpan<byte> data = ms.GetBuffer().AsSpan(0, (int)ms.Length);

        var result = new List<TachibkManga>();
        int totalChapters = 0;
        var r = new Reader(data);
        while (r.More)
        {
            (int field, int wire) = r.Tag();
            if (field == 1 && wire == 2)
            {
                if (result.Count >= MaxManga)
                    throw new BackupTooLargeException($"more than {MaxManga} series");
                TachibkManga manga = ParseManga(r.LengthDelimited(), ref totalChapters);
                if (manga.Title.Length <= MaxTitleLength)
                    result.Add(manga);
            }
            else
                r.Skip(wire);
        }
        return result;
    }

    private static TachibkManga ParseManga(ReadOnlySpan<byte> data, ref int totalChapters)
    {
        var manga = new TachibkManga();
        // One entry per chapter number: duplicates merge (read/bookmark OR-ed,
        // furthest page kept), so no number can be repeated to inflate the work.
        var byNumber = new Dictionary<decimal, TachibkChapter>();
        var r = new Reader(data);
        while (r.More)
        {
            (int field, int wire) = r.Tag();
            if (wire == 2)
            {
                ReadOnlySpan<byte> value = r.LengthDelimited();
                switch (field)
                {
                    case 2: manga.Url = System.Text.Encoding.UTF8.GetString(value); break;
                    case 3: manga.Title = System.Text.Encoding.UTF8.GetString(value); break;
                    case 16:
                        TachibkChapter ch = ParseChapter(value);
                        if (ch.Number < 0 || (!ch.Read && !ch.Bookmark && ch.LastPageRead <= 0))
                            break;   // nothing to import
                        if (byNumber.TryGetValue(ch.Number, out TachibkChapter? seen))
                        {
                            seen.Read |= ch.Read;
                            seen.Bookmark |= ch.Bookmark;
                            seen.LastPageRead = Math.Max(seen.LastPageRead, ch.LastPageRead);
                            break;
                        }
                        if (byNumber.Count >= MaxChaptersPerManga)
                            throw new BackupTooLargeException($"more than {MaxChaptersPerManga} chapters with read state in one series");
                        if (++totalChapters > MaxChaptersTotal)
                            throw new BackupTooLargeException($"more than {MaxChaptersTotal} chapters with read state");
                        byNumber[ch.Number] = ch;
                        manga.Chapters.Add(ch);
                        break;
                }
            }
            else
            {
                r.Skip(wire);
            }
        }
        return manga;
    }

    private static TachibkChapter ParseChapter(ReadOnlySpan<byte> data)
    {
        var ch = new TachibkChapter();
        var r = new Reader(data);
        while (r.More)
        {
            (int field, int wire) = r.Tag();
            switch (wire)
            {
                case 0:
                    long v = r.Varint();
                    if (field == 4) ch.Read = v != 0;
                    else if (field == 5) ch.Bookmark = v != 0;
                    else if (field == 6) ch.LastPageRead = (int)Math.Clamp(v, 0, int.MaxValue);
                    break;
                case 5:
                    ReadOnlySpan<byte> raw = r.Fixed(4);
                    if (field == 9)
                    {
                        float f = BinaryPrimitives.ReadSingleLittleEndian(raw);
                        if (!float.IsNaN(f) && !float.IsInfinity(f) && f >= 0)
                            ch.Number = (decimal)f;
                    }
                    break;
                case 2:
                    ReadOnlySpan<byte> value = r.LengthDelimited();
                    if (field == 2) ch.Name = System.Text.Encoding.UTF8.GetString(value);
                    break;
                default:
                    r.Skip(wire);
                    break;
            }
        }
        return ch;
    }

    /// <summary>
    /// Forward-only protobuf cursor. Every read is bounds-checked against the
    /// span it was given, and a length can never be negative or run past the
    /// end — a crafted length used to be able to step the index BACKWARDS and
    /// loop the parser forever on a few bytes of input.
    /// </summary>
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public Reader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
        }

        public readonly bool More => _pos < _data.Length;

        public (int field, int wire) Tag()
        {
            long tag = Varint();
            return ((int)(tag >> 3), (int)(tag & 7));
        }

        public long Varint()
        {
            long result = 0;
            int shift = 0;
            while (true)
            {
                if (_pos >= _data.Length)
                    throw new InvalidDataException("Truncated varint in backup");
                byte b = _data[_pos++];
                result |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return result;
                shift += 7;
                if (shift > 63)
                    throw new InvalidDataException("Malformed varint in backup");
            }
        }

        public ReadOnlySpan<byte> Fixed(int size)
        {
            if (size > _data.Length - _pos)
                throw new InvalidDataException("Truncated field in backup");
            ReadOnlySpan<byte> value = _data.Slice(_pos, size);
            _pos += size;
            return value;
        }

        public ReadOnlySpan<byte> LengthDelimited()
        {
            long len = Varint();
            if (len < 0 || len > _data.Length - _pos)
                throw new InvalidDataException("Field length runs past the end of the backup");
            return Fixed((int)len);
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: Fixed(8); break;
                case 2: LengthDelimited(); break;
                case 5: Fixed(4); break;
                default: throw new InvalidDataException($"Unsupported wire type {wire} in backup");
            }
        }
    }
}
