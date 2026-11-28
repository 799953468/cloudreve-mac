using System.Globalization;
using Cloudreve.Sync;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

// Either a scope that covers every change since the last complete pass, or the reason a full scan is needed instead.
internal sealed record CatchUpPlan(PairScanScope? Scope, string? FullReason, int LocalChanges = 0, int CloudChanges = 0, int CloudDeletions = 0);

// Finds what changed while nobody was watching, without listing every cloud folder. The local side is walked in full
// (cheap: unchanged files reuse their digests) and compared with the baseline. The cloud side is searched for items
// modified since the change cursor, and the trash for items deleted since then.
internal static class CatchUpPlanner
{
    internal const string CursorState = "remoteCursor";
    internal const string LastFullState = "lastFullScan";
    // A cursor is saved this far before the server time it was taken at, so slow commits and clock rounding are covered.
    internal static readonly TimeSpan CursorMargin = TimeSpan.FromMinutes(2);
    // Older cursors are not trusted: the trash may already have purged deletions made since then.
    internal static readonly TimeSpan CursorLifetime = TimeSpan.FromDays(7);
    internal const int MaxPaths = 4096;

    internal static long? ReadCursor(BaselineDatabase database, string name = CursorState)
    {
        return long.TryParse(database.ReadState(name), NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : null;
    }

    internal static void WriteCursor(BaselineDatabase database, string name, DateTimeOffset value)
    {
        database.WriteState(name, value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
    }

    internal static async Task<CatchUpPlan> PlanAsync(FolderPairStore store, FolderPair pair, DriveClient drive, PassResources resources, SyncActivity? activity,
        bool includeLocal, bool includeRemote, IEnumerable<string> extraPaths, DateTimeOffset serverNow, Action? beginRemote, CancellationToken cancel)
    {
        using (store.Lock(pair))
        using (var database = store.OpenBaseline(pair))
        {
            if (database.HasLegacyIds())
            {
                return new CatchUpPlan(null, "LegacyBaselineIds");
            }

            long? cursor = ReadCursor(database);
            if (includeRemote && cursor == null)
            {
                return new CatchUpPlan(null, "NoChangeCursor");
            }

            if (includeRemote && serverNow - DateTimeOffset.FromUnixTimeSeconds(cursor!.Value) > CursorLifetime)
            {
                return new CatchUpPlan(null, "ChangeCursorExpired");
            }

            FolderPairStore.Verify(pair);
            var rules = new PairIgnoreRules(store.ReadRules(pair));
            var baseline = database.Read();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var confirmedLocal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var confirmedRemote = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int localChanges = 0, cloudChanges = 0, cloudDeletions = 0;
            try
            {
                if (includeRemote)
                {
                    activity?.SetScan(ScanPhase.Remote, isScoped: true);
                    var remote = await ReadRemoteAsync(drive, pair, cursor!.Value, baseline, rules, beginRemote, cancel);
                    if (remote == null)
                    {
                        activity?.SetScan(ScanPhase.Idle);
                        return new CatchUpPlan(null, "CloudChangeSearchFailed");
                    }

                    if (remote.Value.Keys.Count > MaxPaths)
                    {
                        activity?.SetScan(ScanPhase.Idle);
                        return new CatchUpPlan(null, "TooManyCloudChanges");
                    }

                    keys.UnionWith(remote.Value.Keys);
                    confirmedRemote.UnionWith(remote.Value.Deleted);
                    (cloudChanges, cloudDeletions) = (remote.Value.Keys.Count, remote.Value.Deleted.Count);
                }

                if (includeLocal)
                {
                    activity?.SetScan(ScanPhase.Local);
                    var local = await PairInventory.ReadLocalAsync(pair, rules, baseline, resources.LocalScan, cancel,
                        activity == null ? null : count => activity.SetScan(ScanPhase.Local, count));
                    var (changed, deleted) = LocalChanges(baseline, local, rules);
                    keys.UnionWith(changed);
                    confirmedLocal.UnionWith(deleted);
                    localChanges = changed.Count;
                }
            }
            catch (Exception) when (cancel.IsCancellationRequested)
            {
                activity?.SetScan(ScanPhase.Idle);
                throw;
            }
            catch
            {
                activity?.SetScan(ScanPhase.Failed);
                throw;
            }

            activity?.SetScan(ScanPhase.Idle);
            keys.UnionWith(extraPaths.Where(TransferQueue.IsSafeKey));
            // Interrupted transfers resume only inside a pass that covers them.
            keys.UnionWith(TransferQueue.Open(store.TransferDirectory(pair), resources.CachedQueue).Entries.Select(entry => entry.File.Key));
            var targets = keys.Where(key => !HasAncestorIn(keys, key)).ToArray();
            if (targets.Length > MaxPaths)
            {
                return new CatchUpPlan(null, "TooManyChanges");
            }

            var scope = new PairScanScope(targets) { ConfirmedLocalDeletions = confirmedLocal, ConfirmedRemoteDeletions = confirmedRemote };
            return new CatchUpPlan(scope, null, localChanges, cloudChanges, cloudDeletions);
        }
    }

    // Paths whose local state differs from the baseline. A tracked path missing on disk is a confirmed deletion unless
    // its identity turns up at another path, which then joins the changes as the other half of a move.
    internal static (HashSet<string> Changed, HashSet<string> Deleted) LocalChanges(Dictionary<string, FileBaseline> baseline, PairInventory local,
        PairIgnoreRules rules)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in local.Nodes.Values)
        {
            if (!baseline.TryGetValue(node.Key, out var known) || node.Kind == NodeKind.Unsupported || known.IsDirectory != (node.Kind == NodeKind.Directory) ||
                known.LocalId != node.LocalId || !known.IsDirectory && (known.LocalHash != node.Hash || known.LocalGeneration != node.LocalGeneration))
            {
                changed.Add(node.Key);
            }
        }

        var byId = local.Nodes.Values.Where(node => node.LocalId != null).ToLookup(node => node.LocalId!);
        foreach (var (key, known) in baseline)
        {
            if (rules.Excludes(key, known.IsDirectory) || local.Nodes.ContainsKey(key) || ExcludedBelow(local, key))
            {
                continue;
            }

            changed.Add(key);
            if (known.LocalId == null || !byId.Contains(known.LocalId))
            {
                deleted.Add(key);
            }
            else
            {
                changed.UnionWith(byId[known.LocalId].Select(node => node.Key));
            }
        }

        return (changed, deleted);
    }

