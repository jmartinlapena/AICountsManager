# Validación de AICountsManager 3.2.0

Comprobaciones del 11 de noviembre de 2026. [Diseño y alcance del cambio](LOCAL-ORGANIZATION.md).

## Backend y regresiones

- Compilación Release correcta, sin errores ni advertencias.
- Batería completa: **100/100 pruebas**, incluidos ocho casos nuevos de organización local.
- Codex A → B → A: crear, renombrar, eliminar y ordenar proyectos; reorganizar asociaciones y orden de chats; conservar los estados propios de la cuenta de destino y las asociaciones de nube. La prueba reproduce la carga de ámbitos observada en el cliente instalado.
- Claude A → B → A: identificar conversaciones por `cliSessionId`, conservar el identificador y ajustes del índice de destino, propagar títulos, fechas de orden, archivo, altas y bajas. Las tareas programadas y los historiales permanecen fuera de la transferencia.
- Errores inyectados en credenciales y en cada escritura del caso de índices múltiples: se recuperan exactamente los bytes anteriores. También se comprueban recuperación al reiniciar, ediciones externas, identidad desconocida, índices duplicados, rutas ajenas y un cliente que se abre a mitad de operación.
- La consulta de cuotas sigue funcionando aunque un índice de organización contenga JSON inválido: no ejecuta el nuevo componente. Las pruebas anteriores de autenticación, renovación, alta, cancelación, proveedores y presentación siguen pasando.
- `git diff --check` correcto.

## Copias del formato real

Se copiaron a un almacén temporal privado los metadatos reales de seis proyectos de Codex y un índice local de Claude Desktop. Los adaptadores completaron el recorrido de ida y vuelta y propagaron las modificaciones hechas únicamente en esas copias. Se verificó que los originales conservaban exactamente sus bytes.

No se cambió ninguna cuenta activa ni se abrió o cerró Codex o Claude. Esta prueba no equivale a recorrer dos cuentas reales en las interfaces de ambos clientes. Los esquemas internos se contrastaron con Codex 26.1103.7773.0 y Claude Desktop 4.23118.1.

## Interfaz y reposo

Los SHA-256 de `WidgetForm.cs`, `WidgetWindow.xaml`, `ProviderViews.cs` y `QuotaResetBadge.cs` coinciden con los anteriores al cambio. No se añadió ningún control ni se modificó el diseño.

El componente nuevo no tiene temporizadores, observadores, procesos auxiliares ni consultas de red. Solo prepara y lee metadatos al activar otra cuenta, o al recuperar una operación interrumpida.

Muestreo del ejecutor de pruebas, con dos cuentas ficticias de Claude, observadores existentes y panel oculto durante diez segundos: **0 ms de CPU**, **0 peticiones de red adicionales**, 129,1 MiB de memoria privada y 198,2 MiB de conjunto de trabajo. Son cifras de ese escenario de pruebas, no una medición de la nueva instancia instalada.

## Distribución y actualización local

Se generaron instaladores y ZIP autocontenidos para `win-x64` y `win-arm64`, bajo `artifacts/releases/3.2.0`, con manifiestos y `SHA256SUMS.txt`. Se verificaron los hashes de los 477 archivos del paquete x64 y los 476 del ARM64.

El intento de ejecutar el paquete x64 original con `--self-test` recibió **Acceso denegado**. No se ha validado todavía el arranque de ese ejecutable y su manifiesto conserva `LaunchValidated: false`. Se deja para la clasificación normal de la protección del equipo, sin alterar esa protección ni el ejecutable publicado. ARM64 no se ejecuta en este equipo x64.

Los dos accesos activos encontrados, menú Inicio e inicio automático de Windows, apuntan a la carpeta publicada 3.2.0. Se guardaron copias de los enlaces anteriores. La instancia 3.1.2, PID 39644 en el momento de la comprobación, permanece abierta desde su carpeta original. Los perfiles Dpto_Tecno, EquipoIA y Personal siguen presentes; esta publicación no migra ni sustituye los datos de las cuentas.

Cuando la protección permita iniciar la nueva versión, el usuario puede salir de la anterior y abrir el acceso habitual. La versión anterior y su paquete permanecen disponibles durante la transición.
