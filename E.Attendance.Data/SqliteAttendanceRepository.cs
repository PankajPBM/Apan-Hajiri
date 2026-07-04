using System.Data;
using System.Data.Common;
using System.Threading;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Npgsql;
using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;

namespace E.Attendance.Data;

public sealed class SqliteAttendanceRepository : IAttendanceRepository
{
    private const string PostgresAppSchema = "E_Attendace";
    private static readonly string[] PostgresIdentifiers =
    [
        "Employees", "Devices", "AttendanceRecords", "DeviceCommands", "SyncLogs", "DeviceTableStamps",
        "Areas", "Departments", "EmployeeDepartments", "SyncQueue",
        "Id", "EmployeeCode", "FullName", "Area", "Department", "DepartmentId", "AreaId", "DepartmentsJson", "Position", "CardNumber", "Password", "MobileNumber", "Gender",
        "Privilege", "FaceTemplateVisible", "FaceTemplateInfrared", "ProfilePhoto", "FingerprintTemplates",
        "CreatedAt", "SyncedAt", "UpdatedAt", "IsActive", "IsDefault", "IsRegistered", "SerialNumber", "Name", "Model", "IpAddress", "Location",
        "TimeZone", "SupportsVisibleLightFace", "SupportsInfraredFace", "SupportsFingerprint",
        "FaceAlgorithmVersion", "FingerprintAlgorithmVersion", "FirmwareVersion", "DeviceType", "PushVer", "Language",
        "MultiBioDataSupport", "MultiBioPhotoSupport", "MultiBioVersion", "MaxMultiBioDataCount", "MaxMultiBioPhotoCount",
        "FingerFunOn", "FaceFunOn", "VisiLightFunOn", "BioDataFun", "BioPhotoFun", "SupportsPalm", "SupportsFingerVein",
        "CapabilitiesJson", "CreatedAtUtc", "UpdatedAtUtc",
        "Status", "IsRegistrationDevice", "IsOnline", "LastSeenUtc",
        "EmployeeId", "Timestamp", "AttendanceDate", "StatusCode", "VerifyMode", "Workcode", "DeviceId", "DeviceSerialNumber",
        "TableName", "StampValue", "UpdatedUtc",
        "CommandText", "CreatedUtc", "SentUtc", "ReplyText", "Type", "ResponseText", "CompletedUtc",
        "AssignedAt", "AssignedBy", "EntityType", "EntityId", "Operation", "Payload", "ErrorMessage",
        "RetryCount", "MaxRetries", "ProcessedAt", "NextRetryAt"
    ];

    private readonly string _connectionString;
    private readonly bool _usePostgreSql;
    private static readonly AsyncLocal<DbConnection?> AmbientConnection = new();
    private static readonly AsyncLocal<DbTransaction?> AmbientTransaction = new();
    private static readonly AsyncLocal<int> AmbientDepth = new();

    public SqliteAttendanceRepository(string databasePathOrConnectionString, bool usePostgreSql = false)
    {
        _usePostgreSql = usePostgreSql || LooksLikePostgresConnectionString(databasePathOrConnectionString);

        if (_usePostgreSql)
        {
            var builder = new NpgsqlConnectionStringBuilder(databasePathOrConnectionString)
            {
                SearchPath = PostgresAppSchema
            };
            _connectionString = builder.ConnectionString;
        }
        else
        {
            var directory = Path.GetDirectoryName(databasePathOrConnectionString);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePathOrConnectionString
            }.ToString();
        }

