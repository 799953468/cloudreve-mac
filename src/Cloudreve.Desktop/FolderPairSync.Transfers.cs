namespace Cloudreve.Desktop;

internal sealed partial class FolderPairSync
{
    private sealed record CompletedTransfer(FileTransfer Item, FileBaseline? Baseline);

    private static bool MatchesPreviousVersion(DriveEntry placeholder, DriveEntry? previous)
    {
        return previous == null || !previous.IsFolder && !previous.SkipSync && !string.IsNullOrEmpty(previous.Version) &&
               PairInventory.SameUri(previous.Uri, placeholder.Uri) && (previous.Id == null || previous.Id == placeholder.Id) && previous.Version == placeholder.Version;
    }

    // A new upload and a version upload can both appear as an excluded placeholder in a fresh listing.
    private async Task<bool> CollectOwnedUploadAsync(string key, DriveEntry placeholder, PairNode? here, FileBaseline? baseline, TransferQueue queue,
        List<FileTransfer> pending, CancellationToken cancel)
    {
        var entry = queue.Find(key);
        if (entry?.File.Action != SyncAction.Upload)
        {
            return false;
        }

        var files = queue.Files(entry);
        var saved = files.ReadUpload();
        var previous = entry.File.Remote;
        if (saved == null || placeholder.UploadSessionId != saved.SessionId || !MatchesPreviousVersion(placeholder, previous))
        {
            return false;
        }

        if (baseline == entry.File.Previous && here == entry.File.Local && here?.Hash != null &&
            SyncDecision.ForFile(baseline, here.Hash, previous?.Version) == SyncAction.Upload)
        {
            pending.Add(new FileTransfer(key, SyncAction.Upload, here, previous, baseline));
        }
        else
        {
            // Discard only our own stale session. A later scan will decide what the current copies need.
            await VerifyRemoteRootAsync(cancel);
            string target = await ResolveRemoteAsync(key, previous, false, cancel, requireParents: true, uploadSession: saved.SessionId);
            await drive.CancelUploadAsync(target, saved.SessionId, cancel);
        }

        return true;
    }

    private async Task<CompletedTransfer> TransferFileAsync(FileTransfer item, TransferFiles files, CancellationToken cancel)
    {
        string key = item.Key;
        var here = item.Local;
        var file = item.Remote;
        var progress = activity?.CreateWorker();
        try
        {
            cancel.ThrowIfCancellationRequested();
            FolderPairStore.Verify(pair);
            switch (item.Action)
            {
                case SyncAction.Upload:
                    progress?.Start(UiText.Get("上传"), key);
                    await VerifyRemoteRootAsync(cancel);
                    // Parent creation belongs to the serial directory pass, not competing sibling uploads.
                    var saved = files.ReadUpload();
                    string target = await ResolveRemoteAsync(key, file, false, cancel, requireParents: true, uploadSession: saved?.SessionId);
                    if (saved?.Expired == true)
                    {
                        await drive.CancelUploadAsync(target, saved.SessionId, cancel);
                        File.Delete(files.UploadPath);
                        saved = null;
                        target = await ResolveRemoteAsync(key, file, false, cancel, requireParents: true);
                    }

                    string path = Path.Combine(pair.Local, key);
                    await RequireLocalAsync(key, here!.Hash, cancel);
                    await drive.UploadAsync(path, target, file, cancel, progress, files, saved);
                    var uploaded = await drive.StatAsync(target, cancel);
                    // A resumed local/relay upload has no separate finalize call. Only replace our own stranded placeholder.
                    if (saved?.StorageKind == "local" && uploaded.UploadSessionId == saved.SessionId &&
                        PairInventory.SameUri(target, uploaded.Uri) && MatchesPreviousVersion(uploaded, file))
                    {
                        await VerifyRemoteRootAsync(cancel);
                        await drive.CancelUploadAsync(target, saved.SessionId, cancel);
                        File.Delete(files.UploadPath);
                        target = await ResolveRemoteAsync(key, file, false, cancel, requireParents: true);
                        await RequireLocalAsync(key, here.Hash, cancel);
                        await drive.UploadAsync(path, target, file, cancel, progress, files);
                        uploaded = await drive.StatAsync(target, cancel);
                    }

                    file = PairInventory.RequireVersion(uploaded, target);
                    if (await CompareRemoteHashAsync(key, file, here.Hash, progress, cancel) != here.Hash)
                    {
                        throw new IOException(UiText.Get("云端回读与上传前的文件内容不同，未记录为已同步。"));
                    }

                    break;
                case SyncAction.Download:
                    progress?.Start(UiText.Get("下载"), key);
                    here = await InstallDownloadAsync(key, file!, here?.Hash, progress, cancel, files);
                    break;
                case SyncAction.CompareContents:
                    progress?.Start(UiText.Get("核对内容"), key);
                    if (await CompareRemoteHashAsync(key, file!, here!.Hash, progress, cancel) != here.Hash)
                    {
                        progress?.Finish(UiText.Get("冲突"));
                        return new CompletedTransfer(item, null);
                    }

                    break;
                default:
                    throw new InvalidOperationException(UiText.Get("未知的同步操作。"));
            }

            var baseline = new FileBaseline(here.Hash!, file!.Version!, here.LocalId, file.Id, LocalGeneration: here.LocalGeneration);
            FolderPairStore.WriteJson(files.VerifiedPath, baseline);
            progress?.Finish(UiText.Get("完成"));
            return new CompletedTransfer(item, baseline);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            progress?.Finish(UiText.Get("已取消"));
            throw;
        }
        catch
        {
            progress?.Finish(UiText.Get("失败"));
            throw;
        }
    }
}
