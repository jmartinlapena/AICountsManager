@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "PROJECT_ROOT=%%~fI"
set "PROJECT=%PROJECT_ROOT%\src\AICountsManager.App\AICountsManager.App.csproj"

if not exist "%PROJECT%" (
    echo No se encontro el proyecto esperado en src\AICountsManager.App.
    exit /b 1
)

dotnet run --project "%PROJECT%"
