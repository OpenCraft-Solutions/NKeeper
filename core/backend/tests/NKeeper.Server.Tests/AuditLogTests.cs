using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>docs/architecture.md §7.44: the audit log page's API, for administrators.</summary>
public sealed class AuditLogTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string BobPassword = "Bob-Passw0rd-123";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Administrators_read_the_log_newest_first_a_page_at_a_time_with_hosts_named()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();
        var web = await CreateHostAsync(admin, "web01");
        var db = await CreateHostAsync(admin, "db01");

        var page = await admin.GetFromJsonAsync<JsonElement>("/api/audit/events?category=host&limit=1", Token);
        var newest = Assert.Single(page.GetProperty("events").EnumerateArray());
        Assert.Equal(("host.create", db.ToString(), "db01"), (newest.GetProperty("action").GetString(), newest.GetProperty("target").GetString(), newest.GetProperty("hostName").GetString()));

        var next = page.GetProperty("next").GetInt64();
        var older = await admin.GetFromJsonAsync<JsonElement>($"/api/audit/events?category=host&limit=1&before={next}", Token);
        Assert.Equal(web.ToString(), Assert.Single(older.GetProperty("events").EnumerateArray()).GetProperty("target").GetString());
        Assert.Equal(JsonValueKind.Null, older.GetProperty("next").ValueKind);

        // Filters combine: a target exactly, text anywhere, and a time range.
        var byTarget = await admin.GetFromJsonAsync<JsonElement>($"/api/audit/events?target={web}", Token);
        Assert.All(byTarget.GetProperty("events").EnumerateArray(), e => Assert.Equal(web.ToString(), e.GetProperty("target").GetString()));
        var byText = await admin.GetFromJsonAsync<JsonElement>("/api/audit/events?q=DB01", Token);
        Assert.Contains(byText.GetProperty("events").EnumerateArray(), e => e.GetProperty("detail").GetString() == "db01");
        var future = await admin.GetFromJsonAsync<JsonElement>($"/api/audit/events?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"))}", Token);
        Assert.Empty(future.GetProperty("events").EnumerateArray());

        var categories = await admin.GetFromJsonAsync<string[]>("/api/audit/categories", Token);
        Assert.Contains("host", categories!);
        Assert.Contains("auth", categories!);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/audit/events?limit=501", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/audit/events?from=yesterday", Token)).StatusCode);
    }

    [Fact]
    public async Task The_CSV_has_the_filtered_events_and_nothing_a_spreadsheet_would_run()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();
        await CreateHostAsync(admin, "=HYPERLINK(\"x\")");

        using var response = await admin.GetAsync("/api/audit/events.csv?category=host", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        var lines = (await response.Content.ReadAsStringAsync(Token)).TrimEnd().Split("\r\n");
        Assert.Equal("Time (UTC),Actor,Client address,Action,Target,Host,Detail", lines[0]);
        var row = Assert.Single(lines.Skip(1));
        Assert.Contains("\"host.create\"", row, StringComparison.Ordinal);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_administrators_see_the_log()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        await server.CreateUserAsync("bob", BobPassword);
        using var bob = server.Client();
        (await bob.LoginAsync("bob", BobPassword)).EnsureSuccessStatusCode();

        foreach (var path in new[] { "/api/audit/events", "/api/audit/events.csv", "/api/audit/categories" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync(path, Token)).StatusCode);
        }
    }

    private static async Task<Guid> CreateHostAsync(HttpClient admin, string name)
    {
        using var response = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new { name, ipAddress = "127.0.0.1" });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
    }
}
