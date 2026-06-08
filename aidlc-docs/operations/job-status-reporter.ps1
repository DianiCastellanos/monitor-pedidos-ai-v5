# job-status-reporter.ps1
# Ejecutar en SR-SDEV02CO cada 5 minutos via Task Scheduler
# Consulta schtasks localmente y reporta el estado al app en Render
#
# Configuración en Task Scheduler:
#   Acción:    powershell.exe -ExecutionPolicy Bypass -File "C:\Scripts\job-status-reporter.ps1"
#   Trigger:   Repetir cada 5 minutos, indefinidamente
#   Cuenta:    Usuario con permisos para schtasks /QUERY local

param(
    [string]$AppUrl    = "https://monitor-pedidos.onrender.com",   # URL del despliegue en Render
    [string]$ApiKey    = "",                                        # Valor de JobsReport__ApiKey en .env
    [string]$TaskName  = "OC_PATPRIMO"                             # Nombre exacto del job en Task Scheduler
)

$ErrorActionPreference = "Stop"

# 1. Consultar Task Scheduler local
try {
    $result = schtasks /QUERY /TN $TaskName /FO CSV /NH 2>&1
    $exitCode = $LASTEXITCODE
} catch {
    $result   = $_.Exception.Message
    $exitCode = 1
}

# 2. Mapear estado de schtasks → status del endpoint
$status       = "Failed"
$errorMessage = $null

if ($exitCode -eq 0 -and $result) {
    # Formato CSV: "TaskName","Next Run","Status","Last Run","Last Result"
    $parts     = ($result -split ',')
    $rawStatus = if ($parts.Count -ge 3) { $parts[2].Trim('"').Trim() } else { "Unknown" }

    switch -Wildcard ($rawStatus) {
        "Ready"    { $status = "Ready" }
        "Running"  { $status = "Running" }
        "Disabled" { $status = "Disabled"; $errorMessage = "Job deshabilitado en Task Scheduler" }
        default    { $status = "Failed";   $errorMessage = "Job en estado inesperado: $rawStatus" }
    }
} else {
    $errorMessage = "schtasks /QUERY falló (exit=$exitCode): $result"
}

# 3. Enviar reporte al endpoint de Render
$body = @{
    jobName      = $TaskName
    status       = $status
    errorMessage = $errorMessage
} | ConvertTo-Json -Compress

try {
    $response = Invoke-RestMethod `
        -Uri         "$AppUrl/api/jobs/report" `
        -Method      POST `
        -ContentType "application/json" `
        -Headers     @{ "X-Jobs-Report-Key" = $ApiKey } `
        -Body        $body

    Write-Host "[OK] $TaskName reportado como '$status' → $AppUrl"
} catch {
    $statusCode = $_.Exception.Response.StatusCode.value__
    Write-Warning "[ERROR] POST /api/jobs/report falló (HTTP $statusCode): $_"
    exit 1
}
