using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using TrackBoard.Dtos;
using TrackBoard.Storage;

namespace TrackBoard.Tests.Integration;

/// <summary>
/// Stands in for the private backup bucket. Signs anything; an object exists only once a test
/// "uploads" it, the way the app PUTs to the signed URL; remembers what was deleted.
/// </summary>
public class FakeBackupStorage : IBackupStorage
{
    public ConcurrentDictionary<string, long> Objects { get; } = new();

    public ConcurrentBag<string> Deleted { get; } = [];

    public ConcurrentBag<string> DeletedFolders { get; } = [];

    public bool IsConfigured => true;

    /// <summary>What the app does with the signed URL.</summary>
    public void Upload(string path, long sizeBytes) => Objects[path] = sizeBytes;

    public Task<SignedBackupUpload> CreateSignedUploadAsync(string path, CancellationToken ct) =>
        Task.FromResult(new SignedBackupUpload($"https://backup.test/upload/{path}?token=t", path));

    public Task<BackupObjectInfo?> GetInfoAsync(string path, CancellationToken ct) =>
        Task.FromResult(Objects.TryGetValue(path, out var size) ? new BackupObjectInfo(size) : null);

    public Task<string> CreateSignedDownloadAsync(string path, TimeSpan lifetime, CancellationToken ct) =>
        Task.FromResult($"https://backup.test/download/{path}?expires={lifetime.TotalSeconds}");

    public Task DeleteAsync(IEnumerable<string?> paths, CancellationToken ct)
    {
        foreach (var path in paths.OfType<string>())
        {
            Deleted.Add(path);
            Objects.TryRemove(path, out _);
        }

        return Task.CompletedTask;
    }

    public Task DeleteFolderAsync(string folder, CancellationToken ct)
    {
        DeletedFolders.Add(folder);

        foreach (var path in Objects.Keys.Where(p => p.StartsWith(folder, StringComparison.Ordinal)))
        {
            Deleted.Add(path);
            Objects.TryRemove(path, out _);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Small limits, so the size and quota rules can be reached with tiny numbers.</summary>
public class BackupApiFactory : TrackBoardApiFactory
{
    public const long MaxArchiveBytes = 1_000;

    public const long QuotaBytes = 2_500;

    public FakeBackupStorage Storage { get; } = new();

    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>
        {
            ["Backup:MaxArchiveBytes"] = MaxArchiveBytes.ToString(CultureInfo.InvariantCulture),
            ["Backup:QuotaBytesPerUser"] = QuotaBytes.ToString(CultureInfo.InvariantCulture),
        };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IBackupStorage>();
            services.AddSingleton<IBackupStorage>(Storage);
        });
    }
}

public class BackupTests(BackupApiFactory factory) : TrackProTestBase(factory), IClassFixture<BackupApiFactory>
{
    private const string Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly FakeBackupStorage _storage = factory.Storage;

    [Fact]
    public async Task An_archive_goes_upload_then_commit_and_can_be_listed_downloaded_replaced_and_deleted()
    {
        var (client, auth) = await DriverAsync();
        var id = Guid.NewGuid();
        var startedAt = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

        // Upload and commit.
        var first = await UploadAsync(client, id, 400);
        first.Path.ShouldStartWith($"users/{auth.User.Id:N}/sessions/{id:N}/");
        first.Path.ShouldEndWith(".json.gz");
        _storage.Upload(first.Path, 400);

        var (status, created) = await PutAsync<BackupSessionResponse>(
            client, $"/api/v1/me/backup/sessions/{id}", Commit(first.Path, 400, startedAt));

        status.ShouldBe(HttpStatusCode.Created);
        created!.Id.ShouldBe(id);
        created.SizeBytes.ShouldBe(400);
        created.ContentHash.ShouldBe(Hash);
        created.Kind.ShouldBe(Entities.BackupKind.Track);

        // Listed, newest first, alongside an older one.
        var older = Guid.NewGuid();
        await CommitAsync(client, older, 100, startedAt.AddDays(-3));

        var list = await client.GetFromJsonAsync<Common.PagedResult<BackupSessionResponse>>(
            "/api/v1/me/backup/sessions?page=1&pageSize=500", Json);
        list!.Items.Select(b => b.Id).ShouldBe([id, older]);
        list.PageSize.ShouldBe(500);

        // Downloaded through a short-lived signed URL.
        var download = await client.GetFromJsonAsync<BackupDownloadResponse>(
            $"/api/v1/me/backup/sessions/{id}/download", Json);
        download!.Url.ShouldContain(first.Path);
        download.ExpiresAt.ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(9), DateTimeOffset.UtcNow.AddMinutes(11));

