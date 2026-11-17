using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TrackBoard.Common;

namespace TrackBoard.Storage;

/// <summary>A signed, single-use upload target in the backup bucket.</summary>
public record SignedBackupUpload(string UploadUrl, string Path);

/// <summary>What the bucket knows about an object that exists.</summary>
/// <param name="SizeBytes">The stored size, when the storage API reported one.</param>
public record BackupObjectInfo(long? SizeBytes);

/// <summary>
/// The private bucket holding the app's session archives. Shares the <c>Supabase</c> settings
/// with photo storage, so configuring one configures both; only the bucket differs.
/// </summary>
public interface IBackupStorage
{
    bool IsConfigured { get; }

    /// <summary>Signs an upload for exactly <paramref name="path"/>. The client PUTs the bytes itself.</summary>
    Task<SignedBackupUpload> CreateSignedUploadAsync(string path, CancellationToken ct);

    /// <summary>The object's info, or null when nothing is stored at <paramref name="path"/>.</summary>
    Task<BackupObjectInfo?> GetInfoAsync(string path, CancellationToken ct);

    /// <summary>A URL that downloads the object until <paramref name="lifetime"/> has passed.</summary>
    Task<string> CreateSignedDownloadAsync(string path, TimeSpan lifetime, CancellationToken ct);

    /// <summary>Best effort, like photo deletion: failures are logged, never thrown.</summary>
    Task DeleteAsync(IEnumerable<string?> paths, CancellationToken ct);

    /// <summary>
    /// Best effort. Deletes every object under <paramref name="folder"/>, including uploads
    /// that were signed and sent but never committed, which no row points at.
    /// </summary>
    Task DeleteFolderAsync(string folder, CancellationToken ct);
}

/// <summary>Supabase Storage over its REST API, against the private backup bucket.</summary>
public partial class SupabaseBackupStorage(
    HttpClient http,
    IOptions<MediaStorageOptions> options,
    ILogger<SupabaseBackupStorage> logger) : IBackupStorage
{
    /// <summary>Supabase's list call returns one folder level at a time; archives sit two below a user.</summary>
    private const int MaxFolderDepth = 3;

    private const int ListPageSize = 1000;

    private readonly MediaStorageOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    private string Base => _options.Url!.TrimEnd('/') + "/storage/v1";

    private string Bucket => _options.BackupBucket;

    public async Task<SignedBackupUpload> CreateSignedUploadAsync(string path, CancellationToken ct)
    {
        EnsureConfigured();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/object/upload/sign/{Bucket}/{path}");
        Authorise(request);

        using var response = await http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            LogStorageFailed(logger, "sign upload", (int)response.StatusCode, Bucket);
            throw new ServiceUnavailableException(
                "Backup storage refused the upload. Check that the backup bucket exists.");
        }

        var body = await response.Content.ReadFromJsonAsync<SignResponse>(ct)
            ?? throw new ServiceUnavailableException("Backup storage returned an empty answer.");

        // Supabase answers with a path relative to /storage/v1.
        return new SignedBackupUpload(Base + body.Url, path);
    }

    public async Task<BackupObjectInfo?> GetInfoAsync(string path, CancellationToken ct)
    {
        EnsureConfigured();

        // HEAD on the authenticated route answers with the object's headers and no body, so
        // checking a several-MB archive costs nothing.
        using var request = new HttpRequestMessage(HttpMethod.Head, $"{Base}/object/authenticated/{Bucket}/{path}");
        Authorise(request);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.IsSuccessStatusCode)
        {
            return new BackupObjectInfo(response.Content.Headers.ContentLength);
        }

        // Supabase reports a missing object as 400 as often as 404.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return null;
        }

        LogStorageFailed(logger, "check object", (int)response.StatusCode, Bucket);
        throw new ServiceUnavailableException("Backup storage could not be reached.");
    }

    public async Task<string> CreateSignedDownloadAsync(string path, TimeSpan lifetime, CancellationToken ct)
    {
        EnsureConfigured();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/object/sign/{Bucket}/{path}")
        {
            Content = JsonContent.Create(new { expiresIn = (int)lifetime.TotalSeconds }),
        };
        Authorise(request);

        using var response = await http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            LogStorageFailed(logger, "sign download", (int)response.StatusCode, Bucket);
            throw new ServiceUnavailableException("Backup storage could not sign a download.");
        }

        var body = await response.Content.ReadFromJsonAsync<SignDownloadResponse>(ct)
            ?? throw new ServiceUnavailableException("Backup storage returned an empty answer.");

        return Base + body.SignedUrl;
    }

    public async Task DeleteAsync(IEnumerable<string?> paths, CancellationToken ct)
    {
        var prefixes = paths.OfType<string>().Where(p => p.Length > 0).Distinct().ToArray();

        if (prefixes.Length == 0 || !IsConfigured)
        {
            return;
        }

        try
        {
            // The delete call takes at most 1000 paths.
            foreach (var chunk in prefixes.Chunk(1000))
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, $"{Base}/object/{Bucket}")
                {
                    Content = JsonContent.Create(new { prefixes = chunk }),
                };
                Authorise(request);

                using var response = await http.SendAsync(request, ct);

                if (!response.IsSuccessStatusCode)
                {
                    LogDeleteFailed(logger, (int)response.StatusCode, chunk.Length);
                }
            }
        }
        catch (HttpRequestException ex)
        {
            LogDeleteError(logger, prefixes.Length, ex);
        }
    }

    public async Task DeleteFolderAsync(string folder, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return;
        }

        try
        {
            var files = new List<string>();
            await CollectAsync(folder.TrimEnd('/'), MaxFolderDepth, files, ct);
            await DeleteAsync(files, ct);
        }
        catch (HttpRequestException ex)
        {
            LogDeleteError(logger, 0, ex);
        }
    }

    private async Task CollectAsync(string folder, int depth, List<string> files, CancellationToken ct)
    {
        for (var offset = 0; ; offset += ListPageSize)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/object/list/{Bucket}")
            {
                Content = JsonContent.Create(new { prefix = folder, limit = ListPageSize, offset }),
            };
            Authorise(request);

            using var response = await http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                LogStorageFailed(logger, "list folder", (int)response.StatusCode, Bucket);
                return;
            }

            var entries = await response.Content.ReadFromJsonAsync<List<ListEntry>>(ct) ?? [];

            foreach (var entry in entries)
            {
                var path = $"{folder}/{entry.Name}";

                // Folders are listed with no id; they are virtual, made of their objects' paths.
                if (entry.Id is not null)
                {
                    files.Add(path);
                }
                else if (depth > 1)
                {
                    await CollectAsync(path, depth - 1, files, ct);
                }
            }

            if (entries.Count < ListPageSize)
            {
                return;
            }
        }
    }

    private void Authorise(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ServiceRoleKey);
        request.Headers.Add("apikey", _options.ServiceRoleKey);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new ServiceUnavailableException("Backup storage is not configured on this server.");
        }
    }

    private sealed record SignResponse([property: JsonPropertyName("url")] string Url);

    private sealed record SignDownloadResponse([property: JsonPropertyName("signedURL")] string SignedUrl);

    private sealed record ListEntry(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("id")] string? Id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup storage call '{Operation}' failed with {Status} (bucket {Bucket})")]
    private static partial void LogStorageFailed(ILogger logger, string operation, int status, string bucket);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting {Count} backup objects failed with {Status}")]
    private static partial void LogDeleteFailed(ILogger logger, int status, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting {Count} backup objects failed")]
    private static partial void LogDeleteError(ILogger logger, int count, Exception exception);
}
