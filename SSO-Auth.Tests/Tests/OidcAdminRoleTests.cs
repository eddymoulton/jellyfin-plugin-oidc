using Jellyfin.Plugin.SSO_Auth.Tests.Fixtures;
using Jellyfin.Plugin.SSO_Auth.Tests.Helpers;
using Xunit;

namespace Jellyfin.Plugin.SSO_Auth.Tests.Tests;

/// <summary>
/// Exercises the OIDC role -> admin permission write path (promote and demote), guarding the
/// 10.11 persistence fix (jellyfin/jellyfin#16298). Each test uses its own dedicated dex account
/// (promote@/demote@), so no DB reset is needed - the accounts aren't shared with other tests and
/// both tests are idempotent regardless of starting state.
/// </summary>
[Collection("Jellyfin")]
public class OidcAdminRoleTests
{
    private readonly JellyfinFixture _fixture;

    public OidcAdminRoleTests(JellyfinFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UserWithAdminRole_NoExistingAccount_IsPromotedToAdmin()
    {
        // promote@test.local has the admin role but no existing account, so the plugin must
        // create a default non-admin user and then promote it from the role.
        var flow = new OidcFlow(_fixture.JellyfinBaseUrl, _fixture.ProviderName);

        var result = await flow.LoginAsync("promote@test.local", "password");

        Assert.True(result.Succeeded, $"Expected success; got denied: {result.DeniedBody}");
        Assert.NotNull(result.User);
        Assert.Equal("promote", result.User!.Name);
        Assert.True(result.User.IsAdministrator);
    }

    [Fact]
    public async Task UserWithoutAdminRole_PreviouslyAdmin_IsDemoted()
    {
        // First login provisions/links the non-admin demote@ account. We make it an admin
        // out-of-band, then log in again with only the jellyfin-users role - which must revoke
        // admin. (The demote account has no admin role, so the first login is always non-admin.)
        using var jf = new JellyfinClient(_fixture.JellyfinBaseUrl, _fixture.Config);
        await jf.AuthenticateAdminAsync();

        var flow = new OidcFlow(_fixture.JellyfinBaseUrl, _fixture.ProviderName);

        var first = await flow.LoginAsync("demote@test.local", "password");
        Assert.True(first.Succeeded, $"First login denied: {first.DeniedBody}");
        Assert.NotNull(first.User);
        Assert.False(first.User!.IsAdministrator);

        await jf.SetAdministratorAsync(Guid.Parse(first.User.Id), true);

        var second = await flow.LoginAsync("demote@test.local", "password");
        Assert.True(second.Succeeded, $"Second login denied: {second.DeniedBody}");
        Assert.NotNull(second.User);
        Assert.False(second.User!.IsAdministrator);
    }
}
