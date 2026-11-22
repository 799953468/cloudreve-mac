using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Cloudreve.Desktop;

// The pair lease serializes writers. SQLite commits only the rows changed by a pass.
internal sealed class BaselineDatabase : IDisposable
{
    private const string Columns = "path, hash, version, localId, remoteId, directory, generation";
    private readonly SqliteConnection connection;

    internal BaselineDatabase(string legacyPath)
    {
        string path = Path.ChangeExtension(legacyPath, ".sqlite");
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.Write, Share = FileShare.ReadWrite };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        if (!File.Exists(path))
        {
            new FileStream(path, options).Dispose();
        }
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            connection.Open();
            connection.CreateCollation("path_nocase", StringComparer.OrdinalIgnoreCase.Compare);
            using (var version = Command("PRAGMA user_version"))
            {
                switch ((long)version.ExecuteScalar()!)
                {
                    case 0:
                        if (File.Exists(legacyPath + ".migrated"))
                        {
                            throw new InvalidDataException(UiText.Get("同步基线无效。"));
                        }
                        Initialize(legacyPath);
                        break;
                    case 1:
                        break;
                    default:
                        throw new InvalidDataException(UiText.Get("同步基线无效。"));
                }
            }
            // The old client must never use a stale common ancestor after the new database has advanced.
            if (File.Exists(legacyPath))
            {
                File.Move(legacyPath, legacyPath + ".migrated");
            }
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void Initialize(string legacyPath)
    {
        using (var transaction = connection.BeginTransaction())
        using (var schema = Command("""
            CREATE TABLE files (path TEXT PRIMARY KEY, hash TEXT NOT NULL, version TEXT NOT NULL,
                localId TEXT, remoteId TEXT, directory INTEGER NOT NULL, generation INTEGER NOT NULL);
            CREATE INDEX paths ON files(path COLLATE path_nocase);
            CREATE INDEX local_ids ON files(localId) WHERE localId IS NOT NULL;
            CREATE INDEX remote_ids ON files(remoteId) WHERE remoteId IS NOT NULL;
            CREATE INDEX legacy_ids ON files(localId) WHERE instr(localId, ':') = 17;
            PRAGMA user_version = 1;
            """))
        {
            schema.Transaction = transaction;
            schema.ExecuteNonQuery();
            if (File.Exists(legacyPath))
            {
#pragma warning disable CS0618 // Keep the one-time migration available in this release.
                ImportLegacyJsonBaseline(legacyPath, transaction);
#pragma warning restore CS0618
            }
            transaction.Commit();
        }
    }

    [Obsolete("Legacy JSON baseline import will be removed in the next release. New baselines use SQLite.")]
    private void ImportLegacyJsonBaseline(string path, SqliteTransaction transaction)
    {
        using (var input = File.OpenRead(path))
        {
            var legacy = JsonSerializer.Deserialize(input, DesktopJson.Default.DictionaryStringFileBaseline)
                ?? throw new InvalidDataException(UiText.Get("同步基线无效。"));
            WriteRows(legacy, transaction);
        }
    }

