using System.Buffers.Binary;
using System.IO.Compression;

namespace RensaioBackend.Services.Reader;

public class TachibkManga
{
    public string Title = "";
    public string Url = "";
    public List<TachibkChapter> Chapters = [];
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
    public static List<TachibkManga> Parse(Stream tachibk)
    {
        using var gz = new GZipStream(tachibk, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        byte[] data = ms.ToArray();

        var result = new List<TachibkManga>();
        int i = 0;
        while (i < data.Length)
        {
            (int field, int wire, i) = ReadTag(data, i);
            if (field == 1 && wire == 2)
            {
                (int len, i) = ReadVarint32(data, i);
                result.Add(ParseManga(data, i, i + len));
                i += len;
            }
            else
            {
                i = Skip(data, i, wire);
            }
        }
        return result;
    }

    private static TachibkManga ParseManga(byte[] data, int start, int end)
    {
        var manga = new TachibkManga();
        int i = start;
        while (i < end)
        {
            (int field, int wire, i) = ReadTag(data, i);
            if (wire == 2)
            {
                (int len, i) = ReadVarint32(data, i);
                switch (field)
                {
                    case 2: manga.Url = System.Text.Encoding.UTF8.GetString(data, i, len); break;
                    case 3: manga.Title = System.Text.Encoding.UTF8.GetString(data, i, len); break;
                    case 16: manga.Chapters.Add(ParseChapter(data, i, i + len)); break;
                }
                i += len;
            }
            else
            {
                i = Skip(data, i, wire);
            }
        }
        return manga;
    }

    private static TachibkChapter ParseChapter(byte[] data, int start, int end)
    {
        var ch = new TachibkChapter();
        int i = start;
        while (i < end)
        {
            (int field, int wire, i) = ReadTag(data, i);
            switch (wire)
            {
                case 0:
                    (long v, i) = ReadVarint64(data, i);
                    if (field == 4) ch.Read = v != 0;
                    else if (field == 5) ch.Bookmark = v != 0;
                    else if (field == 6) ch.LastPageRead = (int)v;
                    break;
                case 5:
                    if (field == 9)
                    {
                        float f = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(i, 4));
                        if (!float.IsNaN(f) && !float.IsInfinity(f) && f >= 0)
                            ch.Number = (decimal)f;
                    }
                    i += 4;
                    break;
                case 2:
                    (int len, i) = ReadVarint32(data, i);
                    if (field == 2) ch.Name = System.Text.Encoding.UTF8.GetString(data, i, len);
                    i += len;
                    break;
                default:
                    i = Skip(data, i, wire);
                    break;
            }
        }
        return ch;
    }

    private static (int field, int wire, int next) ReadTag(byte[] data, int i)
    {
        (long tag, int next) = ReadVarint64(data, i);
        return ((int)(tag >> 3), (int)(tag & 7), next);
    }

    private static (int value, int next) ReadVarint32(byte[] data, int i)
    {
        (long v, int next) = ReadVarint64(data, i);
        return ((int)v, next);
    }

    private static (long value, int next) ReadVarint64(byte[] data, int i)
    {
        long result = 0;
        int shift = 0;
        while (true)
        {
            byte b = data[i++];
            result |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return (result, i);
            shift += 7;
            if (shift > 63)
                throw new InvalidDataException("Malformed varint in backup");
        }
    }

    private static int Skip(byte[] data, int i, int wire) => wire switch
    {
        0 => ReadVarint64(data, i).next,
        1 => i + 8,
        2 => SkipLengthDelimited(data, i),
        5 => i + 4,
        _ => throw new InvalidDataException($"Unsupported wire type {wire} in backup")
    };

    private static int SkipLengthDelimited(byte[] data, int i)
    {
        (int len, int next) = ReadVarint32(data, i);
        return next + len;
    }
}
