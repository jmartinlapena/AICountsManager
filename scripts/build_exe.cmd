@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "PROJECT_ROOT=%%~fI"
set "PROJECT=%PROJECT_ROOT%\src\CodexManager.App\CodexManager.App.csproj"
set "PUBLISH_DIR=%PROJECT_ROOT%\dist\CodexManager"
set "FALLBACK_DIR=%PROJECT_ROOT%\dist\CodexManager.new"

if not exist "%PROJECT%" (
    echo No se encontro el proyecto esperado en src\CodexManager.App.
    exit /b 1
)

if exist "%PUBLISH_DIR%" (
    rmdir /s /q "%PUBLISH_DIR%" 2>nul
)

set "TARGET_DIR=%PUBLISH_DIR%"
if exist "%PUBLISH_DIR%" (
    echo No se pudo limpiar dist\CodexManager. Se publicara en dist\CodexManager.new.
    if exist "%FALLBACK_DIR%" (
        rmdir /s /q "%FALLBACK_DIR%" 2>nul
    )
    set "TARGET_DIR=%FALLBACK_DIR%"
)

echo Publicando CodexManager...
dotnet publish "%PROJECT%" ^
    -c Release ^
    -r win-x64 ^
    --self-contained false ^
    -o "%TARGET_DIR%"

if errorlevel 1 exit /b %errorlevel%

if not exist "%TARGET_DIR%\CodexManager.exe" (
    echo dotnet publish termino pero no se encontro el ejecutable esperado.
    exit /b 1
)

echo Build completado: "%TARGET_DIR%\CodexManager.exe"
exit /b 0
