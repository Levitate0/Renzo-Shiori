using RenzoBackend.Extensions;
using RenzoBackend.Models.Enums;
using System.ComponentModel.DataAnnotations;

namespace RenzoBackend.Migration.Models
{
    public class Import
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
        public RenzoBackend.Models.Action Action { get; set; } = RenzoBackend.Models.Action.Add;
        public required RenzoBackend.Models.ImportSeriesSnapshot Info { get; set; }
        public List<ProviderSeriesDetails>? Series { get; set; }

        public decimal? ContinueAfterChapter { get; set; } = null;


    }
}
