using RenzoOAuthProxy.Services;

namespace RenzoOAuthProxy;

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddControllers();

        // Register services
        builder.Services.AddSingleton<TokenStoreService>();
        builder.Services.AddScoped<ProviderApiService>();
        builder.Services.AddHttpClient();

        var app = builder.Build();

        // Bundled in the Renzo container behind the backend on plain-http loopback —
        // no HTTPS redirect (the public TLS terminates at the user's reverse proxy).
        app.MapControllers();

        app.Run();
    }
}
