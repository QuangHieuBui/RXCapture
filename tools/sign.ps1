# Signs .exe files with a code-signing certificate (Authenticode + timestamp).
#   powershell -ExecutionPolicy Bypass -File tools\sign.ps1 <file> [<file> ...]
#   powershell -ExecutionPolicy Bypass -File tools\sign.ps1 -CreateOnly        (just create the certificate)
#   powershell -ExecutionPolicy Bypass -File tools\sign.ps1 -Thumbprint <sha1> <file>   (use a certificate you bought)
#
# Without -Thumbprint a SELF-SIGNED certificate "RXCapture" is used (created on first run in Cert:\CurrentUser\My, valid 5 years).
# A self-signed signature proves the files were not changed after signing, but Windows only shows "Verified publisher" on
# computers that trust the certificate (export it with -ExportCer and import it there). To remove the SmartScreen warning for
# everybody you need a certificate from a public authority (OV/EV code-signing certificate, or Azure Trusted Signing) -
# then pass its thumbprint.
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)][string[]]$Files,
    [string]$Thumbprint,
    [string]$Subject = 'CN=RXCapture, O=RXCapture',
    [string]$TimestampServer = 'http://timestamp.digicert.com',
    [switch]$CreateOnly,
    [string]$ExportCer,
    [string]$ExportPfx,
    [string]$PfxPassword
)
$ErrorActionPreference = 'Stop'

function Get-SigningCert {
    if ($Thumbprint) {
        $c = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $Thumbprint } | Select-Object -First 1
        if (-not $c) { throw "Certificate $Thumbprint was not found in the certificate store" }
        return $c
    }
    $now = Get-Date
    $c = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
         Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt $now.AddDays(30) -and $_.HasPrivateKey } |
         Sort-Object NotAfter -Descending | Select-Object -First 1
    if ($c) { return $c }
    Write-Host "Creating a self-signed code-signing certificate ($Subject)..."
    return New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -KeyExportPolicy Exportable -CertStoreLocation Cert:\CurrentUser\My -NotAfter $now.AddYears(5) -KeyUsage DigitalSignature
}

$cert = Get-SigningCert
Write-Host ("Certificate: {0}  thumbprint {1}  valid until {2:yyyy-MM-dd}" -f $cert.Subject, $cert.Thumbprint, $cert.NotAfter)

if ($ExportCer) { Export-Certificate -Cert $cert -FilePath $ExportCer -Force | Out-Null; Write-Host "Public certificate written to $ExportCer" }
if ($ExportPfx) {
    if (-not $PfxPassword) { throw 'Give -PfxPassword to protect the private key file' }
    Export-PfxCertificate -Cert $cert -FilePath $ExportPfx -Password (ConvertTo-SecureString $PfxPassword -AsPlainText -Force) -Force | Out-Null
    Write-Host "Private key backup written to $ExportPfx - keep it secret"
}
if ($CreateOnly) { return }

if (-not $Files -or $Files.Count -eq 0) { throw 'No file to sign' }
$failed = 0
foreach ($f in $Files) {
    $r = $null
    try { $r = Set-AuthenticodeSignature -FilePath $f -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $TimestampServer }
    catch { $r = $null }
    if ($null -eq $r -or -not $r.SignerCertificate) {
        # no internet / timestamp server down: sign without a timestamp (the signature then ends when the certificate expires)
        Write-Warning "Timestamping failed for $f - signing without a timestamp"
        $r = Set-AuthenticodeSignature -FilePath $f -Certificate $cert -HashAlgorithm SHA256
    }
    $sig = Get-AuthenticodeSignature $f
    $ts = if ($sig.TimeStamperCertificate) { 'timestamped' } else { 'no timestamp' }
    # a self-signed certificate reports UnknownError/NotTrusted here until it is trusted; the signature itself is present and intact
    Write-Host ("Signed {0}  [{1}, {2}]" -f $f, $sig.Status, $ts)
    if (-not $sig.SignerCertificate) { $failed++ }
}
if ($failed -gt 0) { throw "$failed file(s) could not be signed" }
