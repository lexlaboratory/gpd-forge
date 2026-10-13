# GPD Forge: diagnóstico de entrada, potencia y temperatura

Fecha: 2026-10-06, America/Mexico_City. Alcance: revisión y diagnóstico, sin cambiar potencia, ventilador, firmware, servicios ni drivers.

## Estado verificado

- GPD G1618-04, Ryzen AI 9 HX 370, BIOS 0.10.
- Repositorio: `lexlaboratory/gpd-forge`. Este checkout está limpio en `main`, `4d89ac6`.
- Trabajo de septiembre: `tacodececina/restauracion-y-correcion-cmd`, `08fb305`, checkout en `C:/Users/Alex/orca/workspaces/gpd-forge/restauracion-y-correcion-cmd`.
- El servicio instalado declara `0.4.0+08fb305a09c41d0594a16fc7a77b2d7f73e7123d` y está corriendo. No confundir este checkout de main con el código instalado.
- GitHub: última release v0.3.0, publicada 2026-09-01; sin PR abiertos; rama 08fb305 confirmada en el remoto. La diferencia de la rama con main incluye 365 archivos.
- Entorno del servicio: hardware, ventilador, FPS y perfiles automáticos habilitados. Preferencia persistida: ventilador Balanced, modo windows/auto.
- GPDToolService detenido y deshabilitado. `takeover-state.json` registra la toma de control por Forge el 2026-09-25 y conserva Disabled como estado previo: no asumir que una desinstalación lo habilitará.
- Motion Assistant instalado: 1.2.0.9. No estaba ejecutándose al medir; su driver R0MotionAssistant estaba detenido. PawnIO e inpoutx64 estaban cargados. La presencia de un driver no demuestra que otro programa esté escribiendo al hardware.

## Hallazgos y límites

### Temperatura y apagados

Los registros de Forge contienen 96–98 °C; las sesiones recientes bajo Orca registran 18 W de paquete, alrededor de 80–81 °C de media y un máximo de 97.8 °C conectado a CA. Son lecturas registradas mientras Orca tenía el foco, no una prueba de reposo ni una atribución del consumo a Orca.

Windows registra cuatro Kernel-Power 41 el 2026-10-01 (22:46:41, 22:48:32, 22:51:18 y 22:56:11, hora local), con BugcheckCode=0, PowerButtonTimestamp=0 y sin pulsación prolongada registrada. Eso confirma reinicios tras apagados no limpios, pero no identifica su causa. También existe un evento distinto el 2026-09-24 con código decimal 159 (0x9F), que requiere diagnóstico separado.

No se ha correlacionado cada apagón con la temperatura, alimentación y voltaje inmediatamente anteriores. El historial de caída de voltaje de batería de septiembre es un antecedente, no una explicación confirmada de los nuevos apagones.

### TDP y protección térmica

Eventos recientes del servicio: intentos de aplicar 12, 14 y 15 W, cuatro reintentos, lectura observada null. No puede afirmarse que el firmware revirtió una escritura cuando falta la lectura.

El backend usa por defecto `C:/Program Files/Motion Assistant/amd/ryzenadj.exe`. Una lectura `--info`, dentro y fuera del sandbox, falla con código -1: `WinRing0 Err: Driver not found`, `Unable to get PCI Obj, check permission`, `Unable to init ryzenadj`. Ambas ejecuciones corresponden al contexto de esta terminal; falta una captura desde el contexto privilegiado real del servicio para distinguir permiso, driver, incompatibilidad y parser.

Defectos comprobados por lectura del código correspondiente a 08fb305:

- `SystemProcessRunner` redirige stderr pero no lo consume y descarta ExitCode. Un fallo externo puede terminar como lectura vacía o null, sin una causa útil para la UI; un stderr voluminoso también puede bloquear el proceso.
- `ClosedLoopTdpController` llama «reverted by firmware» a toda falta de verificación, incluso sin lectura.
- El Guardian publica «holding 12 W» antes de comprobar que se aplicó. Su resultado de ApplyAsync no modifica ese mensaje. La orden de reducir potencia no constituye protección física confirmada.
- El valor de watts de paquete de una sesión es consumo medido; no equivale a STAPM ni demuestra por sí solo que un límite falló.

### Ventilador e interfaz

Balanced solicita duty máximo desde 88 °C. Los registros térmicos explican por qué puede resultar muy ruidoso, pero falta leer duty y RPM actuales y comprobar su respuesta a una orden.

`FanPage` cambia la selección de forma optimista, oculta los errores con `catch(() => {})` y obtiene el estado del ventilador una sola vez. `/fan` guarda la preferencia y devuelve capacidad de control, no confirmación de escritura. `FanWorker` descarta el bool de SetManualDuty. El controlador sí compara el registro escrito, pero ese resultado no llega al usuario.

La página Power guarda presets por modo y también oculta errores. Hace falta distinguir en la UI: perfil guardado, potencia solicitada, límites leídos, consumo actual, quién tiene el control y razón de una restricción.

### Gatillos y botones traseros

Forge no tiene una ruta API activa para remapear o calibrar el mando. El escritor HID seguro existe, pero el transporte asumido falla en esta unidad (FeatureReportByteLength=0 y rechazo de pyWinControls, según las pruebas documentadas). El script gpd-winctl no debe presentarse como solución ya validada para este firmware.

