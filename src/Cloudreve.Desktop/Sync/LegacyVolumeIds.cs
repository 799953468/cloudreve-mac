using Cloudreve.Sync;

namespace Cloudreve.Desktop;

internal static class LegacyVolumeIds
{
    private const int MountIdLength = 16;

    internal static bool Present(Dictionary<string, FileBaseline> baseline)
    {
        return baseline.Values.Any(entry => entry.LocalId?.IndexOf(':') == MountIdLength);
    }

    internal static bool Upgrade(Dictionary<string, FileBaseline> baseline, PairInventory local, HashSet<string> conflicts)
    {
        var legacy = baseline.Where(entry => entry.Value.LocalId?.IndexOf(':') == MountIdLength).ToArray();
        if (legacy.Length == 0)
        {
            return false;
        }

        var volumes = IdentifyVolumes(local, legacy);
        bool changed = false;
        ILookup<string, PairNode>? byObject = null;
        foreach (var (key, previous) in legacy)
        {
            string id = previous.LocalId!;
            string mount = id[..MountIdLength];
            bool witnessed = volumes.TryGetValue(mount, out var volume);
            if (volume != null)
            {
                if (volume != mount)
                {
                    // Keep the last verified digest/version; only a fresh content check may repopulate the generation cache.
                    baseline[key] = previous with { LocalId = volume + id[MountIdLength..], LocalGeneration = 0 };
                    changed = true;
                }

                continue;
            }

            // Without a volume witness a possible move must not become a delete and a new upload.
            byObject ??= local.Nodes.Values.Where(node => node.LocalId != null).ToLookup(node => ObjectPart(node.LocalId!));
            var candidates = byObject[ObjectPart(id)];
            if (witnessed || candidates.Any())
            {
                conflicts.UnionWith(candidates.Select(node => node.Key));
                conflicts.Add(key);
                for (string? parent = PairKey.Parent(key); !string.IsNullOrEmpty(parent) && !local.Nodes.ContainsKey(parent); parent = PairKey.Parent(parent))
                {
                    conflicts.Add(parent);
                }
            }
        }

        return changed;
    }

    private static string ObjectPart(string id)
    {
        return id[(id.IndexOf(':') + 1)..];
    }

    private static Dictionary<string, string?> IdentifyVolumes(PairInventory local, KeyValuePair<string, FileBaseline>[] legacy)
    {
        var volumes = new Dictionary<string, string?>();
        // A legacy baseline has no volume UUID. Bind its mount ID using objects still at
        // their original path, with the same kind, file ID and nanosecond birth time. The
        // current mount number alone is not evidence: another disk can reuse it.
        foreach (var (key, previous) in legacy)
        {
            if (!local.Nodes.TryGetValue(key, out var node) || node.Key != key || node.LocalId == null ||
                node.Kind != (previous.IsDirectory ? NodeKind.Directory : NodeKind.File))
            {
                continue;
            }

            string oldId = previous.LocalId!;
            int separator = node.LocalId.IndexOf(':');
            if (separator < 0 || !node.LocalId.AsSpan(separator).SequenceEqual(oldId.AsSpan(MountIdLength)))
            {
                continue;
            }

            string mount = oldId[..MountIdLength];
            string volume = node.LocalId[..separator];
            if (volumes.TryGetValue(mount, out var known) && known != volume)
            {
                volumes[mount] = null; // Conflicting witnesses: never guess which disk owned the record.
            }
            else
            {
                volumes[mount] = volume;
            }
        }

        return volumes;
    }
}
