using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FluxGet.Engine;

public sealed class JobStore
{
    private readonly string _connectionString;

    public JobStore(string databasePath)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = true }.ToString();
        Migrate();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS jobs (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              kind TEXT NOT NULL,
              url TEXT NOT NULL,
              file_name TEXT,
              directory TEXT NOT NULL,
              file_path TEXT,
              grp TEXT,
              total_bytes INTEGER,
              received_bytes INTEGER NOT NULL DEFAULT 0,
              etag TEXT,
              last_modified TEXT,
              resumable INTEGER NOT NULL DEFAULT 1,
              priority INTEGER NOT NULL DEFAULT 0,
              speed_limit INTEGER NOT NULL DEFAULT 0,
              connections INTEGER NOT NULL DEFAULT 0,
              checksum TEXT,
              use_schedule INTEGER NOT NULL DEFAULT 0,
              status TEXT NOT NULL,
              error TEXT,
              options TEXT,
              created_at TEXT NOT NULL,
              completed_at TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_jobs_status ON jobs(status, priority DESC, id);
            CREATE INDEX IF NOT EXISTS ix_jobs_completed ON jobs(completed_at DESC);
            CREATE TABLE IF NOT EXISTS segments (
              job_id INTEGER NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
              idx INTEGER NOT NULL,
              start INTEGER NOT NULL,
              "end" INTEGER NOT NULL,
              received INTEGER NOT NULL,
              PRIMARY KEY (job_id, idx)
            );
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS tokens (hash TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
            """);
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Insert(Job job)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO jobs (kind, url, file_name, directory, file_path, grp, total_bytes, received_bytes, etag, last_modified,
              resumable, priority, speed_limit, connections, checksum, use_schedule, status, error, options, created_at, completed_at)
            VALUES ($kind, $url, $file_name, $directory, $file_path, $grp, $total, $received, $etag, $lm,
              $resumable, $priority, $limit, $connections, $checksum, $schedule, $status, $error, $options, $created, $completed)
            RETURNING id;
            """;
        Bind(cmd, job);
        job.Id = (long)cmd.ExecuteScalar()!;
    }

    public void Update(Job job)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE jobs SET url=$url, file_name=$file_name, directory=$directory, file_path=$file_path, grp=$grp, total_bytes=$total,
              received_bytes=$received, etag=$etag, last_modified=$lm, resumable=$resumable, priority=$priority, speed_limit=$limit,
              connections=$connections, checksum=$checksum, use_schedule=$schedule, status=$status, error=$error, options=$options,
              completed_at=$completed
            WHERE id=$id;
            """;
        Bind(cmd, job);
        cmd.Parameters.AddWithValue("$id", job.Id);
        cmd.ExecuteNonQuery();
    }

    public void SaveProgress(Job job, IReadOnlyList<(int Index, long Start, long End, long Received)> segments, long received)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var del = c.CreateCommand())
        {
            del.CommandText = "DELETE FROM segments WHERE job_id=$id";
            del.Parameters.AddWithValue("$id", job.Id);
            del.ExecuteNonQuery();
        }
        using (var ins = c.CreateCommand())
        {
            ins.CommandText = "INSERT INTO segments (job_id, idx, start, \"end\", received) VALUES ($id, $i, $s, $e, $r)";
            var pi = ins.Parameters.Add("$i", SqliteType.Integer);
            var ps = ins.Parameters.Add("$s", SqliteType.Integer);
            var pe = ins.Parameters.Add("$e", SqliteType.Integer);
            var pr = ins.Parameters.Add("$r", SqliteType.Integer);
            ins.Parameters.AddWithValue("$id", job.Id);
            foreach (var s in segments)
            {
                pi.Value = s.Index; ps.Value = s.Start; pe.Value = s.End; pr.Value = s.Received;
                ins.ExecuteNonQuery();
            }
        }
        using (var upd = c.CreateCommand())
        {
            upd.CommandText = "UPDATE jobs SET received_bytes=$r, total_bytes=$t WHERE id=$id";
            upd.Parameters.AddWithValue("$r", received);
            upd.Parameters.AddWithValue("$t", (object?)job.TotalBytes ?? DBNull.Value);
            upd.Parameters.AddWithValue("$id", job.Id);
            upd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Delete(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM jobs WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public List<Job> LoadUnfinished()
    {
        using var c = Open();
        var jobs = Query(c, "SELECT * FROM jobs WHERE status <> 'Completed' ORDER BY id", null);
        var byId = jobs.ToDictionary(j => j.Id);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT s.job_id, s.idx, s.start, s.\"end\", s.received FROM segments s JOIN jobs j ON j.id = s.job_id WHERE j.status <> 'Completed' ORDER BY s.job_id, s.idx";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (byId.TryGetValue(r.GetInt64(0), out var job))
                job.Segments.Add(new Segment { Index = r.GetInt32(1), Start = r.GetInt64(2), End = r.GetInt64(3), Received = r.GetInt64(4) });
        }
        return jobs;
    }

    public (List<Job> Items, int Total) QueryCompleted(string? search, JobKind? kind, int offset, int limit)
    {
        using var c = Open();
        var where = "status = 'Completed'" + (kind is null ? "" : " AND kind = $kind") + (string.IsNullOrWhiteSpace(search) ? "" : " AND (file_name LIKE $q OR url LIKE $q)");
        void Params(SqliteCommand cmd)
        {
            if (kind is not null) cmd.Parameters.AddWithValue("$kind", kind.ToString());
            if (!string.IsNullOrWhiteSpace(search)) cmd.Parameters.AddWithValue("$q", "%" + search.Trim() + "%");
        }
        using var countCmd = c.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM jobs WHERE {where}";
        Params(countCmd);
        var total = Convert.ToInt32(countCmd.ExecuteScalar());
        var items = Query(c, $"SELECT * FROM jobs WHERE {where} ORDER BY completed_at DESC, id DESC LIMIT $limit OFFSET $offset", cmd =>
        {
            Params(cmd);
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$offset", offset);
        });
        return (items, total);
    }

    public Job? Get(long id)
    {
        using var c = Open();
        return Query(c, "SELECT * FROM jobs WHERE id=$id", cmd => cmd.Parameters.AddWithValue("$id", id)).FirstOrDefault();
    }

    public (int Completed, long CompletedBytes) CompletedTotals()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(received_bytes),0) FROM jobs WHERE status='Completed'";
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt32(0), r.GetInt64(1));
    }

    public Job? FindDuplicate(JobKind kind, string url)
    {
        using var c = Open();
        return Query(c, "SELECT * FROM jobs WHERE kind=$k AND url=$u ORDER BY id DESC LIMIT 1", cmd =>
        {
            cmd.Parameters.AddWithValue("$k", kind.ToString());
            cmd.Parameters.AddWithValue("$u", url);
        }).FirstOrDefault();
    }

    public T? GetSetting<T>(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() is string s ? JsonSerializer.Deserialize<T>(s, Json.Options) : default;
    }

    public void SetSetting<T>(string key, T value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(value, Json.Options));
        cmd.ExecuteNonQuery();
    }

    public void AddToken(string token, string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO tokens (hash, name, created_at) VALUES ($h, $n, $c)";
        cmd.Parameters.AddWithValue("$h", HashToken(token));
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public bool HasToken(string token)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM tokens WHERE hash=$h";
        cmd.Parameters.AddWithValue("$h", HashToken(token));
        return cmd.ExecuteScalar() is not null;
    }

    public List<(string Hash, string Name, DateTime CreatedAt)> ListTokens()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT hash, name, created_at FROM tokens ORDER BY created_at";
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, DateTime)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), DateTime.Parse(r.GetString(2)).ToUniversalTime()));
        return list;
    }

    public void RevokeToken(string hash)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM tokens WHERE hash=$h";
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.ExecuteNonQuery();
    }

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static List<Job> Query(SqliteConnection c, string sql, Action<SqliteCommand>? bind)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        using var r = cmd.ExecuteReader();
        var list = new List<Job>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    private static Job Read(SqliteDataReader r)
    {
        string? S(string n) => r.IsDBNull(r.GetOrdinal(n)) ? null : r.GetString(r.GetOrdinal(n));
        long? L(string n) => r.IsDBNull(r.GetOrdinal(n)) ? null : r.GetInt64(r.GetOrdinal(n));
        return new Job
        {
            Id = L("id")!.Value,
            Kind = Enum.Parse<JobKind>(S("kind")!),
            Url = S("url")!,
            FileName = S("file_name"),
            Directory = S("directory")!,
            FilePath = S("file_path"),
            Group = S("grp"),
            TotalBytes = L("total_bytes"),
            ReceivedBytes = L("received_bytes") ?? 0,
            ETag = S("etag"),
            LastModified = S("last_modified"),
            Resumable = L("resumable") == 1,
            Priority = (int)(L("priority") ?? 0),
            SpeedLimit = L("speed_limit") ?? 0,
            Connections = (int)(L("connections") ?? 0),
            Checksum = S("checksum"),
            UseSchedule = L("use_schedule") == 1,
            Status = Enum.TryParse<JobStatus>(S("status"), out var st) ? st : JobStatus.Queued,
            Error = S("error"),
            Options = S("options"),
            CreatedAt = DateTime.Parse(S("created_at")!).ToUniversalTime(),
            CompletedAt = S("completed_at") is { } ca ? DateTime.Parse(ca).ToUniversalTime() : null
        };
    }

    private static void Bind(SqliteCommand cmd, Job j)
    {
        object V(object? v) => v ?? DBNull.Value;
        cmd.Parameters.AddWithValue("$kind", j.Kind.ToString());
        cmd.Parameters.AddWithValue("$url", j.Url);
        cmd.Parameters.AddWithValue("$file_name", V(j.FileName));
        cmd.Parameters.AddWithValue("$directory", j.Directory);
        cmd.Parameters.AddWithValue("$file_path", V(j.FilePath));
        cmd.Parameters.AddWithValue("$grp", V(j.Group));
        cmd.Parameters.AddWithValue("$total", V(j.TotalBytes));
        cmd.Parameters.AddWithValue("$received", Interlocked.Read(ref j.ReceivedBytes));
        cmd.Parameters.AddWithValue("$etag", V(j.ETag));
        cmd.Parameters.AddWithValue("$lm", V(j.LastModified));
        cmd.Parameters.AddWithValue("$resumable", j.Resumable ? 1 : 0);
        cmd.Parameters.AddWithValue("$priority", j.Priority);
        cmd.Parameters.AddWithValue("$limit", j.SpeedLimit);
        cmd.Parameters.AddWithValue("$connections", j.Connections);
        cmd.Parameters.AddWithValue("$checksum", V(j.Checksum));
        cmd.Parameters.AddWithValue("$schedule", j.UseSchedule ? 1 : 0);
        cmd.Parameters.AddWithValue("$status", j.Status.ToString());
        cmd.Parameters.AddWithValue("$error", V(j.Error));
        cmd.Parameters.AddWithValue("$options", V(j.Options));
        cmd.Parameters.AddWithValue("$created", j.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$completed", V(j.CompletedAt?.ToString("O")));
    }
}
