param([string]$BaseUrl = "http://127.0.0.1:8080")
$ErrorActionPreference = "Stop"

$health = Invoke-RestMethod -Uri "$BaseUrl/health" -Method Get
if ($health.ok -ne "true") { throw "health endpoint did not return ok=true" }

$passports = Invoke-RestMethod -Uri "$BaseUrl/passports" -Method Get
$dashboard = Invoke-WebRequest -Uri "$BaseUrl/dashboard" -UseBasicParsing
if ($dashboard.StatusCode -ne 200 -or $dashboard.Content -notmatch "Green Passport") {
    throw "dashboard is unavailable or does not contain the expected title"
}

try {
    Invoke-WebRequest -Uri "$BaseUrl/passports" -Method Post -ContentType "application/json" -Body "{}" -UseBasicParsing | Out-Null
    throw "browser-like write unexpectedly succeeded"
}
catch {
    if ($_.Exception.Response.StatusCode.value__ -ne 403) { throw }
}

Write-Host "SMOKE PASS: health, read-only list/dashboard, and browser write protection"
Write-Host ("Passport count: " + $passports.Count)
