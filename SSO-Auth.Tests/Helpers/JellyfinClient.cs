using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

  /// <summary>
  /// Sets a user's IsAdministrator flag via the admin API, preserving the rest of their policy.
  /// </summary>
  public async Task SetAdministratorAsync(Guid userId, bool isAdmin, CancellationToken ct = default)
  {
    using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/Users/{userId}");
    getRequest.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{Token}\"");

    using var getResponse = await _http.SendAsync(getRequest, ct);
    getResponse.EnsureSuccessStatusCode();

    var userBody = await getResponse.Content.ReadAsStringAsync(ct);
    using var doc = JsonDocument.Parse(userBody);
    var policy = JsonNode.Parse(doc.RootElement.GetProperty("Policy").GetRawText())
        ?? throw new InvalidOperationException("User response missing Policy.");
    policy["IsAdministrator"] = isAdmin;

    using var postRequest = new HttpRequestMessage(HttpMethod.Post, $"/Users/{userId}/Policy")
    {
      Content = new StringContent(policy.ToJsonString(), Encoding.UTF8, "application/json"),
    };
    postRequest.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{Token}\"");

    using var postResponse = await _http.SendAsync(postRequest, ct);
    postResponse.EnsureSuccessStatusCode();
  }

  /// <summary>
  /// Reads a user's folder access policy (EnableAllFolders + EnabledFolders).
  /// </summary>
  public async Task<UserFolderPolicy> GetFolderPolicyAsync(Guid userId, CancellationToken ct = default)
  {
    using var request = new HttpRequestMessage(HttpMethod.Get, $"/Users/{userId}");
    request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{Token}\"");

    using var response = await _http.SendAsync(request, ct);
    response.EnsureSuccessStatusCode();

    var body = await response.Content.ReadAsStringAsync(ct);
    using var doc = JsonDocument.Parse(body);
    var policy = doc.RootElement.GetProperty("Policy");

    var enableAllFolders = policy.GetProperty("EnableAllFolders").GetBoolean();
    var enabledFolders = policy.GetProperty("EnabledFolders")
        .EnumerateArray()
        .Select(e => Guid.Parse(e.GetString() ?? throw new InvalidOperationException("EnabledFolders entry null")))
        .ToArray();

    return new UserFolderPolicy(enableAllFolders, enabledFolders);
  }

  /// <summary>
  /// Lists Jellyfin's media folders (libraries) as name/id pairs, for translating between
  /// folder names and the opaque ids that appear in user policies.
  /// </summary>
  public async Task<IReadOnlyList<MediaFolder>> GetMediaFoldersAsync(CancellationToken ct = default)
  {
    using var request = new HttpRequestMessage(HttpMethod.Get, "/Library/MediaFolders");
    request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{Token}\"");

    using var response = await _http.SendAsync(request, ct);
    response.EnsureSuccessStatusCode();

    var body = await response.Content.ReadAsStringAsync(ct);
    using var doc = JsonDocument.Parse(body);

    var folders = new List<MediaFolder>();
    foreach (var item in doc.RootElement.GetProperty("Items").EnumerateArray())
    {
      var id = item.GetProperty("Id").GetString()
          ?? throw new InvalidOperationException("MediaFolder Id missing");
      var name = item.GetProperty("Name").GetString()
          ?? throw new InvalidOperationException("MediaFolder Name missing");
      folders.Add(new MediaFolder(Guid.Parse(id), name));
    }

    return folders;
  }

  public void Dispose() => _http.Dispose();
}

public sealed record MediaFolder(Guid Id, string Name);

public sealed record UserFolderPolicy(bool EnableAllFolders, IReadOnlyList<Guid> EnabledFolders);

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
