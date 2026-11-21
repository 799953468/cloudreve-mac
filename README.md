# Cloudreve Mac

<img src="src/Cloudreve.Desktop/Assets/cloudreve.png" align="right" width="80" height="80" alt="Cloudreve app icon">

Cloudreve Mac is an unofficial macOS sync client for [Cloudreve](https://github.com/cloudreve/Cloudreve). Pair a folder on your Cloudreve site with a folder on your Mac, then work with your files in Finder. Synced files are stored locally and open offline. No WebDAV connection or mounted network drive is required.

[Installation](#installation) | [Connect a drive](#connect-a-drive) | [Sync behavior](#sync-behavior) | [Report an issue](https://github.com/799953468/cloudreve-mac/issues)

## Features

- **Two-way sync.** Sync files and empty folders in both directions. Local changes and server notifications trigger a scan; periodic scans catch missed events.
- **Multiple drives.** Connect more than one account or site. Each drive has its own local folder, ignore patterns, and transfer queue.
- **Menu bar activity.** See active transfers and recent changes for one drive or all of them. The waiting list shows each file in queue order, with its drive, folder, and next operation: upload, download, or compare. Closing the panel keeps sync running; quitting the app stops it.
- **Resumable transfers.** After a restart, Cloudreve Mac checks unfinished work before retrying it. Uploads resume from confirmed chunks and downloads from verified partial files when the storage backend supports it.

## Installation

The client requires a Cloudreve **4.12.0 or later** site. Choose a remote folder that fits on your Mac: Finder shows full local copies, not on-demand placeholders.

For a packaged build, check [Releases](https://github.com/799953468/cloudreve-mac/releases) for `Cloudreve-osx-arm64.zip` (Apple Silicon) or `Cloudreve-osx-x64.zip` (Intel). Unzip the matching archive and open `Cloudreve.app`. The bundle includes its own .NET runtime. If no package is listed, build from source using the steps below. Project builds are ad-hoc signed, not notarized by Apple.

### Build from source

On a Mac, install the [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0) and Apple's Command Line Tools for Xcode (`xcode-select --install`). The SDK version used by the project is recorded in [global.json](global.json).

```sh
git clone https://github.com/799953468/cloudreve-mac.git
cd cloudreve-mac

rid=osx-arm64  # Use osx-x64 on an Intel Mac.
dotnet publish src/Cloudreve.Desktop -c Release -r "$rid" \
  --self-contained true -p:MacBundle=true
open "artifacts/$rid/Cloudreve.app"
```

## Connect a drive

1. In **Add Drive**, enter the site's root URL (for example, `https://drive.example.com`) and choose **Connect**. Use HTTPS to protect your tokens and files in transit.
2. Sign in on the site in your browser, choose the remote folder to authorize, and allow the browser to reopen Cloudreve. Leave the app running during sign-in. The app never asks for your site password.
3. In **Final Setup**, name the drive, choose an **empty local folder**, and select **Finish**. Click the Cloudreve menu bar icon to follow the first sync.

> [!IMPORTANT]
> Deleting a synced file in Finder is **not** a way to free up local space: the app also asks Cloudreve to move the unchanged remote copy to its trash. For occasional access without keeping a local copy, use the web interface instead.

## Sync behavior

| When you... | What happens |
| --- | --- |
| Edit the same file on both sides | If the contents differ, the app reports a conflict and leaves both versions in place. Back up both copies before resolving it manually. |
| Delete a file on the site, or replace it with a newer version | The old local copy is kept in `.cloudreve-recovery/` inside the paired folder. This folder is never uploaded or automatically cleared. Press `Command+Shift+.` in Finder to see it. |
| Delete a synced subfolder in Finder | The app requests that the matching remote folder be moved to the site's trash. If it finds new or changed remote content or ignored items, it leaves the folder in place and reports a conflict. |
| Rename a file or move it without changing its name | When the app can identify the unchanged file, it applies the move without uploading its contents again. Whole-folder renames and moves aren't supported. |
| Exclude files | In **Settings > Drives > Ignore Patterns**, enter one rule per line, such as `*.tmp` or `/build/`. Rules apply to both sides without deleting existing files. |
| Remove a drive from Settings | Sync stops for that drive. Local and remote files stay in place. Removing the drive from the Mac does not revoke authorization on the site. |

<details>
<summary>How scans and retries work</summary>

At startup and during routine sync, the app checks local files and lists remote folders in parallel, reading each folder's pages in order. It waits for complete scans on both sides and rechecks the paired root before transferring or deleting anything.

Local scans, remote scans, and transfers each start with two concurrent operations per drive and can grow to six while work is waiting. Rate limits, service-unavailable responses, or request timeouts stop the current pass and halve the affected limit, down to one. The next pass scans again. Folder changes, moves, deletions, and chunks within a single upload stay sequential.

</details>

## Troubleshooting

- **The browser doesn't return to the app.** Launch the built `Cloudreve.app` rather than using `dotnet run`. Start **Connect** again and keep that instance open until sign-in finishes.
- **A transfer keeps failing.** Check the activity panel and `~/Library/Logs/Cloudreve/desktop-*.log`. File logging can be configured in **Settings > General**.
- **Reporting a problem?** Don't share `drives.json` files or the `folder-pairs` transfer cache under `~/Library/Application Support/Cloudreve/`. The session files store access and refresh tokens in plaintext, not in Keychain. Other programs running as your macOS user can read them.

## Contributing

For a sync bug, [open an issue](https://github.com/799953468/cloudreve-mac/issues) with your macOS and Cloudreve versions, which side changed first, and what you expected to happen. Use sample filenames and omit credentials.

The [desktop app](src/Cloudreve.Desktop) is written in C# with Avalonia. [Cloudreve.Sync](src/Cloudreve.Sync) is a separate local-directory tool; the desktop app does not invoke it. Run the [test suite](tests/Cloudreve.Sync.Tests) from the repository root:

```sh
dotnet test --solution CloudreveMac.slnx -c Release
```

The tests use temporary folders, simulated server responses, and headless windows. They do not use a live Cloudreve account. Check changes to sign-in or storage-provider behavior against a real site as well.

### CI and release builds

- [Test](.github/workflows/test.yml) runs the Release test suite on macOS for branch pushes and pull requests.
- [Release build](.github/workflows/release.yml) tests and packages both Mac architectures on a version tag (such as `v1.2.3` or `v1.2.3-rc.1`), a published release, or a manual run. Tag builds take the app version from the tag; manual branch builds use the source version.
- Tag pushes and manual runs produce a `Cloudreve-macOS` workflow artifact without creating a release. Publishing a mutable release attaches missing ZIPs without overwriting existing ones. For an immutable release, attach both ZIPs from the tag build to the draft before publishing.

## Licensing and credits

Artwork and upstream interface translations come from [Cloudreve Desktop](https://github.com/cloudreve/desktop) and are covered by its [MIT notice](src/Cloudreve.Desktop/Assets/Cloudreve-LICENSE.txt). The bundled Roboto font uses the [SIL Open Font License 1.1](src/Cloudreve.Desktop/Assets/Fonts/OFL.txt). These notices cover those assets; the repository does not currently include a project-wide license.
