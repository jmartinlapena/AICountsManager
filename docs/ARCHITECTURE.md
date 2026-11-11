# Arquitectura de AICountsManager — 3.2.1

Aplicación de un solo proceso para Windows. WinForms mantiene la bandeja y WPF dibuja el panel al primer uso. Los proveedores comparten contratos y utilidades, conservando sus formatos de autenticación y comprobaciones de cierre.

## Componentes

| Componente | Responsabilidad |
| --- | --- |
| `TrayApplicationContext`, `WidgetForm` | Ciclo de vida de la bandeja y presentación existente. |
| `AICountsManagerService` | Fachada de las acciones actuales, cuotas, históricos y estado del panel. Sus métodos anteriores siguen disponibles. |
| `ProviderComposition` | Crear adaptadores, históricos y cliente HTTP; administrar su ciclo de vida. |
| `ServiceInitialization` / `ServiceStartup` | Una inicialización asíncrona compartida, cancelable al salir. Recuperar y reconciliar antes de ejecutar comandos. |
| `AccountProviderRegistry` | Registro ordenado, activación del proveedor, capacidades y acceso uniforme a sus cuentas. |
| `IAccountProvider`, `IAccountManager` | Consulta de lecturas; alta, cancelación, activación, renombrado y borrado de cuentas guardadas. |
| `CodexAccountSwitcher` | Transacción existente de credenciales de Codex, verificación y coordinación con la organización local. |
| `ClaudeAccounts`, `ClaudeSessionArchive` | Cuentas, credenciales y comprobaciones específicas de Desktop y terminal. |
| `LocalOrganizationTransfer` y sus dos adaptadores | Preparar, respaldar, verificar y recuperar metadatos locales al cambiar de cuenta. |
| `ProtectedJsonStore`, `BoundedFile`, `AtomicFile` | Cifrado DPAPI compatible, lecturas limitadas y escritura atómica. |
| `WindowsIntegration` | Acceso de inicio automático y apertura de carpetas, conservando las acciones existentes. |

La composición registra Codex, los proveedores de sesión actual y Claude en el mismo registro de cuentas. La distinción de capacidades evita confundir consultar una sesión con permitir modificarla. En las consultas HTTP, `AcceptsSavedCredentials` describe el tipo de solicitud admitida por el adaptador; las capacidades de gestión de cuentas pertenecen a `IAccountManager`.

Los métodos utilizados por la interfaz siguen siendo una fachada compatible. Tanto esos métodos como los comandos del contrato común esperan la recuperación inicial. Un proveedor adicional puede implementar o componer `IAccountManager` y registrarse sin añadir casos particulares al registro. La identidad visual y la autenticación siguen perteneciendo al adaptador de cada proveedor.

## Inicialización y coordinación

El constructor compone los objetos y lee la configuración necesaria; no espera tareas asíncronas. La bandeja se crea antes de `InitializeAsync`. La recuperación y reconciliación se ejecutan una sola vez. Cancelar la espera de un consumidor no cancela la inicialización de los demás; salir de la aplicación sí cancela su ciclo de vida.

Los comandos esperan esa fase antes de cambiar datos. Se mantiene el orden de bloqueos existente: operación de cuentas, actividad de cuotas y credenciales. Las comprobaciones de cliente cerrado y el bloqueo global de cada proveedor siguen en los adaptadores originales. Las cuentas de otro proveedor no cambian sus credenciales ni obligan a abrir sus clientes.

La consulta conjunta conserva los límites temporales, la pausa tras errores 429 y los lotes existentes. Una consulta realizada mediante el contrato de un solo proveedor queda limitada a ese proveedor; Actualizar/F5 sigue actualizando el conjunto de cuentas como antes.

## Persistencia y recursos

No hay migración de datos en 3.2.1. Permanecen las rutas, identificadores, nombres de archivo y propiedades JSON de cuentas, cachés, ajustes y diarios. `ProtectedJsonStore` conserva el mismo JSON y DPAPI `CurrentUser` sin entropía adicional. Los archivos anteriores siguen siendo legibles; los archivos escritos con la utilidad nueva también pueden ser descifrados con el lector anterior.

Los adaptadores de organización dependen de utilidades comunes, sin depender del archivo de sesiones de Claude. Se conserva su mecanismo de recuperación y el respaldo por proveedor. La lógica de transacción de credenciales de Codex se extrajo sin alterar sus etapas ni su comportamiento.

No se añaden temporizadores, sondeos, procesos permanentes ni dependencias externas. El arranque crea una única tarea; los observadores y el ciclo de cuotas siguen siendo los existentes. El panel WPF continúa creándose bajo demanda y deteniendo su reloj al ocultarse.

## Pruebas y publicación

`TestGroups.cs` selecciona los grupos por identidad de método, mediante `nameof`, y comprueba miembros ausentes o duplicados. Añadir o reordenar pruebas no cambia silenciosamente lo que ejecutan las opciones existentes de la línea de comandos.

La batería cubre los flujos anteriores y añade compatibilidad de archivos cifrados, arranque concurrente y cancelación, bandeja disponible antes de recuperar, comandos comunes, registro de otro proveedor con cuentas y estabilidad de grupos. La interfaz se verifica también mediante hashes de sus archivos y comparación de capturas con datos ficticios.

La publicación se realiza en una carpeta de versión independiente. Se conservan la instancia y los datos actuales; los accesos apuntan a la nueva versión. El ejecutable nuevo no se inicia hasta que la protección del equipo lo autorice.
