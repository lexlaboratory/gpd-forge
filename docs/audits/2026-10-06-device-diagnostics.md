# Diagnóstico GPD Forge: batería y triggers analógicos

Las primeras secciones registran la recolección inicial. La actualización al final
contiene las pruebas posteriores autorizadas; las preferencias iniciales no son
una declaración del estado vigente.

Fecha de medición local: 2026-10-06 (America/Mexico_City). Lecturas del recolector al cierre: 2026-10-06 23:27 local / 2026-10-07 05:27 UTC. Historial previo de batería: [cierre 2026-09-03](C:/Users/Alex/GOD.INC/06-OPERACIONES/cierre-2026-09-03-gpd-forge-bateria-arranque-dpc-limite-carga.md); se usa como antecedente, no como estado vigente.

## Estado observado

- El usuario reporta que L2/R2 no alcanzan el máximo esperado en GTA V y apagones repentinos al usar batería. No se ha reproducido el apagón.
- La muestra local de `root/WMI:BatteryStatus` fue: `PowerOnline=False`, `Charging=False`, `Discharging=True`, `Voltage=11605 mV`, `RemainingCapacity=38207 mWh`; `Rate` vacío. `BatteryFullChargedCapacity=40945 mWh`. El cociente restante/capacidad reportada es 93.3%; 11,605 mV / 3 = 3,868 mV/celda aproximados. Lectura a las 23:27 local. No basta una muestra para diagnosticar resistencia interna ni caída de tensión.
- El operador comunicó una lectura MCP simultánea: 65.4 °C, 14.3 W de paquete, 3,584 RPM, batería 95%, descarga 19.5 W, AC=false, TDP=false. Es una lectura ajena a esta consulta WMI y puntual.
- El archivo de capacidad de Forge contiene muestras entre el 2 de septiembre y el 7 de octubre; las dos últimas son 40,899 mWh (6 oct 18:29 UTC) y 40,899 mWh (7 oct 00:29 UTC), 93.2% del diseño de 43,890 mWh. Historial variable; no se interpreta como tendencia de desgaste con solo estas muestras.
- `C:\Users\Alex\bat-log.csv` tiene 781 filas de una captura antigua y termina el 3 de septiembre a las 18:38 local. No hay voltaje de batería continuo alrededor de los apagones investigados.

## Apagones y correlación temporal

El log `System` contiene 7 eventos Kernel-Power 41 y 7 eventos EventLog 6008 (conteos por ID en todo el log disponible). El 6008 conserva la hora del apagado anterior:

| Hora local del apagado anterior (6008) | ID 41 al siguiente arranque |
|---|---|
| 2026-09-22 17:02:23 | 2026-09-22 17:05:46 |
| 2026-09-24 08:50:33 | 2026-09-24 11:57:07 |
| 2026-09-28 15:59:49 | 2026-09-28 16:00:36 |
| 2026-10-01 22:45:25 | 2026-10-01 22:46:41 |
| 2026-10-01 22:46:53 | 2026-10-01 22:48:32 |
| 2026-10-01 22:48:41 | 2026-10-01 22:51:18 |
| 2026-10-01 22:53:29 | 2026-10-01 22:56:11 |

Los eventos 41/6008 prueban cierres no limpios, no que la batería fuera la causa. Las sesiones conservadas en `sessions.json` (200 entradas) empiezan el 3 de octubre UTC; las alertas (237) empiezan el 2 de octubre UTC. Por ello no cubren esas siete horas de apagado ni permiten correlacionar porcentaje, voltaje, carga de proceso o temperatura. La captura antigua de batería termina tres semanas antes. No se hizo descarga, benchmark ni prueba para provocar un apagón.

Hay 18 sesiones marcadas `OnBattery` en la ventana conservada, todas del 4 al 6 de octubre UTC; las más largas registran descensos porcentuales de 14 puntos entre 18:19–18:33 UTC el 5 oct (72→58%) y de 7 puntos en 18:01–18:10 UTC (87→80%). Son contadores de sesión y no mediciones independientes del voltaje; no demuestran una caída físicamente imposible. Las alertas de batería actuales son principalmente guardias de batería baja/carga prolongada, sin alerta de apagado en esa ventana.

## Controlador, switches y WinControls

