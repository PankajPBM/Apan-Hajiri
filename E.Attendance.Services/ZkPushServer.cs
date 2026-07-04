using System.Net;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;

namespace E.Attendance.Services;

public sealed class ZkPushServer : IDisposable
{
    private readonly IAttendanceService _service;
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, DateTime> _metadataProbeUtcBySerial = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid> _commandIdByWireId = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _metadataProbeLock = new();
    private readonly object _commandAliasLock = new();
    private CancellationTokenSource? _cts;
    public bool IsLocalhostOnly { get; private set; }
    public string ListeningDescription { get; private set; } = "Server: stopped";

    public event Action<Device>? DeviceTouched;
    public event Action<AttendanceRecord>? AttendanceReceived;
    public event Action<string>? Log;
    public event Action<string>? RequestReceived;

    public ZkPushServer(IAttendanceService service)
    {
        _service = service;
    }

    public void Start(int port)
    {
        if (_cts is not null)
        {
            return;
        }

        _listener.Prefixes.Clear();
        try
        {
            _listener.Prefixes.Add($"http://+:{port}/");
            _listener.Start();
            IsLocalhostOnly = false;
            ListeningDescription = $"Server: listening on port {port}";
        }
        catch (HttpListenerException)
        {
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            IsLocalhostOnly = true;
            ListeningDescription = $"Server: listening on localhost:{port} (run as admin for device access)";
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoop(_cts.Token));
        Log?.Invoke(ListeningDescription);
    }

