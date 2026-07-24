using RenzoBackend.Models.Database;

namespace RenzoBackend.Services.Images
{
    public interface IImageProvider
    {
        bool CanProcess(string url);
        public Task<Stream?> ObtainStreamAsync(EtagCacheEntity entry, CancellationToken token);
    }
}
