using System.IO.Compression;
using AryanEbookLibrary.Services.Metadata;
using SharpCompress.Archives.Rar;

namespace AryanEbookLibrary.Reader.Comic;

/// <summary>
/// A CBZ or CBR comic: its pages are the pictures in the archive in natural name order, the order the library takes
/// its cover from. A picture EPUB (<see cref="PictureEpub"/>) is one too, its pages in spine order. Neither archive
/// reader is thread-safe, so every page read takes the lock.
/// </summary>
internal sealed class ComicBook : IDisposable
{
    private readonly object _lock = new();
    private readonly IDisposable _archive;
    private readonly Func<int, Stream> _openPage;
    private bool _disposed;

    public int PageCount { get; }

    private ComicBook(IDisposable archive, int pageCount, Func<int, Stream> openPage)
    {
        _archive = archive;
        PageCount = pageCount;
        _openPage = openPage;
    }

    /// <summary>
    /// Opens the comic, or throws. Extensions lie (many ".cbr" files are zips), so the header decides. A solid RAR
    /// can only be read from the start, which would make every page turn slower than the last: refused.
    /// </summary>
    public static ComicBook Open(string path)
    {
        bool isZip;
        using (var head = File.OpenRead(path))
            isZip = head.ReadByte() == 0x50 && head.ReadByte() == 0x4B;

        if (isZip)
        {
            var zip = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read);
            var pages = PictureEpub.Pages(zip) ?? zip.Entries
                .Where(e => e.Length > 0 && !e.FullName.Contains("__MACOSX", StringComparison.Ordinal) && ImageSniffer.IsImageName(e.Name))
                .OrderBy(e => e.FullName, NaturalComparer.Instance)
                .ToList();
            return new ComicBook(zip, pages.Count, i => pages[i].Open());
        }

        var rar = RarArchive.Open(path);
        var rarPages = rar.Entries
            .Where(e => !e.IsDirectory && e.Key is not null && e.Size > 0 && ImageSniffer.IsImageName(e.Key))
            .OrderBy(e => e.Key ?? "", NaturalComparer.Instance)
            .ToList();
        if (rarPages.Any(e => e.IsSolid))
        {
            rar.Dispose();
            throw new NotSupportedException("a solid RAR archive");
        }
        return new ComicBook(rar, rarPages.Count, i => rarPages[i].OpenEntryStream());
    }

    /// <summary>The page's picture file, or null once the book is closed.</summary>
    public byte[]? ReadPage(int index)
    {
        lock (_lock)
        {
            if (_disposed || index < 0 || index >= PageCount) return null;
            using var stream = _openPage(index);
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _archive.Dispose();
        }
    }
}