    private static bool ExcludedBelow(PairInventory local, string key)
    {
        for (string? path = key; !string.IsNullOrEmpty(path); path = PairKey.Parent(path))
        {
            if (local.Excluded.Contains(path))
            {
                return true;
            }
        }

        return false;
    }

    // Null when the server cannot answer the search; the caller then scans everything.
    private static async Task<(HashSet<string> Keys, HashSet<string> Deleted)?> ReadRemoteAsync(DriveClient drive, FolderPair pair, long since,
        Dictionary<string, FileBaseline> baseline, PairIgnoreRules rules, Action? beginRemote, CancellationToken cancel)
    {
        List<DriveEntry> changed;
        List<TrashedEntry> trashed;
        try
        {
            // This search covers subscription gaps reported before it starts, like a full listing would.
            beginRemote?.Invoke();
            changed = await drive.ChangedSinceAsync(pair.Remote, since, cancel);
            trashed = await drive.TrashedSinceAsync(since, cancel);
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested && ex is HttpRequestException or InvalidDataException or KeyNotFoundException or InvalidOperationException
                                       or System.Text.Json.JsonException)
        {
            Log.Logger.ForContext("PairId", pair.Marker).WriteFailure(LogEventLevel.Warning, "Search cloud changes since the last sync", ex);
            return null;
        }

        var byRemoteId = baseline.Where(entry => entry.Value.RemoteId != null).ToLookup(entry => entry.Value.RemoteId!, entry => entry.Key);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in changed)
        {
            if (RelativeKey(pair.Remote, entry.Uri) is not { } key || rules.Excludes(key, entry.IsFolder))
            {
                continue;
            }

            keys.Add(key);
            if (entry.Id != null)
            {
                // A move or rename shows up at its new path only; the old one must be scanned too.
                keys.UnionWith(byRemoteId[entry.Id]);
            }
        }

        foreach (var item in trashed)
        {
            if (RelativeKey(pair.Remote, item.RestoreUri) is not { } key || rules.Excludes(key, item.IsFolder))
            {
                continue;
            }

            keys.Add(key);
            if (baseline.TryGetValue(key, out var known) && known.RemoteId == item.Id)
            {
                deleted.Add(key);
            }
        }

        return (keys, deleted);
    }

    // The pair-relative key of a cloud URI, or null when it is not strictly inside root or is not a safe key.
    internal static string? RelativeKey(string root, string uri)
    {
        if (!Uri.TryCreate(root, UriKind.Absolute, out var rootUri) || !Uri.TryCreate(uri, UriKind.Absolute, out var itemUri) ||
            rootUri.Scheme != itemUri.Scheme || rootUri.Authority != itemUri.Authority)
        {
            return null;
        }

        string[] parent = rootUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        string[] child = itemUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (child.Length <= parent.Length || !parent.SequenceEqual(child[..parent.Length], StringComparer.Ordinal))
        {
            return null;
        }

        string key = string.Join('/', child[parent.Length..]).Normalize();
        return TransferQueue.IsSafeKey(key) ? key : null;
    }

    private static bool HasAncestorIn(HashSet<string> keys, string key)
    {
        for (string? parent = PairKey.Parent(key); !string.IsNullOrEmpty(parent); parent = PairKey.Parent(parent))
        {
            if (keys.Contains(parent))
            {
                return true;
            }
        }

        return false;
    }
}
