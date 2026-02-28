# Build Instructions

## Prerequisites

### Install .NET 9 SDK (macOS via Homebrew)
```sh
brew install dotnet@9
export PATH="/opt/homebrew/opt/dotnet@9/bin:$PATH"
```

To make this permanent, add the export to your shell profile (`~/.zshrc` or `~/.bash_profile`):
```sh
echo 'export PATH="/opt/homebrew/opt/dotnet@9/bin:$PATH"' >> ~/.zshrc
```

Verify:
```sh
dotnet --version  # should print 9.x.x
```

---

## Build

```sh
dotnet build
```

Output DLL: `bin/Debug/net9.0/Jellyfin.Plugin.TimeLimiter.dll`

### Release build
```sh
dotnet build -c Release
```

Output DLL: `bin/Release/net9.0/Jellyfin.Plugin.TimeLimiter.dll`

---

## Deploy to Jellyfin

Jellyfin runs on `root@jellyfin` (SSH).

```sh
ssh root@jellyfin "mkdir -p /var/lib/jellyfin/plugins/TimeLimiter_1.0.0.0"
scp bin/Release/net9.0/Jellyfin.Plugin.TimeLimiter.dll root@jellyfin:/var/lib/jellyfin/plugins/TimeLimiter_1.0.0.0/
ssh root@jellyfin "chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/TimeLimiter_1.0.0.0 && systemctl restart jellyfin"
```

> **Note:** the `chown` is required because `scp` as root creates files owned by root, but Jellyfin runs as the `jellyfin` user and needs write access to create `meta.json`.

---

## Verify Plugin Loaded

1. Open Jellyfin Dashboard → **Plugins**
2. "Time Limiter" should appear under the General category
3. Click it to open the configuration page
