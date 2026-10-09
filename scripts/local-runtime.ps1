[CmdletBinding()]
param(
    [ValidateSet('up', 'stop', 'status', 'check', 'logs')]
    [string]$Command = 'up',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskLinuxRoot = '/home/rrojacam/projects/acropolis-channel'
$taskCertPath = Join-Path $taskRoot '.local/runtime/export/localhost.crt'
function Invoke-LocalLinux {
    param([string]$Action, [switch]$ReuseImages)
    $taskArguments = @('-d', 'Ubuntu-24.04', '--cd', $taskLinuxRoot, '--exec', '/usr/bin/python3', 'scripts/local-runtime.py', $Action)
    if ($ReuseImages) { $taskArguments += '--no-build' }
    & wsl.exe @taskArguments
    if ($LASTEXITCODE -ne 0) { throw "La operación local $Action falló. Revisar el registro privado de WSL." }
}
if ($Command -eq 'up') {
    # Stop only this owned local stack, preserving data, before probing Windows.
    if (Test-Path -LiteralPath (Join-Path $taskRoot '.local/runtime/.env')) {
        Invoke-LocalLinux -Action stop
    }
    foreach ($taskPort in @(17480, 17443, 17425)) {
        $taskProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $taskPort)
        try { $taskProbe.Start() }
        catch { throw "El puerto fijo $taskPort está ocupado en Windows. No se elegirá otro puerto." }
        finally { $taskProbe.Stop() }
    }
    Invoke-LocalLinux -Action up -ReuseImages:$NoBuild
    $taskCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem([IO.File]::ReadAllText($taskCertPath))
    try {
        $taskConstraints = $taskCertificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' } | Select-Object -First 1
        $taskBasic = [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($taskConstraints.RawData, $taskConstraints.Critical)
        $taskSan = $taskCertificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' } | Select-Object -First 1
        if (-not $taskSan) { throw 'El certificado local no tiene SAN.' }
        $taskNames = [Security.Cryptography.X509Certificates.X509SubjectAlternativeNameExtension]::new($taskSan.RawData, $taskSan.Critical)
        $taskDns = @($taskNames.EnumerateDnsNames())
        $taskIps = @($taskNames.EnumerateIPAddresses())
        $taskLoopbackV4 = @($taskIps | Where-Object { $_.Equals([Net.IPAddress]::Loopback) }).Count
        $taskLoopbackV6 = @($taskIps | Where-Object { $_.Equals([Net.IPAddress]::IPv6Loopback) }).Count
        if ($taskCertificate.Subject -ne 'CN=Acropolis Localhost Development' -or $taskCertificate.Issuer -ne $taskCertificate.Subject -or $taskBasic.CertificateAuthority -or $taskDns.Count -ne 1 -or $taskDns[0] -cne 'localhost' -or $taskIps.Count -ne 2 -or $taskLoopbackV4 -ne 1 -or $taskLoopbackV6 -ne 1 -or $taskCertificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow -or $taskCertificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) {
            throw 'El certificado no corresponde a la identidad local prevista.'
        }
        $taskTrusted = Test-Path -LiteralPath ('Cert:\CurrentUser\Root\' + $taskCertificate.Thumbprint)
        if (-not $taskTrusted) {
            Import-Certificate -FilePath $taskCertPath -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
        }
        @{ thumbprint=$taskCertificate.Thumbprint; subject=$taskCertificate.Subject; store='CurrentUser/Root'; scope='localhost leaf only'; checkedAt=[DateTime]::UtcNow.ToString('o') } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRoot '.local/runtime/windows-trust.json') -Encoding utf8
    }
    finally { $taskCertificate.Dispose() }
}
elseif ($Command -ne 'check') { Invoke-LocalLinux -Action $Command }
if ($Command -in @('up', 'check')) {
    if ($Command -eq 'check') { Invoke-LocalLinux -Action check }
    $taskHandler = [Net.Http.HttpClientHandler]::new()
    $taskHandler.AllowAutoRedirect = $false
    $taskClient = [Net.Http.HttpClient]::new($taskHandler)
    $taskClient.Timeout = [TimeSpan]::FromSeconds(15)
    try {
        $taskReady = $taskClient.GetAsync('https://localhost:17443/health/ready').GetAwaiter().GetResult()
        $taskReady.EnsureSuccessStatusCode() | Out-Null
        $taskRedirect = $taskClient.GetAsync('http://localhost:17480/').GetAwaiter().GetResult()
        if ([int]$taskRedirect.StatusCode -notin @(301, 308) -or $taskRedirect.Headers.Location.AbsoluteUri -ne 'https://localhost:17443/') {
            throw 'La redirección HTTP local no corresponde al puerto HTTPS fijo.'
        }
        $taskMail = $taskClient.GetAsync('http://localhost:17425/api/v1/messages').GetAwaiter().GetResult()
        $taskMail.EnsureSuccessStatusCode() | Out-Null
        Write-Output 'Aplicación: https://localhost:17443 | Buzón local: http://localhost:17425 | HTTPS verificado desde Windows.'
    }
    finally { $taskClient.Dispose(); $taskHandler.Dispose() }
}
