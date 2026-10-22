param(
    [ValidateSet('win-x64','win-arm64')][string[]]$Runtime = @('win-x64','win-arm64'),
    [string]$Dotnet = 'dotnet',
    [string]$Iscc = '',
    [switch]$SkipTests,
    [switch]$SkipLaunchTest,
    [string]$SigningThumbprint = '',
    [string]$SignTool = 'signtool.exe'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Set-Location $projectRoot
$project = Join-Path $projectRoot 'src/AicountsManager.App/AicountsManager.App.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version de producto no valida.' }
if (-not $Iscc) {
    $candidate = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($candidate) { $Iscc = $candidate.Source }
    else { $Iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe' }
}
if (-not (Test-Path -LiteralPath $Iscc -PathType Leaf)) { throw 'Instala Inno Setup 6 o proporciona -Iscc. Solo lo necesita el equipo que genera la distribucion.' }
$release = Join-Path $projectRoot "artifacts/releases/$version"
New-Item -ItemType Directory -Force -Path $release | Out-Null
if (-not $SkipTests) {
    & $Dotnet run --project src/AicountsManager.Tests/AicountsManager.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Las pruebas fallaron. No se genera la distribucion.' }
}
foreach ($rid in $Runtime) {
    $packageName = "AicountsManager-$version-$rid"
    $package = Join-Path $release ($packageName + '-files')
    if (Test-Path -LiteralPath $package) { throw "Ya existe $package. Usa una version nueva o retira esa carpeta de salida antes de repetir." }
    & $Dotnet publish $project -c Release -r $rid --self-contained true -p:UseAppHost=true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true -o $package --nologo
    if ($LASTEXITCODE -ne 0) { throw "Fallo publicando $rid." }
    # Redistribute the license/notice files belonging to the exact bundled runtime packs.
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $package 'AicountsManager.runtimeconfig.json') -Raw | ConvertFrom-Json
    $assets = Get-Content -LiteralPath (Join-Path $projectRoot 'src/AicountsManager.App/obj/project.assets.json') -Raw | ConvertFrom-Json
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        $packId = ($framework.name + '.runtime.' + $rid).ToLowerInvariant()
        $pack = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ "$packId/$($framework.version)" } | Where-Object { Test-Path -LiteralPath $_ -PathType Container }) | Select-Object -First 1
        if (-not $pack) { throw "No se encuentra la licencia del runtime: $packId" }
        $notices = @(Get-ChildItem -LiteralPath $pack -File | Where-Object { $_.Name -match '^(LICENSE(\.TXT)?|THIRD-PARTY-NOTICES\.TXT)$' })
        if (-not $notices) { throw "El runtime no incluye archivos de licencia: $packId" }
        $licenseDir = Join-Path $package "licenses/$($framework.name)"
        New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null
        $notices | Copy-Item -Destination $licenseDir
    }
    $exe = Join-Path $package 'AicountsManager.exe'
    $dll = Join-Path $package 'AicountsManager.dll'
    if ([Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString() -ne "$version.0") { throw 'Version de DLL incorrecta.' }
    foreach ($file in @('hostfxr.dll','coreclr.dll','PresentationFramework.dll','assets/icon.ico')) {
        if (-not (Test-Path -LiteralPath (Join-Path $package $file))) { throw "Dependencia autocontenida ausente: $file" }
    }
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($exe))
    try { $reader.BaseStream.Position = 0x3c; $pe = $reader.ReadInt32(); $reader.BaseStream.Position = $pe + 4; $machine = $reader.ReadUInt16() } finally { $reader.Dispose() }
    $expected = if ($rid -eq 'win-x64') { 0x8664 } else { 0xAA64 }
    if ($machine -ne $expected) { throw 'Arquitectura del lanzador incorrecta.' }
    if ($SigningThumbprint) {
        foreach ($binary in @($exe,$dll)) {
            & $SignTool sign /sha1 $SigningThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $binary
            if ($LASTEXITCODE -ne 0) { throw 'Fallo de firma de la aplicacion.' }
        }
    }
    # Smoke test only a native package. Never read profiles or start a background instance here.
    $native = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $launchValidated = $false
    if ($rid -eq "win-$native" -and -not $SkipLaunchTest) {
        $report = Join-Path $release "$packageName-self-test.json"
        $probe = Start-Process -FilePath $exe -ArgumentList @('--self-test',('"'+$report+'"')) -WindowStyle Hidden -PassThru
        if (-not $probe.WaitForExit(30000)) { $probe.Kill(); throw 'La prueba de arranque no finalizo en 30 segundos.' }
        if ($probe.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) { throw 'El paquete no supera la prueba de arranque.' }
        $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        if (-not $result.UiLoaded -or -not $result.IconLoaded -or -not $result.SqliteLoaded -or $result.Version -ne "$version.0") { throw 'Prueba de dependencias incompleta.' }
        $launchValidated = $true
    }
    $arch = if ($rid -eq 'win-arm64') { 'arm64' } else { 'x64compatible' }
    & $Iscc "/DAppVersion=$version" "/DPackageDir=$package" "/DOutputDir=$release" "/DPackageName=$packageName" "/DTargetArchitecture=$arch" (Join-Path $projectRoot 'installer/AicountsManager.iss')
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo generar el instalador.' }
    $installer = Join-Path $release "$packageName-Setup.exe"
    if ($SigningThumbprint) {
        & $SignTool sign /sha1 $SigningThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $installer
        if ($LASTEXITCODE -ne 0) { throw 'Fallo de firma del instalador.' }
    }
    $zip = Join-Path $release "$packageName-Portable.zip"
    Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip -CompressionLevel Optimal
    $manifest = [ordered]@{ Product='AicountsManager'; Version=$version; Runtime=$rid; SelfContained=$true; LaunchValidated=$launchValidated; Signed=[bool]$SigningThumbprint; Files=@() }
    $manifest.Files = @(Get-ChildItem -LiteralPath $package -Recurse -File | ForEach-Object { [ordered]@{ Path=[IO.Path]::GetRelativePath($package,$_.FullName); SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $release "$packageName-manifest.json") -Encoding utf8
    Write-Host "Generado: $installer"
    Write-Host "Portable: $zip"
}
Get-ChildItem -LiteralPath $release -File | Where-Object { $_.Extension -in '.exe','.zip' } | ForEach-Object { $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256; "$($hash.Hash)  $($_.Name)" } | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding utf8
