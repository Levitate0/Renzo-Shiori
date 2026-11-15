using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using Mihon.ExtensionsBridge.Models.Abstractions;

namespace RenzoBackend.Services.Images.Providers
{
    public class ExtensionsImageProvider : IImageProvider
    {
        private readonly IWorkingFolderStructure _workingFolderStructure;
        private readonly AppDbContext _db;
        public ExtensionsImageProvider(AppDbContext db, IWorkingFolderStructure workingFolderStructure)
        {
            _workingFolderStructure = workingFolderStructure;
            _db = db;
        }

        public bool CanProcess(string url)
        {
            if (string.IsNullOrEmpty(url))
                return false;
            if (url.StartsWith("ext://"))
                return true;
            return false;
        }

        public async Task<Stream?> ObtainStreamAsync(EtagCacheEntity cache, CancellationToken token)
        {
            string originalFilename = cache.Url.Substring(6);
            // ext:// names an extension icon and nothing else; "ext://../../renzo.db"
            // must not be served (same rule as storage://).
            string? finalPath = RenzoBackend.Extensions.PathSafety.ContainedPath(_workingFolderStructure.ExtensionsFolder, originalFilename);
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
