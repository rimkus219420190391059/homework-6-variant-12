using System.Net.Http.Json;
using Microsoft.Data.Sqlite;

namespace CampusDbSync;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var demo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        var dbPath = GetArg(args, "--db", Path.Combine("data", "campus.db"));
        var backupDir = GetArg(args, "--backup-dir", "backups");
        var logPath = GetArg(args, "--log", Path.Combine("logs", "campus-db-sync.log"));
        var reportPath = GetArg(args, "--report", Path.Combine("reports", "sanity-report.md"));
        var apiUrl = GetArg(args, "--api", "https://jsonplaceholder.typicode.com/users");
        var log = new Logger(logPath);

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("CampusDbSync [--demo] [--db PATH] [--backup-dir PATH] [--log PATH] [--report PATH] [--api URL]");
            return 0;
        }

        try
        {
            log.Info("=== CampusDbSync started ===");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

            if (demo)
                await DemoData.CreateV1Async(dbPath, log);

            if (!File.Exists(dbPath))
                throw new FileNotFoundException("SQLite DB not found. Use --demo for a sample database.", dbPath);

            // API — вспомогательная проверка сети, не источник данных проходов.
            var apiAvailable = await ApiProbe.CheckAsync(apiUrl, log);

            // Резервная копия создается до любых изменений схемы.
            var backup = Backup.Create(dbPath, backupDir, log);
            var oldVersion = await Database.GetVersionAsync(dbPath);

            if (oldVersion > 2)
                throw new InvalidOperationException($"Unsupported database schema version: {oldVersion}.");

            var migratedRows = 0;
            if (oldVersion < 2)
            {
                try
                {
                    migratedRows = await Database.MigrateToV2Async(dbPath);
                    log.Info($"Schema migrated: v{oldVersion} -> v2; rows={migratedRows}.");
                }
                catch (Exception migrationError)
                {
                    log.Error("Migration failed. Restoring backup.", migrationError);
                    Backup.Restore(backup, dbPath, log);
                    throw new InvalidOperationException("Migration failed; database restored from backup.", migrationError);
                }
            }
            else
            {
                log.Info("Database already uses schema v2; migration skipped.");
            }

            var sanity = await Database.CheckAsync(dbPath);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath,
                Report.Build(dbPath, backup, oldVersion, migratedRows, apiAvailable, sanity));

            Console.WriteLine();
            Console.WriteLine($"Backup: {backup}");
            Console.WriteLine($"Sanity: {(sanity.Ok ? "PASS" : "FAIL")}");
            Console.WriteLine($"Report: {Path.GetFullPath(reportPath)}");
            log.Info($"Sanity result: {(sanity.Ok ? "PASS" : "FAIL")}");
            return sanity.Ok ? 0 : 2;
        }
        catch (Exception ex)
        {
            log.Error("Fatal error.", ex);
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine($"See log: {Path.GetFullPath(logPath)}");
            return 1;
        }
    }

    private static string GetArg(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }
}

internal sealed class Logger
{
    private readonly string _path;
    private readonly object _sync = new();

    public Logger(string path)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public void Info(string message) => Write("INFO", message, null);
    public void Error(string message, Exception? ex) => Write("ERROR", message, ex);

    private void Write(string level, string message, Exception? ex)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] [{level}] {message}";
        if (ex != null) line += Environment.NewLine + ex;
        lock (_sync) File.AppendAllText(_path, line + Environment.NewLine);
        Console.WriteLine(line);
    }
}

internal static class ApiProbe
{
    public static async Task<bool> CheckAsync(string url, Logger log)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CampusDbSync/1.0");
        try
        {
            var users = await client.GetFromJsonAsync<List<ApiUser>>(url);
            log.Info($"JSONPlaceholder reachable; records returned: {users?.Count ?? 0}.");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   or System.Text.Json.JsonException)
        {
            log.Info($"External API unavailable; local migration continues: {ex.Message}");
            return false;
        }
    }

    private sealed record ApiUser(int Id, string Name, string Username, string Email);
}

