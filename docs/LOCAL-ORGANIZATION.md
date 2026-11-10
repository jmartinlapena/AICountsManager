# Organización local al cambiar de cuenta — 3.2.0

Al activar otra cuenta guardada de Codex o Claude, la organización local de la cuenta saliente sustituye la organización anterior del destino. La última cuenta utilizada es la referencia: se trasladan las altas, renombrados, eliminaciones, orden y asociaciones admitidas por cada adaptador. La interfaz y las acciones existentes permanecen iguales.

El componente común `LocalOrganizationTransfer` solo se invoca desde el cambio de cuenta, bajo el bloqueo de credenciales del proveedor. No observa carpetas ni añade temporizadores, consultas de red o escaneos durante el reposo. Actualizar cuotas no ejecuta esta transferencia. Activar una cuenta que ya está activa no traslada su organización a otro ámbito.

## Flujo y recuperación

1. Exigir que el cliente correspondiente esté cerrado; el otro proveedor no interviene.
2. Preparar el estado de destino en memoria y comprobar los formatos y rutas.
3. Guardar un diario cifrado con DPAPI y los bytes anteriores antes de modificar metadatos.
4. Aplicar la organización y ejecutar el cambio de credenciales existente.
5. Verificar ambos y confirmar. Si falla, restaurar la organización y las credenciales anteriores.

Si el cliente se abre durante la operación, se detienen las escrituras y se conserva el diario pendiente hasta que pueda recuperarse con el cliente cerrado. Una edición externa que no coincida con el estado anterior ni con el preparado impide sobrescribirla automáticamente. Una identidad desconocida también conserva el respaldo sin reescribir datos.

Tras una interrupción, las credenciales existentes determinan la recuperación: si sigue instalada la cuenta saliente, se restauran los metadatos anteriores; si está instalada la entrante y la organización terminó de aplicarse, se confirma el cambio sin descartar posibles ediciones posteriores de esa cuenta. La recuperación de credenciales mantiene su lógica previa.

Los diarios quedan en `%APPDATA%/AICountsManager/transactions/organization-<proveedor>.bin`. Se conserva un único respaldo cifrado de la última operación por proveedor en `backups/organization-<proveedor>-last.bin`; no se acumulan copias por cada cambio. DPAPI los vincula al usuario de Windows. No contienen copias de directorios de proyectos ni de transcripciones.

## Codex

`CodexOrganizationAdapter` prepara el ámbito entrante de `.codex-global-state.json`, dentro de `electron-account-renderer-state-v1.inactive`. Calcula su identificador con SHA-256 del JSON compacto `[userId, accountId]`, como el bootstrap del cliente instalado. Deja que Codex conserve el propietario saliente y cargue el ámbito de destino al abrirse.

Se trasladan únicamente el catálogo `local-projects`, `project-order`, la selección local, apariencias, orden de chats, asociaciones locales y referencias de migración de esos proyectos al catálogo compartido. Las asociaciones de chats con proyectos de nube del destino se conservan; no se copian las del origen. Los estados de identidad, cachés de nube, permisos y otros ajustes propios del destino no se sustituyen.

No se modifican las bases SQLite, los proyectos del disco ni `sessions`/`archived_sessions`. Los clientes anteriores sin estados por cuenta conservan su organización global existente. Un esquema desconocido detiene el cambio antes de instalar credenciales.

## Claude Desktop, pestaña Code

`ClaudeCodeOrganizationAdapter` trabaja exclusivamente con los índices `claude-code-sessions/<accountUuid>/<organizationUuid>/local_<uuid>.json`. Reconoce las conversaciones por `cliSessionId`, no por su nombre visible ni por el nombre del perfil.

Si una conversación ya existe en el destino, conserva su `sessionId` y sus campos específicos, incluidos ajustes de ejecución y conectores. Actualiza las referencias a las carpetas existentes, título, fechas de orden y estados de organización. Una conversación nueva recibe un índice mínimo con esos metadatos; no copia configuraciones privadas de ejecución del origen. Los índices locales del destino ausentes en la cuenta saliente se retiran, sin eliminar sus transcripciones.

Un directorio de origen existente pero vacío representa una organización vacía y sustituye los índices anteriores. La ausencia de ese directorio representa un estado no observado, por ejemplo un perfil utilizado únicamente en terminal: no borra los índices del destino. Los identificadores ambiguos o los formatos no admitidos detienen el cambio. Se limitan el número de registros, su tamaño y el tamaño del diario.

Los proyectos alojados en Claude.ai, Cowork, tareas programadas y transcripciones quedan fuera de este mecanismo. No hay sincronización de nube, importación de conversaciones ni duplicación de historiales.

## Validación y límites

Las pruebas ejercitan A → B → A con creación, renombrado, borrado, orden, asociaciones e identidad; inyectan fallos de credenciales y de metadatos y verifican restauraciones byte por byte. También cubren reinicios, aperturas simultáneas del cliente, rutas ajenas, ediciones externas y la independencia de las consultas de cuotas.

Se ha contrastado el formato con Codex 26.1103.7773.0 y Claude Desktop 4.23118.1. Ambos esquemas son internos y pueden cambiar. La prueba adicional con copias privadas de los índices reales no cambia las cuentas activas y no equivale a abrir ambas cuentas reales en la interfaz de cada cliente.
