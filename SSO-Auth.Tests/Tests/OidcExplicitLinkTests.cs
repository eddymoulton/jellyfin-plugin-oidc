using Jellyfin.Plugin.SSO_Auth.Tests.Fixtures;
using Jellyfin.Plugin.SSO_Auth.Tests.Helpers;
using Xunit;

namespace Jellyfin.Plugin.SSO_Auth.Tests.Tests;

[Collection("Jellyfin")]
public class OidcExplicitLinkTests : IAsyncLifetime
{
    private readonly JellyfinFixture _fixture;

    public OidcExplicitLinkTests(JellyfinFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetAsync();

    // Reset after the test too: this class persists a canonical link
    // (dex -> bob) in the plugin config that would break later tests
    // (OidcLoginTests.RegularUser expects dex -> "user").
    public Task DisposeAsync() => _fixture.ResetAsync();

    [Fact]
    public async Task LinkFlow_LinksSsoIdentityToExistingLocalUser()
    {
        using var jf = new JellyfinClient(_fixture.JellyfinBaseUrl, _fixture.Config);
        var adminToken = await jf.AuthenticateAdminAsync();

        var bob = await jf.CreateUserAsync("bob", "bob-password");

        var flow = new OidcFlow(_fixture.JellyfinBaseUrl, _fixture.ProviderName);
        var linkResult = await flow.LinkAsync(bob.Id, adminToken, "user@test.local", "password");
        Assert.True(linkResult.Succeeded, $"Link flow denied: {linkResult.DeniedBody}");

        var login = await flow.LoginAsync("user@test.local", "password");
        Assert.True(login.Succeeded, $"Post-link login denied: {login.DeniedBody}");
        Assert.NotNull(login.User);
        Assert.Equal(bob.Id, Guid.Parse(login.User!.Id));
        Assert.Equal("bob", login.User.Name);

        var users = await jf.ListUsersAsync();
        Assert.Equal(2, users.Count);
        Assert.DoesNotContain(users, u => u.Name == "user");
    }
}
