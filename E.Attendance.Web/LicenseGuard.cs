using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace E.Attendance.Web.Licensing;

internal sealed record LicenseRuntimeOptions(
    string AppDataRoot,
    string ContentRootPath,
    string BaseDirectory,
    string? LicenseDirectory,
    string? PublicKeyPath);

internal sealed record LicenseValidationResult(
    bool IsValid,
    string Code,
    string Message,
    string MachineHash,
    string? LicenseMachineHash,
    DateTimeOffset? ExpiryUtc,
    DateTimeOffset CheckedAtUtc,
    string? CustomerName,
    string? LicenseType,
    string? LicenseDataPath,
    string? KeyPath,
    string? PublicKeyPath,
    string? ContactNumber = null,
    string? Email = null,
    int? MaxDevices = null);

internal sealed class LicenseGuard
{
    private const string EmbeddedPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAhqFn6s4Y2IqQGnFWu6cm
        Iagid2Q/ydBsEjsudICPCxQoE7IpQRcOREeGOidKfDQeT0pPKM3KQBY3xjRkdxD2
        rWXjYXS8RnpSvRfOXpW11I0FXybEnKgZHJzS9I93sLM4CJMZ61tjXnXlKwnpTRtl
        DrdMhaNJtV4s0qUXPV+kcSBDdhSxiafitvNv2y/yB0SOkazJAxKq+fgFKRTmH8zo
        I13mbnwmz+7bbhhf/bLvE+vpmun3oMp9r8W/x/eyUk+xCmtkVed4KyDN02fhjreA
        YFtMyvkVRdkASBoVSkERldc7LgXcFgp9kOFCP5PftG6FLtjq9iTSzECIsszeC4uV
        IQIDAQAB
        -----END PUBLIC KEY-----
        """;

    private static readonly byte[] SystemSaltBytes = Encoding.UTF8.GetBytes("PB_SECURE_SYSTEM_V1_77");
    private readonly LicenseRuntimeOptions _options;
    private readonly string _machineHash;
    private readonly HashSet<string> _machineHashVariants;

    public LicenseGuard(LicenseRuntimeOptions options)
    {
        _options = options;
        (_machineHash, _machineHashVariants) = ComputeMachineHashes();
    }

    public string GetMachineHash() => _machineHash;

    public LicenseValidationResult Validate()
    {
        var checkedAtUtc = DateTimeOffset.UtcNow;
        var machineHash = _machineHash;
        var locations = ResolveLicenseLocations();
        if (string.IsNullOrWhiteSpace(locations.KeyPath))
        {
            return Invalid(
                "missing_key_file",
                "Product not activated. Encryption key file (.key) was not found.",
                machineHash,
                checkedAtUtc,
                locations);
        }

        var publicKeyPem = ResolvePublicKeyPem(locations.PublicKeyPath);
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            return Invalid(
                "missing_public_key",
                "Public key is missing. Add public_key.pem or embed it in code.",
                machineHash,
                checkedAtUtc,
                locations);
        }

        string encryptedToken;
        string fernetKey;
        try
        {
            var material = ReadActivationMaterial(locations.KeyPath, locations.LicensePath);
            encryptedToken = material.EncryptedToken;
            fernetKey = material.FernetKey;
        }
        catch (Exception ex)
        {
            return Invalid(
                "read_error",
                $"Unable to read license files: {ex.Message}",
                machineHash,
                checkedAtUtc,
                locations);
        }

        byte[] decryptedBytes;
        try
        {
            decryptedBytes = FernetDecrypt(encryptedToken, fernetKey);
        }
        catch (Exception ex)
        {
            return Invalid(
                "decrypt_failed",
                $"License decryption failed: {ex.Message}",
                machineHash,
                checkedAtUtc,
                locations);
        }

        var decryptedText = Encoding.UTF8.GetString(decryptedBytes).Trim();
        if (string.IsNullOrWhiteSpace(decryptedText))
        {
            return Invalid(
                "empty_payload",
                "License payload is empty.",
                machineHash,
                checkedAtUtc,
                locations);
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(decryptedText);
            root = document.RootElement.Clone();
        }
        catch (Exception ex)
        {
            return Invalid(
                "invalid_json",
                $"License payload is not valid JSON: {ex.Message}",
                machineHash,
                checkedAtUtc,
                locations);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return Invalid(
                "invalid_payload_shape",
                "License payload must be a JSON object.",
                machineHash,
                checkedAtUtc,
                locations);
        }

        var payload = ResolvePayloadElement(root, out var payloadAsText);
        var signatureText = TryGetString(root, "signature");
        if (string.IsNullOrWhiteSpace(signatureText))
        {
            return Invalid(
                "missing_signature",
                "License signature is missing.",
                machineHash,
                checkedAtUtc,
                locations);
        }

        byte[] signature;
        try
        {
            signature = DecodeBase64Flexible(signatureText);
        }
        catch (Exception ex)
        {
            return Invalid(
                "invalid_signature_encoding",
                $"License signature is not valid Base64: {ex.Message}",
                machineHash,
                checkedAtUtc,
                locations);
        }

        var signatureCandidates = BuildSignatureCandidates(root, payload, decryptedText, payloadAsText);
        var signatureValid = VerifySignature(publicKeyPem, signature, signatureCandidates);
        if (!signatureValid)
        {
            return Invalid(
                "signature_invalid",
                "License signature verification failed.",
                machineHash,
                checkedAtUtc,
                locations);
        }

        var licenseMachineHash = TryGetString(payload, "machine_hash", "machineHash", "machine_id");
        if (string.IsNullOrWhiteSpace(licenseMachineHash))
        {
            return Invalid(
                "missing_machine_hash",
                "License payload does not contain machine_hash.",
                machineHash,
                checkedAtUtc,
                locations,
                null);
        }

        if (!MachineHashesMatch(_machineHashVariants, licenseMachineHash))
        {
            return Invalid(
                "machine_mismatch",
                "License is not issued for this machine.",
                machineHash,
                checkedAtUtc,
                locations,
                licenseMachineHash);
        }

        var expiryUtc = TryGetExpiryUtc(payload);
        if (!expiryUtc.HasValue)
        {
            return Invalid(
                "missing_expiry",
                "License payload does not contain a valid expiry date.",
                machineHash,
                checkedAtUtc,
                locations,
                licenseMachineHash);
        }

        var customerName = ResolveCustomerName(payload, root);
        var licenseType = ResolveLicenseType(payload, root);
        var contactNumber = ResolveContactNumber(payload, root);
        var email = ResolveEmail(payload, root);
        var maxDevices = ResolveMaxDevices(payload, root, licenseType);

        if (DateTimeOffset.UtcNow > expiryUtc.Value)
        {
            return new LicenseValidationResult(
                false,
                "expired",
                $"License expired on {expiryUtc.Value:yyyy-MM-dd}.",
                machineHash,
                licenseMachineHash,
                expiryUtc,
                checkedAtUtc,
                customerName,
                licenseType,
                locations.LicensePath,
                locations.KeyPath,
                locations.PublicKeyPath,
                contactNumber,
                email,
                maxDevices);
        }

        return new LicenseValidationResult(
            true,
            "ok",
            "License is valid.",
            machineHash,
            licenseMachineHash,
            expiryUtc,
            checkedAtUtc,
            customerName,
            licenseType,
            locations.LicensePath,
            locations.KeyPath,
            locations.PublicKeyPath,
            contactNumber,
            email,
            maxDevices);
    }

    private static string? ResolveCustomerName(JsonElement payload, JsonElement root)
    {
        return TryGetString(payload, "company_name", "company", "customer_name", "customer", "client_name", "issued_to", "organization", "org_name")
            ?? TryGetString(root, "company_name", "company", "customer_name", "customer", "client_name", "issued_to", "organization", "org_name");
    }

    private static string? ResolveLicenseType(JsonElement payload, JsonElement root)
    {
        return TryGetString(payload, "license_type", "plan", "edition", "package", "product")
            ?? TryGetString(root, "license_type", "plan", "edition", "package", "product");
    }

    private static string? ResolveContactNumber(JsonElement payload, JsonElement root)
    {
        return TryGetString(payload, "contact_number", "contact", "phone", "mobile", "mobile_number")
            ?? TryGetString(root, "contact_number", "contact", "phone", "mobile", "mobile_number");
    }

    private static string? ResolveEmail(JsonElement payload, JsonElement root)
    {
        return TryGetString(payload, "email", "email_address", "mail", "gmail")
            ?? TryGetString(root, "email", "email_address", "mail", "gmail");
    }

    private static int? ResolveMaxDevices(JsonElement payload, JsonElement root, string? licenseType)
    {
        var explicitLimit = TryGetInt32(payload, "max_devices", "maxDevices", "device_limit", "deviceLimit", "allowed_devices", "allowedDevices")
            ?? TryGetInt32(root, "max_devices", "maxDevices", "device_limit", "deviceLimit", "allowed_devices", "allowedDevices");
        if (explicitLimit.HasValue)
        {
            return explicitLimit.Value;
        }

        return NormalizeLicenseType(licenseType) switch
        {
            "basic" => 2,
            "premium" => 5,
            "enterprise" => 10,
            "unlimited" => -1,
            _ => null
        };
    }

    private static string NormalizeLicenseType(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static LicenseValidationResult Invalid(
        string code,
        string message,
        string machineHash,
        DateTimeOffset checkedAtUtc,
        LicenseLocations locations,
        string? licenseMachineHash = null)
    {
        return new LicenseValidationResult(
            false,
            code,
            message,
            machineHash,
            licenseMachineHash,
            null,
            checkedAtUtc,
            null,
            null,
            locations.LicensePath,
            locations.KeyPath,
            locations.PublicKeyPath);
    }

    private static string NormalizeHash(string value)
    {
        var chars = value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }

    private static bool MachineHashesMatch(ISet<string> currentMachineHashes, string licenseMachineHash)
    {
        var expected = NormalizeHash(licenseMachineHash);
        return currentMachineHashes.Contains(expected);
    }

    private static (string EncryptedToken, string FernetKey) ReadActivationMaterial(string keyPath, string? licensePath)
    {
        var keyBytes = File.ReadAllBytes(keyPath);
        if (keyBytes.Length == 0)
        {
            throw new InvalidOperationException("Key file is empty.");
        }

        var keyText = Encoding.UTF8.GetString(keyBytes).Trim();
        if (!string.IsNullOrWhiteSpace(keyText))
        {
            try
            {
                using var activationDoc = JsonDocument.Parse(keyText);
                var root = activationDoc.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    TryGetString(root, "format") is { Length: > 0 } format &&
                    string.Equals(format, "PB-ACTIVATION-V2", StringComparison.OrdinalIgnoreCase))
                {
                    var blobB64 = TryGetString(root, "license_blob_b64", "licenseBlobB64");
                    var fernetKey = TryGetString(root, "fernet_key", "fernetKey");
                    if (string.IsNullOrWhiteSpace(blobB64) || string.IsNullOrWhiteSpace(fernetKey))
                    {
                        throw new InvalidOperationException("Activation key file is missing encrypted payload or key.");
                    }

                    var encryptedBytes = DecodeBase64Flexible(blobB64);
                    var tokenText = Encoding.UTF8.GetString(encryptedBytes).Trim();
                    if (string.IsNullOrWhiteSpace(tokenText))
                    {
                        tokenText = Convert.ToBase64String(encryptedBytes).Trim();
                    }

                    return (tokenText, fernetKey.Trim());
                }
            }
            catch (JsonException)
            {
                // Legacy key format, handled below.
            }
        }

        var legacyFernetKey = ReadKeyFromText(keyText);
        if (string.IsNullOrWhiteSpace(licensePath))
        {
            throw new InvalidOperationException("license.dat is required for legacy key format.");
        }

        var legacyToken = ReadTokenFromFile(licensePath);
        return (legacyToken, legacyFernetKey);
    }

    private static string ReadTokenFromFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("license.dat is empty.");
        }

        var text = Encoding.UTF8.GetString(bytes).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            text = Convert.ToBase64String(bytes).Trim();
        }

        return text;
    }

    private static string ReadKeyFromText(string allText)
    {
        if (string.IsNullOrWhiteSpace(allText))
        {
            throw new InvalidOperationException("Key file is empty.");
        }

        var firstLine = allText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            throw new InvalidOperationException("Key file does not contain a valid key value.");
        }

        return firstLine.Trim();
    }

    private static string? ResolvePublicKeyPem(string? publicKeyPath)
    {
        if (!string.IsNullOrWhiteSpace(publicKeyPath) && File.Exists(publicKeyPath))
        {
            var pem = File.ReadAllText(publicKeyPath).Trim();
            if (!string.IsNullOrWhiteSpace(pem))
            {
                return pem;
            }
        }

        if (!IsPlaceholderPublicKey(EmbeddedPublicKeyPem))
        {
            return EmbeddedPublicKeyPem.Trim();
        }

        return null;
    }

    private static bool IsPlaceholderPublicKey(string value)
    {
        return value.Contains("REPLACE_WITH_YOUR_PUBLIC_KEY_PEM", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement ResolvePayloadElement(JsonElement root, out string? payloadAsText)
    {
        payloadAsText = null;

        if (TryGetPropertyIgnoreCase(root, "payload", out var payloadProperty))
        {
            if (payloadProperty.ValueKind == JsonValueKind.Object)
            {
                return payloadProperty.Clone();
            }

            if (payloadProperty.ValueKind == JsonValueKind.String)
            {
                var payloadText = payloadProperty.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(payloadText))
                {
                    payloadAsText = payloadText;
                    try
                    {
                        using var payloadDoc = JsonDocument.Parse(payloadText);
                        return payloadDoc.RootElement.Clone();
                    }
                    catch
                    {
                        // Keep fallback behavior.
                    }
                }
            }
        }

        if (TryGetPropertyIgnoreCase(root, "license", out var licenseProperty) &&
            licenseProperty.ValueKind == JsonValueKind.Object)
        {
            return licenseProperty.Clone();
        }

        if (TryGetPropertyIgnoreCase(root, "data", out var dataProperty) &&
            dataProperty.ValueKind == JsonValueKind.Object)
        {
            return dataProperty.Clone();
        }

        return root.Clone();
    }

    private static bool VerifySignature(string publicKeyPem, byte[] signature, IReadOnlyList<byte[]> candidates)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem.AsSpan());

        foreach (var candidate in candidates)
        {
            if (candidate.Length == 0)
            {
                continue;
            }

            var saltedHashPayload = Encoding.UTF8.GetBytes(ComputePayloadHashHex(candidate, includeSalt: true));
            if (rsa.VerifyData(saltedHashPayload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                return true;
            }

            var plainHashPayload = Encoding.UTF8.GetBytes(ComputePayloadHashHex(candidate, includeSalt: false));
            if (rsa.VerifyData(plainHashPayload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                return true;
            }

            if (rsa.VerifyData(candidate, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                return true;
            }

            if (rsa.VerifyData(candidate, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<byte[]> BuildSignatureCandidates(
        JsonElement root,
        JsonElement payload,
        string decryptedText,
        string? payloadAsText)
    {
        var candidates = new List<byte[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddCandidate(byte[] value)
        {
            if (value.Length == 0)
            {
                return;
            }

            var key = Convert.ToBase64String(value);
            if (seen.Add(key))
            {
                candidates.Add(value);
            }
        }

        if (!string.IsNullOrWhiteSpace(payloadAsText))
        {
            AddCandidate(Encoding.UTF8.GetBytes(payloadAsText));
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            AddCandidate(Encoding.UTF8.GetBytes(SerializePythonStyleJson(root, skipSignatureProperty: true)));
        }

        if (TryGetString(root, "signed_data", "signed_payload", "signature_payload") is { Length: > 0 } signedText)
        {
            AddCandidate(Encoding.UTF8.GetBytes(signedText));
        }

        if (payload.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            AddCandidate(Encoding.UTF8.GetBytes(payload.GetRawText()));
            AddCandidate(SerializeCanonicalJson(payload, propertyToSkip: null));
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            AddCandidate(Encoding.UTF8.GetBytes(root.GetRawText()));
            AddCandidate(SerializeCanonicalJson(root, propertyToSkip: "signature"));
        }

        AddCandidate(Encoding.UTF8.GetBytes(decryptedText));
        return candidates;
    }

    private static string ComputePayloadHashHex(byte[] payload, bool includeSalt)
    {
        if (includeSalt)
        {
            var combined = new byte[payload.Length + SystemSaltBytes.Length];
            Buffer.BlockCopy(payload, 0, combined, 0, payload.Length);
            Buffer.BlockCopy(SystemSaltBytes, 0, combined, payload.Length, SystemSaltBytes.Length);
            var hash = SHA256.HashData(combined);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static string SerializePythonStyleJson(JsonElement element, bool skipSignatureProperty)
    {
        var builder = new StringBuilder();
        AppendPythonStyleJson(builder, element, skipSignatureProperty: skipSignatureProperty);
        return builder.ToString();
    }

    private static void AppendPythonStyleJson(
        StringBuilder builder,
        JsonElement element,
        bool skipSignatureProperty)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                builder.Append('{');
                var properties = element.EnumerateObject()
                    .Where(p => !(skipSignatureProperty && string.Equals(p.Name, "signature", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToArray();
                for (var i = 0; i < properties.Length; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(JsonSerializer.Serialize(properties[i].Name));
                    builder.Append(": ");
                    AppendPythonStyleJson(builder, properties[i].Value, skipSignatureProperty: false);
                }

                builder.Append('}');
                break;
            }
            case JsonValueKind.Array:
            {
                builder.Append('[');
                var values = element.EnumerateArray().ToArray();
                for (var i = 0; i < values.Length; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    AppendPythonStyleJson(builder, values[i], skipSignatureProperty: false);
                }

                builder.Append(']');
                break;
            }
            case JsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(element.GetString()));
                break;
            case JsonValueKind.Number:
                builder.Append(element.GetRawText());
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                builder.Append("null");
                break;
            default:
                builder.Append(element.GetRawText());
                break;
        }
    }

    private static byte[] SerializeCanonicalJson(JsonElement element, string? propertyToSkip)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalElement(writer, element, propertyToSkip);
            writer.Flush();
        }

        return stream.ToArray();
    }

    private static void WriteCanonicalElement(Utf8JsonWriter writer, JsonElement element, string? propertyToSkip)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(propertyToSkip) &&
                        string.Equals(property.Name, propertyToSkip, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteCanonicalElement(writer, property.Value, propertyToSkip: null);
                }

                writer.WriteEndObject();
                break;
            }
            case JsonValueKind.Array:
            {
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalElement(writer, item, propertyToSkip: null);
                }

                writer.WriteEndArray();
                break;
            }
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
        }
    }

    private static DateTimeOffset? TryGetExpiryUtc(JsonElement payload)
    {
        if (!TryGetPropertyValue(payload, out var expiryValue, "expiry_date", "expiryDate", "expires_at", "expiry", "valid_until"))
        {
            return null;
        }

        if (expiryValue.ValueKind == JsonValueKind.Number)
        {
            if (expiryValue.TryGetInt64(out var unixSeconds))
            {
                try
                {
                    if (unixSeconds > 9_000_000_000L)
                    {
                        return DateTimeOffset.FromUnixTimeMilliseconds(unixSeconds);
                    }

                    return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                }
                catch
                {
                    return null;
                }
            }
        }

        if (expiryValue.ValueKind == JsonValueKind.String)
        {
            var raw = expiryValue.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            if (DateOnly.TryParse(raw, out var dateOnly))
            {
                var endOfDayUtc = DateTime.SpecifyKind(dateOnly.ToDateTime(TimeOnly.MaxValue), DateTimeKind.Utc);
                return new DateTimeOffset(endOfDayUtc);
            }

            if (DateTimeOffset.TryParse(raw, out var dto))
            {
                return dto.ToUniversalTime();
            }

            if (DateTime.TryParse(raw, out var dateTime))
            {
                var utc = dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime.ToUniversalTime();
                return new DateTimeOffset(utc);
            }
        }

        return null;
    }

    private static string? TryGetString(JsonElement element, params string[] names)
    {
        if (!TryGetPropertyValue(element, out var value, names))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static int? TryGetInt32(JsonElement element, params string[] names)
    {
        if (!TryGetPropertyValue(element, out var value, names))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            return numeric;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var token = value.GetString()?.Trim();
            if (int.TryParse(token, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool TryGetPropertyValue(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static byte[] DecodeBase64Flexible(string value)
    {
        var token = value.Trim();
        try
        {
            return Convert.FromBase64String(token);
        }
        catch
        {
            return Base64UrlDecode(token);
        }
    }

    private static byte[] FernetDecrypt(string token, string key)
    {
        var keyBytes = Base64UrlDecode(key);
        if (keyBytes.Length != 32)
        {
            throw new InvalidOperationException("Invalid Fernet key length.");
        }

        var signingKey = keyBytes[..16];
        var encryptionKey = keyBytes[16..];

        var tokenBytes = Base64UrlDecode(token);
        if (tokenBytes.Length < 1 + 8 + 16 + 32)
        {
            throw new InvalidOperationException("Invalid Fernet token length.");
        }

        if (tokenBytes[0] != 0x80)
        {
            throw new InvalidOperationException("Unsupported Fernet token version.");
        }

        var macStart = tokenBytes.Length - 32;
        var signedPortion = tokenBytes.AsSpan(0, macStart);
        var providedMac = tokenBytes.AsSpan(macStart, 32);

        using (var hmac = new HMACSHA256(signingKey))
        {
            var expectedMac = hmac.ComputeHash(signedPortion.ToArray());
            if (!CryptographicOperations.FixedTimeEquals(expectedMac, providedMac))
            {
                throw new InvalidOperationException("Token integrity check failed.");
            }
        }

        var iv = tokenBytes.AsSpan(9, 16).ToArray();
        var cipherText = tokenBytes.AsSpan(25, macStart - 25).ToArray();
        using var aes = Aes.Create();
        aes.Key = encryptionKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        if (padding > 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');
        }

        return Convert.FromBase64String(normalized);
    }

    private (string Preferred, HashSet<string> Variants) ComputeMachineHashes()
    {
        var identifiers = GetMachineIdentifiers();
        var combined = string.Join("|", identifiers);
        var raw = Encoding.UTF8.GetBytes(combined);

        var unsalted = Convert.ToHexString(SHA256.HashData(raw)).ToUpperInvariant();
        var saltedRaw = new byte[raw.Length + SystemSaltBytes.Length];
        Buffer.BlockCopy(raw, 0, saltedRaw, 0, raw.Length);
        Buffer.BlockCopy(SystemSaltBytes, 0, saltedRaw, raw.Length, SystemSaltBytes.Length);
        var salted = Convert.ToHexString(SHA256.HashData(saltedRaw)).ToUpperInvariant();

        var variants = new HashSet<string>(StringComparer.Ordinal)
        {
            NormalizeHash(unsalted),
            NormalizeHash(salted),
        };

        return (salted, variants);
    }

    private static IReadOnlyList<string> GetMachineIdentifiers()
    {
        var list = new List<string>();

        AddIfPresent(list, TryReadWmiValue("Win32_Bios", "SerialNumber"));
        AddIfPresent(list, TryReadWmiValue("Win32_BaseBoard", "SerialNumber"));
        AddIfPresent(list, TryReadWmiValue("Win32_Processor", "ProcessorId"));
        AddIfPresent(list, TryReadMachineGuid());
        AddIfPresent(list, Environment.GetEnvironmentVariable("COMPUTERNAME"));
        AddIfPresent(list, Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var item in list)
        {
            if (seen.Add(item))
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static void AddIfPresent(ICollection<string> list, string? value)
    {
        var normalized = NormalizeIdentifier(value);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            list.Add(normalized);
        }
    }

    private static string NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = Regex.Replace(value.Trim().ToUpperInvariant(), @"\s+", " ");
        return cleaned;
    }

    private static string? TryReadWmiValue(string className, string propertyName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"Get-CimInstance {className} | Select-Object -ExpandProperty {propertyName}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore kill failure.
                }
            }

            var trimmed = output.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return null;
            }

            return trimmed
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string TryReadMachineGuid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var value = key?.GetValue("MachineGuid")?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
        catch
        {
            // Fallback below.
        }

        return string.Empty;
    }

    private LicenseLocations ResolveLicenseLocations()
    {
        var directories = new List<string>();

        void AddDir(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var full = Path.GetFullPath(value);
            if (directories.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            directories.Add(full);
        }

        AddDir(_options.LicenseDirectory);
        AddDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PB_Attendance"));
        AddDir(Path.Combine(_options.AppDataRoot, "license"));
        AddDir(_options.BaseDirectory);
        AddDir(_options.ContentRootPath);

        string? licensePath = null;
        string? keyPath = null;
        string? publicKeyPath = null;

        foreach (var dir in directories)
        {
            var candidate = Path.Combine(dir, "license.dat");
            if (File.Exists(candidate))
            {
                licensePath = candidate;
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(licensePath))
        {
            var licenseDir = Path.GetDirectoryName(licensePath) ?? string.Empty;
            keyPath = ResolveKeyFile(licenseDir) ?? ResolveKeyFileFromDirectories(directories);
            publicKeyPath = ResolvePublicKeyPath(licenseDir);
        }
        else
        {
            keyPath = ResolveKeyFileFromDirectories(directories);
            publicKeyPath = ResolvePublicKeyPathFromDirectories(directories);
        }

        if (!string.IsNullOrWhiteSpace(_options.PublicKeyPath) && File.Exists(_options.PublicKeyPath))
        {
            publicKeyPath = Path.GetFullPath(_options.PublicKeyPath);
        }

        return new LicenseLocations(licensePath, keyPath, publicKeyPath, directories);
    }

    private static string? ResolveKeyFileFromDirectories(IEnumerable<string> directories)
    {
        foreach (var dir in directories)
        {
            var key = ResolveKeyFile(dir);
            if (!string.IsNullOrWhiteSpace(key))
            {
                return key;
            }
        }

        return null;
    }

    private static string? ResolvePublicKeyPathFromDirectories(IEnumerable<string> directories)
    {
        foreach (var dir in directories)
        {
            var key = ResolvePublicKeyPath(dir);
            if (!string.IsNullOrWhiteSpace(key))
            {
                return key;
            }
        }

        return null;
    }

    private static string? ResolvePublicKeyPath(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var path = Path.Combine(directory, "public_key.pem");
        return File.Exists(path) ? path : null;
    }

    private static string? ResolveKeyFile(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var preferred = Directory
            .EnumerateFiles(directory, "*.key", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).StartsWith("PB-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred;
        }

        return Directory
            .EnumerateFiles(directory, "*.key", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private sealed record LicenseLocations(
        string? LicensePath,
        string? KeyPath,
        string? PublicKeyPath,
        IReadOnlyList<string> SearchedDirectories);
}
