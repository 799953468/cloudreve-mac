namespace Cloudreve.Desktop;

internal sealed partial class FolderPairSync
{
    private sealed record FileTransfer(string Key, SyncAction Action, PairNode? Local, DriveEntry? Remote);
    private sealed record CompletedTransfer(FileTransfer Item, FileBaseline? Baseline);

    private async Task<CompletedTransfer> TransferFileAsync(FileTransfer item, CancellationToken cancel)
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
                    string target = await ResolveRemoteAsync(key, file, false, cancel, requireParents: true);
                    await RequireLocalAsync(key, here!.Hash, cancel);
                    await drive.UploadAsync(Path.Combine(pair.Local, key), target, file, cancel, progress);
                    file = await PairInventory.ReadVersionAsync(drive, target, cancel);
                    if (await CompareRemoteHashAsync(key, file, here.Hash, progress, cancel) != here.Hash)
                    {
                        throw new IOException(UiText.Get("云端回读与上传前的文件内容不同，未记录为已同步。"));
                    }

                    break;
                case SyncAction.Download:
                    progress?.Start(UiText.Get("下载"), key);
                    here = await InstallDownloadAsync(key, file!, here?.Hash, progress, cancel);
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
