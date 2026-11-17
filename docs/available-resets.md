# Inventario de resets disponibles

Disponible desde la versión 3.4.0. No modifica el canje de resets ni las sesiones de los clientes.

## Interfaz y actualización

- Una fila plegada entre la identidad de la cuenta y sus cuotas muestra los resets disponibles.
- Al desplegarla aparecen el tipo, los límites afectados y la caducidad. No se realiza ninguna petición al desplegar.
- El mismo control y modelo sirven para todos los proveedores. Actualmente se consultan Codex y Claude; los demás mantienen su interfaz existente.
- F5 y la actualización habitual reutilizan la consulta de cada cuenta. Los perfiles guardados se consultan sin activarlos.
- Los inventarios vacíos o no soportados no ocupan espacio. Un fallo no se convierte en cero; se conserva la última lectura, identificada como anterior.
- Si el próximo reset conocido caduca dentro de siete días o menos, la fila plegada añade `Caduca pronto: 3d 4h`, en el color de aviso del tema. Los resets caducados se excluyen. Sin fecha conocida o con una caducidad posterior no se inventa un aviso. Se reutiliza el reloj visible existente.

## Errores de consulta

La línea de aviso ya existente diferencia transporte/DNS (`Error de conexión · No se ha podido actualizar`), autenticación (`Error de autenticación · Inicia sesión de nuevo`), servicio HTTP 5xx (`Servicio no disponible · No se ha podido actualizar`), límites de consulta y tiempo de espera. Los códigos HTTP siguen disponibles en los metadatos internos, pero no se muestran como un error de Wi-Fi. No se incorporan sondeos de conectividad, monitores de interfaces ni texto bruto de excepciones.

Una desconexión mantiene las últimas cuotas y resets, también para la sesión de Claude todavía no registrada, siempre que siga siendo la misma sesión. Google conserva la causa de transporte cuando fallan sus dos hosts, en lugar de convertirla en un HTTP 503 ficticio. La cancelación solicitada por el usuario sigue propagándose.

## Consultas

Codex conserva `/wham/usage` y consulta `/wham/rate-limit-reset-credits` con el mismo token y `ChatGPT-Account-Id` cuando necesita detalles. Si la respuesta de cuotas ya contiene el inventario completo, se evita la segunda consulta. El detalle tiene un máximo de tres segundos y se omite cuando la cuota ya ha consumido el presupuesto opcional de diez segundos, dejando margen dentro del límite existente de doce segundos por cuenta. Un error del detalle conserva la cuota; `429` detiene y aplaza el resto del lote respetando `Retry-After`.

Claude consulta `/api/oauth/usage?cedar_ember=1`, con `anthropic-beta: oauth-2025-04-20` y el identificador compatible `claude-cli/2.1.316 (external, cli)`, verificado con una sesión Desktop. Es una capacidad interna: un rechazo `400`/`403` permite una sola consulta de compatibilidad sin el parámetro; `401` y `429` no se reintentan. No se ejecuta Claude para detectar versiones ni se renuevan sus tokens. La verificación de identidad y organización existente sigue vigente.

Los detalles de Codex no sustituyen su contador autoritativo cuando la lista es parcial. Claude suma `resets_left`, excluyendo grants pausados, futuros, agotados o caducados. `usable_now=false` no elimina un reset acumulado. El alcance se obtiene de `clears` y la etiqueta del proveedor: `type=full` por sí solo no implica que recupere todas las cuotas.

Referencias de protocolo: [cliente oficial de Codex](https://github.com/openai/codex/blob/main/codex-rs/backend-client/src/client/rate_limit_resets.rs), [consulta OAuth de Claude observada en CodexBar](https://github.com/steipete/CodexBar/blob/main/Sources/CodexBarCore/Providers/Claude/ClaudeOAuth/ClaudeOAuthUsageFetcher.swift). La implementación local no copia código de esos proyectos.

## Caché y hora local

Se amplían `cache/usage_cache.json` y `cache/claude-accounts.json` con metadatos opcionales. No se guardan respuestas completas ni identificadores de canje. La sesión de Claude todavía no registrada tiene una entrada reservada vinculada al hash de la sesión; no se reutiliza para una sesión distinta. Una caché antigua sin estos campos sigue siendo válida y un bloque de resets ilegible no invalida las cuotas.

Las fechas absolutas se normalizan a UTC. La presentación convierte cada caducidad mediante `TimeZoneInfo.Local` e incluye el desplazamiento correspondiente a esa fecha, también en verano. `WM_TIMECHANGE` y `WM_SETTINGCHANGE` invalidan la caché de zona horaria y refrescan el panel visible. No se añade ningún temporizador, dependencia o proceso auxiliar. Se reutiliza el reloj existente del panel, detenido cuando está oculto.

## Validación y demostración

La suite completa contiene 118 pruebas, incluidas seis específicas de inventarios y cuatro de caducidad próxima, clasificación de errores, conservación de datos y avisos de una sola línea. La implementación de los inventarios compilada también se verificó mediante GET con tres perfiles reales de Codex y una sesión real de Claude, sin guardar datos ni cambiar sesiones. Los casos sin conexión y los rechazos HTTP se prueban con respuestas simuladas, sin desconectar la red del equipo.

El ejecutable de pruebas permite generar imágenes con los controles WPF reales y cuentas ficticias:

```powershell
dotnet src/AICountsManager.Tests/bin/Debug/net10.0-windows/AICountsManager.Tests.dll --render-available-resets <carpeta>
dotnet src/AICountsManager.Tests/bin/Debug/net10.0-windows/AICountsManager.Tests.dll --render-quota-feedback <carpeta>
dotnet src/AICountsManager.Tests/bin/Debug/net10.0-windows/AICountsManager.Tests.dll --measure-available-resets <informe.json>
```

La última medición aislada diferencia carga y reposo: sin inventarios registró 31,25 ms de CPU en diez segundos (0,022 % del equipo); el intervalo inmediatamente posterior a crear cuatro inventarios registró 687,5 ms (0,490 %), y el siguiente intervalo estable, sin tocar la interfaz, 15,62 ms (0,011 %). La memoria privada pasó de 123,9 a 129,6 MiB. Son cifras del proceso de pruebas con el panel ya creado, no una medición de la versión instalada ni una garantía de consumo a largo plazo.
