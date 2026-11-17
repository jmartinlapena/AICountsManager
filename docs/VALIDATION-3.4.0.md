# Validación de AICountsManager 3.4.0

Comprobaciones del 17 de noviembre de 2026. Código publicado: `3ea41ffa2bc74542cbb7ae68e3131f8b8ca6da46`. [Inventarios y avisos](available-resets.md).

## Código y regresiones

- SDK .NET 10.0.402 e Inno Setup 6.7.3, siguiendo `scripts/build_release.ps1`.
- **118/118 pruebas de Release superadas**, incluidas las consultas simuladas, aislamiento de cuentas, cachés antiguas, zona horaria y horario de verano, caducidad próxima, errores de red/autenticación/servicio y flujos previos de cuentas.
- La primera ejecución de Release detectó una prueba de concurrencia dependiente de una espera de 30 ms. Se sustituyó por una barrera explícita para las primeras dos consultas y un máximo observado actualizado atómicamente. Se mantienen las comprobaciones de cinco consultas, cuatro resultados correctos, concurrencia máxima de dos y credenciales inalteradas. No se modificó el algoritmo de concurrencia de la aplicación para resolverlo.
- Los inventarios se integran en las consultas existentes, sin nuevos temporizadores, servicios o sondeos de conectividad.

## Distribución

Se generaron instaladores y ZIP autocontenidos para x64 y ARM64 en `artifacts/releases/3.4.0`, con el runtime .NET incluido. Ambos paquetes contienen versión de DLL `3.4.0.0` y versión de producto `3.4.0+3ea41ffa2bc74542cbb7ae68e3131f8b8ca6da46`.

Se verificaron individualmente los SHA-256 de los **477 archivos x64** y **476 ARM64** contra sus manifiestos. También se comprobaron las rutas y el contenido descomprimido de cada entrada de los dos ZIP, y los cuatro hashes de instaladores/ZIP registrados en `SHA256SUMS.txt`. Los paquetes no contienen perfiles, credenciales ni configuraciones del usuario.

SHA-256 del ejecutable x64: `AB0F6A0C68B4DF09E9D98742D61A4276027B13900BDE74B28AEA880F113A61B1`.

La generación se hizo con `-SkipLaunchTest` para separar la distribución de la clasificación del antivirus. **El arranque x64 sigue pendiente:** Windows devuelve «Acceso denegado» y el usuario confirma que Panda todavía no lo ha aceptado. Se conserva el mismo binario, sin cambios para alterar su clasificación, y `LaunchValidated` permanece en `false`. ARM64 tampoco se ha ejecutado en este equipo x64. La validación de contenido no sustituye esas pruebas de ejecución.

## Accesos y conservación de datos

Los dos enlaces activos, menú Inicio e inicio automático de Windows, se guardaron y volvieron a leer: ambos apuntan al ejecutable y directorio de trabajo de la 3.4.0. Las copias de los enlaces 3.3.1 están en `artifacts/releases/3.4.0/previous-shortcuts`, fuera de los paquetes.

Se creó una copia privada en `%APPDATA%/AICountsManager/backups/release-3.4.0-20261117-122919`, heredando la protección de la carpeta de datos. Se comprobaron por hash los 18 archivos copiados de perfiles, ajustes, cachés y transacciones; se conservan los tres perfiles guardados de Codex. No se movieron ni sustituyeron los originales. La versión 3.3.1 continúa abierta; el usuario realizará el cierre y apertura cuando Panda permita el binario nuevo.

## Recursos

La prueba aislada con el panel oculto y cuatro inventarios cargados registró, tras el intervalo inicial de carga, 15,62 ms de CPU en diez segundos (**0,011 % del equipo**). La memoria privada fue 129,6 MiB. Son cifras del ejecutor de pruebas con WPF ya creado, no de la nueva versión instalada. La aplicación mantiene la creación diferida del panel y detiene su reloj cuando está oculto.
