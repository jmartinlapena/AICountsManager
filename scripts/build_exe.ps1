$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location $projectRoot

$project = Join-Path $projectRoot "src\CodexManager.App\CodexManager.App.csproj"
$publishDir = Join-Path $projectRoot "dist\CodexManager"
$fallbackPublishDir = Join-Path $projectRoot "dist\CodexManager.new"

if (-not (Test-Path $project)) {
    throw "No se encontro el proyecto C# esperado en src\CodexManager.App."
}

function Reset-Directory($path) {
    if (Test-Path $path) {
        Remove-Item -Recurse -Force $path
    }
}

$targetDir = $publishDir
try {
    Reset-Directory $publishDir
} catch {
    Write-Warning "No se pudo limpiar dist\CodexManager. Se publicara en dist\CodexManager.new. Cierra cualquier instancia abierta si quieres reutilizar la ruta principal."
    $targetDir = $fallbackPublishDir
    Reset-Directory $fallbackPublishDir
}

Write-Host "Publicando CodexManager..."
dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o $targetDir

$exe = Join-Path $targetDir "CodexManager.exe"
if (-not (Test-Path $exe)) {
    throw "dotnet publish termino pero no se encontro el ejecutable esperado."
}

Write-Host "Build completado: $exe"
