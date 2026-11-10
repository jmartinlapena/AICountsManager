# Proveedores de AICountsManager 3.0

Codex y Claude permiten guardar y activar varias cuentas. Todos los proveedores se habilitan en Ajustes; Claude y Cursor están apagados en una instalación nueva. El flujo y la recuperación de sesiones se documentan en [Cuentas de Codex y Claude](CLAUDE-ACCOUNTS.md). Desactivarlos retira sus tarjetas e históricos y evita consultas y lecturas posteriores.

## Claude

El mismo proveedor acepta la sesión de **Claude Code o Claude Desktop en Windows**. Primero lee `CLAUDE_CONFIG_DIR/.credentials.json` o `~/.claude/.credentials.json`, exclusivamente `claudeAiOauth`. Si falta, no se puede leer o ha caducado, consulta `%APPDATA%/Claude/config.json` y `Local State`. Desktop no crea otra tarjeta ni otro selector.

La caché OAuth de Desktop se descifra en memoria con la clave de Electron protegida por DPAPI para el usuario actual y AES-256-GCM. Solo se usa el access token; no se extrae ni utiliza el refresh token. Se filtra por `lastKnownAccountUuid`, alcance `user:profile` y vigencia. V2 y las entradas de cuenta prevalecen sobre las antiguas, incluidas las marcas de eliminación. Si hay varias organizaciones posibles, no se elige una arbitrariamente. Se comprueba la cuenta y organización contra `/api/oauth/profile` antes de mostrar cuotas. La consulta de cuotas no lee cookies ni escribe Desktop. Los cambios manuales de cuenta conservan su sesión web cifrada según el documento de cuentas.

Los archivos se leen únicamente durante la actualización existente (máximo 1 MiB por archivo y 256 entradas por caché), sin nuevos temporizadores ni procesos. No se retiene la clave descifrada entre consultas. El histórico mantiene sus fuentes de Claude Code actuales; esta adaptación solo amplía la detección de sesión para consultar límites.

Consulta `GET https://api.anthropic.com/api/oauth/usage`, con el access token y `anthropic-beta: oauth-2025-04-20`. Una consulta opcional a `/api/oauth/profile`, limitada a dos segundos, obtiene nombre, correo y plan actuales. Normaliza `five_hour`, `seven_day`, `seven_day_*`, `limits[kind=weekly_scoped]` y uso extra con tope conocido. Los indicadores de ritmo solo se aplican a ventanas semanales.

La consulta de cuotas no renueva ni escribe credenciales externas. Ante caducidad o 401/403 pide iniciar sesión en Claude Desktop o Claude Code; Desktop renueva su propia sesión. No genera mensajes para averiguar cuotas.

El histórico procede de `projects/**/*.jsonl`: mensajes assistant con `message.usage`. Suma entrada, salida, caché leída y creada, deduplicando el ID del mensaje incluso entre archivos. Guarda IDs hash, fechas, modelos, contadores y posiciones; nunca mensajes. No importa tokens de otros clientes que utilicen modelos Claude. La lectura está limitada a 4 MiB, aproximadamente 600 ms y 50.000 eventos indexados. Las fuentes sin cambios no se releen. No se garantiza cobertura de formatos no soportados, por ejemplo contabilidad anidada de asesores.

## Cursor

Lee en modo de solo lectura `%APPDATA%/Cursor/User/globalStorage/state.vscdb`: `cursorAuth/accessToken`, `cursorAuth/cachedEmail` y `cursorAuth/stripeMembershipType`. No escanea cookies ni inicia Cursor.

Consulta el RPC JSON `https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage`, con `Authorization: Bearer` y `Connect-Protocol-Version: 1`. Si no contiene métricas utilizables, consulta `https://cursor.com/api/usage-summary` con la cookie derivada de esa misma sesión. Nunca mezcla sesiones ni rota credenciales.

Muestra el ciclo de facturación y los grupos de modelos recibidos. No suma porcentajes de grupos para inventar un límite global ni aplica previsiones semanales a cuotas mensuales. Los formatos empresariales desconocidos muestran error de consulta en lugar de cifras supuestas.

Los tokens proceden de `POST https://cursor.com/api/dashboard/get-filtered-usage-events`: `inputTokens`, `outputTokens`, `cacheWriteTokens` y `cacheReadTokens`. Se consulta al actualizar el histórico del panel: como máximo una vez cada cinco minutos, cuatro páginas de 500 registros y ocho segundos por pasada. Una paginación incompleta conserva el último periodo completo y se marca parcial. La caché no cruza identidades. Conserva hasta un año desde la primera recogida, sin una descarga masiva del historial anterior.

## Contratos y límites de la validación

Estas son interfaces internas utilizadas por clientes y herramientas de referencia, no una API pública estable de cuotas de suscripción. Sin cuentas reales se verifican contratos, lectura de credenciales, selectores, aislamiento, HTTP 401/429, cancelación, tamaño de respuesta, paginación e indexación incremental mediante fixtures. Claude Desktop se ha validado con una sesión real en Windows el 28 de octubre de 2026: identidad confirmada y límites de 5 horas y semanal recibidos. Cursor sigue sin validación con cuenta real.

Las peticiones usan HTTPS con validación normal, sin redirecciones con credenciales. Las cachés de cuotas y los logs no guardan tokens ni cuerpos de error. Los perfiles de Claude conservan las sesiones cifradas con DPAPI. Un historial ausente no equivale a cero consumo.

Fuentes consultadas el 22 de octubre de 2026, sin instalar ni ejecutar esas herramientas:

- [OpenUsage: Claude](https://github.com/robinebers/openusage/blob/main/docs/providers/claude.md)
- [Cliente Claude](https://github.com/robinebers/openusage/blob/main/Sources/OpenUsage/Providers/Claude/ClaudeUsageClient.swift)
- [Normalización Claude](https://github.com/robinebers/openusage/blob/main/Sources/OpenUsage/Providers/Claude/ClaudeUsageMapper.swift)
- [Logs Claude](https://github.com/robinebers/openusage/blob/main/Sources/OpenUsage/Providers/Claude/ClaudeLogUsageScanner.swift)
- [OpenUsage: Cursor](https://github.com/robinebers/openusage/blob/main/docs/providers/cursor.md)
- [Cliente Cursor](https://github.com/robinebers/openusage/blob/main/Sources/OpenUsage/Providers/Cursor/CursorUsageClient.swift)
- [Normalización Cursor](https://github.com/robinebers/openusage/blob/main/Sources/OpenUsage/Providers/Cursor/CursorUsageMapper.swift)
- [Eventos Cursor](https://github.com/robinebers/openusage/blob/main/Sources/OpenUsage/Providers/Cursor/CursorUsageEvents.swift)
- [Monitorización oficial de Claude Code](https://code.claude.com/docs/en/monitoring-usage): describe OTel, no un contrato público de cuotas OAuth. AICountsManager no habilita telemetría ni recolectores adicionales.

Referencias para Desktop en Windows (28 de octubre de 2026):

- [Electron safeStorage](https://github.com/electron/electron/blob/main/docs/api/safe-storage.md).
- [Formato Windows de OSCrypt en Chromium](https://chromium.googlesource.com/chromium/src/+/63f931c05a5566f6f072c78d95e097998d884376/components/os_crypt/os_crypt_win.cc).
- [Selección de caché Desktop en OpenUsage](https://github.com/robinebers/openusage/blob/main/Sources/OpenUsage/Providers/Claude/ClaudeDesktopAuthStore%2BTokenCache.swift).
