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
- Si Codex esta abierto, el cambio lo cierra normalmente, espera a que terminen sus procesos, preserva la generacion final de la cuenta saliente, instala la cuenta entrante y vuelve a abrirlo.
- Si Codex esta cerrado, el cambio suele completarse en menos de un segundo y no inicia procesos salvo que se haya solicitado reautenticacion.
- Un watcher de `auth.json` guarda inmediatamente las renovaciones de tokens en el perfil que coincide por identidad.
- Cada 5 minutos solo se actualizan los porcentajes de uso. Esta tarea no copia `auth.json`, no guarda perfiles y no crea backups.
- Las cuentas no activas muestran el ultimo dato cacheado.
- El tooltip de la bandeja muestra el perfil activo y el porcentaje restante de la ventana de sesion cuando hay datos.

## Gestion de perfiles

- `Add current account` guarda la cuenta activa actual como perfil.
- Cada fila en `Settings` permite renombrar o eliminar un perfil.
- `Verificar la renovacion antes de abrir Codex` activa el modo estricto. Este llama temporalmente a `account/read` con `refreshToken: true`; el modo rapido difiere esa comprobacion hasta que Codex se abra.
- `Open data folder` abre la carpeta de datos local de la aplicacion.
- La app puede crear un acceso directo de inicio en Windows para arrancar con la sesion del usuario.
- Si `CODEX_HOME` no esta definido, se usa `%USERPROFILE%\\.codex`.

## Almacenamiento local

- Perfiles: `%APPDATA%\\CodexAccountTrayManager\\accounts\\`
- Backups: `%APPDATA%\\CodexAccountTrayManager\\backups\\`
- Cache: `%APPDATA%\\CodexAccountTrayManager\\cache\\usage_cache.json`
- Config app: `%APPDATA%\\CodexAccountTrayManager\\config\\app_config.json`
- Logs: `%APPDATA%\\CodexAccountTrayManager\\logs\\app.log`
- Diario transaccional sin secretos: `%APPDATA%\\CodexAccountTrayManager\\transactions\\current.json`
- Config Codex: `%USERPROFILE%\\.codex\\config.toml`, o `CODEX_HOME\\config.toml` si `CODEX_HOME` esta definido.
- Auth activo: `%USERPROFILE%\\.codex\\auth.json`, o `CODEX_HOME\\auth.json` si `CODEX_HOME` esta definido.

## Seguridad

- No guarda contrasenas.
- No muestra el contenido de `auth.json`.
- No registra tokens: los logs y el diario usan solamente huellas SHA-256 abreviadas o completas.
- No implementa OAuth propio.
- Nunca ejecuta `logout`, porque esa operacion puede revocar credenciales.
- No rota cuentas automaticamente.
- No cambia de cuenta al llegar a un limite.
- No sube datos fuera de la maquina.
- Las escrituras de credenciales son atomicas: temporal en el mismo directorio, `Flush(true)`, permisos restringidos, `File.Replace` y lectura de verificacion.
- Un mutex global serializa todas las operaciones de credenciales. Una generacion con `last_refresh` anterior nunca sustituye a otra mas nueva.
- Los backups nuevos solo se crean al eliminar explicitamente un perfil y se deduplican por huella. No se crean backups al observar una renovacion ni al consultar porcentajes.

## Identidad y recuperacion

La asociacion entre el `auth.json` activo y un perfil se realiza con los identificadores presentes en los tokens (`chatgpt_user_id`/`sub`, `account_id` y workspace). `ActiveProfile` solo se usa para resolver duplicados que ya coinciden en identidad; nunca basta por si mismo para sobrescribir credenciales.

Si una verificacion estricta devuelve `refresh_token_invalidated`, la aplicacion restaura la cuenta saliente y ofrece preparar la cuenta de destino para reautenticacion. Tras un login correcto, el watcher captura el nuevo `auth.json` sin esperar al temporizador de consumo.

El diario transaccional no contiene tokens y permite reconciliar un cambio interrumpido. Si la identidad activa no pertenece a ningun perfil guardado, CodexManager no la sobrescribe: exige guardarla primero.

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

Pruebas de regresion (usan exclusivamente credenciales ficticias y directorios temporales):

```powershell
dotnet run --project src\CodexManager.Tests\CodexManager.Tests.csproj
```

## Build

```cmd
scripts\build_exe.cmd
```

Salida esperada:

- `dist\CodexManager\CodexManager.exe`
- `dist\CodexManager\CodexManager.lnk`
- `dist\CodexManager\CodexManager.dll`

El script construye primero un apphost con la misma identidad binaria que la version
original aceptada en este equipo y comprueba su SHA-256. Despues publica por separado
la DLL 2.0.1 y combina ambos artefactos. Si la huella del lanzador cambia, el build se
detiene en lugar de instalar un ejecutable que Panda pueda bloquear.

La version del SDK esta fijada en `global.json` para que el resultado sea reproducible.
La aplicacion debe estar cerrada antes del build. No se crean carpetas de fallback ni
instalaciones paralelas.

## Notas

- Si `codex` no se detecta automaticamente, puedes definir `CODEX_BINARY` con la ruta completa al ejecutable.
- La deteccion tambien comprueba `PATH`, `where codex`, `%LOCALAPPDATA%\\OpenAI\\Codex\\bin\\codex.exe` y paquetes `OpenAI.Codex_*` en WindowsApps.
- La consulta de uso prefiere `account/rateLimits/read` cuando el modo estricto esta activo y Codex esta cerrado; en los demas casos usa `https://chatgpt.com/backend-api/wham/usage` o la base definida en `config.toml`.
- Las ventanas se clasifican por `windowDurationMins`: 300 minutos para la sesion de cinco horas y 10.080 minutos para la semanal.
- Un 401/403 al consultar consumo solo indica que el `access_token` no fue aceptado; no se interpreta como prueba de que el `refresh_token` haya sido revocado.
- `CodexManager.exe` es solamente el apphost estable; la implementacion y su version 2.0.1 estan en `CodexManager.dll`.
- La huella validada del apphost es `FDB675DC32AE35AC5391835B018E52858C7253824A610FD5F4D0B65DE95FBAC1`. Se comprobo mediante creacion suspendida y mediante un arranque real sin desactivar Panda ni crear exclusiones.
- Para distribuir el ejecutable en otros equipos sigue siendo recomendable firmarlo con un certificado de firma de codigo confiable o autorizarlo en la herramienta de seguridad correspondiente.
