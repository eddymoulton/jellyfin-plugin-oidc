using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SSO_Auth.Tests.Helpers;

/// <summary>
/// Drives the full OIDC login flow against the running dex + Jellyfin stack with a single
/// HttpClient + CookieContainer. Mirrors the seven-step walk documented in
/// docs/superpowers/specs/2026-05-27-automated-tests-design.md (the "OidcFlow" section).
/// </summary>
public sealed class OidcFlow(string jellyfinBaseUrl, string providerName)
{
  private static readonly Regex StateInHtml =
      new("var data = '([^']+)'", RegexOptions.Compiled);

  public async Task<OidcLoginResult> LoginAsync(
      string email,
      string password,
      CancellationToken ct = default)
  {
    var cookies = new CookieContainer();
    using var handler = new HttpClientHandler
    {
      CookieContainer = cookies,
      AllowAutoRedirect = false,
      UseCookies = true,
    };
    using var http = new HttpClient(handler);

    var step1 = await Step1StartAsync(http, ct);
    if (step1.Denial is not null)
    {
      return step1.Denial;
    }

    var step3Uri = await Step2DexAuthAsync(http, step1.DexAuthUri!, ct);
    var loginUri = await Step3AuthLocalAsync(http, step3Uri, ct);
    var redirectUri = await Step4SubmitCredentialsAsync(http, loginUri, email, password, ct);

    var step5 = await Step5PluginRedirectAsync(http, redirectUri, ct);
    if (step5.Denial is not null)
    {
      return step5.Denial;
    }

    return await Step6CompleteAuthAsync(http, step5.StateToken!, ct);
  }

  /// <summary>Step 1: GET /sso/OID/start/{provider} — expect 302 to dex.</summary>
  private async Task<Step1Result> Step1StartAsync(HttpClient http, CancellationToken ct)
  {
    var response = await http.GetAsync(
        $"{jellyfinBaseUrl}/sso/OID/start/{providerName}", ct);

    if (response.StatusCode != HttpStatusCode.Found)
    {
      return new Step1Result(null, OidcLoginResult.Denied(response.StatusCode, await ReadAsync(response, ct)));
    }

    var dexAuthUri = response.Headers.Location
        ?? throw new InvalidOperationException("Step 1 missing Location header.");

    return new Step1Result(dexAuthUri, null);
  }

  /// <summary>Step 2: GET dex /auth — expect 302 to /auth/local?...</summary>
  private static async Task<Uri> Step2DexAuthAsync(HttpClient http, Uri dexAuthUri, CancellationToken ct)
  {
    var response = await http.GetAsync(dexAuthUri, ct);
    return ResolveLocation(dexAuthUri, response);
  }

  /// <summary>Step 3: GET /auth/local — expect 302 to /auth/local/login?back=&amp;state=&lt;dex-session&gt;.</summary>
  private static async Task<Uri> Step3AuthLocalAsync(HttpClient http, Uri authLocalUri, CancellationToken ct)
  {
    var response = await http.GetAsync(authLocalUri, ct);
    return ResolveLocation(authLocalUri, response);
  }

  /// <summary>Step 4: POST credentials. Expect 302/303 back to the plugin's /redirect URL.</summary>
  private static async Task<Uri> Step4SubmitCredentialsAsync(
      HttpClient http,
      Uri loginUri,
      string email,
      string password,
      CancellationToken ct)
  {
    var content = new FormUrlEncodedContent(new[]
    {
        new KeyValuePair<string, string>("login", email),
        new KeyValuePair<string, string>("password", password),
    });
    var response = await http.PostAsync(loginUri, content, ct);

    if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.SeeOther))
    {
      throw new InvalidOperationException(
          $"Dex login POST returned {(int)response.StatusCode}; expected 302/303. "
          + $"Body: {await ReadAsync(response, ct)}");
    }

    return ResolveLocation(loginUri, response);
  }

  /// <summary>Step 5: GET plugin /redirect endpoint. Either returns the state token or a denial.</summary>
  private static async Task<Step5Result> Step5PluginRedirectAsync(HttpClient http, Uri redirectUri, CancellationToken ct)
  {
    var response = await http.GetAsync(redirectUri, ct);

    if (!response.IsSuccessStatusCode)
    {
      // Denied path: e.g. the no-access user gets 401 with "Error. Check permissions."
      return new Step5Result(null, OidcLoginResult.Denied(response.StatusCode, await ReadAsync(response, ct)));
    }

    var html = await ReadAsync(response, ct);
    var match = StateInHtml.Match(html);
    if (!match.Success)
    {
      throw new InvalidOperationException(
          "Plugin /redirect HTML did not contain `var data = '<state>'`. "
          + $"Body (first 400 chars): {html[..Math.Min(400, html.Length)]}");
    }

    return new Step5Result(match.Groups[1].Value, null);
  }

  /// <summary>Step 6: POST /sso/OID/Auth/{provider} with the state token to complete the session.</summary>
  private async Task<OidcLoginResult> Step6CompleteAuthAsync(HttpClient http, string stateToken, CancellationToken ct)
  {
    var payload = new
    {
      deviceId = "oidc-flow-test",
      appName = "oidc-flow-test",
      appVersion = "1.0.0",
      deviceName = "oidc-flow-test",
      data = stateToken,
    };
    var body = new StringContent(
        JsonSerializer.Serialize(payload),
        System.Text.Encoding.UTF8,
        "application/json");
    var response = await http.PostAsync(
        $"{jellyfinBaseUrl}/sso/OID/Auth/{providerName}", body, ct);
    response.EnsureSuccessStatusCode();

    var resultBody = await ReadAsync(response, ct);
    var result = JsonSerializer.Deserialize<JsonElement>(resultBody);

    var accessToken = result.GetProperty("AccessToken").GetString()
        ?? throw new InvalidOperationException("AuthenticationResult.AccessToken was null.");
    var userElement = result.GetProperty("User");

    return OidcLoginResult.Success(accessToken, JellyfinUser.From(userElement));
  }

  private static Uri ResolveLocation(Uri baseUri, HttpResponseMessage response)
  {
    var location = response.Headers.Location
        ?? throw new InvalidOperationException(
            $"Expected Location header on {(int)response.StatusCode} response; got none.");

    return location.IsAbsoluteUri ? location : new Uri(baseUri, location);
  }

  private static Task<string> ReadAsync(HttpResponseMessage response, CancellationToken ct) =>
      response.Content.ReadAsStringAsync(ct);

  private readonly record struct Step1Result(Uri? DexAuthUri, OidcLoginResult? Denial);

  private readonly record struct Step5Result(string? StateToken, OidcLoginResult? Denial);
}

public sealed record OidcLoginResult(
    bool Succeeded,
    string? AccessToken,
    JellyfinUser? User,
    HttpStatusCode? DeniedStatusCode,
    string? DeniedBody)
{
  public static OidcLoginResult Success(string accessToken, JellyfinUser user) =>
      new(true, accessToken, user, null, null);

  public static OidcLoginResult Denied(HttpStatusCode statusCode, string body) =>
      new(false, null, null, statusCode, body);
}

public sealed record JellyfinUser(string Id, string Name, bool IsAdministrator)
{
  public static JellyfinUser From(JsonElement user)
  {
    var policy = user.GetProperty("Policy");
    return new JellyfinUser(
        Id: user.GetProperty("Id").GetString() ?? throw new InvalidOperationException("User.Id missing"),
        Name: user.GetProperty("Name").GetString() ?? throw new InvalidOperationException("User.Name missing"),
        IsAdministrator: policy.GetProperty("IsAdministrator").GetBoolean());
  }
}
