# Diseño: bandeja, menú Inicio y centro de alertas

**Fecha:** 2026-08-27  
**Estado:** pendiente de aprobación para implementación

## Objetivo

Cuando GPD Forge esté instalado y el usuario haya iniciado sesión, habrá un único icono residente en la bandeja del sistema. Al abrirlo se mostrará la aplicación web local; el acceso del menú Inicio abrirá la misma aplicación. El mismo flujo alimentará un centro de alertas persistente, legible y accionable.

## Alcance funcional

### Proceso residente y menú Inicio

- `forge-notify.ps1` será el único proceso residente de sesión de usuario; deberá impedir duplicados mediante mutex.
- Usará el icono oficial de `ui/src-tauri/icons/icon.ico`, sin crear una identidad visual paralela.
- Doble clic, menú **Abrir GPD Forge** y notificaciones accionables abrirán `http://127.0.0.1:8787` (o la URL configurada del servicio).
- El menú contextual incluirá **Abrir**, **Estado del servicio** y **Salir del icono**. Salir no detiene el servicio de Windows.
- El instalador creará un acceso `.lnk` real bajo el menú Inicio del usuario, apuntando a la aplicación/launcher, y eliminará el acceso `.url` legado. La desinstalación retirará el acceso y la tarea de inicio del usuario.
- El residente arrancará al iniciar sesión, separado del servicio (que vive en Session 0).

### Centro de alertas

El daemon será la fuente de verdad. Persistirá eventos en `%ProgramData%\GPD Forge\alerts.json`, con escritura atómica y tolerancia a corrupción (renombrar el archivo dañado y empezar uno nuevo, registrando el incidente).

- Retención: máximo 500 eventos y 30 días, aplicando ambas restricciones.
- Evento: `id`, `timestampUtc`, `severity` (`info`, `aviso`, `crítica`), `category` (`thermal`, `hardware`, `service`, `configuration`, `system`), `title`, `message`, `technicalData` opcional, `acknowledged`, `dedupeKey` opcional.
- Dedupe por `dedupeKey` y ventana temporal para evitar tormentas; los eventos de recuperación cierran visualmente el incidente sin borrar el historial.
- Severidad `info`: historial y centro solamente. `aviso` y `crítica`: además de persistirse, generan notificación Windows no intrusiva (respetando el límite de repetición).
- Fuentes iniciales: sobrecalentamiento, malfuncionamiento/lecturas inválidas, caída o recuperación del servicio y cambios de configuración relevantes.

### API

- `GET /alerts?limit=&unreadOnly=` devuelve eventos más recientes, ordenados por fecha descendente.
- `GET /alerts/summary` devuelve total no leído por severidad y el último evento.
- `POST /alerts/{id}/ack` marca un evento como leído.
- `POST /alerts/ack-all` marca todos los eventos como leídos.
- `DELETE /alerts/{id}` elimina un evento individual (la retención sigue aplicándose).
- Las rutas validan límites, identificadores y payloads; no exponen rutas de disco ni secretos.

### UI

- La navegación mostrará **Centro de alertas** con contador de no leídas.
- Se mostrarán tarjetas profesionales con severidad, categoría, fecha relativa/absoluta, mensaje y detalles técnicos plegables.
- Habrá filtros por severidad/categoría, marcar como leído, marcar todo como leído y eliminar; estados vacíos y de error serán claros y accesibles.
- Hacer clic en una alerta crítica/aviso desde la bandeja abrirá el centro y enfocará ese evento.

## No objetivos

- No se cambiará el contrato del control PWM ni la política térmica existente.
- No se añadirá un segundo daemon residente ni una base de datos externa.
- No se enviarán datos fuera del equipo; las alertas son locales.

## Calidad, seguridad y aceptación

- El almacenamiento se restringirá a administradores/SYSTEM y al usuario que ejecute el daemon, sin credenciales ni datos sensibles.
- Se cubrirán con pruebas unitarias la retención, deduplicación, recuperación de corrupción, severidades y validación de API.
- Las pruebas E2E cubrirán navegación, contador, filtros, acknowledge/delete y apertura desde bandeja/menú Inicio (con adaptador mock cuando no exista shell Windows).
- Instalación, actualización y desinstalación serán idempotentes; no se crearán residentes duplicados.
- La aceptación requiere build, tipos, lint, pruebas unitarias (mínimo 80% en componentes nuevos), E2E y revisión de seguridad sin regresiones.

## Criterios de aceptación

1. Tras instalar e iniciar sesión, aparece un único icono oficial; abrirlo muestra GPD Forge y salir del icono deja el servicio intacto.
2. El menú Inicio contiene un único acceso funcional y no queda el `.url` legado.
3. Un sobrecalentamiento produce una alerta persistente y una notificación `aviso`/`crítica` sin duplicados masivos; la recuperación queda registrada.
4. Reiniciar el servicio o el equipo conserva como máximo 500 eventos/30 días y el contador refleja no leídas.
5. Todas las operaciones del centro funcionan desde la UI y sus endpoints validan entradas y manejan errores de forma segura.