    private SqliteCommand Command(string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    internal bool HasLegacyIds()
    {
        using (var command = Command("SELECT 1 FROM files WHERE instr(localId, ':') = 17 LIMIT 1"))
        {
            return command.ExecuteScalar() != null;
        }
    }

    internal Dictionary<string, FileBaseline> Read(PairScanScope? scope = null)
    {
        var rows = new Dictionary<string, FileBaseline>();
        if (scope == null)
        {
            using (var all = Command("SELECT " + Columns + " FROM files"))
            {
                ReadRows(all, rows);
            }
            return rows;
        }

        rows = ReadPaths(scope.Targets.Concat(scope.Ancestors).Distinct(StringComparer.OrdinalIgnoreCase));
        using (var tree = Command("SELECT " + Columns + " FROM files WHERE path COLLATE path_nocase >= $start AND path COLLATE path_nocase < $end"))
        {
            var start = tree.Parameters.Add("$start", SqliteType.Text);
            var end = tree.Parameters.Add("$end", SqliteType.Text);
            foreach (string target in scope.Targets)
            {
                // '/' immediately precedes '0' in ordinal order, so this range covers only descendants.
                start.Value = target + "/";
                end.Value = target + "0";
                ReadRows(tree, rows);
            }
        }
        return rows;
    }

    internal Dictionary<string, FileBaseline> ReadPaths(IEnumerable<string> paths)
    {
        var rows = new Dictionary<string, FileBaseline>();
        using (var exact = Command("SELECT " + Columns + " FROM files WHERE path COLLATE path_nocase = $key"))
        {
            var key = exact.Parameters.Add("$key", SqliteType.Text);
            foreach (string path in paths)
            {
                key.Value = path;
                ReadRows(exact, rows);
            }
        }
        return rows;
    }

    private static void ReadRows(SqliteCommand command, Dictionary<string, FileBaseline> rows)
    {
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string path = reader.GetString(0);
                PairInventory.ValidateKey(path);
                rows[path] = new FileBaseline(reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetBoolean(5), checked((uint)reader.GetInt64(6)));
            }
        }
    }

    internal bool HasOutsideIdentity(Dictionary<string, FileBaseline> included, PairInventory local, PairInventory remote)
    {
        return HasOutside("SELECT path FROM files WHERE localId = $id", local.Nodes.Values.Select(node => node.LocalId)) ||
            HasOutside("SELECT path FROM files WHERE remoteId = $id", remote.Nodes.Values.Select(node => node.Remote?.Id));

        bool HasOutside(string sql, IEnumerable<string?> ids)
        {
            using (var command = Command(sql))
            {
                var id = command.Parameters.Add("$id", SqliteType.Text);
                foreach (string value in ids.OfType<string>().Distinct())
                {
                    id.Value = value;
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (!included.ContainsKey(reader.GetString(0)))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }
    }

    internal void SaveChanges(Dictionary<string, FileBaseline> original, Dictionary<string, FileBaseline> current)
    {
        using (var transaction = connection.BeginTransaction())
        using (var remove = Command("DELETE FROM files WHERE path = $path"))
        {
            remove.Transaction = transaction;
            var path = remove.Parameters.Add("$path", SqliteType.Text);
            foreach (string key in original.Keys)
            {
                if (!current.ContainsKey(key))
                {
                    path.Value = key;
                    remove.ExecuteNonQuery();
                }
            }
            WriteRows(current.Where(row => !original.TryGetValue(row.Key, out var old) || old != row.Value), transaction);
            transaction.Commit();
        }
    }

    private void WriteRows(IEnumerable<KeyValuePair<string, FileBaseline>> rows, SqliteTransaction transaction)
    {
        using (var insert = Command("INSERT OR REPLACE INTO files (" + Columns + ") VALUES ($path,$hash,$version,$local,$remote,$dir,$gen)"))
        {
            insert.Transaction = transaction;
            var path = insert.Parameters.Add("$path", SqliteType.Text);
            var hash = insert.Parameters.Add("$hash", SqliteType.Text);
            var version = insert.Parameters.Add("$version", SqliteType.Text);
            var local = insert.Parameters.Add("$local", SqliteType.Text);
            var remote = insert.Parameters.Add("$remote", SqliteType.Text);
            var directory = insert.Parameters.Add("$dir", SqliteType.Integer);
            var generation = insert.Parameters.Add("$gen", SqliteType.Integer);
            foreach (var (key, value) in rows)
            {
                PairInventory.ValidateKey(key);
                path.Value = key;
                hash.Value = value.LocalHash;
                version.Value = value.RemoteVersion;
                local.Value = (object?)value.LocalId ?? DBNull.Value;
                remote.Value = (object?)value.RemoteId ?? DBNull.Value;
                directory.Value = value.IsDirectory;
                generation.Value = (long)value.LocalGeneration;
                insert.ExecuteNonQuery();
            }
        }
    }

    public void Dispose()
    {
        connection.Dispose();
    }
}