internal static class Backup
{
    public static string Create(string dbPath, string backupDir, Logger log)
    {
        Directory.CreateDirectory(backupDir);

        // Checkpoint переносит подтвержденные WAL-изменения в основной файл.
        using (var connection = new SqliteConnection($"Data Source={Path.GetFullPath(dbPath)}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(FULL);";
            command.ExecuteNonQuery();
        }

        var destination = Path.Combine(Path.GetFullPath(backupDir),
            $"campus-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.db.bak");
        File.Copy(Path.GetFullPath(dbPath), destination, false);

        var info = new FileInfo(destination);
        if (!info.Exists || info.Length == 0)
            throw new IOException("Backup file is missing or empty.");

        log.Info($"Backup created: {destination}; size={info.Length} bytes.");
        return destination;
    }

    public static void Restore(string backupPath, string dbPath, Logger log)
    {
        File.Copy(backupPath, Path.GetFullPath(dbPath), true);
        log.Info($"Database restored from backup: {backupPath}");
    }
}

internal static class Database
{
    public static async Task<int> GetVersionAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(path)}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public static async Task<int> MigrateToV2Async(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(path)}");
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        var version = await GetVersionInTransactionAsync(connection, transaction);

        if (version == 0)
        {
            await ExecuteAsync(connection, transaction,
                "CREATE TABLE IF NOT EXISTS Passes (Id INTEGER PRIMARY KEY AUTOINCREMENT, PersonId INTEGER NOT NULL, PassDate TEXT NOT NULL); PRAGMA user_version = 1;");
            version = 1;
        }

        var rows = 0;
        if (version < 2)
        {
            await ExecuteAsync(connection, transaction,
                "ALTER TABLE Passes ADD COLUMN AccessPoint TEXT NOT NULL DEFAULT 'UNKNOWN';");
            await ExecuteAsync(connection, transaction,
                "ALTER TABLE Passes ADD COLUMN Status TEXT NOT NULL DEFAULT 'Active';");
            await ExecuteAsync(connection, transaction, "PRAGMA user_version = 2;");

            await using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM Passes;";
            rows = Convert.ToInt32(await count.ExecuteScalarAsync());
        }

        await transaction.CommitAsync();
        return rows;
    }

    private static async Task<int> GetVersionInTransactionAsync(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(SqliteConnection connection,
        SqliteTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<Sanity> CheckAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(path)}");
        await connection.OpenAsync();

        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync());

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM Passes;";
        var rows = Convert.ToInt32(await countCommand.ExecuteScalarAsync());

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = "PRAGMA table_info(Passes);";
        await using var reader = await schemaCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));

        var columnsOk = columns.Contains("AccessPoint") && columns.Contains("Status");
        return new Sanity(version == 2 && rows >= 0 && columnsOk, version, rows, columnsOk);
    }
}

internal sealed record Sanity(bool Ok, int Version, int Rows, bool ColumnsOk);

internal static class DemoData
{
    public static async Task CreateV1Async(string path, Logger log)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (File.Exists(fullPath)) File.Delete(fullPath);

        await using var connection = new SqliteConnection($"Data Source={fullPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE Passes (Id INTEGER PRIMARY KEY AUTOINCREMENT, PersonId INTEGER NOT NULL, PassDate TEXT NOT NULL);" +
            "INSERT INTO Passes(PersonId, PassDate) VALUES (101, '2026-10-20T08:30:00Z'), (102, '2026-10-20T09:15:00Z'), (103, '2026-10-20T10:00:00Z');" +
            "PRAGMA user_version = 1;";
        await command.ExecuteNonQueryAsync();
        log.Info("Demo SQLite database created: schema v1, three sample records.");
    }
}

internal static class Report
{
    public static string Build(string db, string backup, int oldVersion,
        int migratedRows, bool apiAvailable, Sanity sanity) => $"""
        # Отчет о готовности АРМ к эксплуатации

        - Дата: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}
        - SQLite: `{Path.GetFullPath(db)}`
        - Версия схемы до запуска: {oldVersion}
        - Версия схемы после запуска: {sanity.Version}
        - Перенесено/проверено записей: {migratedRows}
        - Резервная копия: `{Path.GetFullPath(backup)}`
        - JSONPlaceholder API: {(apiAvailable ? "ДОСТУПЕН" : "НЕДОСТУПЕН; локальная миграция не зависит от него")}
        - Sanity: **{(sanity.Ok ? "PASS" : "FAIL")}**

        ## Контрольные проверки
        - `PRAGMA user_version = 2`: {(sanity.Version == 2 ? "PASS" : "FAIL")}
        - Таблица Passes доступна; число записей: {sanity.Rows}
        - Колонки AccessPoint и Status: {(sanity.ColumnsOk ? "PASS" : "FAIL")}

        ## Заключение
        {(sanity.Ok ? "Структура локальной БД прошла автоматические проверки. Перед промышленной эксплуатацией дополнительно проверить реальный сценарий прохода и оборудование считывателя." : "АРМ не готов к промышленной эксплуатации. Проверить журнал и восстановить резервную копию при необходимости.")}
        """;
}