- La enumeración PnP muestra tres colecciones HID de GPD `VID_2F24&PID_0135` (dispositivo físico identificado también en el código del repo), varias colecciones `VID_258A&PID_000C`, y el bus virtual Nefarius (`ROOT\SYSTEM\0001`). No aparece un mando Xbox/XInput físico claramente nombrado. La presencia de HID o del bus virtual no valida el recorrido de L2/R2 ni sus valores analógicos. La consulta no modifica el estado de botones ni inicia UI.
- WinControls está registrado como **GPD WinControls 1.15**, con `WinControls.exe` en `C:\Program Files (x86)\win3\WinControls.exe`; la consulta de procesos no lo encontró en ejecución. La carpeta contiene el ejecutable, `DuiLib.dll` y `Resources`; no se halló archivo de configuración junto al ejecutable. No se abrió la GUI, ni se descargó/instaló nada. El repo documenta que en G1618-04 el método programático `HidD_SetFeature` falla con “Incorrect function” y no permite leer la configuración: no intentar escritura programática; cualquier estado de mapeo requeriría lectura desde la app oficial con autorización separada.
- Motion Assistant instalado: `C:\Program Files\Motion Assistant\Profiles\Global.ini` apunta a `lastFile=windows eco`, `StartMode=Normal`, `DSUServer=False`. El perfil persistido `Profiles\General\windows eco.ini` indica `ACTDP=15`, `DCTDP=12`, `AutoSetTDP=True`, `FanControlEnable=True`, `FanSettingSelect=2` y curva dos `True;60;30;50;70;90;10`. Esto solo acredita configuración guardada; no confirma cuál está aplicada en vivo. No se cambió TDP, ventilador ni perfil.
- GPD Forge en `C:\ProgramData\GPD Forge`: configuración persistida observada `fan.json` = `Mode=Balanced`, `ManualDuty=128`; `mode.json` = `active=battery`, `source=auto`. Se leyó sin alterarla.

## Captura analógica preparada, aún pendiente

Se creó [`capture-xinput-triggers.ps1`](../../scripts/diagnostics/capture-xinput-triggers.ps1). Usa `XInputGetState` para sondear slots 0–3 y guardar timestamp UTC/local, conexión y triggers L/R (0–255) cada 100 ms en un CSV dentro de `scripts/diagnostics/`. `-DurationSeconds` por defecto es 30 y está limitado a 1–30 s; `-OutputPath` permite elegir el CSV solo dentro de `scripts/diagnostics/`. Primero hace 1 s de enumeración inactiva (triggers soltados), luego inicia la captura sin pregunta bloqueante. El operador debe presionar L2/R2 hasta el tope, mantener brevemente y soltar. No captura teclado ni otros botones, no calibra ni cambia ajustes.

El recolector **no se ejecutó aún**: hace falta que Alex esté listo para presionar físicamente los switches durante la ventana. Los archivos pasaron validación de sintaxis PowerShell (`Parser::ParseFile`, 0 errores). Las pruebas con fixtures sintéticos de `test-xinput-summary.ps1` pasaron (máximo 230 no califica; secuencia 255/hold/release sí; slot desconectado conserva min/max no disponibles). Esto prueba el análisis puro, no se ha validado contra un mando XInput conectado. Un slot desconectado o max<250 deja el resultado inconcluso para el recorrido físico.

Criterio de evaluación solicitado: tras una pulsación físicamente confirmada, comprobar que L2 y R2 llegan a 255, permanecen en >=250 mientras se mantienen y vuelven a <=5 después de soltarlos. Revisar secuencia temporal en el CSV (min/max global por sí solos pueden incluir el valor 0 inicial); no declarar saturación o defecto basándose solo en HID presente, en el driver virtual, o en max de una ventana sin pulsación hasta el tope.

## Hipótesis y datos faltantes

1. Para L2/R2 aún faltan una lectura XInput producida durante pulsaciones verificadas y una lectura del juego. Si los triggers XInput cumplen el criterio, la causa probable se desplaza a mapeo/configuración del juego; si falla, revisar por separado app/driver y estado físico, sin inferir causa solo del dispositivo HID.
2. Para los apagones, el antecedente del 3 sep describe hundimiento de tensión bajo carga, con estado de salud cercano al 91%, pero es histórico. Los siete reinicios no tienen telemetría coetánea; causa actual sigue abierta entre batería/caída de tensión, pérdida de alimentación, bloqueo/crash u otra condición. Faltan muestra sostenida WMI/voltaje con hora, evento/registro adicional alrededor del siguiente incidente natural, y contexto de AC/carga antes del corte. No descargar a propósito ni ejecutar cargas para reproducirlo.
3. Los saltos entre sesiones marcadas en batería describen porcentaje agregado por intervalos, no carga eléctrica independiente ni validación de colapso de tensión. No hay base para afirmar una “caída imposible” en octubre.

