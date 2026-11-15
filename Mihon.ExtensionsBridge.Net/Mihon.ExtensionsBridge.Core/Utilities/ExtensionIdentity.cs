using System.Text.RegularExpressions;

namespace Mihon.ExtensionsBridge.Core.Utilities
{
    /// <summary>
    /// The package and version an extension declares become folder and file names
    /// (ExtensionsFolder/{name}/{version}_{repo}/…), so for an uploaded APK they are
    /// attacker-chosen path segments. Anything that is not a plain Java package id /
    /// plain version string is refused before it reaches Path.Combine.
    /// </summary>
    public static class ExtensionIdentity
    {
        // Dot-separated Java identifiers: no "..", no separators, no drive letters,
        // no leading dot, so it can never climb out of or re-root the extensions folder.
        private static readonly Regex PackageId = new(
            @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Every installed keiyoushi/custom version is "1.4.12"-shaped; no separators.
        private static readonly Regex VersionName = new(
            @"^[A-Za-z0-9._+-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool IsValidPackageName(string? package) =>
            !string.IsNullOrEmpty(package) && package.Length <= 255 && PackageId.IsMatch(package);

        public static bool IsValidVersionName(string? version) =>
            !string.IsNullOrEmpty(version) && version.Length <= 64 && version != "." && version != ".."
            && VersionName.IsMatch(version);

        /// <summary>True when <paramref name="path"/> resolves strictly inside <paramref name="root"/>.</summary>
        public static bool IsUnder(string root, string path)
        {
            string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(r, StringComparison.Ordinal);
        }
    }
}
