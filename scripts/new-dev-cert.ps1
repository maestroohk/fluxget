param(
    [string]$OutDir = (Join-Path $env:LOCALAPPDATA 'FluxGet-signing'),
    [string]$Subject = 'CN=FluxGet Development Signing'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$pfx = Join-Path $OutDir 'fluxget-dev-signing.pfx'
$passFile = Join-Path $OutDir 'fluxget-dev-signing.pass'
if (Test-Path $pfx) { Write-Host "Certificate already exists: $pfx"; return }

$rsa = [System.Security.Cryptography.RSA]::Create(3072)
$req = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest($Subject, $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
$eku = New-Object System.Security.Cryptography.OidCollection
$eku.Add((New-Object System.Security.Cryptography.Oid '1.3.6.1.5.5.7.3.3')) | Out-Null
$req.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($eku, $true)))
$req.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509KeyUsageExtension([System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true)))
$cert = $req.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddYears(2))

$bytes = New-Object byte[] 24
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$password = [Convert]::ToBase64String($bytes)
[IO.File]::WriteAllBytes($pfx, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password))
[IO.File]::WriteAllText($passFile, $password)
Write-Host "Created self-signed code-signing certificate (not trusted by Windows): $pfx"
Write-Host "Thumbprint: $($cert.Thumbprint)"
