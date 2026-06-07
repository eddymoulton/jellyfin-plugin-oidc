# Building & Releasing

## Dependencies

This project is built with .NET, which manages its own runtime — install the [.NET SDK](https://dotnet.microsoft.com/download) and `dotnet restore` will pull down everything else.

A `package.json` in the repo root provides the formatting tooling (Prettier) for the web assets and docs. Run `npm install` once, then `npm run lint`.

## Building

This is built with .NET 9.0. Build with `dotnet publish .` for the debug release in the `SSO-Auth` directory. Copy over the `IdentityModel.OidcClient.dll`, the `IdentityModel.dll` and the `SSO-Auth.dll` files in the `/bin/Debug/net9.0/publish` directory to a new folder in your Jellyfin configuration: `config/plugins/sso`.

### VSCode Workflow

An example `.vscode` configuration may be found at [strazto/jellyfin-plugin-sso-vscode](https://github.com/strazto/jellyfin-plugin-sso-vscode).

From the root of this repo, you may clone that to `.vscode`

```bash
# From repo root

git clone https://github.com/strazto/jellyfin-plugin-sso-vscode .vscode
```

## Releasing

This plugin uses [JPRM](https://github.com/oddstr13/jellyfin-plugin-repository-manager) to build the plugin. Refer to the documentation there to install JPRM.

Build the zipped plugin with `jprm --verbosity=debug plugin build .`.

### CI Releases

Every change merged to the `main` branch is built and published to the **unstable** manifest by CI.

Formal GitHub releases are built and published to the **stable** manifest by CI.

If you wish to use builds from your own fork, refer to [Installing](../README.md#installing), but change the manifest URLs (`.../manifest-stable/manifest.json` and `.../manifest-unstable/manifest.json`) so they refer to your fork.
