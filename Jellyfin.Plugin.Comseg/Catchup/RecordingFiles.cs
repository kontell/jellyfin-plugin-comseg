using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Jellyfin.Plugin.Comseg.Catchup;

/// <summary>
/// Where a catchup download goes and the files beside it, laid out the way
/// Jellyfin's own recorder lays out a programme that is not a series
/// (RecordingsManager.GetRecordingPath with subfolders off):
/// <c>&lt;root&gt;/&lt;Name&gt;/&lt;Name&gt; yyyy_MM_dd_HH_mm_ss.ts</c>, a
/// <c>movie.nfo</c> and a per-file nfo, and a poster. The library then reads
/// it as it reads a recording.
/// </summary>
public static class RecordingFiles
{
    /// <summary>
    /// The programme's folder name, or null when the title cleans up to
    /// something that is not one folder inside the recording folder: nothing,
    /// only dots ("." and ".." would mean the recording folder or the one above
    /// it, and Windows drops trailing dots), or a path.
    /// </summary>
    /// <param name="title">The programme's title.</param>
    /// <param name="validName">The server's file-name cleaner (IFileSystem.GetValidFilename).</param>
    /// <returns>The folder name, or null.</returns>
    public static string? FolderName(string title, Func<string, string> validName)
    {
        var name = validName(title).Trim();
        return name.Trim('.').Trim().Length == 0 || name.IndexOfAny(['/', '\\']) >= 0 ? null : name;
    }

    /// <summary>The folder and the file (without extension) for a programme.</summary>
    /// <param name="root">The recording folder.</param>
    /// <param name="title">The programme's title.</param>
    /// <param name="startUtc">When the programme started.</param>
    /// <param name="validName">The server's file-name cleaner (IFileSystem.GetValidFilename).</param>
    /// <returns>The folder, and the file path without its extension.</returns>
    /// <exception cref="ArgumentException">The title makes no folder name (see <see cref="FolderName"/>).</exception>
    public static (string Folder, string FileBase) Paths(string root, string title, DateTime startUtc, Func<string, string> validName)
    {
        var name = FolderName(title, validName) ?? throw new ArgumentException("the title makes no usable folder name", nameof(title));
        var folder = Path.Combine(root, name);
        var stamp = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy_MM_dd_HH_mm_ss", CultureInfo.InvariantCulture);
        var file = validName(title + " " + stamp).Trim();
        return (folder, Path.Combine(folder, file));
    }

    /// <summary>The folder's movie.nfo, as Jellyfin writes it for a recording.</summary>
    /// <param name="title">The title.</param>
    /// <param name="plot">The description, or null.</param>
    /// <param name="startUtc">When the programme started.</param>
    /// <param name="posterPath">The poster, or null.</param>
    /// <returns>The file's bytes (UTF-8 with a byte order mark).</returns>
    public static byte[] MovieNfo(string title, string? plot, DateTime startUtc, string? posterPath)
    {
        var movie = new XElement(
            "movie",
            new XElement("plot", plot ?? string.Empty),
            new XElement("lockdata", "true"),
            new XElement("dateadded", Local(startUtc)),
            new XElement("title", title),
            new XElement("year", DateTime.SpecifyKind(startUtc, DateTimeKind.Utc).ToLocalTime().Year.ToString(CultureInfo.InvariantCulture)));
        if (posterPath is not null)
        {
            movie.Add(new XElement("art", new XElement("poster", posterPath)));
        }

        return Write(movie);
    }

    /// <summary>The nfo beside the video file, as Jellyfin writes it for a recording.</summary>
    /// <param name="title">The title.</param>
    /// <param name="plot">The description, or null.</param>
    /// <param name="startUtc">When the programme started.</param>
    /// <returns>The file's bytes (UTF-8 with a byte order mark).</returns>
    public static byte[] FileNfo(string title, string? plot, DateTime startUtc)
    {
        var movie = new XElement(
            "movie",
            new XElement("title", title),
            new XElement("dateadded", Local(startUtc)),
            new XElement("plot", plot ?? string.Empty),
            new XElement("lockdata", "true"));
        return Write(movie);
    }

    private static string Local(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static byte[] Write(XElement root)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(true),
            Indent = true,
            IndentChars = "  ",
        };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).Save(writer);
        }

        return stream.ToArray();
    }
}
