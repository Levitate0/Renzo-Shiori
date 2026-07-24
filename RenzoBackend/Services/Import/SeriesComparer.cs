using RenzoBackend.Extensions;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Enums;

namespace RenzoBackend.Services.Import;

public class SeriesComparer
{
    public List<RenzoBackend.Models.Database.SeriesEntity> FindMatchingSeries(IEnumerable<RenzoBackend.Models.Database.SeriesEntity> allSeries, ImportSeriesSnapshot ImportSeriesSnapshot)
    {
        var result = new List<RenzoBackend.Models.Database.SeriesEntity>();
        if (ImportSeriesSnapshot == null || allSeries == null)
            return result;

        ImportSeriesResult seriesMetadata = ImportSeriesSnapshot.Series;

        // 1. Try to find a direct path match
        foreach (var series in allSeries)
        {
            if (!string.IsNullOrEmpty(series.StoragePath) &&
                !string.IsNullOrEmpty(ImportSeriesSnapshot.Path) &&
                string.Equals(series.StoragePath, ImportSeriesSnapshot.Path, StringComparison.InvariantCultureIgnoreCase))
            {
                result.Add(series);
                return result;
            }
        }

        // 2. If no path match, search for title similarity in all Sources
        foreach (var series in allSeries)
        {
            // Assume Series has a navigation property: ICollection<SeriesProvider> SeriesProviders
            if (series is null || series.Sources is null)
                continue;

            foreach (var provider in series.Sources)
            {
                if (!string.IsNullOrEmpty(provider.Title) &&
                    !string.IsNullOrEmpty(seriesMetadata.Title) &&
                    provider.Title.AreStringSimilar(seriesMetadata.Title, 0))
                {
                    result.Add(series);
                    break; // Only add each series once
                }
            }
        }

        return result;
    }


    public ArchiveCompare CompareArchives(ImportSeriesSnapshot ImportSeriesSnapshot, RenzoBackend.Models.Database.SeriesEntity series)
    {
        if (ImportSeriesSnapshot == null || series == null)
            return 0;

        var archiveList = ImportSeriesSnapshot.Series.Providers?
            .Where(p => p.Archives != null)
            .SelectMany(p => p.Archives) ?? Enumerable.Empty<ProviderArchiveSnapshot>();

        var existing = series.Sources?
            .Where(sp => sp.Chapters != null)
            .SelectMany(sp => sp.Chapters) ?? Enumerable.Empty<Chapter>();

        return ImportMetricsExtensions.CompareArchives(archiveList, existing);
    }
}

