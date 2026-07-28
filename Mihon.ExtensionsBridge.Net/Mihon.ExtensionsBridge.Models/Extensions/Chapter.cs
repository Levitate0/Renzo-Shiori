namespace Mihon.ExtensionsBridge.Models.Extensions
{
    public class Chapter
    {

        public string Url { get; set; }

        public string Name { get; set; }

        public DateTimeOffset DateUpload { get; set; }

        public float ChapterNumber { get; set; }

        public string? Scanlator { get; set; }

        /// <summary>extensions-lib 1.6 memo blob (raw JSON), needed by getPageList for some sources.</summary>
        public string? Memo { get; set; }

    }
    public class ParsedChapter : Chapter
    {
        public int Index { get; set; }
        public string ParsedName { get; set; }
        public string RealUrl { get; set; }
        public decimal ParsedNumber { get; set; }

    }

}
