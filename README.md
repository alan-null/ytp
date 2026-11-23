# ytp

`ytp` is a small HTTP proxy around [yt-dlp](https://github.com/yt-dlp/yt-dlp). It resolves a YouTube video ID to a direct best-audio stream URL and caches the result in memory.

```
GET http://<host>:497/bestAudio?v=<videoId>
-> { "url": "https://..." }
```

## Prerequisites

Build machine:
- .NET 8 SDK (or newer; the project targets `net8.0`)

Server:
- No .NET installation required (the binary is self-contained)
- `yt-dlp` on `PATH`

## Build

```bash
dotnet restore ytp.sln
dotnet build ytp.sln -c Release
```

## Publish

```bash
scripts/publish linux-x64 1.0.0
```

Arguments are optional: `scripts/publish [RID] [VERSION]` (defaults: `linux-x64`, `0.0.0-local`). The script runs a self-contained, single-file `dotnet publish` into `artifacts/<RID>/`:
- `ytp`: the executable
- `appsettings.json`: optional configuration (logging); it is read from the binary's directory

Development-only files (`appsettings.Development.json`, `web.config`, static web asset manifests) are removed from the output. CI uses the same script.

To package binaries the same way a release does:

```bash
scripts/package 1.0.0 linux-x64 linux-arm64
```

This writes `dist/ytp-<RID>.tar.gz` (`ytp`, `appsettings.json`, `VERSION`) and `dist/SHA256SUMS`.

## CI And Releases (GitHub Actions)

| Workflow | Trigger | What it does |
|---|---|---|
| `ci.yml` | pull request, push to `master`, manual | Restore and build. On `master` pushes, also publishes a `linux-x64` snapshot as a workflow artifact (kept 14 days). |
| `release.yml` | push of a `v*.*.*` tag | Runs `ci.yml`, publishes `linux-x64` and `linux-arm64`, packages them, and creates a GitHub Release with generated notes. |

### Cutting a release

From an up-to-date `master` with green CI:

```bash
git tag v1.0.0
```

```bash
git push origin v1.0.0
```

- The version is taken from the tag without the `v` (`v1.0.0` → `1.0.0`) and stamped into the binary and the `VERSION` file.
- A tag with a suffix, e.g. `v1.1.0-rc.1`, is published as a **pre-release** and never becomes "latest".
- If the release job fails, fix the problem, delete the tag (`git push --delete origin v1.0.0`, `git tag -d v1.0.0`) and tag again.

Release assets (names are stable across versions):
- `ytp-linux-x64.tar.gz`
- `ytp-linux-arm64.tar.gz`
- `SHA256SUMS`

### Downloading a release on a server

If the repository is private, downloads need a token: use a fine-grained personal access token limited to this repository with **Contents: read-only**.

```bash
export GH_TOKEN=<token>
gh release download --repo alan-null/YT-DLP-Proxy -p "ytp-linux-x64.tar.gz" -p SHA256SUMS
```

```bash
sha256sum --ignore-missing -c SHA256SUMS && mkdir -p ytp && tar -xzf ytp-linux-x64.tar.gz -C ytp
```

Without a tag argument, `gh release download` takes the latest non-pre-release. Compare `ytp/VERSION` with the deployed copy to decide whether an update is needed.

## Run On Server

RID mapping (`uname -m`, glibc distro such as Debian/Ubuntu): `x86_64` → `linux-x64`, `aarch64` → `linux-arm64`.

`ytp` listens on port **497**, which is below 1024, so a non-root user needs the bind capability:

```bash
sudo setcap 'cap_net_bind_service=+ep' ytp/ytp
```

```bash
./ytp/ytp
```