## Restricciones de la recolección inicial

Solo lectura de eventos, WMI/CIM, PnP, procesos, registro de desinstalación, archivos de historial y configuraciones indicadas. Se creó un script bajo `scripts/diagnostics/` y este informe bajo `docs/audits/`. No hubo GUI, elevación, cambios de servicio/driver/firmware/TDP/ventilador, benchmark, descarga provocada, descarga/instalación de software, ni commits.

## Actualización: pruebas físicas y comparación autorizadas

Alex confirmó estar listo; se le pidió presionar L2/R2 a fondo, mantener y soltar.
La captura empezó el 2026-10-06 a las 23:39:13 locales y duró 30.02 s. El slot 0
se conectó durante la ventana, con 140 muestras conectadas; los otros tres slots
no aparecieron. L2 dio min=0/max=223 (87.5%) y R2 min=0/max=181 (71.0%). Ninguno
llegó a 255 ni se mantuvo >=250; tampoco hubo retorno <=5 posterior al último
pico dentro de la ventana. Es evidencia de rango incompleto en Windows durante
esta prueba, sin establecer todavía si la causa es calibración o una pieza física.
PnP posterior identificó Xbox 360 Controller for Windows, USB VID_045E/PID_028E.
La presencia de Nefarius no establece que este mando sea virtual.

Se abrió WinControls 1.15: firmware mostrado X409K407, pantalla de botones
traseros y modo ratón. Se cerró sin guardar mapeos. GamePad Test Calibration Tool
es una utilidad distinta: su descarga y calibración siguen pendientes.

RyzenAdj instalado falló incluso elevado: exit -1, `WinRing0 Err: Driver not found`,
imposibilidad de inicializar PCI. No se agregó ese driver ni se debilitó Windows.
El módulo firmado PawnIO RyzenSMU respondió: CPU code 31, tabla 0x5D0009,
MP1 response=1. El refresco incluido envía PSMU 0x65 con argumento 3 y da datos
incoherentes aquí. Enviar 0x65 con argumentos cero permitió leer límites coherentes:
18 W STAPM, 24 W fast, 22 W slow y 100 C Tctl. Esa prueba leyó límites; no los cambió.

