$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location $projectRoot

$project = Join-Path $projectRoot "src\CodexManager.App\CodexManager.App.csproj"
$publishDir = Join-Path $projectRoot "dist\CodexManager"
$fallbackPublishDir = Join-Path $projectRoot "dist\CodexManager.new"

if (-not (Test-Path $project)) {
    throw "No se encontro el proyecto C# esperado en src\CodexManager.App."
}

function Reset-Directory([string]$path) {
    $fullPath = [System.IO.Path]::GetFullPath($path)
    $distRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "dist"))
    if (-not $fullPath.StartsWith($distRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Se rechazo una ruta de salida fuera de dist: $fullPath"
    }

    if (Test-Path -LiteralPath $fullPath) {
        $item = Get-Item -LiteralPath $fullPath -Force
        if (-not $item.PSIsContainer -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "La salida no es un directorio ordinario: $fullPath"
        }
        Get-ChildItem -LiteralPath $fullPath -Force | Out-Null
        Remove-Item -LiteralPath $fullPath -Recurse -Force
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

$dll = Join-Path $targetDir "CodexManager.dll"
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
    throw "dotnet publish termino pero no se encontro CodexManager.dll."
}

$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$shortcutPath = Join-Path $targetDir "CodexManager.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $dotnet
$shortcut.Arguments = '"' + $dll + '"'
$shortcut.WorkingDirectory = $targetDir
$shortcut.IconLocation = (Join-Path $targetDir "assets\icon.ico") + ",0"
$shortcut.Description = "CodexManager"
$shortcut.WindowStyle = 7
$shortcut.Save()

Write-Host "Build completado: $shortcutPath"
Write-Host "Ensamblado: $dll"
