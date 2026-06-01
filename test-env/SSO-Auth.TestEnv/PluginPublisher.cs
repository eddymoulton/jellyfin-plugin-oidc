using System.Diagnostics;
using System.Text.Json;

namespace Jellyfin.Plugin.SSO_Auth.TestEnv;

public sealed class PluginPublisher(EnvConfig config)
{
    public async Task PublishAsync(CancellationToken ct = default)
    {
        Console.Out.WriteLine($"[+] Publishing plugin to {config.PublishDir} ...");

        var csproj = Path.Combine(config.RepoRoot, "SSO-Auth", "SSO-Auth.csproj");
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("publish");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Release");
        psi.ArgumentList.Add(csproj);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(config.PublishDir);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet.");
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            throw new OrchestrationException($"dotnet publish exited with code {process.ExitCode}.");
        }

        await WriteMetaJsonAsync(ct);
    }

    /// <summary>
    /// Writes a Jellyfin plugin manifest (<c>meta.json</c>) into the publish output, sourced from the
    /// production <c>build.yaml</c>.
    /// <para>
    /// A bare <c>dotnet publish</c> does not emit a meta.json (only the production JPRM packaging path
    /// does). Without one, Jellyfin must load and instantiate the plugin at startup to derive the
    /// manifest before it writes the file itself — and whether the plugin's controllers get wired into
    /// the MVC route table during that first-discovery pass is timing-dependent. On a fast machine it
    /// always wins; on slower/contended CI runners it intermittently loses, leaving the <c>/sso/*</c>
    /// routes returning 404 for the entire server lifetime. Shipping the manifest up front (exactly as a
    /// real install does) makes plugin discovery synchronous and deterministic, removing the race.
    /// </para>
    /// </summary>
    private async Task WriteMetaJsonAsync(CancellationToken ct)
    {
        var buildYaml = Path.Combine(config.RepoRoot, "build.yaml");
        if (!File.Exists(buildYaml))
        {
            throw new OrchestrationException($"Missing {buildYaml}; cannot generate plugin meta.json.");
        }

        var manifest = ParseBuildYaml(await File.ReadAllLinesAsync(buildYaml, ct));

        // Canonicalise the GUID to the lowercase form Jellyfin persists.
        var guid = Guid.Parse(Require(manifest, "guid")).ToString();

        var meta = new
        {
            category = manifest.GetValueOrDefault("category", string.Empty),
            changelog = string.Empty,
            description = manifest.GetValueOrDefault("overview", string.Empty),
            guid,
            name = Require(manifest, "name"),
            overview = manifest.GetValueOrDefault("overview", string.Empty),
            owner = manifest.GetValueOrDefault("owner", string.Empty),
            targetAbi = Require(manifest, "targetAbi"),
            timestamp = "0001-01-01T00:00:00.0000000Z",
            version = manifest.GetValueOrDefault("version", "0.0.0.0"),
            status = "Active",
            autoUpdate = false,
            assemblies = Array.Empty<string>(),
        };

        var metaPath = Path.Combine(config.PublishDir, "meta.json");
        var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(metaPath, json, ct);
        Console.Out.WriteLine($"[+] Wrote plugin meta.json ({meta.name} {guid}).");
    }

    /// <summary>
    /// Minimal parser for the single-line <c>key: "value"</c> scalars in build.yaml. Block scalars
    /// (<c>description</c>, <c>changelog</c>, introduced by <c>|</c>) are intentionally ignored — they
    /// are not needed to load the plugin.
    /// </summary>
    private static Dictionary<string, string> ParseBuildYaml(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(' ') || line.StartsWith('-'))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value.Length == 0 || value == "|")
            {
                continue;
            }

            values[key] = value.Trim('"');
        }

        return values;
    }

    private static string Require(Dictionary<string, string> manifest, string key) =>
        manifest.TryGetValue(key, out var value) && value.Length > 0
            ? value
            : throw new OrchestrationException($"build.yaml is missing required key '{key}'.");
}
