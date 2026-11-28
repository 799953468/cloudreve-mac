# Cloudreve Client

<img src="src/Cloudreve.Desktop/Assets/cloudreve.png" align="right" width="80" height="80" alt="Cloudreve app icon">

Cloudreve Client is an unofficial macOS and Windows sync client for [Cloudreve](https://github.com/cloudreve/Cloudreve). Pair a folder on your Cloudreve site with a folder on your computer, then work with your files in Finder or File Explorer. Synced files are stored locally and open offline. No WebDAV connection or mounted network drive is required.

[Installation](#installation) | [Connect a drive](#connect-a-drive) | [Sync behavior](#sync-behavior) | [Report an issue](https://github.com/799953468/cloudreve-mac/issues/new/choose)

## Features

- **Two-way sync.** Sync files and empty folders in both directions. Local changes and server notifications trigger a scan. The app polls if either watcher is unavailable.
- **Multiple drives.** Connect more than one account or site. Each drive has its own local folder, ignore patterns, and transfer queue.
- **Menu bar activity.** See active transfers and recent changes for one drive or all of them. The waiting list shows each file in queue order, with its drive, folder, and next operation: upload, download, or compare. Closing the panel keeps sync running; quitting the app stops it.
- **Resumable transfers.** After a restart, Cloudreve Client checks unfinished work before retrying it. Uploads resume from confirmed chunks and downloads from verified partial files when the storage backend supports it.

## Installation

The client requires a Cloudreve **4.12.0 or later** site. Choose a remote folder that fits on your Mac: Finder shows full local copies, not on-demand placeholders.

For a packaged build, check [Releases](https://github.com/799953468/cloudreve-mac/releases) for `Cloudreve-osx-arm64.zip` (Apple Silicon) or `Cloudreve-osx-x64.zip` (Intel). Unzip the matching archive and open `Cloudreve.app`. The bundle includes its own .NET runtime. If no package is listed, build from source using the steps below. Project builds are ad-hoc signed, not notarized by Apple.

### Windows

Cloudreve Client also runs on Windows 10 (1809) or later. Download `Cloudreve-win-x64.zip` (or `Cloudreve-win-arm64.zip` for Arm PCs) from Releases, extract the `Cloudreve` folder to a permanent location such as `%LOCALAPPDATA%\Programs\Cloudreve`, and run `CloudreveClient.exe`. The app lives in the notification area: click the cloud icon for activity, right-click it for the menu. Builds are not code-signed, so SmartScreen may ask you to confirm the first launch.

- Choosing **Connect** registers the `cloudreve://` link for your Windows account so the browser can return to this app. If you also use another Cloudreve desktop client, its sign-in links will open this app until that client registers the link again.
- **Settings > General > Launch at startup** adds an entry under **Settings > Apps > Startup**. If you move the folder, turn the switch off and on again.
- Settings, tokens, and logs are stored in `%LOCALAPPDATA%\CloudreveClient`.
- Files and folders whose cloud names Windows cannot store (for example names containing `:` `?` `"`, ending in a dot or space, or reserved names such as `CON`) are reported as conflicts and not synced.

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

To build for Windows, install the .NET 11 SDK and run the following in PowerShell. You can also cross-build Windows packages from a Mac with the same command.

```powershell
dotnet publish src/Cloudreve.Desktop -c Release -r win-x64 --self-contained true -o artifacts/win-x64/Cloudreve
.\artifacts\win-x64\Cloudreve\CloudreveClient.exe
```

On macOS 13 or later, turn on **Settings > General > Launch at startup** in `Cloudreve.app` to open it automatically when you log in. If macOS asks for approval, allow Cloudreve in **System Settings > General > Login Items**. Turn the same switch off to remove the login item.

## Connect a drive

1. In **Add Drive**, enter the site's root URL (for example, `https://drive.example.com`) and choose **Connect**. Use HTTPS to protect your tokens and files in transit.
2. Sign in on the site in your browser, choose the remote folder to authorize, and allow the browser to reopen Cloudreve. Leave the app running during sign-in. The app never asks for your site password.
3. In **Final Setup**, name the drive, choose a local folder, and select **Finish**. You can choose a folder that already contains files. The first sync uploads local-only files and downloads cloud-only files. If files at the same relative path have different contents, the app keeps both versions in place and reports a conflict. Click the Cloudreve menu bar icon to follow the first sync.

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

Click the Cloudreve menu bar icon to see which drives are scanning. Each drive shows whether it is checking the local or cloud folder and how many items it has found on that side. A failed scan stays visible until the next attempt.

After a successful pass, a drive with working local and cloud notifications waits for a change without periodically rescanning. If either watcher is unavailable, it polls after 30 seconds instead. A failed pass waits 30 seconds before retrying, even if more notifications arrive. If a local file changes while it is being transferred, the app skips only that file, finishes the rest of the pass, and rechecks that path 30 seconds later instead of rescanning the whole pair. The debug scan summary counts these as `DeferredFiles`.

At startup the app scans the paired folders in full. During routine sync it groups local and cloud notifications by path. Editing a file checks that file and lists its parent directories. A directory notification covers its subtree, and a rename checks both the old and new paths. The app rechecks both paired roots before applying changes.

At startup, after push notifications reconnect, or after the local watcher loses events, the app catches up instead of listing every cloud folder: it walks the local folder (unchanged files reuse their digests) and compares it with the last sync, searches the cloud for items modified since the last complete pass, and checks the cloud trash for items deleted since then. A trashed item that still has the identity the app knew is treated as a deletion; a moved or renamed item is followed by its identity. Only those paths are then synced. The app falls back to a full scan when it has no record of a previous complete pass, that record is more than 7 days old, the search fails or finds more than 4,096 changes, or old sync history is being migrated. Because the cloud search cannot see permanently deleted items, an emptied trash, or items moved out of the paired folder, a full scan still runs in the background 10 minutes after a catch-up (unless one ran within the last 12 hours) and every 12 hours after that.

A change to ignore rules or an uncertain move also triggers a full scan. For example, when a tracked file disappears and the batch contains no matching destination, the app checks the rest of the pair before propagating the deletion. At Debug log level, the scan summary includes `ScanScope` (`Paths`, `CatchUp` or `Full`). A catch-up records `CatchUpReason` (`Startup`, `RemoteCatchUp`, `LocalWatcherOverflow`), and a full scan records `FullScanReason`, for example `Startup:NoChangeCursor`, `ScheduledVerification`, `TooManyChangedPaths`, `RetryAfterFailedFullScan`, or `Escalated:LocalItemDisappeared` when a path scan had to widen.

Local scans, remote scans, and transfers each start with two concurrent operations per drive and can grow to six while work is waiting. Rate limits, service-unavailable responses, or request timeouts stop the current pass and halve the affected limit, down to one. The next pass scans again. Folder changes, moves, deletions, and chunks within a single upload stay sequential.

</details>

## Troubleshooting

- **The browser doesn't return to the app.** Launch the built `Cloudreve.app` rather than using `dotnet run`. Start **Connect** again and keep that instance open until sign-in finishes.
- **A transfer keeps failing.** Check `Synchronize folder pair failed` in `~/Library/Logs/Cloudreve/desktop-*.log` (Windows: `%LOCALAPPDATA%\CloudreveClient\Logs`). `PairId` identifies the pair without exposing its paths. `Stage` and `ElapsedMs` identify what the scan was doing and how long it ran. For HTTP failures, `HttpStatus: null` means the exception carried no HTTP status code; look at `HttpRequestError`, `SocketError`, and `Causes` instead. A numeric `CloudreveCode` is an application error, not an HTTP status. Warnings include these details by default; **Settings > General > Log level > Debug** also records successful scan counts and timing.
- **Files differ but nothing syncs.** When a path exists on both sides with different contents and no sync history (for example, the first sync of a folder that already has files on both computers), or both sides changed it, the app does not pick a winner. The activity window lists these under **Conflicts**, where you can keep the local version, keep the cloud version, or keep both (the local version is saved as a `local-copy-…` file). The footer shows the conflict count instead of "up to date". With **Settings > General > Keep the newer version in conflicts** (on by default), a file that differs on both sides is resolved automatically when one side was modified more than 2 seconds after the other: a newer local file is uploaded, and a newer cloud file is downloaded after the local version is moved to the recovery folder. Files whose times are within 2 seconds, or whose cloud time is unavailable, stay in the list; so do deletions on one side. Each new conflict is logged as a warning, `Sync conflict at {ConflictKey}: {ConflictReason}`, with the path relative to the paired folder; the paired root itself is never logged.
- **A folder was deleted and recreated, for example by a build.** A folder that reappears at the same path on the same volume, while its cloud folder is unchanged, is treated as the same folder, and its contents sync file by file: unchanged files are not transferred again, new files are uploaded, and files missing from the new folder are moved to the cloud trash. If the old folder still exists elsewhere in the pair (a move), the cloud folder was replaced too, or the pass is migrating old baseline IDs, the folder stays a conflict and nothing below it is changed. A path scan that meets such a folder escalates once to a full scan (`FullScanReason: Escalated:LocalDirectoryRecreated`), because only a full scan can rule out a move. Build output is still better excluded with ignore rules such as `bin/`, `obj/`, or `node_modules/`.
- **A local directory changed since the scan.** If a directory recheck detects a change, the app defers that subtree and continues syncing unrelated edits. The debug scan summary counts these as `DeferredDirectories`, not conflicts. A new scan starts after a 30-second wait. A missing or replaced paired root still stops the scan; `DirectoryCheckFailure` in the warning describes a failed local directory or marker check without logging paths.
- **Settings says real-time event push is lost.** Push and folder scans are independent. Look for `GET /api/v4/file/events` in the log for the disconnect reason, retry interval, or an unexpected response instead of `text/event-stream`. A confirmed reconnection clears the warning and triggers a catch-up scan. While disconnected, the scanner still polls after at most 30 seconds of waiting; it can fail separately if the site is unreachable.
- **Reporting a problem?** Don't share `drives.json` files or the `folder-pairs` transfer cache under `~/Library/Application Support/Cloudreve/` (Windows: `%LOCALAPPDATA%\CloudreveClient\`). The session files store access and refresh tokens in plaintext, not in Keychain. Other programs running as your macOS user can read them.

## Contributing

For a sync bug, [file a bug report](https://github.com/799953468/cloudreve-mac/issues/new?template=bug_report.yml) with your macOS and Cloudreve versions, which side changed first, and what you expected to happen. Use sample filenames and omit credentials.

The [desktop app](src/Cloudreve.Desktop) is written in C# with Avalonia. [Cloudreve.Sync](src/Cloudreve.Sync) is a separate local-directory tool; the desktop app does not invoke it. Run the [test suite](tests/Cloudreve.Sync.Tests) from the repository root:

```sh
dotnet test --solution CloudreveClient.slnx -c Release
```

The tests use temporary folders, simulated server responses, and headless windows. They do not use a live Cloudreve account. Check changes to sign-in or storage-provider behavior against a real site as well.

### CI and release builds

- [Test](.github/workflows/test.yml) runs the Release test suite on macOS for branch pushes and pull requests.
- [Release build](.github/workflows/release.yml) tests and packages both Mac architectures on a version tag (such as `v1.2.3` or `v1.2.3-rc.1`), a published release, or a manual run. Tag builds take the app version from the tag; manual branch builds use the source version.
- Tag pushes and manual runs produce a `Cloudreve-macOS` workflow artifact without creating a release. Publishing a mutable release attaches missing ZIPs without overwriting existing ones. For an immutable release, attach both ZIPs from the tag build to the draft before publishing.

## Licensing and credits

Artwork and upstream interface translations come from [Cloudreve Desktop](https://github.com/cloudreve/desktop) and are covered by its [MIT notice](src/Cloudreve.Desktop/Assets/Cloudreve-LICENSE.txt). The bundled Roboto font uses the [SIL Open Font License 1.1](src/Cloudreve.Desktop/Assets/Fonts/OFL.txt). These notices cover those assets; the repository does not currently include a project-wide license.