        // Replaced: a new object, and the old one goes once the new one is committed.
        var second = await UploadAsync(client, id, 500);
        second.Path.ShouldNotBe(first.Path);
        _storage.Upload(second.Path, 500);
        _storage.Deleted.ShouldNotContain(first.Path);

        var (replacedStatus, replaced) = await PutAsync<BackupSessionResponse>(
            client, $"/api/v1/me/backup/sessions/{id}", Commit(second.Path, 500, startedAt));

        replacedStatus.ShouldBe(HttpStatusCode.OK);
        replaced!.SizeBytes.ShouldBe(500);
        _storage.Deleted.ShouldContain(first.Path);

        // Committing the same path again is a harmless retry.
        var (retryStatus, _) = await PutAsync<BackupSessionResponse>(
            client, $"/api/v1/me/backup/sessions/{id}", Commit(second.Path, 500, startedAt));
        retryStatus.ShouldBe(HttpStatusCode.OK);
        _storage.Objects.ShouldContainKey(second.Path);

        // Deleted, and deleting again is still 204.
        (await client.DeleteAsync($"/api/v1/me/backup/sessions/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _storage.Deleted.ShouldContain(second.Path);
        (await client.DeleteAsync($"/api/v1/me/backup/sessions/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/v1/me/backup/sessions/{id}/download")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var summary = await client.GetFromJsonAsync<BackupSummaryResponse>("/api/v1/me/backup", Json);
        summary!.SessionCount.ShouldBe(1);
        summary.TotalBytes.ShouldBe(100);
        summary.QuotaBytes.ShouldBe(BackupApiFactory.QuotaBytes);
    }

    [Fact]
    public async Task Deleting_a_backup_also_removes_uploads_that_were_never_committed()
    {
        var (client, _) = await DriverAsync();
        var id = Guid.NewGuid();
        var abandoned = await UploadAsync(client, id, 100);
        _storage.Upload(abandoned.Path, 100);

        (await client.DeleteAsync($"/api/v1/me/backup/sessions/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        _storage.Objects.ShouldNotContainKey(abandoned.Path);
    }

    [Fact]
    public async Task Another_driver_cannot_download_commit_or_delete_someone_elses_backup()
    {
        var (alice, _) = await DriverAsync("Alice");
        var id = Guid.NewGuid();
        var alicesPath = await CommitAsync(alice, id, 100);

        var (mallory, _) = await DriverAsync("Mallory");

        (await mallory.GetAsync($"/api/v1/me/backup/sessions/{id}/download")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Alice's object, committed under Mallory's account with the very same session id.
        var (status, _) = await PutAsync<BackupSessionResponse>(
            mallory, $"/api/v1/me/backup/sessions/{id}", Commit(alicesPath, 100));
        status.ShouldBe(HttpStatusCode.Forbidden);

        // Mallory's delete only ever reaches Mallory's own backups.
        (await mallory.DeleteAsync($"/api/v1/me/backup/sessions/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _storage.Objects.ShouldContainKey(alicesPath);
        (await alice.GetAsync($"/api/v1/me/backup/sessions/{id}/download")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await mallory.GetFromJsonAsync<BackupSummaryResponse>("/api/v1/me/backup", Json))!.SessionCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("users/{user}/sessions/{id}/../other/0123456789abcdef0123456789abcdef.json.gz")]
    [InlineData("users/{user}/sessions/{id}/nested/0123456789abcdef0123456789abcdef.json.gz")]
    [InlineData("users/{user}/sessions/{id}/archive.json.gz")]
    [InlineData("users/{user}/sessions/{other}/0123456789abcdef0123456789abcdef.json.gz")]
    [InlineData("users/{user}/avatar/0123456789abcdef0123456789abcdef.json.gz")]
    public async Task Only_a_path_signed_for_this_session_can_be_committed(string template)
    {
        var (client, auth) = await DriverAsync();
        var id = Guid.NewGuid();
        var path = template
            .Replace("{user}", auth.User.Id.ToString("N"), StringComparison.Ordinal)
            .Replace("{id}", id.ToString("N"), StringComparison.Ordinal)
            .Replace("{other}", Guid.NewGuid().ToString("N"), StringComparison.Ordinal);
        _storage.Upload(path, 100);

        var (status, _) = await PutAsync<BackupSessionResponse>(
            client, $"/api/v1/me/backup/sessions/{id}", Commit(path, 100));

        status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Committing_before_the_upload_finished_is_a_conflict()
    {
        var (client, _) = await DriverAsync();
        var id = Guid.NewGuid();
        var target = await UploadAsync(client, id, 100);

        var response = await client.PutAsJsonAsync($"/api/v1/me/backup/sessions/{id}", Commit(target.Path, 100), Json);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(response)).ShouldBe("BackupObjectMissing");
    }

    [Fact]
    public async Task An_archive_over_the_size_limit_is_refused_before_and_after_upload()
    {
        var (client, _) = await DriverAsync();
        var id = Guid.NewGuid();

        var declared = await client.PostAsJsonAsync(
            $"/api/v1/me/backup/sessions/{id}/upload", new { sizeBytes = BackupApiFactory.MaxArchiveBytes + 1 }, Json);
        declared.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await CodeAsync(declared)).ShouldBe("BackupTooLarge");

        // Declared small, but more was sent through the signed URL: the stored size decides,
        // and the oversized object is removed.
        var target = await UploadAsync(client, id, 100);
        _storage.Upload(target.Path, BackupApiFactory.MaxArchiveBytes + 1);

        var committed = await client.PutAsJsonAsync($"/api/v1/me/backup/sessions/{id}", Commit(target.Path, 100), Json);
        committed.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        _storage.Objects.ShouldNotContainKey(target.Path);
    }

    [Fact]
    public async Task Going_over_the_quota_is_a_conflict_but_replacing_counts_only_the_new_size()
    {
        var (client, _) = await DriverAsync();
        var big = Guid.NewGuid();
        await CommitAsync(client, big, 1_000);
        await CommitAsync(client, Guid.NewGuid(), 1_000);

        // 2,000 used of 2,500: another 1,000 does not fit.
        var response = await client.PostAsJsonAsync(
            $"/api/v1/me/backup/sessions/{Guid.NewGuid()}/upload", new { sizeBytes = 1_000 }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(response)).ShouldBe("BackupQuotaExceeded");

        // Replacing an existing 1,000-byte archive with another 1,000 does.
        await CommitAsync(client, big, 1_000);
    }

    [Fact]
    public async Task Settings_round_trip_and_are_404_until_stored()
    {
        var (client, _) = await DriverAsync();

        (await client.GetAsync("/api/v1/me/backup/settings")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetFromJsonAsync<BackupSummaryResponse>("/api/v1/me/backup", Json))!
            .SettingsUpdatedAt.ShouldBeNull();

        var body = new { schemaVersion = 3, data = new { units = "metric", language = "hu", mainCar = new { id = 7 } } };
        var (status, stored) = await PutAsync<BackupSettingsResponse>(client, "/api/v1/me/backup/settings", body);
        status.ShouldBe(HttpStatusCode.OK);

        var read = await client.GetFromJsonAsync<BackupSettingsResponse>("/api/v1/me/backup/settings", Json);
        read!.SchemaVersion.ShouldBe(3);
        read.Data.GetProperty("units").GetString().ShouldBe("metric");
        read.Data.GetProperty("mainCar").GetProperty("id").GetInt32().ShouldBe(7);
        // The database keeps microseconds, the PUT answer the full clock reading.
        read.UpdatedAt.ShouldBe(stored!.UpdatedAt, TimeSpan.FromMilliseconds(1));

        (await client.GetFromJsonAsync<BackupSummaryResponse>("/api/v1/me/backup", Json))!
            .SettingsUpdatedAt!.Value.ShouldBe(stored.UpdatedAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Settings_over_64_KB_or_not_an_object_are_refused()
    {
        var (client, _) = await DriverAsync();

        var tooBig = await client.PutAsJsonAsync(
            "/api/v1/me/backup/settings",
            new { schemaVersion = 1, data = new { blob = new string('x', 64 * 1024) } },
            Json);
        tooBig.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await CodeAsync(tooBig)).ShouldBe("BackupSettingsTooLarge");

        var notAnObject = await client.PutAsJsonAsync(
            "/api/v1/me/backup/settings", new { schemaVersion = 1, data = new[] { 1, 2 } }, Json);
        notAnObject.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var missing = await client.PutAsJsonAsync("/api/v1/me/backup/settings", new { schemaVersion = 1 }, Json);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Every_backup_endpoint_needs_a_token()
    {
        var anonymous = CreateClient();
        var id = Guid.NewGuid();

        (await anonymous.GetAsync("/api/v1/me/backup")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/v1/me/backup/sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync($"/api/v1/me/backup/sessions/{id}/upload", new { sizeBytes = 1 }, Json))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/api/v1/me/backup/sessions/{id}/download")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.DeleteAsync($"/api/v1/me/backup/sessions/{id}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/v1/me/backup/settings")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_export_lists_backup_metadata_and_settings()
    {
        var (client, _) = await DriverAsync();
        var id = Guid.NewGuid();
        await CommitAsync(client, id, 300);
        await PutAsync<BackupSettingsResponse>(
            client, "/api/v1/me/backup/settings", new { schemaVersion = 2, data = new { units = "imperial" } });

        var export = await client.GetFromJsonAsync<JsonElement>("/api/v1/me/export", Json);
        var backup = export.GetProperty("backup");

        var session = backup.GetProperty("sessions").EnumerateArray().ShouldHaveSingleItem();
        session.GetProperty("id").GetGuid().ShouldBe(id);
        session.GetProperty("sizeBytes").GetInt64().ShouldBe(300);
        session.TryGetProperty("path", out _).ShouldBeFalse();
        backup.GetProperty("settings").GetProperty("data").GetProperty("units").GetString().ShouldBe("imperial");
    }

    [Fact]
    public async Task Deleting_the_account_removes_every_backup_object()
    {
        var (client, auth) = await DriverAsync();
        var first = await CommitAsync(client, Guid.NewGuid(), 100);
        var second = await CommitAsync(client, Guid.NewGuid(), 100);
        var abandoned = await UploadAsync(client, Guid.NewGuid(), 100);
        _storage.Upload(abandoned.Path, 100);
        await PutAsync<BackupSettingsResponse>(
            client, "/api/v1/me/backup/settings", new { schemaVersion = 1, data = new { units = "metric" } });

        (await client.DeleteAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        _storage.Deleted.ShouldContain(first);
        _storage.Deleted.ShouldContain(second);
        _storage.Objects.Keys.ShouldNotContain(p => p.StartsWith($"users/{auth.User.Id:N}/", StringComparison.Ordinal));
    }

    private static object Commit(string path, long sizeBytes, DateTimeOffset? startedAt = null) => new
    {
        path,
        contentHash = Hash,
        sizeBytes,
        schemaVersion = 1,
        kind = "Track",
        startedAt = (startedAt ?? DateTimeOffset.UtcNow.AddHours(-1)).ToString("O", CultureInfo.InvariantCulture),
    };

    private static async Task<BackupUploadResponse> UploadAsync(HttpClient client, Guid id, long sizeBytes)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/me/backup/sessions/{id}/upload", new { sizeBytes }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<BackupUploadResponse>(Json))!;
    }

    /// <summary>Signs, uploads and commits an archive, returning its path.</summary>
    private async Task<string> CommitAsync(HttpClient client, Guid id, long sizeBytes, DateTimeOffset? startedAt = null)
    {
        var target = await UploadAsync(client, id, sizeBytes);
        _storage.Upload(target.Path, sizeBytes);

        var (status, _) = await PutAsync<BackupSessionResponse>(
            client, $"/api/v1/me/backup/sessions/{id}", Commit(target.Path, sizeBytes, startedAt));
        status.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);

        return target.Path;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString();
}

/// <summary>A deployment with no Supabase settings: the backup is simply not offered.</summary>
public class BackupWithoutStorageTests(TrackBoardApiFactory factory) : TrackProTestBase(factory)
{
    [Theory]
    [InlineData("GET", "/api/v1/me/backup")]
    [InlineData("GET", "/api/v1/me/backup/sessions")]
    [InlineData("POST", "/api/v1/me/backup/sessions/{id}/upload")]
    [InlineData("PUT", "/api/v1/me/backup/sessions/{id}")]
    [InlineData("GET", "/api/v1/me/backup/sessions/{id}/download")]
    [InlineData("DELETE", "/api/v1/me/backup/sessions/{id}")]
    [InlineData("GET", "/api/v1/me/backup/settings")]
    [InlineData("PUT", "/api/v1/me/backup/settings")]
    public async Task Every_backup_endpoint_answers_503(string method, string url)
    {
        var (client, auth) = await DriverAsync();
        var id = Guid.NewGuid();
        var body = url.EndsWith("/upload", StringComparison.Ordinal)
            ? (object)new { sizeBytes = 100 }
            : url.EndsWith("/settings", StringComparison.Ordinal)
                ? new { schemaVersion = 1, data = new { } }
                : new
                {
                    path = $"users/{auth.User.Id:N}/sessions/{id:N}/0123456789abcdef0123456789abcdef.json.gz",
                    contentHash = new string('a', 64),
                    sizeBytes = 100,
                    schemaVersion = 1,
                    kind = "Drag",
                    startedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                };

        using var request = new HttpRequestMessage(new HttpMethod(method), url.Replace("{id}", id.ToString(), StringComparison.Ordinal));

        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Account_export_and_deletion_still_work_without_storage()
    {
        var (client, _) = await DriverAsync();

        var export = await client.GetFromJsonAsync<JsonElement>("/api/v1/me/export", Json);
        export.GetProperty("backup").GetProperty("sessions").GetArrayLength().ShouldBe(0);

        (await client.DeleteAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