El backend nuevo valida CPU, versión, capacidades y tabla antes de escribir. Usa
el mailbox MP1 separado para 0x14/0x15/0x16/0x19, mutex PCI y esperas acotadas.
Fuentes primarias: [RyzenAdj API](https://github.com/FlyGoat/RyzenAdj/blob/master/lib/api.c),
[mailboxes RyzenAdj](https://github.com/FlyGoat/RyzenAdj/blob/master/lib/nb_smu_ops.c),
[módulo PawnIO](https://github.com/namazso/PawnIO.Modules/blob/main/RyzenSMU.p).
La construcción con fixtures no sustituye la validación instalada.

Se respaldaron Forge instalado, ProgramData, servicio e INI de Motion Assistant.
Forge se detuvo antes de iniciar Motion Assistant; GPDTool siguió detenido y
deshabilitado. El Forge antiguo demoró al cerrar y registró OperationCanceledException
en WindowsServiceLifetime; finalmente paró. Fue un fallo de cierre del proceso,
sin apagado del equipo.

Motion Assistant 1.2.0.9 + Power Mod 2.2.2 inició elevado con windows eco y solicitó
DC TDP=12 W. PawnIO independiente a las 23:43:54 locales seguía leyendo 18/24/22 W.
La pantalla mostraba Fan Speed `-- rpm`; durante el intento de seleccionar Fixed,
Enable Fan Speed apareció desactivado y los controles inhabilitados. No se confirmó
control manual. Cerrar su ventana no terminó el proceso: se detuvo exclusivamente
el PID 42776 de esta prueba tras verificar su ruta. Se restauraron los INI y Forge.
A las 23:45:26 locales Forge estaba Running y Motion Assistant cerrado; la preferencia
vigente de fan al volver era Quiet. No se corrieron dos controladores simultáneos.

[GPD documenta](https://www.gpd.hk/gpdwin42025firmwaredriver) la dependencia de
Motion Assistant de WinRing0 para TDP, sensores y ventilador. Volver a esa versión
no resolvió el control en esta comparación.

## Entrega instalada y verificada: 2026-10-07

Fuente instalada `6029655a125a6456613992f43c09f8c1cd6633e2`, versión 0.4.0.
Se verificaron los 73 archivos del manifiesto contra el destino. SHA256 del DLL:
`01470E48B7E9A703866CDFEB0108CCB76C7E892ED3A9945414482126430F745F`;
app Tauri `9DC34F60A9ADC50489CF848B48EB6E6B1E116E026607BED5AFA576C5BA4830BC`.
La app instalada abrió y se revisaron Power y Fan contra el daemon real.

El primer intento de copia falló por la DLL retenida por el agente GPU de Forge;
el rollback también encontró ese bloqueo. El servicio quedó detenido durante
la recuperación. Se identificó y detuvo exclusivamente el agente GPU PID 25132
por ruta y argumentos; se retomó la copia verificada y el servicio inició a las
00:20 locales. El agente GPU se reinició en la sesión del usuario; `/gpu` Ready.
No se borró el árbol instalado: se preservaron PresentMon y los demás auxiliares.
El respaldo de instalación y configuración está en el directorio privado
`out/repair-validation/backup/deploy-20261007-001844`.

Registro del servicio: se añadió únicamente `GPDFORGE_TDP_BACKEND=pawnio-strix`;
hardware, fan, FPS y perfiles automáticos conservaron sus flags. Servicio SYSTEM,
inicio automático; Motion Assistant cerrado y GPDToolService Stopped/Disabled.

Prueba 00:21 local: POST TDP 10 W devolvió requested=10, observed=10,
verified=true, error=null. Re-seleccionar Battery restauró 8 W, fast 12 W,
owner=mode, manualStapmW=null, verified=true en un intento. Fan Manual 255 devolvió
requestedDuty=observedDuty=255 y verified=true. Se restauró Quiet/manualDuty128;
cuatro segundos después duty 120/120, verified=true, 3072 RPM y CPU 67 C.
La orden manual breve verificó el registro EC; no midió RPM máxima estacionaria.

Observación normal sin benchmark: 13 muestras 00:08:48–00:09:49 antes de instalar,
CPU 69.6–80.9 C, paquete 8.4–22.6 W, voltaje 10344–10838 mV. Tras instalar,
las primeras diez muestras con batería (00:21:30–00:22:16) dieron CPU
66.8–67.4 C, paquete 7.5–8.5 W y fan 3072 RPM. A las 00:22:21 se detectó AC;
el modo automático eligió Windows y confirmó 15 W/fast20 W. La observación
mixta no es una comparación controlada: después de conectar CPU alcanzó 89.4 C
y paquete 19.7 W, con aumento del ventilador. El boost, la carga y el calor al
cargar deben evaluarse antes de atribuir la temperatura a una falla física.
No se reprodujo un apagón ni se provocó descarga profunda o carga de estrés.

Validación: .NET 1709/1709; cobertura de líneas ejecutables añadidas 91.74%
en la suite (95.65% al combinar la lectura física separada); cobertura global
de la suite 70.30%. UI completa 283/283 antes de los últimos ajustes de texto;
regresiones finales 17/17, visual 6/6 y fit final 1/1. Build Release/Tauri,
tipos y lint/analyzers pasaron; NuGet y npm no reportaron vulnerabilidades.
`Program.cs` conserva 72 diagnósticos de formato fuera del diff y una prueba
conserva el warning anterior xUnit1031. No se ocultaron en la cobertura.

GamePad Test Calibration Tool no pudo descargarse: GPD HTTP429 incluso tras una
espera, espejo publicado con Microsoft OAuth o Cloudflare403. La revisión
automática también rechazó abrir la página del fabricante en Edge sin motivo
específico. No se burló autenticación/desafíos ni se usó un binario de origen
desconocido. La calibración y su repetición XInput siguen pendientes; no afirmar
que los gatillos se repararon. Los apagones siguen abiertos sin telemetría de
los eventos de octubre 1. Volver a Motion Assistant 1.2.0.9 no mejoró la prueba.

Para re-medir el estado vigente:

```powershell
Invoke-RestMethod http://127.0.0.1:8787/version
Invoke-RestMethod http://127.0.0.1:8787/tdp
Invoke-RestMethod http://127.0.0.1:8787/fan
Invoke-RestMethod http://127.0.0.1:8787/telemetry
Get-CimInstance -Namespace root/wmi -ClassName BatteryStatus |
  Select-Object Voltage,DischargeRate,RemainingCapacity,PowerOnline
```

Cierre: [PR 1 en borrador](https://github.com/lexlaboratory/gpd-forge/pull/1), base
`tacodececina/restauracion-y-correcion-cmd`; no merge ni release. GOD.INC recibió
el informe de operaciones y memoria en `9bc43ca005251ea0753ad4a559493a54c3022b9e`;
se verificaron SHA remoto y presencia de ambas entradas en `origin/main`.

