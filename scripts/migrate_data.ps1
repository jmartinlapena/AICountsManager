param(
    [Parameter(Mandatory=$true)][string]$Source,
    [string]$Destination = (Join-Path $env:APPDATA 'AICountsManager')
)
$ErrorActionPreference = 'Stop'
# Close the application before running. Explicit source avoids guessing or reading unrelated data.
$sourceRoot = (Resolve-Path -LiteralPath $Source).ProviderPath.TrimEnd('\')
$destinationRoot = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
if ([string]::Equals($sourceRoot,$destinationRoot,[StringComparison]::OrdinalIgnoreCase) -or
    $destinationRoot.StartsWith($sourceRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or
    $sourceRoot.StartsWith($destinationRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Origen y destino deben ser carpetas independientes.' }
if (Test-Path -LiteralPath $destinationRoot) { throw 'El destino ya existe. No se mezclan ni sobrescriben datos.' }
if (-not (Test-Path -LiteralPath (Split-Path -Parent $destinationRoot) -PathType Container)) { throw 'El directorio padre del destino no existe.' }
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'config/app_config.json') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $sourceRoot 'accounts') -PathType Container)) { throw 'El origen no tiene la estructura esperada de datos de la aplicacion.' }
if (Test-Path -LiteralPath (Join-Path $sourceRoot 'transactions/current.json')) { throw 'Hay una transaccion pendiente. Abre la version anterior para recuperarla y cierrala antes de migrar.' }
$entries = @((Get-Item -LiteralPath $sourceRoot -Force)) + @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -Force)
if ($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'No se migran datos con enlaces o junctions.' }
$hashes = @{}
foreach ($file in $entries | Where-Object { -not $_.PSIsContainer }) {
    $hashes[[IO.Path]::GetRelativePath($sourceRoot,$file.FullName)] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}
# Both absolute paths and every entry have been validated above. No recursive shell composition.
Move-Item -LiteralPath $sourceRoot -Destination $destinationRoot
$actual = @(Get-ChildItem -LiteralPath $destinationRoot -Recurse -File -Force)
if ($actual.Count -ne $hashes.Count) { throw 'El recuento posterior no coincide. Conserva ambas rutas para revisar; no borres datos.' }
foreach ($file in $actual) {
    $relative = [IO.Path]::GetRelativePath($destinationRoot,$file.FullName)
    if ($hashes[$relative] -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { throw 'La comprobacion de integridad no coincide. No borres datos.' }
}
[pscustomobject]@{ Destination=$destinationRoot; FilesVerified=$actual.Count; IntegrityVerified=$true }
