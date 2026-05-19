# CodexManager

Widget nativo de Windows para ver y cambiar perfiles de Codex desde la bandeja del sistema.

La implementacion principal esta hecha en C# con WinForms. El objetivo es ofrecer un popover ligero en la bandeja, sin una ventana clasica fija en la barra de tareas, para alternar entre varias sesiones locales de Codex guardadas como perfiles.

## Requisitos

- Windows.
- .NET SDK 9 para desarrollo y build.
- Codex instalado o accesible mediante `CODEX_BINARY`.
- Codex configurado para usar credenciales en archivo (`cli_auth_credentials_store = "file"`). La app puede escribir esta opcion en `config.toml` desde los controles de configuracion.

## Estructura del repositorio

- `src/CodexManager.App/`: codigo fuente de la aplicacion WinForms.
- `assets/`: iconos y recursos estaticos usados por la app.
- `scripts/`: scripts de build y publicacion.
- `run.py`: wrapper minimo para ejecutar `dotnet run`.

`dist/`, `artifacts/`, `bin/` y `obj/` son salidas generadas y no deben versionarse.

## Uso diario

- Clic izquierdo en el icono: abre o cierra el widget.
- Clic derecho: muestra la opcion `Salir`.
- El boton con icono de uso activa un perfil guardado.
- El boton de refresco actualiza manualmente el uso de la cuenta activa.
- Antes de cambiar de cuenta, la app muestra una confirmacion propia recordando que hay que cerrar Codex antes.
- La cuenta activa se refresca automaticamente cada 5 minutos.
- Las cuentas no activas muestran el ultimo dato cacheado.
- El tooltip de la bandeja muestra el perfil activo y el porcentaje restante de la ventana de sesion cuando hay datos.

## Gestion de perfiles

- `Add current account` guarda la cuenta activa actual como perfil.
- Cada fila en `Settings` permite renombrar o eliminar un perfil.
- `Open data folder` abre la carpeta de datos local de la aplicacion.
- La app puede crear un acceso directo de inicio en Windows para arrancar con la sesion del usuario.
- Si `CODEX_HOME` no esta definido, se usa `%USERPROFILE%\\.codex`.

## Almacenamiento local

- Perfiles: `%APPDATA%\\CodexAccountTrayManager\\accounts\\`
- Backups: `%APPDATA%\\CodexAccountTrayManager\\backups\\`
- Cache: `%APPDATA%\\CodexAccountTrayManager\\cache\\usage_cache.json`
- Config app: `%APPDATA%\\CodexAccountTrayManager\\config\\app_config.json`
- Logs: `%APPDATA%\\CodexAccountTrayManager\\logs\\app.log`
- Config Codex: `%USERPROFILE%\\.codex\\config.toml`, o `CODEX_HOME\\config.toml` si `CODEX_HOME` esta definido.
- Auth activo: `%USERPROFILE%\\.codex\\auth.json`, o `CODEX_HOME\\auth.json` si `CODEX_HOME` esta definido.

## Seguridad

- No guarda contrasenas.
- No muestra el contenido de `auth.json`.
- No implementa OAuth propio.
- No rota cuentas automaticamente.
- No cambia de cuenta al llegar a un limite.
- No sube datos fuera de la maquina.

## Desarrollo

Ruta recomendada:

```cmd
scripts\run_dev.cmd
```

Tambien puedes ejecutar directamente:

```powershell
dotnet run --project src\CodexManager.App\CodexManager.App.csproj
```

`run.py` sigue disponible como wrapper minimo, pero no es necesario.

Tambien puedes usar:

```cmd
python run.py
```

## Build

```cmd
scripts\build_exe.cmd
```

Salida esperada:

- `dist\CodexManager\CodexManager.exe`

Alternativa directa, sin scripts:

```powershell
dotnet publish src\CodexManager.App\CodexManager.App.csproj -c Release -r win-x64 --self-contained false -o dist\CodexManager
```

Si `dist\CodexManager` no se puede limpiar porque el ejecutable esta abierto, los scripts intentan publicar en `dist\CodexManager.new`.

## Notas

- Si `codex` no se detecta automaticamente, puedes definir `CODEX_BINARY` con la ruta completa al ejecutable.
- La deteccion tambien comprueba `PATH`, `where codex`, `%LOCALAPPDATA%\\OpenAI\\Codex\\bin\\codex.exe` y paquetes `OpenAI.Codex_*` en WindowsApps.
- El refresco de uso consulta `https://chatgpt.com/backend-api/wham/usage` o la base definida en `config.toml`.
- Para equipos con antivirus estricto, lo mas neutro es mover el codigo fuente y compilar localmente con `dotnet publish`. Eso evita distribuir un `.exe` ya generado. Compilar localmente no sustituye una firma de codigo real, pero suele reducir falsos positivos frente a binarios precompilados descargados.
