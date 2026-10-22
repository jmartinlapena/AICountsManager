# Validación de AicountsManager 3.0.0

Fecha: 22 de octubre de 2026. Entorno: Windows x64, SDK .NET 10.0.402.

## Comprobaciones realizadas

- Regresión completa: 74/74 pruebas superadas, incluidas las 67 existentes de Codex/Antigravity y siete grupos nuevos de Claude/Cursor.
- Reejecutados los siete grupos nuevos después de ampliar los casos de cuota empresarial agrupada de Cursor, eventos sin contabilidad de tokens y ruta opcional inválida de Claude: todos pasan.
- Renderizado real WPF en claro y oscuro de cuentas, modelos, rosquilla y ajustes con los cuatro proveedores y datos ficticios. Sin cambios en la transparencia de producción.
- Claude/Cursor: selectores persistentes, desactivados por defecto, sin consultas ni lecturas de sus credenciales mientras están desactivados. Solo Codex admite cambios de cuenta.
- Lectura de credenciales JSON/SQLite sin modificaciones; HTTP 401/429, Retry-After, cancelación y límites de respuesta; históricos incrementales, deduplicación y aislamiento de cuentas/proveedores.
- Publicación autocontenida e instalador Inno Setup compilados para win-x64 y win-arm64. Verificados máquina PE, versión, dependencias y contenido de los ZIP contra los manifiestos SHA-256. No se empaquetan cuentas ni credenciales.

## Recursos

Prueba aislada con datos ficticios de cuatro proveedores, no la aplicación instalada: tras crear el panel, 0 ms de CPU en 10 segundos; después de ocultarlo, 0 ms en otros 10 segundos. Memoria privada 123,7/124,4 MiB y conjunto de trabajo 195,1/196,8 MiB respectivamente. Incluye el proceso de pruebas y WPF cargado; no debe compararse con el arranque de la bandeja sin abrir WPF ni presentarse como consumo de producción.

No se han añadido temporizadores de fondo. Las cuotas reutilizan el ciclo existente de cinco minutos; los históricos se actualizan al abrir o actualizar el panel. La interfaz sigue creándose de forma diferida. Los escaneos y consultas nuevas tienen presupuestos de tiempo/tamaño y caché incremental.

## Pendiente de validación externa

- No hay sesiones reales de Claude/Cursor disponibles: se han contrastado los contratos con código público de referencia y probado respuestas controladas, no autenticación real de esos servicios.
- Panda Adaptive Defense 360 bloquea preventivamente el EXE nuevo como «Programa no confiable bloqueado». El usuario ha confirmado el aviso. Los permisos de archivo permiten ejecución; no se han desactivado protecciones ni creado exclusiones. No se ha validado el arranque de producción, la instalación/desinstalación ni su consumo real. Los manifiestos lo indican con `LaunchValidated: false`.
- Los paquetes no están firmados: no hay certificado de firma de código disponible. El script admite un certificado válido mediante `-SigningThumbprint`; no se instala una raíz de confianza ni se autofirma el producto.
- ARM64 se ha compilado y comprobado estructuralmente; no se ha ejecutado en hardware ARM64.

El script normal exige el smoke test nativo. `-SkipLaunchTest` permite generar artefactos pendientes de esa validación sin marcarla como superada. Debe probarse `AicountsManager.exe --self-test <ruta-de-informe>` después de la clasificación/autorización de Panda y antes de sustituir la versión anterior.

[Procedimiento oficial de clasificación de WatchGuard](https://www.watchguard.com/help/docs/help-center/en-us/content/en-us/Endpoint-Security/monitor-threats/allow-blocked-items.html).
