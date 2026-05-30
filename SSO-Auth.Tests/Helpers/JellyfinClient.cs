using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Plugin.SSO_Auth.TestEnv;

namespace Jellyfin.Plugin.SSO_Auth.Tests.Helpers;

/// <summary>
/// Thin wrapper around the Jellyfin admin REST API. One instance per test;
/// dispose to release the underlying HttpClient. Reuses the Authorization
/// header format ProviderProvisioner uses, so calls look identical to the
/// orchestration CLI's auth calls on the wire.
/// </summary>
public sealed class JellyfinClient : IDisposable
{
  private const string UnauthenticatedAuthHeader =
      "MediaBrowser Client=\"sso-auth-tests\", Device=\"sso-auth-tests\", DeviceId=\"sso-auth-tests\", Version=\"1.0.0\"";

  private readonly HttpClient _http;
  private readonly EnvConfig _config;
  private string? _token;

  public JellyfinClient(string jellyfinBaseUrl, EnvConfig config)
  {
    _http = new HttpClient { BaseAddress = new Uri(jellyfinBaseUrl) };
    _config = config;
  }

  public string Token => _token ?? throw new InvalidOperationException(
      "Token not set. Call AuthenticateAdminAsync first.");

  public async Task<string> AuthenticateAdminAsync(CancellationToken ct = default)
  {
    using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName")
    {
      Content = JsonContent.Create(new
      {
        Username = _config.AdminUsername,
        Pw = _config.AdminPassword,
      }),
    };
    request.Headers.TryAddWithoutValidation("Authorization", UnauthenticatedAuthHeader);

    using var response = await _http.SendAsync(request, ct);
    response.EnsureSuccessStatusCode();

    var body = await response.Content.ReadAsStringAsync(ct);
    using var doc = JsonDocument.Parse(body);
    _token = doc.RootElement.GetProperty("AccessToken").GetString()
        ?? throw new InvalidOperationException("AuthenticateByName response missing AccessToken.");
    return _token;
  }

  public async Task<IReadOnlyList<JellyfinUserSummary>> ListUsersAsync(CancellationToken ct = default)
  {
    using var request = new HttpRequestMessage(HttpMethod.Get, "/Users");
    request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{Token}\"");

    using var response = await _http.SendAsync(request, ct);
    response.EnsureSuccessStatusCode();

    var body = await response.Content.ReadAsStringAsync(ct);
    using var doc = JsonDocument.Parse(body);

    var users = new List<JellyfinUserSummary>();
    foreach (var element in doc.RootElement.EnumerateArray())
    {
      users.Add(JellyfinUserSummary.From(element));
    }
    return users;
  }

  public async Task<JellyfinUserSummary> CreateUserAsync(string name, string password, CancellationToken ct = default)
  {
    using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/New")
    {
      Content = JsonContent.Create(new { Name = name, Password = password }),
    };
    request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{Token}\"");

    using var response = await _http.SendAsync(request, ct);
    response.EnsureSuccessStatusCode();

    var body = await response.Content.ReadAsStringAsync(ct);
    using var doc = JsonDocument.Parse(body);
    return JellyfinUserSummary.From(doc.RootElement);
  }

  public void Dispose() => _http.Dispose();
}

public sealed record JellyfinUserSummary(Guid Id, string Name, bool IsAdministrator)
{
  public static JellyfinUserSummary From(JsonElement user)
  {
    var policy = user.GetProperty("Policy");
    var idText = user.GetProperty("Id").GetString()
        ?? throw new InvalidOperationException("User.Id missing");
    return new JellyfinUserSummary(
        Id: Guid.Parse(idText),
        Name: user.GetProperty("Name").GetString()
            ?? throw new InvalidOperationException("User.Name missing"),
        IsAdministrator: policy.GetProperty("IsAdministrator").GetBoolean());
  }
}
