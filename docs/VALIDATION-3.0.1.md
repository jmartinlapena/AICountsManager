# AICountsManager 3.0.1

Verificado el 22 de octubre de 2026:

- Nombre exacto en título de ventana, encabezado, bandeja, mensajes, recursos, ensamblados, proyectos, instalador y accesos: **AICountsManager**.
- Regresión: **74/74 pruebas**, con comprobación explícita del título y encabezado WPF.
- Datos locales trasladados a `%APPDATA%/AICountsManager`, con 4.777 archivos verificados mediante SHA-256. Se conservan cuentas, ajustes e histórico; los logs históricos no se reescriben.
- El árbol actual del repositorio no contiene referencias a la marca anterior. El historial Git se conserva sin reescribir commits.
- La migración desde cualquier carpeta de datos anterior se realiza de forma explícita con `scripts/migrate_data.ps1`; no se adivinan rutas, mezclan destinos ni incrustan nombres antiguos en el programa.
- No se añaden procesos, temporizadores, red ni tareas de fondo. La migración se ejecuta una sola vez, con la aplicación cerrada.

La comprobación de arranque del nuevo ejecutable y su instalación sigue condicionada a la clasificación de Panda; no se han cambiado protecciones del sistema. Véase el informe de 3.0.0 para los protocolos de proveedores y las limitaciones de validación con cuentas reales.
