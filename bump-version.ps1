param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$assemblyVersion = "$Version.0"

# ── .NET project files ────────────────────────────────────────────────────────
$projectFiles = @(
    Join-Path $root "E.Attendance.Web\E.Attendance.Web.csproj"
    Join-Path $root "E.Attendance\E.Attendance.csproj"
)

foreach ($file in $projectFiles) {
    $text = Get-Content -LiteralPath $file -Raw
    $text = [regex]::Replace($text, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
    $text = [regex]::Replace($text, '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$assemblyVersion</FileVersion>")
    $text = [regex]::Replace($text, '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$assemblyVersion</AssemblyVersion>")
    Set-Content -LiteralPath $file -Value $text -NoNewline
    Write-Host "  Updated: $(Split-Path -Leaf $file)"
}

# ── Frontend package files ────────────────────────────────────────────────────
$pkgFile = Join-Path $root "e-attendance-next\package.json"
if (Test-Path $pkgFile) {
    $pkg = Get-Content -LiteralPath $pkgFile -Raw
    $pkg = [regex]::Replace($pkg, '("version"\s*:\s*")[^"]+(")', "`${1}$Version`$2", 1)
    Set-Content -LiteralPath $pkgFile -Value $pkg -NoNewline
    Write-Host "  Updated: package.json"
}

$lockFile = Join-Path $root "e-attendance-next\package-lock.json"
if (Test-Path $lockFile) {
    $lock = Get-Content -LiteralPath $lockFile -Raw
    $lock = [regex]::Replace($lock, '("version"\s*:\s*")[^"]+(")', "`${1}$Version`$2", 1)
    Set-Content -LiteralPath $lockFile -Value $lock -NoNewline
    Write-Host "  Updated: package-lock.json"
}

# ── Inno Setup installer script ───────────────────────────────────────────────
$issFile = Join-Path $root "installer\E_Attendace.Setup.iss"
$iss = Get-Content -LiteralPath $issFile -Raw
$iss = [regex]::Replace($iss, '(#define MyAppVersion\s+")[^"]+(")', "`${1}$Version`$2")
Set-Content -LiteralPath $issFile -Value $iss -NoNewline
Write-Host "  Updated: $(Split-Path -Leaf $issFile)"

Write-Host ""
Write-Host "Version bumped to $Version across all files."
Write-Host "Next: Rebuild Release in VS, then compile installer\E_Attendace.Setup.iss with ISCC."
