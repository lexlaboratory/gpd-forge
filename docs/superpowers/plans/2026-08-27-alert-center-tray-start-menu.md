# Plan: bandeja, menú Inicio y centro de alertas

> **Ejecución:** cada tarea debe seguir RED → GREEN → REFACTOR; ejecutar las pruebas del área antes de pasar a la siguiente.

## 1. Modelo persistente y servicio de alertas (daemon)

**Archivos:** `core/Alerts/AlertEvent.cs`, `AlertSeverity.cs`, `AlertCategory.cs`, `AlertStore.cs`, `AlertService.cs` y sus pruebas en `core.tests/Alerts/`.

1. Escribir pruebas para serialización, valores por defecto, retención simultánea de 30 días/500 eventos, orden descendente y deduplicación por clave/ventana.
2. Escribir pruebas para escritura atómica, permisos esperados, archivo corrupto (renombrado y reinicio limpio) y recuperación.
3. Implementar el modelo inmutable, el store con `File.Replace`/fallback seguro y el servicio de publicación/acknowledge/delete.
4. Integrar límites configurables solo para tests; en producción usar 500/30 días y UTC.
5. Ejecutar `dotnet test core.tests --filter Alerts` y revisar cobertura de componentes nuevos.

## 2. API local y generación de eventos

**Archivos:** `core/Api/*` (router/handlers existentes), `core/Guardian/*`, `core/Telemetry/*`, `core/ForgeWorker.cs`, `core.tests/Api/`.

1. Añadir pruebas de contrato para `GET /alerts`, `GET /alerts/summary`, `POST /alerts/{id}/ack`, `POST /alerts/ack-all` y `DELETE /alerts/{id}`: límites, filtros, 404 y payload inválido.
2. Implementar endpoints sobre `AlertService`, sin exponer la ruta física ni datos sensibles.
3. Conectar fuentes iniciales: umbral térmico existente, lecturas inválidas/malfuncionamiento, caída/recuperación del servicio y cambios de configuración; emitir eventos de recuperación deduplicados.
4. Añadir pruebas de integración que verifiquen persistencia entre reinicios y que `info` no dispare notificación de Windows.
5. Ejecutar la suite completa de `core.tests` y `dotnet build`.

## 3. UI del centro de alertas

**Archivos:** `ui/src/*` (cliente API, navegación, páginas/componentes de alertas, estilos), `tests/e2e/alerts.spec.ts`.

1. Crear pruebas E2E/mock de navegación, contador no leído, filtros, acknowledge-all, delete, estados vacío/error y foco de alerta desde deep-link.
2. Implementar cliente tipado y componentes accesibles: tarjetas por severidad/categoría, detalles técnicos plegables, fechas UTC/local, filtros y acciones.
3. Integrar el contador en navegación y actualizarlo tras mutaciones con manejo de errores/reintento.
4. Aplicar el lenguaje visual premium existente (tipografía, color, espaciado, estados y motion ya usados por la app), con contraste y teclado completos.
5. Ejecutar `npm run typecheck`, `npm run lint` y E2E completo.

## 4. Icono de bandeja y notificaciones Windows

**Archivos:** `scripts/forge-notify.ps1`, `scripts/forge-notify.Tests.ps1` (o harness equivalente), `ui/src-tauri/icons/icon.ico`, documentación de instalación.

1. Añadir pruebas/harness para mutex de instancia única, click/doble click, menú contextual, salida sin detener servicio y polling de summary.
2. Reutilizar `icon.ico` oficial y validar dimensiones/alpha; no generar un icono alternativo. Mantener apariencia premium en tamaños de bandeja y tooltip.
3. Implementar apertura de la app, menú **Abrir/Estado/Salir**, notificaciones solo para `aviso`/`crítica`, dedupe local y deep-link al centro.
4. Cubrir ausencia de sesión interactiva/servicio caído sin bloquear el shell; registrar errores de forma local.
5. Ejecutar pruebas PowerShell y una verificación manual en sesión Windows.

## 5. Instalador, inicio de sesión y desinstalación

**Archivos:** `scripts/install-gpd-forge.ps1`, script de uninstall/upgrade existente, `README.md`, documentación operativa.

1. Añadir pruebas/validaciones idempotentes para eliminar `.url` legado, crear `.lnk` en el menú Inicio del usuario y registrar el arranque de sesión del residente.
2. Implementar copia segura del icono y launcher, tarea/clave de inicio apropiada para usuario (sin ejecutar el daemon en Session 0), y limpieza simétrica al desinstalar.
3. Verificar que actualizaciones no dupliquen accesos ni procesos y que `Salir` del residente no cambie el servicio.
4. Ejecutar instalación en entorno de prueba, reinicio de sesión controlado y desinstalación; documentar rollback.

## 6. Verificación integrada y documentación

1. Ejecutar build, tipos, lint, pruebas unitarias (≥80% en código nuevo), E2E y auditorías de dependencias.
2. Probar reinicio del servicio/equipo, persistencia/retención, alertas térmicas y recuperación, y comportamiento sin hardware.
3. Actualizar `docs/api.md`, `README.md` y `06-OPERACIONES` del vault con instalación, rutas, privacidad y troubleshooting; actualizar `09-AGENTES/kronos/MEMORIA.md`.
4. Revisar seguridad (ACL del store, input validation, no secretos), preparar commit atómico y push tras verificación.
