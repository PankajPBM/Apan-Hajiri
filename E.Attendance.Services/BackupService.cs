using System.Diagnostics;
using Npgsql;

namespace E.Attendance.Services;

public sealed record BackupRunResult(
    bool Success,
    string FileName,
    string FilePath,
    long? FileSizeBytes,
    string? ErrorMessage);

public sealed record RestoreRunResult(
    bool Success,
    string? ErrorMessage);

/// <summary>
/// Shared by both the web backend (scheduled/manual backups) and the WinForms tray app (restore,
/// plus the pre-restore safety dump). Both sides are C#, so unlike a split Node/desktop stack this
/// is one real implementation instead of two hand-kept-in-sync copies of the same pg_dump/psql logic.
/// </summary>
public static class BackupService
{
    public static string ResolvePgDumpExePath() => ResolvePostgreSqlBinaryPath("pg_dump.exe", "pg_dump");

    public static string ResolvePsqlExePath() => ResolvePostgreSqlBinaryPath("psql.exe", "psql");

    private static string ResolvePostgreSqlBinaryPath(string binaryFileName, string fallbackName)
    {
        // 1. Probe candidate directories from Windows Service ImagePath (Registry)
        if (OperatingSystem.IsWindows())
        {
            var serviceNames = new[]
            {
                "E_Attendance_PostgreSQL",
                "E-Attendance-PostgreSQL",
                "postgresql-x64-15",
                "postgresql-x64-18",
                "postgresql-x64-16",
                "postgresql-x64-17"
            };

            foreach (var svc in serviceNames)
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{svc}");
                    if (key?.GetValue("ImagePath") is string imagePath && !string.IsNullOrWhiteSpace(imagePath))
                    {
                        var extractedExe = ExtractExecutablePath(imagePath);
                        if (!string.IsNullOrWhiteSpace(extractedExe))
                        {
                            var binDir = Path.GetDirectoryName(extractedExe);
                            if (!string.IsNullOrWhiteSpace(binDir))
                            {
                                var candidate = Path.Combine(binDir, binaryFileName);
                                if (File.Exists(candidate)) return candidate;
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore registry read errors
                }
            }
        }

        // 2. Relative to AppContext.BaseDirectory
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir))
        {
            var localCandidates = new[]
            {
                Path.Combine(baseDir, "PostgreSQL", "18", "bin", binaryFileName),
                Path.Combine(baseDir, "PostgreSQL", "15", "bin", binaryFileName),
                Path.Combine(baseDir, "..", "PostgreSQL", "18", "bin", binaryFileName),
                Path.Combine(baseDir, "..", "PostgreSQL", "15", "bin", binaryFileName),
                Path.Combine(baseDir, "..", "..", "PostgreSQL", "18", "bin", binaryFileName),
                Path.Combine(baseDir, "..", "..", "PostgreSQL", "15", "bin", binaryFileName),
            };

            foreach (var cand in localCandidates)
            {
                try
                {
                    var fullPath = Path.GetFullPath(cand);
                    if (File.Exists(fullPath)) return fullPath;
                }
                catch
                {
                    // Ignore invalid path errors
                }
            }
        }

        // 3. Known Program Files and System installation paths
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var searchRoots = new[] { programFiles, programFilesX86, @"C:\Program Files", @"C:\Program Files (x86)" }
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var subPaths = new[]
        {
            Path.Combine("E-Attendance", "PostgreSQL", "15", "bin", binaryFileName),
            Path.Combine("E-Attendance", "PostgreSQL", "18", "bin", binaryFileName),
            Path.Combine("E_Attendance", "PostgreSQL", "15", "bin", binaryFileName),
            Path.Combine("E_Attendance", "PostgreSQL", "18", "bin", binaryFileName),
            Path.Combine("Apan Hajiri", "PostgreSQL", "15", "bin", binaryFileName),
            Path.Combine("Apan Hajiri", "PostgreSQL", "18", "bin", binaryFileName),
            Path.Combine("PostgreSQL", "15", "bin", binaryFileName),
            Path.Combine("PostgreSQL", "18", "bin", binaryFileName),
            Path.Combine("PostgreSQL", "16", "bin", binaryFileName),
            Path.Combine("PostgreSQL", "17", "bin", binaryFileName),
        };

