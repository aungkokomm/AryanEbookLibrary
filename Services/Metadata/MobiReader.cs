using System.Text;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services.Metadata;

/// <summary>
/// Reads Kindle-format metadata (.mobi / .azw / .azw3 / .prc) from the PalmDB + MOBI + EXTH headers.
/// Only the headers and the cover record are read, never the whole book.
/// </summary>
public static class MobiReader
{
    private const uint NoIndex = 0xFFFFFFFF;

    public static BookMetadata Read(string path)
    {
        var md = new BookMetadata();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < 86) return md;

        var head = ReadAt(fs, 0, 78);
        int numRecords = (head[76] << 8) | head[77];
        if (numRecords < 1) return md;

        var table = ReadAt(fs, 78, numRecords * 8);
        if (table.Length < numRecords * 8) return md;

        long Off(int i) => i < numRecords ? BE32(table, i * 8) : fs.Length;

        var r0 = Off(0);
        var r0Len = (int)Math.Min(Math.Max(Off(1) - r0, 0), 256 * 1024);
        if (r0Len < 132) return md;

        var rec = ReadAt(fs, r0, r0Len);
        if (rec.Length < 132 || Encoding.ASCII.GetString(rec, 16, 4) != "MOBI") return md;

        var headerLen = (int)BE32(rec, 20);
        var enc = BE32(rec, 28) == 65001 ? Encoding.UTF8 : Encoding.Latin1;

        // Full name (fallback title)
        var nameOff = (int)BE32(rec, 84);
        var nameLen = (int)BE32(rec, 88);
        if (nameOff > 0 && nameLen > 0 && nameOff + nameLen <= rec.Length)
            md.Title = XmlUtil.Clean(enc.GetString(rec, nameOff, nameLen));

        var firstImage = rec.Length >= 112 ? BE32(rec, 108) : NoIndex;
        var exthFlags = BE32(rec, 128);
        var exthPos = 16 + headerLen;

        long coverIdx = -1, thumbIdx = -1;

        if ((exthFlags & 0x40) != 0 && exthPos + 12 <= rec.Length &&
            Encoding.ASCII.GetString(rec, exthPos, 4) == "EXTH")
        {
            var count = (int)BE32(rec, exthPos + 8);
            var p = exthPos + 12;
            var authors = new List<string>();
            var subjects = new List<string>();
            string? updatedTitle = null;

            for (var i = 0; i < count && p + 8 <= rec.Length; i++)
            {
                var type = (int)BE32(rec, p);
                var len = (int)BE32(rec, p + 4);
                if (len < 8 || p + len > rec.Length) break;

                var dataOff = p + 8;
                var dataLen = len - 8;
                string Text() => enc.GetString(rec, dataOff, dataLen);

                switch (type)
                {
                    case 100: authors.Add(Text()); break;
                    case 101: md.Publisher = XmlUtil.Clean(Text()); break;
                    case 103: md.Description = XmlUtil.Clean(XmlUtil.StripHtml(Text())); break;
                    case 104: md.Isbn = XmlUtil.Clean(Text()); break;
                    case 105: subjects.Add(Text()); break;
                    case 106: md.Year = XmlUtil.ParseYear(Text()); break;
                    case 503: updatedTitle = XmlUtil.Clean(Text()); break;
                    case 524: md.Language = XmlUtil.Clean(Text()); break;
                    case 201 when dataLen == 4: coverIdx = BE32(rec, dataOff); break;
                    case 202 when dataLen == 4: thumbIdx = BE32(rec, dataOff); break;
                }
                p += len;
            }

            var cleanAuthors = authors.Select(XmlUtil.Clean).Where(a => a is not null).Distinct().ToList();
            if (cleanAuthors.Count > 0)
                md.Author = string.Join(", ", cleanAuthors.Select(FlipLastFirst));

            var cleanSubjects = subjects.Select(XmlUtil.Clean).Where(a => a is not null).Distinct().ToList();
            if (cleanSubjects.Count > 0) md.Subjects = string.Join(", ", cleanSubjects);

            if (updatedTitle is not null) md.Title = updatedTitle;
        }

        // Cover image record = first image record + EXTH offset
        if (firstImage != NoIndex)
        {
            foreach (var idx in new[] { coverIdx, thumbIdx })
            {
                if (idx < 0) continue;
                var recIndex = firstImage + idx;
                if (recIndex >= numRecords) continue;

                var start = Off((int)recIndex);
                var end = Off((int)recIndex + 1);
                var size = end - start;
                if (size < 100 || size > 20 * 1024 * 1024) continue;

                var bytes = ReadAt(fs, start, (int)size);
                var ext = ImageSniffer.Ext(bytes);
                if (ext is null) continue;

                md.Cover = bytes;
                md.CoverExt = ext;
                break;
            }
        }

        return md;
    }

    /// <summary>Kindle often stores "Last, First". Show "First Last" to match everything else.</summary>
    private static string FlipLastFirst(string? name)
    {
        if (name is null) return "";
        var parts = name.Split(',', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0 && !parts[1].Contains(' ')
            ? $"{parts[1]} {parts[0]}"
            : name;
    }

    private static byte[] ReadAt(FileStream fs, long offset, int count)
    {
        if (offset < 0 || offset >= fs.Length) return Array.Empty<byte>();
        count = (int)Math.Min(count, fs.Length - offset);
        var buf = new byte[count];
        fs.Seek(offset, SeekOrigin.Begin);
        var read = 0;
        while (read < count)
        {
            var n = fs.Read(buf, read, count - read);
            if (n <= 0) break;
            read += n;
        }
        return read == count ? buf : buf[..read];
    }

    private static uint BE32(byte[] b, int i) =>
        i + 4 > b.Length ? 0 : (uint)((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);
}
