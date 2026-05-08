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
        "Id", "EmployeeCode", "FullName", "Area", "Department", "DepartmentsJson", "Position", "CardNumber", "Password", "MobileNumber", "Gender",
        "Privilege", "FaceTemplateVisible", "FaceTemplateInfrared", "ProfilePhoto", "FingerprintTemplates",
        "CreatedAt", "SyncedAt", "IsActive", "SerialNumber", "Name", "Model", "IpAddress", "Location",
        "TimeZone", "SupportsVisibleLightFace", "SupportsInfraredFace", "SupportsFingerprint",
        "FaceAlgorithmVersion", "FingerprintAlgorithmVersion", "FirmwareVersion", "DeviceType", "PushVer", "Language",
        "MultiBioDataSupport", "MultiBioPhotoSupport", "MultiBioVersion", "MaxMultiBioDataCount", "MaxMultiBioPhotoCount",
        "FingerFunOn", "FaceFunOn", "VisiLightFunOn", "BioDataFun", "BioPhotoFun", "SupportsPalm", "SupportsFingerVein",
        "CapabilitiesJson", "CreatedAtUtc", "UpdatedAtUtc",
        "Status", "IsRegistrationDevice", "IsOnline", "LastSeenUtc",
        "EmployeeId", "Timestamp", "StatusCode", "VerifyMode", "Workcode", "DeviceId", "DeviceSerialNumber",
        "TableName", "StampValue", "UpdatedUtc",
        "CommandText", "CreatedUtc", "SentUtc", "ReplyText", "Type", "ResponseText", "CompletedUtc"
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
            SELECT Id, EmployeeCode, FullName, Area, Department, DepartmentsJson, Position, CardNumber, Password, MobileNumber, Gender, Privilege, VerifyMode, FaceTemplateVisible, FaceTemplateInfrared, ProfilePhoto, FingerprintTemplates, CreatedAt, SyncedAt, IsActive
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
            Departments = DeserializeDepartments(reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(4) ? string.Empty : reader.GetString(4)),
            Position = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            CardNumber = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            Password = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            MobileNumber = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            Gender = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            Privilege = reader.IsDBNull(11) ? UserPrivilege.User : (UserPrivilege)ReadInt32(reader, 11),
            VerifyMode = reader.IsDBNull(12) ? 0 : ReadInt32(reader, 12),
            FaceTemplateVisible = reader.IsDBNull(13) ? null : reader.GetString(13),
            FaceTemplateInfrared = reader.IsDBNull(14) ? null : reader.GetString(14),
            ProfilePhoto = reader.IsDBNull(15) ? null : reader.GetString(15),
            FingerprintTemplates = reader.IsDBNull(16) ? null : reader.GetString(16),
            CreatedAt = reader.IsDBNull(17) ? DateTime.UtcNow : ReadDateTime(reader, 17),
            SyncedAt = reader.IsDBNull(18) ? null : ReadDateTime(reader, 18),
            IsActive = ReadInt32(reader, 19) == 1
        });
    }

    public Employee AddEmployee(Employee employee)
    {
        const string sql = """
            INSERT INTO Employees (Id, EmployeeCode, FullName, Area, Department, DepartmentsJson, Position, CardNumber, Password, MobileNumber, Gender, Privilege, VerifyMode, FaceTemplateVisible, FaceTemplateInfrared, ProfilePhoto, FingerprintTemplates, CreatedAt, SyncedAt, IsActive)
            VALUES ($id, $code, $name, $area, $dept, $departmentsJson, $position, $card, $password, $mobile, $gender, $privilege, $verifyMode, $faceVisible, $faceInfra, $photo, $fingerprints, $createdAt, $syncedAt, $active)
            """;

        Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$id", employee.Id.ToString());
            command.Parameters.AddWithValue("$code", employee.EmployeeCode);
            command.Parameters.AddWithValue("$name", employee.FullName);
            command.Parameters.AddWithValue("$area", employee.Area);
            command.Parameters.AddWithValue("$dept", employee.Department);
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
            command.Parameters.AddWithValue("$syncedAt", employee.SyncedAt?.ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$active", employee.IsActive ? 1 : 0);
        });

        return employee;
    }

    public bool UpdateEmployee(Employee employee)
    {
        const string sql = """
            UPDATE Employees
            SET EmployeeCode = $code,
                FullName = $name,
                Area = $area,
                Department = $dept,
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
                SyncedAt = $syncedAt,
                IsActive = $active
            WHERE Id = $id
            """;

        var rows = Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$id", employee.Id.ToString());
            command.Parameters.AddWithValue("$code", employee.EmployeeCode);
            command.Parameters.AddWithValue("$name", employee.FullName);
            command.Parameters.AddWithValue("$area", employee.Area);
            command.Parameters.AddWithValue("$dept", employee.Department);
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
            command.Parameters.AddWithValue("$syncedAt", employee.SyncedAt?.ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$active", employee.IsActive ? 1 : 0);
        });

        return rows > 0;
    }

    public bool DeleteEmployee(Guid employeeId)
    {
        const string sql = """
            DELETE FROM Employees
            WHERE Id = $id
            """;

        var rows = Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$id", employeeId.ToString());
        });

        return rows > 0;
    }

    public Employee? FindEmployeeByCode(string employeeCode)
    {
        const string sql = """
            SELECT Id, EmployeeCode, FullName, Area, Department, DepartmentsJson, Position, CardNumber, Password, MobileNumber, Gender, Privilege, VerifyMode, FaceTemplateVisible, FaceTemplateInfrared, ProfilePhoto, FingerprintTemplates, CreatedAt, SyncedAt, IsActive
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
            Departments = DeserializeDepartments(reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(4) ? string.Empty : reader.GetString(4)),
            Position = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            CardNumber = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            Password = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            MobileNumber = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            Gender = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            Privilege = reader.IsDBNull(11) ? UserPrivilege.User : (UserPrivilege)ReadInt32(reader, 11),
            VerifyMode = reader.IsDBNull(12) ? 0 : ReadInt32(reader, 12),
            FaceTemplateVisible = reader.IsDBNull(13) ? null : reader.GetString(13),
            FaceTemplateInfrared = reader.IsDBNull(14) ? null : reader.GetString(14),
            ProfilePhoto = reader.IsDBNull(15) ? null : reader.GetString(15),
            FingerprintTemplates = reader.IsDBNull(16) ? null : reader.GetString(16),
            CreatedAt = reader.IsDBNull(17) ? DateTime.UtcNow : ReadDateTime(reader, 17),
            SyncedAt = reader.IsDBNull(18) ? null : ReadDateTime(reader, 18),
            IsActive = ReadInt32(reader, 19) == 1
        });
    }

    public IReadOnlyList<Device> GetDevices()
    {
        const string sql = """
            SELECT Id, SerialNumber, Name, Model, IpAddress, Location, TimeZone, SupportsVisibleLightFace, SupportsInfraredFace, SupportsFingerprint, FaceAlgorithmVersion, FingerprintAlgorithmVersion,
                   FirmwareVersion, DeviceType, PushVer, Language, MultiBioDataSupport, MultiBioPhotoSupport, MultiBioVersion, MaxMultiBioDataCount, MaxMultiBioPhotoCount,
                   FingerFunOn, FaceFunOn, VisiLightFunOn, BioDataFun, BioPhotoFun, SupportsPalm, SupportsFingerVein, CapabilitiesJson, CreatedAtUtc, UpdatedAtUtc, Status, IsRegistrationDevice, IsOnline, LastSeenUtc
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
            LastSeenUtc = reader.IsDBNull(34) ? null : ReadDateTime(reader, 34)
        });
    }

    public Device AddDevice(Device device)
    {
        var capabilitiesValueToken = _usePostgreSql ? "CAST($capabilitiesJson AS jsonb)" : "$capabilitiesJson";
        var sql = $"""
            INSERT INTO Devices (Id, SerialNumber, Name, Model, IpAddress, Location, TimeZone, SupportsVisibleLightFace, SupportsInfraredFace, SupportsFingerprint, FaceAlgorithmVersion, FingerprintAlgorithmVersion,
                                 FirmwareVersion, DeviceType, PushVer, Language, MultiBioDataSupport, MultiBioPhotoSupport, MultiBioVersion, MaxMultiBioDataCount, MaxMultiBioPhotoCount,
                                 FingerFunOn, FaceFunOn, VisiLightFunOn, BioDataFun, BioPhotoFun, SupportsPalm, SupportsFingerVein, CapabilitiesJson, CreatedAtUtc, UpdatedAtUtc, Status, IsRegistrationDevice, IsOnline, LastSeenUtc)
            VALUES ($id, $sn, $name, $model, $ip, $loc, $timezone, $visible, $infra, $fp, $faceAlgo, $fpAlgo,
                    $firmware, $deviceType, $pushVer, $language, $multiBioDataSupport, $multiBioPhotoSupport, $multiBioVersion, $maxMultiBioDataCount, $maxMultiBioPhotoCount,
                    $fingerFunOn, $faceFunOn, $visiLightFunOn, $bioDataFun, $bioPhotoFun, $supportsPalm, $supportsFingerVein, {capabilitiesValueToken}, $createdAtUtc, $updatedAtUtc, $status, $isReg, $online, $lastSeen)
            """;

        Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$id", device.Id.ToString());
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
            command.Parameters.AddWithValue("$createdAtUtc", device.CreatedAtUtc == default ? DateTime.UtcNow : device.CreatedAtUtc);
            command.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow);
            command.Parameters.AddWithValue("$status", (int)device.Status);
            command.Parameters.AddWithValue("$isReg", device.IsRegistrationDevice ? 1 : 0);
            command.Parameters.AddWithValue("$online", device.IsOnline ? 1 : 0);
            command.Parameters.AddWithValue("$lastSeen", device.LastSeenUtc.HasValue ? device.LastSeenUtc.Value : DBNull.Value);
        });

        return device;
    }

    public Device? FindDeviceById(Guid deviceId)
    {
        const string sql = """
            SELECT Id, SerialNumber, Name, Model, IpAddress, Location, TimeZone, SupportsVisibleLightFace, SupportsInfraredFace, SupportsFingerprint, FaceAlgorithmVersion, FingerprintAlgorithmVersion,
                   FirmwareVersion, DeviceType, PushVer, Language, MultiBioDataSupport, MultiBioPhotoSupport, MultiBioVersion, MaxMultiBioDataCount, MaxMultiBioPhotoCount,
                   FingerFunOn, FaceFunOn, VisiLightFunOn, BioDataFun, BioPhotoFun, SupportsPalm, SupportsFingerVein, CapabilitiesJson, CreatedAtUtc, UpdatedAtUtc, Status, IsRegistrationDevice, IsOnline, LastSeenUtc
            FROM Devices
            WHERE Id = $id
            LIMIT 1
            """;

        return QuerySingle(sql, command =>
        {
            command.Parameters.AddWithValue("$id", deviceId.ToString());
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
            LastSeenUtc = reader.IsDBNull(34) ? null : ReadDateTime(reader, 34)
        });
    }

    public Device? FindDeviceBySerial(string serialNumber)
    {
        const string sql = """
            SELECT Id, SerialNumber, Name, Model, IpAddress, Location, TimeZone, SupportsVisibleLightFace, SupportsInfraredFace, SupportsFingerprint, FaceAlgorithmVersion, FingerprintAlgorithmVersion,
                   FirmwareVersion, DeviceType, PushVer, Language, MultiBioDataSupport, MultiBioPhotoSupport, MultiBioVersion, MaxMultiBioDataCount, MaxMultiBioPhotoCount,
                   FingerFunOn, FaceFunOn, VisiLightFunOn, BioDataFun, BioPhotoFun, SupportsPalm, SupportsFingerVein, CapabilitiesJson, CreatedAtUtc, UpdatedAtUtc, Status, IsRegistrationDevice, IsOnline, LastSeenUtc
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
            LastSeenUtc = reader.IsDBNull(34) ? null : ReadDateTime(reader, 34)
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
                UpdatedAtUtc = $updatedAtUtc,
                Status = $status,
                IsRegistrationDevice = $isReg,
                IsOnline = $online,
                LastSeenUtc = $lastSeen
            WHERE Id = $id
            """;

        var rows = Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$id", device.Id.ToString());
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
            command.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow);
            command.Parameters.AddWithValue("$status", (int)device.Status);
            command.Parameters.AddWithValue("$isReg", device.IsRegistrationDevice ? 1 : 0);
            command.Parameters.AddWithValue("$online", device.IsOnline ? 1 : 0);
            command.Parameters.AddWithValue("$lastSeen", device.LastSeenUtc.HasValue ? device.LastSeenUtc.Value : DBNull.Value);
        });

        return rows > 0;
    }

    public bool DeleteDevice(Guid deviceId)
    {
        const string sql = """
            DELETE FROM Devices
            WHERE Id = $id
            """;

        var rows = Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$id", deviceId.ToString());
        });

        return rows > 0;
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

        var now = DateTime.UtcNow.ToString("O");
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
            command.Parameters.AddWithValue("$validFrom", validFrom.HasValue ? validFrom.Value.ToString("O") : DBNull.Value);
            command.Parameters.AddWithValue("$validTo", validTo.HasValue ? validTo.Value.ToString("O") : DBNull.Value);
            command.Parameters.AddWithValue("$expiresEnabled", expiresEnabled.HasValue ? (expiresEnabled.Value ? 1 : 0) : DBNull.Value);
            command.Parameters.AddWithValue("$phone", phone ?? string.Empty);
            command.Parameters.AddWithValue("$gender", gender.HasValue ? gender.Value : DBNull.Value);
            command.Parameters.AddWithValue("$nation", nation.HasValue ? nation.Value : DBNull.Value);
            command.Parameters.AddWithValue("$idNumber", idNumber ?? string.Empty);
            command.Parameters.AddWithValue("$createdAt", now);
            command.Parameters.AddWithValue("$updatedAt", now);
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
        var templatesTable = _usePostgreSql ? "\"E_Attendace\".\"biometric_templates\"" : "biometric_templates";
        var sql = $"""
            INSERT INTO {templatesTable}
            (user_pin, type, no, idx, valid, duress, major_ver, minor_ver, format, template_data, created_at, updated_at)
            VALUES ($userPin, $type, $no, $idx, $valid, $duress, $majorVer, $minorVer, $format, $templateData, $createdAt, $updatedAt)
            ON CONFLICT (user_pin, type, no, idx) DO UPDATE SET
                valid = excluded.valid,
                duress = excluded.duress,
                major_ver = excluded.major_ver,
                minor_ver = excluded.minor_ver,
                format = excluded.format,
                template_data = excluded.template_data,
                updated_at = excluded.updated_at
            """;

        var now = DateTime.UtcNow.ToString("O");
        Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$userPin", userPin);
            command.Parameters.AddWithValue("$type", type);
            command.Parameters.AddWithValue("$no", no);
            command.Parameters.AddWithValue("$idx", idx);
            command.Parameters.AddWithValue("$valid", valid ? 1 : 0);
            command.Parameters.AddWithValue("$duress", duress ? 1 : 0);
            command.Parameters.AddWithValue("$majorVer", majorVer.HasValue ? majorVer.Value : DBNull.Value);
            command.Parameters.AddWithValue("$minorVer", minorVer.HasValue ? minorVer.Value : DBNull.Value);
            command.Parameters.AddWithValue("$format", format.HasValue ? format.Value : DBNull.Value);
            command.Parameters.AddWithValue("$templateData", templateData ?? string.Empty);
            command.Parameters.AddWithValue("$createdAt", now);
            command.Parameters.AddWithValue("$updatedAt", now);
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

        var now = DateTime.UtcNow.ToString("O");
        Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$userPin", userPin);
            command.Parameters.AddWithValue("$type", type);
            command.Parameters.AddWithValue("$no", no);
            command.Parameters.AddWithValue("$idx", idx);
            command.Parameters.AddWithValue("$format", format.HasValue ? format.Value : DBNull.Value);
            command.Parameters.AddWithValue("$content", content ?? string.Empty);
            command.Parameters.AddWithValue("$createdAt", now);
            command.Parameters.AddWithValue("$updatedAt", now);
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
            SELECT Id, EmployeeId, EmployeeCode, Timestamp, Status, StatusCode, VerifyMode, Workcode, DeviceId, DeviceSerialNumber
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
        }, reader => new AttendanceRecord
        {
            Id = ReadGuid(reader, 0),
            EmployeeId = ReadGuid(reader, 1),
            EmployeeCode = reader.GetString(2),
            Timestamp = ReadDateTime(reader, 3),
            Status = (AttendanceStatus)ReadInt32(reader, 4),
            StatusCode = reader.IsDBNull(5) ? null : ReadInt32(reader, 5),
            VerifyMode = reader.IsDBNull(6) ? null : ReadInt32(reader, 6),
            Workcode = reader.IsDBNull(7) ? null : reader.GetString(7),
            DeviceId = reader.IsDBNull(8) ? null : ReadGuid(reader, 8),
            DeviceSerialNumber = reader.IsDBNull(9) ? null : reader.GetString(9)
        });
    }

    public AttendanceRecord AddAttendance(AttendanceRecord record)
    {
        var sql = _usePostgreSql
            ? """
                INSERT INTO AttendanceRecords
                (Id, EmployeeId, EmployeeCode, Timestamp, Status, StatusCode, VerifyMode, Workcode, DeviceId, DeviceSerialNumber)
                VALUES ($id, $employeeId, $code, $timestamp, $status, $statusCode, $verify, $workcode, $deviceId, $deviceSn)
                ON CONFLICT DO NOTHING
                """
            : """
                INSERT OR IGNORE INTO AttendanceRecords
                (Id, EmployeeId, EmployeeCode, Timestamp, Status, StatusCode, VerifyMode, Workcode, DeviceId, DeviceSerialNumber)
                VALUES ($id, $employeeId, $code, $timestamp, $status, $statusCode, $verify, $workcode, $deviceId, $deviceSn)
                """;

        var timestampText = record.Timestamp.ToString("O");
        var rows = Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$id", record.Id.ToString());
            command.Parameters.AddWithValue("$employeeId", record.EmployeeId.ToString());
            command.Parameters.AddWithValue("$code", record.EmployeeCode);
            command.Parameters.AddWithValue("$timestamp", timestampText);
            command.Parameters.AddWithValue("$status", (int)record.Status);
            command.Parameters.AddWithValue("$statusCode", record.StatusCode.HasValue ? record.StatusCode.Value : DBNull.Value);
            command.Parameters.AddWithValue("$verify", record.VerifyMode.HasValue ? record.VerifyMode.Value : DBNull.Value);
            command.Parameters.AddWithValue("$workcode", record.Workcode ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$deviceId", record.DeviceId.HasValue ? record.DeviceId.Value.ToString() : DBNull.Value);
            command.Parameters.AddWithValue("$deviceSn", record.DeviceSerialNumber ?? (object)DBNull.Value);
        });

        if (rows > 0)
        {
            return record;
        }

        const string lookupSql = """
            SELECT Id, EmployeeId, EmployeeCode, Timestamp, Status, StatusCode, VerifyMode, Workcode, DeviceId, DeviceSerialNumber
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
        }, reader => new AttendanceRecord
        {
            Id = ReadGuid(reader, 0),
            EmployeeId = ReadGuid(reader, 1),
            EmployeeCode = reader.GetString(2),
            Timestamp = ReadDateTime(reader, 3),
            Status = (AttendanceStatus)ReadInt32(reader, 4),
            StatusCode = reader.IsDBNull(5) ? null : ReadInt32(reader, 5),
            VerifyMode = reader.IsDBNull(6) ? null : ReadInt32(reader, 6),
            Workcode = reader.IsDBNull(7) ? null : reader.GetString(7),
            DeviceId = reader.IsDBNull(8) ? null : ReadGuid(reader, 8),
            DeviceSerialNumber = reader.IsDBNull(9) ? null : reader.GetString(9)
        });

        return existing ?? record;
    }

    public bool UpdateAttendance(AttendanceRecord record)
    {
        const string sql = """
            UPDATE AttendanceRecords
            SET EmployeeId = $employeeId,
                EmployeeCode = $code,
                Timestamp = $timestamp,
                Status = $status,
                StatusCode = $statusCode,
                VerifyMode = $verify,
                Workcode = $workcode,
                DeviceId = $deviceId,
                DeviceSerialNumber = $deviceSn
            WHERE Id = $id
            """;

        var rows = Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$id", record.Id.ToString());
            command.Parameters.AddWithValue("$employeeId", record.EmployeeId.ToString());
            command.Parameters.AddWithValue("$code", record.EmployeeCode);
            command.Parameters.AddWithValue("$timestamp", record.Timestamp.ToString("O"));
            command.Parameters.AddWithValue("$status", (int)record.Status);
            command.Parameters.AddWithValue("$statusCode", record.StatusCode.HasValue ? record.StatusCode.Value : DBNull.Value);
            command.Parameters.AddWithValue("$verify", record.VerifyMode.HasValue ? record.VerifyMode.Value : DBNull.Value);
            command.Parameters.AddWithValue("$workcode", record.Workcode ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$deviceId", record.DeviceId.HasValue ? record.DeviceId.Value.ToString() : DBNull.Value);
            command.Parameters.AddWithValue("$deviceSn", record.DeviceSerialNumber ?? (object)DBNull.Value);
        });

        return rows > 0;
    }

    public bool DeleteAttendance(Guid attendanceId)
    {
        const string sql = """
            DELETE FROM AttendanceRecords
            WHERE Id = $id
            """;

        var rows = Execute(sql, command =>
        {
            command.Parameters.AddWithValue("$id", attendanceId.ToString());
        });

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
        const string sql = """
            INSERT INTO DeviceCommands (Id, DeviceSerialNumber, CommandText, CreatedUtc)
            VALUES ($id, $sn, $cmd, $created)
            """;

        Execute(sql, db =>
        {
            db.Parameters.AddWithValue("$id", command.Id.ToString());
            db.Parameters.AddWithValue("$sn", command.DeviceSerialNumber);
            db.Parameters.AddWithValue("$cmd", command.CommandText);
            db.Parameters.AddWithValue("$created", command.CreatedUtc.ToString("O"));
        });

        return command;
    }

    public void MarkCommandSent(Guid commandId)
    {
        const string sql = """
            UPDATE DeviceCommands
            SET SentUtc = $sent
            WHERE Id = $id
            """;

        Execute(sql, db =>
        {
            db.Parameters.AddWithValue("$id", commandId.ToString());
            db.Parameters.AddWithValue("$sent", DateTime.UtcNow.ToString("O"));
        });
    }

    public void SaveCommandReply(Guid commandId, string replyText)
    {
        const string sql = """
            UPDATE DeviceCommands
            SET ReplyText = $reply
            WHERE Id = $id
            """;

        Execute(sql, db =>
        {
            db.Parameters.AddWithValue("$id", commandId.ToString());
            db.Parameters.AddWithValue("$reply", replyText);
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

    public SyncLog AddSyncLog(SyncLog log)
    {
        const string sql = """
            INSERT INTO SyncLogs (Id, DeviceSerialNumber, Type, Status, CommandText, ResponseText, CreatedUtc, CompletedUtc)
            VALUES ($id, $sn, $type, $status, $cmd, $response, $created, $completed)
            """;

        Execute(sql, db =>
        {
            db.Parameters.AddWithValue("$id", log.Id.ToString());
            db.Parameters.AddWithValue("$sn", log.DeviceSerialNumber);
            db.Parameters.AddWithValue("$type", (int)log.Type);
            db.Parameters.AddWithValue("$status", (int)log.Status);
            db.Parameters.AddWithValue("$cmd", log.CommandText);
            db.Parameters.AddWithValue("$response", log.ResponseText ?? string.Empty);
            db.Parameters.AddWithValue("$created", log.CreatedUtc.ToString("O"));
            db.Parameters.AddWithValue("$completed", log.CompletedUtc?.ToString("O") ?? (object)DBNull.Value);
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
        const string sql = """
            UPDATE SyncLogs
            SET Status = $status,
                ResponseText = $response,
                CompletedUtc = $completed
            WHERE Id = $id
            """;

        Execute(sql, db =>
        {
            db.Parameters.AddWithValue("$id", syncLogId.ToString());
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

        const string sql = """
            INSERT INTO DeviceTableStamps (DeviceSerialNumber, TableName, StampValue, UpdatedUtc)
            VALUES ($sn, $table, $stamp, $updated)
            ON CONFLICT (DeviceSerialNumber, TableName) DO UPDATE SET
                StampValue = excluded.StampValue,
                UpdatedUtc = excluded.UpdatedUtc
            """;

        Execute(sql, db =>
        {
            db.Parameters.AddWithValue("$sn", deviceSerialNumber.Trim());
            db.Parameters.AddWithValue("$table", tableName.Trim().ToUpperInvariant());
            db.Parameters.AddWithValue("$stamp", stampValue);
            db.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
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
                UNIQUE (user_pin, type, no, idx),
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
            """;

        using var connection = CreateConnection();
        OpenConnection(connection);
        using var command = connection.CreateCommand();
        command.CommandText = NormalizeSql(sql);
        command.ExecuteNonQuery();

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

    private DbConnection AcquireConnection(out bool ownsConnection)
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

    private string NormalizeSql(string sql)
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

        foreach (var table in new[] { "Employees", "Devices", "AttendanceRecords", "DeviceCommands", "SyncLogs", "DeviceTableStamps" })
        {
            normalized = Regex.Replace(
                normalized,
                $@"(?<!\.)""{Regex.Escape(table)}""",
                $@"""{PostgresAppSchema}"".""{table}""",
                RegexOptions.CultureInvariant);
        }

        return normalized;
    }

    private void NormalizeParameters(DbCommand command)
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

    private List<T> Query<T>(string sql, Func<DbDataReader, T> map)
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

    private List<T> Query<T>(string sql, Action<DbCommand> configure, Func<DbDataReader, T> map)
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

    private T? QuerySingle<T>(string sql, Action<DbCommand> configure, Func<DbDataReader, T> map)
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

    private int Execute(string sql, Action<DbCommand> configure)
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

    private static Guid ReadGuid(DbDataReader reader, int ordinal)
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

    private static DateTime ReadDateTime(DbDataReader reader, int ordinal)
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

    private static int ReadInt32(DbDataReader reader, int ordinal)
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

internal static class DbParameterCollectionExtensions
{
    public static void AddWithValue(this DbParameterCollection parameters, string parameterName, object? value)
    {
        var v = value ?? DBNull.Value;
        if (parameters is SqliteParameterCollection sqlite)
        {
            sqlite.AddWithValue(parameterName, v);
            return;
        }

        if (parameters is NpgsqlParameterCollection npgsql)
        {
            npgsql.AddWithValue(parameterName, v);
            return;
        }

        throw new NotSupportedException($"Unsupported parameter collection type: {parameters.GetType().Name}");
    }
}
