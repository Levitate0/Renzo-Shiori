namespace RenzoBackend.Models.Enums;

public enum JobType
{
    ScanLocalFiles,
    InstallAdditionalExtensions,
    SearchProviders,
    ImportSeries,
    GetChapters,
    GetLatest,
    Download,
    UpdateExtensions,
    UpdateAllSeries,
    DailyUpdate,
    StatusCheck,
    ScrobblerSync,
    VerifyAllSeries,
    /// <summary>Library-wide new-chapter scan: enqueues GetChapters for every active provider.</summary>
    LibraryScan
}