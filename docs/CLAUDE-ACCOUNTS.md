# Cuentas de Codex y Claude — 3.1.0

Todos los proveedores aparecen en Ajustes. Desactivarlos conserva sus cuentas y su orden, oculta sus métricas y evita consultar sus cuotas e históricos. Los observadores de sesión se detienen al desactivar el proveedor.

## Añadir y activar

`+` permite elegir **Codex** o **Claude** y dar un nombre a la nueva cuenta. El gestor conserva primero la sesión anterior; si aún no estaba guardada, la registra como «Codex anterior» o «Claude anterior». Después prepara un estado local sin sesión, sin revocar tokens ni inventar credenciales. El usuario abre el cliente e inicia la nueva sesión.

- El alta de Codex se captura al cambiar `auth.json`.
- Para Claude, el panel indica que hay que iniciar sesión y cerrar el cliente para completar la captura coherente de sus cookies. El gestor observa los archivos de autenticación y la salida del proceso durante el alta; F5 también reconcilia el estado pendiente.
- `Cancelar alta` restaura la cuenta anterior. Si ya terminó el nuevo inicio de sesión, se guarda esa cuenta en lugar de descartarla.
- El alta pendiente persiste al reiniciar AICountsManager. No se puede desactivar el proveedor ni iniciar otra alta hasta terminarla o cancelarla.
- La aplicación correspondiente debe estar cerrada para añadir, cancelar o activar. No se abren ni cierran clientes automáticamente. Claude no comprueba ni modifica procesos o archivos de Codex; Codex no bloquea por tener Claude abierto.

Las cuentas de Claude tienen acciones de activar, renombrar y eliminar, y participan en el mismo orden de cuentas de Ajustes. Eliminar un perfil conserva su archivo cifrado como copia recuperable. No elimina la cuenta del proveedor ni modifica su historial.

## Dos inicios de sesión oficiales de Claude

Desktop y Claude Code de terminal emiten credenciales distintas. Se guardan juntos cuando ambos pertenecen a la misma cuenta y organización. Si solo está autenticado uno, se conserva ese inicio de sesión; el otro tendrá que autenticarse mediante su cliente oficial. No se convierte un token de Desktop en un token de terminal. Si los clientes están autenticados con cuentas distintas, se detiene el cambio antes de sobrescribirlas.

Las consultas usan el adaptador HTTP existente y verifican la identidad. `Actualizar`/F5 consulta las cuentas guardadas, sin instalarlas, renovar tokens ni abrir los clientes. Las sesiones caducadas se renuevan desde Claude. El historial de tokens continúa sumando los registros locales; no se duplica por tener varias cuentas guardadas.

## Qué se conserva

- Desktop: las entradas `lastKnownAccountUuid`, `oauth:tokenCache` y `oauth:tokenCacheV2` de `config.json`, y los archivos `Cookies`, `Cookies-journal`, `Cookies-wal` y `Cookies-shm` de su directorio de cookies. El resto de `config.json` permanece intacto semánticamente. `Local State` no se sustituye: se comprueba la huella de su clave antes de restaurar un perfil cifrado.
- Terminal: `claudeAiOauth` de `.credentials.json` y `oauthAccount` de `.claude.json`, conservando los demás ajustes y proyectos. Se respeta `CLAUDE_CONFIG_DIR`.
- Los historiales, carpetas de proyectos, transcripciones, configuración MCP y credenciales de otros proveedores no se mueven. Desde 3.2.0 se sustituye la organización local del destino por la última organización saliente al activar otra cuenta guardada. [Adaptadores de Codex y Claude Code, respaldo y recuperación](LOCAL-ORGANIZATION.md).

Los perfiles se guardan bajo `AICountsManager/provider-accounts/claude`, protegidos con DPAPI para el usuario de Windows y con permisos locales restringidos. Las consultas solo descifran la información necesaria en memoria. No se registran tokens, cookies ni cuerpos de error. Los perfiles cifrados no se trasladan entre usuarios/instalaciones de Windows: en otro equipo se inicia sesión de nuevo; el instalador de la aplicación sí es autocontenido.

Antes de cambiar la sesión se guarda un diario cifrado de los archivos originales. Cada escritura se verifica y un fallo restaura exactamente los bytes anteriores. Una recuperación pendiente se completa únicamente cuando Claude está cerrado. Los nombres de archivo de destino son fijos; los perfiles no pueden seleccionar rutas arbitrarias.

## Recursos y comprobaciones

No se añaden temporizadores de consulta. Las cuotas mantienen el ciclo existente de cinco minutos; las cuentas adicionales se consultan con el botón/F5, con plazos y pausas tras errores 429. Los cambios y copias se ejecutan solo por acción del usuario o para completar un alta pendiente. Los perfiles contienen las sesiones, no una copia completa de la carpeta de Claude.

Las pruebas cubren el recorrido entre dos identidades, la actualización conjunta sin escribir sesiones, la cancelación, la persistencia, el orden visual y la independencia de Codex. Se inyectan fallos tras cada escritura y se compara la recuperación byte por byte. También se comprueba el recorrido con una copia temporal privada del almacenamiento real de Desktop; no equivale a probar el inicio de sesión de dos cuentas reales en la interfaz de Claude.

Fuentes comprobadas el 28 de octubre de 2026; no se instalaron herramientas externas ni se reutilizó su implementación:

- [Claude Code: autenticación](https://code.claude.com/docs/en/authentication).
- [Claude Account Switcher: archivos de sesión de Desktop](https://github.com/SnlperStripes/claude-account-switcher/blob/main/desktop.go).
- [Claude Account Switcher: preservación y restauración](https://github.com/SnlperStripes/claude-account-switcher/blob/main/profiles.go).
- [Electron: sesiones](https://www.electronjs.org/docs/latest/api/session).

El formato de Desktop es interno y puede cambiar con una actualización de Claude. Un formato desconocido o una identidad que no se pueda verificar produce un error y no autoriza a sustituir una sesión existente.
