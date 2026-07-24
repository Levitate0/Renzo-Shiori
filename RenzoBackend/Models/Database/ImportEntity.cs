using System.ComponentModel.DataAnnotations;
using RenzoBackend.Extensions;
using RenzoBackend.Models.Enums;

namespace RenzoBackend.Models.Database
{
    public class ImportEntity
    {
        private string _path = string.Empty;


        [Key]
        public required string Path
        {
            get => _path.SanitizeDirectory();
            set => _path = value;
        }
        public required string Title { get; set; }
        public ImportStatus Status { get; set; } = ImportStatus.Import;
        public Action Action { get; set; } = Action.Add;
        public required ImportSeriesSnapshot Info { get; set; }
        public List<ProviderSeriesDetails>? Series { get; set; }

        public decimal? ContinueAfterChapter { get; set; } = null;

        // Scans ImportFolder instead of StorageFolder; commit path computes a fresh storage
        // path from title/type instead of reusing Path, since there's nothing to reuse on disk.
        public bool IsTitleOnly { get; set; } = false;

    }
}

