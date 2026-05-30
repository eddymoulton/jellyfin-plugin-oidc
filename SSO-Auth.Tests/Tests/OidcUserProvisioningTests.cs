using Jellyfin.Plugin.SSO_Auth.Tests.Fixtures;
using Jellyfin.Plugin.SSO_Auth.Tests.Helpers;
using Xunit;

namespace Jellyfin.Plugin.SSO_Auth.Tests.Tests;

[Collection("Jellyfin")]
public class OidcUserProvisioningTests : IAsyncLifetime
{
    private readonly JellyfinFixture _fixture;

    public OidcUserProvisioningTests(JellyfinFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task FirstLoginAsUnknownUser_CreatesJellyfinUser()
    {
        using var jf = new JellyfinClient(_fixture.JellyfinBaseUrl, _fixture.Config);
        await jf.AuthenticateAdminAsync();

        var before = await jf.ListUsersAsync();
        Assert.Single(before);
        Assert.Equal("admin", before[0].Name);

        var flow = new OidcFlow(_fixture.JellyfinBaseUrl, _fixture.ProviderName);
        var result = await flow.LoginAsync("user@test.local", "password");

        Assert.True(result.Succeeded, $"Expected success; got denied: {result.DeniedBody}");
        Assert.NotNull(result.User);
        Assert.Equal("user", result.User!.Name);

        var after = await jf.ListUsersAsync();
        Assert.Equal(2, after.Count);
        var newUser = Assert.Single(after, u => u.Name == "user");
        Assert.Equal(newUser.Id, Guid.Parse(result.User.Id));
    }

    [Fact]
    public async Task FirstLoginMatchingExistingUsername_LinksWithoutCreating()
    {
        using var jf = new JellyfinClient(_fixture.JellyfinBaseUrl, _fixture.Config);
        await jf.AuthenticateAdminAsync();

        var before = await jf.ListUsersAsync();
        var existingAdmin = Assert.Single(before);
        Assert.Equal("admin", existingAdmin.Name);

        var flow = new OidcFlow(_fixture.JellyfinBaseUrl, _fixture.ProviderName);
        var result = await flow.LoginAsync("admin@test.local", "password");

        Assert.True(result.Succeeded, $"Expected success; got denied: {result.DeniedBody}");
        Assert.NotNull(result.User);
        Assert.Equal(existingAdmin.Id, Guid.Parse(result.User!.Id));

        var after = await jf.ListUsersAsync();
        Assert.Single(after);
    }

    [Fact]
    public async Task RepeatLoginAsSameSsoIdentity_ReturnsSameUser()
    {
        var flow = new OidcFlow(_fixture.JellyfinBaseUrl, _fixture.ProviderName);

        var first = await flow.LoginAsync("user@test.local", "password");
        Assert.True(first.Succeeded, $"First login denied: {first.DeniedBody}");
        Assert.NotNull(first.User);
        var firstId = first.User!.Id;

        var second = await flow.LoginAsync("user@test.local", "password");
        Assert.True(second.Succeeded, $"Second login denied: {second.DeniedBody}");
        Assert.NotNull(second.User);

        Assert.Equal(firstId, second.User!.Id);

        using var jf = new JellyfinClient(_fixture.JellyfinBaseUrl, _fixture.Config);
        await jf.AuthenticateAdminAsync();
        var after = await jf.ListUsersAsync();
        Assert.Equal(2, after.Count);
    }
}