        InitializeSchema();
    }

    public void RunInTransaction(Action action)
    {
        if (action is null)
        {
            return;
        }

        if (AmbientDepth.Value > 0)
        {
            AmbientDepth.Value++;
            try
            {
                action();
                return;
            }
            finally
            {
                AmbientDepth.Value--;
            }
        }

        using var connection = CreateConnection();
        OpenConnection(connection);
        using var transaction = connection.BeginTransaction();
        AmbientConnection.Value = connection;
        AmbientTransaction.Value = transaction;
        AmbientDepth.Value = 1;
        try
        {
            action();
            transaction.Commit();
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch
            {
            }
            throw;
        }
        finally
        {
            AmbientDepth.Value = 0;
            AmbientTransaction.Value = null;
            AmbientConnection.Value = null;
        }
    }

    public T RunInTransaction<T>(Func<T> action)
    {
        if (action is null)
        {
            return default!;
        }

        T result = default!;
        RunInTransaction(() => { result = action(); });
        return result!;
    }

    public IReadOnlyList<Employee> GetEmployees()
    {
        const string sql = """
            SELECT Id, EmployeeCode, FullName, Area, Department, DepartmentId, DepartmentsJson, Position, CardNumber, Password, MobileNumber, Gender, Privilege, VerifyMode, FaceTemplateVisible, FaceTemplateInfrared, ProfilePhoto, FingerprintTemplates, CreatedAt, SyncedAt, IsActive, AreaId
            FROM Employees
            ORDER BY EmployeeCode
            """;

        return Query(sql, reader => new Employee
        {
            Id = ReadGuid(reader, 0),
            EmployeeCode = reader.GetString(1),
            FullName = reader.GetString(2),
            Area = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            Department = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            DepartmentId = reader.IsDBNull(5) ? 1 : ReadInt32(reader, 5),
            Departments = DeserializeDepartments(reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(4) ? string.Empty : reader.GetString(4)),
            Position = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            CardNumber = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            Password = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            MobileNumber = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            Gender = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            Privilege = reader.IsDBNull(12) ? UserPrivilege.User : (UserPrivilege)ReadInt32(reader, 12),
            VerifyMode = reader.IsDBNull(13) ? 0 : ReadInt32(reader, 13),
            FaceTemplateVisible = reader.IsDBNull(14) ? null : reader.GetString(14),
            FaceTemplateInfrared = reader.IsDBNull(15) ? null : reader.GetString(15),
            ProfilePhoto = reader.IsDBNull(16) ? null : reader.GetString(16),
            FingerprintTemplates = reader.IsDBNull(17) ? null : reader.GetString(17),
            CreatedAt = reader.IsDBNull(18) ? DateTime.UtcNow : ReadDateTime(reader, 18),
            SyncedAt = reader.IsDBNull(19) ? null : ReadDateTime(reader, 19),
            IsActive = ReadInt32(reader, 20) == 1,
            AreaId = reader.IsDBNull(21) ? 1 : ReadInt32(reader, 21)
        });
    }

    public Employee AddEmployee(Employee employee)
    {
        var sql = $"""
            INSERT INTO Employees (Id, EmployeeCode, FullName, Area, Department, DepartmentId, DepartmentsJson, Position, CardNumber, Password, MobileNumber, Gender, Privilege, VerifyMode, FaceTemplateVisible, FaceTemplateInfrared, ProfilePhoto, FingerprintTemplates, CreatedAt, SyncedAt, IsActive, AreaId)
            VALUES ({SqlUuid("$id")}, $code, $name, $area, $dept, $deptId, $departmentsJson, $position, $card, $password, $mobile, $gender, $privilege, $verifyMode, $faceVisible, $faceInfra, $photo, $fingerprints, {SqlTs("$createdAt")}, {SqlTs("$syncedAt")}, $active, $areaId)
            """;

        Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$id", GuidParam(employee.Id));
            command.Parameters.AddWithValue("$code", employee.EmployeeCode);
            command.Parameters.AddWithValue("$name", employee.FullName);
            command.Parameters.AddWithValue("$area", employee.Area);
            command.Parameters.AddWithValue("$dept", employee.Department);
            command.Parameters.AddWithValue("$deptId", employee.DepartmentId > 0 ? employee.DepartmentId : (object)DBNull.Value);
            command.Parameters.AddWithValue("$departmentsJson", SerializeDepartments(employee.Departments));
            command.Parameters.AddWithValue("$position", employee.Position);
            command.Parameters.AddWithValue("$card", employee.CardNumber);
            command.Parameters.AddWithValue("$password", employee.Password);
            command.Parameters.AddWithValue("$mobile", employee.MobileNumber);
            command.Parameters.AddWithValue("$gender", employee.Gender);
            command.Parameters.AddWithValue("$privilege", (int)employee.Privilege);
            command.Parameters.AddWithValue("$verifyMode", employee.VerifyMode);
            command.Parameters.AddWithValue("$faceVisible", employee.FaceTemplateVisible ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$faceInfra", employee.FaceTemplateInfrared ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$photo", employee.ProfilePhoto ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$fingerprints", employee.FingerprintTemplates ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", employee.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$syncedAt", employee.SyncedAt.HasValue ? employee.SyncedAt.Value.ToString("O") : (object)DBNull.Value);
            command.Parameters.AddWithValue("$active", employee.IsActive ? 1 : 0);
            command.Parameters.AddWithValue("$areaId", employee.AreaId > 0 ? employee.AreaId : 1);
        });

        return employee;
    }

    public bool UpdateEmployee(Employee employee)
    {
        var sql = $"""
            UPDATE Employees
            SET EmployeeCode = $code,
                FullName = $name,
                Area = $area,
                Department = $dept,
                DepartmentId = $deptId,
                AreaId = $areaId,
                DepartmentsJson = $departmentsJson,
                Position = $position,
                CardNumber = $card,
                Password = $password,
                MobileNumber = $mobile,
                Gender = $gender,
                Privilege = $privilege,
                VerifyMode = $verifyMode,
                FaceTemplateVisible = $faceVisible,
                FaceTemplateInfrared = $faceInfra,
                ProfilePhoto = $photo,
                FingerprintTemplates = $fingerprints,
                SyncedAt = {SqlTs("$syncedAt")},
                IsActive = $active
            WHERE Id = {SqlUuid("$id")}
            """;

        var rows = Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$id", GuidParam(employee.Id));
            command.Parameters.AddWithValue("$code", employee.EmployeeCode);
            command.Parameters.AddWithValue("$name", employee.FullName);
            command.Parameters.AddWithValue("$area", employee.Area);
            command.Parameters.AddWithValue("$dept", employee.Department);
            command.Parameters.AddWithValue("$deptId", employee.DepartmentId > 0 ? employee.DepartmentId : (object)DBNull.Value);
            command.Parameters.AddWithValue("$areaId", employee.AreaId > 0 ? employee.AreaId : 1);
            command.Parameters.AddWithValue("$departmentsJson", SerializeDepartments(employee.Departments));
            command.Parameters.AddWithValue("$position", employee.Position);
            command.Parameters.AddWithValue("$card", employee.CardNumber);
            command.Parameters.AddWithValue("$password", employee.Password);
            command.Parameters.AddWithValue("$mobile", employee.MobileNumber);
            command.Parameters.AddWithValue("$gender", employee.Gender);
            command.Parameters.AddWithValue("$privilege", (int)employee.Privilege);
            command.Parameters.AddWithValue("$verifyMode", employee.VerifyMode);
            command.Parameters.AddWithValue("$faceVisible", employee.FaceTemplateVisible ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$faceInfra", employee.FaceTemplateInfrared ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$photo", employee.ProfilePhoto ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$fingerprints", employee.FingerprintTemplates ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$syncedAt", employee.SyncedAt.HasValue ? employee.SyncedAt.Value.ToString("O") : (object)DBNull.Value);
            command.Parameters.AddWithValue("$active", employee.IsActive ? 1 : 0);
        });

        return rows > 0;
    }

    public bool DeleteEmployee(Guid employeeId)
    {
        var sql = $"DELETE FROM Employees WHERE Id = {SqlUuid("$id")}";
        var rows = Execute(sql, command => command.Parameters.AddWithValue("$id", GuidParam(employeeId)));
        return rows > 0;
    }

    public Employee? FindEmployeeByCode(string employeeCode)
    {
        const string sql = """
            SELECT Id, EmployeeCode, FullName, Area, Department, DepartmentId, DepartmentsJson, Position, CardNumber, Password, MobileNumber, Gender, Privilege, VerifyMode, FaceTemplateVisible, FaceTemplateInfrared, ProfilePhoto, FingerprintTemplates, CreatedAt, SyncedAt, IsActive, AreaId
            FROM Employees
            WHERE EmployeeCode = $code
            LIMIT 1
            """;

        return QuerySingle(sql, command =>
        {
            command.Parameters.AddWithValue("$code", employeeCode);
        }, reader => new Employee
        {
            Id = ReadGuid(reader, 0),
            EmployeeCode = reader.GetString(1),
            FullName = reader.GetString(2),
            Area = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            Department = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            DepartmentId = reader.IsDBNull(5) ? 1 : ReadInt32(reader, 5),
            Departments = DeserializeDepartments(reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(4) ? string.Empty : reader.GetString(4)),
            Position = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            CardNumber = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            Password = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            MobileNumber = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            Gender = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            Privilege = reader.IsDBNull(12) ? UserPrivilege.User : (UserPrivilege)ReadInt32(reader, 12),
            VerifyMode = reader.IsDBNull(13) ? 0 : ReadInt32(reader, 13),
            FaceTemplateVisible = reader.IsDBNull(14) ? null : reader.GetString(14),
            FaceTemplateInfrared = reader.IsDBNull(15) ? null : reader.GetString(15),
            ProfilePhoto = reader.IsDBNull(16) ? null : reader.GetString(16),
            FingerprintTemplates = reader.IsDBNull(17) ? null : reader.GetString(17),
            CreatedAt = reader.IsDBNull(18) ? DateTime.UtcNow : ReadDateTime(reader, 18),
            SyncedAt = reader.IsDBNull(19) ? null : ReadDateTime(reader, 19),
            IsActive = ReadInt32(reader, 20) == 1,
            AreaId = reader.IsDBNull(21) ? 1 : ReadInt32(reader, 21)
        });
    }

    public IReadOnlyList<Device> GetDevices()
    {
        const string sql = """
            SELECT Id, SerialNumber, Name, Model, IpAddress, Location, TimeZone, SupportsVisibleLightFace, SupportsInfraredFace, SupportsFingerprint, FaceAlgorithmVersion, FingerprintAlgorithmVersion,
                   FirmwareVersion, DeviceType, PushVer, Language, MultiBioDataSupport, MultiBioPhotoSupport, MultiBioVersion, MaxMultiBioDataCount, MaxMultiBioPhotoCount,
                   FingerFunOn, FaceFunOn, VisiLightFunOn, BioDataFun, BioPhotoFun, SupportsPalm, SupportsFingerVein, CapabilitiesJson, CreatedAtUtc, UpdatedAtUtc, Status, IsRegistrationDevice, IsOnline, LastSeenUtc, AreaId, DepartmentId, IsRegistered
            FROM Devices
            ORDER BY SerialNumber
            """;

        return Query(sql, reader => new Device
        {
            Id = ReadGuid(reader, 0),
            SerialNumber = reader.GetString(1),
            Name = reader.GetString(2),
            Model = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            IpAddress = reader.GetString(4),
            Location = reader.GetString(5),
            TimeZone = reader.IsDBNull(6) ? "+5:45" : reader.GetString(6),
            SupportsVisibleLightFace = !reader.IsDBNull(7) && ReadInt32(reader, 7) == 1,
            SupportsInfraredFace = !reader.IsDBNull(8) && ReadInt32(reader, 8) == 1,
            SupportsFingerprint = !reader.IsDBNull(9) && ReadInt32(reader, 9) == 1,
            FaceAlgorithmVersion = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            FingerprintAlgorithmVersion = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            FirmwareVersion = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            DeviceType = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
            PushVer = reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
            Language = reader.IsDBNull(15) ? 0 : ReadInt32(reader, 15),
            MultiBioDataSupport = reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
            MultiBioPhotoSupport = reader.IsDBNull(17) ? string.Empty : reader.GetString(17),
            MultiBioVersion = reader.IsDBNull(18) ? string.Empty : reader.GetString(18),
            MaxMultiBioDataCount = reader.IsDBNull(19) ? string.Empty : reader.GetString(19),
            MaxMultiBioPhotoCount = reader.IsDBNull(20) ? string.Empty : reader.GetString(20),
            FingerFunOn = !reader.IsDBNull(21) && ReadInt32(reader, 21) == 1,
            FaceFunOn = !reader.IsDBNull(22) && ReadInt32(reader, 22) == 1,
            VisiLightFunOn = !reader.IsDBNull(23) && ReadInt32(reader, 23) == 1,
            BioDataFun = !reader.IsDBNull(24) && ReadInt32(reader, 24) == 1,
            BioPhotoFun = !reader.IsDBNull(25) && ReadInt32(reader, 25) == 1,
            SupportsPalm = !reader.IsDBNull(26) && ReadInt32(reader, 26) == 1,
            SupportsFingerVein = !reader.IsDBNull(27) && ReadInt32(reader, 27) == 1,
            CapabilitiesJson = reader.IsDBNull(28) ? "{}" : reader.GetString(28),
            CreatedAtUtc = reader.IsDBNull(29) ? DateTime.UtcNow : ReadDateTime(reader, 29),
            UpdatedAtUtc = reader.IsDBNull(30) ? DateTime.UtcNow : ReadDateTime(reader, 30),
            Status = reader.IsDBNull(31) ? DeviceStatus.Offline : (DeviceStatus)ReadInt32(reader, 31),
            IsRegistrationDevice = !reader.IsDBNull(32) && ReadInt32(reader, 32) == 1,
            IsOnline = ReadInt32(reader, 33) == 1,
            LastSeenUtc = reader.IsDBNull(34) ? null : ReadDateTime(reader, 34),
            AreaId = reader.IsDBNull(35) ? 1 : ReadInt32(reader, 35),
            DepartmentId = reader.IsDBNull(36) ? 1 : ReadInt32(reader, 36),
            IsRegistered = !reader.IsDBNull(37) && ReadInt32(reader, 37) == 1
        });
    }

    public Device AddDevice(Device device)
    {
        // All boolean cols passed as INTEGER 0/1 — works for both INTEGER and BOOLEAN column types
        // EnsurePostgresSchema normalises all these to INTEGER on startup
        var sql = $"""
            INSERT INTO Devices (Id, SerialNumber, Name, Model, IpAddress, Location, TimeZone, SupportsVisibleLightFace, SupportsInfraredFace, SupportsFingerprint, FaceAlgorithmVersion, FingerprintAlgorithmVersion,
                                 FirmwareVersion, DeviceType, PushVer, Language, MultiBioDataSupport, MultiBioPhotoSupport, MultiBioVersion, MaxMultiBioDataCount, MaxMultiBioPhotoCount,
                                 FingerFunOn, FaceFunOn, VisiLightFunOn, BioDataFun, BioPhotoFun, SupportsPalm, SupportsFingerVein, CapabilitiesJson, CreatedAtUtc, UpdatedAtUtc, Status, IsRegistrationDevice, IsOnline, LastSeenUtc, AreaId, DepartmentId, IsRegistered)
            VALUES ({SqlUuid("$id")}, $sn, $name, $model, $ip, $loc, $timezone, $visible, $infra, $fp, $faceAlgo, $fpAlgo,
                    $firmware, $deviceType, $pushVer, $language, $multiBioDataSupport, $multiBioPhotoSupport, $multiBioVersion, $maxMultiBioDataCount, $maxMultiBioPhotoCount,
                    $fingerFunOn, $faceFunOn, $visiLightFunOn, $bioDataFun, $bioPhotoFun, $supportsPalm, $supportsFingerVein,
                    {(_usePostgreSql ? "CAST($capabilitiesJson AS jsonb)" : "$capabilitiesJson")},
                    {SqlTs("$createdAtUtc")}, {SqlTs("$updatedAtUtc")}, $status, $isReg, $online, {SqlTs("$lastSeen")}, $areaId, $deptId, $isRegistered)
            """;

        Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$id", GuidParam(device.Id));
            command.Parameters.AddWithValue("$sn", device.SerialNumber);
            command.Parameters.AddWithValue("$name", device.Name);
            command.Parameters.AddWithValue("$model", device.Model ?? string.Empty);
            command.Parameters.AddWithValue("$ip", device.IpAddress);
            command.Parameters.AddWithValue("$loc", device.Location);
            command.Parameters.AddWithValue("$timezone", string.IsNullOrWhiteSpace(device.TimeZone) ? "+5:45" : device.TimeZone);
            command.Parameters.AddWithValue("$visible", device.SupportsVisibleLightFace ? 1 : 0);
            command.Parameters.AddWithValue("$infra", device.SupportsInfraredFace ? 1 : 0);
            command.Parameters.AddWithValue("$fp", device.SupportsFingerprint ? 1 : 0);
            command.Parameters.AddWithValue("$faceAlgo", device.FaceAlgorithmVersion ?? string.Empty);
            command.Parameters.AddWithValue("$fpAlgo", device.FingerprintAlgorithmVersion ?? string.Empty);
            command.Parameters.AddWithValue("$firmware", device.FirmwareVersion ?? string.Empty);
            command.Parameters.AddWithValue("$deviceType", device.DeviceType ?? string.Empty);
            command.Parameters.AddWithValue("$pushVer", device.PushVer ?? string.Empty);
            command.Parameters.AddWithValue("$language", device.Language);
            command.Parameters.AddWithValue("$multiBioDataSupport", device.MultiBioDataSupport ?? string.Empty);
            command.Parameters.AddWithValue("$multiBioPhotoSupport", device.MultiBioPhotoSupport ?? string.Empty);
            command.Parameters.AddWithValue("$multiBioVersion", device.MultiBioVersion ?? string.Empty);
            command.Parameters.AddWithValue("$maxMultiBioDataCount", device.MaxMultiBioDataCount ?? string.Empty);
            command.Parameters.AddWithValue("$maxMultiBioPhotoCount", device.MaxMultiBioPhotoCount ?? string.Empty);
            command.Parameters.AddWithValue("$fingerFunOn", device.FingerFunOn ? 1 : 0);
            command.Parameters.AddWithValue("$faceFunOn", device.FaceFunOn ? 1 : 0);
            command.Parameters.AddWithValue("$visiLightFunOn", device.VisiLightFunOn ? 1 : 0);
            command.Parameters.AddWithValue("$bioDataFun", device.BioDataFun ? 1 : 0);
            command.Parameters.AddWithValue("$bioPhotoFun", device.BioPhotoFun ? 1 : 0);
            command.Parameters.AddWithValue("$supportsPalm", device.SupportsPalm ? 1 : 0);
            command.Parameters.AddWithValue("$supportsFingerVein", device.SupportsFingerVein ? 1 : 0);
            command.Parameters.AddWithValue("$capabilitiesJson", string.IsNullOrWhiteSpace(device.CapabilitiesJson) ? "{}" : device.CapabilitiesJson);
            // Pass timestamps as ISO strings — SQL CAST($x AS timestamptz) handles conversion
            command.Parameters.AddWithValue("$createdAtUtc", (device.CreatedAtUtc == default ? DateTime.UtcNow : device.CreatedAtUtc).ToString("O"));
            command.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$status", (int)device.Status);
            command.Parameters.AddWithValue("$isReg", device.IsRegistrationDevice ? 1 : 0);
            command.Parameters.AddWithValue("$online", device.IsOnline ? 1 : 0);
            command.Parameters.AddWithValue("$lastSeen", device.LastSeenUtc.HasValue ? device.LastSeenUtc.Value.ToString("O") : (object)DBNull.Value);
            command.Parameters.AddWithValue("$areaId", device.AreaId > 0 ? device.AreaId : 1);
            command.Parameters.AddWithValue("$deptId", device.DepartmentId > 0 ? device.DepartmentId : 1);
            command.Parameters.AddWithValue("$isRegistered", device.IsRegistered ? 1 : 0);
        });

        return device;
    }

    public Device? FindDeviceById(Guid deviceId)
    {
        var sql = $"""
            SELECT Id, SerialNumber, Name, Model, IpAddress, Location, TimeZone, SupportsVisibleLightFace, SupportsInfraredFace, SupportsFingerprint, FaceAlgorithmVersion, FingerprintAlgorithmVersion,
                   FirmwareVersion, DeviceType, PushVer, Language, MultiBioDataSupport, MultiBioPhotoSupport, MultiBioVersion, MaxMultiBioDataCount, MaxMultiBioPhotoCount,
                   FingerFunOn, FaceFunOn, VisiLightFunOn, BioDataFun, BioPhotoFun, SupportsPalm, SupportsFingerVein, CapabilitiesJson, CreatedAtUtc, UpdatedAtUtc, Status, IsRegistrationDevice, IsOnline, LastSeenUtc, AreaId, DepartmentId, IsRegistered
            FROM Devices
            WHERE Id = {SqlUuid("$id")}
            LIMIT 1
            """;

        return QuerySingle(sql, command =>
        {
            command.Parameters.AddWithValue("$id", GuidParam(deviceId));
        }, reader => new Device
        {
            Id = ReadGuid(reader, 0),
            SerialNumber = reader.GetString(1),
            Name = reader.GetString(2),
            Model = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            IpAddress = reader.GetString(4),
            Location = reader.GetString(5),
            TimeZone = reader.IsDBNull(6) ? "+5:45" : reader.GetString(6),
            SupportsVisibleLightFace = !reader.IsDBNull(7) && ReadInt32(reader, 7) == 1,
            SupportsInfraredFace = !reader.IsDBNull(8) && ReadInt32(reader, 8) == 1,
            SupportsFingerprint = !reader.IsDBNull(9) && ReadInt32(reader, 9) == 1,
            FaceAlgorithmVersion = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            FingerprintAlgorithmVersion = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            FirmwareVersion = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            DeviceType = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
            PushVer = reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
            Language = reader.IsDBNull(15) ? 0 : ReadInt32(reader, 15),
            MultiBioDataSupport = reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
            MultiBioPhotoSupport = reader.IsDBNull(17) ? string.Empty : reader.GetString(17),
            MultiBioVersion = reader.IsDBNull(18) ? string.Empty : reader.GetString(18),
            MaxMultiBioDataCount = reader.IsDBNull(19) ? string.Empty : reader.GetString(19),
            MaxMultiBioPhotoCount = reader.IsDBNull(20) ? string.Empty : reader.GetString(20),
            FingerFunOn = !reader.IsDBNull(21) && ReadInt32(reader, 21) == 1,
            FaceFunOn = !reader.IsDBNull(22) && ReadInt32(reader, 22) == 1,
            VisiLightFunOn = !reader.IsDBNull(23) && ReadInt32(reader, 23) == 1,
            BioDataFun = !reader.IsDBNull(24) && ReadInt32(reader, 24) == 1,
            BioPhotoFun = !reader.IsDBNull(25) && ReadInt32(reader, 25) == 1,
            SupportsPalm = !reader.IsDBNull(26) && ReadInt32(reader, 26) == 1,
            SupportsFingerVein = !reader.IsDBNull(27) && ReadInt32(reader, 27) == 1,
            CapabilitiesJson = reader.IsDBNull(28) ? "{}" : reader.GetString(28),
            CreatedAtUtc = reader.IsDBNull(29) ? DateTime.UtcNow : ReadDateTime(reader, 29),
            UpdatedAtUtc = reader.IsDBNull(30) ? DateTime.UtcNow : ReadDateTime(reader, 30),
            Status = reader.IsDBNull(31) ? DeviceStatus.Offline : (DeviceStatus)ReadInt32(reader, 31),
            IsRegistrationDevice = !reader.IsDBNull(32) && ReadInt32(reader, 32) == 1,
            IsOnline = ReadInt32(reader, 33) == 1,
            LastSeenUtc = reader.IsDBNull(34) ? null : ReadDateTime(reader, 34),
            AreaId = reader.IsDBNull(35) ? 1 : ReadInt32(reader, 35),
            DepartmentId = reader.IsDBNull(36) ? 1 : ReadInt32(reader, 36),
            IsRegistered = !reader.IsDBNull(37) && ReadInt32(reader, 37) == 1
        });
    }

    public Device? FindDeviceBySerial(string serialNumber)
    {
        const string sql = """
            SELECT Id, SerialNumber, Name, Model, IpAddress, Location, TimeZone, SupportsVisibleLightFace, SupportsInfraredFace, SupportsFingerprint, FaceAlgorithmVersion, FingerprintAlgorithmVersion,
                   FirmwareVersion, DeviceType, PushVer, Language, MultiBioDataSupport, MultiBioPhotoSupport, MultiBioVersion, MaxMultiBioDataCount, MaxMultiBioPhotoCount,
                   FingerFunOn, FaceFunOn, VisiLightFunOn, BioDataFun, BioPhotoFun, SupportsPalm, SupportsFingerVein, CapabilitiesJson, CreatedAtUtc, UpdatedAtUtc, Status, IsRegistrationDevice, IsOnline, LastSeenUtc, AreaId, DepartmentId, IsRegistered
            FROM Devices
            WHERE LOWER(SerialNumber) = LOWER($sn)
            LIMIT 1
            """;

        return QuerySingle(sql, command =>
        {
            command.Parameters.AddWithValue("$sn", serialNumber);
        }, reader => new Device
        {
            Id = ReadGuid(reader, 0),
            SerialNumber = reader.GetString(1),
            Name = reader.GetString(2),
            Model = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            IpAddress = reader.GetString(4),
            Location = reader.GetString(5),
            TimeZone = reader.IsDBNull(6) ? "+5:45" : reader.GetString(6),
            SupportsVisibleLightFace = !reader.IsDBNull(7) && ReadInt32(reader, 7) == 1,
            SupportsInfraredFace = !reader.IsDBNull(8) && ReadInt32(reader, 8) == 1,
            SupportsFingerprint = !reader.IsDBNull(9) && ReadInt32(reader, 9) == 1,
            FaceAlgorithmVersion = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            FingerprintAlgorithmVersion = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            FirmwareVersion = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            DeviceType = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
            PushVer = reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
            Language = reader.IsDBNull(15) ? 0 : ReadInt32(reader, 15),
            MultiBioDataSupport = reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
            MultiBioPhotoSupport = reader.IsDBNull(17) ? string.Empty : reader.GetString(17),
            MultiBioVersion = reader.IsDBNull(18) ? string.Empty : reader.GetString(18),
            MaxMultiBioDataCount = reader.IsDBNull(19) ? string.Empty : reader.GetString(19),
            MaxMultiBioPhotoCount = reader.IsDBNull(20) ? string.Empty : reader.GetString(20),
            FingerFunOn = !reader.IsDBNull(21) && ReadInt32(reader, 21) == 1,
            FaceFunOn = !reader.IsDBNull(22) && ReadInt32(reader, 22) == 1,
            VisiLightFunOn = !reader.IsDBNull(23) && ReadInt32(reader, 23) == 1,
            BioDataFun = !reader.IsDBNull(24) && ReadInt32(reader, 24) == 1,
            BioPhotoFun = !reader.IsDBNull(25) && ReadInt32(reader, 25) == 1,
            SupportsPalm = !reader.IsDBNull(26) && ReadInt32(reader, 26) == 1,
            SupportsFingerVein = !reader.IsDBNull(27) && ReadInt32(reader, 27) == 1,
            CapabilitiesJson = reader.IsDBNull(28) ? "{}" : reader.GetString(28),
            CreatedAtUtc = reader.IsDBNull(29) ? DateTime.UtcNow : ReadDateTime(reader, 29),
            UpdatedAtUtc = reader.IsDBNull(30) ? DateTime.UtcNow : ReadDateTime(reader, 30),
            Status = reader.IsDBNull(31) ? DeviceStatus.Offline : (DeviceStatus)ReadInt32(reader, 31),
            IsRegistrationDevice = !reader.IsDBNull(32) && ReadInt32(reader, 32) == 1,
            IsOnline = ReadInt32(reader, 33) == 1,
            LastSeenUtc = reader.IsDBNull(34) ? null : ReadDateTime(reader, 34),
            AreaId = reader.IsDBNull(35) ? 1 : ReadInt32(reader, 35),
            DepartmentId = reader.IsDBNull(36) ? 1 : ReadInt32(reader, 36),
            IsRegistered = !reader.IsDBNull(37) && ReadInt32(reader, 37) == 1
        });
    }

    public bool UpdateDevice(Device device)
    {
        var capabilitiesValueToken = _usePostgreSql ? "CAST($capabilitiesJson AS jsonb)" : "$capabilitiesJson";
        var sql = $"""
            UPDATE Devices
            SET SerialNumber = $sn,
                Name = $name,
                Model = $model,
                IpAddress = $ip,
                Location = $loc,
                TimeZone = $timezone,
                SupportsVisibleLightFace = $visible,
                SupportsInfraredFace = $infra,
                SupportsFingerprint = $fp,
                FaceAlgorithmVersion = $faceAlgo,
                FingerprintAlgorithmVersion = $fpAlgo,
                FirmwareVersion = $firmware,
                DeviceType = $deviceType,
                PushVer = $pushVer,
                Language = $language,
                MultiBioDataSupport = $multiBioDataSupport,
                MultiBioPhotoSupport = $multiBioPhotoSupport,
                MultiBioVersion = $multiBioVersion,
                MaxMultiBioDataCount = $maxMultiBioDataCount,
                MaxMultiBioPhotoCount = $maxMultiBioPhotoCount,
                FingerFunOn = $fingerFunOn,
                FaceFunOn = $faceFunOn,
                VisiLightFunOn = $visiLightFunOn,
                BioDataFun = $bioDataFun,
                BioPhotoFun = $bioPhotoFun,
                SupportsPalm = $supportsPalm,
                SupportsFingerVein = $supportsFingerVein,
                CapabilitiesJson = {capabilitiesValueToken},
                UpdatedAtUtc = {SqlTs("$updatedAtUtc")},
                Status = $status,
                IsRegistrationDevice = $isReg,
                IsOnline = $online,
                LastSeenUtc = {SqlTs("$lastSeen")},
                AreaId = $areaId,
                DepartmentId = $deptId,
                IsRegistered = $isRegistered
            WHERE Id = {SqlUuid("$id")}
            """;

        var rows = Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$id", GuidParam(device.Id));
            command.Parameters.AddWithValue("$sn", device.SerialNumber);
            command.Parameters.AddWithValue("$name", device.Name);
            command.Parameters.AddWithValue("$model", device.Model ?? string.Empty);
            command.Parameters.AddWithValue("$ip", device.IpAddress);
            command.Parameters.AddWithValue("$loc", device.Location);
            command.Parameters.AddWithValue("$timezone", string.IsNullOrWhiteSpace(device.TimeZone) ? "+5:45" : device.TimeZone);
            command.Parameters.AddWithValue("$visible", device.SupportsVisibleLightFace ? 1 : 0);
            command.Parameters.AddWithValue("$infra", device.SupportsInfraredFace ? 1 : 0);
            command.Parameters.AddWithValue("$fp", device.SupportsFingerprint ? 1 : 0);
            command.Parameters.AddWithValue("$faceAlgo", device.FaceAlgorithmVersion ?? string.Empty);
            command.Parameters.AddWithValue("$fpAlgo", device.FingerprintAlgorithmVersion ?? string.Empty);
            command.Parameters.AddWithValue("$firmware", device.FirmwareVersion ?? string.Empty);
            command.Parameters.AddWithValue("$deviceType", device.DeviceType ?? string.Empty);
            command.Parameters.AddWithValue("$pushVer", device.PushVer ?? string.Empty);
            command.Parameters.AddWithValue("$language", device.Language);
            command.Parameters.AddWithValue("$multiBioDataSupport", device.MultiBioDataSupport ?? string.Empty);
            command.Parameters.AddWithValue("$multiBioPhotoSupport", device.MultiBioPhotoSupport ?? string.Empty);
            command.Parameters.AddWithValue("$multiBioVersion", device.MultiBioVersion ?? string.Empty);
            command.Parameters.AddWithValue("$maxMultiBioDataCount", device.MaxMultiBioDataCount ?? string.Empty);
            command.Parameters.AddWithValue("$maxMultiBioPhotoCount", device.MaxMultiBioPhotoCount ?? string.Empty);
            command.Parameters.AddWithValue("$fingerFunOn", device.FingerFunOn ? 1 : 0);
            command.Parameters.AddWithValue("$faceFunOn", device.FaceFunOn ? 1 : 0);
            command.Parameters.AddWithValue("$visiLightFunOn", device.VisiLightFunOn ? 1 : 0);
            command.Parameters.AddWithValue("$bioDataFun", device.BioDataFun ? 1 : 0);
            command.Parameters.AddWithValue("$bioPhotoFun", device.BioPhotoFun ? 1 : 0);
            command.Parameters.AddWithValue("$supportsPalm", device.SupportsPalm ? 1 : 0);
            command.Parameters.AddWithValue("$supportsFingerVein", device.SupportsFingerVein ? 1 : 0);
            command.Parameters.AddWithValue("$capabilitiesJson", string.IsNullOrWhiteSpace(device.CapabilitiesJson) ? "{}" : device.CapabilitiesJson);
            command.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$status", (int)device.Status);
            command.Parameters.AddWithValue("$isReg", device.IsRegistrationDevice ? 1 : 0);
            command.Parameters.AddWithValue("$online", device.IsOnline ? 1 : 0);
            command.Parameters.AddWithValue("$lastSeen", device.LastSeenUtc.HasValue ? device.LastSeenUtc.Value.ToString("O") : (object)DBNull.Value);
            command.Parameters.AddWithValue("$areaId", device.AreaId > 0 ? device.AreaId : 1);
            command.Parameters.AddWithValue("$deptId", device.DepartmentId > 0 ? device.DepartmentId : 1);
            command.Parameters.AddWithValue("$isRegistered", device.IsRegistered ? 1 : 0);
        });

        return rows > 0;
    }

    public bool DeleteDevice(Guid deviceId)
    {
        var sql = $"DELETE FROM Devices WHERE Id = {SqlUuid("$id")}";
        var rows = Execute(sql, command => command.Parameters.AddWithValue("$id", GuidParam(deviceId)));
        return rows > 0;
    }

    /// <summary>
    /// Inserts a minimal stub row into the users table for <paramref name="userPin"/> if one
    /// does not already exist. This satisfies the FK constraints on biometric_templates and
    /// biometric_photos (both reference users.pin ON DELETE CASCADE) when biometric data
    /// arrives from the device before a full USERINFO push has been processed.
    /// Uses INSERT … ON CONFLICT DO NOTHING so it never overwrites existing data.
    /// </summary>
    private void EnsureUserStubExists(string userPin)
    {
        if (string.IsNullOrWhiteSpace(userPin))
        {
            return;
        }

        var usersTable = _usePostgreSql ? "\"E_Attendace\".\"users\"" : "users";
        var now = DateParam(DateTime.UtcNow);
        var sql = $"""
            INSERT INTO {usersTable} (pin, name, created_at, updated_at)
            VALUES ($pin, $pin, $now, $now)
            ON CONFLICT (pin) DO NOTHING
            """;

        Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$pin", userPin.Trim());
            command.Parameters.AddWithValue("$now", now);
        });
    }

    public void UpsertUnifiedUser(
        string pin,
        string? name,
        int? privilege,
        string? password,

        string? primaryCard,
        string? secondaryCard,
        int? groupId,
        string? timezoneProfile,
        int? verifyMode,
        DateTime? validFrom,
        DateTime? validTo,
        bool? expiresEnabled,
        string? phone,
        int? gender,
        int? nation,
        string? idNumber)
    {
        var usersTable = _usePostgreSql ? "\"E_Attendace\".\"users\"" : "users";
        var sql = $"""
            INSERT INTO {usersTable}
            (pin, name, privilege, password, primary_card, secondary_card, group_id, timezone_profile, verify_mode, valid_from, valid_to, expires_enabled, phone, gender, nation, id_number, created_at, updated_at)
            VALUES ($pin, $name, $privilege, $password, $primaryCard, $secondaryCard, $groupId, $timezoneProfile, $verifyMode, $validFrom, $validTo, $expiresEnabled, $phone, $gender, $nation, $idNumber, $createdAt, $updatedAt)
            ON CONFLICT (pin) DO UPDATE SET
                name = excluded.name,
                privilege = excluded.privilege,
                password = excluded.password,
                primary_card = excluded.primary_card,
                secondary_card = excluded.secondary_card,
                group_id = excluded.group_id,
                timezone_profile = excluded.timezone_profile,
                verify_mode = excluded.verify_mode,
                valid_from = excluded.valid_from,
                valid_to = excluded.valid_to,
                expires_enabled = excluded.expires_enabled,
                phone = excluded.phone,
                gender = excluded.gender,
                nation = excluded.nation,
                id_number = excluded.id_number,
                updated_at = excluded.updated_at
            """;

        var nowDt = DateTime.UtcNow; var now = nowDt.ToString("O");
        Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$pin", pin);
            command.Parameters.AddWithValue("$name", name ?? string.Empty);
            command.Parameters.AddWithValue("$privilege", privilege.HasValue ? privilege.Value : DBNull.Value);
            command.Parameters.AddWithValue("$password", password ?? string.Empty);
            command.Parameters.AddWithValue("$primaryCard", primaryCard ?? string.Empty);
            command.Parameters.AddWithValue("$secondaryCard", secondaryCard ?? string.Empty);
            command.Parameters.AddWithValue("$groupId", groupId.HasValue ? groupId.Value : DBNull.Value);
            command.Parameters.AddWithValue("$timezoneProfile", timezoneProfile ?? string.Empty);
            command.Parameters.AddWithValue("$verifyMode", verifyMode.HasValue ? verifyMode.Value : DBNull.Value);
            command.Parameters.AddWithValue("$validFrom", DateParam(validFrom));
            command.Parameters.AddWithValue("$validTo", DateParam(validTo));
            command.Parameters.AddWithValue("$expiresEnabled", expiresEnabled.HasValue ? (expiresEnabled.Value ? 1 : 0) : DBNull.Value);
            command.Parameters.AddWithValue("$phone", phone ?? string.Empty);
            command.Parameters.AddWithValue("$gender", gender.HasValue ? gender.Value : DBNull.Value);
            command.Parameters.AddWithValue("$nation", nation.HasValue ? nation.Value : DBNull.Value);
            command.Parameters.AddWithValue("$idNumber", idNumber ?? string.Empty);
            command.Parameters.AddWithValue("$createdAt", DateParam(nowDt));
            command.Parameters.AddWithValue("$updatedAt", DateParam(nowDt));
        });
    }

    public bool DeleteUnifiedUser(string pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return false;
        }

        var usersTable = _usePostgreSql ? "\"E_Attendace\".\"users\"" : "users";
        var sql = $"DELETE FROM {usersTable} WHERE pin = $pin";
        var rows = Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$pin", pin.Trim());
        });

        return rows > 0;
    }

    public int DeleteEmployeeArtifactsByCode(string employeeCode)
    {
        if (string.IsNullOrWhiteSpace(employeeCode))
        {
            return 0;
        }

        var code = employeeCode.Trim();
        var usersTable = _usePostgreSql ? "\"E_Attendace\".\"users\"" : "users";
        var templatesTable = _usePostgreSql ? "\"E_Attendace\".\"biometric_templates\"" : "biometric_templates";
        var photosTable = _usePostgreSql ? "\"E_Attendace\".\"biometric_photos\"" : "biometric_photos";
        var attendanceTable = _usePostgreSql ? "\"E_Attendace\".\"AttendanceRecords\"" : "AttendanceRecords";
        var employeesTable = _usePostgreSql ? "\"E_Attendace\".\"Employees\"" : "Employees";

        var affected = 0;
        affected += Execute($"DELETE FROM {templatesTable} WHERE user_pin = $pin", command => command.Parameters.AddWithValue("$pin", code));
        affected += Execute($"DELETE FROM {photosTable} WHERE user_pin = $pin", command => command.Parameters.AddWithValue("$pin", code));
        affected += Execute($"DELETE FROM {usersTable} WHERE pin = $pin", command => command.Parameters.AddWithValue("$pin", code));
        affected += Execute($"DELETE FROM {attendanceTable} WHERE EmployeeCode = $pin", command => command.Parameters.AddWithValue("$pin", code));
        affected += Execute($"DELETE FROM {employeesTable} WHERE EmployeeCode = $pin", command => command.Parameters.AddWithValue("$pin", code));
        return affected;
    }

    public void UpsertBiometricTemplate(
        string userPin,
        int type,
        int no,
        int idx,
        bool valid,
        bool duress,
        int? majorVer,
        int? minorVer,
        int? format,
        string templateData)
    {
        // Ensure the parent users row exists before inserting into biometric_templates.
        // biometric_templates has FK: user_pin → users(pin) ON DELETE CASCADE.
        // When data arrives from the device (cdata push) the user may not be in the
        // users table yet (it might only be in the Employees table). An INSERT ... ON
        // CONFLICT DO NOTHING is safe: it never overwrites an existing user record.
        EnsureUserStubExists(userPin);

        var templatesTable = _usePostgreSql ? "\"E_Attendace\".\"biometric_templates\"" : "biometric_templates";
        // major_ver is part of the uniqueness key (not just a data column): a finger re-enrolled from
        // a device running a different algorithm version (e.g. v13 after an earlier v10 capture) must
        // land in its own row instead of overwriting the older version's template, so device sync can
        // later pick whichever version matches the target device. Default null to 0 so the conflict
        // target always matches consistently (Postgres treats NULL as distinct from NULL in unique
        // constraints, which would otherwise let every "unversioned" upsert insert a fresh duplicate
        // row rather than updating in place).
        var effectiveMajorVer = majorVer ?? 0;
        var sql = $"""
            INSERT INTO {templatesTable}
            (user_pin, type, no, idx, valid, duress, major_ver, minor_ver, format, template_data, created_at, updated_at)
            VALUES ($userPin, $type, $no, $idx, $valid, $duress, $majorVer, $minorVer, $format, $templateData, $createdAt, $updatedAt)
            ON CONFLICT (user_pin, type, no, idx, major_ver) DO UPDATE SET
                valid = excluded.valid,
                duress = excluded.duress,
                minor_ver = excluded.minor_ver,
                format = excluded.format,
                template_data = excluded.template_data,
                updated_at = excluded.updated_at
            """;

        var nowDt = DateTime.UtcNow; var now = nowDt.ToString("O");
        Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$userPin", userPin);
            command.Parameters.AddWithValue("$type", type);
            command.Parameters.AddWithValue("$no", no);
            command.Parameters.AddWithValue("$idx", idx);
            command.Parameters.AddWithValue("$valid", valid ? 1 : 0);
            command.Parameters.AddWithValue("$duress", duress ? 1 : 0);
            command.Parameters.AddWithValue("$majorVer", effectiveMajorVer);
            command.Parameters.AddWithValue("$minorVer", minorVer.HasValue ? minorVer.Value : DBNull.Value);
            command.Parameters.AddWithValue("$format", format.HasValue ? format.Value : DBNull.Value);
            command.Parameters.AddWithValue("$templateData", templateData ?? string.Empty);
            command.Parameters.AddWithValue("$createdAt", DateParam(nowDt));
            command.Parameters.AddWithValue("$updatedAt", DateParam(nowDt));
        });
    }

    public void UpsertBiometricPhoto(
        string userPin,
        int type,
        int no,
        int idx,
        int? format,
        string content)
    {
        // Ensure the parent users row exists before inserting into biometric_photos.
        // biometric_photos has FK: user_pin → users(pin) ON DELETE CASCADE.
        // When data arrives from the device (cdata push) the user may not be in the
        // users table yet (it might only be in the Employees table). An INSERT ... ON
        // CONFLICT DO NOTHING is safe: it never overwrites an existing user record.
        EnsureUserStubExists(userPin);

        var photosTable = _usePostgreSql ? "\"E_Attendace\".\"biometric_photos\"" : "biometric_photos";
        var sql = $"""
            INSERT INTO {photosTable}
            (user_pin, type, no, idx, format, content, created_at, updated_at)
            VALUES ($userPin, $type, $no, $idx, $format, $content, $createdAt, $updatedAt)
            ON CONFLICT (user_pin, type, no, idx) DO UPDATE SET
                format = excluded.format,
                content = excluded.content,
                updated_at = excluded.updated_at
            """;

        var nowDt = DateTime.UtcNow; var now = nowDt.ToString("O");
        Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$userPin", userPin);
            command.Parameters.AddWithValue("$type", type);
            command.Parameters.AddWithValue("$no", no);
            command.Parameters.AddWithValue("$idx", idx);
            command.Parameters.AddWithValue("$format", format.HasValue ? format.Value : DBNull.Value);
            command.Parameters.AddWithValue("$content", content ?? string.Empty);
            command.Parameters.AddWithValue("$createdAt", DateParam(nowDt));
            command.Parameters.AddWithValue("$updatedAt", DateParam(nowDt));
        });
    }

    public IReadOnlyDictionary<string, UnifiedUserSnapshot> GetUnifiedUsersByPins(IEnumerable<string> pins)
    {
        var pinSet = pins
            .Where(static pin => !string.IsNullOrWhiteSpace(pin))
            .Select(static pin => pin.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pinSet.Count == 0)
        {
            return new Dictionary<string, UnifiedUserSnapshot>(StringComparer.OrdinalIgnoreCase);
        }

        var usersTable = _usePostgreSql ? "\"E_Attendace\".\"users\"" : "users";
        var sql = $"""
            SELECT pin, name, privilege, password, primary_card, secondary_card, group_id, timezone_profile, verify_mode, valid_from, valid_to, expires_enabled, phone, gender, nation, id_number, updated_at
            FROM {usersTable}
            ORDER BY pin
            """;

        var rows = Query(sql, reader => new UnifiedUserSnapshot
        {
            Pin = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            Privilege = reader.IsDBNull(2) ? null : ReadInt32(reader, 2),
            Password = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            PrimaryCard = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            SecondaryCard = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            GroupId = reader.IsDBNull(6) ? null : ReadInt32(reader, 6),
            TimezoneProfile = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            VerifyMode = reader.IsDBNull(8) ? null : ReadInt32(reader, 8),
            ValidFrom = reader.IsDBNull(9) ? null : ReadDateTime(reader, 9),
            ValidTo = reader.IsDBNull(10) ? null : ReadDateTime(reader, 10),
            ExpiresEnabled = reader.IsDBNull(11) ? null : ReadInt32(reader, 11) == 1,
            Phone = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            Gender = reader.IsDBNull(13) ? null : ReadInt32(reader, 13),
            Nation = reader.IsDBNull(14) ? null : ReadInt32(reader, 14),
            IdNumber = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
            UpdatedAt = reader.IsDBNull(16) ? null : ReadDateTime(reader, 16)
        });

        var map = new Dictionary<string, UnifiedUserSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!pinSet.Contains(row.Pin))
            {
                continue;
            }

            map[row.Pin] = row;
        }

        return map;
    }

    public IReadOnlyList<UnifiedUserSnapshot> GetAllUnifiedUsers()
    {
        var usersTable = _usePostgreSql ? "\"E_Attendace\".\"users\"" : "users";
        var sql = $"""
            SELECT pin, name, privilege, password, primary_card, secondary_card, group_id, timezone_profile, verify_mode, valid_from, valid_to, expires_enabled, phone, gender, nation, id_number, updated_at
            FROM {usersTable}
            ORDER BY pin
            """;

        return Query(sql, reader => new UnifiedUserSnapshot
        {
            Pin = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            Privilege = reader.IsDBNull(2) ? null : ReadInt32(reader, 2),
            Password = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            PrimaryCard = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            SecondaryCard = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            GroupId = reader.IsDBNull(6) ? null : ReadInt32(reader, 6),
            TimezoneProfile = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            VerifyMode = reader.IsDBNull(8) ? null : ReadInt32(reader, 8),
            ValidFrom = reader.IsDBNull(9) ? null : ReadDateTime(reader, 9),
            ValidTo = reader.IsDBNull(10) ? null : ReadDateTime(reader, 10),
            ExpiresEnabled = reader.IsDBNull(11) ? null : ReadInt32(reader, 11) == 1,
            Phone = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            Gender = reader.IsDBNull(13) ? null : ReadInt32(reader, 13),
            Nation = reader.IsDBNull(14) ? null : ReadInt32(reader, 14),
            IdNumber = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
            UpdatedAt = reader.IsDBNull(16) ? null : ReadDateTime(reader, 16)
        });
    }

    public IReadOnlyDictionary<string, List<BiometricTemplateSnapshot>> GetBiometricTemplatesByPins(IEnumerable<string> pins)
    {
        var pinSet = pins
            .Where(static pin => !string.IsNullOrWhiteSpace(pin))
            .Select(static pin => pin.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pinSet.Count == 0)
        {
            return new Dictionary<string, List<BiometricTemplateSnapshot>>(StringComparer.OrdinalIgnoreCase);
        }

        var templatesTable = _usePostgreSql ? "\"E_Attendace\".\"biometric_templates\"" : "biometric_templates";
        var sql = $"""
            SELECT user_pin, type, no, idx, valid, duress, major_ver, minor_ver, format, template_data, updated_at
            FROM {templatesTable}
            ORDER BY user_pin, type, no, idx
            """;

        var rows = Query(sql, reader => new BiometricTemplateSnapshot
        {
            UserPin = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            Type = reader.IsDBNull(1) ? 0 : ReadInt32(reader, 1),
            No = reader.IsDBNull(2) ? 0 : ReadInt32(reader, 2),
            Index = reader.IsDBNull(3) ? 0 : ReadInt32(reader, 3),
            Valid = !reader.IsDBNull(4) && ReadInt32(reader, 4) == 1,
            Duress = !reader.IsDBNull(5) && ReadInt32(reader, 5) == 1,
            MajorVersion = reader.IsDBNull(6) ? null : ReadInt32(reader, 6),
            MinorVersion = reader.IsDBNull(7) ? null : ReadInt32(reader, 7),
            Format = reader.IsDBNull(8) ? null : ReadInt32(reader, 8),
            TemplateData = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            UpdatedAt = reader.IsDBNull(10) ? null : ReadDateTime(reader, 10)
        });

        var map = new Dictionary<string, List<BiometricTemplateSnapshot>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!pinSet.Contains(row.UserPin))
            {
                continue;
            }

            if (!map.TryGetValue(row.UserPin, out var list))
            {
                list = new List<BiometricTemplateSnapshot>();
                map[row.UserPin] = list;
            }

            list.Add(row);
        }

        return map;
    }

    public IReadOnlyDictionary<string, List<BiometricPhotoSnapshot>> GetBiometricPhotosByPins(IEnumerable<string> pins)
    {
        var pinSet = pins
            .Where(static pin => !string.IsNullOrWhiteSpace(pin))
            .Select(static pin => pin.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pinSet.Count == 0)
        {
            return new Dictionary<string, List<BiometricPhotoSnapshot>>(StringComparer.OrdinalIgnoreCase);
        }

        var photosTable = _usePostgreSql ? "\"E_Attendace\".\"biometric_photos\"" : "biometric_photos";
        var sql = $"""
            SELECT user_pin, type, no, idx, format, content, updated_at
            FROM {photosTable}
            ORDER BY user_pin, type, no, idx
            """;

        var rows = Query(sql, reader => new BiometricPhotoSnapshot
        {
            UserPin = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            Type = reader.IsDBNull(1) ? 0 : ReadInt32(reader, 1),
            No = reader.IsDBNull(2) ? 0 : ReadInt32(reader, 2),
            Index = reader.IsDBNull(3) ? 0 : ReadInt32(reader, 3),
            Format = reader.IsDBNull(4) ? null : ReadInt32(reader, 4),
            Content = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            UpdatedAt = reader.IsDBNull(6) ? null : ReadDateTime(reader, 6)
        });

        var map = new Dictionary<string, List<BiometricPhotoSnapshot>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!pinSet.Contains(row.UserPin))
            {
                continue;
            }

            if (!map.TryGetValue(row.UserPin, out var list))
            {
                list = new List<BiometricPhotoSnapshot>();
                map[row.UserPin] = list;
            }

            list.Add(row);
        }

        return map;
    }

    public IReadOnlyList<AttendanceRecord> GetAttendance(DateOnly? from, DateOnly? to)
    {
        var sql = """
            SELECT Id, EmployeeId, EmployeeCode, Timestamp, Status, StatusCode, VerifyMode, Workcode, DeviceId, DeviceSerialNumber, AreaId, DepartmentId
            FROM AttendanceRecords
            WHERE 1 = 1
            """;

        var parameters = new List<(string, object?)>();

        if (from.HasValue)
        {
            sql += " AND date(Timestamp) >= $from";
            parameters.Add(("$from", from.Value.ToString("yyyy-MM-dd")));
        }

        if (to.HasValue)
        {
            sql += " AND date(Timestamp) <= $to";
            parameters.Add(("$to", to.Value.ToString("yyyy-MM-dd")));
        }

        sql += " ORDER BY Timestamp DESC";

        return Query(sql, command =>
        {
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
        }, reader => MapAttendanceRecord(reader));
    }

    public AttendanceRecord AddAttendance(AttendanceRecord record)
    {
        var sql = _usePostgreSql
            ? $"""
                INSERT INTO AttendanceRecords
                (Id, EmployeeId, EmployeeCode, Timestamp, AttendanceDate, Status, StatusCode, VerifyMode, Workcode, DeviceId, DeviceSerialNumber, AreaId, DepartmentId)
                VALUES ({SqlUuid("$id")}, {SqlUuid("$employeeId")}, $code, {SqlTs("$timestamp")}, CAST({SqlTs("$timestamp")} AS date), $status, $statusCode, $verify, $workcode, {SqlUuid("$deviceId")}, $deviceSn, $areaId, $deptId)
                ON CONFLICT DO NOTHING
                """
            : """
                INSERT OR IGNORE INTO AttendanceRecords
                (Id, EmployeeId, EmployeeCode, Timestamp, Status, StatusCode, VerifyMode, Workcode, DeviceId, DeviceSerialNumber, AreaId, DepartmentId)
                VALUES ($id, $employeeId, $code, $timestamp, $status, $statusCode, $verify, $workcode, $deviceId, $deviceSn, $areaId, $deptId)
                """;

        var timestampText = record.Timestamp.ToString("O"); // kept for idempotency WHERE clause
        var rows = Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$id", GuidParam(record.Id));
            command.Parameters.AddWithValue("$employeeId", GuidParam(record.EmployeeId));
            command.Parameters.AddWithValue("$code", record.EmployeeCode);
            command.Parameters.AddWithValue("$timestamp", timestampText);
            command.Parameters.AddWithValue("$status", (int)record.Status);
            command.Parameters.AddWithValue("$statusCode", record.StatusCode.HasValue ? record.StatusCode.Value : DBNull.Value);
            command.Parameters.AddWithValue("$verify", record.VerifyMode.HasValue ? record.VerifyMode.Value : DBNull.Value);
            command.Parameters.AddWithValue("$workcode", record.Workcode ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$deviceId", record.DeviceId.HasValue ? GuidParam(record.DeviceId.Value) : (object)DBNull.Value); // uuid handled by SqlUuid in SQL
            command.Parameters.AddWithValue("$deviceSn", record.DeviceSerialNumber ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$areaId", record.AreaId > 0 ? record.AreaId : 1);
            command.Parameters.AddWithValue("$deptId", record.DepartmentId > 0 ? record.DepartmentId : 1);
        });

        if (rows > 0)
        {
            return record;
        }

        const string lookupSql = """
            SELECT Id, EmployeeId, EmployeeCode, Timestamp, Status, StatusCode, VerifyMode, Workcode, DeviceId, DeviceSerialNumber, AreaId, DepartmentId
            FROM AttendanceRecords
            WHERE COALESCE(DeviceSerialNumber,'') = $deviceSnKey
              AND EmployeeCode = $code
              AND Timestamp = $timestamp
              AND COALESCE(StatusCode,-1) = $statusCodeKey
              AND COALESCE(VerifyMode,-1) = $verifyKey
              AND COALESCE(Workcode,'') = $workcodeKey
            LIMIT 1
            """;

        var existing = QuerySingle(lookupSql, command =>
        {
            command.Parameters.AddWithValue("$deviceSnKey", record.DeviceSerialNumber?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("$code", record.EmployeeCode);
            command.Parameters.AddWithValue("$timestamp", timestampText);
            command.Parameters.AddWithValue("$statusCodeKey", record.StatusCode ?? -1);
            command.Parameters.AddWithValue("$verifyKey", record.VerifyMode ?? -1);
            command.Parameters.AddWithValue("$workcodeKey", record.Workcode?.Trim() ?? string.Empty);
        }, reader => MapAttendanceRecord(reader));

        return existing ?? record;
    }

    public bool UpdateAttendance(AttendanceRecord record)
    {
        var sql = $"""
            UPDATE AttendanceRecords
            SET EmployeeId = {SqlUuid("$employeeId")},
                EmployeeCode = $code,
                Timestamp = {SqlTs("$timestamp")},
                Status = $status,
                StatusCode = $statusCode,
                VerifyMode = $verify,
                Workcode = $workcode,
                DeviceId = {SqlUuid("$deviceId")},
                DeviceSerialNumber = $deviceSn
            WHERE Id = {SqlUuid("$id")}
            """;

        var rows = Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$id", GuidParam(record.Id));
            command.Parameters.AddWithValue("$employeeId", GuidParam(record.EmployeeId));
            command.Parameters.AddWithValue("$code", record.EmployeeCode);
            command.Parameters.AddWithValue("$timestamp", record.Timestamp.ToString("O"));
            command.Parameters.AddWithValue("$status", (int)record.Status);
            command.Parameters.AddWithValue("$statusCode", record.StatusCode.HasValue ? record.StatusCode.Value : DBNull.Value);
            command.Parameters.AddWithValue("$verify", record.VerifyMode.HasValue ? record.VerifyMode.Value : DBNull.Value);
            command.Parameters.AddWithValue("$workcode", record.Workcode ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$deviceId", record.DeviceId.HasValue ? GuidParam(record.DeviceId.Value) : (object)DBNull.Value); // uuid handled by SqlUuid in SQL
            command.Parameters.AddWithValue("$deviceSn", record.DeviceSerialNumber ?? (object)DBNull.Value);
        });

        return rows > 0;
    }

    public bool DeleteAttendance(Guid attendanceId)
    {
        var sql = $"DELETE FROM AttendanceRecords WHERE Id = {SqlUuid("$id")}";
        var rows = Execute(sql, command => command.Parameters.AddWithValue("$id", GuidParam(attendanceId)));
        return rows > 0;
    }

    public IReadOnlyList<DeviceCommand> GetPendingCommands(string deviceSerialNumber, int maxCount)
    {
        const string sql = """
            SELECT Id, DeviceSerialNumber, CommandText, CreatedUtc, SentUtc, ReplyText
            FROM DeviceCommands
            WHERE LOWER(DeviceSerialNumber) = LOWER($sn) AND SentUtc IS NULL
            ORDER BY CreatedUtc
            LIMIT $maxCount
            """;

        return Query(sql, command =>
        {
            command.Parameters.AddWithValue("$sn", deviceSerialNumber);
            command.Parameters.AddWithValue("$maxCount", maxCount);
        }, reader => new DeviceCommand
        {
            Id = ReadGuid(reader, 0),
            DeviceSerialNumber = reader.GetString(1),
            CommandText = reader.GetString(2),
            CreatedUtc = ReadDateTime(reader, 3),
            SentUtc = reader.IsDBNull(4) ? null : ReadDateTime(reader, 4),
            ReplyText = reader.IsDBNull(5) ? null : reader.GetString(5)
        });
    }

    public IReadOnlyList<DeviceCommand> GetDeviceCommands(string deviceSerialNumber, int maxCount)
    {
        const string sql = """
            SELECT Id, DeviceSerialNumber, CommandText, CreatedUtc, SentUtc, ReplyText
            FROM DeviceCommands
            WHERE LOWER(DeviceSerialNumber) = LOWER($sn)
            ORDER BY CreatedUtc DESC
            LIMIT $maxCount
            """;

        return Query(sql, command =>
        {
            command.Parameters.AddWithValue("$sn", deviceSerialNumber);
            command.Parameters.AddWithValue("$maxCount", maxCount);
        }, reader => new DeviceCommand
        {
            Id = ReadGuid(reader, 0),
            DeviceSerialNumber = reader.GetString(1),
            CommandText = reader.GetString(2),
            CreatedUtc = ReadDateTime(reader, 3),
            SentUtc = reader.IsDBNull(4) ? null : ReadDateTime(reader, 4),
            ReplyText = reader.IsDBNull(5) ? null : reader.GetString(5)
        });
    }

    public DeviceCommand EnqueueCommand(DeviceCommand command)
    {
        var sql = $"""
            INSERT INTO DeviceCommands (Id, DeviceSerialNumber, CommandText, CreatedUtc)
            VALUES ({SqlUuid("$id")}, $sn, $cmd, {SqlTs("$created")})
            """;

        Execute(sql, db =>
        {
            db.Parameters.AddWithValue("$id", GuidParam(command.Id));
            db.Parameters.AddWithValue("$sn", command.DeviceSerialNumber);
            db.Parameters.AddWithValue("$cmd", command.CommandText);
            db.Parameters.AddWithValue("$created", command.CreatedUtc.ToString("O"));
        });

        return command;
    }

    public void MarkCommandSent(Guid commandId)
    {
        var sql = $"UPDATE DeviceCommands SET SentUtc = {SqlTs("$sent")} WHERE Id = {SqlUuid("$id")}";
        Execute(sql, db =>
        {
            db.Parameters.AddWithValue("$id", GuidParam(commandId));
            db.Parameters.AddWithValue("$sent", DateTime.UtcNow.ToString("O"));
        });
    }

    public void SaveCommandReply(Guid commandId, string replyText)
    {
        // Also set SentUtc when it is still NULL (fire-and-forget commands like CHECK / DATA QUERY
        // are replied-on-delivery via RecordCommandReply before MarkCommandSent is called, so
        // SentUtc is NULL at that point). Without this, GetPendingCommands (SentUtc IS NULL)
        // keeps returning the same command on every getrequest poll → infinite re-send loop.
        var sql = $"""
            UPDATE DeviceCommands
            SET ReplyText = $reply,
                SentUtc   = CASE WHEN SentUtc IS NULL THEN {SqlTs("$sent")} ELSE SentUtc END
            WHERE Id = {SqlUuid("$id")}
            """;

        Execute(sql, db =>
        {
            db.Parameters.AddWithValue("$id", GuidParam(commandId));
            db.Parameters.AddWithValue("$reply", replyText);
            db.Parameters.AddWithValue("$sent", DateParam(DateTime.UtcNow));
        });
    }


    public int ClearPendingCommands(string deviceSerialNumber)
    {
        const string sql = """
            DELETE FROM DeviceCommands
            WHERE LOWER(DeviceSerialNumber) = LOWER($sn)
              AND SentUtc IS NULL
            """;

        return Execute(sql, db =>
        {
            db.Parameters.AddWithValue("$sn", deviceSerialNumber);
        });
    }

    public int ResetStaleSentCommands(string deviceSerialNumber, int olderThanMinutes)
    {
        // Commands delivered but never replied to stay stuck forever because GetPendingCommands
        // filters SentUtc IS NULL. Reset them so they get re-delivered next connection.
        // SentUtc is timestamptz on Postgres, so the threshold must be CAST via SqlTs — passing
        // the raw ISO string makes Postgres throw "operator does not exist: timestamptz < text".
        var sql = $"""
            UPDATE DeviceCommands
            SET SentUtc = NULL
            WHERE LOWER(DeviceSerialNumber) = LOWER($sn)
              AND SentUtc IS NOT NULL
              AND ReplyText IS NULL
              AND SentUtc < {SqlTs("$threshold")}
            """;

        var threshold = DateParam(DateTime.UtcNow.AddMinutes(-olderThanMinutes));
        return Execute(sql, db =>
        {
            db.Parameters.AddWithValue("$sn", deviceSerialNumber);
            db.Parameters.AddWithValue("$threshold", threshold);
        });
    }

    public SyncLog AddSyncLog(SyncLog log)
    {
        var sql = $"""
            INSERT INTO SyncLogs (Id, DeviceSerialNumber, Type, Status, CommandText, ResponseText, CreatedUtc, CompletedUtc)
            VALUES ({SqlUuid("$id")}, $sn, $type, $status, $cmd, $response, {SqlTs("$created")}, {SqlTs("$completed")})
            """;

        Execute(sql, db =>
        {
            db.Parameters.AddWithValue("$id", GuidParam(log.Id));
            db.Parameters.AddWithValue("$sn", log.DeviceSerialNumber);
            db.Parameters.AddWithValue("$type", (int)log.Type);
            db.Parameters.AddWithValue("$status", (int)log.Status);
            db.Parameters.AddWithValue("$cmd", log.CommandText);
            db.Parameters.AddWithValue("$response", log.ResponseText ?? string.Empty);
            db.Parameters.AddWithValue("$created", log.CreatedUtc.ToString("O"));
            db.Parameters.AddWithValue("$completed", log.CompletedUtc.HasValue ? log.CompletedUtc.Value.ToString("O") : (object)DBNull.Value);
        });

        return log;
    }

    public IReadOnlyList<SyncLog> GetSyncLogs(string deviceSerialNumber, int maxCount)
    {
        const string sql = """
            SELECT Id, DeviceSerialNumber, Type, Status, CommandText, ResponseText, CreatedUtc, CompletedUtc
            FROM SyncLogs
            WHERE LOWER(DeviceSerialNumber) = LOWER($sn)
            ORDER BY CreatedUtc DESC
            LIMIT $max
            """;

        return Query(sql, db =>
        {
            db.Parameters.AddWithValue("$sn", deviceSerialNumber);
            db.Parameters.AddWithValue("$max", maxCount);
        }, reader => new SyncLog
        {
            Id = ReadGuid(reader, 0),
            DeviceSerialNumber = reader.GetString(1),
            Type = (SyncType)ReadInt32(reader, 2),
            Status = (SyncStatus)ReadInt32(reader, 3),
            CommandText = reader.GetString(4),
            ResponseText = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            CreatedUtc = ReadDateTime(reader, 6),
            CompletedUtc = reader.IsDBNull(7) ? null : ReadDateTime(reader, 7)
        });
    }

    public void UpdateSyncLogResult(Guid syncLogId, SyncStatus status, string responseText)
    {
        var sql = $"""
            UPDATE SyncLogs
            SET Status = $status,
                ResponseText = $response,
                CompletedUtc = {SqlTs("$completed")}
            WHERE Id = {SqlUuid("$id")}
            """;

        Execute(sql, db =>
        {
            db.Parameters.AddWithValue("$id", GuidParam(syncLogId));
            db.Parameters.AddWithValue("$status", (int)status);
            db.Parameters.AddWithValue("$response", responseText ?? string.Empty);
            db.Parameters.AddWithValue("$completed", DateTime.UtcNow.ToString("O"));
        });
    }

    public IReadOnlyDictionary<string, long> GetDeviceTableStamps(string deviceSerialNumber)
    {
        if (string.IsNullOrWhiteSpace(deviceSerialNumber))
        {
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        const string sql = """
            SELECT TableName, StampValue
            FROM DeviceTableStamps
            WHERE LOWER(DeviceSerialNumber) = LOWER($sn)
            """;

        var rows = Query(sql, db =>
        {
            db.Parameters.AddWithValue("$sn", deviceSerialNumber.Trim());
        }, reader =>
        {
            var table = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var stamp = reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1));
            return (table, stamp);
        });

        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, stamp) in rows)
        {
            if (!string.IsNullOrWhiteSpace(table))
            {
                result[table.Trim().ToUpperInvariant()] = stamp;
            }
        }

        return result;
    }

    public void UpsertDeviceTableStamp(string deviceSerialNumber, string tableName, long stampValue)
    {
        if (string.IsNullOrWhiteSpace(deviceSerialNumber) || string.IsNullOrWhiteSpace(tableName))
        {
            return;
        }

        var sql = $"""
            INSERT INTO DeviceTableStamps (DeviceSerialNumber, TableName, StampValue, UpdatedUtc)
            VALUES ($sn, $table, $stamp, {SqlTs("$updated")})
            ON CONFLICT (DeviceSerialNumber, TableName) DO UPDATE SET
                StampValue = excluded.StampValue,
                UpdatedUtc = excluded.UpdatedUtc
            """;

        Execute(sql, db =>
        {
            db.Parameters.AddWithValue("$sn", deviceSerialNumber.Trim());
            db.Parameters.AddWithValue("$table", tableName.Trim().ToUpperInvariant());
            db.Parameters.AddWithValue("$stamp", stampValue);
            db.Parameters.AddWithValue("$updated", DateParam(DateTime.UtcNow));
        });
    }

    private void InitializeSchema()
    {
        var biometricTemplateIdColumn = _usePostgreSql
            ? "id BIGSERIAL PRIMARY KEY,"
            : "id INTEGER PRIMARY KEY AUTOINCREMENT,";
        var biometricPhotoIdColumn = _usePostgreSql
            ? "id BIGSERIAL PRIMARY KEY,"
            : "id INTEGER PRIMARY KEY AUTOINCREMENT,";

        var sql = $"""
            CREATE TABLE IF NOT EXISTS Employees (
                Id TEXT PRIMARY KEY,
                EmployeeCode TEXT NOT NULL UNIQUE,
                FullName TEXT NOT NULL,
                Area TEXT NOT NULL DEFAULT '',
                Department TEXT NOT NULL,
                DepartmentId INTEGER NULL,
                DepartmentsJson TEXT NOT NULL DEFAULT '[]',
                Position TEXT NOT NULL DEFAULT '',
                CardNumber TEXT NOT NULL DEFAULT '',
                Password TEXT NOT NULL DEFAULT '',
                MobileNumber TEXT NOT NULL DEFAULT '',
                Gender TEXT NOT NULL DEFAULT '',
                Privilege INTEGER NOT NULL DEFAULT 0,
                VerifyMode INTEGER NOT NULL DEFAULT 0,
                FaceTemplateVisible TEXT NULL,
                FaceTemplateInfrared TEXT NULL,
                ProfilePhoto TEXT NULL,
                FingerprintTemplates TEXT NULL,
                CreatedAt TEXT NOT NULL DEFAULT '',
                SyncedAt TEXT NULL,
                IsActive INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Devices (
                Id TEXT PRIMARY KEY,
                SerialNumber TEXT NOT NULL UNIQUE,
                Name TEXT NOT NULL,
                Model TEXT NOT NULL DEFAULT '',
                IpAddress TEXT NOT NULL,
                Location TEXT NOT NULL,
                TimeZone TEXT NOT NULL DEFAULT '+5:45',
                SupportsVisibleLightFace INTEGER NOT NULL DEFAULT 0,
                SupportsInfraredFace INTEGER NOT NULL DEFAULT 0,
                SupportsFingerprint INTEGER NOT NULL DEFAULT 0,
                FaceAlgorithmVersion TEXT NOT NULL DEFAULT '',
                FingerprintAlgorithmVersion TEXT NOT NULL DEFAULT '',
                FirmwareVersion TEXT NOT NULL DEFAULT '',
                DeviceType TEXT NOT NULL DEFAULT '',
                PushVer TEXT NOT NULL DEFAULT '',
                Language INTEGER NOT NULL DEFAULT 0,
                MultiBioDataSupport TEXT NOT NULL DEFAULT '',
                MultiBioPhotoSupport TEXT NOT NULL DEFAULT '',
                MultiBioVersion TEXT NOT NULL DEFAULT '',
                MaxMultiBioDataCount TEXT NOT NULL DEFAULT '',
                MaxMultiBioPhotoCount TEXT NOT NULL DEFAULT '',
                FingerFunOn INTEGER NOT NULL DEFAULT 0,
                FaceFunOn INTEGER NOT NULL DEFAULT 0,
                VisiLightFunOn INTEGER NOT NULL DEFAULT 0,
                BioDataFun INTEGER NOT NULL DEFAULT 0,
                BioPhotoFun INTEGER NOT NULL DEFAULT 0,
                SupportsPalm INTEGER NOT NULL DEFAULT 0,
                SupportsFingerVein INTEGER NOT NULL DEFAULT 0,
                CapabilitiesJson TEXT NOT NULL DEFAULT '',
                CreatedAtUtc TEXT NOT NULL DEFAULT '',
                UpdatedAtUtc TEXT NOT NULL DEFAULT '',
                Status INTEGER NOT NULL DEFAULT 0,
                IsRegistrationDevice INTEGER NOT NULL DEFAULT 0,
                IsOnline INTEGER NOT NULL,
                LastSeenUtc TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS users (
                pin TEXT PRIMARY KEY,
                name TEXT NOT NULL DEFAULT '',
                privilege INTEGER NULL,
                password TEXT NOT NULL DEFAULT '',
                primary_card TEXT NOT NULL DEFAULT '',
                secondary_card TEXT NOT NULL DEFAULT '',
                group_id INTEGER NULL,
                timezone_profile TEXT NOT NULL DEFAULT '',
                verify_mode INTEGER NULL,
                valid_from TEXT NULL,
                valid_to TEXT NULL,
                expires_enabled INTEGER NULL,
                phone TEXT NOT NULL DEFAULT '',
                gender INTEGER NULL,
                nation INTEGER NULL,
                id_number TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS biometric_templates (
                {biometricTemplateIdColumn}
                user_pin TEXT NOT NULL,
                type INTEGER NOT NULL,
                no INTEGER NOT NULL,
                idx INTEGER NOT NULL,
                valid INTEGER NOT NULL DEFAULT 1,
                duress INTEGER NOT NULL DEFAULT 0,
                major_ver INTEGER NULL,
                minor_ver INTEGER NULL,
                format INTEGER NULL,
                template_data TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT '',
                UNIQUE (user_pin, type, no, idx, major_ver),
                FOREIGN KEY (user_pin) REFERENCES users(pin) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS biometric_photos (
                {biometricPhotoIdColumn}
                user_pin TEXT NOT NULL,
                type INTEGER NOT NULL,
                no INTEGER NOT NULL,
                idx INTEGER NOT NULL,
                format INTEGER NULL,
                content TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT '',
                UNIQUE (user_pin, type, no, idx),
                FOREIGN KEY (user_pin) REFERENCES users(pin) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS AttendanceRecords (
                Id TEXT PRIMARY KEY,
                EmployeeId TEXT NOT NULL,
                EmployeeCode TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                Status INTEGER NOT NULL,
                StatusCode INTEGER NULL,
                VerifyMode INTEGER NULL,
                Workcode TEXT NULL,
                DeviceId TEXT NULL,
                DeviceSerialNumber TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_AttendanceRecords_Timestamp ON AttendanceRecords (Timestamp);
            CREATE TABLE IF NOT EXISTS DeviceCommands (
                Id TEXT PRIMARY KEY,
                DeviceSerialNumber TEXT NOT NULL,
                CommandText TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                SentUtc TEXT NULL,
                ReplyText TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_DeviceCommands_Pending ON DeviceCommands (DeviceSerialNumber, SentUtc);
            CREATE TABLE IF NOT EXISTS SyncLogs (
                Id TEXT PRIMARY KEY,
                DeviceSerialNumber TEXT NOT NULL,
                Type INTEGER NOT NULL,
                Status INTEGER NOT NULL,
                CommandText TEXT NOT NULL,
                ResponseText TEXT NULL,
                CreatedUtc TEXT NOT NULL,
                CompletedUtc TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_SyncLogs_DeviceStatus ON SyncLogs (DeviceSerialNumber, Status, CreatedUtc);
            CREATE TABLE IF NOT EXISTS DeviceTableStamps (
                DeviceSerialNumber TEXT NOT NULL,
                TableName TEXT NOT NULL,
                StampValue BIGINT NOT NULL DEFAULT 0,
                UpdatedUtc TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (DeviceSerialNumber, TableName)
            );
            CREATE TABLE IF NOT EXISTS Areas (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE,
                IsDefault INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT '',
                UpdatedAt TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS Departments (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                AreaId INTEGER NOT NULL DEFAULT 1,
                IsDefault INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT '',
                UpdatedAt TEXT NOT NULL DEFAULT '',
                UNIQUE(AreaId, Name)
            );
            CREATE TABLE IF NOT EXISTS EmployeeDepartments (
                EmployeeId TEXT NOT NULL,
                DepartmentId INTEGER NOT NULL,
                AssignedAt TEXT NOT NULL DEFAULT '',
                AssignedBy TEXT NULL,
                PRIMARY KEY (EmployeeId, DepartmentId)
            );
            CREATE TABLE IF NOT EXISTS SyncQueue (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EntityType TEXT NOT NULL,
                EntityId TEXT NOT NULL,
                Operation TEXT NOT NULL,
                Status INTEGER NOT NULL DEFAULT 0,
                RetryCount INTEGER NOT NULL DEFAULT 0,
                MaxRetries INTEGER NOT NULL DEFAULT 3,
                Payload TEXT NULL,
                ErrorMessage TEXT NULL,
                CreatedAt TEXT NOT NULL DEFAULT '',
                ProcessedAt TEXT NULL,
                NextRetryAt TEXT NULL
            );
            """;

        DbConnection? connection = null;
        try
        {
            connection = CreateConnection();
            OpenConnection(connection);
        }
        catch
        {
            // PostgreSQL may not be running yet — schema will be created on first successful connection.
            connection?.Dispose();
            return;
        }

        try
        {
        using var command = connection.CreateCommand();
        command.CommandText = NormalizeSql(sql);
        command.ExecuteNonQuery();

        // ── Migration: Update biometric_templates unique constraint to allow multi-version FP templates ──
        if (_usePostgreSql)
        {
            try
            {
                using var pgCmd = connection.CreateCommand();
                pgCmd.CommandText = "ALTER TABLE \"E_Attendace\".\"biometric_templates\" DROP CONSTRAINT IF EXISTS \"biometric_templates_user_pin_type_no_idx_key\";";
                pgCmd.ExecuteNonQuery();
            }
            catch { }

            try
            {
                using var pgCmd = connection.CreateCommand();
                pgCmd.CommandText = "ALTER TABLE \"E_Attendace\".\"biometric_templates\" ADD CONSTRAINT \"biometric_templates_user_pin_type_no_idx_major_ver_key\" UNIQUE (user_pin, type, no, idx, major_ver);";
                pgCmd.ExecuteNonQuery();
            }
            catch { }
        }
        else
        {
            try
            {
                string? tableSql = null;
                using (var checkCmd = connection.CreateCommand())
                {
                    checkCmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='biometric_templates';";
                    using (var reader = checkCmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            tableSql = reader.GetString(0);
                        }
                    }
                }

                if (tableSql != null && tableSql.Contains("UNIQUE (user_pin, type, no, idx)", StringComparison.OrdinalIgnoreCase))
                {
                    using (var txCmd = connection.CreateCommand())
                    {
                        txCmd.CommandText = @"""
                            PRAGMA foreign_keys = OFF;
                            BEGIN TRANSACTION;
                            
                            CREATE TABLE biometric_templates_new (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                user_pin TEXT NOT NULL,
                                type INTEGER NOT NULL,
                                no INTEGER NOT NULL,
                                idx INTEGER NOT NULL,
                                valid INTEGER NOT NULL DEFAULT 1,
                                duress INTEGER NOT NULL DEFAULT 0,
                                major_ver INTEGER NULL,
                                minor_ver INTEGER NULL,
                                format INTEGER NULL,
                                template_data TEXT NOT NULL DEFAULT '',
                                created_at TEXT NOT NULL DEFAULT '',
                                updated_at TEXT NOT NULL DEFAULT '',
                                UNIQUE (user_pin, type, no, idx, major_ver),
                                FOREIGN KEY (user_pin) REFERENCES users(pin) ON DELETE CASCADE
                            );
                            
                            INSERT INTO biometric_templates_new (id, user_pin, type, no, idx, valid, duress, major_ver, minor_ver, format, template_data, created_at, updated_at)
                            SELECT id, user_pin, type, no, idx, valid, duress, major_ver, minor_ver, format, template_data, created_at, updated_at FROM biometric_templates;
                            
                            DROP TABLE biometric_templates;
                            
                            ALTER TABLE biometric_templates_new RENAME TO biometric_templates;
                            
                            COMMIT;
                            PRAGMA foreign_keys = ON;
                            """;
                        txCmd.ExecuteNonQuery();
                    }
                }
            }
            catch { }
        }

        EnsureColumn(connection, "Devices", "Model", "ALTER TABLE Devices ADD COLUMN Model TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Devices", "TimeZone", "ALTER TABLE Devices ADD COLUMN TimeZone TEXT NOT NULL DEFAULT '+5:45';");
        EnsureColumn(connection, "Devices", "SupportsVisibleLightFace", "ALTER TABLE Devices ADD COLUMN SupportsVisibleLightFace INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(connection, "Devices", "SupportsInfraredFace", "ALTER TABLE Devices ADD COLUMN SupportsInfraredFace INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(connection, "Devices", "SupportsFingerprint", "ALTER TABLE Devices ADD COLUMN SupportsFingerprint INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(connection, "Devices", "FaceAlgorithmVersion", "ALTER TABLE Devices ADD COLUMN FaceAlgorithmVersion TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Devices", "FingerprintAlgorithmVersion", "ALTER TABLE Devices ADD COLUMN FingerprintAlgorithmVersion TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Devices", "FirmwareVersion", "ALTER TABLE Devices ADD COLUMN FirmwareVersion TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Devices", "DeviceType", "ALTER TABLE Devices ADD COLUMN DeviceType TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Devices", "PushVer", "ALTER TABLE Devices ADD COLUMN PushVer TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Devices", "Language", "ALTER TABLE Devices ADD COLUMN Language INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(connection, "Devices", "MultiBioDataSupport", "ALTER TABLE Devices ADD COLUMN MultiBioDataSupport TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Devices", "MultiBioPhotoSupport", "ALTER TABLE Devices ADD COLUMN MultiBioPhotoSupport TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Devices", "MultiBioVersion", "ALTER TABLE Devices ADD COLUMN MultiBioVersion TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Devices", "MaxMultiBioDataCount", "ALTER TABLE Devices ADD COLUMN MaxMultiBioDataCount TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Devices", "MaxMultiBioPhotoCount", "ALTER TABLE Devices ADD COLUMN MaxMultiBioPhotoCount TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Devices", "FingerFunOn", "ALTER TABLE Devices ADD COLUMN FingerFunOn INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(connection, "Devices", "FaceFunOn", "ALTER TABLE Devices ADD COLUMN FaceFunOn INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(connection, "Devices", "VisiLightFunOn", "ALTER TABLE Devices ADD COLUMN VisiLightFunOn INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(connection, "Devices", "BioDataFun", "ALTER TABLE Devices ADD COLUMN BioDataFun INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(connection, "Devices", "BioPhotoFun", "ALTER TABLE Devices ADD COLUMN BioPhotoFun INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(connection, "Devices", "SupportsPalm", "ALTER TABLE Devices ADD COLUMN SupportsPalm INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(connection, "Devices", "SupportsFingerVein", "ALTER TABLE Devices ADD COLUMN SupportsFingerVein INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(connection, "Devices", "CapabilitiesJson", "ALTER TABLE Devices ADD COLUMN CapabilitiesJson TEXT NOT NULL DEFAULT '{}';");
        EnsureColumn(connection, "Devices", "CreatedAtUtc", "ALTER TABLE Devices ADD COLUMN CreatedAtUtc TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Devices", "UpdatedAtUtc", "ALTER TABLE Devices ADD COLUMN UpdatedAtUtc TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Devices", "Status", "ALTER TABLE Devices ADD COLUMN Status INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(connection, "Devices", "IsRegistrationDevice", "ALTER TABLE Devices ADD COLUMN IsRegistrationDevice INTEGER NOT NULL DEFAULT 0;");

        EnsureColumn(connection, "Employees", "Area", "ALTER TABLE Employees ADD COLUMN Area TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Employees", "DepartmentId", "ALTER TABLE Employees ADD COLUMN DepartmentId INTEGER NULL;");
        EnsureColumn(connection, "Employees", "AreaId", "ALTER TABLE Employees ADD COLUMN AreaId INTEGER NOT NULL DEFAULT 1;");
        EnsureColumn(connection, "Devices", "AreaId", "ALTER TABLE Devices ADD COLUMN AreaId INTEGER NOT NULL DEFAULT 1;");
        EnsureColumn(connection, "Devices", "DepartmentId", "ALTER TABLE Devices ADD COLUMN DepartmentId INTEGER NOT NULL DEFAULT 1;");
        EnsureColumn(connection, "Devices", "IsRegistered", "ALTER TABLE Devices ADD COLUMN IsRegistered INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(connection, "AttendanceRecords", "AreaId", "ALTER TABLE AttendanceRecords ADD COLUMN AreaId INTEGER NOT NULL DEFAULT 1;");
        EnsureColumn(connection, "AttendanceRecords", "DepartmentId", "ALTER TABLE AttendanceRecords ADD COLUMN DepartmentId INTEGER NOT NULL DEFAULT 1;");
        SeedDefaultAreaAndDepartment(connection);
        EnsureColumn(connection, "Employees", "CardNumber", "ALTER TABLE Employees ADD COLUMN CardNumber TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Employees", "DepartmentsJson", "ALTER TABLE Employees ADD COLUMN DepartmentsJson TEXT NOT NULL DEFAULT '[]';");
        EnsureColumn(connection, "Employees", "Position", "ALTER TABLE Employees ADD COLUMN Position TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Employees", "Password", "ALTER TABLE Employees ADD COLUMN Password TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Employees", "MobileNumber", "ALTER TABLE Employees ADD COLUMN MobileNumber TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Employees", "Gender", "ALTER TABLE Employees ADD COLUMN Gender TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Employees", "Privilege", "ALTER TABLE Employees ADD COLUMN Privilege INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(connection, "Employees", "VerifyMode", "ALTER TABLE Employees ADD COLUMN VerifyMode INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(connection, "Employees", "FaceTemplateVisible", "ALTER TABLE Employees ADD COLUMN FaceTemplateVisible TEXT NULL;");
        EnsureColumn(connection, "Employees", "FaceTemplateInfrared", "ALTER TABLE Employees ADD COLUMN FaceTemplateInfrared TEXT NULL;");
        EnsureColumn(connection, "Employees", "ProfilePhoto", "ALTER TABLE Employees ADD COLUMN ProfilePhoto TEXT NULL;");
        EnsureColumn(connection, "Employees", "FingerprintTemplates", "ALTER TABLE Employees ADD COLUMN FingerprintTemplates TEXT NULL;");
        EnsureColumn(connection, "Employees", "CreatedAt", "ALTER TABLE Employees ADD COLUMN CreatedAt TEXT NOT NULL DEFAULT '';");
        EnsureColumn(connection, "Employees", "SyncedAt", "ALTER TABLE Employees ADD COLUMN SyncedAt TEXT NULL;");

        EnsureAttendanceRecordIdempotency(connection);
        } // end try
        catch { /* best-effort — schema applied on next successful call */ }
        finally { connection?.Dispose(); }
    }

    private void EnsureAttendanceRecordIdempotency(DbConnection connection)
    {
        const string indexSql =
            """
            CREATE UNIQUE INDEX IF NOT EXISTS UX_AttendanceRecords_DeviceEmployeeTimeStatusVerifyWork
            ON AttendanceRecords (
                COALESCE(DeviceSerialNumber,''),
                EmployeeCode,
                Timestamp,
                COALESCE(StatusCode,-1),
                COALESCE(VerifyMode,-1),
                COALESCE(Workcode,'')
            );
            """;

        try
        {
            using var indexCmd = connection.CreateCommand();
            indexCmd.CommandText = NormalizeSql(indexSql);
            indexCmd.ExecuteNonQuery();
            return;
        }
        catch
        {
            // Likely duplicates from repeated uploads; attempt best-effort de-duplication then retry index creation.
        }

        try
        {
            var dedupSql = _usePostgreSql
                ? """
                    DELETE FROM AttendanceRecords a
                    USING (
                        SELECT Id,
                               ROW_NUMBER() OVER (
                                   PARTITION BY
                                       COALESCE(DeviceSerialNumber,''),
                                       EmployeeCode,
                                       Timestamp,
                                       COALESCE(StatusCode,-1),
                                       COALESCE(VerifyMode,-1),
                                       COALESCE(Workcode,'')
                                   ORDER BY Id
                               ) AS rn
                        FROM AttendanceRecords
                    ) d
                    WHERE a.Id = d.Id
                      AND d.rn > 1;
                    """
                : """
                    DELETE FROM AttendanceRecords
                    WHERE rowid NOT IN (
                        SELECT MIN(rowid)
                        FROM AttendanceRecords
                        GROUP BY
                            COALESCE(DeviceSerialNumber,''),
                            EmployeeCode,
                            Timestamp,
                            COALESCE(StatusCode,-1),
                            COALESCE(VerifyMode,-1),
                            COALESCE(Workcode,'')
                    );
                    """;

            using var dedupCmd = connection.CreateCommand();
            dedupCmd.CommandText = NormalizeSql(dedupSql);
            dedupCmd.ExecuteNonQuery();
        }
        catch
        {
            return;
        }

        try
        {
            using var indexCmd = connection.CreateCommand();
            indexCmd.CommandText = NormalizeSql(indexSql);
            indexCmd.ExecuteNonQuery();
        }
        catch
        {
        }
    }

    // ── Timetable / Holiday stubs (handled by TimetableRepository via EF) ──

    public TimetableEntity? GetTimetableById(int timetableId) => null;
    public IReadOnlyList<TimetableEntity> GetAllTimetables() => [];
    public IReadOnlyList<TimetableDayEntity> GetTimetableDays(int timetableId) => [];
    public IReadOnlyList<EmployeeTimetableEntity> GetEmployeeTimetables(int timetableId) => [];
    public IReadOnlyList<DepartmentTimetableEntity> GetDepartmentTimetables(int timetableId) => [];
    public IReadOnlyList<DepartmentTimetableEntity> GetAllDepartmentTimetables() => [];
    public IReadOnlyList<EmployeeTimetableEntity> GetEmployeeTimetablesByPersonnel(Guid personnelId) => [];
    public IReadOnlyList<DepartmentTimetableEntity> GetDepartmentTimetablesByDepartment(int departmentId) => [];
    public IReadOnlyList<HolidayEntity> GetAllHolidays() => [];
    public IReadOnlyList<AccCombinationEntity> GetAllAccCombinations() => [];

    // ── Seed ────────────────────────────────────────────────────────────────

    private void SeedDefaultAreaAndDepartment(DbConnection connection)
    {
        var nowDt = DateTime.UtcNow; var now = nowDt.ToString("O");
        // Use INSERT OR IGNORE (SQLite) / INSERT ... ON CONFLICT DO NOTHING (Postgres)
        var areaInsert = _usePostgreSql
            ? $"INSERT INTO \"E_Attendace\".\"Areas\" (\"Id\", \"Name\", \"IsDefault\", \"CreatedAt\", \"UpdatedAt\") VALUES (1, 'Default Area', 1, @now, @now) ON CONFLICT (\"Id\") DO NOTHING;"
            : "INSERT OR IGNORE INTO Areas (Id, Name, IsDefault, CreatedAt, UpdatedAt) VALUES (1, 'Default Area', 1, $now, $now);";

        var deptInsert = _usePostgreSql
            ? $"INSERT INTO \"E_Attendace\".\"Departments\" (\"Id\", \"Name\", \"AreaId\", \"IsDefault\", \"CreatedAt\", \"UpdatedAt\") VALUES (1, 'Default Department', 1, 1, @now, @now) ON CONFLICT (\"Id\") DO NOTHING;"
            : "INSERT OR IGNORE INTO Departments (Id, Name, AreaId, IsDefault, CreatedAt, UpdatedAt) VALUES (1, 'Default Department', 1, 1, $now, $now);";

        using var cmd1 = connection.CreateCommand();
        cmd1.CommandText = areaInsert;
        var p1 = cmd1.CreateParameter();
        p1.ParameterName = _usePostgreSql ? "@now" : "$now";
        p1.Value = now;
        cmd1.Parameters.Add(p1);
        cmd1.ExecuteNonQuery();

        using var cmd2 = connection.CreateCommand();
        cmd2.CommandText = deptInsert;
        var p2 = cmd2.CreateParameter();
        p2.ParameterName = _usePostgreSql ? "@now" : "$now";
        p2.Value = now;
        cmd2.Parameters.Add(p2);
        cmd2.ExecuteNonQuery();
    }

    // ── Area CRUD ────────────────────────────────────────────────────────────

    public IReadOnlyList<Area> GetAreas()
    {
        const string sql = "SELECT Id, Name, IsDefault, CreatedAt, UpdatedAt FROM Areas ORDER BY Id;";
        return Query(sql, reader => new Area
        {
            Id = ReadInt32(reader, 0),
            Name = reader.GetString(1),
            IsDefault = ReadInt32(reader, 2) == 1,
            CreatedAt = reader.IsDBNull(3) ? DateTime.UtcNow : ReadDateTime(reader, 3),
            UpdatedAt = reader.IsDBNull(4) ? DateTime.UtcNow : ReadDateTime(reader, 4)
        });
    }

    public Area? GetAreaById(int areaId)
    {
        const string sql = "SELECT Id, Name, IsDefault, CreatedAt, UpdatedAt FROM Areas WHERE Id = $id LIMIT 1;";
        return QuerySingle(sql,
            cmd => cmd.Parameters.AddWithValue("$id", areaId),
            reader => new Area
            {
                Id = ReadInt32(reader, 0),
                Name = reader.GetString(1),
                IsDefault = ReadInt32(reader, 2) == 1,
                CreatedAt = reader.IsDBNull(3) ? DateTime.UtcNow : ReadDateTime(reader, 3),
                UpdatedAt = reader.IsDBNull(4) ? DateTime.UtcNow : ReadDateTime(reader, 4)
            });
    }

    public Area AddArea(Area area)
    {
        var nowDt = DateTime.UtcNow; var now = nowDt.ToString("O");
        // Auto-generate ID via AUTOINCREMENT; return inserted row
        var sql = _usePostgreSql
            ? "INSERT INTO Areas (\"Name\", \"IsDefault\", \"CreatedAt\", \"UpdatedAt\") VALUES ($name, $isDefault, $createdAt, $updatedAt);"
            : "INSERT INTO Areas (Name, IsDefault, CreatedAt, UpdatedAt) VALUES ($name, $isDefault, $createdAt, $updatedAt);";
        Execute(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("$name", area.Name.Trim());
            cmd.Parameters.AddWithValue("$isDefault", area.IsDefault ? 1 : 0);
            cmd.Parameters.AddWithValue("$createdAt", DateParam(nowDt));
            cmd.Parameters.AddWithValue("$updatedAt", DateParam(nowDt));
        });

        // Fetch back the inserted row
        var inserted = QuerySingle("SELECT Id, Name, IsDefault, CreatedAt, UpdatedAt FROM Areas WHERE Name = $name LIMIT 1;",
            cmd => cmd.Parameters.AddWithValue("$name", area.Name.Trim()),
            reader => new Area
            {
                Id = ReadInt32(reader, 0),
                Name = reader.GetString(1),
                IsDefault = ReadInt32(reader, 2) == 1,
                CreatedAt = reader.IsDBNull(3) ? DateTime.UtcNow : ReadDateTime(reader, 3),
                UpdatedAt = reader.IsDBNull(4) ? DateTime.UtcNow : ReadDateTime(reader, 4)
            }) ?? area;

        // Auto-create default department for the new area
        AddDepartment(new Department
        {
            Name = $"{inserted.Name} Department",
            AreaId = inserted.Id,
            IsDefault = false
        });

        return inserted;
    }

    public bool UpdateArea(Area area)
    {
        if (area.Id == 1 && area.IsDefault) { /* name can be changed, just not deleted */ }
        var nowDt = DateTime.UtcNow; var now = nowDt.ToString("O");
        const string sql = "UPDATE Areas SET Name = $name, UpdatedAt = $updatedAt WHERE Id = $id;";
        var rows = Execute(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("$id", area.Id);
            cmd.Parameters.AddWithValue("$name", area.Name.Trim());
            cmd.Parameters.AddWithValue("$updatedAt", DateParam(nowDt));
        });
        return rows > 0;
    }

    public bool DeleteArea(int areaId)
    {
        if (areaId == 1) return false; // Default Area is protected
        var rows = Execute("DELETE FROM Areas WHERE Id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", areaId));
        return rows > 0;
    }

    // ── Department CRUD ──────────────────────────────────────────────────────

    public IReadOnlyList<Department> GetDepartments()
    {
        const string sql = "SELECT Id, Name, AreaId, IsDefault, CreatedAt, UpdatedAt FROM Departments ORDER BY AreaId, Id;";
        return Query(sql, reader => new Department
        {
            Id = ReadInt32(reader, 0),
            Name = reader.GetString(1),
            AreaId = ReadInt32(reader, 2),
            IsDefault = ReadInt32(reader, 3) == 1,
            CreatedAt = reader.IsDBNull(4) ? DateTime.UtcNow : ReadDateTime(reader, 4),
            UpdatedAt = reader.IsDBNull(5) ? DateTime.UtcNow : ReadDateTime(reader, 5)
        });
    }

    public IReadOnlyList<Department> GetDepartmentsByArea(int areaId)
    {
        const string sql = "SELECT Id, Name, AreaId, IsDefault, CreatedAt, UpdatedAt FROM Departments WHERE AreaId = $areaId ORDER BY Id;";
        return Query(sql,
            cmd => cmd.Parameters.AddWithValue("$areaId", areaId),
            reader => new Department
            {
                Id = ReadInt32(reader, 0),
                Name = reader.GetString(1),
                AreaId = ReadInt32(reader, 2),
                IsDefault = ReadInt32(reader, 3) == 1,
                CreatedAt = reader.IsDBNull(4) ? DateTime.UtcNow : ReadDateTime(reader, 4),
                UpdatedAt = reader.IsDBNull(5) ? DateTime.UtcNow : ReadDateTime(reader, 5)
            });
    }

    public Department? GetDepartmentById(int departmentId)
    {
        const string sql = "SELECT Id, Name, AreaId, IsDefault, CreatedAt, UpdatedAt FROM Departments WHERE Id = $id LIMIT 1;";
        return QuerySingle(sql,
            cmd => cmd.Parameters.AddWithValue("$id", departmentId),
            reader => new Department
            {
                Id = ReadInt32(reader, 0),
                Name = reader.GetString(1),
                AreaId = ReadInt32(reader, 2),
                IsDefault = ReadInt32(reader, 3) == 1,
                CreatedAt = reader.IsDBNull(4) ? DateTime.UtcNow : ReadDateTime(reader, 4),
                UpdatedAt = reader.IsDBNull(5) ? DateTime.UtcNow : ReadDateTime(reader, 5)
            });
    }

    public Department AddDepartment(Department department)
    {
        var nowDt = DateTime.UtcNow; var now = nowDt.ToString("O");
        const string sql = """
            INSERT INTO Departments (Name, AreaId, IsDefault, CreatedAt, UpdatedAt)
            VALUES ($name, $areaId, $isDefault, $createdAt, $updatedAt);
            """;
        Execute(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("$name", department.Name.Trim());
            cmd.Parameters.AddWithValue("$areaId", department.AreaId);
            cmd.Parameters.AddWithValue("$isDefault", department.IsDefault ? 1 : 0);
            cmd.Parameters.AddWithValue("$createdAt", DateParam(nowDt));
            cmd.Parameters.AddWithValue("$updatedAt", DateParam(nowDt));
        });

        return QuerySingle("SELECT Id, Name, AreaId, IsDefault, CreatedAt, UpdatedAt FROM Departments WHERE AreaId = $areaId AND Name = $name LIMIT 1;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$areaId", department.AreaId);
                cmd.Parameters.AddWithValue("$name", department.Name.Trim());
            },
            reader => new Department
            {
                Id = ReadInt32(reader, 0),
                Name = reader.GetString(1),
                AreaId = ReadInt32(reader, 2),
                IsDefault = ReadInt32(reader, 3) == 1,
                CreatedAt = reader.IsDBNull(4) ? DateTime.UtcNow : ReadDateTime(reader, 4),
                UpdatedAt = reader.IsDBNull(5) ? DateTime.UtcNow : ReadDateTime(reader, 5)
            }) ?? department;
    }

    public bool UpdateDepartment(Department department)
    {
        var nowDt = DateTime.UtcNow; var now = nowDt.ToString("O");
        const string sql = "UPDATE Departments SET Name = $name, UpdatedAt = $updatedAt WHERE Id = $id;";
        var rows = Execute(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("$id", department.Id);
            cmd.Parameters.AddWithValue("$name", department.Name.Trim());
            cmd.Parameters.AddWithValue("$updatedAt", DateParam(nowDt));
        });
        return rows > 0;
    }

    public bool DeleteDepartment(int departmentId)
    {
        if (departmentId == 1) return false; // Default Department is protected
        var rows = Execute("DELETE FROM Departments WHERE Id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", departmentId));
        return rows > 0;
    }

    // ── EmployeeDepartments ──────────────────────────────────────────────────

    public IReadOnlyList<EmployeeDepartment> GetEmployeeDepartments(Guid employeeId)
    {
        const string sql = "SELECT EmployeeId, DepartmentId, AssignedAt, AssignedBy FROM EmployeeDepartments WHERE EmployeeId = $empId;";
        return Query(sql,
            cmd => cmd.Parameters.AddWithValue("$empId", GuidParam(employeeId)),
            reader => new EmployeeDepartment
            {
                EmployeeId = ReadGuid(reader, 0),
                DepartmentId = ReadInt32(reader, 1),
                AssignedAt = reader.IsDBNull(2) ? DateTime.UtcNow : ReadDateTime(reader, 2),
                AssignedBy = reader.IsDBNull(3) ? null : ReadGuid(reader, 3)
            });
    }

    public IReadOnlyList<EmployeeDepartment> GetDepartmentEmployees(int departmentId)
    {
        const string sql = "SELECT EmployeeId, DepartmentId, AssignedAt, AssignedBy FROM EmployeeDepartments WHERE DepartmentId = $deptId;";
        return Query(sql,
            cmd => cmd.Parameters.AddWithValue("$deptId", departmentId),
            reader => new EmployeeDepartment
            {
                EmployeeId = ReadGuid(reader, 0),
                DepartmentId = ReadInt32(reader, 1),
                AssignedAt = reader.IsDBNull(2) ? DateTime.UtcNow : ReadDateTime(reader, 2),
                AssignedBy = reader.IsDBNull(3) ? null : ReadGuid(reader, 3)
            });
    }

    public bool AddEmployeeDepartment(EmployeeDepartment ed)
    {
        var sql = _usePostgreSql
            ? """
                INSERT INTO EmployeeDepartments (EmployeeId, DepartmentId, AssignedAt, AssignedBy)
                VALUES ($empId, $deptId, $assignedAt, $assignedBy)
                ON CONFLICT (EmployeeId, DepartmentId) DO NOTHING;
                """
            : """
                INSERT OR IGNORE INTO EmployeeDepartments (EmployeeId, DepartmentId, AssignedAt, AssignedBy)
                VALUES ($empId, $deptId, $assignedAt, $assignedBy);
                """;
        var rows = Execute(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("$empId", GuidParam(ed.EmployeeId));
            cmd.Parameters.AddWithValue("$deptId", ed.DepartmentId);
            cmd.Parameters.AddWithValue("$assignedAt", DateParam(ed.AssignedAt));
            cmd.Parameters.AddWithValue("$assignedBy", ed.AssignedBy.HasValue ? GuidParam(ed.AssignedBy.Value) : (object)DBNull.Value);
        });
        return rows > 0;
    }

    public bool RemoveEmployeeDepartment(Guid employeeId, int departmentId)
    {
        const string sql = "DELETE FROM EmployeeDepartments WHERE EmployeeId = $empId AND DepartmentId = $deptId;";
        var rows = Execute(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("$empId", GuidParam(employeeId));
            cmd.Parameters.AddWithValue("$deptId", departmentId);
        });
        return rows > 0;
    }

    public bool RemoveAllEmployeeDepartments(Guid employeeId)
    {
        const string sql = "DELETE FROM EmployeeDepartments WHERE EmployeeId = $empId;";
        var rows = Execute(sql, cmd => cmd.Parameters.AddWithValue("$empId", GuidParam(employeeId)));
        return rows > 0;
    }

    public bool ValidateEmployeeSameArea(Guid employeeId, int newDepartmentId, int newAreaId)
    {
        // Check that all existing departments of the employee share the same AreaId as newAreaId
        const string sql = """
            SELECT COUNT(*) FROM EmployeeDepartments ed
            JOIN Departments d ON ed.DepartmentId = d.Id
            WHERE ed.EmployeeId = $empId AND d.AreaId != $areaId;
            """;
        var conflictCount = 0;
        var connection = AcquireConnection(out var owns);
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = NormalizeSql(sql);
            cmd.Parameters.AddWithValue("$empId", GuidParam(employeeId));
            cmd.Parameters.AddWithValue("$areaId", newAreaId);
            NormalizeParameters(cmd);
            var result = cmd.ExecuteScalar();
            conflictCount = Convert.ToInt32(result);
        }
        finally { if (owns) connection.Dispose(); }
        return conflictCount == 0;
    }

    // ── Employee by Area / Department ────────────────────────────────────────

    public IReadOnlyList<Employee> GetEmployeesByArea(int areaId)
    {
        const string sql = """
            SELECT Id, EmployeeCode, FullName, Area, Department, DepartmentId, DepartmentsJson, Position, CardNumber, Password, MobileNumber, Gender, Privilege, VerifyMode, FaceTemplateVisible, FaceTemplateInfrared, ProfilePhoto, FingerprintTemplates, CreatedAt, SyncedAt, IsActive, AreaId
            FROM Employees WHERE AreaId = $areaId AND IsActive = 1 ORDER BY EmployeeCode;
            """;
        return Query(sql,
            cmd => cmd.Parameters.AddWithValue("$areaId", areaId),
            reader => MapEmployee(reader));
    }

    public IReadOnlyList<Employee> GetEmployeesByDepartment(int departmentId)
    {
        const string sql = """
            SELECT e.Id, e.EmployeeCode, e.FullName, e.Area, e.Department, e.DepartmentId, e.DepartmentsJson, e.Position, e.CardNumber, e.Password, e.MobileNumber, e.Gender, e.Privilege, e.VerifyMode, e.FaceTemplateVisible, e.FaceTemplateInfrared, e.ProfilePhoto, e.FingerprintTemplates, e.CreatedAt, e.SyncedAt, e.IsActive, e.AreaId
            FROM Employees e
            JOIN EmployeeDepartments ed ON ed.EmployeeId = e.Id
            WHERE ed.DepartmentId = $deptId AND e.IsActive = 1
            ORDER BY e.EmployeeCode;
            """;
        return Query(sql,
            cmd => cmd.Parameters.AddWithValue("$deptId", departmentId),
            reader => MapEmployee(reader));
    }

    // ── Device by Area/Department ────────────────────────────────────────────

    public IReadOnlyList<Device> GetDevicesByAreaDepartment(int areaId, int departmentId)
    {
        const string sql = """
            SELECT Id, SerialNumber, Name, Model, IpAddress, Location, TimeZone, SupportsVisibleLightFace, SupportsInfraredFace, SupportsFingerprint, FaceAlgorithmVersion, FingerprintAlgorithmVersion,
                   FirmwareVersion, DeviceType, PushVer, Language, MultiBioDataSupport, MultiBioPhotoSupport, MultiBioVersion, MaxMultiBioDataCount, MaxMultiBioPhotoCount,
                   FingerFunOn, FaceFunOn, VisiLightFunOn, BioDataFun, BioPhotoFun, SupportsPalm, SupportsFingerVein, CapabilitiesJson, CreatedAtUtc, UpdatedAtUtc, Status, IsRegistrationDevice, IsOnline, LastSeenUtc, AreaId, DepartmentId, IsRegistered
            FROM Devices WHERE AreaId = $areaId AND DepartmentId = $deptId ORDER BY SerialNumber;
            """;
        return Query(sql,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$areaId", areaId);
                cmd.Parameters.AddWithValue("$deptId", departmentId);
            },
            reader => MapDevice(reader));
    }

    // ── Attendance by Area/Department ────────────────────────────────────────

    public IReadOnlyList<AttendanceRecord> GetAttendanceByAreaDepartment(int areaId, int departmentId, DateOnly? from, DateOnly? to)
    {
        var sql = """
            SELECT Id, EmployeeId, EmployeeCode, Timestamp, Status, StatusCode, VerifyMode, Workcode, DeviceId, DeviceSerialNumber, AreaId, DepartmentId
            FROM AttendanceRecords
            WHERE AreaId = $areaId AND DepartmentId = $deptId
            """ +
            (from.HasValue ? $" AND Timestamp >= {SqlTs("$from")}" : "") +
            (to.HasValue ? $" AND Timestamp <= {SqlTs("$to")}" : "") +
            " ORDER BY Timestamp DESC;";

        return Query(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("$areaId", areaId);
            cmd.Parameters.AddWithValue("$deptId", departmentId);
            if (from.HasValue) cmd.Parameters.AddWithValue("$from", from.Value.ToString("yyyy-MM-dd"));
            if (to.HasValue) cmd.Parameters.AddWithValue("$to", to.Value.ToString("yyyy-MM-dd") + " 23:59:59");
        }, reader => MapAttendanceRecord(reader));
    }

    // ── SyncQueue ────────────────────────────────────────────────────────────

    public SyncQueueEntry EnqueueSync(SyncQueueEntry entry)
    {
        var nowDt = DateTime.UtcNow; var now = nowDt.ToString("O");
        const string sql = """
            INSERT INTO SyncQueue (EntityType, EntityId, Operation, Status, RetryCount, MaxRetries, Payload, CreatedAt)
            VALUES ($entityType, $entityId, $operation, $status, 0, $maxRetries, $payload, $createdAt);
            """;
        Execute(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("$entityType", entry.EntityType);
            cmd.Parameters.AddWithValue("$entityId", entry.EntityId);
            cmd.Parameters.AddWithValue("$operation", entry.Operation);
            cmd.Parameters.AddWithValue("$status", (int)entry.Status);
            cmd.Parameters.AddWithValue("$maxRetries", entry.MaxRetries);
            cmd.Parameters.AddWithValue("$payload", entry.Payload ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$createdAt", DateParam(nowDt));
        });
        return entry;
    }

    public IReadOnlyList<SyncQueueEntry> GetPendingSync(int maxCount = 100)
    {
        const string sql = "SELECT Id, EntityType, EntityId, Operation, Status, RetryCount, MaxRetries, Payload, ErrorMessage, CreatedAt, ProcessedAt, NextRetryAt FROM SyncQueue WHERE Status = 0 ORDER BY CreatedAt LIMIT $max;";
        return Query(sql,
            cmd => cmd.Parameters.AddWithValue("$max", maxCount),
            reader => MapSyncQueueEntry(reader));
    }

    public IReadOnlyList<SyncQueueEntry> GetFailedSync(int maxCount = 100)
    {
        const string sql = "SELECT Id, EntityType, EntityId, Operation, Status, RetryCount, MaxRetries, Payload, ErrorMessage, CreatedAt, ProcessedAt, NextRetryAt FROM SyncQueue WHERE Status = 3 ORDER BY CreatedAt LIMIT $max;";
        return Query(sql,
            cmd => cmd.Parameters.AddWithValue("$max", maxCount),
            reader => MapSyncQueueEntry(reader));
    }

    public bool UpdateSyncQueueStatus(long syncQueueId, SyncQueueStatus status, string? errorMessage = null)
    {
        var nowDt = DateTime.UtcNow; var now = nowDt.ToString("O");
        const string sql = "UPDATE SyncQueue SET Status = $status, ErrorMessage = $err, ProcessedAt = $processedAt WHERE Id = $id;";
        var rows = Execute(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("$id", syncQueueId);
            cmd.Parameters.AddWithValue("$status", (int)status);
            cmd.Parameters.AddWithValue("$err", errorMessage ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$processedAt", DateParam(nowDt));
        });
        return rows > 0;
    }

    public bool IncrementSyncRetryCount(long syncQueueId)
    {
        const string sql = "UPDATE SyncQueue SET RetryCount = RetryCount + 1 WHERE Id = $id;";
        var rows = Execute(sql, cmd => cmd.Parameters.AddWithValue("$id", syncQueueId));
        return rows > 0;
    }

    public int ProcessSyncQueue(int batchSize = 50) => 0; // Hook for background processor

    public int RetryFailedSync(int maxRetries = 3)
    {
        const string sql = "UPDATE SyncQueue SET Status = 0 WHERE Status = 3 AND RetryCount < $maxRetries;";
        return Execute(sql, cmd => cmd.Parameters.AddWithValue("$maxRetries", maxRetries));
    }

    // ── Private mappers ──────────────────────────────────────────────────────

    private static Employee MapEmployee(DbDataReader reader) => new Employee
    {
        Id = ReadGuid(reader, 0),
        EmployeeCode = reader.GetString(1),
        FullName = reader.GetString(2),
        Area = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
        Department = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
        DepartmentId = reader.IsDBNull(5) ? 1 : ReadInt32(reader, 5),
        Departments = DeserializeDepartments(reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(4) ? string.Empty : reader.GetString(4)),
        Position = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
        CardNumber = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
        Password = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
        MobileNumber = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
        Gender = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
        Privilege = reader.IsDBNull(12) ? UserPrivilege.User : (UserPrivilege)ReadInt32(reader, 12),
        VerifyMode = reader.IsDBNull(13) ? 0 : ReadInt32(reader, 13),
        FaceTemplateVisible = reader.IsDBNull(14) ? null : reader.GetString(14),
        FaceTemplateInfrared = reader.IsDBNull(15) ? null : reader.GetString(15),
        ProfilePhoto = reader.IsDBNull(16) ? null : reader.GetString(16),
        FingerprintTemplates = reader.IsDBNull(17) ? null : reader.GetString(17),
        CreatedAt = reader.IsDBNull(18) ? DateTime.UtcNow : ReadDateTime(reader, 18),
        SyncedAt = reader.IsDBNull(19) ? null : ReadDateTime(reader, 19),
        IsActive = ReadInt32(reader, 20) == 1,
        AreaId = reader.IsDBNull(21) ? 1 : ReadInt32(reader, 21)
    };

    private static Device MapDevice(DbDataReader reader) => new Device
    {
        Id = ReadGuid(reader, 0),
        SerialNumber = reader.GetString(1),
        Name = reader.GetString(2),
        Model = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
        IpAddress = reader.GetString(4),
        Location = reader.GetString(5),
        TimeZone = reader.IsDBNull(6) ? "+5:45" : reader.GetString(6),
        SupportsVisibleLightFace = !reader.IsDBNull(7) && ReadInt32(reader, 7) == 1,
        SupportsInfraredFace = !reader.IsDBNull(8) && ReadInt32(reader, 8) == 1,
        SupportsFingerprint = !reader.IsDBNull(9) && ReadInt32(reader, 9) == 1,
        FaceAlgorithmVersion = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
        FingerprintAlgorithmVersion = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
        FirmwareVersion = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
        DeviceType = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
        PushVer = reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
        Language = reader.IsDBNull(15) ? 0 : ReadInt32(reader, 15),
        MultiBioDataSupport = reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
        MultiBioPhotoSupport = reader.IsDBNull(17) ? string.Empty : reader.GetString(17),
        MultiBioVersion = reader.IsDBNull(18) ? string.Empty : reader.GetString(18),
        MaxMultiBioDataCount = reader.IsDBNull(19) ? string.Empty : reader.GetString(19),
        MaxMultiBioPhotoCount = reader.IsDBNull(20) ? string.Empty : reader.GetString(20),
        FingerFunOn = !reader.IsDBNull(21) && ReadInt32(reader, 21) == 1,
        FaceFunOn = !reader.IsDBNull(22) && ReadInt32(reader, 22) == 1,
        VisiLightFunOn = !reader.IsDBNull(23) && ReadInt32(reader, 23) == 1,
        BioDataFun = !reader.IsDBNull(24) && ReadInt32(reader, 24) == 1,
        BioPhotoFun = !reader.IsDBNull(25) && ReadInt32(reader, 25) == 1,
        SupportsPalm = !reader.IsDBNull(26) && ReadInt32(reader, 26) == 1,
        SupportsFingerVein = !reader.IsDBNull(27) && ReadInt32(reader, 27) == 1,
        CapabilitiesJson = reader.IsDBNull(28) ? "{}" : reader.GetString(28),
        CreatedAtUtc = reader.IsDBNull(29) ? DateTime.UtcNow : ReadDateTime(reader, 29),
        UpdatedAtUtc = reader.IsDBNull(30) ? DateTime.UtcNow : ReadDateTime(reader, 30),
        Status = reader.IsDBNull(31) ? DeviceStatus.Offline : (DeviceStatus)ReadInt32(reader, 31),
        IsRegistrationDevice = !reader.IsDBNull(32) && ReadInt32(reader, 32) == 1,
        IsOnline = ReadInt32(reader, 33) == 1,
        LastSeenUtc = reader.IsDBNull(34) ? null : ReadDateTime(reader, 34),
        AreaId = reader.IsDBNull(35) ? 1 : ReadInt32(reader, 35),
        DepartmentId = reader.IsDBNull(36) ? 1 : ReadInt32(reader, 36),
        IsRegistered = !reader.IsDBNull(37) && ReadInt32(reader, 37) == 1
    };

    private static AttendanceRecord MapAttendanceRecord(DbDataReader reader) => new AttendanceRecord
    {
        Id = ReadGuid(reader, 0),
        EmployeeId = ReadGuid(reader, 1),
        EmployeeCode = reader.GetString(2),
        Timestamp = reader.IsDBNull(3) ? DateTime.UtcNow : ReadDateTime(reader, 3),
        Status = reader.IsDBNull(4) ? AttendanceStatus.Unknown : (AttendanceStatus)ReadInt32(reader, 4),
        StatusCode = reader.IsDBNull(5) ? null : ReadInt32(reader, 5),
        VerifyMode = reader.IsDBNull(6) ? null : ReadInt32(reader, 6),
        Workcode = reader.IsDBNull(7) ? null : reader.GetString(7),
        DeviceId = reader.IsDBNull(8) ? null : ReadGuid(reader, 8),
        DeviceSerialNumber = reader.IsDBNull(9) ? null : reader.GetString(9),
        AreaId = reader.IsDBNull(10) ? 1 : ReadInt32(reader, 10),
        DepartmentId = reader.IsDBNull(11) ? 1 : ReadInt32(reader, 11)
    };

    private static SyncQueueEntry MapSyncQueueEntry(DbDataReader reader) => new SyncQueueEntry
    {
        Id = ReadInt32(reader, 0),
        EntityType = reader.GetString(1),
        EntityId = reader.GetString(2),
        Operation = reader.GetString(3),
        Status = (SyncQueueStatus)ReadInt32(reader, 4),
        RetryCount = reader.IsDBNull(5) ? 0 : ReadInt32(reader, 5),
        MaxRetries = reader.IsDBNull(6) ? 3 : ReadInt32(reader, 6),
        Payload = reader.IsDBNull(7) ? null : reader.GetString(7),
        ErrorMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
        CreatedAt = reader.IsDBNull(9) ? DateTime.UtcNow : ReadDateTime(reader, 9),
        ProcessedAt = reader.IsDBNull(10) ? null : ReadDateTime(reader, 10),
        NextRetryAt = reader.IsDBNull(11) ? null : ReadDateTime(reader, 11)
    };

    private static string SerializeDepartments(IEnumerable<string> departments)
    {
        var normalized = departments
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(normalized);
    }

    private static List<string> DeserializeDepartments(string? departmentsJson, string primaryDepartment)
    {
        if (!string.IsNullOrWhiteSpace(departmentsJson))
        {
            try
            {
                var values = JsonSerializer.Deserialize<List<string>>(departmentsJson);
                if (values is { Count: > 0 })
                {
                    return values
                        .Where(static value => !string.IsNullOrWhiteSpace(value))
                        .Select(static value => value.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }
            catch (JsonException)
            {
            }
        }

        if (string.IsNullOrWhiteSpace(primaryDepartment))
        {
            return [];
        }

        return [primaryDepartment.Trim()];
    }

    private static bool LooksLikePostgresConnectionString(string value)
    {
        return value.Contains("Host=", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Port=", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Username=", StringComparison.OrdinalIgnoreCase);
    }

    public DbConnection AcquireConnection(out bool ownsConnection)
    {
        var ambient = AmbientConnection.Value;
        if (ambient is not null)
        {
            if (ambient.State != ConnectionState.Open)
            {
                OpenConnection(ambient);
            }
            ownsConnection = false;
            return ambient;
        }

        var connection = CreateConnection();
        OpenConnection(connection);
        ownsConnection = true;
        return connection;
    }

    private static DbTransaction? AcquireTransaction(DbConnection connection)
    {
        var tx = AmbientTransaction.Value;
        if (tx is null)
        {
            return null;
        }

        return ReferenceEquals(tx.Connection, connection) ? tx : null;
    }

    private DbConnection CreateConnection()
    {
        return _usePostgreSql
            ? new NpgsqlConnection(_connectionString)
            : new SqliteConnection(_connectionString);
    }

    private void OpenConnection(DbConnection connection)
    {
        connection.Open();
        if (!_usePostgreSql)
        {
            return;
        }

        // Force schema at session level so all unqualified objects resolve to app schema only.
        using var schemaCmd = connection.CreateCommand();
        schemaCmd.CommandText = $"SET search_path TO \"{PostgresAppSchema}\";";
        schemaCmd.ExecuteNonQuery();
    }

    public string NormalizeSql(string sql)
    {
        if (!_usePostgreSql)
        {
            return sql;
        }

        var normalized = sql.Replace("$", "@", StringComparison.Ordinal)
            .Replace("COLLATE NOCASE", string.Empty, StringComparison.OrdinalIgnoreCase);

        foreach (var identifier in PostgresIdentifiers)
        {
            normalized = Regex.Replace(
                normalized,
                $@"(?<![""])\b{Regex.Escape(identifier)}\b(?![""])",
                $"\"{identifier}\"",
                RegexOptions.CultureInvariant);
        }

        foreach (var table in new[] { "Employees", "Devices", "AttendanceRecords", "DeviceCommands", "SyncLogs", "DeviceTableStamps", "Areas", "Departments", "EmployeeDepartments", "SyncQueue" })
        {
            normalized = Regex.Replace(
                normalized,
                $@"(?<!\.)""{Regex.Escape(table)}""",
                $@"""{PostgresAppSchema}"".""{table}""",
                RegexOptions.CultureInvariant);
        }

        return normalized;
    }

    public void NormalizeParameters(DbCommand command)
    {
        if (!_usePostgreSql)
        {
            return;
        }

        foreach (DbParameter parameter in command.Parameters)
        {
            if (parameter.ParameterName.StartsWith("$", StringComparison.Ordinal))
            {
                parameter.ParameterName = "@" + parameter.ParameterName[1..];
            }
        }
    }

    private void EnsureColumn(DbConnection connection, string tableName, string columnName, string alterSql)
    {
        var existsSql = _usePostgreSql
            ? "SELECT 1 FROM information_schema.columns WHERE lower(table_schema) = lower(current_schema()) AND lower(table_name) = lower(@table) AND lower(column_name) = lower(@column) LIMIT 1;"
            : $"PRAGMA table_info({tableName});";

        using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = existsSql;
        if (_usePostgreSql)
        {
            var table = checkCmd.CreateParameter();
            table.ParameterName = "@table";
            table.Value = tableName;
            checkCmd.Parameters.Add(table);

            var column = checkCmd.CreateParameter();
            column.ParameterName = "@column";
            column.Value = columnName;
            checkCmd.Parameters.Add(column);

            using var reader = checkCmd.ExecuteReader();
            if (reader.Read())
            {
                return;
            }
        }
        else
        {
            using var reader = checkCmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        using var alterCmd = connection.CreateCommand();
        alterCmd.CommandText = NormalizeSql(alterSql);
        alterCmd.ExecuteNonQuery();
    }

    public List<T> Query<T>(string sql, Func<DbDataReader, T> map)
    {
        var connection = AcquireConnection(out var ownsConnection);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = NormalizeSql(sql);
            var tx = AcquireTransaction(connection);
            if (tx is not null)
            {
                command.Transaction = tx;
            }
            NormalizeParameters(command);
            using var reader = command.ExecuteReader();

            var items = new List<T>();
            while (reader.Read())
            {
                items.Add(map(reader));
            }

            return items;
        }
        finally
        {
            if (ownsConnection)
            {
                connection.Dispose();
            }
        }
    }

    public void Query(string sql, Action<DbCommand> configure, Action<DbDataReader> process)
    {
        var connection = AcquireConnection(out var ownsConnection);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = NormalizeSql(sql);
            configure(command);
            var tx = AcquireTransaction(connection);
            if (tx is not null) command.Transaction = tx;
            NormalizeParameters(command);
            using var reader = command.ExecuteReader();
            while (reader.Read()) process(reader);
        }
        finally { if (ownsConnection) connection.Dispose(); }
    }

    public List<T> Query<T>(string sql, Action<DbCommand> configure, Func<DbDataReader, T> map)
    {
        var connection = AcquireConnection(out var ownsConnection);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = NormalizeSql(sql);
            configure(command);
            var tx = AcquireTransaction(connection);
            if (tx is not null)
            {
                command.Transaction = tx;
            }
            NormalizeParameters(command);
            using var reader = command.ExecuteReader();

            var items = new List<T>();
            while (reader.Read())
            {
                items.Add(map(reader));
            }

            return items;
        }
        finally
        {
            if (ownsConnection)
            {
                connection.Dispose();
            }
        }
    }

    public T? QuerySingle<T>(string sql, Action<DbCommand> configure, Func<DbDataReader, T> map)
        where T : class
    {
        var connection = AcquireConnection(out var ownsConnection);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = NormalizeSql(sql);
            configure(command);
            var tx = AcquireTransaction(connection);
            if (tx is not null)
            {
                command.Transaction = tx;
            }
            NormalizeParameters(command);
            using var reader = command.ExecuteReader();

            if (reader.Read())
            {
                return map(reader);
            }

            return null;
        }
        finally
        {
            if (ownsConnection)
            {
                connection.Dispose();
            }
        }
    }

    public int Execute(string sql, Action<DbCommand> configure)
    {
        var connection = AcquireConnection(out var ownsConnection);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = NormalizeSql(sql);
            configure(command);
            var tx = AcquireTransaction(connection);
            if (tx is not null)
            {
                command.Transaction = tx;
            }
            NormalizeParameters(command);
            return command.ExecuteNonQuery();
        }
        finally
        {
            if (ownsConnection)
            {
                connection.Dispose();
            }
        }
    }

    // Guid → text. ID columns are TEXT in both SQLite and PostgreSQL, so no cast needed.
    private static string GuidParam(Guid value) => value.ToString();

    // SQL-level cast helpers.
    //  - SqlUuid: NO-OP. All ID columns are TEXT (matching the repository's own schema),
    //    so a plain text parameter is correct. (EnsurePostgresSchema converts any legacy
    //    uuid columns to TEXT on startup.)
    //  - SqlTs: cast text ISO string to timestamptz for PostgreSQL timestamp columns.
    //  - SqlBool: NO-OP. All boolean columns are INTEGER, integer params go in directly.
    private string SqlUuid(string param) => param;
    private string SqlTs(string param)   => _usePostgreSql ? $"CAST({param} AS timestamptz)" : param;
    private string SqlBool(string param) => param;

    // DateTime parameters.
    //  - PostgreSQL: bind a real UTC DateTime so Npgsql sends it as `timestamp with time zone`.
    //    This works whether or not the surrounding SQL wraps the param in SqlTs (CAST of an
    //    already-timestamptz value is a harmless no-op), so statements that forgot SqlTs still
    //    succeed instead of throwing "operator does not exist: timestamptz = text". Npgsql 6+
    //    requires Kind=Utc for timestamptz, so normalize first (all stored times are UTC).
    //  - SQLite: keep the ISO string form (dynamic typing; preserves the existing on-disk format).
    private object DateParam(DateTime value)
    {
        if (!_usePostgreSql)
        {
            return value.ToString("O");
        }

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }

    private object DateParam(DateTime? value) => value.HasValue ? DateParam(value.Value) : (object)DBNull.Value;

    public static Guid ReadGuid(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        if (value is Guid guid)
        {
            return guid;
        }

        if (value is string text && Guid.TryParse(text, out var parsed))
        {
            return parsed;
        }

        return Guid.Parse(Convert.ToString(value) ?? string.Empty);
    }

    public static DateTime ReadDateTime(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        if (value is DateTime dt)
        {
            return dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt;
        }

        if (value is string text && DateTime.TryParse(text, out var parsed))
        {
            return parsed;
        }

        if (value is string empty && string.IsNullOrWhiteSpace(empty))
        {
            return DateTime.UtcNow;
        }

        return Convert.ToDateTime(value);
    }

    public static int ReadInt32(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            int i => i,
            long l => checked((int)l),
            short s => s,
            byte b => b,
            bool b => b ? 1 : 0,
            decimal d => decimal.ToInt32(d),
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => Convert.ToInt32(value)
        };
    }
}

