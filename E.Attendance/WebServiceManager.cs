using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using Npgsql;

namespace E.Attendance;

internal sealed class WebServiceManager
{
    private readonly object _sync = new();
    private Process? _ownedProcess;
    private Process? _ownedFrontendProcess;
    private DateTime _lastPgCheckUtc = DateTime.MinValue;
    private string _lastPgStatus = "Not tested";
    internal const int ApiDefaultPort = 8081;
    internal const int FrontendDefaultPort = 8001;
    private const int FrontendFallbackPort = 8000;
    private const string ExpectedPostgreSqlServiceName = "E_Attendance_PostgreSQL";
    private const string ExpectedPostgreSqlDatabase = "E_Attendace";
    private const string ExpectedPostgreSqlSchema = "E_Attendace";
    private const string ExpectedPostgreSqlUser = "E_Attendace";
    private static readonly string ExpectedPostgreSqlPassword = ResolveExpectedPostgreSqlPassword();
    private const int ExpectedPostgreSqlPort = 1277;
    private const string PostgreSqlWindowsDownloadUrl = "https://www.postgresql.org/download/windows/";
    private const string DefaultPostgreSqlInstallPath = @"C:\Program Files\PostgreSQL\18";
    private const string DefaultPostgreSqlBinPath = DefaultPostgreSqlInstallPath + @"\bin";
    private const string DefaultPostgreSqlDataPath = DefaultPostgreSqlInstallPath + @"\data";

    // Avoid hardcoded dev-machine paths. Allow override + sensible repo-relative defaults.
    private static readonly string DefaultOfflineInstallerPath = ResolveDefaultOfflineInstallerPath();

    private static string ResolveDefaultOfflineInstallerPath()
    {
        var envOverride = Environment.GetEnvironmentVariable("PB_ATTENDANCE_PG_INSTALLER_PATH");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            return Path.GetFullPath(envOverride.Trim());
        }