Los siete nodos USB/HID VID_2F24/PID_0135 aparecen OK; eso no comprueba el recorrido ni la continuidad de la entrada. Falta confirmar si Alex se refiere a L2/R2 analógicos o L4/R4, y qué acción se corta. Para L2/R2: capturar mínimo, máximo y valor sostenido; para L4/R4: capturar press/hold/release y comprobar si el mapeo es una macro momentánea o una entrada sostenida. No recalibrar ni escribir firmware con esta ambigüedad.

### Aplicaciones del fabricante

- GPDTool.exe tuvo un crash el 2026-10-01 19:35:19, excepción 0xc0000005. Su servicio está deshabilitado, pero eso no demuestra la causa del crash.
- MotionAssistant.exe 1.2.0.9 tuvo un crash el 2026-10-01 21:57:38, excepción 0xc00000fd.
- No se confirmó por qué Motion Assistant no permite el ventilador manual. No se encontraron eventos de bloqueo relevantes en la consulta acotada de CodeIntegrity; ausencia de eventos no descarta bloqueo del driver.

## Hipótesis y pruebas, por prioridad

1. Fallo de acceso o lectura SMU: capturar stdout, stderr y ExitCode de `ryzenadj --info` en el contexto real del servicio; comparar las etiquetas con el parser. Si ese es el fallo, la captura lo distinguirá del rechazo de un límite válido.
2. Temperatura por carga y capacidad de refrigeración: observar carga, potencia y temperatura con un límite bajo confirmado. Si sigue sobrecalentándose a poca potencia real, revisar ventilación, disipador, contacto térmico y alimentación antes de afinar curvas.
3. Control de ventilador sin confirmación o interferencia: medir duty solicitado, leído y RPM con un único controlador activo. Hoy no hay evidencia de que Motion Assistant esté escribiendo simultáneamente.
4. Entrada analógica incompleta o macro de botón: capturar la entrada física fuera de GTA V y repetir dentro del juego. Separar calibración, mapeo y cambio entre teclado/mando.

## Recomendación sobre Motion Assistant

Preparar una comparación reversible con Motion Assistant como único dueño de potencia y ventilador. Conservar Forge y sus datos durante el diagnóstico. Antes: respaldo, comprobar acceso al hardware y devolver ventilador al automático del firmware al retirar Forge. No ejecutar dos escritores sobre EC/SMU ni reducir manualmente el ventilador mientras la temperatura siga crítica.

Reinstalar Motion Assistant no es una garantía: ya está instalada la versión 1.2.0.9, registra un crash y Forge comparte su binario RyzenAdj. No cambiar BIOS, desactivar protecciones de Windows ni flashear el mando como primera prueba.

## Pendientes reconciliados

Prioridad nueva:

1. P0: diagnosticar acceso SMU, errores del proceso y estado real de la protección térmica.
2. P0: correlacionar apagados con temperatura y alimentación; separar el 0x9F.
3. P1: ventilador con estado solicitado/aplicado/verificado y error visible.
4. P1: diagnóstico físico de gatillos/botones y herramienta correcta del fabricante.
5. P1: explicar TDP en watts y fan duty en porcentaje, mostrar propietario y motivo de cambios automáticos.
6. P2: comparar Forge frente a Motion Assistant, con un solo controlador por prueba.

Del roadmap de la rama 0.4.0 siguen pendientes: prueba de F1–F3 en juego real, captura RyzenAdj real, validación hardware RSR/RIS y powercfg, comparación gaming/gaming-battery, separación de métricas ADLX H5, UI de política de energía, PR y release 0.4.0. El remapeo HID continúa bloqueado. Los problemas de E2E anotados en main fueron tratados en la rama; no deben duplicarse como nuevos pendientes sin reproducción.

El cierre de septiembre informa 1661 tests .NET y 272 E2E; no se volvieron a ejecutar en esta revisión y no equivalen a validación física. No hubo cambios de código de producto.

## Evidencia y reproducción

`out/gpd-diagnostics/collect-readonly.ps1` recoge configuración, eventos y sesiones en `evidence.json` sin escribir hardware. Repetir desde la raíz del repo:

```powershell
powershell -NoProfile -File out/gpd-diagnostics/collect-readonly.ps1
gh api repos/lexlaboratory/gpd-forge/releases/latest --jq '{tag_name,published_at,html_url}'
gh api repos/lexlaboratory/gpd-forge/pulls -f state=open -X GET
& 'C:/Program Files/Motion Assistant/amd/ryzenadj.exe' --info
```

La consulta de telemetría MCP fue rechazada por la política de aprobación («approval policy is never»). No se obtuvo telemetría instantánea ni se comprobó físicamente ningún cambio. Las preguntas sobre botones y tipo de apagón quedaron enviadas a Alex.

Fuentes externas: [Microsoft: significado y límites de Kernel-Power 41](https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/event-id-41-restart), [descargas oficiales WIN 4 2025](https://www.softwincn.com/gpdwin42025gjxz), [última release de Forge](https://github.com/lexlaboratory/gpd-forge/releases/tag/v0.3.0).