public static class DbParameterCollectionExtensions
{
    public static void AddWithValue(this DbParameterCollection parameters, string parameterName, object? value)
    {
        var v = value ?? DBNull.Value;

        if (parameters is SqliteParameterCollection sqlite)
        {
            // SQLite stores everything as text/blob — pass as-is
            sqlite.AddWithValue(parameterName, v);
            return;
        }

        if (parameters is NpgsqlParameterCollection npgsql)
        {
            // For PostgreSQL: use EXPLICIT typed NpgsqlParameter so Npgsql does NOT
            // fall back to 'text' inference for Guid/DateTime values boxed as object.
            // Without this, 'column "Id" is of type uuid but expression is of type text'
            // and 'column "CreatedAt" is of type timestamptz but expression is of type text'.

            // Normalise the parameter name ($foo → @foo already done by NormalizeSql,
            // but AddWithValue is sometimes called before normalisation — support both.)
            var pgName = parameterName.StartsWith("$", StringComparison.Ordinal)
                ? "@" + parameterName[1..]
                : parameterName;

            NpgsqlParameter param;

            switch (v)
            {
                case Guid g:
                    param = new NpgsqlParameter(pgName, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = g };
                    break;

                case DateTime dt:
                    param = new NpgsqlParameter(pgName, NpgsqlTypes.NpgsqlDbType.TimestampTz)
                    {
                        Value = DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                    };
                    break;

                case DateTimeOffset dto:
                    param = new NpgsqlParameter(pgName, NpgsqlTypes.NpgsqlDbType.TimestampTz)
                    {
                        Value = dto.ToUniversalTime()
                    };
                    break;

                case DBNull _:
                    param = new NpgsqlParameter(pgName, DBNull.Value);
                    break;

                default:
                    // bool, int, long, string, decimal, etc. — safe to infer
                    param = new NpgsqlParameter(pgName, v);
                    break;
            }

            npgsql.Add(param);
            return;
        }

        throw new NotSupportedException($"Unsupported parameter collection type: {parameters.GetType().Name}");
    }
}
