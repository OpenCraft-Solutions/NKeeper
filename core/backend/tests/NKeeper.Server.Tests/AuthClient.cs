using System.Net.Http.Json;
using System.Text.Json;

namespace NKeeper.Server.Tests;

/// <summary>What a browser does with the API: fetch an antiforgery token, send it on unsafe requests.</summary>
internal static class AuthClient
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A fresh antiforgery token; tokens are bound to the user, so fetch one after signing in or out.</summary>
    public static async Task<(string HeaderName, string Token)> AntiforgeryAsync(this HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/api/auth/antiforgery", UriKind.Relative), Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        return (json.RootElement.GetProperty("headerName").GetString()!, json.RootElement.GetProperty("token").GetString()!);
    }

    /// <summary>Sends a request the way the SDK does, with the antiforgery header.</summary>
    public static async Task<HttpResponseMessage> SendWithTokenAsync(this HttpClient client, HttpMethod method, string path, object? body = null)
    {
        var (header, token) = await client.AntiforgeryAsync();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(header, token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, Token);
    }

    public static Task<HttpResponseMessage> LoginAsync(this HttpClient client, string userNameOrEmail, string password, bool rememberMe = false) =>
        client.SendWithTokenAsync(HttpMethod.Post, "/api/auth/login", new { userNameOrEmail, password, rememberMe });

    public static Task<HttpResponseMessage> LogoutAsync(this HttpClient client) =>
        client.SendWithTokenAsync(HttpMethod.Post, "/api/auth/logout");

    /// <summary>The body of a problem response, which must be <c>application/problem+json</c>.</summary>
    public static async Task<JsonElement> ProblemAsync(this HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        return json.RootElement.Clone();
    }

    public static IEnumerable<string> SetCookies(this HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
}
