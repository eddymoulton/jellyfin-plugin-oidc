using Jellyfin.Plugin.SSO_Auth.Tests.Fixtures;
using Jellyfin.Plugin.SSO_Auth.Tests.Helpers;
using Xunit;

namespace Jellyfin.Plugin.SSO_Auth.Tests.Tests;

/// <summary>
/// Covers role-based folder access (enableAllFolders=false + folderRoleMapping) - the only path
/// that converts folder ids string[] -> Guid[] in the permission write. Uses the statically
/// provisioned dex-folderroles provider (which maps jellyfin-users to the "Movies" library) and a
/// dedicated folderuser@ account, so no provider mutation or DB reset is needed.
/// </summary>
[Collection("Jellyfin")]
public class OidcFolderRoleTests
{
    private const string FolderRoleProvider = "dex-folderroles";

    private readonly JellyfinFixture _fixture;

    public OidcFolderRoleTests(JellyfinFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FolderRoles_GrantOnlyMappedLibrary()
    {
        using var jf = new JellyfinClient(_fixture.JellyfinBaseUrl, _fixture.Config);
        await jf.AuthenticateAdminAsync();
        var folders = await jf.GetMediaFoldersAsync();
        var nameById = folders.ToDictionary(f => f.Id, f => f.Name);

        var flow = new OidcFlow(_fixture.JellyfinBaseUrl, FolderRoleProvider);
        var result = await flow.LoginAsync("folderuser@test.local", "password");
        Assert.True(result.Succeeded, $"Login denied: {result.DeniedBody}");
        Assert.NotNull(result.User);

        var policy = await jf.GetFolderPolicyAsync(Guid.Parse(result.User!.Id));
        Assert.False(policy.EnableAllFolders);

        var grantedNames = policy.EnabledFolders
            .Select(id => nameById.TryGetValue(id, out var name) ? name : id.ToString());
        Assert.Equal(new[] { "Movies" }, grantedNames);
    }
}
