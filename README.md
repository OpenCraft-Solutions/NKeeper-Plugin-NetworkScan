# NKeeper network scan plugin

The official `network-scan` plugin for [NKeeper](https://github.com/OpenCraft-Solutions/NKeeper) 2.0: 1.x's external scan of a host and network scan of a subnet. It lives in a repository of its own and is released on its own, so releasing it isn't a release of NKeeper (NKeeper's `docs/architecture.md` §9). Its design is §7.42 there, on the host-wide scanners and inventory tools of §7.41.

## What it does

It probes from the NKeeper server itself, with its own sockets, and carries no credential. Plugins run in-process, which isn't a sandbox, so both scans are limited by what they may reach:

**The external scan** (`network-scan.host`, in a host's Scan dialog, for someone who can manage the host): the host's own address only, its IP address or its hostname resolved once. It finds the open TCP ports among 1.x's common ones and the host's own service ports, the SSH banner and the operating system it suggests, the name reverse DNS gives, and the MAC address when the host is on the server's network segment. The shell proposes the changes, as for any scan. It's audited as `network-scan.host`, and at most four run at once.

**The network scan** (**Scan network**, above the host tree, for administrators): every address in an IPv4 subnet, a /20 at most, that the administrator enters or picks from the server's own networks.
- It's loud, so it must be acknowledged first, and is audited as `network-scan.subnet`. Only one runs at a time on the server; it can be stopped, and is kept for its administrator for two hours after it ends.
- An address is a machine when it answers a ping, answers or refuses a TCP connection on a discovery port, or resolves in the neighbour table. For each, it reads the open ports, the SSH banner, web servers' titles, headers and certificate names, the Remote Desktop certificate's names, the NetBIOS name and workgroup, reverse DNS, the ping's TTL and the MAC address's vendor.
- The dialog matches each machine to an NKeeper host (by MAC address, else IP address, else hostname), and adds the ones the administrator ticks through core's own `POST /api/hosts`, with a service for each open port whose type is known.

An address with Remote Desktop open is also asked for its certificate, as 1.x did: the RDP negotiation, then TLS only to read it. Its names count among the machine's.

The plugin's ID is `network-scan`, and it is built in: it is signed with NKeeper's official key and installed with NKeeper. It can be turned off (§9.2). It needs SDK 1.14 (`>=1.14.0 <2.0.0`).

## Layout

```text
backend/        the plugin: NetworkScanPlugin.cs (the scanner and endpoints), ExternalScan.cs, NetworkScan.cs (the subnet scan),
                NetworkProbes.cs, ScanPorts.cs, Subnet.cs, NeighborTable.cs and AddressText.cs (1.x's), nkeeper-plugin.json, the project
frontend/       the Scan network dialog, and matching (scan.ts): a Vite library build to dist/index.js
tests/          the backend tests, which start NKeeper's test server with this plugin loaded
.github/        CI and the release workflow
```

It builds against NKeeper's published SDK, never core's source:

| Package | Registry | Used for |
| --- | --- | --- |
| `NKeeper.Abstractions` | GitHub Packages (NuGet) | the backend SDK |
| `NKeeper.Plugin.Build` | GitHub Packages (NuGet) | builds, packs and signs the plugin |
| `NKeeper.Testing` | GitHub Packages (NuGet) | the test server and PostgreSQL fixture |
| `@opencraft-solutions/nkeeper-sdk` | GitHub Packages (npm) | the frontend SDK, imported as `@nkeeper/sdk` |

## Building

You need the .NET 10 SDK (see `global.json`), Node.js 24 (see `.nvmrc`) and Docker for the tests.

GitHub Packages needs a token even for public packages. Make a GitHub personal access token that can read packages (`read:packages`) and give it to the build in the environment. It is never written to the repository:

```bash
export NUGET_USERNAME=<your GitHub user name>
export NUGET_AUTH_TOKEN=<token>
export NODE_AUTH_TOKEN=<token>
```

Then, from the repository root:

```bash
(cd frontend && npm ci && npm run lint && npm run typecheck && npm test)
dotnet restore NKeeper.Plugins.NetworkScan.slnx
dotnet build NKeeper.Plugins.NetworkScan.slnx --configuration Release
```

Building the plugin builds its frontend, packs `network-scan-<version>.nkp` into `artifacts/plugins/` and signs it with a development key, which the build creates under `.dev/keys/` on first use. Both are ignored by git. To load the package in a development NKeeper, point `Plugins:Directory` at `artifacts/plugins` and `Plugins:DevelopmentTrustDirectory` at `.dev/keys`.

Pinned versions: NuGet packages are locked in `packages.lock.json` (CI restores with `--locked-mode`), and npm packages in `frontend/package-lock.json`. Run `dotnet restore --force-evaluate` after changing a version.

## Testing

```bash
dotnet test NKeeper.Plugins.NetworkScan.slnx --configuration Release
```

The tests need Docker, for PostgreSQL. They start NKeeper's test server (from `NKeeper.Testing`) with this plugin's built package, and scan the loopback address only, where they open a port of their own. They cover:
- the host-wide scanner's contribution, and an external scan finding the open port, audited;
- the network scan's checks (acknowledged, at most a /20, administrators only), a scan of loopback, and its audit;
- the subnet parser, the NetBIOS, web, SSH banner and neighbour-table parsers, and the MAC vendors, ported from 1.x's tests.

The frontend's tests (`npm test`) cover matching found machines to hosts, and the hosts the dialog adds.

## Continuous integration

`.github/workflows/ci.yml` runs on every push to `main` and every pull request: it restores from GitHub Packages with the workflow's token, lints, type-checks and tests the frontend, checks formatting, builds, runs the tests and packs the plugin signed with a development key, which it keeps as a build artifact.

## Releasing

1. Raise `version` in `backend/nkeeper-plugin.json` (and keep `frontend/package.json` in step), and merge it to `main`.
2. Tag the merge commit `v<version>`, for example `v0.2.0`, and push the tag.
3. `.github/workflows/release.yml` runs. It checks that the tag matches the manifest's version and is on `main`, builds and tests, and then, in the protected `release` environment, which needs an owner's approval, packs `network-scan-<version>.nkp` signed with the official key and publishes a GitHub release with the package and its SHA-256.

The official key's private half, `NKEEPER_RELEASE_KEY` (a PEM), is a secret of the `release` environment. It is available only to the signing job, goes to memory-backed storage for the packing step alone, and is removed when that step ends. It is never in the repository or in a log.

NKeeper's installer picks the release up when its `deploy/plugins.lock` is updated with the new URL and SHA-256.

## License

MIT, as NKeeper. See [LICENSE](LICENSE).
