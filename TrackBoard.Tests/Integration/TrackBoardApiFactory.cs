using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TrackBoard.Data;
using TrackBoard.Data.Interceptors;

namespace TrackBoard.Tests.Integration;

/// <summary>
/// Boots the real application — real middleware, real authentication, real services — against
/// an isolated in-memory database.
/// </summary>
/// <remarks>
/// <para>
/// The database is SQLite rather than the Testcontainers PostgreSQL the roadmap calls for,
/// because Docker is not available on the machine these were written on. That trade is
/// deliberate but it is a real gap: PostgreSQL-specific translation (the leaderboard
/// <c>GROUP BY</c> projection, <c>uuid</c> and <c>bigint</c> mapping, snake_case naming) is
/// <b>not</b> covered here. Everything above the provider — routing, model binding,
/// validation, JWT validation, authorisation, service logic, cache invalidation — is.
/// </para>
/// <para>
/// Swapping in PostgreSQL is intended to be a one-method change: replace the
/// <see cref="UseTestDatabase"/> body with a Testcontainers connection string.
/// </para>
/// </remarks>
public class TrackBoardApiFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// A SQLite in-memory database lives only as long as a connection to it is open, so this
    /// one is held for the factory's lifetime and handed to every scope.
    /// </summary>
    /// <remarks>
    /// <c>Foreign Keys=True</c> matters: EF only switches SQLite's foreign-key enforcement on
    /// when it opens the connection itself, and this one arrives already open. Without it,
    /// cascades, SET NULL and RESTRICT silently do nothing here while PostgreSQL enforces them.
    /// </remarks>
    private readonly SqliteConnection _connection = new("DataSource=:memory:;Foreign Keys=True");

    /// <summary>Known to the tests so they can forge tokens to be rejected.</summary>
    public const string JwtSecret = "test-only-signing-key-that-is-comfortably-over-256-bits-long";

    public const string Issuer = "TrackBoard";

    public const string Audience = "TrackBoard";

    /// <summary>Per-class configuration overrides, applied last so they win.</summary>
    protected virtual IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        // Program reads the connection string at the top, before the host is built, and
        // ConfigureAppConfiguration below only lands at build time. A host setting is there
        // from the start. Without it the tests passed only on machines whose user-secrets
        // happened to hold a connection string, and failed on CI.
        builder.UseSetting("ConnectionStrings:Default", "Host=unused;Database=unused");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                // Startup refuses to continue without these, and the real values live in
                // user-secrets which tests must not depend on.
                ["ConnectionStrings:Default"] = "Host=unused;Database=unused",
                ["ConnectionStrings:Redis"] = string.Empty,
                ["Jwt:Secret"] = JwtSecret,
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience,
                ["Jwt:AccessTokenMinutes"] = "60",
                // The warm-up would race with each test's own seeding for no benefit.
                ["Cache:WarmupEnabled"] = "false",
                // Every test shares one client address, so production's 5-per-minute auth
                // budget would be spent partway through a class and later tests would fail
                // on 429 depending purely on execution order. RateLimitTests overrides
                // these back down to verify the limiter itself.
                ["RateLimit:AuthPermitLimit"] = "10000",
                ["RateLimit:GlobalPermitLimit"] = "100000",
            };

            foreach (var (key, value) in ExtraConfiguration)
            {
                settings[key] = value;
            }

            config.AddInMemoryCollection(settings);
        });

        builder.ConfigureTestServices(services =>
        {
            _connection.Open();

            services.RemoveAll<DbContextOptions<TrackBoardDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<TrackBoardDbContext>();

            // EF Core 9+ registers the provider selection as its own service. Removing only
            // the options leaves that behind, and the Npgsql and SQLite providers then both
            // apply — which EF rejects outright.
            services.RemoveAll<IDbContextOptionsConfiguration<TrackBoardDbContext>>();

            services.AddDbContext<TrackBoardDbContext>(UseTestDatabase);
        });
    }

    /// <summary>
    /// The schema is created once the real host exists, rather than from a throwaway provider
    /// built during service registration — that second provider would own a different
    /// connection and the migration would land in a database nothing else could see.
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TrackBoardDbContext>().Database.EnsureCreated();

        return host;
    }

    private void UseTestDatabase(IServiceProvider sp, DbContextOptionsBuilder options) =>
        options
            .UseSqlite(_connection)
            // Kept identical to production so column naming cannot drift between the two.
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<AuditingInterceptor>())
            .ReplaceService<IModelCustomizer, SqliteDateTimeOffsetModelCustomizer>();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _connection.Dispose();
        }

        base.Dispose(disposing);
    }
}
