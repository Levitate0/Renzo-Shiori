namespace RenzoBackend.Extensions
{
    /// <summary>
    /// Containment checks for every place a stored or client-supplied name is
    /// joined onto the library folder.
    ///
    /// 2026-11-15 audit: Path.Combine happily takes "../../renzo.db" or an
    /// absolute "/app/appsettings.json" as its last part, so an OPDS chapter
    /// download served any file on the host, and a chapter filename or storage
    /// path posted with POST /api/serie later turned "delete downloads" into
    /// "delete anywhere". Validate names on the way in, and still check the
    /// resolved path at every read/write/delete (defence in depth).
    /// </summary>
    public static class PathSafety
    {
        // The library lives on Linux (case-sensitive); a Windows dev run is not.
        private static readonly StringComparison PathComparison =
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>
        /// True when <paramref name="candidate"/> resolves to a path strictly
        /// INSIDE <paramref name="root"/> (the root itself does not count).
        /// Lexical only: ".." and absolute parts are resolved, symlinks are not.
        /// </summary>
        public static bool IsInside(string? root, string? candidate)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(candidate))
                return false;
            if (root.Contains('\0') || candidate.Contains('\0'))
                return false;
            string rootFull = Path.GetFullPath(root);
            string prefix = Path.EndsInDirectorySeparator(rootFull) ? rootFull : rootFull + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(candidate);
            return full.StartsWith(prefix, PathComparison) && full.Length > prefix.Length;
        }

        /// <summary>
        /// <paramref name="root"/> joined with <paramref name="parts"/> as a full
        /// path, or null when any part is missing or the result escapes the root.
        /// </summary>
        public static string? ContainedPath(string? root, params string?[] parts)
        {
            if (string.IsNullOrEmpty(root) || parts.Any(string.IsNullOrEmpty))
                return null;
            string combined;
            try
            {
                combined = Path.GetFullPath(Path.Combine([root, .. parts!]));
            }
            catch (ArgumentException)
            {
                return null;
            }
            return IsInside(root, combined) ? combined : null;
        }

        /// <summary>
        /// A single file name: no directory separators, not "." or "..", not
        /// rooted. Chapter archives always sit directly in their series folder
        /// (the scanner and the downloader both only ever produce bare names).
        /// </summary>
        public static bool IsSafeFileName(string? name) =>
            !string.IsNullOrWhiteSpace(name)
            && name != "." && name != ".."
            && name.IndexOfAny(['/', '\\', '\0']) < 0
            && !Path.IsPathRooted(name);

        /// <summary>
        /// A relative path under the library: not rooted, no ".." segment, no
        /// drive or NUL. Empty is allowed (callers decide what empty means).
        /// </summary>
        public static bool IsSafeRelativePath(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return true;
            if (path.Contains('\0') || Path.IsPathRooted(path) || path.StartsWith('/') || path.StartsWith('\\'))
                return false;
            if (path.Length >= 2 && path[1] == ':')
                return false;
            return !path.Split('/', '\\').Any(s => s == "..");
        }
    }
}
