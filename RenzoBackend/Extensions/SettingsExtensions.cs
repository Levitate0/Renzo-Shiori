using RenzoBackend.Models.Dto;

namespace RenzoBackend.Extensions
{
    public static class SettingsExtensions
    {

        public static string? ResolveSeriesAbsolutePath(this SettingsDto series, string? seriesPath)
        {
            if (string.IsNullOrEmpty(series.StorageFolder) || string.IsNullOrEmpty(seriesPath))
                return null;
            // A StoragePath that climbs out of (or is) the library root is never a
            // series folder: callers read, write renzo.json and move folders here.
            return PathSafety.ContainedPath(series.StorageFolder, seriesPath);
        }

        /// <summary>
        /// The full path of a chapter archive in a series folder, or null when the
        /// filename is not a bare name or the result would leave the series folder.
        /// Use this at every place that opens, moves or deletes a chapter file.
        /// </summary>
        public static string? ResolveChapterFilePath(this SettingsDto series, string? seriesPath, string? chapterFilename)
        {
            if (!PathSafety.IsSafeFileName(chapterFilename))
                return null;
            string? seriesAbsolutePath = ResolveSeriesAbsolutePath(series, seriesPath);
            return seriesAbsolutePath == null ? null : PathSafety.ContainedPath(seriesAbsolutePath, chapterFilename);
        }
        public static string? ResolveChapterPath(this SettingsDto series, string? seriesPath, string? chapterFilename)
        {
            // Bare names only, resolved inside the series folder: the OPDS download
            // used to hand this an attacker's "../../renzo.db" or absolute path.
            string? exactPath = ResolveChapterFilePath(series, seriesPath, chapterFilename);
            if (exactPath == null)
                return null;
            string seriesAbsolutePath = Path.GetDirectoryName(exactPath)!;
            if (!Directory.Exists(seriesAbsolutePath))
                return null;
            // Try exact match
            if (File.Exists(exactPath))
                return exactPath;

            // Try with common extensions
            foreach (string ext in new[] { ".cbz", ".zip", ".rar", ".cbr", ".7z", ".cb7" })
            {
                string extPath = System.IO.Path.Combine(seriesAbsolutePath,
                    System.IO.Path.GetFileNameWithoutExtension(chapterFilename) + ext);
                if (File.Exists(extPath))
                    return extPath;
            }

            // Scan directory for matching files (an empty stem would match anything)
            string stem = System.IO.Path.GetFileNameWithoutExtension(chapterFilename)!;
            if (string.IsNullOrWhiteSpace(stem))
                return null;
            foreach (string file in Directory.GetFiles(seriesAbsolutePath))
            {
                string fileName = System.IO.Path.GetFileName(file);
                if (fileName.Contains(stem,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }

            return null;
        }


    }
}