        const string fileName = "postgresql-18.3-1-windows-x64.exe";
        var candidates = new[]
        {
            Path.Combine(ResolveSolutionRoot(), "installer", fileName),
            Path.Combine(ResolveSolutionRoot(), fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(Environment.CurrentDirectory, fileName)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Return the best guess so the FileNotFoundException message tells the user where to put it.
        return candidates[0];
    }

    private int _activeFrontendPort;

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            // Includes InvalidOperationException: "No process is associated with this object."
            return true;
        }
    }

    private static void SafeKill(Process process)
    {
        try
        {
            if (!SafeHasExited(process))
            {
                process.Kill(true);
            }
        }
        catch
        {
        }
    }

    private static void SafeWaitForExit(Process process, int milliseconds)
    {
        try
        {
            if (!SafeHasExited(process))
            {
                process.WaitForExit(milliseconds);
            }
        }
        catch
        {
        }
    }
    
    // System.ServiceProcess assembly check for graceful fallback
    private static readonly bool ServiceControllerAvailable = CheckServiceControllerAvailable();
    
    private static bool CheckServiceControllerAvailable()
    {
        try
        {
            _ = typeof(ServiceController);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public int DefaultPort => ApiDefaultPort;

    public string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "E_Attendance");

    public string SettingsPath => Path.Combine(AppDataRoot, "websettings.json");
    public string PgAdminSecretPath => Path.Combine(AppDataRoot, "pgadmin.secret");
    public string MainDbPath => Path.Combine(AppDataRoot, "E_Attendace.db");
    public string ProductionDbPath => Path.Combine(AppDataRoot, "E_Attendace-prod.db");
    public string PostgresSetupScriptPath => ResolvePostgresSetupScriptPath();

    public void EnsureDefaults()
    {
        Directory.CreateDirectory(AppDataRoot);
        if (!File.Exists(SettingsPath))
        {
            SavePorts(DefaultPort, FrontendDefaultPort);
        }
    }

    public int LoadPort()
        => LoadSettings().Port;

    public int LoadFrontendPort()
        => LoadSettings().FrontendPort;

    public void SavePort(int port)
        => SavePorts(port, LoadFrontendPort());

    public void SaveFrontendPort(int frontendPort)
        => SavePorts(LoadPort(), frontendPort);

    public void SavePorts(int apiPort, int frontendPort)
    {
        if (apiPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(apiPort), "API port must be between 1 and 65535.");
        }

        if (frontendPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(frontendPort), "Web UI port must be between 1 and 65535.");
        }

        if (apiPort == frontendPort)
        {
            throw new InvalidOperationException(
                $"API Port and Web UI Port cannot be the same. Choose different ports (e.g. {ApiDefaultPort} and {FrontendDefaultPort}).");
        }

        Directory.CreateDirectory(AppDataRoot);
        var json = JsonSerializer.Serialize(new WebSettings(apiPort, frontendPort), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);

        // If the service is installed, re-register it to update the binPath (--urls)
        if (GetWindowsServiceInstalled())
        {
            InstallWindowsServices();
        }
    }

    public void SetupDatabases()
    {
        EnsurePostgreSqlControllerSettings();
        EnsurePostgreSqlServiceRunning();
        _ = EnsureExpectedPostgreSqlAccess(null);
        RunPostgreSqlSetupScript();
        var test = TestPostgreSqlConnection();
        if (!test.ok)
        {
            throw new InvalidOperationException(test.message);
        }
    }

    public void EnsurePostgreSqlReady()
    {
        _ = EnsurePostgreSqlControllerSettings();
        EnsurePostgreSqlServiceRunning();
        _ = EnsureExpectedPostgreSqlAccess(null);
        var test = TestPostgreSqlConnection();
        if (!test.ok)
        {
            throw new InvalidOperationException(test.message);
        }

        // Auto-fix database schema on startup - ensures all tables exist
        try
        {
            FixDatabaseSchemaInternal();
        }
        catch
        {
            // Non-fatal: API will also run schema init on first start
        }
    }

    /// <summary>
    /// Internal version of FixDatabaseSchema without throwing on connection test failures.
    /// Used during automatic startup initialization.
    /// </summary>
    public void FixDatabaseSchemaInternal()
    {
        var connString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(connString)) return;

        // 1. Ensure schema exists
        try { ExecuteSqlScript(connString, $"CREATE SCHEMA IF NOT EXISTS \"{ExpectedPostgreSqlSchema}\";"); } catch { }

        // 2. Create all core tables FIRST so the SQL scripts below can safely ALTER them
        try { EnsureCoreTablesExist(connString); } catch { }

        // 3. Run supplementary SQL scripts (add columns, biometric tables, etc.)
        var scripts = new[]
        {
            ("ensure_logs_transactions_tables.sql",       "AppLogs and TransactionDetails tables"),
            ("ensure_unified_biometrics_schema.sql",      "Biometric users/templates/photos tables"),
            ("ensure_device_capability_columns.sql",      "Device capability columns"),
        };
        foreach (var (fileName, _) in scripts)
        {
            try
            {
                var scriptPath = ResolveSqlScriptPath(fileName);
                if (!File.Exists(scriptPath)) continue;
                var sql = File.ReadAllText(scriptPath);
                ExecuteSqlScript(connString, sql, ExpectedPostgreSqlSchema);
            }
            catch { /* non-fatal, best-effort */ }
        }
    }

    public string EnsurePostgreSqlReadyWithReport(string? adminPasswordOverride = null)
    {
        var changed = EnsurePostgreSqlControllerSettings();
        EnsurePostgreSqlServiceRunning();
        var accessChanged = EnsureExpectedPostgreSqlAccess(adminPasswordOverride);
        var test = TestPostgreSqlConnection();
        if (!test.ok)
        {
            throw new InvalidOperationException(test.message);
        }

        if (changed || accessChanged)
        {
            return "DB controller settings were corrected and database is ready.";
        }

        return "Database is ready. DB controller settings were already correct.";
    }

    /// <summary>
    /// Runs all database fix scripts to ensure schema and tables are properly created.
    /// This creates/fixes: database, schema, user, tables (AppLogs, TransactionDetails, users, biometric_templates, biometric_photos),
    /// and adds missing columns to Devices table.
    /// </summary>
    public string FixDatabaseSchema(string? adminPasswordOverride = null)
    {
        var sb = new StringBuilder();
        
        // Step 1: Ensure basic connection settings are correct
        var changed = EnsurePostgreSqlControllerSettings();
        EnsurePostgreSqlServiceRunning();
        var accessChanged = EnsureExpectedPostgreSqlAccess(adminPasswordOverride);
        var test = TestPostgreSqlConnection();
        if (!test.ok)
        {
            throw new InvalidOperationException(test.message);
        }
        
        sb.AppendLine("✓ Connection settings validated.");

        // Step 2: Get the connection string for the application database
        var connString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(connString))
        {
            throw new InvalidOperationException("PostgreSQLConnection not found in appsettings.json");
        }

        // Step 3: Ensure schema exists AND set search_path for the connection
        try
        {
            using var conn = new NpgsqlConnection(connString);
            conn.Open();
            
            // Create schema if not exists
            var schemaSql = $"CREATE SCHEMA IF NOT EXISTS \"{ExpectedPostgreSqlSchema}\";";
            using var cmd = new NpgsqlCommand(schemaSql, conn);
            cmd.ExecuteNonQuery();
            
            // Set search_path for this connection
            var searchPathSql = $"SET search_path TO \"{ExpectedPostgreSqlSchema}\";";
            cmd.CommandText = searchPathSql;
            cmd.ExecuteNonQuery();
            
            sb.AppendLine($"✓ Schema '{ExpectedPostgreSqlSchema}' ensured and search_path set.");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"✗ FAILED: Schema creation - {RedactSecrets(ex.Message)}");
        }

        // Step 4: Create ALL core tables first (so scripts that ALTER them don't fail)
        try
        {
            EnsureCoreTablesExist(connString);
            sb.AppendLine("✓ Core tables (Employees, AttendanceRecords, Devices, Areas, Departments, etc.) verified/created.");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"✗ FAILED: Core tables - {RedactSecrets(ex.Message)}");
        }

        // Step 5: Run supplementary SQL scripts (add columns, biometrics, etc.)
        var scripts = new[]
        {
            ("ensure_logs_transactions_tables.sql",  "Create AppLogs and TransactionDetails tables"),
            ("ensure_unified_biometrics_schema.sql", "Create users, biometric_templates, biometric_photos tables"),
            ("ensure_device_capability_columns.sql", "Add capability columns to Devices table"),
        };

        using (var sharedConn = new NpgsqlConnection(connString))
        {
            sharedConn.Open();
            using (var setCmd = new NpgsqlCommand($"SET search_path TO \"{ExpectedPostgreSqlSchema}\";", sharedConn))
                setCmd.ExecuteNonQuery();

            foreach (var (fileName, description) in scripts)
            {
                try
                {
                    var scriptPath = ResolveSqlScriptPath(fileName);
                    if (!File.Exists(scriptPath))
                    {
                        sb.AppendLine($"⚠ Script not found: {fileName} (expected at {scriptPath})");
                        continue;
                    }
                    var sql = File.ReadAllText(scriptPath);
                    sql = sql.Replace($"SET search_path TO \"{ExpectedPostgreSqlSchema}\";", "", StringComparison.OrdinalIgnoreCase);
                    sql = sql.Replace($"SET search_path TO {ExpectedPostgreSqlSchema};", "", StringComparison.OrdinalIgnoreCase);
                    sql = sql.Replace("SET search_path TO E_Attendace;", "", StringComparison.OrdinalIgnoreCase);
                    using var cmd = new NpgsqlCommand(sql, sharedConn) { CommandTimeout = 120 };
                    cmd.ExecuteNonQuery();
                    sb.AppendLine($"✓ {description} ({fileName})");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"✗ FAILED: {description} ({fileName}) - {RedactSecrets(ex.Message)}");
                }
            }
        }

        // Step 6: Verify all expected tables exist
        var verification = VerifyExpectedTables(connString);
        sb.AppendLine("");
        sb.AppendLine("=== Verification ===");
        sb.AppendLine(verification);

        return sb.ToString();
    }

    private string ResolveSqlScriptPath(string fileName)
    {
        // Try multiple locations - prioritize source files over output directory to ensure we use latest fixes
        var candidates = new[]
        {
            Path.Combine(ResolveSolutionRoot(), "E.Attendance.Web", fileName),
            Path.Combine(ResolveSolutionRoot(), fileName),
            Path.Combine(Environment.CurrentDirectory, "E.Attendance.Web", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(Environment.CurrentDirectory, fileName),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Return first candidate for error reporting
        return candidates[0];
    }

    private void ExecuteSqlScript(string connectionString, string sql, string? schema = null)
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 120;
        if (!string.IsNullOrWhiteSpace(schema))
        {
            cmd.CommandText = $"SET search_path TO \"{schema}\";";
            cmd.ExecuteNonQuery();
            cmd.CommandText = sql;
        }
        cmd.ExecuteNonQuery();
    }

    private void EnsureCoreTablesExist(string connectionString)
    {
        var schema = ExpectedPostgreSqlSchema;  // "E_Attendace"
        var user   = ExpectedPostgreSqlUser;    // "E_Attendace"

        // Each statement runs independently — one failure won't block the rest.
        var statements = new[]
        {
            $"CREATE SCHEMA IF NOT EXISTS \"{schema}\";",
            $"SET search_path TO \"{schema}\";",
            $"GRANT USAGE ON SCHEMA \"{schema}\" TO \"{user}\";",

            // Employees
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."Employees" (
                "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                "EmployeeCode" TEXT NOT NULL DEFAULT '',
                "FirstName" TEXT NOT NULL DEFAULT '',
                "LastName" TEXT NOT NULL DEFAULT '',
                "FullName" TEXT NOT NULL DEFAULT '',
                "Email" TEXT NULL,
                "Phone" TEXT NULL,
                "BranchId" INTEGER NULL,
                "DepartmentId" INTEGER NULL,
                "DesignationId" INTEGER NULL,
                "ShiftId" INTEGER NULL,
                "JoinDate" date NULL,
                "Status" TEXT NOT NULL DEFAULT 'active',
                "CreatedAt" timestamptz NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamptz NOT NULL DEFAULT NOW()
            );
            """,
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Employees_EmployeeCode\" ON \"{schema}\".\"Employees\" (\"EmployeeCode\") WHERE \"EmployeeCode\" <> '';",

            // AttendanceRecords
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."AttendanceRecords" (
                "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                "PersonnelId" TEXT NOT NULL DEFAULT '',
                "AttendanceDate" date NOT NULL,
                "FirstIn" timestamptz NULL,
                "LastOut" timestamptz NULL,
                "TotalWorkHours" numeric(10,2) NOT NULL DEFAULT 0,
                "LateMinutes" INTEGER NOT NULL DEFAULT 0,
                "EarlyExitMinutes" INTEGER NOT NULL DEFAULT 0,
                "OvertimeMinutes" INTEGER NOT NULL DEFAULT 0,
                "Status" TEXT NOT NULL DEFAULT 'Present',
                "ProcessedAt" timestamptz NOT NULL DEFAULT NOW()
            );
            """,

            // AppLogs
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."AppLogs" (
                "Id" TEXT PRIMARY KEY,
                "TimestampUtc" timestamptz NOT NULL DEFAULT NOW(),
                "Level" TEXT NOT NULL DEFAULT 'INFO',
                "Source" TEXT NOT NULL DEFAULT '',
                "Message" TEXT NOT NULL DEFAULT '',
                "Detail" TEXT NULL
            );
            """,
            $"CREATE INDEX IF NOT EXISTS \"IX_AppLogs_TimestampUtc\" ON \"{schema}\".\"AppLogs\" (\"TimestampUtc\" DESC);",

            // TransactionDetails
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."TransactionDetails" (
                "Id" TEXT PRIMARY KEY,
                "ReceivedUtc" timestamptz NOT NULL DEFAULT NOW(),
                "DeviceSerialNumber" TEXT NULL,
                "SourceTable" TEXT NOT NULL DEFAULT '',
                "RawLine" TEXT NOT NULL DEFAULT '',
                "EmployeeCode" TEXT NULL,
                "PunchTime" timestamptz NULL,
                "StatusCode" INTEGER NULL,
                "VerifyMode" INTEGER NULL,
                "Workcode" TEXT NULL,
                "ParsedOk" INTEGER NOT NULL DEFAULT 0
            );
            """,
            $"CREATE INDEX IF NOT EXISTS \"IX_TransactionDetails_ReceivedUtc\" ON \"{schema}\".\"TransactionDetails\" (\"ReceivedUtc\" DESC);",
            $"CREATE INDEX IF NOT EXISTS \"IX_TransactionDetails_EmployeeCode\" ON \"{schema}\".\"TransactionDetails\" (\"EmployeeCode\");",
            $"CREATE INDEX IF NOT EXISTS \"IX_TransactionDetails_DeviceSerial\" ON \"{schema}\".\"TransactionDetails\" (\"DeviceSerialNumber\");",
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"UX_TransactionDetails_Source_Raw\" ON \"{schema}\".\"TransactionDetails\" (COALESCE(\"DeviceSerialNumber\",''),\"SourceTable\",\"RawLine\");",

            // Timetables
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."Timetables" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "TimetableName" TEXT NOT NULL,
                "IsActive" BOOLEAN NOT NULL DEFAULT TRUE,
                "CreatedAt" timestamptz NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamptz NOT NULL DEFAULT NOW()
            );
            """,

            // BreakTimes
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."BreakTimes" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "BreakName" TEXT NOT NULL,
                "StartTime" time NOT NULL,
                "EndTime" time NOT NULL,
                "DurationMinutes" INTEGER NOT NULL DEFAULT 0,
                "CalculateType" TEXT NOT NULL DEFAULT 'auto_deduct',
                "MinimumBreakMinutes" INTEGER NOT NULL DEFAULT 0,
                "IsActive" BOOLEAN NOT NULL DEFAULT TRUE
            );
            """,

            // TimetableDays
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."TimetableDays" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "TimetableId" INTEGER NOT NULL REFERENCES "{schema}"."Timetables"("Id") ON DELETE CASCADE,
                "DayOfWeek" INTEGER NOT NULL,
                "IsWeeklyOff" BOOLEAN NOT NULL DEFAULT FALSE,
                "CheckInTime" time NOT NULL DEFAULT TIME '10:00',
                "CheckInStartTime" time NOT NULL DEFAULT TIME '00:00',
                "CheckInEndTime" time NOT NULL DEFAULT TIME '10:30',
                "CheckOutStartTime" time NOT NULL DEFAULT TIME '17:00',
                "CheckOutTime" time NOT NULL DEFAULT TIME '23:59',
                "CheckOutEndTime" time NOT NULL DEFAULT TIME '00:59',
                "CrossDay" BOOLEAN NOT NULL DEFAULT FALSE,
                "UseBreakTime" BOOLEAN NOT NULL DEFAULT FALSE,
                "BreakTimeId" INTEGER NULL
            );
            """,

            // TimetableDayBreakTimes
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."TimetableDayBreakTimes" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "TimetableDayId" INTEGER NOT NULL REFERENCES "{schema}"."TimetableDays"("Id") ON DELETE CASCADE,
                "BreakTimeId" INTEGER NOT NULL REFERENCES "{schema}"."BreakTimes"("Id") ON DELETE CASCADE,
                "SortOrder" INTEGER NOT NULL DEFAULT 0
            );
            """,

            // EmployeeTimetables
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."EmployeeTimetables" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "PersonnelId" TEXT NOT NULL,
                "TimetableId" INTEGER NOT NULL REFERENCES "{schema}"."Timetables"("Id") ON DELETE CASCADE,
                "EffectiveFrom" date NOT NULL,
                "IsActive" BOOLEAN NOT NULL DEFAULT TRUE
            );
            """,

            // DepartmentTimetables
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."DepartmentTimetables" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "DepartmentId" INTEGER NOT NULL,
                "TimetableId" INTEGER NOT NULL REFERENCES "{schema}"."Timetables"("Id") ON DELETE CASCADE,
                "EffectiveFrom" date NOT NULL,
                "IsActive" BOOLEAN NOT NULL DEFAULT TRUE
            );
            """,

            // Holidays
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."Holidays" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "HolidayDate" date NOT NULL,
                "HolidayName" TEXT NOT NULL,
                "Description" TEXT NULL,
                "IsRecurring" BOOLEAN NOT NULL DEFAULT FALSE,
                "DepartmentId" INTEGER NULL,
                "IsActive" BOOLEAN NOT NULL DEFAULT TRUE
            );
            """,

            // AttendancePolicies
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."AttendancePolicies" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "WeeklyOffWorkAsOvertime" BOOLEAN NOT NULL DEFAULT TRUE,
                "HolidayWorkAsOvertime" BOOLEAN NOT NULL DEFAULT TRUE,
                "UpdatedAt" timestamptz NOT NULL DEFAULT NOW()
            );
            """,
            $"INSERT INTO \"{schema}\".\"AttendancePolicies\" (\"WeeklyOffWorkAsOvertime\",\"HolidayWorkAsOvertime\") SELECT true,true WHERE NOT EXISTS (SELECT 1 FROM \"{schema}\".\"AttendancePolicies\");",

            // LeaveTypes
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."LeaveTypes" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "LeaveName" TEXT NOT NULL,
                "IsPaid" BOOLEAN NOT NULL DEFAULT TRUE,
                "MaxPerYear" numeric(10,2) NULL,
                "CarryForward" BOOLEAN NOT NULL DEFAULT FALSE,
                "AllowHalfDay" BOOLEAN NOT NULL DEFAULT FALSE,
                "IsActive" BOOLEAN NOT NULL DEFAULT TRUE
            );
            """,
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"UX_LeaveTypes_LeaveName\" ON \"{schema}\".\"LeaveTypes\" (\"LeaveName\");",
            $"INSERT INTO \"{schema}\".\"LeaveTypes\" (\"LeaveName\",\"IsPaid\",\"MaxPerYear\") VALUES ('Casual Leave',true,12) ON CONFLICT DO NOTHING;",
            $"INSERT INTO \"{schema}\".\"LeaveTypes\" (\"LeaveName\",\"IsPaid\",\"MaxPerYear\") VALUES ('Sick Leave',true,10) ON CONFLICT DO NOTHING;",
            $"INSERT INTO \"{schema}\".\"LeaveTypes\" (\"LeaveName\",\"IsPaid\",\"MaxPerYear\") VALUES ('Annual Leave',true,15) ON CONFLICT DO NOTHING;",

            // LeaveRequests
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."LeaveRequests" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "PersonnelId" TEXT NOT NULL,
                "LeaveTypeId" INTEGER NOT NULL REFERENCES "{schema}"."LeaveTypes"("Id") ON DELETE CASCADE,
                "FromDate" date NOT NULL,
                "ToDate" date NOT NULL,
                "TotalDays" numeric(10,2) NOT NULL DEFAULT 0,
                "IsHalfDay" BOOLEAN NOT NULL DEFAULT FALSE,
                "HalfDayType" TEXT NULL,
                "Reason" TEXT NULL,
                "Status" TEXT NOT NULL DEFAULT 'pending',
                "AppliedDate" timestamptz NOT NULL DEFAULT NOW(),
                "ApprovedBy" TEXT NULL,
                "ApprovedDate" timestamptz NULL,
                "CancelledDate" timestamptz NULL
            );
            """,

            // LeaveBalances
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."LeaveBalances" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "PersonnelId" TEXT NOT NULL,
                "LeaveTypeId" INTEGER NOT NULL REFERENCES "{schema}"."LeaveTypes"("Id") ON DELETE CASCADE,
                "Year" INTEGER NOT NULL,
                "TotalAllocated" numeric(10,2) NOT NULL DEFAULT 0,
                "Used" numeric(10,2) NOT NULL DEFAULT 0,
                "Remaining" numeric(10,2) NOT NULL DEFAULT 0,
                "UpdatedAt" timestamptz NOT NULL DEFAULT NOW()
            );
            """,

            // LeaveAttachments
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."LeaveAttachments" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "LeaveRequestId" INTEGER NOT NULL REFERENCES "{schema}"."LeaveRequests"("Id") ON DELETE CASCADE,
                "FileName" TEXT NOT NULL,
                "FileUrl" TEXT NOT NULL,
                "UploadedAt" timestamptz NOT NULL DEFAULT NOW(),
                "UploadedBy" TEXT NULL
            );
            """,

            // LeaveRemarks
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."LeaveRemarks" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "LeaveRequestId" INTEGER NOT NULL REFERENCES "{schema}"."LeaveRequests"("Id") ON DELETE CASCADE,
                "Action" TEXT NOT NULL,
                "Remarks" TEXT NULL,
                "ActorId" TEXT NULL,
                "CreatedAt" timestamptz NOT NULL DEFAULT NOW()
            );
            """,

            // PersonnelAreas
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."PersonnelAreas" (
                "Id" TEXT PRIMARY KEY,
                "Code" TEXT NOT NULL UNIQUE,
                "Name" TEXT NOT NULL,
                "Remark" TEXT NOT NULL DEFAULT '',
                "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
            );
            """,
            $"INSERT INTO \"{schema}\".\"PersonnelAreas\" (\"Id\",\"Code\",\"Name\",\"Remark\") VALUES (gen_random_uuid()::text,'1','Default Area','') ON CONFLICT (\"Code\") DO NOTHING;",

            // PersonnelDepartments
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."PersonnelDepartments" (
                "Id" TEXT PRIMARY KEY,
                "Code" TEXT NOT NULL UNIQUE,
                "Name" TEXT NOT NULL,
                "Superior" TEXT NOT NULL DEFAULT '',
                "Manager" TEXT NOT NULL DEFAULT '',
                "AreaCode" TEXT NOT NULL DEFAULT '1',
                "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
            );
            """,
            $"INSERT INTO \"{schema}\".\"PersonnelDepartments\" (\"Id\",\"Code\",\"Name\",\"AreaCode\") VALUES (gen_random_uuid()::text,'1','Default Department','1') ON CONFLICT (\"Code\") DO NOTHING;",

            // PersonnelPositions
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."PersonnelPositions" (
                "Id" TEXT PRIMARY KEY,
                "Code" TEXT NOT NULL UNIQUE,
                "Name" TEXT NOT NULL,
                "Department" TEXT NOT NULL DEFAULT '',
                "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
            );
            """,

            // PersonnelDeviceAssignments
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."PersonnelDeviceAssignments" (
                "DeviceSerial" TEXT PRIMARY KEY,
                "AreaCode" TEXT NOT NULL DEFAULT '1',
                "DepartmentCode" TEXT NOT NULL DEFAULT '1',
                "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
            );
            """,

            // PersonnelEmployeeProfiles
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."PersonnelEmployeeProfiles" (
                "EmployeeCode" TEXT PRIMARY KEY,
                "Privilege" TEXT NOT NULL DEFAULT 'User',
                "CardNumber" TEXT NOT NULL DEFAULT '',
                "PictureDataUrl" TEXT NOT NULL DEFAULT '',
                "MobileNumber" TEXT NOT NULL DEFAULT '',
                "Gender" TEXT NOT NULL DEFAULT 'Male',
                "HireDate" TEXT NOT NULL DEFAULT '',
                "Password" TEXT NOT NULL DEFAULT '',
                "Position" TEXT NOT NULL DEFAULT '',
                "AreaCode" TEXT NOT NULL DEFAULT '',
                "DepartmentCode" TEXT NOT NULL DEFAULT '',
                "AssignmentsJson" TEXT NOT NULL DEFAULT '[]',
                "VerifyMode" INTEGER NOT NULL DEFAULT 0,
                "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
            );
            """,

            // auth_users
            $"CREATE TABLE IF NOT EXISTS \"{schema}\".\"auth_users\" (" +
            "\"Id\" SERIAL PRIMARY KEY," +
            "\"UserName\" TEXT NOT NULL UNIQUE," +
            "\"PasswordHash\" TEXT NOT NULL DEFAULT ''," +
            "\"FullName\" TEXT NULL," +
            "\"Role\" TEXT NOT NULL DEFAULT 'Operator'," +
            "\"Status\" TEXT NOT NULL DEFAULT 'active'," +
            "\"FeatureAccessJson\" jsonb NOT NULL DEFAULT '{}'::jsonb," +
            "\"CreatedAtUtc\" timestamptz NOT NULL DEFAULT NOW()," +
            "\"UpdatedAtUtc\" timestamptz NOT NULL DEFAULT NOW()" +
            ");",
            // Default admin — password PB@TechZone; replaced on first startup if wrong
            $"INSERT INTO \"{schema}\".\"auth_users\" (\"UserName\",\"PasswordHash\",\"FullName\",\"Role\",\"Status\",\"FeatureAccessJson\") VALUES ('admin',md5('PB@TechZone'),'Admin User','Admin','active','{{}}') ON CONFLICT DO NOTHING;",

            // auth_login_attempts — correct schema required by login lockout logic
            // UserName is PRIMARY KEY; FailureCount/LockedUntilUtc drive lockout
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."auth_login_attempts" (
                "UserName" TEXT PRIMARY KEY,
                "FailureCount" INTEGER NOT NULL DEFAULT 0,
                "LastFailedAtUtc" timestamptz NULL,
                "LockedUntilUtc" timestamptz NULL,
                "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
            );
            """,
            // Patch existing tables that were created with the old wrong schema (add missing columns)
            $"ALTER TABLE \"{schema}\".\"auth_login_attempts\" ADD COLUMN IF NOT EXISTS \"FailureCount\" INTEGER NOT NULL DEFAULT 0;",
            $"ALTER TABLE \"{schema}\".\"auth_login_attempts\" ADD COLUMN IF NOT EXISTS \"LastFailedAtUtc\" timestamptz NULL;",
            $"ALTER TABLE \"{schema}\".\"auth_login_attempts\" ADD COLUMN IF NOT EXISTS \"LockedUntilUtc\" timestamptz NULL;",
            $"ALTER TABLE \"{schema}\".\"auth_login_attempts\" ADD COLUMN IF NOT EXISTS \"UpdatedAtUtc\" timestamptz NOT NULL DEFAULT NOW();",
            // Ensure UserName is unique (needed for ON CONFLICT ... DO UPDATE)
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"UX_auth_login_attempts_UserName\" ON \"{schema}\".\"auth_login_attempts\" (LOWER(TRIM(\"UserName\")));",

            // app_config
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."app_config" (
                "ConfigKey" TEXT PRIMARY KEY,
                "JsonValue" TEXT NOT NULL DEFAULT '',
                "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
            );
            """,

            // ── Devices (ZKTeco devices — full schema with all capability columns) ────
            // Use $$ to allow literal {} in JSONB default without escaping issues
            $$"""
            CREATE TABLE IF NOT EXISTS "{{schema}}"."Devices" (
                "Id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                "SerialNumber" TEXT NOT NULL,
                "Name" TEXT NOT NULL DEFAULT '',
                "Model" TEXT NOT NULL DEFAULT '',
                "IpAddress" TEXT NOT NULL DEFAULT '',
                "Location" TEXT NOT NULL DEFAULT '',
                "TimeZone" TEXT NOT NULL DEFAULT '+5:45',
                "SupportsVisibleLightFace" BOOLEAN NOT NULL DEFAULT FALSE,
                "SupportsInfraredFace" BOOLEAN NOT NULL DEFAULT FALSE,
                "SupportsFingerprint" BOOLEAN NOT NULL DEFAULT FALSE,
                "FaceAlgorithmVersion" TEXT NOT NULL DEFAULT '',
                "FingerprintAlgorithmVersion" TEXT NOT NULL DEFAULT '',
                "FirmwareVersion" TEXT NOT NULL DEFAULT '',
                "DeviceType" TEXT NOT NULL DEFAULT '',
                "PushVer" TEXT NOT NULL DEFAULT '',
                "Language" INTEGER NOT NULL DEFAULT 0,
                "MultiBioDataSupport" TEXT NOT NULL DEFAULT '',
                "MultiBioPhotoSupport" TEXT NOT NULL DEFAULT '',
                "MultiBioVersion" TEXT NOT NULL DEFAULT '',
                "MaxMultiBioDataCount" TEXT NOT NULL DEFAULT '',
                "MaxMultiBioPhotoCount" TEXT NOT NULL DEFAULT '',
                "FingerFunOn" BOOLEAN NOT NULL DEFAULT FALSE,
                "FaceFunOn" BOOLEAN NOT NULL DEFAULT FALSE,
                "VisiLightFunOn" BOOLEAN NOT NULL DEFAULT FALSE,
                "BioDataFun" BOOLEAN NOT NULL DEFAULT FALSE,
                "BioPhotoFun" BOOLEAN NOT NULL DEFAULT FALSE,
                "SupportsPalm" BOOLEAN NOT NULL DEFAULT FALSE,
                "SupportsFingerVein" BOOLEAN NOT NULL DEFAULT FALSE,
                "CapabilitiesJson" JSONB NOT NULL DEFAULT '{}',
                "CreatedAtUtc" TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                "UpdatedAtUtc" TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                "Status" INTEGER NOT NULL DEFAULT 0,
                "IsRegistrationDevice" BOOLEAN NOT NULL DEFAULT FALSE,
                "IsOnline" BOOLEAN NOT NULL DEFAULT FALSE,
                "IsRegistered" BOOLEAN NOT NULL DEFAULT FALSE,
                "LastSeenUtc" TIMESTAMPTZ NULL,
                "AreaId" INTEGER NOT NULL DEFAULT 1,
                "DepartmentId" INTEGER NOT NULL DEFAULT 1
            );
            """,
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Devices_SerialNumber\" ON \"{schema}\".\"Devices\" (\"SerialNumber\");",
            $"CREATE INDEX IF NOT EXISTS \"IX_Devices_IsOnline\" ON \"{schema}\".\"Devices\" (\"IsOnline\");",
            $"CREATE INDEX IF NOT EXISTS \"IX_Devices_AreaDept\" ON \"{schema}\".\"Devices\" (\"AreaId\",\"DepartmentId\");",

            // ── DeviceCommands ───────────────────────────────────────────────────────
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."DeviceCommands" (
                "Id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                "DeviceSerialNumber" TEXT NOT NULL,
                "CommandText" TEXT NOT NULL,
                "CreatedUtc" TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                "SentUtc" TIMESTAMPTZ NULL,
                "ReplyText" TEXT NULL
            );
            """,
            $"CREATE INDEX IF NOT EXISTS \"IX_DeviceCommands_Pending\" ON \"{schema}\".\"DeviceCommands\" (\"DeviceSerialNumber\",\"SentUtc\");",

            // ── DeviceSyncLogs ───────────────────────────────────────────────────────
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."DeviceSyncLogs" (
                "Id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                "DeviceSerialNumber" TEXT NOT NULL,
                "Type" INTEGER NOT NULL DEFAULT 0,
                "Status" INTEGER NOT NULL DEFAULT 0,
                "CommandText" TEXT NOT NULL DEFAULT '',
                "ResponseText" TEXT NULL,
                "CreatedUtc" TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                "CompletedUtc" TIMESTAMPTZ NULL
            );
            """,
            $"CREATE INDEX IF NOT EXISTS \"IX_DeviceSyncLogs_DeviceStatus\" ON \"{schema}\".\"DeviceSyncLogs\" (\"DeviceSerialNumber\",\"Status\",\"CreatedUtc\");",

            // ── DeviceTableStamps ────────────────────────────────────────────────────
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."DeviceTableStamps" (
                "DeviceSerialNumber" TEXT NOT NULL,
                "TableName" TEXT NOT NULL,
                "StampValue" BIGINT NOT NULL DEFAULT 0,
                "UpdatedUtc" TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY ("DeviceSerialNumber","TableName")
            );
            """,

            // ── Areas (integer-based hierarchy) ──────────────────────────────────────
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."Areas" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "IsDefault" BOOLEAN NOT NULL DEFAULT FALSE,
                "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                "UpdatedAt" TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE ("Name")
            );
            """,
            $"INSERT INTO \"{schema}\".\"Areas\" (\"Name\",\"IsDefault\") VALUES ('Default Area',TRUE) ON CONFLICT (\"Name\") DO NOTHING;",

            // ── Departments (integer-based, FK to Areas) ─────────────────────────────
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."Departments" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "AreaId" INTEGER NOT NULL DEFAULT 1,
                "IsDefault" BOOLEAN NOT NULL DEFAULT FALSE,
                "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                "UpdatedAt" TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE ("AreaId","Name")
            );
            """,
            $"INSERT INTO \"{schema}\".\"Departments\" (\"Name\",\"AreaId\",\"IsDefault\") VALUES ('Default Department',1,TRUE) ON CONFLICT (\"AreaId\",\"Name\") DO NOTHING;",

            // ── EmployeeDepartments (many-to-many junction) ───────────────────────────
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."EmployeeDepartments" (
                "EmployeeId" UUID NOT NULL,
                "DepartmentId" INTEGER NOT NULL,
                "AssignedAt" TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                "AssignedBy" UUID NULL,
                PRIMARY KEY ("EmployeeId","DepartmentId")
            );
            """,
            $"CREATE INDEX IF NOT EXISTS \"IX_EmployeeDepartments_EmployeeId\" ON \"{schema}\".\"EmployeeDepartments\" (\"EmployeeId\");",
            $"CREATE INDEX IF NOT EXISTS \"IX_EmployeeDepartments_DepartmentId\" ON \"{schema}\".\"EmployeeDepartments\" (\"DepartmentId\");",

            // ── SyncQueue (background device sync queue) ──────────────────────────────
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."SyncQueue" (
                "Id" BIGSERIAL PRIMARY KEY,
                "EntityType" TEXT NOT NULL,
                "EntityId" TEXT NOT NULL,
                "Operation" TEXT NOT NULL,
                "Status" INTEGER NOT NULL DEFAULT 0,
                "RetryCount" INTEGER NOT NULL DEFAULT 0,
                "MaxRetries" INTEGER NOT NULL DEFAULT 3,
                "Payload" TEXT NULL,
                "ErrorMessage" TEXT NULL,
                "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                "ProcessedAt" TIMESTAMPTZ NULL,
                "NextRetryAt" TIMESTAMPTZ NULL
            );
            """,
            $"CREATE INDEX IF NOT EXISTS \"IX_SyncQueue_Status\" ON \"{schema}\".\"SyncQueue\" (\"Status\");",

            // ── Biometric tables ──────────────────────────────────────────────────────
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."users" (
                pin VARCHAR(50) PRIMARY KEY,
                name VARCHAR(255),
                privilege INT,
                password VARCHAR(255),
                primary_card VARCHAR(255),
                secondary_card VARCHAR(255),
                group_id INT,
                timezone_profile VARCHAR(32),
                verify_mode INT,
                valid_from TIMESTAMP,
                valid_to TIMESTAMP,
                expires_enabled BOOLEAN,
                phone VARCHAR(50),
                gender INT,
                nation INT,
                id_number VARCHAR(50),
                created_at TIMESTAMP NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMP NOT NULL DEFAULT NOW()
            );
            """,

            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."biometric_templates" (
                id BIGSERIAL PRIMARY KEY,
                user_pin VARCHAR(50) NOT NULL REFERENCES "{schema}"."users"(pin) ON DELETE CASCADE,
                type INT NOT NULL,
                no INT NOT NULL,
                idx INT NOT NULL,
                valid BOOLEAN NOT NULL DEFAULT TRUE,
                duress BOOLEAN NOT NULL DEFAULT FALSE,
                major_ver INT,
                minor_ver INT,
                format INT,
                template_data TEXT,
                created_at TIMESTAMP NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
                UNIQUE (user_pin, type, no, idx)
            );
            """,

            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."biometric_photos" (
                id BIGSERIAL PRIMARY KEY,
                user_pin VARCHAR(50) NOT NULL REFERENCES "{schema}"."users"(pin) ON DELETE CASCADE,
                type INT NOT NULL,
                no INT NOT NULL,
                idx INT NOT NULL,
                format INT,
                content TEXT,
                created_at TIMESTAMP NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
                UNIQUE (user_pin, type, no, idx)
            );
            """,

            // ── Add missing columns to Employees (biometric/sync fields) ──────────────
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"Area\" TEXT NOT NULL DEFAULT '';",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"Department\" TEXT NOT NULL DEFAULT '';",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"AreaId\" INTEGER NOT NULL DEFAULT 1;",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"DepartmentId\" INTEGER NULL;",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"DepartmentsJson\" TEXT NOT NULL DEFAULT '[]';",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"Position\" TEXT NOT NULL DEFAULT '';",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"CardNumber\" TEXT NOT NULL DEFAULT '';",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"Password\" TEXT NOT NULL DEFAULT '';",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"MobileNumber\" TEXT NOT NULL DEFAULT '';",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"Gender\" TEXT NOT NULL DEFAULT '';",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"Privilege\" INTEGER NOT NULL DEFAULT 0;",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"VerifyMode\" INTEGER NOT NULL DEFAULT 0;",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"FaceTemplateVisible\" TEXT NULL;",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"FaceTemplateInfrared\" TEXT NULL;",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"ProfilePhoto\" TEXT NULL;",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"FingerprintTemplates\" TEXT NULL;",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"SyncedAt\" TIMESTAMPTZ NULL;",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"IsActive\" BOOLEAN NOT NULL DEFAULT TRUE;",

            // ── Triggers — protect default Area/Dept from deletion ────────────────────
            $"""
            CREATE OR REPLACE FUNCTION "{schema}".fn_protect_default_area()
            RETURNS TRIGGER LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD."Id" = 1 THEN
                    RAISE EXCEPTION 'Default Area (ID=1) cannot be deleted.';
                END IF;
                RETURN OLD;
            END; $$;
            """,
            $"DROP TRIGGER IF EXISTS trg_protect_default_area ON \"{schema}\".\"Areas\";",
            $"CREATE TRIGGER trg_protect_default_area BEFORE DELETE ON \"{schema}\".\"Areas\" FOR EACH ROW EXECUTE FUNCTION \"{schema}\".fn_protect_default_area();",

            $"""
            CREATE OR REPLACE FUNCTION "{schema}".fn_protect_default_department()
            RETURNS TRIGGER LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD."Id" = 1 THEN
                    RAISE EXCEPTION 'Default Department (ID=1) cannot be deleted.';
                END IF;
                RETURN OLD;
            END; $$;
            """,
            $"DROP TRIGGER IF EXISTS trg_protect_default_dept ON \"{schema}\".\"Departments\";",
            $"CREATE TRIGGER trg_protect_default_dept BEFORE DELETE ON \"{schema}\".\"Departments\" FOR EACH ROW EXECUTE FUNCTION \"{schema}\".fn_protect_default_department();",

            // ── Schema patch — fix every column mismatch from older installs ──────────
            // auth_users: old installer used uuid Id + "Username" (lowercase n) — patch to correct schema
            $"ALTER TABLE \"{schema}\".\"auth_users\" ADD COLUMN IF NOT EXISTS \"UserName\" TEXT;",
            $"UPDATE \"{schema}\".\"auth_users\" SET \"UserName\" = \"Username\" WHERE \"UserName\" IS NULL AND \"Username\" IS NOT NULL;",
            $"ALTER TABLE \"{schema}\".\"auth_users\" ADD COLUMN IF NOT EXISTS \"FullName\" TEXT NULL;",
            $"ALTER TABLE \"{schema}\".\"auth_users\" ADD COLUMN IF NOT EXISTS \"Role\" TEXT NOT NULL DEFAULT 'Operator';",
            $"ALTER TABLE \"{schema}\".\"auth_users\" ADD COLUMN IF NOT EXISTS \"Status\" TEXT NOT NULL DEFAULT 'active';",
            $"ALTER TABLE \"{schema}\".\"auth_users\" ADD COLUMN IF NOT EXISTS \"FeatureAccessJson\" jsonb NOT NULL DEFAULT '{{}}';",
            $"ALTER TABLE \"{schema}\".\"auth_users\" ADD COLUMN IF NOT EXISTS \"CreatedAtUtc\" timestamptz NOT NULL DEFAULT NOW();",
            $"ALTER TABLE \"{schema}\".\"auth_users\" ADD COLUMN IF NOT EXISTS \"UpdatedAtUtc\" timestamptz NOT NULL DEFAULT NOW();",
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"UX_auth_users_UserName\" ON \"{schema}\".\"auth_users\" (LOWER(TRIM(\"UserName\")));",

            // AttendanceRecords: old installer schema had DeviceSerial/PunchTime/PunchType — patch to correct schema
            $"ALTER TABLE \"{schema}\".\"AttendanceRecords\" ADD COLUMN IF NOT EXISTS \"EmployeeId\" TEXT;",
            $"ALTER TABLE \"{schema}\".\"AttendanceRecords\" ADD COLUMN IF NOT EXISTS \"EmployeeCode\" TEXT NOT NULL DEFAULT '';",
            $"ALTER TABLE \"{schema}\".\"AttendanceRecords\" ADD COLUMN IF NOT EXISTS \"Timestamp\" timestamptz;",
            $"UPDATE \"{schema}\".\"AttendanceRecords\" SET \"Timestamp\" = \"PunchTime\" WHERE \"Timestamp\" IS NULL AND \"PunchTime\" IS NOT NULL;",
            $"ALTER TABLE \"{schema}\".\"AttendanceRecords\" ADD COLUMN IF NOT EXISTS \"Status\" INTEGER NOT NULL DEFAULT 0;",
            $"ALTER TABLE \"{schema}\".\"AttendanceRecords\" ADD COLUMN IF NOT EXISTS \"StatusCode\" INTEGER NULL;",
            $"ALTER TABLE \"{schema}\".\"AttendanceRecords\" ADD COLUMN IF NOT EXISTS \"VerifyMode\" INTEGER NULL;",
            $"ALTER TABLE \"{schema}\".\"AttendanceRecords\" ADD COLUMN IF NOT EXISTS \"Workcode\" TEXT NULL;",
            $"ALTER TABLE \"{schema}\".\"AttendanceRecords\" ADD COLUMN IF NOT EXISTS \"DeviceId\" UUID NULL;",
            $"ALTER TABLE \"{schema}\".\"AttendanceRecords\" ADD COLUMN IF NOT EXISTS \"DeviceSerialNumber\" TEXT NULL;",
            $"UPDATE \"{schema}\".\"AttendanceRecords\" SET \"DeviceSerialNumber\" = \"DeviceSerial\" WHERE \"DeviceSerialNumber\" IS NULL AND \"DeviceSerial\" IS NOT NULL;",
            $"ALTER TABLE \"{schema}\".\"AttendanceRecords\" ADD COLUMN IF NOT EXISTS \"AreaId\" INTEGER NOT NULL DEFAULT 1;",
            $"ALTER TABLE \"{schema}\".\"AttendanceRecords\" ADD COLUMN IF NOT EXISTS \"DepartmentId\" INTEGER NOT NULL DEFAULT 1;",

            // Employees: patch all biometric/sync columns that may be missing in older installs
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"EmployeeCode\" TEXT NOT NULL DEFAULT '';",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"FullName\" TEXT NOT NULL DEFAULT '';",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"Area\" TEXT NOT NULL DEFAULT '';",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"Department\" TEXT NOT NULL DEFAULT '';",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"DepartmentId\" INTEGER NULL;",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"AreaId\" INTEGER NOT NULL DEFAULT 1;",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"DepartmentsJson\" TEXT NOT NULL DEFAULT '[]';",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"Position\" TEXT NOT NULL DEFAULT '';",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"CardNumber\" TEXT NOT NULL DEFAULT '';",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"Password\" TEXT NOT NULL DEFAULT '';",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"MobileNumber\" TEXT NOT NULL DEFAULT '';",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"Gender\" TEXT NOT NULL DEFAULT '';",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"Privilege\" INTEGER NOT NULL DEFAULT 0;",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"VerifyMode\" INTEGER NOT NULL DEFAULT 0;",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"FaceTemplateVisible\" TEXT NULL;",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"FaceTemplateInfrared\" TEXT NULL;",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"ProfilePhoto\" TEXT NULL;",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"FingerprintTemplates\" TEXT NULL;",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"SyncedAt\" timestamptz NULL;",
            $"ALTER TABLE \"{schema}\".\"Employees\" ADD COLUMN IF NOT EXISTS \"IsActive\" BOOLEAN NOT NULL DEFAULT TRUE;",
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Employees_EmployeeCode\" ON \"{schema}\".\"Employees\" (\"EmployeeCode\") WHERE \"EmployeeCode\" <> '';",

            // Devices: patch any missing columns that older installs might not have
            $"ALTER TABLE \"{schema}\".\"Devices\" ADD COLUMN IF NOT EXISTS \"Location\" TEXT NOT NULL DEFAULT '';",
            $"ALTER TABLE \"{schema}\".\"Devices\" ADD COLUMN IF NOT EXISTS \"IsRegistered\" BOOLEAN NOT NULL DEFAULT FALSE;",
            $"ALTER TABLE \"{schema}\".\"Devices\" ADD COLUMN IF NOT EXISTS \"AreaId\" INTEGER NOT NULL DEFAULT 1;",
            $"ALTER TABLE \"{schema}\".\"Devices\" ADD COLUMN IF NOT EXISTS \"DepartmentId\" INTEGER NOT NULL DEFAULT 1;",

            // Ensure default Area and Department rows are always present
            $"INSERT INTO \"{schema}\".\"Areas\" (\"Name\",\"IsDefault\") VALUES ('Default Area',TRUE) ON CONFLICT (\"Name\") DO NOTHING;",
            $"INSERT INTO \"{schema}\".\"Departments\" (\"Name\",\"AreaId\",\"IsDefault\") VALUES ('Default Department',1,TRUE) ON CONFLICT (\"AreaId\",\"Name\") DO NOTHING;",

            // Ensure default admin login works even after schema migration (password: PB@TechZone)
            $"INSERT INTO \"{schema}\".\"auth_users\" (\"UserName\",\"PasswordHash\",\"FullName\",\"Role\",\"Status\",\"FeatureAccessJson\") VALUES ('admin',md5('PB@TechZone'),'Admin User','Admin','active','{{}}') ON CONFLICT DO NOTHING;",

            // Grants
            $"GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA \"{schema}\" TO \"{user}\";",
            $"GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA \"{schema}\" TO \"{user}\";",
            $"ALTER DEFAULT PRIVILEGES IN SCHEMA \"{schema}\" GRANT ALL ON TABLES TO \"{user}\";",
            $"ALTER DEFAULT PRIVILEGES IN SCHEMA \"{schema}\" GRANT ALL ON SEQUENCES TO \"{user}\";",
        };

        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        foreach (var sql in statements)
        {
            try
            {
                using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 60 };
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Ignore per-statement failures — best-effort, never abort the whole fix
            }
        }
    }





    /// <summary>Returns a one-line summary of key entity counts for the DB stats label.</summary>
    public string GetDatabaseStats()
    {
        var connString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(connString)) return "Not connected";

        var schema = ExpectedPostgreSqlSchema;
        static int Count(NpgsqlConnection c, string sql)
        {
            try { using var cmd = new NpgsqlCommand(sql, c) { CommandTimeout = 5 }; return Convert.ToInt32(cmd.ExecuteScalar()); }
            catch { return -1; }
        }

        using var conn = new NpgsqlConnection(connString);
        conn.Open();
        var areas   = Count(conn, $"SELECT COUNT(*) FROM \"{schema}\".\"Areas\"");
        var depts   = Count(conn, $"SELECT COUNT(*) FROM \"{schema}\".\"Departments\"");
        var devices = Count(conn, $"SELECT COUNT(*) FROM \"{schema}\".\"Devices\"");
        var emps    = Count(conn, $"SELECT COUNT(*) FROM \"{schema}\".\"Employees\"");
        var attn    = Count(conn, $"SELECT COUNT(*) FROM \"{schema}\".\"AttendanceRecords\"");

        string F(int v) => v < 0 ? "–" : v.ToString();
        return $"Areas: {F(areas)} | Depts: {F(depts)} | Devices: {F(devices)} | Employees: {F(emps)} | Attendance: {F(attn)}";
    }

    private string VerifyExpectedTables(string connectionString)
    {
        var expectedTables = new[]
        {
            // Core attendance tables
            "Employees", "AttendanceRecords", "Devices",
            // Hierarchy
            "Areas", "Departments", "EmployeeDepartments",
            // Device protocol tables
            "DeviceCommands", "DeviceSyncLogs", "DeviceTableStamps", "SyncQueue",
            // Logging
            "AppLogs", "TransactionDetails",
            // Schedule/shift
            "Timetables", "BreakTimes", "TimetableDays", "TimetableDayBreakTimes",
            "EmployeeTimetables", "DepartmentTimetables",
            // Attendance management
            "Holidays", "AttendancePolicies",
            // Leave management
            "LeaveTypes", "LeaveRequests", "LeaveBalances", "LeaveAttachments", "LeaveRemarks",
            // Personnel management
            "PersonnelAreas", "PersonnelDepartments", "PersonnelPositions",
            "PersonnelDeviceAssignments", "PersonnelEmployeeProfiles",
            // Auth & config
            "auth_users", "auth_login_attempts", "app_config",
            // Biometric
            "users", "biometric_templates", "biometric_photos"
        };

        var sb = new StringBuilder();
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();

        foreach (var table in expectedTables)
        {
            using var cmd = new NpgsqlCommand(
                $@"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = '{ExpectedPostgreSqlSchema}' AND table_name = @table)",
                conn);
            cmd.Parameters.AddWithValue("table", table);
            var exists = cmd.ExecuteScalar() is true;
            sb.AppendLine(exists ? $"  ✓ {table}" : $"  ✗ MISSING: {table}");
        }

        return sb.ToString();
    }

    public string RepairPostgreSqlLogin(string adminPassword)
    {
        if (string.IsNullOrWhiteSpace(adminPassword))
        {
            throw new InvalidOperationException("PostgreSQL admin password is required.");
        }

        _ = EnsurePostgreSqlControllerSettings();
        EnsurePostgreSqlServiceRunning();

        var accessChanged = EnsureExpectedPostgreSqlAccess(adminPassword);
        var test = TestPostgreSqlConnection();
        if (!test.ok)
        {
            throw new InvalidOperationException(
                $"Could not repair PostgreSQL login. {test.message} " +
                "Verify postgres admin password and try again.");
        }

        SaveAdminPassword(adminPassword);

        return accessChanged
            ? $"Database login repaired for '{ExpectedPostgreSqlUser}'."
            : $"Database login for '{ExpectedPostgreSqlUser}' is already valid.";
    }

    public string SetApplicationDbPassword(string adminPassword, string applicationPassword)
    {
        if (string.IsNullOrWhiteSpace(adminPassword))
        {
            throw new InvalidOperationException("PostgreSQL admin password is required.");
        }

        if (string.IsNullOrWhiteSpace(applicationPassword))
        {
            throw new InvalidOperationException("Application DB password is required.");
        }

        SaveApplicationDbPassword(applicationPassword.Trim());
        _ = EnsurePostgreSqlControllerSettings();
        EnsurePostgreSqlServiceRunning();

        var accessChanged = EnsureExpectedPostgreSqlAccess(adminPassword);
        var test = TestPostgreSqlConnection();
        if (!test.ok)
        {
            throw new InvalidOperationException(
                $"Password was saved but connection test failed. {test.message}");
        }

        SaveAdminPassword(adminPassword);
        return accessChanged
            ? $"Application DB password updated and login repaired for '{ExpectedPostgreSqlUser}'."
            : $"Application DB password updated for '{ExpectedPostgreSqlUser}'.";
    }

    public string InitializePostgreSqlDatabase(string adminPassword)
    {
        if (string.IsNullOrWhiteSpace(adminPassword))
        {
            throw new InvalidOperationException("PostgreSQL admin password is required.");
        }

        // IMPORTANT: do NOT stop the web service or touch the database before the admin
        // password is confirmed valid. A wrong password must leave the running service and
        // the existing data completely untouched. We only ensure PostgreSQL itself is up
        // (starting it if stopped is non-destructive) so we can validate the credentials.
        _ = EnsurePostgreSqlControllerSettings();
        EnsurePostgreSqlServiceRunning();

        var expectedConnection = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(expectedConnection))
        {
            throw new InvalidOperationException("PostgreSQLConnection not found in appsettings.");
        }

        var expectedBuilder = new NpgsqlConnectionStringBuilder(expectedConnection);
        var host = string.IsNullOrWhiteSpace(expectedBuilder.Host) ? "127.0.0.1" : expectedBuilder.Host;
        var configuredPort = expectedBuilder.Port <= 0 ? ExpectedPostgreSqlPort : expectedBuilder.Port;

        var candidates = BuildPostgreSqlPortCandidates(configuredPort);
        NpgsqlConnectionStringBuilder? adminBuilder = null;
        Exception? lastError = null;

        // STEP 1 — validate the admin password with a read-only login probe only.
        // No service is stopped and no data is altered here.
        foreach (var candidatePort in candidates)
        {
            var candidateBuilder = new NpgsqlConnectionStringBuilder
            {
                Host = host,
                Port = candidatePort,
                Database = "postgres",
                Username = "postgres",
                Password = adminPassword,
                Timeout = 8,
                CommandTimeout = 60,
                IncludeErrorDetail = true
            };

            try
            {
                using var probe = new NpgsqlConnection(candidateBuilder.ConnectionString);
                probe.Open();
                adminBuilder = candidateBuilder;
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (adminBuilder is null)
        {
            var portsLabel = string.Join(", ", candidates);
            var message = lastError is null ? "Unknown error." : RedactSecrets(lastError.Message);
            throw new InvalidOperationException(
                $"Incorrect PostgreSQL admin password (or server unreachable) on {host} (ports tried: {portsLabel}). " +
                "No changes were made — the running service and existing data were left untouched. " + message,
                lastError);
        }

        // STEP 2 — password confirmed. Only now is it safe to stop the service and reset
        // the application database.
        StopServiceForDatabaseMaintenance();

        using (var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            adminConnection.Open();
            EnsureApplicationRole(adminConnection, GetConfiguredApplicationDbPassword());
            ResetApplicationDatabase(adminConnection);
        }

        var port = adminBuilder.Port;

        // Post-init steps require a live connection to the new DB.
        // If PostgreSQL isn't fully warmed up yet, skip — API completes schema on first start.
        try { ConfigureInitializedDatabase(adminBuilder, host, port); } catch { }
        try { EnsurePostgreSqlObjectOwnership(adminBuilder, host, port); } catch { }

        SaveAdminPassword(adminPassword);
        UpdatePostgreSqlConnectionHostPort(host, port);

        return $"Database '{ExpectedPostgreSqlDatabase}' initialized on {host}:{port}. All previous data deleted. Start the service to complete schema setup.";
    }

    /// <summary>
    /// Initialize the database using the saved admin password, falling back to the known
    /// default (PB@14891277). No user prompt required. Errors are surfaced as exceptions.
    /// </summary>
    public string InitializePostgreSqlDatabaseAuto()
    {
        // Priority: saved DPAPI secret → app-db password (same in this deployment) → known default
        var candidates = new[]
        {
            LoadAdminPassword(),
            GetConfiguredApplicationDbPassword(),
            ExpectedPostgreSqlPassword
        }
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Distinct(StringComparer.Ordinal)
        .ToList();

        Exception? lastEx = null;
        foreach (var password in candidates)
        {
            try
            {
                var result = InitializePostgreSqlDatabase(password!);
                // Save for next time so subsequent calls use DPAPI path immediately
                SaveAdminPassword(password!);
                return result;
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
        }

        throw new InvalidOperationException(
            "Database initialization failed with all known credentials.\n" +
            RedactSecrets(lastEx?.Message),
            lastEx);
    }

    private void SaveApplicationDbPassword(string password)
    {
        var root = LoadWebAppSettingsJson() ?? CreateDefaultWebAppSettingsJson();
        var connectionStrings = root["ConnectionStrings"] as JsonObject ?? new JsonObject();
        var existingConnection = connectionStrings["PostgreSQLConnection"]?.GetValue<string>();
        var builder = !string.IsNullOrWhiteSpace(existingConnection)
            ? new NpgsqlConnectionStringBuilder(existingConnection)
            : new NpgsqlConnectionStringBuilder();

        if (string.IsNullOrWhiteSpace(builder.Host))
        {
            builder.Host = "127.0.0.1";
        }

        if (builder.Port <= 0)
        {
            builder.Port = ExpectedPostgreSqlPort;
        }

        builder.Database = ExpectedPostgreSqlDatabase;
        builder.SearchPath = ExpectedPostgreSqlSchema;
        builder.Username = ExpectedPostgreSqlUser;
        builder.Password = password;
        builder.IncludeErrorDetail = true;
        if (builder.MaxPoolSize <= 0)
        {
            builder.MaxPoolSize = 100;
        }

        if (builder.ConnectionLifetime <= 0)
        {
            builder.ConnectionLifetime = 300;
        }

        connectionStrings["PostgreSQLConnection"] = builder.ConnectionString;
        root["ConnectionStrings"] = connectionStrings;
        SaveWebAppSettingsJson(root);
    }

    public void UpdatePostgreSqlPort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "PostgreSQL port must be between 1 and 65535.");
        }

        UpdatePostgreSqlConnectionHostPort(null!, port);
    }

    public void UpdatePostgreSqlConnectionHostPort(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) && port <= 0)
        {
            return;
        }

        var root = LoadWebAppSettingsJson() ?? new JsonObject();
        var connectionStrings = root["ConnectionStrings"] as JsonObject ?? new JsonObject();
        var existingConnection = connectionStrings["PostgreSQLConnection"]?.GetValue<string>();
        var builder = !string.IsNullOrWhiteSpace(existingConnection)
            ? new NpgsqlConnectionStringBuilder(existingConnection)
            : new NpgsqlConnectionStringBuilder();

        builder.Host = string.IsNullOrWhiteSpace(host)
            ? (string.IsNullOrWhiteSpace(builder.Host) ? "127.0.0.1" : builder.Host)
            : host;

        if (port > 0)
        {
            builder.Port = port;
        }

        if (string.IsNullOrWhiteSpace(builder.Database))
        {
            builder.Database = ExpectedPostgreSqlDatabase;
        }

        if (string.IsNullOrWhiteSpace(builder.SearchPath))
        {
            builder.SearchPath = ExpectedPostgreSqlSchema;
        }

        if (string.IsNullOrWhiteSpace(builder.Username))
        {
            builder.Username = ExpectedPostgreSqlUser;
        }

        builder.IncludeErrorDetail = true;

        if (builder.MaxPoolSize <= 0)
        {
            builder.MaxPoolSize = 100;
        }

        if (builder.ConnectionLifetime <= 0)
        {
            builder.ConnectionLifetime = 300;
        }

        connectionStrings["PostgreSQLConnection"] = builder.ConnectionString;
        root["ConnectionStrings"] = connectionStrings;
        SaveWebAppSettingsJson(root);
    }

    /// <summary>
    /// Updates the PostgreSQL connection string with a completely new connection string.
    /// </summary>
    public void UpdatePostgreSqlConnection(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString));
        }

        var root = LoadWebAppSettingsJson() ?? new JsonObject();
        var connectionStrings = root["ConnectionStrings"] as JsonObject ?? new JsonObject();
        connectionStrings["PostgreSQLConnection"] = connectionString;
        root["ConnectionStrings"] = connectionStrings;
        SaveWebAppSettingsJson(root);
    }

    public (bool installed, string message) DiscoverPostgreSql()
    {
        var configuredService = GetPostgreSqlServiceName();
        if (ServiceExists(ExpectedPostgreSqlServiceName))
        {
            return (true, $"PostgreSQL service ready: {ExpectedPostgreSqlServiceName}");
        }

        if (!string.Equals(configuredService, ExpectedPostgreSqlServiceName, StringComparison.OrdinalIgnoreCase) &&
            ServiceExists(configuredService))
        {
            return (true, $"PostgreSQL detected via configured service: {configuredService}");
        }

        var detected = FindInstalledPostgreSqlServiceName();
        if (!string.IsNullOrWhiteSpace(detected))
        {
            return (true, $"PostgreSQL detected: {detected}");
        }

        var hasClient = !string.IsNullOrWhiteSpace(ResolvePsqlExecutable());
        if (hasClient)
        {
            return (false, "PostgreSQL client tools found, but no Windows PostgreSQL service was detected.");
        }

        return (false, "PostgreSQL was not detected on this machine.");
    }

    public string AutoDetectAndConfigurePostgreSql(string? adminPasswordOverride = null)
    {
        var discovery = DiscoverPostgreSql();
        if (!discovery.installed)
        {
            return "PostgreSQL is not installed. Use 'Install PostgreSQL' in DB Manager, then run Auto Detect again.";
        }

        var changed = EnsurePostgreSqlControllerSettings();
        EnsurePostgreSqlServiceRunning();
        var accessChanged = EnsureExpectedPostgreSqlAccess(adminPasswordOverride);
        var test = TestPostgreSqlConnection();
        if (!test.ok)
        {
            throw new InvalidOperationException(test.message);
        }

        var configuredPort = GetConfiguredPostgreSqlPort();
        return changed || accessChanged
            ? $"PostgreSQL configured for service '{GetPostgreSqlServiceName()}', user '{ExpectedPostgreSqlUser}', DB '{ExpectedPostgreSqlDatabase}', schema '{ExpectedPostgreSqlSchema}', port {configuredPort}."
            : $"PostgreSQL already configured correctly ({GetPostgreSqlServiceName()}, {ExpectedPostgreSqlDatabase}/{ExpectedPostgreSqlSchema}, port {configuredPort}).";
    }

    public string OpenPostgreSqlInstallOption()
    {
        var discovery = DiscoverPostgreSql();
        if (discovery.installed)
        {
            return discovery.message;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = PostgreSqlWindowsDownloadUrl,
            UseShellExecute = true
        });

        return
            "PostgreSQL not found. Opened PostgreSQL Windows download page.\n" +
            "Use these preset values during install/setup:\n" +
            $"Service Name: {ExpectedPostgreSqlServiceName}\n" +
            $"Host: 127.0.0.1\n" +
            $"Port: {ExpectedPostgreSqlPort}\n" +
            $"Database: {ExpectedPostgreSqlDatabase}\n" +
            $"Schema: {ExpectedPostgreSqlSchema}\n" +
            $"User: {ExpectedPostgreSqlUser}\n" +
            "Password: [Hidden]";
    }

    public string CheckPostgreSqlInstallationReport()
    {
        var discovery = DiscoverPostgreSql();
        var serviceName = ResolveOrProvisionPostgreSqlServiceName();
        var isServicePresent = ServiceExists(serviceName);
        var isServiceRunning = false;
        if (isServicePresent)
        {
            try
            {
                using var svc = new ServiceController(serviceName);
                isServiceRunning = svc.Status == ServiceControllerStatus.Running;
            }
            catch
            {
                isServiceRunning = false;
            }
        }

        var psqlPath = ResolvePsqlExecutable();
        var psqlExists = !string.IsNullOrWhiteSpace(psqlPath) && File.Exists(psqlPath);
        var installDetected = Directory.Exists(DefaultPostgreSqlInstallPath);
        var test = TestPostgreSqlConnection();
        var configuredPort = GetConfiguredPostgreSqlPort();

        return
            $"Install: {(discovery.installed ? "Detected" : "Not detected")} ({discovery.message})\n" +
            $"Service: {serviceName} | Exists: {(isServicePresent ? "Yes" : "No")} | Running: {(isServiceRunning ? "Yes" : "No")}\n" +
            $"Port: {configuredPort}\n" +
            $"DB/User: {ExpectedPostgreSqlDatabase}/{ExpectedPostgreSqlUser}\n" +
            $"Install Path Exists: {(installDetected ? "Yes" : "No")} ({DefaultPostgreSqlInstallPath})\n" +
            $"psql.exe: {(psqlExists ? psqlPath : "Not found")}\n" +
            $"Connection Test: {(test.ok ? "SUCCESS" : "FAILED")} -> {test.message}";
    }

    public string InstallPostgreSqlOffline(string? installerPathOverride = null)
    {
        var discovery = DiscoverPostgreSql();
        if (discovery.installed)
        {
            return discovery.message;
        }

        var installerPath = string.IsNullOrWhiteSpace(installerPathOverride)
            ? DefaultOfflineInstallerPath
            : installerPathOverride.Trim();

        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException(
                $"Offline installer not found at '{installerPath}'.", installerPath);
        }

        Directory.CreateDirectory(DefaultPostgreSqlDataPath);
        var superPassword = GetConfiguredApplicationDbPassword();
        var args =
            "--mode unattended --unattendedmodeui none " +
            $"--superpassword \"{superPassword}\" " +
            $"--servicepassword \"{superPassword}\" " +
            $"--servicename \"{ExpectedPostgreSqlServiceName}\" " +
            $"--serverport {ExpectedPostgreSqlPort} " +
            $"--datadir \"{DefaultPostgreSqlDataPath}\" " +
            "--disable-components pgAdmin,stackbuilder";

        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            throw new InvalidOperationException("Failed to start PostgreSQL offline installer.");
        }

        var output = proc.StandardOutput.ReadToEnd();
        var error = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Offline installation failed (exit {proc.ExitCode}): {RedactSecrets(error)}");
        }

        EnsurePathContains(DefaultPostgreSqlBinPath);
        _ = EnsurePostgreSqlControllerSettings();

        return "PostgreSQL offline installation completed successfully.";
    }

    public string ConfigurePostgreSqlPresets(string? adminPasswordOverride = null)
    {
        var changed = EnsurePostgreSqlControllerSettings();
        EnsurePostgreSqlServiceRunning();
        var accessChanged = EnsureExpectedPostgreSqlAccess(adminPasswordOverride);
        var test = TestPostgreSqlConnection();
        if (!test.ok)
        {
            throw new InvalidOperationException(test.message);
        }

        return changed || accessChanged
            ? "Preset configuration completed and connection test passed."
            : "Preset configuration already valid and connection test passed.";
    }

    public string GetPostgreSqlPresetSummary()
    {
        return
            $"Preset -> Service: {ExpectedPostgreSqlServiceName}, Host: 127.0.0.1, Port: {ExpectedPostgreSqlPort}, " +
            $"DB: {ExpectedPostgreSqlDatabase}, Schema: {ExpectedPostgreSqlSchema}, User: {ExpectedPostgreSqlUser}, Password: [Hidden]";
    }

    public void ClearSavedAdminPassword()
    {
        try
        {
            if (File.Exists(PgAdminSecretPath))
            {
                File.Delete(PgAdminSecretPath);
            }
        }
        catch
        {
            // Non-fatal
        }
    }

    public void UpdateSavedAdminPassword(string password)
    {
        SaveAdminPassword(password);
    }

    public ServiceState GetState()
    {
        var port = LoadPort();
        var configuredFrontendPort = LoadFrontendPort();
        var frontendPortToCheck = _activeFrontendPort > 0 ? _activeFrontendPort : configuredFrontendPort;
        var dbConfigured = true;
        var pgMessage = GetPostgresStatusCached();
        var apiInstalled = GetWindowsServiceInstalled();
        var apiStatus = GetWindowsServiceStatus();
        return new ServiceState(
            Port: port,
            IsWebReachable: IsWebApiReachable(port),
            FrontendPort: configuredFrontendPort,
            ActiveFrontendPort: frontendPortToCheck,
            IsFrontendReachable: IsFrontendReachable(frontendPortToCheck),
            MainDbPath: "N/A (PostgreSQL mode)",
            ProductionDbPath: "N/A (PostgreSQL mode)",
            MainDbExists: true,
            ProductionDbExists: true,
            DatabaseEngine: "PostgreSQL",
            IsPostgreSqlConfigured: dbConfigured,
            PostgresStatus: pgMessage,
            ApiWinSvcInstalled: apiInstalled,
            ApiWinSvcStatus: apiStatus);
    }

    public IReadOnlyList<RelatedServiceStatus> GetRelatedServices(ServiceState? state = null)
    {
        state ??= GetState();
        var pgServiceName = ResolveOrProvisionPostgreSqlServiceName();
        var items = new List<RelatedServiceStatus>
        {
            new(
                Name: "E.Attendance.Web API",
                Category: "HTTP",
                Status: state.IsWebReachable ? "Running" : "Stopped",
                Details: $"http://127.0.0.1:{state.Port}/api/server/stats"),
            new(
                Name: "E-Attendance Web UI",
                Category: "HTTP",
                Status: state.IsFrontendReachable ? "Running" : "Stopped",
                Details: $"http://127.0.0.1:{(state.ActiveFrontendPort > 0 ? state.ActiveFrontendPort : state.FrontendPort)}/"),
            new(
                Name: "E_Attendance_PostgreSQL",
                Category: "Windows Service",
                Status: GetWindowsServiceStatus(pgServiceName),
                Details: $"PostgreSQL service ({pgServiceName}) for E_Attendace"),
            new(
                Name: "Push Command Queue",
                Category: "App",
                Status: state.IsWebReachable ? "Active" : "Inactive",
                Details: "Served by E.Attendance.Web /iclock/getrequest"),
            new(
                Name: "Device Callback Endpoint",
                Category: "App",
                Status: state.IsWebReachable ? "Active" : "Inactive",
                Details: "Served by E.Attendance.Web /iclock/devicecmd")
        };

        return items;
    }

    public bool IsPostgreSqlConfigured()
    {
        return true;
    }

    public void SetUsePostgreSql(bool enabled)
    {
        if (!enabled)
        {
            enabled = true;
        }
        var root = LoadWebAppSettingsJson() ?? new JsonObject();
        var db = root["DatabaseSettings"] as JsonObject ?? new JsonObject();
        db["UsePostgreSql"] = enabled;
        root["DatabaseSettings"] = db;
        SaveWebAppSettingsJson(root);
    }

    public (bool ok, string message) TestPostgreSqlConnection()
    {
        try
        {
            var conn = GetPostgreSqlConnectionString();
            if (string.IsNullOrWhiteSpace(conn))
            {
                return (false, "PostgreSQLConnection not found in E.Attendance.Web/appsettings.json");
            }

            EnsurePostgreSqlServiceRunning();
            var builder = new NpgsqlConnectionStringBuilder(conn);
            var probe = ProbePostgreSql(builder);
            return (probe.ok, probe.message);
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {RedactSecrets(ex.Message)}. Make sure service '{ExpectedPostgreSqlServiceName}' is running.");
        }
    }

    private (bool ok, string message, int port) ProbePostgreSql(NpgsqlConnectionStringBuilder baseBuilder)
    {
        var host = string.IsNullOrWhiteSpace(baseBuilder.Host) ? "127.0.0.1" : baseBuilder.Host;
        var configuredPort = baseBuilder.Port <= 0 ? ExpectedPostgreSqlPort : baseBuilder.Port;
        var errors = new List<string>();
        var portsTried = BuildPostgreSqlPortCandidates(configuredPort);

        foreach (var port in portsTried)
        {
            var builder = new NpgsqlConnectionStringBuilder(baseBuilder.ConnectionString)
            {
                Host = host,
                Port = port,
                Timeout = 4,
                CommandTimeout = 5
            };

            try
            {
                using var connection = new NpgsqlConnection(builder.ConnectionString);
                connection.Open();
                using var command = new NpgsqlCommand("SELECT current_database(), version();", connection);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    var db = reader.GetString(0);
                    var version = reader.GetString(1);
                    var shortVersion = version.Split(',', StringSplitOptions.TrimEntries)[0];
                    var message = port == configuredPort
                        ? $"Connected: {db} | {shortVersion} | Port {port}"
                        : $"Connected on fallback port {port}: {db} | {shortVersion}";
                    return (true, message, port);
                }

                return (true, $"Connected to PostgreSQL on port {port}.", port);
            }
            catch (Exception ex)
            {
                errors.Add($"{host}:{port} -> {RedactSecrets(ex.Message)}");
            }
        }

        var portsLabel = portsTried.Count == 0 ? ExpectedPostgreSqlPort.ToString(CultureInfo.InvariantCulture) : string.Join(", ", portsTried);
        var errorLabel = errors.Count == 0 ? "Unknown error." : string.Join("\n", errors);
        return (false, $"Connection failed on port {portsLabel}.\n{errorLabel}", ExpectedPostgreSqlPort);
    }

    private int TryResolvePostgreSqlPortFromServiceConfig()
    {
        try
        {
            var serviceCandidates = new List<string>();

            void AddServiceCandidate(string? name)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }

                if (serviceCandidates.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    return;
                }

                if (ServiceExists(name))
                {
                    serviceCandidates.Add(name);
                }
            }

            // Prefer the bundled service if present, even if a different postgres service exists.
            AddServiceCandidate(ExpectedPostgreSqlServiceName);
            AddServiceCandidate(GetPostgreSqlServiceName());
            AddServiceCandidate(FindInstalledPostgreSqlServiceName());

            foreach (var serviceName in serviceCandidates)
            {
                var binaryPath = GetServiceBinaryPath(serviceName);
                if (string.IsNullOrWhiteSpace(binaryPath))
                {
                    continue;
                }

                if (TryExtractPortFromServiceCommandLine(binaryPath, out var directPort))
                {
                    return directPort;
                }

                var dataDirectory = TryExtractDataDirectoryFromServiceCommandLine(binaryPath);
                if (string.IsNullOrWhiteSpace(dataDirectory))
                {
                    continue;
                }

                var configPort = TryReadPostgreSqlPortFromConfig(dataDirectory);
                if (configPort > 0)
                {
                    return configPort;
                }
            }
        }
        catch
        {
            // Non-fatal: fallback list will handle it.
        }

        return 0;
    }

    private static bool TryExtractPortFromServiceCommandLine(string commandLine, out int port)
    {
        port = 0;
        var args = SplitCommandLineArguments(commandLine);
        if (TryExtractPortFromArgs(args, out port))
        {
            return true;
        }

        for (var i = 0; i < args.Count - 1; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "-o", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--options", StringComparison.OrdinalIgnoreCase))
            {
                var options = args[i + 1];
                var optionArgs = SplitCommandLineArguments(options);
                if (TryExtractPortFromArgs(optionArgs, out port))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryExtractPortFromArgs(IReadOnlyList<string> args, out int port)
    {
        port = 0;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "-p", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--port", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count && TryParsePortValue(args[i + 1], out port))
                {
                    return true;
                }
            }

            if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParsePortValue(arg["--port=".Length..], out port))
                {
                    return true;
                }
            }

            if (arg.StartsWith("-p", StringComparison.OrdinalIgnoreCase) && arg.Length > 2)
            {
                if (TryParsePortValue(arg[2..], out port))
                {
                    return true;
                }
            }

            if (string.Equals(arg, "-c", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count && TryParsePortFromConfigArg(args[i + 1], out port))
                {
                    return true;
                }
            }

            if (arg.StartsWith("-c", StringComparison.OrdinalIgnoreCase) && arg.Length > 2)
            {
                if (TryParsePortFromConfigArg(arg[2..], out port))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryParsePortFromConfigArg(string arg, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(arg))
        {
            return false;
        }

        var trimmed = arg.Trim();
        if (!trimmed.StartsWith("port=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = trimmed["port=".Length..].Trim().Trim('"', '\'');
        return TryParsePortValue(value, out port);
    }

    private static string? TryExtractDataDirectoryFromServiceCommandLine(string commandLine)
    {
        var args = SplitCommandLineArguments(commandLine);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "-D", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--data-directory", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--pgdata", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                {
                    return args[i + 1];
                }
            }

            if (arg.StartsWith("-D", StringComparison.OrdinalIgnoreCase) && arg.Length > 2)
            {
                return arg[2..];
            }

            if (arg.StartsWith("--data-directory=", StringComparison.OrdinalIgnoreCase))
            {
                return arg["--data-directory=".Length..];
            }

            if (arg.StartsWith("--pgdata=", StringComparison.OrdinalIgnoreCase))
            {
                return arg["--pgdata=".Length..];
            }
        }

        return null;
    }

    private static int TryReadPostgreSqlPortFromConfig(string dataDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dataDirectory))
            {
                return 0;
            }

            var autoConfig = Path.Combine(dataDirectory, "postgresql.auto.conf");
            var port = TryReadPostgreSqlPortFromFile(autoConfig);
            if (port > 0)
            {
                return port;
            }

            var config = Path.Combine(dataDirectory, "postgresql.conf");
            return TryReadPostgreSqlPortFromFile(config);
        }
        catch
        {
            return 0;
        }
    }

    private static int TryReadPostgreSqlPortFromFile(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return 0;
            }

            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine;
                var commentIndex = line.IndexOf('#');
                if (commentIndex >= 0)
                {
                    line = line[..commentIndex];
                }

                line = line.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var match = Regex.Match(line, @"^port\s*=\s*'?(\d+)'?\s*$", RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    continue;
                }

                if (TryParsePortValue(match.Groups[1].Value, out var port))
                {
                    return port;
                }
            }
        }
        catch
        {
        }

        return 0;
    }

    private static List<string> SplitCommandLineArguments(string commandLine)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return args;
        }

        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in commandLine)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    private static bool TryParsePortValue(string value, out int port)
    {
        port = 0;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        if (parsed is < 1 or > 65535)
        {
            return false;
        }

        port = parsed;
        return true;
    }

    private IReadOnlyList<int> BuildPostgreSqlPortCandidates(int configuredPort, int? servicePortOverride = null)
    {
        // User requested strict adherence to port 1277.
        // We no longer perform "Smart Port Discovery" across 5432/5433.
        return new List<int> { ExpectedPostgreSqlPort };
    }

    private string GetPostgresStatusCached()
    {
        var now = DateTime.UtcNow;
        if (_lastPgCheckUtc == DateTime.MinValue || (now - _lastPgCheckUtc) > TimeSpan.FromSeconds(10))
        {
            var (_, message) = TestPostgreSqlConnection();
            _lastPgCheckUtc = now;
            _lastPgStatus = message;
        }

        return _lastPgStatus;
    }

    public void EnsurePostgreSqlServiceRunning()
    {
        if (!ServiceControllerAvailable)
        {
            return; // Service management not available in this .NET runtime
        }
        
        var candidates = GetPostgreSqlServiceCandidates();
        foreach (var serviceName in candidates)
        {
            try
            {
                using var svc = new ServiceController(serviceName);
                if (svc.Status == ServiceControllerStatus.Running)
                {
                    SetPostgreSqlServiceName(serviceName);
                    return;
                }

                if (svc.Status == ServiceControllerStatus.Stopped || svc.Status == ServiceControllerStatus.Paused)
                {
                    svc.Start();
                    svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                    SetPostgreSqlServiceName(serviceName);
                    return;
                }
            }
            catch
            {
                // Try next candidate service.
            }
        }
    }

    public void StartPostgreSqlService()
    {
        if (!ServiceControllerAvailable)
        {
            throw new InvalidOperationException(
                "Service management is not available in this .NET runtime. " +
                "Ensure .NET Desktop Runtime 10.0 is installed with System.ServiceProcess support.");
        }
        
        var candidates = GetPostgreSqlServiceCandidates();
        foreach (var serviceName in candidates)
        {
            try
            {
                using var svc = new ServiceController(serviceName);
                if (svc.Status != ServiceControllerStatus.Running)
                {
                    svc.Start();
                    svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                }

                SetPostgreSqlServiceName(serviceName);
                return;
            }
            catch
            {
                // Try next candidate service.
            }
        }

        throw new InvalidOperationException(
            "Unable to start PostgreSQL service. No usable PostgreSQL Windows service was found. " +
            "Install PostgreSQL or verify the service is installed and accessible.");
    }

    public void StopPostgreSqlService()
    {
        if (!ServiceControllerAvailable)
        {
            throw new InvalidOperationException(
                "Service management is not available in this .NET runtime. " +
                "Ensure .NET Desktop Runtime 10.0 is installed with System.ServiceProcess support.");
        }
        
        var candidates = GetPostgreSqlServiceCandidates();
        foreach (var serviceName in candidates)
        {
            try
            {
                using var svc = new ServiceController(serviceName);
                if (svc.Status == ServiceControllerStatus.Stopped)
                {
                    return;
                }

                svc.Stop();
                svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                return;
            }
            catch
            {
                // Try next candidate service.
            }
        }

        throw new InvalidOperationException("Unable to stop PostgreSQL service.");
    }

    public void RestartPostgreSqlService()
    {
        if (!ServiceControllerAvailable)
        {
            throw new InvalidOperationException(
                "Service management is not available in this .NET runtime. " +
                "Ensure .NET Desktop Runtime 10.0 is installed with System.ServiceProcess support.");
        }
        
        var candidates = GetPostgreSqlServiceCandidates();
        foreach (var serviceName in candidates)
        {
            try
            {
                using var svc = new ServiceController(serviceName);

                if (svc.Status == ServiceControllerStatus.StartPending)
                {
                    svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                }
                else if (svc.Status == ServiceControllerStatus.StopPending)
                {
                    svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                }

                if (svc.Status != ServiceControllerStatus.Stopped)
                {
                    svc.Stop();
                    svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                }

                svc.Start();
                svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                SetPostgreSqlServiceName(serviceName);
                return;
            }
            catch
            {
                // Try next candidate service.
            }
        }

        throw new InvalidOperationException("Unable to restart PostgreSQL service.");
    }

    private void StopServiceForDatabaseMaintenance()
    {
        try
        {
            StopService();
        }
        catch
        {
            // Best-effort shutdown; reset logic will still terminate DB sessions.
        }
    }

    private static void EnsureApplicationRole(NpgsqlConnection adminConnection, string applicationPassword)
    {
        var roleSql = $@"
DO $do$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{ExpectedPostgreSqlUser}') THEN
        CREATE ROLE ""{ExpectedPostgreSqlUser}"" WITH LOGIN PASSWORD '{EscapeSqlLiteral(applicationPassword)}';
    ELSE
        ALTER ROLE ""{ExpectedPostgreSqlUser}"" WITH LOGIN PASSWORD '{EscapeSqlLiteral(applicationPassword)}';
    END IF;
END
$do$;";

        using var roleCommand = new NpgsqlCommand(roleSql, adminConnection);
        roleCommand.ExecuteNonQuery();
    }

    private static void ResetApplicationDatabase(NpgsqlConnection adminConnection)
    {
        using (var terminateCommand = new NpgsqlCommand(
                   "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid();",
                   adminConnection))
        {
            terminateCommand.Parameters.AddWithValue("@db", ExpectedPostgreSqlDatabase);
            terminateCommand.ExecuteNonQuery();
        }

        using (var dropCommand = new NpgsqlCommand(
                   $"DROP DATABASE IF EXISTS \"{ExpectedPostgreSqlDatabase}\";",
                   adminConnection))
        {
            dropCommand.ExecuteNonQuery();
        }

        using var createCommand = new NpgsqlCommand(
            $"CREATE DATABASE \"{ExpectedPostgreSqlDatabase}\" OWNER \"{ExpectedPostgreSqlUser}\" ENCODING 'UTF8' TEMPLATE template0;",
            adminConnection);
        createCommand.ExecuteNonQuery();
    }

    private static void ConfigureInitializedDatabase(NpgsqlConnectionStringBuilder adminBuilder, string host, int port)
    {
        var dbAdminBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
        {
            Host = host,
            Port = port,
            Database = ExpectedPostgreSqlDatabase,
            Timeout = 8,
            CommandTimeout = 45
        };

        using var connection = new NpgsqlConnection(dbAdminBuilder.ConnectionString);
        connection.Open();

        using (var extensionCommand = new NpgsqlCommand(
                   @"CREATE EXTENSION IF NOT EXISTS ""uuid-ossp"";
CREATE EXTENSION IF NOT EXISTS ""pg_trgm"";
CREATE EXTENSION IF NOT EXISTS ""btree_gin"";",
                   connection))
        {
            extensionCommand.ExecuteNonQuery();
        }

        using var defaultsCommand = new NpgsqlCommand(
            $@"ALTER DATABASE ""{ExpectedPostgreSqlDatabase}"" SET timezone TO 'Asia/Kathmandu';
ALTER DATABASE ""{ExpectedPostgreSqlDatabase}"" SET search_path TO ""{ExpectedPostgreSqlSchema}"", public;
GRANT ALL PRIVILEGES ON DATABASE ""{ExpectedPostgreSqlDatabase}"" TO postgres;
GRANT CONNECT ON DATABASE ""{ExpectedPostgreSqlDatabase}"" TO ""{ExpectedPostgreSqlUser}"";
CREATE SCHEMA IF NOT EXISTS ""{ExpectedPostgreSqlSchema}"" AUTHORIZATION ""{ExpectedPostgreSqlUser}"";
GRANT ALL PRIVILEGES ON SCHEMA ""{ExpectedPostgreSqlSchema}"" TO ""{ExpectedPostgreSqlUser}"";
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA ""{ExpectedPostgreSqlSchema}"" TO ""{ExpectedPostgreSqlUser}"";
ALTER DEFAULT PRIVILEGES IN SCHEMA ""{ExpectedPostgreSqlSchema}"" GRANT ALL PRIVILEGES ON TABLES TO ""{ExpectedPostgreSqlUser}"";",
            connection);
        defaultsCommand.ExecuteNonQuery();
    }

    private List<string> GetPostgreSqlServiceCandidates()
    {
        var configured = GetPostgreSqlServiceName();
        var resolved = ResolveOrProvisionPostgreSqlServiceName();
        var detected = FindInstalledPostgreSqlServiceName();

        var candidates = new List<string>();
        void AddIfValid(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            if (candidates.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            if (ServiceExists(name))
            {
                candidates.Add(name);
            }
        }

        AddIfValid(configured);
        AddIfValid(resolved);
        AddIfValid(ExpectedPostgreSqlServiceName);
        AddIfValid(detected);
        return candidates;
    }

    public void RunPostgreSqlSetupScript()
    {
        if (!File.Exists(PostgresSetupScriptPath))
        {
            throw new FileNotFoundException("Setup script not found.", PostgresSetupScriptPath);
        }

        var conn = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(conn))
        {
            throw new InvalidOperationException("PostgreSQLConnection not found in appsettings.");
        }

        EnsurePostgreSqlServiceRunning();

        var builder = new NpgsqlConnectionStringBuilder(conn);
        var host = string.IsNullOrWhiteSpace(builder.Host) ? "localhost" : builder.Host;
        var user = string.IsNullOrWhiteSpace(builder.Username) ? "postgres" : builder.Username;
        var database = "postgres";

        var probe = ProbePostgreSql(builder);
        if (!probe.ok)
        {
            throw new InvalidOperationException(
                $"PostgreSQL is not reachable at {host}:{ExpectedPostgreSqlPort}. " +
                "Ensure PostgreSQL service is running and configured port is correct, then retry. " +
                $"Details: {probe.message}");
        }
        var port = probe.port;

        var psqlExe = ResolvePsqlExecutable();
        if (string.IsNullOrWhiteSpace(psqlExe))
        {
            throw new InvalidOperationException(
                "psql.exe not found. Install PostgreSQL client tools or set environment variable PB_PSQL_PATH to full psql.exe path.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = psqlExe,
            Arguments = $"-h {host} -p {port} -U {user} -d {database} -f \"{PostgresSetupScriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (!string.IsNullOrWhiteSpace(builder.Password))
        {
            psi.Environment["PGPASSWORD"] = builder.Password;
        }

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                output.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                output.AppendLine(args.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start psql. Ensure PostgreSQL client tools are installed.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit(120000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"psql failed (exit {process.ExitCode}): {RedactSecrets(output.ToString())}");
        }
    }

    public void StartService()
    {
        EnsureDefaults();
        EnsurePostgreSqlReady();

        var port = LoadPort();
        var configuredFrontendPort = LoadFrontendPort();
        if (IsWebApiReachable(port))
        {
            // Always restart an already-running web host so latest build output is applied.
            StopService();
            Thread.Sleep(350);
        }

        if (IsPortInUse(port))
        {
            if (TryRecoverSelfHostedApiOnPort(port))
            {
                StartPushEngine(port);
                EnsureFrontendRunning(port);
                return;
            }

            if (!IsPortInUse(port))
            {
                // Stale self-process was released; continue normal startup on configured port.
            }
            else
            {
                throw new InvalidOperationException(
                    $"Configured API port {port} is already in use by another process. " +
                    "Stop that process or change the API Port in Service Controller, then retry.");
            }
        }

        // Backend binds both API and Web UI ports (UseUrls includes both). Preflight Web UI
        // port to fail fast with a clear message instead of a Kestrel bind stack trace.
        if (configuredFrontendPort != port && IsPortInUse(configuredFrontendPort))
        {
            if (TryRecoverSelfHostedApiOnPort(configuredFrontendPort) ||
                TryRecoverSelfHostedFrontendOnPort(configuredFrontendPort))
            {
                Thread.Sleep(300);
            }

            if (IsPortInUse(configuredFrontendPort))
            {
                // Try force-killing the owning process before giving up
                TryKillPortOwner(configuredFrontendPort);
                Thread.Sleep(600);
            }

            if (IsPortInUse(configuredFrontendPort))
            {
                throw new InvalidOperationException(
                    $"Configured Web UI port {configuredFrontendPort} is already in use by {DescribePortOwner(configuredFrontendPort)}. " +
                    "Stop that process or change the Web UI Port in Service Controller, then retry.");
            }
        }

        var startInfo = ResolveWebStartInfo();
        if (startInfo is null)
        {
            throw new InvalidOperationException("E.Attendance.Web executable was not found. Build E.Attendance.Web first.");
        }

        var process = new Process { StartInfo = startInfo };
        var startupOutput = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                startupOutput.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                startupOutput.AppendLine(args.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start E.Attendance.Web process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_sync)
        {
            _ownedProcess = process;
        }

        var timeout = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < timeout)
        {
            Thread.Sleep(300);
            if (IsWebApiReachable(port))
            {
                StartPushEngine(port);
                EnsureFrontendRunning(port);
                return;
            }

            if (process.HasExited)
            {
                var details = GetStartupFailureDetails(process, startupOutput);
                throw new InvalidOperationException(
                    $"E.Attendance.Web exited before becoming ready on port {port}. {details}");
            }
        }
        
        throw new System.TimeoutException($"E.Attendance.Web did not start on port {port}.");
    }

    public void StopService()
    {
        var port = LoadPort();
        TryPost($"http://127.0.0.1:{port}/api/server/stop");
        var configuredFrontendPort = LoadFrontendPort();
        TryPost($"http://127.0.0.1:{configuredFrontendPort}/api/server/stop");

        Process? ownedBackend = null;
        Process? ownedFrontend = null;
        lock (_sync)
        {
            ownedBackend = _ownedProcess;
            _ownedProcess = null;

            ownedFrontend = _ownedFrontendProcess;
            _ownedFrontendProcess = null;
        }

        if (ownedBackend is not null)
        {
            SafeKill(ownedBackend);
            SafeWaitForExit(ownedBackend, 5000);
            try { ownedBackend.Dispose(); } catch { }
        }

        if (ownedFrontend is not null)
        {
            SafeKill(ownedFrontend);
            SafeWaitForExit(ownedFrontend, 5000);
            try { ownedFrontend.Dispose(); } catch { }
        }

        foreach (var process in Process.GetProcessesByName("E.Attendance.Web"))
        {
            try
            {
                SafeKill(process);
                SafeWaitForExit(process, 3000);
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private DateTime _lastDashOpen = DateTime.MinValue;
    public void OpenDashboard()
    {
        // Simple debounce to prevent rapid double-clicks from opening multiple tabs
        if ((DateTime.Now - _lastDashOpen).TotalMilliseconds < 1000) return;
        _lastDashOpen = DateTime.Now;

        var configuredFrontendPort = LoadFrontendPort();
        var targetUrl = $"http://127.0.0.1:{configuredFrontendPort}/";

        try
        {
            // The most reliable way on Windows to open a URL in the default browser exactly once
            Process.Start(new ProcessStartInfo
            {
                FileName = targetUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // Very rare fallback
            try { Process.Start("explorer.exe", targetUrl); } catch { }
        }
    }

    // ── Windows Service names ─────────────────────────────────────────
    public const string ApiWindowsServiceName = "E-Attendance-API";
    public const string WebWindowsServiceName = "E-Attendance-Web";

    public bool GetWindowsServiceInstalled()
        => ServiceExists(ApiWindowsServiceName);

    public string GetWindowsServiceStatus()
    {
        if (!ServiceControllerAvailable) return "N/A";
        try
        {
            using var svc = new ServiceController(ApiWindowsServiceName);
            return svc.Status.ToString();
        }
        catch { return "Not Installed"; }
    }

    public (bool ok, string message) InstallWindowsServices()
    {
        var startInfo = ResolveWebStartInfo();
        if (startInfo is null)
            return (false, "E.Attendance.Web.exe not found. Build the project first.");

        var exePath = startInfo.FileName;
        var port = LoadPort();
        var frontendPort = LoadFrontendPort();

        // Extend SCM startup timeout to 2 minutes (prevents Error 1053 on first start while DB inits)
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control", writable: true);
            key?.SetValue("ServicesPipeTimeout", 120_000, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch { }

        // Uninstall stale versions first
        ScExec($"stop \"{ApiWindowsServiceName}\"");
        ScExec($"delete \"{ApiWindowsServiceName}\"");
        ScExec($"stop \"{WebWindowsServiceName}\"");
        ScExec($"delete \"{WebWindowsServiceName}\"");
        Thread.Sleep(800);

        // Install API service:
        //   delayed-auto = starts after boot is complete (gives PostgreSQL time to start first)
        //   depend = ensures Windows starts the PostgreSQL service before this service
        var apiArgs = "--service-mode";
        var pgServiceName = ExpectedPostgreSqlServiceName; // "E_Attendance_PostgreSQL"

        // Try to install with PostgreSQL dependency first; fall back without it if PG service doesn't exist
        var dependArg = ServiceExists(pgServiceName) ? $" depend= \"{pgServiceName}\"" : string.Empty;
        var apiResult = ScExec(
            $"create \"{ApiWindowsServiceName}\" binPath= \"\\\"{exePath}\\\" {apiArgs}\"" +
            $" start= delayed-auto DisplayName= \"E-Attendance Service\"{dependArg}");

        if (!ServiceExists(ApiWindowsServiceName))
            return (false, $"Failed to create {ApiWindowsServiceName}. Run as Administrator.\n{apiResult}");

        ScExec($"description \"{ApiWindowsServiceName}\" \"E-Attendance API and Web UI — auto-starts after Windows boot\"");
        // Restart on failure: 5 s, 15 s, 30 s; reset counter after 1 day
        ScExec($"failure \"{ApiWindowsServiceName}\" reset= 86400 actions= restart/5000/restart/15000/restart/30000");
        ScExec($"failureflag \"{ApiWindowsServiceName}\" 1");

        // Also ensure PostgreSQL service is set to auto-start
        if (!string.IsNullOrEmpty(pgServiceName) && ServiceExists(pgServiceName))
            ScExec($"config \"{pgServiceName}\" start= auto");

        return (true, $"Installed: E-Attendance-API (DELAYED-AUTO-START, auto-restart on failure{(dependArg.Length > 0 ? ", depends on PostgreSQL" : "")}).");
    }

    public (bool ok, string message) UninstallWindowsServices()
    {
        NuclearStop();
        Thread.Sleep(800);

        ScExec($"stop \"{WebWindowsServiceName}\"");
        Thread.Sleep(500);
        var r1 = ScExec($"delete \"{WebWindowsServiceName}\"");

        ScExec($"stop \"{ApiWindowsServiceName}\"");
        Thread.Sleep(500);
        var r2 = ScExec($"delete \"{ApiWindowsServiceName}\"");

        var ok = !ServiceExists(ApiWindowsServiceName) && !ServiceExists(WebWindowsServiceName);
        return (ok, ok ? "Service uninstalled successfully." : $"Partial uninstall.\nAPI: {r2}");
    }

    public void StartWindowsService(string serviceName)
    {
        // Extend SCM startup timeout to 2 minutes so EF Core init doesn't trigger Error 1053.
        // Takes effect immediately for any new service start (no reboot needed).
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control", writable: true);
            key?.SetValue("ServicesPipeTimeout", 120_000, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch { /* non-fatal — continue regardless */ }

        // Try ServiceController (works when running as Administrator)
        if (ServiceControllerAvailable)
        {
            try
            {
                using var svc = new ServiceController(serviceName);
                var status = svc.Status;
                if (status == ServiceControllerStatus.Running) return;
                if (status == ServiceControllerStatus.StartPending)
                {
                    svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(90));
                    return;
                }
                svc.Start();
                svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(90));
                return;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5 /*ACCESS_DENIED*/)
            {
                // Fall through to sc.exe
            }
            catch (InvalidOperationException)
            {
                // Service doesn't exist yet, fall through
            }
            catch
            {
                // Any other error (e.g., timeout) — fall through to sc.exe fallback
            }
        }

        // Fallback: sc.exe (can be elevated via UAC)
        ScExec($"start \"{serviceName}\"");

        // Poll up to 90 s for Running state
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(1500);
            if (!ServiceControllerAvailable) continue;
            try
            {
                using var svc2 = new ServiceController(serviceName);
                svc2.Refresh();
                if (svc2.Status == ServiceControllerStatus.Running) return;
                if (svc2.Status == ServiceControllerStatus.Stopped)
                    throw new InvalidOperationException(
                        $"Service '{serviceName}' stopped unexpectedly. Check Windows Event Viewer for details.");
            }
            catch (InvalidOperationException) { throw; }
            catch { /* ignore transient */ }
        }
    }

    public void StopWindowsService(string serviceName)
    {
        // Try ServiceController first (works when running as admin)
        if (ServiceControllerAvailable)
        {
            try
            {
                using var svc = new ServiceController(serviceName);
                var status = svc.Status;
                if (status == ServiceControllerStatus.Stopped) return;
                if (status == ServiceControllerStatus.StopPending)
                {
                    svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                    return;
                }
                svc.Stop();
                svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                return;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5 /*ACCESS_DENIED*/)
            {
                // Fall through to sc.exe elevated
            }
            catch { return; }
        }
        // Fallback: run sc.exe
        ScExec($"stop \"{serviceName}\"");
        // Wait up to 20s for Stopped
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(1000);
            if (ServiceControllerAvailable)
            {
                try
                {
                    using var svc2 = new ServiceController(serviceName);
                    if (svc2.Status == ServiceControllerStatus.Stopped) return;
                }
                catch { }
            }
        }
    }

    /// <summary>Kill everything: Windows service + process by name + kill by port. No mercy.</summary>
    public void NuclearStop()
    {
        // 1. Stop Windows services if installed
        foreach (var name in new[] { ApiWindowsServiceName, WebWindowsServiceName })
        {
            try { StopWindowsService(name); } catch { }
        }

        // 2. StopService (graceful via HTTP + own process kill)
        try { StopService(); } catch { }

        // 3. Kill by process name (covers all scenarios)
        foreach (var pname in new[] { "E.Attendance.Web", "PB.Attendance.Web", "E_Attendance_Web" })
        {
            foreach (var proc in Process.GetProcessesByName(pname))
            {
                try { proc.Kill(entireProcessTree: true); proc.WaitForExit(3000); } catch { }
                finally { try { proc.Dispose(); } catch { } }
            }
        }

        // 4. Kill by port (nuclear fallback)
        var port = LoadPort();
        var frontPort = LoadFrontendPort();
        foreach (var p in new[] { port, frontPort }.Distinct())
        {
            KillProcessOnPort(p);
        }

        // 5. taskkill as final sweep
        foreach (var exeName in new[] { "E.Attendance.Web.exe", "PB.Attendance.Web.exe" })
        {
            try
            {
                var tk = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill", Arguments = $"/F /IM \"{exeName}\" /T",
                    UseShellExecute = false, CreateNoWindow = true
                });
                tk?.WaitForExit(3000);
            }
            catch { }
        }
    }

    private static void KillProcessOnPort(int port)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/C for /f \"tokens=5\" %a in ('netstat -ano ^| findstr \":{port} \" ^| findstr LISTENING') do taskkill /F /PID %a /T 2>nul",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(5000);
        }
        catch { }
    }

    private static string ScExec(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            p.WaitForExit(10000);
            return p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static void EnsureFile(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        using var stream = File.Create(path);
    }

    private void StartPushEngine(int port)
    {
        TryPost($"http://127.0.0.1:{port}/api/server/start");
    }

    private static void TryPost(string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            _ = client.PostAsync(url, null).GetAwaiter().GetResult();
        }
        catch
        {
        }
    }

    private static bool IsWebApiReachable(int port)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(400) };

            // Primary readiness probe: health endpoint remains available even when
            // business APIs are license-locked.
            using var health = client.GetAsync($"http://127.0.0.1:{port}/health").GetAwaiter().GetResult();
            if ((int)health.StatusCode is >= 200 and < 300)
            {
                return true;
            }

            // Backward-compat fallback for older builds without /health route.
            using var stats = client.GetAsync($"http://127.0.0.1:{port}/api/server/stats").GetAwaiter().GetResult();
            if ((int)stats.StatusCode is >= 200 and < 300)
            {
                return true;
            }

            // License middleware can return 423 for protected API routes; process is still alive.
            return (int)stats.StatusCode == 423;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureFrontendRunning(int backendPort)
    {
        var configuredFrontendPort = LoadFrontendPort();
        try
        {
            EnsureBundledFrontend(backendPort, configuredFrontendPort);
            StopOwnedFrontendProcess();
            return;
        }
        catch (System.TimeoutException)
        {
            // Fall back to the standalone Next frontend only if the bundled UI is not reachable.
        }

        var baseStartInfo = ResolveFrontendStartInfo(backendPort, configuredFrontendPort);
        if (baseStartInfo is null)
        {
            EnsureBundledFrontend(backendPort, configuredFrontendPort);
            return;
        }

        EnsureFrontendDependencies(baseStartInfo.WorkingDirectory, baseStartInfo.FileName);

        string? lastPortFailure = null;
        foreach (var frontendPort in BuildFrontendPortCandidates(configuredFrontendPort))
        {
            if (IsFrontendReachable(frontendPort))
            {
                _activeFrontendPort = frontendPort;
                return;
            }

            if (IsPortInUse(frontendPort))
            {
                if (TryRecoverSelfHostedFrontendOnPort(frontendPort))
                {
                    _activeFrontendPort = frontendPort;
                    return;
                }

                if (IsPortInUse(frontendPort))
                {
                    lastPortFailure = $"Web UI port {frontendPort} is already in use by {DescribePortOwner(frontendPort)}.";
                    continue;
                }
            }

            var startInfo = ResolveFrontendStartInfo(backendPort, frontendPort);
            if (startInfo is null)
            {
                EnsureBundledFrontend(backendPort, configuredFrontendPort);
                return;
            }

            _activeFrontendPort = frontendPort;
            CleanupFrontendLockIfStale(startInfo.WorkingDirectory, frontendPort);

            var process = new Process { StartInfo = startInfo };
            var startupOutput = new StringBuilder();
            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    startupOutput.AppendLine(args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    startupOutput.AppendLine(args.Data);
                }
            };

            if (!process.Start())
            {
                process.Dispose();
                lastPortFailure = $"Failed to start frontend process on port {frontendPort}.";
                continue;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_sync)
            {
                _ownedFrontendProcess = process;
            }

            var timeout = DateTime.UtcNow.AddSeconds(35);
            var started = false;
            while (DateTime.UtcNow < timeout)
            {
                Thread.Sleep(500);

                if (IsFrontendReachable(frontendPort))
                {
                    started = true;
                    break;
                }

                if (process.HasExited)
                {
                    break;
                }
            }

            if (started)
            {
                return;
            }

            var details = GetStartupFailureDetails(process, startupOutput);
            var addressInUse = details.Contains("EADDRINUSE", StringComparison.OrdinalIgnoreCase) ||
                               details.Contains("address already in use", StringComparison.OrdinalIgnoreCase);
            ClearOwnedFrontendProcess(process);

            if (addressInUse)
            {
                lastPortFailure = $"Web UI port {frontendPort} is already in use by another process.";
                try { process.Dispose(); } catch { }
                continue;
            }

            if (process.HasExited)
            {
                try { process.Dispose(); } catch { }
                throw new InvalidOperationException($"Frontend exited before opening localhost:{frontendPort}. {details}");
            }

            try { process.Dispose(); } catch { }
            throw new System.TimeoutException(
                $"Frontend did not start on http://localhost:{frontendPort} within 35 seconds. " +
                "Please check Node.js installation and npm dependencies.");
        }

        throw new InvalidOperationException(
            $"Unable to start frontend. {lastPortFailure ?? $"Configured Web UI port {configuredFrontendPort} could not be started."} " +
            "Close the process using the port or change Web UI Port in Service Controller.");
    }

    private void EnsureBundledFrontend(int backendPort, int configuredFrontendPort)
    {
        var frontendPort = configuredFrontendPort is >= 1 and <= 65535
            ? configuredFrontendPort
            : backendPort;
        _activeFrontendPort = frontendPort;

        var timeout = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < timeout)
        {
            Thread.Sleep(300);

            if (IsFrontendReachable(frontendPort))
            {
                return;
            }

            if (frontendPort == backendPort && IsWebApiReachable(backendPort))
            {
                return;
            }
        }

        throw new System.TimeoutException($"Web UI is not reachable on http://127.0.0.1:{frontendPort}/");
    }

    private static void EnsureFrontendDependencies(string frontendRoot, string npmCommand)
    {
        var modulesPath = Path.Combine(frontendRoot, "node_modules");
        var tailwindPostcssPath = Path.Combine(modulesPath, "@tailwindcss", "postcss");
        var xlsxPath = Path.Combine(modulesPath, "xlsx");
        if (Directory.Exists(modulesPath) &&
            Directory.Exists(tailwindPostcssPath) &&
            Directory.Exists(xlsxPath))
        {
            return;
        }

        var installInfo = new ProcessStartInfo
        {
            FileName = npmCommand,
            Arguments = "install",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = frontendRoot
        };

        using var installProcess = new Process { StartInfo = installInfo };
        var installOutput = new StringBuilder();
        installProcess.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                installOutput.AppendLine(args.Data);
            }
        };
        installProcess.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                installOutput.AppendLine(args.Data);
            }
        };

        if (!installProcess.Start())
        {
            throw new InvalidOperationException("Failed to start npm install for frontend dependencies.");
        }

        installProcess.BeginOutputReadLine();
        installProcess.BeginErrorReadLine();
        if (!installProcess.WaitForExit(180000) || installProcess.ExitCode != 0)
        {
            var details = GetStartupFailureDetails(installProcess, installOutput);
            throw new InvalidOperationException($"npm install failed. {details}");
        }
    }

    private static bool IsFrontendReachable(int port)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(250) };
            using var response = client.GetAsync($"http://127.0.0.1:{port}/").GetAwaiter().GetResult();
            var statusCode = (int)response.StatusCode;
            return response.IsSuccessStatusCode ||
                   statusCode == 423 ||
                   statusCode == 401 ||
                   statusCode == 403;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessStartInfo? ResolveFrontendStartInfo(int backendPort, int frontendPort)
    {
        var frontendRoot = ResolveFrontendRootPath();
        if (string.IsNullOrWhiteSpace(frontendRoot))
        {
            return null;
        }

        var packageJson = Path.Combine(frontendRoot, "package.json");
        if (!File.Exists(packageJson))
        {
            return null;
        }

        var npm = ResolveNpmCommand();
        var startInfo = new ProcessStartInfo
        {
            FileName = npm,
            Arguments = $"run dev -- --hostname 0.0.0.0 --port {frontendPort} --strictPort",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = frontendRoot
        };

        startInfo.Environment["HOST"] = "0.0.0.0";
        startInfo.Environment["PORT"] = frontendPort.ToString();
        startInfo.Environment["NEXT_PUBLIC_API_BASE_URL"] = $"http://127.0.0.1:{backendPort}";
        return startInfo;
    }

    private static void CleanupFrontendLockIfStale(string frontendRoot, int frontendPort)
    {
        try
        {
            if (IsFrontendReachable(frontendPort))
            {
                return;
            }

            var lockFile = Path.Combine(frontendRoot, ".next", "dev", "lock");
            if (File.Exists(lockFile))
            {
                File.Delete(lockFile);
            }
        }
        catch
        {
        }
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            listener.Stop();
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static IReadOnlyList<int> BuildFrontendPortCandidates(int configuredPort)
    {
        if (configuredPort is < 1 or > 65535)
        {
            return Array.Empty<int>();
        }

        return [configuredPort];
    }

    private void ClearOwnedFrontendProcess(Process process)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_ownedFrontendProcess, process))
            {
                _ownedFrontendProcess = null;
            }
        }
    }

    private void StopOwnedFrontendProcess()
    {
        Process? process = null;
        lock (_sync)
        {
            process = _ownedFrontendProcess;
            _ownedFrontendProcess = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            SafeKill(process);
            SafeWaitForExit(process, 5000);
        }
        catch
        {
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch
            {
            }
        }
    }

    public string CheckConfiguredPorts()
    {
        EnsureDefaults();
        var apiPort = LoadPort();
        var webPort = LoadFrontendPort();

        var lines = new List<string>();
        lines.Add($"API port {apiPort}: {DescribePortStatus(apiPort, IsWebApiReachable(apiPort), isApi: true)}");
        lines.Add($"Web UI endpoint {webPort}: {DescribePortStatus(webPort, IsFrontendReachable(webPort), isApi: false)}");
        lines.Add("Rule: Configured ports are fixed and will not auto-change.");
        return string.Join(Environment.NewLine, lines);
    }

    private bool TryRecoverSelfHostedApiOnPort(int port)
    {
        // If another instance is still booting, allow a short grace period.
        var waitUntil = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < waitUntil)
        {
            if (IsWebApiReachable(port))
            {
                return true;
            }

            Thread.Sleep(300);
        }

        var listeners = GetListeningProcessIds(port);
        if (listeners.Count == 0)
        {
            return false;
        }

        var killedAny = false;
        foreach (var pid in listeners)
        {
            if (!IsPbAttendanceWebProcess(pid))
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(true);
                process.WaitForExit(3000);
                killedAny = true;
            }
            catch
            {
            }
        }

        if (!killedAny)
        {
            return false;
        }

        var releaseWait = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < releaseWait)
        {
            if (!IsPortInUse(port))
            {
                return false;
            }

            if (IsWebApiReachable(port))
            {
                return true;
            }

            Thread.Sleep(250);
        }

        return IsWebApiReachable(port);
    }

    private static bool TryRecoverSelfHostedFrontendOnPort(int port)
    {
        var waitUntil = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < waitUntil)
        {
            if (IsFrontendReachable(port))
            {
                return true;
            }

            Thread.Sleep(300);
        }

        var listeners = GetListeningProcessIds(port);
        if (listeners.Count == 0)
        {
            return false;
        }

        var killedAny = false;
        foreach (var pid in listeners)
        {
            if (!IsPbAttendanceFrontendProcess(pid))
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(true);
                process.WaitForExit(3000);
                killedAny = true;
            }
            catch
            {
            }
        }

        if (!killedAny)
        {
            return false;
        }

        var releaseWait = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < releaseWait)
        {
            if (IsFrontendReachable(port))
            {
                return true;
            }

            if (!IsPortInUse(port))
            {
                return false;
            }

            Thread.Sleep(250);
        }

        return IsFrontendReachable(port);
    }

    private static List<int> GetListeningProcessIds(int port)
    {
        var result = new List<int>();
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano -p tcp",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = new Process { StartInfo = info };
            if (!process.Start())
            {
                return result;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var tokens = Regex.Split(line, @"\s+");
                if (tokens.Length < 5)
                {
                    continue;
                }

                var localAddress = tokens[1];
                var state = tokens[3];
                var pidText = tokens[4];
                if (!state.Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var colon = localAddress.LastIndexOf(':');
                if (colon < 0)
                {
                    continue;
                }

                var portText = localAddress[(colon + 1)..];
                if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort) ||
                    parsedPort != port)
                {
                    continue;
                }

                if (int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) &&
                    pid > 0 &&
                    !result.Contains(pid))
                {
                    result.Add(pid);
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private static bool IsPbAttendanceWebProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.ProcessName.Equals("E.Attendance.Web", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!process.ProcessName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var cmd = ReadProcessCommandLine(pid);
            return cmd.Contains("E.Attendance.Web", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPbAttendanceFrontendProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase) &&
                !process.ProcessName.Equals("npm", StringComparison.OrdinalIgnoreCase) &&
                !process.ProcessName.Equals("npm.cmd", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var cmd = ReadProcessCommandLine(pid);
            return cmd.Contains("pb-attendance-next", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string ReadProcessCommandLine(int pid)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId = {pid}').CommandLine\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = new Process { StartInfo = info };
            if (!process.Start())
            {
                return string.Empty;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(4000);
            return output ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DescribePortStatus(int port, bool reachableByPb, bool isApi)
    {
        if (reachableByPb)
        {
            return isApi ? "In use by E.Attendance.Web (reachable)." : "In use by PB Web UI (reachable).";
        }

        if (!IsPortInUse(port))
        {
            return "Free.";
        }

        var owner = DescribePortOwner(port);
        return $"In use by {owner}.";
    }

    private static string DescribePortOwner(int port)
    {
        var pids = GetListeningProcessIds(port);
        if (pids.Count == 0)
        {
            return "unknown process";
        }

        var names = new List<string>();
        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                var name = $"{process.ProcessName} (PID {pid})";
                if (!names.Contains(name))
                {
                    names.Add(name);
                }
            }
            catch
            {
                var fallback = $"PID {pid}";
                if (!names.Contains(fallback))
                {
                    names.Add(fallback);
                }
            }

            if (names.Count >= 3)
            {
                break;
            }
        }

        return string.Join(", ", names);
    }

    private static void TryKillPortOwner(int port)
    {
        var pids = GetListeningProcessIds(port);
        foreach (var pid in pids)
        {
            try { using var p = Process.GetProcessById(pid); p.Kill(true); } catch { }
        }
    }

    private static string? ResolveFrontendRootPath()
    {
        var envOverride = Environment.GetEnvironmentVariable("PB_ATTENDANCE_FRONTEND_PATH");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            var full = Path.GetFullPath(envOverride);
            if (File.Exists(Path.Combine(full, "package.json")))
            {
                return full;
            }
        }

        var solutionCandidate = Path.Combine(ResolveSolutionRoot(), "pb-attendance-next");
        if (File.Exists(Path.Combine(solutionCandidate, "package.json")))
        {
            return solutionCandidate;
        }

#if DEBUG
        var knownPaths = new[]
        {
            @"D:\Pankaj\Project\P\PB Attendance\pb-attendance-next",
            @"D:\Pankaj\Project\PB Attendance\PB Attendance\pb-attendance-next"
        };
        foreach (var candidate in knownPaths)
        {
            if (File.Exists(Path.Combine(candidate, "package.json")))
            {
                return candidate;
            }
        }
#endif

        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && cursor is not null; i++)
        {
            var sibling = Path.Combine(cursor.FullName, "pb-attendance-next");
            if (File.Exists(Path.Combine(sibling, "package.json")))
            {
                return sibling;
            }

            // Dev layout fallback when current folder is a project folder.
            var parentSibling = cursor.Parent is null
                ? null
                : Path.Combine(cursor.Parent.FullName, "pb-attendance-next");
            if (!string.IsNullOrWhiteSpace(parentSibling) &&
                File.Exists(Path.Combine(parentSibling, "package.json")))
            {
                return parentSibling;
            }

            cursor = cursor.Parent;
        }

        var cwdCandidate = Path.Combine(Environment.CurrentDirectory, "pb-attendance-next");
        if (File.Exists(Path.Combine(cwdCandidate, "package.json")))
        {
            return cwdCandidate;
        }

        return null;
    }

    private static string ResolveNpmCommand()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "npm.cmd")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "npm.cmd";
    }

    private static ProcessStartInfo? ResolveWebStartInfo()
    {
        var baseDir = AppContext.BaseDirectory;
        var solutionRoot = ResolveSolutionRoot();
        var candidateDirs = new List<string>();

        void AddCandidateDir(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            var full = Path.GetFullPath(directory);
            if (candidateDirs.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            candidateDirs.Add(full);
        }

        AddCandidateDir(baseDir);
        AddCandidateDir(Path.Combine(baseDir, "E.Attendance.Web"));
        AddCandidateDir(Path.Combine(baseDir, "E.Attendance.Web.ExeOnly"));
        AddCandidateDir(Environment.CurrentDirectory);
        AddCandidateDir(Path.Combine(solutionRoot, "artifacts", "single-exe", "E.Attendance.Web"));
        AddCandidateDir(Path.Combine(solutionRoot, "artifacts", "single-exe", "E.Attendance.Web.ExeOnly"));
        AddCandidateDir(Path.Combine(solutionRoot, "E.Attendance.Web", "bin", "Release", "net10.0", "win-x64"));
        AddCandidateDir(Path.Combine(solutionRoot, "E.Attendance.Web", "bin", "Debug", "net10.0", "win-x64"));

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            AddCandidateDir(Path.GetDirectoryName(processPath));
        }

        try
        {
            AddCandidateDir(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName));
        }
        catch
        {
            // Best-effort only.
        }

        // Prefer latest E.Attendance.Web build output from the solution during development.
        var webBinRoot = Path.Combine(solutionRoot, "E.Attendance.Web", "bin");
        if (Directory.Exists(webBinRoot))
        {
            var outputDirs = ResolveWebOutputDirectories(webBinRoot).ToArray();
            foreach (var outputDir in outputDirs)
            {
                var runtimeCopyDir = TryPrepareWebRuntimeCopy(outputDir);
                if (!string.IsNullOrWhiteSpace(runtimeCopyDir))
                {
                    var copiedStartInfo = BuildWebStartInfoForDirectory(runtimeCopyDir);
                    if (copiedStartInfo is not null)
                    {
                        return copiedStartInfo;
                    }
                }
    
                // Fallback to source output directory when runtime copy is unavailable.
                var fallbackStartInfo = BuildWebStartInfoForDirectory(outputDir);
                if (fallbackStartInfo is not null)
                {
                    return fallbackStartInfo;
                }
            }
        }

        foreach (var candidateDir in candidateDirs)
        {
            var installedStartInfo = BuildWebStartInfoForDirectory(candidateDir);
            if (installedStartInfo is not null)
            {
                return installedStartInfo;
            }
        }

        return null;
    }

    private static ProcessStartInfo? BuildWebStartInfoForDirectory(string directory)
    {
        var exe = Path.Combine(directory, "E.Attendance.Web.exe");
        if (File.Exists(exe))
        {
            return new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory
            };
        }

        var dll = Path.Combine(directory, "E.Attendance.Web.dll");
        if (File.Exists(dll))
        {
            return new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{dll}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory
            };
        }

        return null;
    }

    private static string? TryPrepareWebRuntimeCopy(string sourceOutputDir)
    {
        try
        {
            var markerPath = ResolveWebRuntimeMarkerPath(sourceOutputDir);
            if (string.IsNullOrWhiteSpace(markerPath) || !File.Exists(markerPath))
            {
                return null;
            }

            var appDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "E_Attendance");
            var runtimeRoot = Path.Combine(appDataRoot, "runtime", "web");
            Directory.CreateDirectory(runtimeRoot);

            var configName = Path.GetFileName(Path.GetDirectoryName(sourceOutputDir)) ?? "Debug";
            var frameworkName = Path.GetFileName(sourceOutputDir);
            var stamp = BuildWebRuntimeCopyStamp(sourceOutputDir, markerPath);
            var copyDirectoryName = $"{configName}-{frameworkName}-{stamp}";
            var targetDirectory = Path.Combine(runtimeRoot, copyDirectoryName);

            if (!Directory.Exists(targetDirectory))
            {
                CopyDirectoryRecursive(sourceOutputDir, targetDirectory);
            }

            CleanupStaleWebRuntimeCopies(runtimeRoot, targetDirectory, keepCount: 6);
            return targetDirectory;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveWebRuntimeMarkerPath(string outputDir)
    {
        var exe = Path.Combine(outputDir, "E.Attendance.Web.exe");
        if (File.Exists(exe))
        {
            return exe;
        }

        var dll = Path.Combine(outputDir, "E.Attendance.Web.dll");
        return File.Exists(dll) ? dll : null;
    }

    private static string BuildWebRuntimeCopyStamp(string sourceOutputDir, string markerPath)
    {
        var markerTicks = File.GetLastWriteTimeUtc(markerPath).Ticks.ToString(CultureInfo.InvariantCulture);
        var webRoot = Path.Combine(sourceOutputDir, "wwwroot");
        if (!Directory.Exists(webRoot))
        {
            return markerTicks;
        }

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendHashLine(hash, $"marker:{markerTicks}");

            foreach (var filePath in Directory.EnumerateFiles(webRoot, "*", SearchOption.AllDirectories)
                         .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                var fileInfo = new FileInfo(filePath);
                var relativePath = Path.GetRelativePath(sourceOutputDir, filePath).Replace('\\', '/');
                AppendHashLine(hash, $"{relativePath}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}");
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch
        {
            // Fallback to coarse stamp so startup is never blocked by fingerprinting.
            var webRootTicks = Directory.GetLastWriteTimeUtc(webRoot).Ticks.ToString(CultureInfo.InvariantCulture);
            return $"{markerTicks}-{webRootTicks}";
        }
    }

    private static void AppendHashLine(IncrementalHash hash, string value)
    {
        var valueBytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(valueBytes);
        Span<byte> newline = stackalloc byte[] { (byte)'\n' };
        hash.AppendData(newline);
    }

    private static void CopyDirectoryRecursive(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory))
        {
            var fileName = Path.GetFileName(filePath);
            var targetPath = Path.Combine(targetDirectory, fileName);
            File.Copy(filePath, targetPath, overwrite: true);
        }

        foreach (var childSourceDirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var childName = Path.GetFileName(childSourceDirectory);
            var childTargetDirectory = Path.Combine(targetDirectory, childName);
            CopyDirectoryRecursive(childSourceDirectory, childTargetDirectory);
        }
    }

    private static void CleanupStaleWebRuntimeCopies(string runtimeRoot, string activeDirectory, int keepCount)
    {
        try
        {
            var directories = Directory
                .GetDirectories(runtimeRoot)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .ToArray();

            var retained = 0;
            var retentionLimit = Math.Max(keepCount, 1);
            foreach (var directory in directories)
            {
                if (string.Equals(directory, activeDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    retained++;
                    continue;
                }

                retained++;
                if (retained <= retentionLimit)
                {
                    continue;
                }

                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
        catch
        {
            // Cleanup should never block startup.
        }
    }

    private static IEnumerable<string> ResolveWebOutputDirectories(string webBinRoot)
    {
        var preferredFrameworks = new[] { "net10.0", "net8.0" };
        var configurations = new[] { "Debug", "Release" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        void AddCandidate(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            if (!seen.Add(directory))
            {
                return;
            }

            candidates.Add(directory);
        }

        foreach (var framework in preferredFrameworks)
        {
            foreach (var configuration in configurations)
            {
                AddCandidate(Path.Combine(webBinRoot, configuration, framework));
            }
        }

        foreach (var configuration in configurations)
        {
            var configRoot = Path.Combine(webBinRoot, configuration);
            if (!Directory.Exists(configRoot))
            {
                continue;
            }

            foreach (var dir in Directory.GetDirectories(configRoot, "net*")
                         .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                AddCandidate(dir);
            }
        }

        static long GetMarkerTicks(string directory)
        {
            var markerPath = ResolveWebRuntimeMarkerPath(directory);
            if (string.IsNullOrWhiteSpace(markerPath) || !File.Exists(markerPath))
            {
                return DateTime.MinValue.Ticks;
            }

            return File.GetLastWriteTimeUtc(markerPath).Ticks;
        }

        foreach (var candidate in candidates
                     .OrderByDescending(GetMarkerTicks)
                     .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            yield return candidate;
        }
    }

    private static string GetStartupFailureDetails(Process process, StringBuilder outputBuffer)
    {
        var output = outputBuffer.ToString().Trim();
        var exitCode = process.ExitCode;
        if (string.IsNullOrWhiteSpace(output))
        {
            return $"Exit code: {exitCode}.";
        }

        var normalized = output.Replace("\r", string.Empty);
        var lines = normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tail = lines.Length > 0
            ? string.Join(" ", lines.Skip(Math.Max(0, lines.Length - 24)))
            : normalized;
        tail = Regex.Replace(tail, @"\s+", " ").Trim();
        if (tail.Length > 2200)
        {
            tail = "..." + tail[^2200..];
        }

        return $"Exit code: {exitCode}. {tail}";
    }

    private WebSettings LoadSettings()
    {
        EnsureDefaults();
        try
        {
            var json = File.ReadAllText(SettingsPath);
            var parsed = JsonSerializer.Deserialize<WebSettings>(json);
            if (parsed is null)
            {
                return new WebSettings(DefaultPort, FrontendDefaultPort);
            }

            var apiPort = parsed.Port is >= 1 and <= 65535 ? parsed.Port : DefaultPort;
            var frontendPort = parsed.FrontendPort is >= 1 and <= 65535 ? parsed.FrontendPort : FrontendDefaultPort;
            if (frontendPort == apiPort)
            {
                frontendPort = apiPort == FrontendDefaultPort ? FrontendFallbackPort : FrontendDefaultPort;
                SavePorts(apiPort, frontendPort);
            }

            return new WebSettings(apiPort, frontendPort);
        }
        catch
        {
            return new WebSettings(DefaultPort, FrontendDefaultPort);
        }
    }

    private sealed record WebSettings(int Port, int FrontendPort);

    private static string ResolveSolutionRoot()
    {
        string? resolved = null;
        try
        {
            resolved = TryLocateSolutionRoot();
        }
        catch
        {
            resolved = null;
        }

        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        var baseDir = AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
    }

    private static string? TryLocateSolutionRoot()
    {
        var candidates = new List<string>();

        void AddCandidate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var full = Path.GetFullPath(value);
            if (!candidates.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(full);
            }
        }

        AddCandidate(AppContext.BaseDirectory);
        AddCandidate(Environment.CurrentDirectory);
        AddCandidate(Path.GetDirectoryName(Environment.ProcessPath));

        foreach (var candidate in candidates)
        {
            var root = TryLocateSolutionRootFrom(candidate);
            if (!string.IsNullOrWhiteSpace(root))
            {
                return root;
            }
        }

        return null;
    }

    private static string? TryLocateSolutionRootFrom(string startDirectory)
    {
        try
        {
            var cursor = new DirectoryInfo(Path.GetFullPath(startDirectory));
            for (var i = 0; i < 10 && cursor is not null; i++)
            {
                if (File.Exists(Path.Combine(cursor.FullName, "E-Attendance.sln")) ||
                    File.Exists(Path.Combine(cursor.FullName, "E-Attendance.slnx")))
                {
                    return cursor.FullName;
                }

                if (Directory.Exists(Path.Combine(cursor.FullName, "E.Attendance.Web")) &&
                    Directory.Exists(Path.Combine(cursor.FullName, "E.Attendance")))
                {
                    return cursor.FullName;
                }

                cursor = cursor.Parent;
            }
        }
        catch
        {
        }

        return null;
    }

    private string ResolvePostgresSetupScriptPath()
    {
        var settingsDirectory = Path.GetDirectoryName(ResolveWebAppSettingsPath()) ?? string.Empty;
        var candidates = new[]
        {
            Path.Combine(ResolveSolutionRoot(), "E.Attendance.Web", "01_CreateDatabase.sql"),
            Path.Combine(AppContext.BaseDirectory, "01_CreateDatabase.sql"),
            string.IsNullOrWhiteSpace(settingsDirectory) ? string.Empty : Path.Combine(settingsDirectory, "01_CreateDatabase.sql")
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    private string ResolveWebAppSettingsPath()
    {
        var envOverride = Environment.GetEnvironmentVariable("PB_ATTENDANCE_WEB_APPSETTINGS");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            var full = Path.GetFullPath(envOverride);
            EnsureWebAppSettingsFile(full);
            return full;
        }

        var baseDir = AppContext.BaseDirectory;
        var baseCandidate = Path.Combine(baseDir, "appsettings.json");

        static bool HasWebBinary(string directory)
            => File.Exists(Path.Combine(directory, "E.Attendance.Web.exe")) ||
               File.Exists(Path.Combine(directory, "E.Attendance.Web.dll"));

        // Prefer the appsettings.json that sits next to the Web binary being managed.
        if (HasWebBinary(baseDir))
        {
            EnsureWebAppSettingsFile(baseCandidate);
            return baseCandidate;
        }

        var nestedWebDirCandidates = new[]
        {
            Path.Combine(baseDir, "E.Attendance.Web"),
            Path.Combine(baseDir, "E.Attendance.Web.ExeOnly")
        };
        foreach (var directory in nestedWebDirCandidates)
        {
            if (!HasWebBinary(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory, "appsettings.json");
            EnsureWebAppSettingsFile(candidate);
            return candidate;
        }

        try
        {
            var baseParent = Directory.GetParent(baseDir)?.FullName;
            if (!string.IsNullOrWhiteSpace(baseParent))
            {
                var siblingCandidates = new[]
                {
                    Path.Combine(baseParent, "E.Attendance.Web"),
                    Path.Combine(baseParent, "E.Attendance.Web.ExeOnly")
                };
                foreach (var directory in siblingCandidates)
                {
                    if (!HasWebBinary(directory))
                    {
                        continue;
                    }

                    var candidate = Path.Combine(directory, "appsettings.json");
                    EnsureWebAppSettingsFile(candidate);
                    return candidate;
                }
            }
        }
        catch
        {
            // Best-effort only.
        }

        var solutionCandidate = Path.Combine(ResolveSolutionRoot(), "E.Attendance.Web", "appsettings.json");
        if (File.Exists(solutionCandidate))
        {
            return solutionCandidate;
        }

        if (File.Exists(baseCandidate))
        {
            return baseCandidate;
        }

        var appDataCandidate = Path.Combine(AppDataRoot, "web-appsettings.runtime.json");
        EnsureWebAppSettingsFile(appDataCandidate, baseCandidate, solutionCandidate);
        return appDataCandidate;
    }

    private JsonObject? LoadWebAppSettingsJson()
    {
        var path = ResolveWebAppSettingsPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            // Skip leading null bytes
            int start = 0;
            while (start < bytes.Length && bytes[start] == 0)
            {
                start++;
            }

            if (start >= bytes.Length)
            {
                // The file is all null bytes or empty
                return null;
            }

            var json = Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            // If the JSON is invalid (e.g., starts with null), return null so defaults are used.
            return null;
        }
        catch (Exception)
        {
            // Handle other exceptions (e.g., IO) by returning null.
            return null;
        }
    }

    private void SaveWebAppSettingsJson(JsonObject root)
    {
        var path = ResolveWebAppSettingsPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private void EnsureWebAppSettingsFile(string targetPath, params string[] templateCandidates)
    {
        if (File.Exists(targetPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        foreach (var candidate in templateCandidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (!File.Exists(candidate))
            {
                continue;
            }

            File.Copy(candidate, targetPath, overwrite: false);
            return;
        }

        var root = CreateDefaultWebAppSettingsJson();
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(targetPath, json);
    }

    private static JsonObject CreateDefaultWebAppSettingsJson()
    {
        var connectionStringBuilder = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = ExpectedPostgreSqlPort,
            Database = ExpectedPostgreSqlDatabase,
            Username = ExpectedPostgreSqlUser,
            SearchPath = ExpectedPostgreSqlSchema,
            IncludeErrorDetail = true,
            MaxPoolSize = 100,
            ConnectionLifetime = 300,
            Password = string.Empty
        };

        return new JsonObject
        {
            ["ConnectionStrings"] = new JsonObject
            {
                ["PostgreSQLConnection"] = connectionStringBuilder.ConnectionString
            },
            ["DatabaseSettings"] = new JsonObject
            {
                ["UsePostgreSql"] = true,
                ["ServiceName"] = ExpectedPostgreSqlServiceName,
                ["CommandTimeout"] = 30,
                ["MaxRetryCount"] = 3
            },
            ["License"] = new JsonObject
            {
                ["Directory"] = string.Empty,
                ["PublicKeyPath"] = string.Empty
            },
            ["AllowedHosts"] = "*"
        };
    }

    private string? GetRawPostgreSqlConnectionString()
    {
        var node = LoadWebAppSettingsJson();
        if (node?["ConnectionStrings"]?["PostgreSQLConnection"] is JsonValue value &&
            value.TryGetValue<string>(out var conn))
        {
            return conn;
        }

        return null;
    }

    private string GetConfiguredApplicationDbPassword()
    {
        var raw = GetRawPostgreSqlConnectionString();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var rawBuilder = new NpgsqlConnectionStringBuilder(raw);
            if (!string.IsNullOrWhiteSpace(rawBuilder.Password))
            {
                return rawBuilder.Password;
            }
        }

        return ExpectedPostgreSqlPassword;
    }

    public string? GetPostgreSqlConnectionString(bool includePassword = true)
    {
        var raw = GetRawPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        var builder = new NpgsqlConnectionStringBuilder(raw);
        if (includePassword)
        {
            if (string.IsNullOrWhiteSpace(builder.Password))
            {
                builder.Password = GetConfiguredApplicationDbPassword();
            }
        }
        else if (!string.IsNullOrWhiteSpace(builder.Password))
        {
            builder.Password = string.Empty;
        }

        return builder.ConnectionString;
    }

    public int GetConfiguredPostgreSqlPort()
    {
        var conn = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(conn))
        {
            return ExpectedPostgreSqlPort;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(conn);
            return builder.Port is >= 1 and <= 65535 ? builder.Port : ExpectedPostgreSqlPort;
        }
        catch
        {
            return ExpectedPostgreSqlPort;
        }
    }

    private string GetPostgreSqlServiceName()
    {
        var node = LoadWebAppSettingsJson();
        if (node?["DatabaseSettings"]?["ServiceName"] is JsonValue value &&
            value.TryGetValue<string>(out var serviceName) &&
            !string.IsNullOrWhiteSpace(serviceName))
        {
            return serviceName;
        }

        return ExpectedPostgreSqlServiceName;
    }

    private void SetPostgreSqlServiceName(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return;
        }

        var root = LoadWebAppSettingsJson() ?? new JsonObject();
        var dbSettings = root["DatabaseSettings"] as JsonObject ?? new JsonObject();
        dbSettings["ServiceName"] = serviceName;
        root["DatabaseSettings"] = dbSettings;
        SaveWebAppSettingsJson(root);
    }

    private string ResolveOrProvisionPostgreSqlServiceName()
    {
        var configured = GetPostgreSqlServiceName();
        if (ServiceExists(configured))
        {
            return configured;
        }

        if (!string.Equals(configured, ExpectedPostgreSqlServiceName, StringComparison.OrdinalIgnoreCase) &&
            ServiceExists(ExpectedPostgreSqlServiceName))
        {
            SetPostgreSqlServiceName(ExpectedPostgreSqlServiceName);
            return ExpectedPostgreSqlServiceName;
        }

        var detected = FindInstalledPostgreSqlServiceName();
        if (!string.IsNullOrWhiteSpace(detected))
        {
            if (!ServiceExists(ExpectedPostgreSqlServiceName) &&
                !string.Equals(detected, ExpectedPostgreSqlServiceName, StringComparison.OrdinalIgnoreCase))
            {
                if (TryCreateServiceAlias(ExpectedPostgreSqlServiceName, detected) &&
                    ServiceExists(ExpectedPostgreSqlServiceName))
                {
                    SetPostgreSqlServiceName(ExpectedPostgreSqlServiceName);
                    return ExpectedPostgreSqlServiceName;
                }
            }

            SetPostgreSqlServiceName(detected);
            return detected;
        }

        return configured;
    }

    private static bool ServiceExists(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return false;
        }

        try
        {
            using var svc = new ServiceController(serviceName);
            _ = svc.Status;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindInstalledPostgreSqlServiceName()
    {
        if (!ServiceControllerAvailable)
        {
            return null;
        }
        
        try
        {
            var matches = ServiceController.GetServices()
                .Where(s =>
                    s.ServiceName.Contains("postgres", StringComparison.OrdinalIgnoreCase) ||
                    s.DisplayName.Contains("postgres", StringComparison.OrdinalIgnoreCase))
                .Select(s =>
                {
                    var isRunning = false;
                    try
                    {
                        isRunning = s.Status == ServiceControllerStatus.Running;
                    }
                    catch
                    {
                    }

                    return new { s.ServiceName, IsRunning = isRunning };
                })
                .OrderByDescending(x => x.IsRunning)
                .ThenBy(x => x.ServiceName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return matches.Count > 0 ? matches[0].ServiceName : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryCreateServiceAlias(string aliasServiceName, string sourceServiceName)
    {
        try
        {
            var sourcePath = GetServiceBinaryPath(sourceServiceName);
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return false;
            }

            var createInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"create \"{aliasServiceName}\" binPath= \"{sourcePath}\" start= auto DisplayName= \"{aliasServiceName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var create = Process.Start(createInfo);
            if (create is null)
            {
                return false;
            }

            create.WaitForExit(10000);
            return create.ExitCode == 0 || ServiceExists(aliasServiceName);
        }
        catch
        {
            return false;
        }
    }

    private static string? GetServiceBinaryPath(string serviceName)
    {
        try
        {
            var queryInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"qc \"{serviceName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(queryInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0)
            {
                return null;
            }

            var line = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(x => x.Contains("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var idx = line.IndexOf(':');
            if (idx < 0 || idx == line.Length - 1)
            {
                return null;
            }

            return line[(idx + 1)..].Trim();
        }
        catch
        {
            return null;
        }
    }

    private bool EnsurePostgreSqlControllerSettings()
    {
        var targetServiceName = ResolveOrProvisionPostgreSqlServiceName();
        var root = LoadWebAppSettingsJson() ?? new JsonObject();
        var connectionStrings = root["ConnectionStrings"] as JsonObject ?? new JsonObject();
        var dbSettings = root["DatabaseSettings"] as JsonObject ?? new JsonObject();

        var existingConnection = GetRawPostgreSqlConnectionString();

        var builder = !string.IsNullOrWhiteSpace(existingConnection)
            ? new NpgsqlConnectionStringBuilder(existingConnection)
            : new NpgsqlConnectionStringBuilder();

        if (string.IsNullOrWhiteSpace(builder.Host))
        {
            builder.Host = "127.0.0.1";
        }
        else
        {
            builder.Host = "127.0.0.1";
        }

        builder.Port = ResolvePreferredPostgreSqlPort(builder.Host, builder.Port);

        builder.Database = ExpectedPostgreSqlDatabase;
        builder.SearchPath = ExpectedPostgreSqlSchema;
        builder.Username = ExpectedPostgreSqlUser;
        builder.IncludeErrorDetail = true;

        if (builder.MaxPoolSize <= 0)
        {
            builder.MaxPoolSize = 100;
        }

        if (builder.ConnectionLifetime <= 0)
        {
            builder.ConnectionLifetime = 300;
        }

        var newConnectionString = builder.ConnectionString;
        var existingServiceName = dbSettings["ServiceName"]?.GetValue<string>();
        var existingUsePg = dbSettings["UsePostgreSql"]?.GetValue<bool?>();
        var changed =
            !string.Equals(existingConnection, newConnectionString, StringComparison.Ordinal) ||
            !string.Equals(existingServiceName, targetServiceName, StringComparison.Ordinal) ||
            existingUsePg != true;

        if (!changed)
        {
            return false;
        }

        connectionStrings["PostgreSQLConnection"] = newConnectionString;
        dbSettings["UsePostgreSql"] = true;
        dbSettings["ServiceName"] = targetServiceName;

        root["ConnectionStrings"] = connectionStrings;
        root["DatabaseSettings"] = dbSettings;
        SaveWebAppSettingsJson(root);
        return true;
    }

    private int ResolvePreferredPostgreSqlPort(string? host, int configuredPort)
    {
        // User requested strict adherence to port 1277.
        // We no longer perform "Smart Port Discovery" or fallback probing.
        return ExpectedPostgreSqlPort;
    }

    private bool CanConnectWithExpectedCredentials(string host, int port)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = host,
                Port = port,
                Database = ExpectedPostgreSqlDatabase,
                Username = ExpectedPostgreSqlUser,
                Password = GetConfiguredApplicationDbPassword(),
                Timeout = 3,
                CommandTimeout = 3
            };

            using var conn = new NpgsqlConnection(builder.ConnectionString);
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT 1;", conn);
            _ = cmd.ExecuteScalar();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool CanConnectWithDefaultPostgresAdmin(string host, int port)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = host,
                Port = port,
                Database = "postgres",
                Username = "postgres",
                Password = ExpectedPostgreSqlPassword,
                Timeout = 3,
                CommandTimeout = 3
            };

            using var conn = new NpgsqlConnection(builder.ConnectionString);
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT 1;", conn);
            _ = cmd.ExecuteScalar();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTcpPortReachable(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            return task.Wait(timeoutMs) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private bool EnsureExpectedPostgreSqlAccess(string? adminPasswordOverride)
    {
        var expected = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var expectedBuilder = new NpgsqlConnectionStringBuilder(expected);
        var expectedProbe = ProbePostgreSql(expectedBuilder);
        if (expectedProbe.ok)
        {
            return false;
        }

        var targetApplicationPassword = GetConfiguredApplicationDbPassword();

        var host = string.IsNullOrWhiteSpace(expectedBuilder.Host) ? "127.0.0.1" : expectedBuilder.Host;
        var configuredPort = expectedBuilder.Port <= 0 ? ExpectedPostgreSqlPort : expectedBuilder.Port;
        var ports = BuildPostgreSqlPortCandidates(configuredPort);
        var adminPasswords = new[]
        {
            adminPasswordOverride,
            LoadAdminPassword(),
            ExpectedPostgreSqlPassword
        }
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.Ordinal)
        .ToList();

        foreach (var port in ports)
        {
            foreach (var adminPassword in adminPasswords)
            {
                var adminBuilder = new NpgsqlConnectionStringBuilder
                {
                    Host = host,
                    Port = port,
                    Database = "postgres",
                    Username = "postgres",
                    Password = adminPassword,
                    Timeout = 5,
                    CommandTimeout = 10
                };

                try
                {
                    using var adminConn = new NpgsqlConnection(adminBuilder.ConnectionString);
                    adminConn.Open();

                    var changed = false;

                    // Ensure role exists and force expected password.
                    var roleExists = false;
                    using (var roleCheck = new NpgsqlCommand("SELECT 1 FROM pg_roles WHERE rolname = @role LIMIT 1;", adminConn))
                    {
                        roleCheck.Parameters.AddWithValue("@role", ExpectedPostgreSqlUser);
                        roleExists = roleCheck.ExecuteScalar() is not null;
                    }

                    if (!roleExists)
                    {
                        using var createRole = new NpgsqlCommand(
                            $"CREATE ROLE \"{ExpectedPostgreSqlUser}\" WITH LOGIN PASSWORD '{EscapeSqlLiteral(targetApplicationPassword)}';",
                            adminConn);
                        createRole.ExecuteNonQuery();
                        changed = true;
                    }
                    else
                    {
                        using var alterRole = new NpgsqlCommand(
                            $"ALTER ROLE \"{ExpectedPostgreSqlUser}\" WITH LOGIN PASSWORD '{EscapeSqlLiteral(targetApplicationPassword)}';",
                            adminConn);
                        alterRole.ExecuteNonQuery();
                    }

                    // Ensure database exists and is owned by expected user.
                    var dbExists = false;
                    using (var dbCheck = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @db LIMIT 1;", adminConn))
                    {
                        dbCheck.Parameters.AddWithValue("@db", ExpectedPostgreSqlDatabase);
                        dbExists = dbCheck.ExecuteScalar() is not null;
                    }

                    if (!dbExists)
                    {
                        using var createDb = new NpgsqlCommand(
                            $"CREATE DATABASE \"{ExpectedPostgreSqlDatabase}\" OWNER \"{ExpectedPostgreSqlUser}\";",
                            adminConn);
                        createDb.ExecuteNonQuery();
                        changed = true;
                    }
                    else
                    {
                        using var alterOwner = new NpgsqlCommand(
                            $"ALTER DATABASE \"{ExpectedPostgreSqlDatabase}\" OWNER TO \"{ExpectedPostgreSqlUser}\";",
                            adminConn);
                        alterOwner.ExecuteNonQuery();
                    }

                    EnsurePostgreSqlObjectOwnership(adminBuilder, host, port);

                    var refreshed = GetPostgreSqlConnectionString();
                    if (!string.IsNullOrWhiteSpace(refreshed))
                    {
                        var refreshedBuilder = new NpgsqlConnectionStringBuilder(refreshed);
                        var refreshedProbe = ProbePostgreSql(refreshedBuilder);
                        if (refreshedProbe.ok)
                        {
                            SaveAdminPassword(adminPassword!);
                            return changed;
                        }
                    }
                }
                catch
                {
                    // Try next admin credential/port; final failure is handled by normal TestPostgreSqlConnection result.
                }
            }
        }

        return false;
    }

    private static void EnsurePostgreSqlObjectOwnership(NpgsqlConnectionStringBuilder adminBuilder, string host, int port)
    {
        var dbAdmin = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
        {
            Host = host,
            Port = port,
            Database = ExpectedPostgreSqlDatabase,
            Timeout = 8,
            CommandTimeout = 20
        };

        using var conn = new NpgsqlConnection(dbAdmin.ConnectionString);
        conn.Open();

        // Keep app objects isolated in dedicated schema and owned by app role.
        using var cmd = new NpgsqlCommand(
            $@"
CREATE SCHEMA IF NOT EXISTS ""{ExpectedPostgreSqlSchema}"" AUTHORIZATION ""{ExpectedPostgreSqlUser}"";
ALTER SCHEMA ""{ExpectedPostgreSqlSchema}"" OWNER TO ""{ExpectedPostgreSqlUser}"";
GRANT ALL PRIVILEGES ON SCHEMA ""{ExpectedPostgreSqlSchema}"" TO ""{ExpectedPostgreSqlUser}"";
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA ""{ExpectedPostgreSqlSchema}"" TO ""{ExpectedPostgreSqlUser}"";
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA ""{ExpectedPostgreSqlSchema}"" TO ""{ExpectedPostgreSqlUser}"";
GRANT ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA ""{ExpectedPostgreSqlSchema}"" TO ""{ExpectedPostgreSqlUser}"";
ALTER DEFAULT PRIVILEGES IN SCHEMA ""{ExpectedPostgreSqlSchema}"" GRANT ALL ON TABLES TO ""{ExpectedPostgreSqlUser}"";
ALTER DEFAULT PRIVILEGES IN SCHEMA ""{ExpectedPostgreSqlSchema}"" GRANT ALL ON SEQUENCES TO ""{ExpectedPostgreSqlUser}"";
ALTER DEFAULT PRIVILEGES IN SCHEMA ""{ExpectedPostgreSqlSchema}"" GRANT ALL ON FUNCTIONS TO ""{ExpectedPostgreSqlUser}"";

DO $do$
DECLARE r RECORD;
BEGIN
  FOR r IN SELECT tablename FROM pg_tables WHERE schemaname='{ExpectedPostgreSqlSchema}' LOOP
    EXECUTE format('ALTER TABLE %I.%I OWNER TO ""{ExpectedPostgreSqlUser}""', '{ExpectedPostgreSqlSchema}', r.tablename);
  END LOOP;
  FOR r IN SELECT sequencename FROM pg_sequences WHERE schemaname='{ExpectedPostgreSqlSchema}' LOOP
    EXECUTE format('ALTER SEQUENCE %I.%I OWNER TO ""{ExpectedPostgreSqlUser}""', '{ExpectedPostgreSqlSchema}', r.sequencename);
  END LOOP;
  FOR r IN SELECT viewname FROM pg_views WHERE schemaname='{ExpectedPostgreSqlSchema}' LOOP
    EXECUTE format('ALTER VIEW %I.%I OWNER TO ""{ExpectedPostgreSqlUser}""', '{ExpectedPostgreSqlSchema}', r.viewname);
  END LOOP;
END
$do$;",
            conn);
        cmd.ExecuteNonQuery();
    }

    private static string EscapeSqlLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string ResolveExpectedPostgreSqlPassword()
    {
        var env = Environment.GetEnvironmentVariable("PB_ATTENDANCE_DB_PASSWORD");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        // Base64 fallback keeps password out of plain-text config/UI; env var can override in production.
        const string fallbackBase64 = "UEJAMTQ4OTEyNzc=";
        return Encoding.UTF8.GetString(Convert.FromBase64String(fallbackBase64));
    }

    private string? LoadAdminPassword()
    {
        try
        {
            if (!File.Exists(PgAdminSecretPath))
            {
                return null;
            }

            var raw = File.ReadAllBytes(PgAdminSecretPath);
            var unprotected = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(unprotected);
        }
        catch
        {
            return null;
        }
    }

    private void SaveAdminPassword(string password)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                return;
            }

            Directory.CreateDirectory(AppDataRoot);
            var bytes = Encoding.UTF8.GetBytes(password);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(PgAdminSecretPath, protectedBytes);
        }
        catch
        {
            // Non-fatal: runtime can still ask again next time.
        }
    }

    private string RedactSecrets(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sanitized = text.Replace(ExpectedPostgreSqlPassword, "***", StringComparison.Ordinal);
        var configuredPassword = GetConfiguredApplicationDbPassword();
        if (!string.IsNullOrWhiteSpace(configuredPassword))
        {
            sanitized = sanitized.Replace(configuredPassword, "***", StringComparison.Ordinal);
        }

        sanitized = Regex.Replace(sanitized, @"(?i)(password\s*=\s*)([^;,\s]+)", "$1***");
        sanitized = Regex.Replace(sanitized, @"(?i)(pwd\s*=\s*)([^;,\s]+)", "$1***");
        return sanitized;
    }

    private static string? ResolvePsqlExecutable()
    {
        var envOverride = Environment.GetEnvironmentVariable("PB_PSQL_PATH");
        if (!string.IsNullOrWhiteSpace(envOverride) && File.Exists(envOverride))
        {
            return envOverride;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(segment, "psql.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }
        .Where(x => !string.IsNullOrWhiteSpace(x));

        foreach (var root in roots)
        {
            var pgRoot = Path.Combine(root, "PostgreSQL");
            if (!Directory.Exists(pgRoot))
            {
                continue;
            }

            var bins = Directory.GetDirectories(pgRoot)
                .Select(x => Path.Combine(x, "bin", "psql.exe"))
                .Where(File.Exists)
                .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (bins.Count > 0)
            {
                return bins[0];
            }
        }

        return null;
    }

    private static void EnsurePathContains(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return;
        }

        TryEnsurePathContainsForTarget(EnvironmentVariableTarget.Machine, directoryPath);
        TryEnsurePathContainsForTarget(EnvironmentVariableTarget.User, directoryPath);

        var processPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!PathVariableContains(processPath, directoryPath))
        {
            var updated = AppendPathSegment(processPath, directoryPath);
            Environment.SetEnvironmentVariable("PATH", updated);
        }
    }

    private static void TryEnsurePathContainsForTarget(EnvironmentVariableTarget target, string directoryPath)
    {
        try
        {
            var current = Environment.GetEnvironmentVariable("PATH", target) ?? string.Empty;
            if (PathVariableContains(current, directoryPath))
            {
                return;
            }

            var updated = AppendPathSegment(current, directoryPath);
            Environment.SetEnvironmentVariable("PATH", updated, target);
        }
        catch
        {
            // Non-fatal; process-level PATH is still updated.
        }
    }

    private static bool PathVariableContains(string pathValue, string candidate)
    {
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return false;
        }

        var normalizedCandidate = NormalizePathSegment(candidate);
        var parts = pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (string.Equals(
                    NormalizePathSegment(part),
                    normalizedCandidate,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string AppendPathSegment(string existingPath, string segment)
    {
        if (string.IsNullOrWhiteSpace(existingPath))
        {
            return segment;
        }

        if (existingPath.EndsWith(';'))
        {
            return existingPath + segment;
        }

        return existingPath + ";" + segment;
    }

    private static string NormalizePathSegment(string path)
    {
        return path.Trim().TrimEnd('\\');
    }

    private static string GetWindowsServiceStatus(string serviceName)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            return service.Status switch
            {
                ServiceControllerStatus.Running => "Running",
                ServiceControllerStatus.Stopped => "Stopped",
                ServiceControllerStatus.Paused => "Paused",
                ServiceControllerStatus.StartPending => "StartPending",
                ServiceControllerStatus.StopPending => "StopPending",
                _ => service.Status.ToString()
            };
        }
        catch
        {
            return "Not Installed";
        }
    }
}

internal sealed record ServiceState(
    int Port,
    bool IsWebReachable,
    int FrontendPort,
    int ActiveFrontendPort,
    bool IsFrontendReachable,
    string MainDbPath,
    string ProductionDbPath,
    bool MainDbExists,
    bool ProductionDbExists,
    string DatabaseEngine,
    bool IsPostgreSqlConfigured,
    string PostgresStatus,
    bool ApiWinSvcInstalled = false,
    string ApiWinSvcStatus = "Not Installed");

internal sealed record RelatedServiceStatus(
    string Name,
    string Category,
    string Status,
    string Details);