    public void Stop()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        _cts.Dispose();
        _cts = null;

        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        IsLocalhostOnly = false;
        ListeningDescription = "Server: stopped";
    }

    public void Dispose()
    {
        Stop();
        _listener.Close();
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Server error: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleRequestAsync(context), token);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        string? cachedBody = null;

        try
        {
            var rawPath = request.Url?.AbsolutePath ?? string.Empty;
            var normalizedPath = NormalizePath(rawPath);
            RequestReceived?.Invoke($"{request.HttpMethod} {rawPath} from {request.RemoteEndPoint?.Address}");

            if (!normalizedPath.StartsWith("/iclock", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 404;
                return;
            }

            var query = ParseQuery(request.Url?.Query);
            query.TryGetValue("table", out var queryTable);
            var isAttPhotoUpload = normalizedPath.EndsWith("/cdata", StringComparison.OrdinalIgnoreCase) &&
                                   request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(queryTable, "ATTPHOTO", StringComparison.OrdinalIgnoreCase);
            var serialNumber = TryGetSerialNumber(request, query);
            if (string.IsNullOrWhiteSpace(serialNumber) && !isAttPhotoUpload)
            {
                cachedBody = await ReadBodyAsync(request).ConfigureAwait(false);
                serialNumber = TryGetSerialNumberFromPayload(cachedBody);
            }

            if (!string.IsNullOrWhiteSpace(serialNumber))
            {
                var ipAddress = request.RemoteEndPoint?.Address.ToString();
                var device = _service.TouchDevice(serialNumber, ipAddress);
                DeviceTouched?.Invoke(device);
                QueueMetadataRefreshCommands(serialNumber);
            }

            if (normalizedPath.EndsWith("/ping", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(response, "OK").ConfigureAwait(false);
                return;
            }

        if (normalizedPath.EndsWith("/getrequest", StringComparison.OrdinalIgnoreCase))
        {
            if (query.TryGetValue("INFO", out var infoPayload))
            {
                ApplyDeviceInfoUpdate(serialNumber, infoPayload);
            }

            if (!string.IsNullOrWhiteSpace(serialNumber))
            {
                var commandPayload = BuildCommandPayload(serialNumber);
                await WriteResponseAsync(response, commandPayload).ConfigureAwait(false);
                return;
            }

            await WriteResponseAsync(response, "OK").ConfigureAwait(false);
            return;
        }

        if (normalizedPath.EndsWith("/devicecmd", StringComparison.OrdinalIgnoreCase))
        {
            var body = cachedBody ?? await ReadBodyAsync(request).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                HandleCommandReplies(body);
            }

            await WriteResponseAsync(response, "OK").ConfigureAwait(false);
            return;
        }

            if (normalizedPath.EndsWith("/cdata", StringComparison.OrdinalIgnoreCase))
            {
                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && query.TryGetValue("options", out var options))
                {
                    if (string.Equals(options, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseAsync(response, BuildOptionsResponse(serialNumber)).ConfigureAwait(false);
                        return;
                    }
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    query.TryGetValue("table", out var table);
                    if (string.Equals(table, "ATTPHOTO", StringComparison.OrdinalIgnoreCase))
                    {
                        var appDataRoot = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                            "E_Attendance");
                        Directory.CreateDirectory(appDataRoot);

                        var (saved, bytes) = await SaveRawAttPhotoPayloadAsync(appDataRoot, serialNumber, request).ConfigureAwait(false);
                        TryUpdateDeviceTableStampFromQuery(serialNumber, "ATTPHOTO", query);
                        if (saved > 0)
                        {
                            Log?.Invoke($"ATTPHOTO saved from {serialNumber ?? "unknown"}: {bytes} bytes");
                        }
                        await WriteResponseAsync(response, saved > 0 ? $"OK: {saved}" : "OK").ConfigureAwait(false);
                        return;
                    }

                    if (string.Equals(table, "ATTLOG", StringComparison.OrdinalIgnoreCase))
                    {
                        var body = cachedBody ?? await ReadBodyAsync(request).ConfigureAwait(false);
                        var count = HandleAttendancePayload(serialNumber, body);
                        TryUpdateDeviceTableStampFromQuery(serialNumber, "ATTLOG", query);
                        await WriteResponseAsync(response, $"OK: {count}").ConfigureAwait(false);
                        return;
                    }

                    if (string.Equals(table, "OPERLOG", StringComparison.OrdinalIgnoreCase))
                    {
                        var body = cachedBody ?? await ReadBodyAsync(request).ConfigureAwait(false);
                        var count = HandleAttendancePayload(serialNumber, body);
                        TryUpdateDeviceTableStampFromQuery(serialNumber, "OPERLOG", query);
                        await WriteResponseAsync(response, $"OK: {count}").ConfigureAwait(false);
                        return;
                    }

                    if (string.Equals(table, "options", StringComparison.OrdinalIgnoreCase))
                    {
                        var body = cachedBody ?? await ReadBodyAsync(request).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(serialNumber))
                        {
                            ApplyOptionsUpdate(serialNumber, request.RemoteEndPoint?.Address.ToString(), body);
                        }

                        Log?.Invoke($"Options from {serialNumber}: {body}".Trim());
                        await WriteResponseAsync(response, "OK").ConfigureAwait(false);
                        return;
                    }

                    await DrainBodyAsync(request).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(serialNumber) && !string.IsNullOrWhiteSpace(table))
                    {
                        TryUpdateDeviceTableStampFromQuery(serialNumber, table, query);
                    }
                    await WriteResponseAsync(response, "OK").ConfigureAwait(false);
                    return;
                }
            }

            await WriteResponseAsync(response, "OK").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Request error: {ex.Message}");
            response.StatusCode = 500;
            await WriteResponseAsync(response, "ERROR").ConfigureAwait(false);
        }
        finally
        {
            response.OutputStream.Close();
        }
    }

    private static string? TryGetSerialNumberFromPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        var lines = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var pairs = line.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var pair in pairs)
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 &&
                    string.Equals(parts[0], "SN", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(parts[1]))
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }
        }

        return null;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        if (path.Length > 1)
        {
            path = path.TrimEnd('/');
        }

        return path;
    }

    private static string? TryGetSerialNumber(HttpListenerRequest request, IReadOnlyDictionary<string, string> query)
    {
        if (query.TryGetValue("SN", out var sn) && !string.IsNullOrWhiteSpace(sn))
        {
            return sn.Trim();
        }

        var headerSn = request.Headers["SN"];
        if (!string.IsNullOrWhiteSpace(headerSn))
        {
            return headerSn.Trim();
        }

        var rawUrl = request.RawUrl;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        var match = Regex.Match(rawUrl, @"(?:\?|&)SN=([^&\s]+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        return Uri.UnescapeDataString(match.Groups[1].Value).Trim();
    }

    private int HandleAttendancePayload(string? deviceSerialNumber, string payload)
    {
        var count = 0;
        var lines = payload
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            if (TryParseAttendanceLine(line, out var employeeCode, out var timestamp, out var status, out var verify, out var workcode))
            {
                var record = _service.AddAttendanceFromDevice(employeeCode, timestamp, status, verify, workcode, deviceSerialNumber);
                AttendanceReceived?.Invoke(record);
                count++;
            }
        }

        return count;
    }

    private static string NormalizeDeviceEmployeeCode(string? rawCode)
    {
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            return string.Empty;
        }

        var code = rawCode.Trim();
        if (code.StartsWith("PIN=", StringComparison.OrdinalIgnoreCase))
        {
            code = code[4..].Trim();
        }
        else if (code.StartsWith("UserPIN=", StringComparison.OrdinalIgnoreCase))
        {
            code = code[8..].Trim();
        }
        else if (code.StartsWith("UserId=", StringComparison.OrdinalIgnoreCase))
        {
            code = code[7..].Trim();
        }
        else if (code.StartsWith("EnrollNumber=", StringComparison.OrdinalIgnoreCase))
        {
            code = code[13..].Trim();
        }

        while (code.StartsWith("=", StringComparison.Ordinal))
        {
            code = code[1..].Trim();
        }

        return code;
    }

    private static bool TryParseDeviceTimestamp(string? value, out DateTime timestamp)
    {
        timestamp = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var token = value.Trim();

        string[] formats =
        [
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss.fff",
            "yyyy-MM-dd HH:mm:ss.ffffff",
            "yyyy-MM-dd HH:mm:ss.fffffff",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy/MM/dd HH:mm:ss.fff",
            "yyyy/MM/dd HH:mm:ss.ffffff",
            "yyyy/MM/dd HH:mm:ss.fffffff",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss.fff",
            "yyyy-MM-ddTHH:mm:ss.ffffff",
            "yyyy-MM-ddTHH:mm:ss.fffffff",
            "yyyy/MM/ddTHH:mm:ss",
            "yyyy/MM/ddTHH:mm:ss.fff",
            "yyyy/MM/ddTHH:mm:ss.ffffff",
            "yyyy/MM/ddTHH:mm:ss.fffffff"
        ];

        if (DateTime.TryParseExact(
                token,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out timestamp))
        {
            return true;
        }

        return DateTime.TryParse(
            token,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out timestamp);
    }

    private static bool TryParseAttendanceLine(string line, out string employeeCode, out DateTime timestamp, out int status, out int verify, out string? workcode)
    {
        employeeCode = string.Empty;
        timestamp = DateTime.MinValue;
        status = 0;
        verify = 0;
        workcode = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();

        if (trimmed.Contains('=', StringComparison.Ordinal))
        {
            var pairs = trimmed.Split('\t', StringSplitOptions.TrimEntries);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in pairs)
            {
                if (string.IsNullOrWhiteSpace(pair) || !pair.Contains('=', StringComparison.Ordinal))
                {
                    continue;
                }

                var kvParts = pair.Split('=', 2);
                var key = kvParts[0].Trim();
                var value = kvParts.Length > 1 ? kvParts[1].Trim() : string.Empty;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    values[key] = value;
                }
            }

            static string? TryGetFirst(Dictionary<string, string> values, params string[] keys)
            {
                foreach (var key in keys)
                {
                    if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }

                return null;
            }

            var pin = TryGetFirst(values, "PIN", "UserPIN", "UserId", "UserID", "EnrollNumber");
            if (!string.IsNullOrWhiteSpace(pin))
            {
                employeeCode = NormalizeDeviceEmployeeCode(pin);
            }

            if (!string.IsNullOrWhiteSpace(employeeCode))
            {
                var dateTimeToken = TryGetFirst(values, "DateTime", "Time", "Timestamp");
                if (TryParseDeviceTimestamp(dateTimeToken, out timestamp))
                {
                    _ = int.TryParse(TryGetFirst(values, "Status", "State") ?? string.Empty, out status);
                    _ = int.TryParse(TryGetFirst(values, "Verify", "VerifyMode") ?? string.Empty, out verify);
                    workcode = TryGetFirst(values, "WorkCode", "Workcode");
                    if (string.IsNullOrWhiteSpace(workcode))
                    {
                        workcode = null;
                    }

                    return true;
                }
            }
        }

        var tabParts = trimmed.Split('\t', StringSplitOptions.TrimEntries);
        if (tabParts.Length >= 3)
        {
            employeeCode = NormalizeDeviceEmployeeCode(tabParts[0]);
            if (string.IsNullOrWhiteSpace(employeeCode))
            {
                return false;
            }

            var statusIndex = 2;
            if (!TryParseDeviceTimestamp(tabParts[1], out timestamp))
            {
                if (tabParts.Length >= 4 && TryParseDeviceTimestamp($"{tabParts[1]} {tabParts[2]}", out timestamp))
                {
                    statusIndex = 3;
                }
                else
                {
                    return false;
                }
            }

            if (tabParts.Length <= statusIndex)
            {
                return false;
            }

            _ = int.TryParse(tabParts[statusIndex], out status);
            if (tabParts.Length > statusIndex + 1)
            {
                _ = int.TryParse(tabParts[statusIndex + 1], out verify);
            }

            if (tabParts.Length > statusIndex + 2 && !string.IsNullOrWhiteSpace(tabParts[statusIndex + 2]))
            {
                workcode = tabParts[statusIndex + 2];
            }

            return true;
        }

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 4)
        {
            return false;
        }

        employeeCode = NormalizeDeviceEmployeeCode(parts[0]);
        if (string.IsNullOrWhiteSpace(employeeCode))
        {
            return false;
        }

        if (!TryParseDeviceTimestamp($"{parts[1]} {parts[2]}", out timestamp))
        {
            return false;
        }

        _ = int.TryParse(parts[3], out status);
        if (parts.Length >= 5)
        {
            _ = int.TryParse(parts[4], out verify);
        }

        if (parts.Length >= 6)
        {
            workcode = parts[5];
        }

        return true;
    }

    private static Dictionary<string, string> ParseQuery(string? queryString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(queryString))
        {
            return result;
        }

        var query = queryString.StartsWith("?") ? queryString[1..] : queryString;
        var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var pair in pairs)
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static async Task DrainBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
        {
            return;
        }

        var buffer = new byte[81920];
        while (true)
        {
            var read = await request.InputStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }
        }
    }

    private static string NormalizeSerialToken(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
        {
            return string.Empty;
        }

        var chars = serial
            .Trim()
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray();
        return chars.Length == 0 ? string.Empty : new string(chars);
    }

    private static async Task<(int Saved, long Bytes)> SaveRawAttPhotoPayloadAsync(
        string appDataRoot,
        string? serialNumber,
        HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
        {
            return (0, 0);
        }

        static bool LooksLikeAsciiText(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0)
            {
                return true;
            }

            var sample = Math.Min(bytes.Length, 512);
            var nonText = 0;

            for (var i = 0; i < sample; i++)
            {
                var b = bytes[i];
                if (b == 0)
                {
                    return false;
                }

                if (b is 9 or 10 or 13)
                {
                    continue;
                }

                if (b >= 32 && b <= 126)
                {
                    continue;
                }

                nonText++;
            }

            return nonText <= sample / 25;
        }

        static string ResolveAttPhotoFileExtension(ReadOnlySpan<byte> header, string? contentType)
        {
            var normalizedContentType = contentType?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(normalizedContentType))
            {
                var token = normalizedContentType.ToLowerInvariant();
                if (token.Contains("jpeg", StringComparison.Ordinal) || token.Contains("jpg", StringComparison.Ordinal))
                {
                    return "jpg";
                }

                if (token.Contains("png", StringComparison.Ordinal))
                {
                    return "png";
                }

                if (token.Contains("gif", StringComparison.Ordinal))
                {
                    return "gif";
                }

                if (token.Contains("text", StringComparison.Ordinal) || token.Contains("plain", StringComparison.Ordinal))
                {
                    return "txt";
                }
            }

            if (header.Length >= 3 &&
                header[0] == 0xFF &&
                header[1] == 0xD8 &&
                header[2] == 0xFF)
            {
                return "jpg";
            }

            if (header.Length >= 8 &&
                header[0] == 0x89 &&
                header[1] == (byte)'P' &&
                header[2] == (byte)'N' &&
                header[3] == (byte)'G' &&
                header[4] == 0x0D &&
                header[5] == 0x0A &&
                header[6] == 0x1A &&
                header[7] == 0x0A)
            {
                return "png";
            }

            if (header.Length >= 6 &&
                header[0] == (byte)'G' &&
                header[1] == (byte)'I' &&
                header[2] == (byte)'F' &&
                header[3] == (byte)'8' &&
                header[4] == (byte)'7' &&
                header[5] == (byte)'a')
            {
                return "gif";
            }

            if (header.Length >= 6 &&
                header[0] == (byte)'G' &&
                header[1] == (byte)'I' &&
                header[2] == (byte)'F' &&
                header[3] == (byte)'8' &&
                header[4] == (byte)'9' &&
                header[5] == (byte)'a')
            {
                return "gif";
            }

            return LooksLikeAsciiText(header) ? "txt" : "bin";
        }

        try
        {
            var serialToken = NormalizeSerialToken(serialNumber);
            if (string.IsNullOrWhiteSpace(serialToken))
            {
                serialToken = "UNKNOWN";
            }

            var targetDir = Path.Combine(appDataRoot, "attphoto", serialToken);
            Directory.CreateDirectory(targetDir);

            var headerBuffer = new byte[512];
            var headerBytes = await request.InputStream.ReadAsync(headerBuffer, 0, headerBuffer.Length).ConfigureAwait(false);
            if (headerBytes <= 0)
            {
                return (0, 0);
            }

            var ext = ResolveAttPhotoFileExtension(headerBuffer.AsSpan(0, headerBytes), request.ContentType);
            var fileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmssfff}_{Guid.NewGuid():N}.{ext}";
            var path = Path.Combine(targetDir, fileName);

            await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: true);
            await stream.WriteAsync(headerBuffer.AsMemory(0, headerBytes)).ConfigureAwait(false);

            long bytesWritten = headerBytes;
            var buffer = new byte[81920];
            while (true)
            {
                var read = await request.InputStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                bytesWritten += read;
            }

            return (1, bytesWritten);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, string content)
    {
        var buffer = Encoding.UTF8.GetBytes(content);
        response.ContentType = "text/plain";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
    }

    private void TryUpdateDeviceTableStampFromQuery(
        string? serialNumber,
        string? tableName,
        IReadOnlyDictionary<string, string> query)
    {
        if (string.IsNullOrWhiteSpace(serialNumber) || string.IsNullOrWhiteSpace(tableName))
        {
            return;
        }

        var normalizedSerial = serialNumber.Trim();
        var normalizedTable = tableName.Trim().ToUpperInvariant();

        static bool TryParseStamp(string? value, out long stamp)
        {
            stamp = 0;
            return !string.IsNullOrWhiteSpace(value) &&
                   long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out stamp) &&
                   stamp >= 0;
        }

        static long? TryGetStamp(IReadOnlyDictionary<string, string> query, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (!query.TryGetValue(key, out var value))
                {
                    continue;
                }

                if (TryParseStamp(value, out var stamp))
                {
                    return stamp;
                }
            }

            return null;
        }

        var stamp = normalizedTable switch
        {
            "ATTLOG" => TryGetStamp(query, "ATTLOGStamp", "Stamp"),
            "OPERLOG" => TryGetStamp(query, "OPERLOGStamp", "OpStamp"),
            "ATTPHOTO" => TryGetStamp(query, "ATTPHOTOStamp", "PhotoStamp"),
            _ => TryGetStamp(query, $"{normalizedTable}Stamp", "Stamp")
        };

        if (stamp.HasValue)
        {
            _service.UpsertDeviceTableStamp(normalizedSerial, normalizedTable, stamp.Value);
        }
    }

    private string BuildOptionsResponse(string? serialNumber)
    {
        var sn = string.IsNullOrWhiteSpace(serialNumber) ? "UNKNOWN" : serialNumber;
        var normalizedSerial = string.IsNullOrWhiteSpace(serialNumber) ? string.Empty : serialNumber.Trim();
        var stamps = string.IsNullOrWhiteSpace(normalizedSerial)
            ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            : _service.GetDeviceTableStamps(normalizedSerial);

        long ResolveStamp(string tableName)
            => stamps.TryGetValue(tableName, out var stamp) ? stamp : 0L;

        var attLogStamp = ResolveStamp("ATTLOG");
        var opLogStamp = ResolveStamp("OPERLOG");
        var attPhotoStamp = ResolveStamp("ATTPHOTO");

        var timeZoneMinutes = 345;
        if (!string.IsNullOrWhiteSpace(normalizedSerial))
        {
            var device = _service.ListDevices().FirstOrDefault(d =>
                string.Equals(d.SerialNumber, normalizedSerial, StringComparison.OrdinalIgnoreCase));
            if (device is not null && !string.IsNullOrWhiteSpace(device.TimeZone))
            {
                timeZoneMinutes = DeviceProtocolService.TimezoneToMinutes(device.TimeZone);
            }
        }

        const string transFlag = "TransData AttLog\tWORKCODE";
        var lines = new[]
        {
            $"GET OPTION FROM: {sn}",
            $"Stamp={attLogStamp}",
            $"OpStamp={opLogStamp}",
            $"PhotoStamp={attPhotoStamp}",
            "ErrorDelay=60",
            "Delay=10",
            "TransTimes=",
            "TransInterval=1",
            $"TransFlag={transFlag}",
            $"TimeZone={timeZoneMinutes}",
            "Realtime=1",
            "Encrypt=0",
            "ServerVer=2.2.14",
            "PushProtVer=2.4.1",
            "PushOptionsFlag=1",
            $"ATTLOGStamp={attLogStamp}",
            $"OPERLOGStamp={opLogStamp}",
            $"ATTPHOTOStamp={attPhotoStamp}"
        };

        // Keep LF separators for maximum firmware compatibility with command batches.
        return string.Join("\n", lines);
    }

    private string BuildCommandPayload(string serialNumber)
    {
        var commands = _service.GetPendingCommands(serialNumber, 10).ToList();
        if (commands.Count == 0)
        {
            var retryCutoff = DateTime.UtcNow.AddSeconds(-45);
            commands = _service.GetDeviceCommands(serialNumber, 500)
                .Where(c => c.SentUtc.HasValue &&
                            c.SentUtc.Value <= retryCutoff &&
                            string.IsNullOrWhiteSpace(c.ReplyText))
                .OrderBy(c => c.CreatedUtc)
                .Take(10)
                .ToList();
        }

        if (commands.Count == 0)
        {
            return "OK";
        }

        var lines = new List<string>();
        foreach (var command in commands)
        {
            var wireId = CreateWireCommandId(command.Id);
            lock (_commandAliasLock)
            {
                _commandIdByWireId[wireId] = command.Id;
            }

            lines.Add($"C:{wireId}:{command.CommandText}");
            _service.MarkCommandSent(command.Id);
        }

        return string.Join('\n', lines);
    }

    private void HandleCommandReplies(string payload)
    {
        var lines = payload
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            if (!line.Contains("ID=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var idPart = parts.FirstOrDefault(p => p.StartsWith("ID=", StringComparison.OrdinalIgnoreCase));
            if (idPart is null)
            {
                continue;
            }

            var idValue = idPart.Substring(3).Trim();
            if (!TryResolveCommandId(idValue, out var commandId))
            {
                Log?.Invoke($"Command reply with unknown ID ignored: {line}");
                continue;
            }

            _service.RecordCommandReply(commandId, line);
            lock (_commandAliasLock)
            {
                _commandIdByWireId.Remove(CreateWireCommandId(commandId));
            }
        }
    }

    private static string CreateWireCommandId(Guid commandId)
        => commandId.ToString("N")[..13];

    private bool TryResolveCommandId(string idValue, out Guid commandId)
    {
        if (Guid.TryParseExact(idValue, "N", out commandId) || Guid.TryParse(idValue, out commandId))
        {
            return true;
        }

        lock (_commandAliasLock)
        {
            return _commandIdByWireId.TryGetValue(idValue, out commandId);
        }
    }

    private void ApplyDeviceInfoUpdate(string? serialNumber, string infoPayload)
    {
        if (string.IsNullOrWhiteSpace(serialNumber) || string.IsNullOrWhiteSpace(infoPayload))
        {
            return;
        }

        // INFO=firmware,userCount,fpCount,attCount,ip,...
        var values = infoPayload.Split(',', StringSplitOptions.TrimEntries);
        if (values.Length < 5)
        {
            return;
        }

        var ipAddress = values[4];
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return;
        }

        var existing = _service.ListDevices().FirstOrDefault(d =>
            string.Equals(d.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return;
        }

        _service.TouchDevice(serialNumber, ipAddress);

        // INFO payload also includes counters; use them to infer capabilities when available.
        var fpQty = values.Length >= 3 && int.TryParse(values[2], out var fp) ? fp : 0;
        var faceQty = values.Length >= 9 && int.TryParse(values[8], out var fa) ? fa : 0;
        var current = _service.ListDevices().FirstOrDefault(d =>
            string.Equals(d.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase));
        var visibleLight = current?.SupportsVisibleLightFace ?? false;
        var infrared = (current?.SupportsInfraredFace ?? false) || faceQty > 0;
        var fingerprint = (current?.SupportsFingerprint ?? false) || fpQty > 0;
        _service.UpdateDeviceCapabilities(
            serialNumber,
            visibleLight,
            infrared,
            fingerprint);
    }

    private void ApplyOptionsUpdate(string serialNumber, string? remoteIpAddress, string body)
    {
        var options = ParseOptionPairs(body);
        var name = FirstValue(options, "DevName", "DeviceName", "Alias", "TerminalName") ?? serialNumber;
        var ipAddress = FirstValue(options, "IPAddress", "IP", "NetIP") ?? remoteIpAddress ?? string.Empty;
        var location = FirstValue(options, "Area", "Location", "Addr", "Address") ?? string.Empty;

        var existing = _service.ListDevices().FirstOrDefault(d =>
            string.Equals(d.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return;
        }

        _service.UpdateDeviceInfo(
            existing.Id,
            string.IsNullOrWhiteSpace(name) ? null : name,
            null,
            string.IsNullOrWhiteSpace(ipAddress) ? null : ipAddress,
            string.IsNullOrWhiteSpace(location) ? null : location);

        _service.TouchDevice(serialNumber, string.IsNullOrWhiteSpace(ipAddress) ? remoteIpAddress : ipAddress);

        // Detect capabilities from OPTIONS payload.
        var current = _service.ListDevices().FirstOrDefault(d =>
            string.Equals(d.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase));
        var faceVersion = FirstValue(options, "FaceVersion", "FaceAlgVer", "FaceAlgoVersion", "FaceAlgorithmVersion") ?? string.Empty;
        var fpVersion = FirstValue(options, "FingerVersion", "FPVersion", "FingerAlgVer", "FingerprintAlgorithmVersion") ?? string.Empty;
        var supportsFace = string.Equals(options.GetValueOrDefault("FaceFunOn"), "1") ||
                           string.Equals(options.GetValueOrDefault("FaceEnrollThreshold"), "1") ||
                           !string.IsNullOrWhiteSpace(faceVersion);

        var isVisibleLight = body.Contains("VLFace", StringComparison.OrdinalIgnoreCase) ||
                             body.Contains("VisibleLight", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(options.GetValueOrDefault("IsSupportVisibleLightFace"), "1");
        if (!isVisibleLight && double.TryParse(faceVersion, out var fv) && fv >= 5.0)
        {
            isVisibleLight = true;
        }

        var supportsFp = string.Equals(options.GetValueOrDefault("FingerFunOn"), "1") ||
                         body.Contains("FPVersion", StringComparison.OrdinalIgnoreCase);
        var model = FirstValue(options, "DeviceType", "ProductType", "DevType", "TerminalType") ?? string.Empty;
        var rawTz = FirstValue(options, "MachineTZ", "TimeZone");
        var timeZone = NormalizeTimeZoneToken(rawTz);

        _service.UpdateDeviceCapabilities(
            serialNumber,
            (current?.SupportsVisibleLightFace ?? false) || isVisibleLight,
            (current?.SupportsInfraredFace ?? false) || supportsFace,
            (current?.SupportsFingerprint ?? false) || supportsFp,
            faceVersion,
            fpVersion,
            model,
            timeZone);
    }

    private static Dictionary<string, string> ParseOptionPairs(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(body))
        {
            return result;
        }

        var pairs = body.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pair in pairs)
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0 || idx == pair.Length - 1)
            {
                continue;
            }

            var key = pair[..idx].Trim();
            var value = pair[(idx + 1)..].Trim();
            if (key.Length > 0)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static string? FirstValue(IReadOnlyDictionary<string, string> source, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (source.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? NormalizeTimeZoneToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var token = raw.Trim();
        if (Regex.IsMatch(token, @"^[+-]\d{4}$"))
        {
            var hh = token[1..3];
            var mm = token[3..5];
            return $"{token[0]}{hh}:{mm}";
        }

        return token;
    }

    private void QueueMetadataRefreshCommands(string serialNumber)
    {
        var now = DateTime.UtcNow;
        lock (_metadataProbeLock)
        {
            if (_metadataProbeUtcBySerial.TryGetValue(serialNumber, out var last) &&
                now - last < TimeSpan.FromMinutes(2))
            {
                return;
            }

            _metadataProbeUtcBySerial[serialNumber] = now;
        }

        // ── Fix: clear commands stuck in "sent" state for >15 min with no reply ──
        // These block the queue forever if the device disconnected mid-command.
        try
        {
            var allRecent = _service.GetDeviceCommands(serialNumber, 200);
            var staleThreshold = now - TimeSpan.FromMinutes(15);
            foreach (var cmd in allRecent)
            {
                if (cmd.SentUtc.HasValue && cmd.SentUtc.Value < staleThreshold && cmd.ReplyText == null)
                {
                    // Mark as replied with a timeout notice so it stops blocking
                    _service.RecordCommandReply(cmd.Id, $"TIMEOUT: no reply after 15 min (cleared on reconnect)");
                    Log?.Invoke($"[Reconnect] Cleared stale command {cmd.Id:N} for {serialNumber}: {cmd.CommandText}");
                }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[Reconnect] Could not clear stale commands for {serialNumber}: {ex.Message}");
        }

        // ── Queue device info/options refresh ────────────────────────────────────
        var pending = _service.GetPendingCommands(serialNumber, 50);
        if (!pending.Any(c => string.Equals(c.CommandText, "INFO", StringComparison.OrdinalIgnoreCase)))
        {
            _service.QueueDeviceCommand(serialNumber, "INFO");
        }
        if (!pending.Any(c => string.Equals(c.CommandText, "RELOAD OPTIONS", StringComparison.OrdinalIgnoreCase)))
        {
            _service.QueueDeviceCommand(serialNumber, "RELOAD OPTIONS");
        }

        // ── Auto-fetch for registration devices on connect ───────────────────────
        // If this is a registration device, automatically queue user+biometric+attendance
        // fetch so data is pulled whenever the device reconnects (within rate limit).
        try
        {
            var device = _service.ListDevices()
                .FirstOrDefault(d => string.Equals(d.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase));
            if (device is not null && device.IsRegistrationDevice)
            {
                // Only auto-fetch if no pending data-query commands already exist
                var refreshedPending = _service.GetPendingCommands(serialNumber, 50);
                var hasDataQuery = refreshedPending.Any(c =>
                    c.CommandText.Contains("DATA QUERY", StringComparison.OrdinalIgnoreCase) ||
                    c.CommandText.Contains("DATA QUERY ATTLOG", StringComparison.OrdinalIgnoreCase));
                if (!hasDataQuery)
                {
                    _service.QueueDeviceCommand(serialNumber, "CHECK");
                    _service.QueueDeviceCommand(serialNumber, "DATA QUERY USERINFO PIN=*");
                    _service.QueueDeviceCommand(serialNumber, "DATA QUERY USERINFO");
                    _service.QueueDeviceCommand(serialNumber, "DATA QUERY BIODATA");
                    _service.QueueDeviceCommand(serialNumber, "DATA QUERY FINGERTMP");
                    _service.QueueDeviceCommand(serialNumber, "DATA QUERY FACE");
                    _service.QueueDeviceCommand(serialNumber, "DATA QUERY BIOPHOTO");
                    _service.QueueDeviceCommand(serialNumber, "DATA QUERY USERPIC");
                    // Also fetch attendance logs from beginning of time
                    var start = "2000-01-01 00:00:00";
                    var end = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                    _service.QueueDeviceCommand(serialNumber, $"DATA QUERY ATTLOG StartTime={start}\tEndTime={end}");
                    Log?.Invoke($"[AutoFetch] Queued user+biometric+attendance fetch for registration device {serialNumber}");
                }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[AutoFetch] Error queuing registration device fetch for {serialNumber}: {ex.Message}");
        }
    }
}
