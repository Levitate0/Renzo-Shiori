using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using RenzoBackend.Services.Settings;

namespace RenzoBackend.Services.Images.Providers
{
    public class StorageImageProvider : IImageProvider
    {
        private readonly SettingsService _settingsService;
        private readonly AppDbContext _db;
        public StorageImageProvider(AppDbContext db, SettingsService settingsService)
        {
            _settingsService = settingsService;
            _db = db;
        }
        public bool CanProcess(string url)
        {
            if (string.IsNullOrEmpty(url))
                return false;
            if (url.StartsWith("storage://"))
                return true;
            return false;
        }
        public async Task<Stream?> ObtainStreamAsync(EtagCacheEntity cache, CancellationToken token)
        {
            string? storagePath = _settingsService.DirectSettings?.StorageFolder;
            if (string.IsNullOrEmpty(storagePath))
                return null;
            string path = cache.Url.Substring(10);
            // storage:// names a file in the library and nothing else: a stored
            // "storage://../../renzo.db" (or an absolute path) must not be served.
            string? finalPath = RenzoBackend.Extensions.PathSafety.ContainedPath(storagePath, path);
            if (finalPath != null && File.Exists(finalPath))
            {
                Stream stream = File.OpenRead(finalPath);
                if (string.IsNullOrEmpty(cache.Etag))
                {
                    cache.Etag = await UrlImageProvider.ComputeMd5HashFromStreamAsync(stream);
                    stream.Position = 0;
                    await _db.SaveChangesAsync();
                }
                return stream;
            }
            return null;
        }
    }
}
