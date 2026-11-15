using RenzoBackend.Data;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Auth;
using Microsoft.EntityFrameworkCore;

namespace RenzoBackend.Services.Import;

/// <summary>
/// Service that scans renzo.json files during import and auto-creates users
/// from UserReadState data. Also tracks which users were auto-created for the
/// setup wizard's "Identify Yourself" step.
/// </summary>
public class UserImportService
{
    private readonly AppDbContext _db;
    private readonly OpdsPathGenerator _opdsPathGenerator;
    private readonly Settings.SettingsService _settings;
    private readonly ILogger<UserImportService> _logger;

    public UserImportService(AppDbContext db, OpdsPathGenerator opdsPathGenerator, Settings.SettingsService settings, ILogger<UserImportService> logger)
    {
        _db = db;
        _opdsPathGenerator = opdsPathGenerator;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Scans all import entries for UserReadStates in renzo.json snapshots
    /// and auto-creates any users that don't already exist in the database.
    /// Returns the list of auto-created usernames.
    /// </summary>
    public async Task<List<string>> AutoCreateUsersFromImportAsync(CancellationToken token = default)
    {
        var autoCreated = new List<string>();

        // Load all imports that have info with UserReadStates
        var imports = await _db.Imports
            .Where(i => i.Status == ImportStatus.Import || i.Status == ImportStatus.DoNotChange)
            .ToListAsync(token);

        // Collect all unique usernames from UserReadStates across all imports
        var usernamesToCreate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var import in imports)
        {
            var snapshot = import.Info;
            if (snapshot?.UserReadStates == null)
                continue;

            foreach (var userState in snapshot.UserReadStates)
            {
                if (!string.IsNullOrWhiteSpace(userState.Username))
                    usernamesToCreate.Add(userState.Username);
            }
        }

        // Create users that don't exist yet. Same rules as POST /api/users: the
        // name becomes a library folder, so it must be one plain folder name and
        // unique ignoring case (a renzo.json is library content, not trusted).
        List<string> taken = await _db.Users.AsNoTracking().Select(u => u.Username).ToListAsync(token);
        string storageRoot = (await _settings.GetSettingsAsync(token)).StorageFolder;
        foreach (string username in usernamesToCreate)
        {
            bool exists = taken.Any(n => string.Equals(n, username, StringComparison.OrdinalIgnoreCase));
            if (!exists && Users.UserCommandService.UsernameProblem(username) == null)
            {
                // Nor a twin of a folder already in the library (see POST /api/users).
                if (Users.UserCommandService.LibraryFolderTwinProblem(storageRoot, username) is { } twin)
                {
                    _logger.LogWarning("Import: not creating user {Username}: {Reason}", username, twin);
                    continue;
                }
                string opdsPath = await _opdsPathGenerator.GenerateUniquePathAsync();
                var user = new UserEntity
                {
                    Id = Guid.NewGuid(),
                    Username = username,
                    Level = UserLevel.User, // Auto-created users start as regular users
                    OpdsPath = opdsPath,
                    CreatedAt = DateTime.UtcNow,
                    IsActive = true
                };
                _db.Users.Add(user);
                await _db.SaveChangesAsync(token);
                autoCreated.Add(username);
                taken.Add(username);
            }
        }

        return autoCreated;
    }

    /// <summary>
    /// Gets whether the import process found any user read states that would
    /// trigger auto-creation. Used by the wizard to determine which step to show.
    /// </summary>
    public async Task<bool> HasImportUserReadStatesAsync(CancellationToken token = default)
    {
        var imports = await _db.Imports
            .Where(i => i.Status == ImportStatus.Import || i.Status == ImportStatus.DoNotChange)
            .ToListAsync(token);

        return imports.Any(i => i.Info?.UserReadStates?.Count > 0);
    }

    /// <summary>
    /// Gets the list of usernames that were auto-created during the most recent import.
    /// </summary>
    public async Task<List<string>> GetAutoCreatedUsernamesAsync(CancellationToken token = default)
    {
        // Users created during this session are tracked by the caller
        // This method is a placeholder; the auto-created list is returned by AutoCreateUsersFromImportAsync
        return [];
    }
}