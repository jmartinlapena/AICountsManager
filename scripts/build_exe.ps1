$ErrorActionPreference = "Stop"

$projectRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Set-Location $projectRoot

$project = Join-Path $projectRoot "src\CodexManager.App\CodexManager.App.csproj"
$publishDir = Join-Path $projectRoot "dist\CodexManager"
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "dist"))
$expectedAppHostHash = "FDB675DC32AE35AC5391835B018E52858C7253824A610FD5F4D0B65DE95FBAC1"
$compatibilityRevision = "9520f70064d40465c8ad24b8187863a677e2849b"

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "No se encontro el proyecto C# esperado en src\CodexManager.App."
}

function Assert-OrdinaryDirectoryTree([string]$path) {
    $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if (-not $item.PSIsContainer -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        throw "La ruta no es un directorio ordinario: $path"
    }

    $links = @(Get-ChildItem -LiteralPath $path -Force -Recurse -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($links.Count -gt 0) {
        throw "El directorio contiene enlaces o junctions y no se modificara: $path"
    }
}

function Assert-PublishDirectoryNotInUse([string]$path) {
    $fullPath = [System.IO.Path]::GetFullPath($path)
    $exe = Join-Path $fullPath "CodexManager.exe"
    $dll = Join-Path $fullPath "CodexManager.dll"
    $users = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
        ($_.ExecutablePath -and [string]::Equals($_.ExecutablePath, $exe, [System.StringComparison]::OrdinalIgnoreCase)) -or
        ($_.CommandLine -and $_.CommandLine.IndexOf($dll, [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
    })
    if ($users.Count -gt 0) {
        throw "CodexManager esta abierto. Cierralo antes de compilar; no se creara una instalacion alternativa."
    }
}

function Reset-PublishDirectory([string]$path) {
    $fullPath = [System.IO.Path]::GetFullPath($path)
    $parent = [System.IO.Directory]::GetParent($fullPath).FullName
    if (-not [string]::Equals($parent, $distRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([System.IO.Path]::GetFileName($fullPath), "CodexManager", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Se rechazo una ruta de publicacion inesperada: $fullPath"
    }

    if (Test-Path -LiteralPath $fullPath) {
        Assert-OrdinaryDirectoryTree $fullPath
        Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction Stop
    }
    New-Item -ItemType Directory -Path $fullPath -ErrorAction Stop | Out-Null
}

function Remove-BuildTemporaryDirectory([string]$path, [string]$temporaryRoot) {
    $fullPath = [System.IO.Path]::GetFullPath($path)
    $parent = [System.IO.Directory]::GetParent($fullPath).FullName
    if (-not [string]::Equals($parent, $temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Path]::GetFileName($fullPath).StartsWith("CodexManager-build-", [System.StringComparison]::Ordinal)) {
        throw "Se rechazo una ruta temporal inesperada: $fullPath"
    }

    if (Test-Path -LiteralPath $fullPath) {
        Assert-OrdinaryDirectoryTree $fullPath
        Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction Stop
    }
}

$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar)
$temporaryDir = Join-Path $temporaryRoot ("CodexManager-build-" + [guid]::NewGuid().ToString("N"))
$compatibilityDir = Join-Path $temporaryDir "compatibility-host"
$packageDir = Join-Path $temporaryDir "package"

Assert-PublishDirectoryNotInUse $publishDir
New-Item -ItemType Directory -Path $compatibilityDir -ErrorAction Stop | Out-Null
New-Item -ItemType Directory -Path $packageDir -ErrorAction Stop | Out-Null

try {
    dotnet restore $project -r win-x64 -p:UseAppHost=true
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudieron preparar las dependencias de Windows antes de compilar."
    }
    Write-Host "Generando apphost compatible..."
    dotnet clean $project -c Release -r win-x64 --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet clean fallo antes de generar el apphost."
    }

    dotnet publish $project `
        -c Release `
        -r win-x64 `
        --self-contained false `
        -p:UseAppHost=true `
        -p:ApplicationIcon=..\..\assets\icon.ico `
        -p:Version=1.0.0 `
        -p:FileVersion=1.0.0.0 `
        -p:AssemblyVersion=1.0.0.0 `
        -p:InformationalVersion=1.0.0+$compatibilityRevision `
        -p:IncludeSourceRevisionInInformationalVersion=false `
        -o $compatibilityDir
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudo generar el apphost compatible."
    }

    $compatibilityExe = Join-Path $compatibilityDir "CodexManager.exe"
    if (-not (Test-Path -LiteralPath $compatibilityExe -PathType Leaf)) {
        throw "La publicacion compatible no genero CodexManager.exe."
    }
    $appHostHash = (Get-FileHash -LiteralPath $compatibilityExe -Algorithm SHA256).Hash
    if (-not [string]::Equals($appHostHash, $expectedAppHostHash, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "El apphost no coincide con la huella validada para este equipo. Esperada: $expectedAppHostHash. Obtenida: $appHostHash."
    }

    Write-Host "Publicando CodexManager 2.3.5..."
    dotnet clean $project -c Release -r win-x64 --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet clean fallo antes de publicar la aplicacion."
    }

    dotnet publish $project `
        -c Release `
        -r win-x64 `
        --self-contained false `
        -p:UseAppHost=false `
        -o $packageDir
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudo publicar CodexManager."
    }

    $packageDll = Join-Path $packageDir "CodexManager.dll"
    if (-not (Test-Path -LiteralPath $packageDll -PathType Leaf)) {
        throw "La publicacion no genero CodexManager.dll."
    }
    $applicationVersion = [System.Reflection.AssemblyName]::GetAssemblyName($packageDll).Version.ToString()
    if (-not [string]::Equals($applicationVersion, "2.3.5.0", [System.StringComparison]::Ordinal)) {
        throw "La DLL publicada no tiene la version esperada 2.3.5.0: $applicationVersion"
    }

    Copy-Item -LiteralPath $compatibilityExe -Destination (Join-Path $packageDir "CodexManager.exe") -ErrorAction Stop
    Assert-OrdinaryDirectoryTree $packageDir

    Assert-PublishDirectoryNotInUse $publishDir
    Reset-PublishDirectory $publishDir
    foreach ($item in Get-ChildItem -LiteralPath $packageDir -Force -ErrorAction Stop) {
        Copy-Item -LiteralPath $item.FullName -Destination $publishDir -Recurse -Force -ErrorAction Stop
    }

    $exe = Join-Path $publishDir "CodexManager.exe"
    $dll = Join-Path $publishDir "CodexManager.dll"
    $deployedHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    $deployedVersion = [System.Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString()
    if (-not [string]::Equals($deployedHash, $expectedAppHostHash, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($deployedVersion, "2.3.5.0", [System.StringComparison]::Ordinal)) {
        throw "La verificacion final del paquete fallo."
    }

    $shortcutPath = Join-Path $publishDir "CodexManager.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exe
    $shortcut.Arguments = ""
    $shortcut.WorkingDirectory = $publishDir
    $shortcut.IconLocation = (Join-Path $publishDir "assets\icon.ico") + ",0"
    $shortcut.Description = "CodexManager 2.3.5"
    $shortcut.WindowStyle = 7
    $shortcut.Save()

    Write-Host "Build completado: $exe"
    Write-Host "Acceso directo: $shortcutPath"
    Write-Host "Aplicacion: CodexManager.dll $deployedVersion"
    Write-Host "Apphost compatible: $deployedHash"
}
finally {
    if (Test-Path -LiteralPath $temporaryDir) {
        Remove-BuildTemporaryDirectory $temporaryDir $temporaryRoot
    }
}
