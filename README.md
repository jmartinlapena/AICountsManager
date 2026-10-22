# AICountsManager

[Repositorio](https://github.com/jmartinlapena/AICountsManager) · [Validación de la versión 3.0.1](docs/VALIDATION-3.0.1.md)

Gestor ligero de cuentas y consumo de herramientas de IA para la bandeja de Windows: Codex, Antigravity, Claude y Cursor.

La aplicación está hecha en C#: WinForms gestiona la bandeja del sistema y WPF dibuja el panel translúcido. Permite alternar entre sesiones locales de Codex sin una ventana permanente en la barra de tareas.

## Instalar sin compilar

Usa `AICountsManager-3.0.1-win-x64-Setup.exe` en equipos Intel/AMD, o `win-arm64` en Windows ARM. Se instala para tu usuario, crea el acceso directo y permite desinstalar desde Configuración de Windows. No necesita administrador, SDK, Python ni instalar .NET: el runtime .NET 10 LTS viene incluido.

También se genera un `Portable.zip`: descomprime **todo** el ZIP y ejecuta `AICountsManager.exe`. No copies solo el EXE. Requiere Windows 10 2004 (19041) o posterior / Windows 11; no funciona en macOS o Linux.

Solo es necesario cerrar la aplicación al instalar una actualización. El instalador detecta si AICountsManager está abierto. Los datos permanecen fuera del paquete, en `%APPDATA%/AICountsManager`; la desinstalación los conserva. Si proceden de una versión con otra carpeta de datos, sigue la migración indicada más abajo antes del primer arranque.

Los paquetes no incluyen cuentas del desarrollador ni credenciales. Se generan SHA-256 y manifiestos por arquitectura. La firma Authenticode requiere un certificado propio; sin él los artefactos se identifican como no firmados. No se garantiza reputación de SmartScreen/antivirus en todos los equipos.

## Proveedores y recursos

Claude y Cursor están desactivados por defecto. Se activan individualmente en Ajustes, igual que Antigravity. Solo Codex cambia cuentas; el resto consulta la sesión actual. [Contratos, fuentes y límites de la integración](docs/PROVIDERS.md).

Sin servicios, procesos auxiliares permanentes, bibliotecas de gráficos, telemetría ni nuevos temporizadores. Las cuotas reutilizan el ciclo de cinco minutos. Los históricos solo se leen al abrir/actualizar el panel; desactivar un proveedor evita sus lecturas y peticiones. WPF continúa creándose al primer uso.

Para Codex se requiere su instalación y autenticación en archivo (`cli_auth_credentials_store = "file"`). Claude necesita un inicio de sesión de Claude Code; Cursor, el de su aplicación. Las sesiones externas caducadas se renuevan desde sus clientes, sin que AICountsManager escriba sus credenciales.

## Requisitos de desarrollo

Solo el equipo que genera las versiones necesita el SDK .NET 10 de `global.json`, PowerShell 7 e Inno Setup 6.7.3. El usuario final ejecuta el instalador o ZIP generado.

## Estructura del repositorio

- `src/AICountsManager.App/`: lógica de sesiones, bandeja WinForms e interfaz WPF.
- `assets/`: iconos y recursos estaticos usados por la app.
- `scripts/`: scripts de build y publicacion.
- `run.py`: wrapper minimo para ejecutar `dotnet run`.

`dist/`, `artifacts/`, `bin/` y `obj/` son salidas generadas y no deben versionarse.

## Uso diario

- Clic izquierdo en el icono: abre o cierra el widget.
- Clic derecho: muestra la opcion `Salir`.
- El botón Activar cambia al perfil guardado, con confirmación previa.
- Actualizar (o F5) consulta las cuotas de todas las cuentas guardadas y actualiza el histórico local de tokens. Escape oculta el panel.
- Si Codex esta abierto, el cambio lo cierra normalmente, espera a que terminen sus procesos, preserva la generacion final de la cuenta saliente, instala la cuenta entrante y vuelve a abrirlo.
- Si Codex esta cerrado, el cambio preserva la sesión sin abrir la aplicación; el modo estricto puede usar un app-server temporal.
- Un watcher de `auth.json` guarda inmediatamente las renovaciones de tokens en el perfil que coincide por identidad.
- Cada 5 minutos solo se actualizan los porcentajes de la cuenta activa. Esta tarea no copia `auth.json`, no guarda perfiles y no crea backups.
- Las cuentas no activas muestran su última consulta conjunta; los fallos conservan la lectura previa y su antigüedad.
- El tooltip de la bandeja muestra el perfil activo y el porcentaje restante de la ventana de sesion cuando hay datos.

## Actividad y cuotas (2.2)

- La ventana WPF se crea al primer clic en la bandeja, no al arrancar ni por eventos de actualización. El reloj del panel permanece detenido mientras está oculto.
- Panel compacto de 382 DIP, tarjetas sombreadas y Acrylic nativo. Conserva la transparencia de Windows y respeta su configuración de accesibilidad. Los tonos de sesión/semanal se derivan del acento del sistema; el fondo sigue el tema de la barra de tareas.
- «Tu actividad» suma tokens diarios entre cuentas. Hoy/Semana y las flechas permiten recorrer hasta 365 días; las semanas son de lunes a domingo en la zona horaria local, independientes de los resets de cuota. «Modelos» despliega el reparto observado sin consultas adicionales.
- El histórico se obtiene de `CODEX_HOME/sessions` y `archived_sessions`. No es un total de facturación ni incluye otros equipos, nube o registros eliminados antes de indexarlos. «Sin modelo» conserva consumo sin modelo identificable. La caché guarda contadores y posiciones, nunca conversaciones ni credenciales.
- Los contadores acumulados se convierten en incrementos para evitar duplicados; cambiar de cuenta no reinicia el histórico. Archivar un archivo no lo cuenta dos veces. Las fronteras de historial heredado en forks/subagentes se excluyen cuando están presentes. Ante registros incompletos o no interpretables, el panel indica cobertura parcial.
- Primera indexación por bloques de hasta 16 MiB / aproximadamente 1,5 s (el bloque de E/S en curso y la escritura del índice pueden finalizar después). Continúa cada 10 s mientras el panel esté visible. Se cancela al ocultarlo y se reanuda al abrirlo. Un índice completo solo se actualiza al abrir o pulsar Actualizar: no hay vigilancia continua de conversaciones en segundo plano. La caché se limita a 24 MiB y 20.000 fuentes.
- Actualización conjunta de Codex: máximo dos consultas HTTP simultáneas, 12 s por cuenta y 60 s por lote. No cambia la cuenta activa, no abre/cierra Codex y no renueva tokens. Una sesión rechazada requiere reautenticación mediante el flujo existente. Los errores por cuenta no impiden actualizar las demás. Un 429 detiene las cuentas pendientes y respeta Retry-After (mínimo 60 s); otros lotes tienen una pausa de 15 s.
- Las barras muestran **porcentaje restante**. La marca vertical indica el restante esperado con reparto uniforme a lo largo de la ventana. Para la cuota semanal equivale a 100/7 ≈ 14,3 puntos porcentuales por día.
- «≈X% al reset» y «Límite en …» extrapolan el consumo medio desde el inicio de la ventana; no son límites oficiales ni predicciones garantizadas. Se ocultan con datos antiguos, errores o al inicio de una ventana. Los resets se muestran como `Resets in Xd Xh` / `Xh Xm`; al vencer se pide actualizar sin inventar un nuevo saldo.
- Inspiración visual: [OpenUsage](https://github.com/robinebers/openusage). Implementación propia en los controles WPF existentes, sin descargar ni ejecutar esa herramienta y sin bibliotecas de gráficos nuevas.

## Ajustes de interfaz (2.2.1)

- Encabezado «AICountsManager» con el símbolo vectorial de la aplicación, blanco en tema oscuro y oscuro en tema claro. Añadir cuenta es el botón «+» junto a Actualizar.
- Ajustes permite subir y bajar cuentas. El orden se guarda en `ProfileOrder`, permanece al cambiar de cuenta y acompaña los renombrados. Las cuentas nuevas se añaden al final; las eliminadas desaparecen del orden.
- El pie muestra la proporción de cuentas con cuotas recientes, la antigüedad de la última lectura y el próximo reset conocido.
- Menú contextual con estilos propios, sin el margen blanco de iconos/marcas del menú clásico. Conserva navegación por teclado y estados deshabilitados.
- Las notificaciones de tema no vuelven a aplicar el material DWM cuando el tema no ha cambiado.

## Planes de cuenta (2.2.2)

- La cabecera de cada tarjeta muestra el plan devuelto por la consulta habitual de cuotas: Plus, Pro 100, Pro 200, Pro 500, etc. No se deducen multiplicadores a partir del consumo. Una cuenta todavía no consultada no inventa un plan; los identificadores nuevos se muestran como «Otro plan».
- La sección completa de 5 h se oculta en planes Pro y Business Premium únicamente cuando una respuesta válida contiene una sola ventana semanal y ninguna de sesión. Un error, una respuesta vacía o una ventana desconocida no se interpretan como ausencia de límite. Las lecturas previas se conservan al fallar una actualización.
- El plan y la configuración de ventanas se guardan junto a las cuotas. No hay consultas, temporizadores ni procesos nuevos. Un cambio de plan o de límites se refleja en la siguiente actualización.
- Referencias: [tipos del protocolo Codex](https://github.com/openai/codex/blob/main/codex-rs/protocol/src/auth.rs), [planes Pro](https://help.openai.com/en/articles/9793128-about-chatgpt-pro-tiers) y [límites de cinco horas](https://help.openai.com/en/articles/20001516-managing-usage-with-gpt-6-astra-in-work-and-codex).

## Interacción compacta (2.2.3)

- Retirada la franja de mensajes de actualización: el resultado permanece en las tarjetas y en el pie. Se conserva el bloqueo de consultas repetidas y Retry-After. Los errores que impiden completar una acción se muestran en un diálogo; los errores de cuotas siguen junto a cada cuenta y los del histórico en su panel.
- Sin anotaciones al pasar el ratón por el panel, sus botones, tarjetas o ajustes. Los controles conservan nombres accesibles y navegación por teclado.
- Pulsar una barra o su día muestra el total exacto de tokens observado ese día, su fecha y su reparto por modelo. Pulsarlo otra vez recupera el resumen. La semana del gráfico no se mueve al seleccionar un día; las flechas y Hoy/Semana reinician la selección. Los días futuros no se pueden seleccionar. Se reutilizan los datos ya cargados.
- `self_serve_business_prolite` se identifica como «Business Premium». Sus observaciones semanales ya guardadas también compactan la tarjeta sin una consulta extra. Los identificadores Business genéricos no se presentan como Premium ni pierden su barra de sesión.
- Premium cuesta 100 USD/mes con facturación anual o 125 USD/mes con facturación mensual; la consulta de cuotas no distingue la modalidad de facturación, por lo que la etiqueta muestra el nivel y no un precio supuesto. [Referencia oficial](https://help.openai.com/en/articles/8542115-chatgpt-business-general-faq).

## Consulta común por proveedor (2.3.5)

- Todos los porcentajes de cuotas y previsiones se muestran como enteros, igual que Codex. Los cálculos internos conservan su precisión; por ejemplo, 99,9934 % se presenta como 100 %.
- `QuotaDisplay` es la implementación común para porcentajes, anchura de barras, posición de la referencia semanal, resets y previsión. Las tarjetas de Codex y de proveedores externos consumen el mismo resultado; las ventanas de cinco horas no muestran ritmo semanal.
- `IQuotaProvider` adapta únicamente la consulta y normalización de cada plataforma. `QuotaQuery` comparte plazo/cancelación y valida el tipo de solicitud. El registro de proveedores comparte caché, errores, actualización y ajustes; cualquier adaptador registrado aparece automáticamente en el menú de conexión, los ajustes y el orden conjunto.
- Solo Codex acepta perfiles guardados y permite cambiar de cuenta. Los demás adaptadores consultan su sesión actual y no muestran acciones de activación; el registro rechaza capacidades de cambio de cuenta externas. Google/Antigravity sigue siendo el único proveedor externo real implementado.
- Sin procesos auxiliares, temporizadores ni sondeos adicionales. Los históricos siguen aislados por origen.

## Orden conjunto y ritmo semanal (2.3.1)

- Ajustes → Orden de las cuentas incluye Codex y los proveedores conectados, con sus logos. Sus tarjetas comparten una única lista y pueden intercalarse. El orden existente de Codex se conserva al migrar; renombrar, actualizar o cambiar de sesión no altera las posiciones. Los proveedores temporalmente desactivados conservan su posición al volver a conectarse.
- `AccountOrder` identifica los perfiles de Codex y las sesiones actuales de proveedores por claves distintas; `ProfileOrder` mantiene la compatibilidad. Ordenar no modifica credenciales ni permite cambiar cuentas de proveedores externos.
- La referencia recomendada y la previsión se calculan exclusivamente para ventanas semanales de 10.080 minutos. OpenAI, Antigravity y los futuros adaptadores usan la misma fórmula y presentación. Las ventanas de cinco horas solo muestran consumo y reset.
- La raya semanal se muestra desde el comienzo de una ventana válida; la previsión mantiene la protección contra datos prematuros (primer 3 % de la semana). Datos desconocidos, vencidos o antiguos no producen marcas ni previsiones.
- No se añaden consultas, tareas en segundo plano ni temporizadores.

## Proveedores y resumen (2.3.0)

- Codex conserva los perfiles, orden, cambio de cuenta, verificación, actualización conjunta y recuperación de errores. Sus credenciales y cachés mantienen el formato existente.
- `IQuotaProvider`, `QuotaRequest`, `ProviderUsage` y `ProviderQuota` separan la consulta y el resultado común de los protocolos de cada proveedor. `QuotaQuery` aplica una cancelación/plazo común de 12 s; `CurrentProviderRegistry` registra consultas de sesiones actuales, aísla fallos y limita su concurrencia a dos. El lote manual mantiene un presupuesto global de 60 s. Un adaptador conserva el cliente y la semántica de cuotas de Codex.
- Antigravity consulta directamente Google Cloud Code con la sesión `gemini:antigravity` del Administrador de credenciales de Windows. No necesita que la aplicación esté abierta. Si el acceso ha caducado o Google lo rechaza, se renueva mediante OAuth; los tokens derivados se conservan solo en memoria y se invalidan al cambiar/desaparecer la credencial original. No se modifica la credencial de Antigravity. Los fallos de red y 429 no provocan renovaciones. Las conexiones remotas validan TLS y rechazan redirecciones.
- El servidor local es un respaldo si no hay credencial compatible y un complemento de hasta 2 segundos para cuotas adicionales no expuestas por Google, solo cuando coincide la identidad. El descubrimiento WMI y la tabla TCP se leen solo al actualizar, fuera del hilo de interfaz. Las solicitudes se limitan a puertos de ese proceso en `127.0.0.1`, sin proxy ni redirecciones. La excepción de certificado autofirmado se limita a HTTPS de loopback. La identidad se comprueba antes y después de leer las cuotas; sesiones diferentes no se mezclan.
- La tarjeta reproduce Gemini semanal/5 h, Claude-GPT semanal/5 h y Gemini 4 Argon semanal cuando está disponible. Cada ventana conserva su reset y porcentaje restante. No se infiere acceso a Argon a partir del plan ni se interpreta un dato ausente como 0 o 100 %.
- Es necesario haber iniciado sesión en Antigravity al menos una vez. No se inicia, cierra ni cambia su cuenta desde AICountsManager. Si la consulta falla, se identifica la última lectura como antigua; una cuenta distinta reemplaza la lectura anterior completa. `+` permite guardar Codex o conectar Antigravity actual; Ajustes permite desactivar Antigravity. Solo Codex muestra acciones de sesión.
- La barra superior `Hoy / Semana / Cuotas` reúne los tokens diarios, los tokens semanales y el resumen por proveedor en un mismo recuadro. La vista inicial es `Hoy`; el histórico conserva sus flechas de navegación, selección de días y desglose por modelos. La rosquilla reparte los tokens registrados de la semana natural actual (lunes a hoy) entre proveedores: un segmento por proveedor, incluyendo todos los modelos de Antigravity. No usa ni suma porcentajes de cuota; las cuotas siguen en las tarjetas de las cuentas. Los colores identifican al proveedor. Los proveedores sin uso no generan segmentos y el historial incompleto se indica como parcial.
- Las barras diarias de tokens apilan los modelos de cada proveedor; la suma sigue siendo diaria y transversal a las cuentas. Se conservan Hoy/Semana, navegación, selección del día y modelos. Los modelos se identifican por proveedor y nombre, también en «otros modelos»: nunca se atribuyen tokens de Codex a Antigravity ni se deducen tokens de un porcentaje de cuota. El estado conserva `sin historial local` cuando falta la fuente; el desglose de modelos omite proveedores desconocidos y filas sin tokens en el periodo seleccionado.
- Antigravity lee `~/.gemini/antigravity*/conversations/*.db`, solo `gen_metadata.data` y la fecha en `steps.metadata`, con SQLite incluido en Windows (`winsqlite3.dll`). Se extraen los campos de contabilidad protobuf, nunca se conservan mensajes. Se suman entrada, prompt de sistema, salida y caché leída. Los registros sin fecha válida no se asignan a un día ficticio. El último tramo se relee para capturar generaciones que se completan, reemplazándolas por índice en vez de sumarlas de nuevo.
- El índice de Antigravity usa la misma retención de 365 días, caché persistente de hasta 8 MiB, hasta 1.024 fuentes/100.000 generaciones, blobs de hasta 1 MiB y bloques de lectura de 8 MiB/aproximadamente 750 ms. Las bases sin cambios no se vuelven a consultar. Cancelación y recuperación aprovechan el flujo existente del histórico; no hay vigilancia continua ni temporizadores nuevos. El tiempo de una operación SQLite/E/S en curso y la agregación/escritura pueden exceder el presupuesto suave del bloque.
- Nuevos archivos locales: `cache/provider-antigravity.json`, `config/provider-antigravity.json`, `cache/antigravity_history.json`. No contienen CSRF ni OAuth; las cuotas incluyen únicamente métricas y metadatos de identificación visibles.
- Claude y Cursor quedan como futuras integraciones; no se presentan como clientes ya soportados. Claude dentro de Antigravity es un grupo de cuota de Antigravity.
- Verificación de protocolo y fuente del histórico: [documentación de Antigravity en OpenUsage](https://github.com/robinebers/openusage/blob/main/docs/providers/antigravity.md). Se ha consultado su código y el servidor local instalado, sin instalar ni ejecutar OpenUsage. Logos vectoriales con fondo transparente procedentes de sus recursos MIT: atribución/licencia en `assets/provider-icons-LICENSE.txt`. La interfaz y el código de integración Windows son propios.
- Los endpoints y el formato de Antigravity no son una API pública estable. Si cambian, se informa de la ausencia de datos y se conserva Codex operativo.

## Gestion de perfiles

- `Añadir cuenta` guarda la cuenta activa actual como perfil.
- El menú `…` de cada cuenta permite renombrar o eliminar un perfil.
- `Verificar la sesión antes de abrir Codex` activa el modo estricto. Este llama temporalmente a `account/read` con `refreshToken: true`; el modo rapido difiere esa comprobacion hasta que Codex se abra.
- `Abrir carpeta de datos`, dentro de Ajustes, abre el almacenamiento local.
- La app puede crear un acceso directo de inicio en Windows para arrancar con la sesion del usuario.
- Si `CODEX_HOME` no esta definido, se usa `%USERPROFILE%\\.codex`.

## Migración de datos

Cierra la aplicación anterior. Conserva la ruta de datos indicada en Ajustes → Almacenamiento y ejecuta `./scripts/migrate_data.ps1 -Source "<ruta actual>"`. El script traslada la carpeta completa a `%APPDATA%/AICountsManager`, comprueba sus huellas y se niega a mezclar o sobrescribir un destino existente. Recrea el acceso de inicio desde el instalador o desde Ajustes. No copies cuentas dentro del paquete de distribución.

## Almacenamiento local

- Perfiles: `%APPDATA%\\AICountsManager\\accounts\\`
- Backups: `%APPDATA%\\AICountsManager\\backups\\`
- Histórico numérico: `%APPDATA%\\AICountsManager\\cache\\token_history.json`
- Cache: `%APPDATA%\\AICountsManager\\cache\\usage_cache.json`
- Config app: `%APPDATA%\\AICountsManager\\config\\app_config.json`
- Logs: `%APPDATA%\\AICountsManager\\logs\\app.log`
- Diario transaccional sin secretos: `%APPDATA%\\AICountsManager\\transactions\\current.json`
- Config Codex: `%USERPROFILE%\\.codex\\config.toml`, o `CODEX_HOME\\config.toml` si `CODEX_HOME` esta definido.
- Auth activo: `%USERPROFILE%\\.codex\\auth.json`, o `CODEX_HOME\\auth.json` si `CODEX_HOME` esta definido.

## Seguridad

- No guarda contrasenas.
- No muestra el contenido de `auth.json`.
- No registra tokens: los logs y el diario usan solamente huellas SHA-256 abreviadas o completas.
- No implementa un inicio de sesión OAuth propio. Antigravity puede renovar en memoria su acceso existente para consultar cuotas con el cliente cerrado.
- Nunca ejecuta `logout`, porque esa operacion puede revocar credenciales.
- No rota cuentas automaticamente.
- No cambia de cuenta al llegar a un limite.
- Los perfiles y copias permanecen en este equipo. Las consultas de consumo y verificación se comunican con los servicios de Codex usando la sesión existente.
- Las escrituras de credenciales son atomicas: temporal en el mismo directorio, `Flush(true)`, permisos restringidos, `File.Replace` y lectura de verificacion.
- Un semáforo con nombre serializa las operaciones de credenciales entre instancias de la misma sesión de Windows. Una generacion con `last_refresh` anterior nunca sustituye a otra mas nueva.
- Los backups nuevos solo se crean al eliminar explicitamente un perfil y se deduplican por huella. No se crean backups al observar una renovacion ni al consultar porcentajes.

## Identidad y recuperacion

La asociacion entre el `auth.json` activo y un perfil se realiza con los identificadores presentes en los tokens (`chatgpt_user_id`/`sub`, `account_id` y workspace). `ActiveProfile` solo se usa para resolver duplicados que ya coinciden en identidad; nunca basta por si mismo para sobrescribir credenciales.

Si una verificacion estricta devuelve `refresh_token_invalidated`, la aplicacion restaura la cuenta saliente y ofrece preparar la cuenta de destino para reautenticacion. Tras un login correcto, el watcher captura el nuevo `auth.json` sin esperar al temporizador de consumo.

El diario transaccional no contiene tokens y permite reconciliar un cambio interrumpido. Si la identidad activa no pertenece a ningun perfil guardado, AICountsManager no la sobrescribe: exige guardarla primero.

## Desarrollo

Ruta recomendada:

```cmd
scripts\run_dev.cmd
```

Tambien puedes ejecutar directamente:

```powershell
dotnet run --project src\AICountsManager.App\AICountsManager.App.csproj
```

`run.py` sigue disponible como wrapper minimo, pero no es necesario.

Tambien puedes usar:

```cmd
python run.py
```

Pruebas de regresion (usan exclusivamente credenciales ficticias y directorios temporales):

```powershell
dotnet run --project src\AICountsManager.Tests\AICountsManager.Tests.csproj
```

## Generar una distribución

```powershell
pwsh ./scripts/build_release.ps1
# Con herramientas fuera de PATH:
pwsh ./scripts/build_release.ps1 -Dotnet C:/herramientas/dotnet.exe -Iscc 'C:/Program Files (x86)/Inno Setup 6/ISCC.exe'
```

Ejecuta las pruebas y genera ambas arquitecturas en `artifacts/releases/3.0.0/`: instaladores, ZIP portables, manifiestos y `SHA256SUMS.txt`. Comprueba versión de DLL, arquitectura del EXE y dependencias autocontenidas. En la arquitectura del equipo ejecuta `--self-test`: carga WPF, icono y SQLite sin abrir perfiles ni iniciar la bandeja. No modifica la instalación en ejecución. Para repetir una versión, retira primero su carpeta de salida o incrementa la versión.

`-SigningThumbprint` y `-SignTool` permiten firmar aplicación e instalador con un certificado de publicación. Sin ellos, el manifiesto registra `Signed: false`. El proceso ya no depende del hash de un EXE aceptado en un ordenador concreto.

El workflow `.github/workflows/release.yml` ejecuta el mismo flujo en Windows y adjunta los paquetes como artefactos al lanzarlo manualmente o crear una etiqueta `v*`. No publica automáticamente una release ni instala una instancia adicional. ARM64 se compila y valida estructuralmente desde x64; la prueba de ejecución requiere un equipo ARM64.

## Notas

- Si `codex` no se detecta automaticamente, puedes definir `CODEX_BINARY` con la ruta completa al ejecutable.
- La deteccion tambien comprueba `PATH`, `where codex`, `%LOCALAPPDATA%\\OpenAI\\Codex\\bin\\codex.exe` y paquetes `OpenAI.Codex_*` en WindowsApps.
- Todas las consultas de uso utilizan HTTP sin lanzar procesos auxiliares: `https://chatgpt.com/backend-api/wham/usage` o la base definida en `config.toml`.
- Las ventanas se clasifican por `windowDurationMins`: 300 minutos para la sesion de cinco horas y 10.080 minutos para la semanal.
- Un 401/403 al consultar consumo solo indica que el `access_token` no fue aceptado; no se interpreta como prueba de que el `refresh_token` haya sido revocado.


## Interfaz nativa

- Tema claro u oscuro según la barra de tareas (`SystemUsesLightTheme`), aunque las aplicaciones usen otro tema. Cuando Windows aplica un color de acento a la barra, el panel incorpora ese matiz. Alto contraste respeta los colores del sistema.
- Acrylic nativo de DWM en Windows 11 22H2 o posterior cuando los efectos de transparencia están activos. El fondo y las filas WPF dejan pasar el material del compositor; texto y barras permanecen nítidos. En otros casos se usa un fondo sólido legible.
- Panel compacto con dos barras horizontales por cuenta, porcentajes restantes alineados, reinicios junto a las etiquetas y acciones en la cabecera. Los perfiles existentes no necesitan migración.
- El panel se abre en el monitor del cursor y ajusta su tamaño al área de trabajo.
- Las filas se reutilizan al actualizar; al retirar perfiles se liberan sus controles y menús.
- El consumo muestra porcentajes restantes, fechas de reinicio, errores y antigüedad del dato. Un reinicio vencido muestra «Sin datos» hasta una nueva consulta, nunca un 100 % supuesto.
- Añadir, renombrar y eliminar son operaciones asíncronas y se serializan con los cambios de cuenta. Salir se bloquea mientras hay una operación de credenciales en curso.
- Los nombres admiten letras Unicode, números, espacios, guiones, guiones bajos y puntos interiores; se rechazan rutas, dispositivos reservados y puntos finales. Los directorios inválidos heredados se ignoran sin borrarlos.
- Un fallo de verificación restaura la sesión anterior, incluida la ausencia de auth.json.

### Comprobación visual aislada

Para generar PNG de los temas, ajustes y estado vacío con cuentas ficticias, sin abrir Codex:

```powershell
dotnet run --project src/AICountsManager.Tests/AICountsManager.Tests.csproj -- --render-ui artifacts/ui-preview
```

Estas capturas verifican el contenido WPF con fondo sólido; el desenfoque de DWM depende del compositor y de la configuración de Windows. Para abrir una vista previa durante dos minutos con cuentas ficticias y el material nativo:

```powershell
dotnet run --project src/AICountsManager.Tests/AICountsManager.Tests.csproj -- --preview-ui
```

La validación de arranque del paquete es obligatoria por defecto. `-SkipLaunchTest` solo permite producir artefactos sin ejecutarlos cuando el equipo de compilación lo bloquea; el manifiesto registra `LaunchValidated: false`. No equivale a una distribución validada.
