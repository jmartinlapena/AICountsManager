# Validación de AICountsManager 3.2.1

Comprobaciones del 11 de noviembre de 2026. Código de la refactorización: `e604fd2`. [Arquitectura y componentes](ARCHITECTURE.md).

## Regresiones y compatibilidad

- Compilación Release sin errores ni advertencias.
- **108/108 pruebas superadas**: los 100 casos anteriores y ocho casos nuevos de arquitectura y compatibilidad.
- Interoperabilidad DPAPI: la utilidad compartida lee los archivos con el formato anterior y produce el mismo JSON descifrable por el lector anterior. Se comprueban tanto perfiles de Claude como diarios de organización.
- La construcción y la inicialización repetida conservan exactamente los bytes de la cuenta activa, el perfil guardado, la configuración y los metadatos de organización del caso de prueba.
- La recuperación pendiente se completa antes de ejecutar comandos, incluidos los que acceden directamente al contrato común. Se comprueba la captura de un inicio de sesión terminado mientras el gestor estaba cerrado, conservando ambas cuentas.
- Una inicialización compartida, esperada por varios consumidores, no se duplica ni se cancela por abandonar un solo consumidor. Salir de la aplicación sí cancela su ciclo de vida. La bandeja permanece disponible mientras la recuperación espera el bloqueo de credenciales.
- Codex y Claude ejecutan los comandos comunes conservando la identidad y las credenciales del otro proveedor. El registro admite otro proveedor con varias cuentas sin casos especiales. Los proveedores desactivados conservan sus cuentas y no se consultan.
- Una consulta mediante el contrato de un proveedor no consulta otros. Actualizar/F5 mantiene la consulta conjunta existente.
- Los grupos de pruebas conservan sus miembros al insertar o reordenar casos y detectan miembros ausentes o duplicados.
- Las etapas de la transacción de credenciales de Codex se compararon con la versión anterior: el bloque extraído coincide tras sustituir exclusivamente los accesos a dependencias y la visibilidad de dos ayudantes compartidos con el alta.

Se repitió el recorrido con copias temporales privadas de los metadatos reales de seis proyectos de Codex y un índice de Claude Desktop. Se comprobaron ida y vuelta y propagación de cambios en las copias. Los originales conservaron sus bytes; no se cambiaron las cuentas activas de los clientes.

## Apariencia y flujos

Los SHA-256 de `WidgetWindow.xaml`, `WidgetForm.cs`, `ProviderViews.cs`, `HistoryPanel.cs`, `ProviderIcons.cs` y `QuotaResetBadge.cs` coinciden con los anteriores a la refactorización. No se modificaron controles, estilos, textos, menús ni el código de presentación.

Se compararon ocho capturas de 382 × 710 píxeles: cuotas, tokens de hoy, semana y ajustes en temas claro y oscuro. Fuera del contador dinámico del pie, los píxeles coinciden exactamente. En cada captura cambian únicamente 167 píxeles dentro de ese contador, obtenido en momentos diferentes.

Los métodos anteriores de la fachada conservan sus firmas y resultados. Las pruebas previas siguen cubriendo alta, activación, renombrado, eliminación, cancelación, orden, actualización, apertura y ocultación del panel, autenticación y recuperación.

## Recursos

No se añaden temporizadores, procesos permanentes ni sondeos. El arranque realiza una única tarea asíncrona y el resto del trabajo conserva sus disparadores existentes.

Muestreo del ejecutor de pruebas con dos cuentas ficticias de Claude y el panel oculto durante 10,01 segundos: **0 ms de CPU**, **0 peticiones adicionales**, **129,1 MiB de memoria privada**, 198,5 MiB de conjunto de trabajo, 60 hilos y 725 handles. La referencia anterior del mismo escenario registró 129,1 MiB de memoria privada y 0 ms de CPU. Son mediciones de ese escenario de pruebas, no del ejecutable nuevo instalado.

## Distribución y transición

Se generaron los instaladores y ZIP autocontenidos para x64 y ARM64 bajo `artifacts/releases/3.2.1`, desde el commit de código indicado. Se verificaron los 477 archivos del paquete x64, los 476 del ARM64 y los SHA-256 de ambos instaladores y ZIP.

**No se intentó arrancar el ejecutable nuevo**, siguiendo la petición de esperar la confirmación de Panda. Ambos manifiestos conservan `LaunchValidated: false`. El inicio del paquete nativo queda pendiente de esa autorización; ARM64 no se ejecuta en este equipo x64.

Los dos accesos activos encontrados, menú Inicio e inicio automático de Windows, apuntan a 3.2.1, con copias de los enlaces anteriores. En la última comprobación seguía abierta la instancia 3.2.0, PID 9444. Los tres perfiles guardados de Codex permanecen presentes. La publicación no sustituye ni migra cuentas, ajustes, cachés o historiales.

La comprobación con copias y fixtures no equivale a alternar dos cuentas reales en las interfaces de los clientes. La confirmación del arranque de la nueva distribución se realizará cuando Panda permita ejecutarla.
