using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.SSO_Auth.TestEnv;

public sealed class ProviderProvisioner(EnvConfig config)
{
    private const string ApiKeyAppName = "test-env";
    private const string AuthHeaderTemplate =
        "MediaBrowser Client=\"jellyfin-sso-test\", Device=\"test-env\", DeviceId=\"test-env-script\", Version=\"1.0.0\"";

    /// <summary>
    /// Idempotent: authenticates as admin, reuses an existing "test-env" API key or mints a new one,
    /// then POSTs every seed file in <see cref="EnvConfig.SeedDir"/> to /sso/OID/Add/{name} (where
    /// {name} is the file name without extension), and verifies all of them via /sso/OID/Get.
    /// </summary>
    public async Task ProvisionAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(config.SeedDir))
        {
            throw new OrchestrationException($"Missing seed directory: {config.SeedDir}");
        }

        var seedFiles = Directory.GetFiles(config.SeedDir, "*.json").OrderBy(f => f).ToArray();
        if (seedFiles.Length == 0)
        {
            throw new OrchestrationException($"No provider seed files (*.json) in {config.SeedDir}");
        }

        using var http = new HttpClient { BaseAddress = new Uri(config.JellyfinBaseUrl) };

        Console.Out.WriteLine($"[+] Authenticating as {config.AdminUsername} ...");
        var token = await AuthenticateAdminAsync(http, ct);

        Console.Out.WriteLine("[+] Minting an API key ...");
        var apiKey = await EnsureApiKeyAsync(http, token, ct);

        Console.Out.WriteLine("[+] Waiting for SSO plugin routes ...");
        await WaitForPluginRoutesAsync(http, apiKey, ct);

        Console.Out.WriteLine("[+] Resolving library names ...");
        var libraryIdsByName = await GetLibraryIdsByNameAsync(http, token, ct);

        var providerNames = new List<string>();
        foreach (var seedFile in seedFiles)
        {
            var providerName = Path.GetFileNameWithoutExtension(seedFile);
            providerNames.Add(providerName);

            Console.Out.WriteLine($"[+] Registering provider '{providerName}' from {Path.GetFileName(seedFile)} ...");
            var seedJson = ResolveFolderNames(await File.ReadAllTextAsync(seedFile, ct), libraryIdsByName);
            var addResponse = await http.PostAsync(
                $"/sso/OID/Add/{providerName}?api_key={Uri.EscapeDataString(apiKey)}",
                new StringContent(seedJson, Encoding.UTF8, "application/json"),
                ct);
            addResponse.EnsureSuccessStatusCode();
        }

        Console.Out.WriteLine("[+] Verifying provider registration ...");
        var getResponse = await http.GetAsync(
            $"/sso/OID/Get?api_key={Uri.EscapeDataString(apiKey)}",
            ct);
        getResponse.EnsureSuccessStatusCode();
        var body = await getResponse.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        foreach (var providerName in providerNames)
        {
            if (!doc.RootElement.TryGetProperty(providerName, out _))
            {
                throw new OrchestrationException(
                    $"Provider '{providerName}' did not appear in /sso/OID/Get response:\n{body}");
            }
        }

        Console.Out.WriteLine($"[+] Registered providers: {string.Join(", ", providerNames)}.");
    }

    /// <summary>
    /// Fetches Jellyfin's media folders (libraries) as a case-insensitive name -> id map, so seed
    /// files can reference folders by human-readable name instead of opaque ids.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> GetLibraryIdsByNameAsync(HttpClient http, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Library/MediaFolders");
        request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{token}\"");

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in doc.RootElement.GetProperty("Items").EnumerateArray())
        {
            var name = item.GetProperty("Name").GetString();
            var id = item.GetProperty("Id").GetString();
            if (name is not null && id is not null)
            {
                map[name] = id;
            }
        }

        return map;
    }

    /// <summary>
    /// Rewrites folderRoleMapping[].folders entries: a value that is already a Guid is kept as-is,
    /// anything else is treated as a library name and resolved to its id. Unknown names throw, so a
    /// typo'd library name fails provisioning loudly rather than silently granting nothing.
    /// </summary>
    private static string ResolveFolderNames(string seedJson, IReadOnlyDictionary<string, string> libraryIdsByName)
    {
        var seed = JsonNode.Parse(seedJson)!.AsObject();
        if (seed["folderRoleMapping"] is not JsonArray mappings)
        {
            return seedJson;
        }

        foreach (var mapping in mappings.OfType<JsonObject>())
        {
            if (mapping["folders"] is not JsonArray folders)
            {
                continue;
            }

            for (var i = 0; i < folders.Count; i++)
            {
                var entry = folders[i]?.GetValue<string>();
                if (entry is null || Guid.TryParse(entry, out _))
                {
                    continue;
                }

                if (!libraryIdsByName.TryGetValue(entry, out var id))
                {
                    throw new OrchestrationException(
                        $"Folder mapping references unknown library '{entry}'. "
                        + $"Known libraries: {string.Join(", ", libraryIdsByName.Keys)}");
                }

                folders[i] = id;
            }
        }

        return seed.ToJsonString();
    }

    /// <summary>
    /// Waits for the SSO plugin's controller routes to be mapped. On a cold start, Jellyfin's core API
    /// (and even /Users/AuthenticateByName) can respond before the plugin finishes loading, so the
    /// /sso/* routes briefly 404. Polls /sso/OID/Get (which requires the just-minted API key) until it
    /// returns 200, treating 404 and 5xx as transient and any other status as a real failure.
    /// </summary>
    private async Task WaitForPluginRoutesAsync(HttpClient http, string apiKey, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(config.PluginReadyTimeoutSeconds);
        var url = $"/sso/OID/Get?api_key={Uri.EscapeDataString(apiKey)}";
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                var code = (int)response.StatusCode;
                if (code != 404 && code is < 500 or >= 600)
                {
                    // Not a cold-start race (e.g. 401/403); surface it immediately.
                    response.EnsureSuccessStatusCode();
                }

                Console.Out.WriteLine($"[+] SSO routes not ready ({code}); retrying ...");
            }
            catch (HttpRequestException)
            {
                // Not up yet.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // GET timeout; retry.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new OrchestrationException(
            $"SSO plugin routes did not become available within {config.PluginReadyTimeoutSeconds} seconds."
            + ReadJellyfinPluginLogTail());
    }

    /// <summary>
    /// Best-effort: reads the most recent Jellyfin log from the bind-mounted config dir and returns the
    /// plugin-relevant lines, so a route-wait timeout (which otherwise only says "404 for N seconds")
    /// carries the actual plugin-load evidence — whether the SSO assembly loaded, whether the plugin was
    /// registered, and any ABI/exception. Never throws; diagnostics must not mask the original failure.
    /// </summary>
    private string ReadJellyfinPluginLogTail()
    {
        try
        {
            var logDir = Path.Combine(config.JellyfinConfigDir, "log");
            if (!Directory.Exists(logDir))
            {
                return $"\n(No Jellyfin log dir at {logDir}.)";
            }

            var newest = new DirectoryInfo(logDir)
                .GetFiles("log_*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null)
            {
                return $"\n(No Jellyfin log files in {logDir}.)";
            }

            string[] keywords = { "plugin", "sso", "oidc", "abi", "controller", "error", "exception", "fail" };
            var relevant = File.ReadLines(newest.FullName)
                .Where(line => keywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .TakeLast(40)
                .ToArray();

            return relevant.Length == 0
                ? $"\n(No plugin-related lines in {newest.Name}.)"
                : $"\n--- Jellyfin plugin log ({newest.Name}) ---\n{string.Join('\n', relevant)}";
        }
        catch (Exception ex)
        {
            return $"\n(Could not read Jellyfin log for diagnostics: {ex.Message})";
        }
    }

    private async Task<string> AuthenticateAdminAsync(HttpClient http, CancellationToken ct)
    {
        // TODO: Revisit whether a smarter health probe in ContainerStack.WaitForJellyfinAsync
        // (e.g., poll /Users/AuthenticateByName itself, or wait on a more specific endpoint)
        // would remove the need for this retry loop. For now, the loop handles the known cold-start
        // race where /System/Info/Public is 200 but /Users/AuthenticateByName is 503 for several seconds.
        // On a fresh container start, Jellyfin can take 60+ seconds for the auth endpoint to
        // become available, so 30 attempts at 2s = 60s is the minimum safe budget.
        const int maxAttempts = 30;
        var delay = TimeSpan.FromSeconds(2);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName")
            {
                Content = JsonContent.Create(new
                {
                    Username = config.AdminUsername,
                    Pw = config.AdminPassword,
                }),
            };
            request.Headers.TryAddWithoutValidation("Authorization", AuthHeaderTemplate);

            using var response = await http.SendAsync(request, ct);

            if ((int)response.StatusCode is >= 500 and < 600 && attempt < maxAttempts)
            {
                Console.Out.WriteLine(
                    $"[+] Admin auth returned {(int)response.StatusCode}; retrying in {delay.TotalSeconds}s "
                    + $"({attempt}/{maxAttempts}) ...");
                await Task.Delay(delay, ct);
                continue;
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("AccessToken", out var tokenElement)
                && tokenElement.GetString() is { Length: > 0 } token)
            {
                return token;
            }

            throw new OrchestrationException("Admin authentication response did not contain AccessToken.");
        }

        throw new OrchestrationException(
            $"Admin authentication failed with 5xx after {maxAttempts} attempts.");
    }

    private async Task<string> EnsureApiKeyAsync(HttpClient http, string token, CancellationToken ct)
    {
        var existing = await GetApiKeyAsync(http, token, ct);
        if (existing is not null)
        {
            return existing;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"/Auth/Keys?App={Uri.EscapeDataString(ApiKeyAppName)}");
        request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{token}\"");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var afterCreate = await GetApiKeyAsync(http, token, ct);
        return afterCreate
            ?? throw new OrchestrationException("Failed to obtain an API key after creation request.");
    }

    private static async Task<string?> GetApiKeyAsync(HttpClient http, string token, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/Auth/Keys");
        request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{token}\"");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("AppName", out var appName)
                && appName.GetString() == ApiKeyAppName
                && item.TryGetProperty("AccessToken", out var accessToken))
            {
                var value = accessToken.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
        }

        return null;
    }
}
