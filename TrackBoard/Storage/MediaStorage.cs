using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TrackBoard.Common;

namespace TrackBoard.Storage;

/// <summary>
/// Where driver photos live. Configured from the <c>Supabase</c> section; every field is
/// optional so a deployment without photo storage still boots and simply answers 503 on the
/// upload endpoints.
/// </summary>
public class MediaStorageOptions
{
    public const string SectionName = "Supabase";

    /// <summary>The project URL, e.g. <c>https://abcd.supabase.co</c>.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// The service-role key. Server-side only: it bypasses row-level security, which is why
    /// the phone never sees it and instead receives a single-use signed upload URL.
    /// </summary>
    public string? ServiceRoleKey { get; set; }

    /// <summary>A public bucket. Object paths carry a random id, so they are not guessable.</summary>
    public string MediaBucket { get; set; } = "trackboard-media";

    /// <summary>
    /// A private bucket for session archives. They hold GPS location history, so nothing in
    /// it is ever public: reads go through short-lived signed URLs only.
    /// </summary>
    public string BackupBucket { get; set; } = "trackboard-backup";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ServiceRoleKey);
}

/// <summary>A signed, single-use upload target plus where the object will be readable afterwards.</summary>
public record SignedUpload(string UploadUrl, string Path, string PublicUrl);

public interface IMediaStorage
{
    bool IsConfigured { get; }

    /// <summary>
    /// Prefix that turns an object path into its public URL, or null when storage is off.
    /// Exposed so SQL projections can build URLs without a per-row call.
    /// </summary>
    string? PublicBaseUrl { get; }

    /// <summary>Signs an upload for exactly <paramref name="path"/>. The client PUTs the bytes itself.</summary>
    Task<SignedUpload> CreateSignedUploadAsync(string path, CancellationToken ct);

    /// <summary>The public URL of an object, or null for no object. Pure string work, no I/O.</summary>
    string? PublicUrl(string? path);

    /// <summary>
    /// Best effort. A photo left behind in the bucket is a storage cost, not a correctness
    /// problem, so a failure here is logged and never fails the request that triggered it.
    /// </summary>
    Task DeleteAsync(IEnumerable<string?> paths, CancellationToken ct);
}

/// <summary>Supabase Storage over its REST API. No SDK: three calls do not justify one.</summary>
public partial class SupabaseMediaStorage(
    HttpClient http,
    IOptions<MediaStorageOptions> options,
    ILogger<SupabaseMediaStorage> logger) : IMediaStorage
{
    private readonly MediaStorageOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    private string Base => _options.Url!.TrimEnd('/') + "/storage/v1";

    public async Task<SignedUpload> CreateSignedUploadAsync(string path, CancellationToken ct)
    {
        EnsureConfigured();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{Base}/object/upload/sign/{_options.MediaBucket}/{path}");
        Authorise(request);

        using var response = await http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            LogSignFailed(logger, (int)response.StatusCode, _options.MediaBucket);
            throw new ServiceUnavailableException(
                "Photo storage refused the upload. Check that the media bucket exists.");
        }

        var body = await response.Content.ReadFromJsonAsync<SignResponse>(ct)
            ?? throw new ServiceUnavailableException("Photo storage returned an empty answer.");

        // Supabase answers with a path relative to /storage/v1.
        return new SignedUpload(Base + body.Url, path, PublicUrl(path)!);
    }

    public string? PublicBaseUrl => IsConfigured ? $"{Base}/object/public/{_options.MediaBucket}/" : null;

    public string? PublicUrl(string? path) =>
        string.IsNullOrEmpty(path) || PublicBaseUrl is not { } baseUrl ? null : baseUrl + path;

    public async Task DeleteAsync(IEnumerable<string?> paths, CancellationToken ct)
    {
        var prefixes = paths.OfType<string>().Where(p => p.Length > 0).Distinct().ToArray();

        if (prefixes.Length == 0 || !IsConfigured)
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{Base}/object/{_options.MediaBucket}")
            {
                Content = JsonContent.Create(new { prefixes }),
            };
            Authorise(request);

            using var response = await http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                LogDeleteFailed(logger, (int)response.StatusCode, prefixes.Length);
            }
        }
        catch (HttpRequestException ex)
        {
            LogDeleteError(logger, prefixes.Length, ex);
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
            throw new ServiceUnavailableException("Photo storage is not configured on this server.");
        }
    }

    private sealed record SignResponse([property: JsonPropertyName("url")] string Url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Signing an upload failed with {Status} (bucket {Bucket})")]
    private static partial void LogSignFailed(ILogger logger, int status, string bucket);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting {Count} media objects failed with {Status}")]
    private static partial void LogDeleteFailed(ILogger logger, int status, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting {Count} media objects failed")]
    private static partial void LogDeleteError(ILogger logger, int count, Exception exception);
}
