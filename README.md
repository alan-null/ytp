# ytp

`ytp` is a small HTTP proxy around [yt-dlp](https://github.com/yt-dlp/yt-dlp). It resolves a YouTube video ID to a playable best-audio URL. Audio can be pre-downloaded into a local cache and served by `ytp` itself.

```
GET http://<host>:497/bestAudio?v=<videoId>
-> { "url": "https://..." }
```

- **Cached:** the URL points at `ytp`'s own `/audio/<videoId>`.
- **Not cached:** the URL is a direct googlevideo URL (kept in memory while it stays valid), the same as a plain proxy. When yt-dlp fails, or YouTube offers only an HLS (m3u8) playlist and no direct audio file, the response is `502`, never a URL.

Audio is cached only on demand, through `POST /prefetch`. Setting `Cache:DownloadOnMiss` to `true` also queues a background download after each miss.

## Endpoints

| Method | Path | Description |
|---|---|---|
| `GET` | `/bestAudio?v=<id>` | Playable URL as `{ "url": ... }` |
| `GET`, `HEAD` | `/audio/<id>` | Cached audio; supports HTTP `Range` (seeking). `404` when not cached |
| `POST` | `/prefetch` | JSON array of IDs to download in the background (max 500). Returns `202` with the IDs grouped as `queued`, `alreadyQueued`, `cached`, `invalid`, `queueFull` |
| `GET` | `/cache` | Cache entries, total size and pending download count |
| `DELETE` | `/cache/<id>` | Remove an entry (`204`, or `404` when missing) |

IDs must be 11-character YouTube video IDs (`[A-Za-z0-9_-]`).

```bash
curl -H 'Content-Type: application/json' -d '["dQw4w9WgXcQ"]' http://localhost:497/prefetch
```

## yt-dlp

| Setting | Default | Meaning |
|---|---|---|
| `YtDlp:Path` | `yt-dlp` | A bare name is looked up on `PATH`, as a shell does. A path containing `/` is used as given, e.g. `/usr/local/bin/yt-dlp` |

No other locations are searched (not the working directory or the binary's directory). The resolved path and version are logged at startup (`Using yt-dlp <path> (<version>)`). Install yt-dlp where executables normally live (e.g. `/usr/local/bin`) and keep a single copy.

## Cache

The cache is stored on local disk as `<id>.data` plus an `<id>.json` metadata sidecar. Writes are atomic (temp file + rename). A background janitor removes entries idle longer than `MaxIdle`, then the least recently used until the cache is under `MaxBytes`. Settings live in the `Cache` section of `appsettings.json`; each one can also be set from the environment, e.g. `Cache__MaxBytes=10737418240`.

| Setting | Default | Meaning |
|---|---|---|
| `Backend` | `FileSystem` | `FileSystem` (local disk) or `S3` (Backblaze B2 or another S3-compatible bucket, see below) |
| `Directory` | `cache` | Cache directory. Services should set an absolute path, e.g. `/var/cache/ytp`; a relative path resolves against the working directory |
| `MaxBytes` | 5 GiB | Size the janitor trims down to |
| `MaxIdle` | `30.00:00:00` | Entries not read for this long are evicted |
| `CleanupInterval` | `00:10:00` | How often the janitor runs |
| `DownloadOnMiss` | `false` | Also queue a background download when `/bestAudio` misses |
| `MaxConcurrentDownloads` | `2` | Parallel yt-dlp downloads |
| `QueueCapacity` | `1000` | Pending downloads accepted before `/prefetch` reports `queueFull` |
| `PublicBaseUrl` | derived from request | Base URL for `/audio` links, e.g. behind a reverse proxy |

Storage sits behind `IStreamCache` (`ytp/Cache/IStreamCache.cs`), with two implementations: `FileSystemStreamCache` and `S3StreamCache`.

### Backblaze B2 (S3-compatible) backend

With `Cache:Backend` set to `S3`, entries live in a bucket instead of on disk. It uses B2's S3-compatible API, so Cloudflare R2 and MinIO work too. `MaxBytes`, `MaxIdle` and the janitor behave the same as on disk.

- **Objects:** `<Prefix>media/<id>` holds the audio, with its `Content-Type`. `<Prefix>access/<id>` is an empty marker whose timestamp is the last access, because object stores don't track reads.
- **Hits:** `/bestAudio` returns a presigned bucket URL valid for 6 hours, so the audio never passes through ytp. `/audio/<id>` redirects to the same URL. With `PresignedUrls=false`, ytp streams the object itself, and range requests are passed through to the bucket.

B2 setup:

1. Create a **private** bucket. Under *Lifecycle Settings* choose **Keep only the last version of the file**. B2 keeps every version by default, so without this, deleted or evicted entries and rewritten access markers keep using storage.
2. Create an application key restricted to that bucket with read and write access. Its *keyID* is the access key and its *applicationKey* is the secret.
3. Take the endpoint from the bucket details, e.g. `s3.eu-central-003.backblazeb2.com`. The middle part, `eu-central-003`, is the region.
4. Configure ytp. Put the secrets in the environment, e.g. a systemd `EnvironmentFile=` readable only by the service:

```ini
Cache__Backend=S3
Cache__S3__ServiceUrl=https://s3.eu-central-003.backblazeb2.com
Cache__S3__Region=eu-central-003
Cache__S3__Bucket=my-ytp-cache
Cache__S3__AccessKeyId=<keyID>
Cache__S3__SecretAccessKey=<applicationKey>
# 9 GiB, under the 10 GB free tier
Cache__MaxBytes=9663676416
Cache__CleanupInterval=01:00:00
```

| `Cache:S3` setting | Default | Meaning |
|---|---|---|
| `ServiceUrl` | – | S3 endpoint, e.g. `https://s3.eu-central-003.backblazeb2.com` |
| `Region` | `us-east-1` | Signing region; for B2 the middle part of the endpoint |
| `Bucket` | – | Bucket name |
| `AccessKeyId`, `SecretAccessKey` | – | Credentials; set them from the environment, not `appsettings.json` |
| `Prefix` | empty | Prefix for all objects, e.g. `ytp/`, so the bucket can be shared |
| `PresignedUrls` | `true` | Give clients presigned bucket URLs; `false` streams through ytp |
| `AccessUpdateInterval` | `01:00:00` | Rewrite an entry's access marker at most this often |
| `ForcePathStyle` | `false` | Bucket in the URL path; needed for MinIO |

B2 free-tier limits:

- Storage: 10 GB.
- Download traffic: free up to 3× the stored amount per month.
- Daily free API calls: 2,500 class B (`HEAD`/`GET`) and 2,500 class C (list). Uploads and deletes are free.
- What ytp uses:
  - Each janitor run lists the bucket, which costs class C calls, hence the hourly `CleanupInterval` above.
  - Each `/bestAudio` hit costs one `HEAD` (class B).
  - After a restart, each entry costs one extra `HEAD` the first time it's listed.

## Prerequisites

Build machine:
- .NET 8 SDK (or newer; the project targets `net8.0`)

Server:
- No .NET installation required (the binary is self-contained)
- `yt-dlp` on `PATH`, or its location set in `YtDlp:Path`

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
