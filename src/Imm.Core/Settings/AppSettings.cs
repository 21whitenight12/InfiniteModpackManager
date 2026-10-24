using System.IO;
using System.Text.Json;
using Imm.Core.IO;
using Serilog;

namespace Imm.Core.Settings;

/// <summary>
/// App-wide settings (settings.json next to library.json). Per-pack facts stay in modpack.json —
/// this file holds only application-level defaults that have no single pack owner.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// Backup retention applied when a pack's manifest carries no own limit (0 = unlimited).
    /// A non-zero <c>backups.maxBackups</c> in the pack manifest always wins.
    /// </summary>
    public int MaxBackups { get; set; }

    /// <summary>UI language: "en" (default) or "ru".</summary>
    public string Language { get; set; } = "en";
}

/// <summary>Persistence for <see cref="AppSettings"/>; a broken file must never block the app.</summary>
public interface IAppSettingsService
{
    string FilePath { get; }

    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Synchronous load for startup (the language must be known before the first window).</summary>
    AppSettings LoadSync();
}

public sealed class AppSettingsService : IAppSettingsService
{
    public const string FileName = "settings.json";

    private readonly string _path;
    private readonly ILogger _logger;

    public AppSettingsService(ImmPaths paths, ILogger logger)
    {
        var directory = Path.GetDirectoryName(paths.LibraryFilePath);
        _path = Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, FileName);
        _logger = logger.ForContext<AppSettingsService>();
    }

    public string FilePath => _path;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(LongPath.Normalize(_path)))
        {
            return new AppSettings();
        }

        try
        {
            return await AtomicFile.ReadJsonAsync<AppSettings>(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Битые настройки не должны мешать запуску: работаем на дефолтах, файл не трогаем.
            _logger.Error(exception, "settings.json is unreadable; defaults used. File kept at {Path}", _path);
            return new AppSettings();
        }
    }

    public AppSettings LoadSync()
    {
        if (!File.Exists(LongPath.Normalize(_path)))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(LongPath.Normalize(_path));
            return Imm.Core.Serialization.ImmJson.Deserialize<AppSettings>(json);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.Error(exception, "settings.json is unreadable at startup; defaults used. File kept at {Path}", _path);
            return new AppSettings();
        }
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteJsonAsync(_path, settings, cancellationToken);
}