        foreach (var root in searchRoots)
        {
            foreach (var sub in subPaths)
            {
                try
                {
                    var candidate = Path.Combine(root, sub);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // Ignore invalid path errors
                }
            }
        }

        // 4. Check workspace/repo installer location (for dev/debugging runs)
        try
        {
            var cur = baseDir;
            for (var i = 0; i < 5 && !string.IsNullOrWhiteSpace(cur); i++)
            {
                var devCandidate = Path.Combine(cur, "installer", "pgsql", "bin", binaryFileName);
                if (File.Exists(devCandidate)) return Path.GetFullPath(devCandidate);
                var parent = Directory.GetParent(cur);
                cur = parent?.FullName;
            }
        }
        catch
        {
            // Ignore dev path traversal errors
        }

        // 5. Check PATH environment variable directories
        try
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathVar))
            {
                foreach (var segment in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    try
                    {
                        var candidate = Path.Combine(segment, binaryFileName);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch
                    {
                        // Ignore invalid paths in PATH
                    }
                }
            }
        }
        catch
        {
            // Ignore environment variable read errors
        }

        return fallbackName;
    }

    private static string? ExtractExecutablePath(string rawImagePath)
    {
        if (string.IsNullOrWhiteSpace(rawImagePath)) return null;
        var trimmed = rawImagePath.Trim();
        if (trimmed.StartsWith("\""))
        {
            var closeQuote = trimmed.IndexOf('"', 1);
            if (closeQuote > 1)
            {
                return trimmed.Substring(1, closeQuote - 1);
            }
        }
        var spaceIdx = trimmed.IndexOf(' ');
        return spaceIdx > 0 ? trimmed.Substring(0, spaceIdx) : trimmed;
    }


    public static string ResolveDefaultBackupLocation()
    {
        try
        {
            var dDrive = new DriveInfo("D");
            if (dDrive.IsReady)
            {
                return @"D:\E_AttendanceBackup";
            }
        }
        catch
        {
            // fall through to the ProgramData default below
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "E_Attendance", "Backup");
    }

    /// <summary>
    /// Next run at the given time-of-day (server local time, e.g. "12:00"), on the cadence implied
    /// by frequency. If that time hasn't passed yet today, the next run is today; otherwise it's
    /// one full period ahead (tomorrow/next week/next month) at the same time — which also means a
    /// "weekly" schedule naturally locks onto whatever day-of-week its first run happened to land
    /// on, since every subsequent call here is invoked with fromUtc already past today's time.
    /// </summary>
    public static DateTime ComputeNextRunUtc(DateTime fromUtc, string frequency, string runTime)
    {
        var localNow = fromUtc.ToLocalTime();
        var timeOfDay = TimeSpan.TryParse(runTime, out var parsed) ? parsed : new TimeSpan(12, 0, 0);
        var candidate = localNow.Date + timeOfDay;
        if (candidate <= localNow)
        {
            candidate = frequency.Trim().ToLowerInvariant() switch
            {
                "weekly" => candidate.AddDays(7),
                "monthly" => candidate.AddMonths(1),
                _ => candidate.AddDays(1),
            };
        }
        return DateTime.SpecifyKind(candidate, DateTimeKind.Local).ToUniversalTime();
    }

    /// <summary>
    /// Full plain-SQL database dump. --clean --if-exists makes the resulting file self-contained:
    /// replaying it via psql -f drops and recreates every object it touches, so restore needs no
    /// separate "wipe the DB first" step.
    /// </summary>
    public static async Task<BackupRunResult> RunBackupAsync(
        string connectionString,
        string backupLocation,
        string appVersion,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(backupLocation);
        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        var fileName = $"db_{DateTime.UtcNow:yyyyMMddHHmmss}_{appVersion}.psql";
        var filePath = Path.Combine(backupLocation, fileName);

        var psi = new ProcessStartInfo
        {
            FileName = ResolvePgDumpExePath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-h");
        psi.ArgumentList.Add(csb.Host ?? "127.0.0.1");
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(csb.Port.ToString());
        psi.ArgumentList.Add("-U");
        psi.ArgumentList.Add(csb.Username ?? "");
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(csb.Database ?? "");
        psi.ArgumentList.Add("--format=plain");
        psi.ArgumentList.Add("--clean");
        psi.ArgumentList.Add("--if-exists");
        psi.ArgumentList.Add("--no-owner");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(filePath);
        psi.Environment["PGPASSWORD"] = csb.Password ?? "";

        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start pg_dump.");
            // Read stdout/stderr concurrently with waiting for exit — a synchronous read-after-exit
            // deadlocks once the dump is large enough to fill the OS pipe buffer.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var exited = await WaitForExitAsync(process, TimeSpan.FromMinutes(10), cancellationToken);
            if (!exited)
            {
                TryKill(process);
                return new BackupRunResult(false, fileName, filePath, null, "pg_dump timed out after 10 minutes.");
            }
            var stderr = await stderrTask;
            await stdoutTask;
            if (process.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(stderr) ? $"pg_dump exited with code {process.ExitCode}." : stderr.Trim();
                return new BackupRunResult(false, fileName, filePath, null, message);
            }
            var size = File.Exists(filePath) ? new FileInfo(filePath).Length : (long?)null;
            return new BackupRunResult(true, fileName, filePath, size, null);
        }
        catch (Exception ex)
        {
            return new BackupRunResult(false, fileName, filePath, null, ex.Message);
        }
    }

    /// <summary>
    /// Replays a backup file (produced by RunBackupAsync, or any --clean --if-exists plain-SQL
    /// dump) against the live database. ON_ERROR_STOP=1 makes psql actually abort on the first real
    /// SQL error instead of limping through a partially-broken restore.
    /// </summary>
    public static async Task<RestoreRunResult> RestoreBackupAsync(
        string connectionString,
        string backupFilePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupFilePath))
        {
            return new RestoreRunResult(false, $"Backup file not found: {backupFilePath}");
        }

        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        var psi = new ProcessStartInfo
        {
            FileName = ResolvePsqlExePath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-h");
        psi.ArgumentList.Add(csb.Host ?? "127.0.0.1");
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(csb.Port.ToString());
        psi.ArgumentList.Add("-U");
        psi.ArgumentList.Add(csb.Username ?? "");
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(csb.Database ?? "");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("ON_ERROR_STOP=1");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(backupFilePath);
        psi.Environment["PGPASSWORD"] = csb.Password ?? "";

        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start psql.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var exited = await WaitForExitAsync(process, TimeSpan.FromMinutes(10), cancellationToken);
            if (!exited)
            {
                TryKill(process);
                return new RestoreRunResult(false, "Restore timed out after 10 minutes — the database may be unusually large or partially restored. Check it manually before retrying.");
            }
            var stderr = await stderrTask;
            await stdoutTask;
            if (process.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(stderr) ? $"psql exited with code {process.ExitCode}." : stderr.Trim();
                return new RestoreRunResult(false, message);
            }
            return new RestoreRunResult(true, null);
        }
        catch (Exception ex)
        {
            return new RestoreRunResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Basic sanity check that a file actually looks like a Postgres plain-SQL dump before it's
    /// ever run against a live database — a bad/unrelated file used to only surface as a confusing
    /// psql error mid-restore. Not a full validator, just a first line of defense.
    /// </summary>
    public static bool LooksLikePostgresDump(string filePath)
    {
        try
        {
            using var reader = new StreamReader(filePath);
            for (var i = 0; i < 20; i++)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                if (line.Contains("PostgreSQL database dump", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("pg_dump", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // best-effort
        }
    }
}
