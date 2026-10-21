param([Parameter(Mandatory = $true)][string] $LiteralPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) { throw 'Installer file is missing' }
$signature = Get-AuthenticodeSignature -LiteralPath $LiteralPath
$version = (Get-Item -LiteralPath $LiteralPath).VersionInfo
function CertificateDetails($certificate) {
    if ($null -eq $certificate) { return $null }
    return @{ subject = $certificate.Subject; thumbprint = $certificate.Thumbprint }
}
@{
    status = [string]$signature.Status
    signatureType = [string]$signature.SignatureType
    signer = CertificateDetails $signature.SignerCertificate
    timestamp = CertificateDetails $signature.TimeStamperCertificate
    productName = $version.ProductName
    fileVersion = $version.FileVersion
} | ConvertTo-Json -Depth 4 -Compress
