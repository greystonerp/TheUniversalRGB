using System.Globalization;
using Microsoft.Data.Sqlite;
using UniversalRGB.Core.Compatibility;
using UniversalRGB.Core.Devices;

namespace UniversalRGB.Core.Storage;

public sealed record KnownDevice(
    string Key,
    string Name,
    string Type,
    string Adapter,
    string? HardwareKey,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

public sealed record InstallRecord(
    long Id,
    DateTimeOffset Timestamp,
    string PackageName,
    string FilePath,
    string Sha256,
    string? Signer,
    bool SignatureValid,
    int? ExitCode,
    string Result,
    string Notes);

/// <summary>
/// Local SQLite database: compatibility records, previously seen devices (for "Offline" status)
/// and driver/dependency installation history.
/// </summary>
public sealed class AppDatabase
{
    private readonly string _connectionString;

    public AppDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS compatibility (
                id            TEXT PRIMARY KEY,
                manufacturer  TEXT NOT NULL,
                model         TEXT NOT NULL,
                category      TEXT NOT NULL,
                bus           TEXT NOT NULL,
                vendor_id     TEXT NOT NULL,
                product_id    TEXT,
                revision      TEXT,
                backend       TEXT NOT NULL,
                requirements  TEXT NOT NULL,
                status        TEXT NOT NULL,
                verified      INTEGER NOT NULL,
                verified_on   TEXT,
                notes         TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_compat_ids ON compatibility(vendor_id, product_id);
            CREATE TABLE IF NOT EXISTS known_devices (
                key           TEXT PRIMARY KEY,
                name          TEXT NOT NULL,
                type          TEXT NOT NULL,
                adapter       TEXT NOT NULL,
                hardware_key  TEXT,
                first_seen    TEXT NOT NULL,
                last_seen     TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS install_history (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp       TEXT NOT NULL,
                package_name    TEXT NOT NULL,
                file_path       TEXT NOT NULL,
                sha256          TEXT NOT NULL,
                signer          TEXT,
                signature_valid INTEGER NOT NULL,
                exit_code       INTEGER,
                result          TEXT NOT NULL,
                notes           TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- compatibility

    /// <summary>Replaces all compatibility records with the manifest contents.</summary>
    public void ReplaceCompatibility(IEnumerable<CompatibilityRecord> records)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var delete = c.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM compatibility;";
            delete.ExecuteNonQuery();
        }

        using var insert = c.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT OR REPLACE INTO compatibility
            (id, manufacturer, model, category, bus, vendor_id, product_id, revision, backend, requirements, status, verified, verified_on, notes)
            VALUES ($id, $m, $model, $cat, $bus, $vid, $pid, $rev, $backend, $req, $status, $verified, $von, $notes);
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var name in new[] { "$id", "$m", "$model", "$cat", "$bus", "$vid", "$pid", "$rev", "$backend", "$req", "$status", "$verified", "$von", "$notes" })
        {
            p[name] = insert.Parameters.Add(name, name == "$verified" ? SqliteType.Integer : SqliteType.Text);
        }

        foreach (var r in records)
        {
            p["$id"].Value = r.Id;
            p["$m"].Value = r.Manufacturer;
            p["$model"].Value = r.Model;
            p["$cat"].Value = r.Category;
            p["$bus"].Value = r.Bus;
            p["$vid"].Value = r.VendorId.ToUpperInvariant();
            p["$pid"].Value = (object?)r.ProductId?.ToUpperInvariant() ?? DBNull.Value;
            p["$rev"].Value = (object?)r.Revision ?? DBNull.Value;
            p["$backend"].Value = r.Backend;
            p["$req"].Value = string.Join(',', r.Requirements);
            p["$status"].Value = r.Status;
            p["$verified"].Value = r.Verified ? 1 : 0;
            p["$von"].Value = (object?)r.VerifiedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value;
            p["$notes"].Value = r.Notes;
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<CompatibilityRecord> GetCompatibility()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM compatibility ORDER BY manufacturer, model;";
        return ReadCompatibility(cmd);
    }

    /// <summary>Most specific match: exact VID+PID (+revision) first, then vendor-family entries.</summary>
    public CompatibilityRecord? Match(HardwareIdentity identity)
    {
        if (identity.VendorId is null)
        {
            return null;
        }

        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM compatibility
            WHERE vendor_id = $vid AND (product_id IS NULL OR product_id = $pid)
            ORDER BY (product_id IS NULL), (revision IS NULL)
            """;
        cmd.Parameters.AddWithValue("$vid", identity.VendorId);
        cmd.Parameters.AddWithValue("$pid", (object?)identity.ProductId ?? DBNull.Value);
        var matches = ReadCompatibility(cmd);
        return matches.FirstOrDefault(m => m.Revision is null || m.Revision == identity.Revision);
    }

    private static List<CompatibilityRecord> ReadCompatibility(SqliteCommand cmd)
    {
        var list = new List<CompatibilityRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new CompatibilityRecord
            {
                Id = reader.GetString(reader.GetOrdinal("id")),
                Manufacturer = reader.GetString(reader.GetOrdinal("manufacturer")),
                Model = reader.GetString(reader.GetOrdinal("model")),
                Category = reader.GetString(reader.GetOrdinal("category")),
                Bus = reader.GetString(reader.GetOrdinal("bus")),
                VendorId = reader.GetString(reader.GetOrdinal("vendor_id")),
                ProductId = reader.IsDBNull(reader.GetOrdinal("product_id")) ? null : reader.GetString(reader.GetOrdinal("product_id")),
                Revision = reader.IsDBNull(reader.GetOrdinal("revision")) ? null : reader.GetString(reader.GetOrdinal("revision")),
                Backend = reader.GetString(reader.GetOrdinal("backend")),
                Requirements = reader.GetString(reader.GetOrdinal("requirements")).Split(',', StringSplitOptions.RemoveEmptyEntries),
                Status = reader.GetString(reader.GetOrdinal("status")),
                Verified = reader.GetInt64(reader.GetOrdinal("verified")) != 0,
                VerifiedOn = reader.IsDBNull(reader.GetOrdinal("verified_on"))
                    ? null
                    : DateOnly.ParseExact(reader.GetString(reader.GetOrdinal("verified_on")), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Notes = reader.GetString(reader.GetOrdinal("notes")),
            });
        }

        return list;
    }

    // ---------------------------------------------------------------- known devices

    public void RecordSeen(IEnumerable<RgbDevice> devices)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO known_devices (key, name, type, adapter, hardware_key, first_seen, last_seen)
            VALUES ($key, $name, $type, $adapter, $hw, $now, $now)
            ON CONFLICT(key) DO UPDATE SET name = excluded.name, type = excluded.type,
                hardware_key = excluded.hardware_key, last_seen = excluded.last_seen;
            """;
        var key = cmd.Parameters.Add("$key", SqliteType.Text);
        var name = cmd.Parameters.Add("$name", SqliteType.Text);
        var type = cmd.Parameters.Add("$type", SqliteType.Text);
        var adapter = cmd.Parameters.Add("$adapter", SqliteType.Text);
        var hw = cmd.Parameters.Add("$hw", SqliteType.Text);
        cmd.Parameters.AddWithValue("$now", now);
        foreach (var d in devices)
        {
            key.Value = d.Id;
            name.Value = d.Name;
            type.Value = d.Type.ToString();
            adapter.Value = d.AdapterName;
            hw.Value = (object?)d.Identity?.Key ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<KnownDevice> GetKnownDevices()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key, name, type, adapter, hardware_key, first_seen, last_seen FROM known_devices ORDER BY name;";
        using var r = cmd.ExecuteReader();
        var list = new List<KnownDevice>();
        while (r.Read())
        {
            list.Add(new KnownDevice(
                r.GetString(0),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture)));
        }

        return list;
    }

    public void ForgetKnownDevice(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM known_devices WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- install history

    public long AddInstallRecord(InstallRecord record)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO install_history (timestamp, package_name, file_path, sha256, signer, signature_valid, exit_code, result, notes)
            VALUES ($ts, $pkg, $file, $sha, $signer, $valid, $exit, $result, $notes);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$ts", record.Timestamp.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$pkg", record.PackageName);
        cmd.Parameters.AddWithValue("$file", record.FilePath);
        cmd.Parameters.AddWithValue("$sha", record.Sha256);
        cmd.Parameters.AddWithValue("$signer", (object?)record.Signer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$valid", record.SignatureValid ? 1 : 0);
        cmd.Parameters.AddWithValue("$exit", (object?)record.ExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$result", record.Result);
        cmd.Parameters.AddWithValue("$notes", record.Notes);
        return (long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<InstallRecord> GetInstallHistory(int limit = 100)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, timestamp, package_name, file_path, sha256, signer, signature_valid, exit_code, result, notes
            FROM install_history ORDER BY id DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<InstallRecord>();
        while (r.Read())
        {
            list.Add(new InstallRecord(
                r.GetInt64(0),
                DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture),
                r.GetString(2),
                r.GetString(3),
                r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.GetInt64(6) != 0,
                r.IsDBNull(7) ? null : (int)r.GetInt64(7),
                r.GetString(8),
                r.GetString(9)));
        }

        return list;
    }
}
