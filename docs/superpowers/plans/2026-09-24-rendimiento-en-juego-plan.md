# GPD Forge — plan por fases: rendimiento en juego, reparaciones y roadmap

## Context
GPD Forge existe para que el GPD Win 4 (Ryzen AI 9 HX 370, pantalla de 60 Hz) rinda lo mejor posible. La sesión del 2026-09-24 dejó resueltos la consola intrusiva, el ventilador, el Guardian y la UI, y midió los límites reales del equipo:
- **Térmico:** sostiene ~22 W a ~88 °C con el ventilador al máximo.
- **FPS:** sin tope, un juego a 130–144 FPS en 60 Hz da tirones (1 % low de 2–17). Con FRTC a 60 mejora (1 % low ~30), pero las escenas pesadas siguen cayendo porque piden más de 22 W.

Alex pidió un plan nuevo que termine lo pendiente, repare lo sugerido y agregue funciones que mejoren el rendimiento.

**Decisiones de Alex:**
1. Priorizar el **rendimiento en juego**.
2. Forge actúa **en automático con aviso**: aplica los perfiles solo y lo notifica; las recomendaciones nuevas se aceptan con un botón.

**Hallazgos de la exploración (facts):**
- **PresentMon:**
  - Captura *todos* los procesos y gana el que más filas tiene en una ventana de 2 s, con marcas de tiempo de llegada, sin relación con el primer plano (`core/Telemetry/PresentMonCsv.cs:118`, `PresentMonFrameRateProbe.cs:100,148`).
  - Descarta las filas `NA` (`PresentMonCsv.cs:56`).
  - Los frametimes existen pero nunca se exponen.
- **cpuClockMhz:** viene de `Win32_Processor.CurrentClockSpeed`, que es el reloj base fijo de 2000 MHz (`WmiTelemetryService.cs:99`). LHM no aporta el reloj real.
- **Coste de telemetría:**
  - Cada `ReadAsync` hace 4 consultas WMI nuevas, `Update()` de todo LHM (CPU, GPU, placa base, controlador) y una lectura del EC, sin caché: ~100–140 ms.
  - La llaman el worker, `GET /telemetry` (UI y overlay, cada 1 s cada uno), `FocusProfileWorker` (1.5 s, solo para `AcConnected`), `/jobs`, `/health/check` y `StandbyService`.
- **Reglas por app:** `AppRule(Id, Match, Mode, Enabled)` (`core/Profiles/AppRule.cs:8`); solo eligen un modo.
- **ADLX:** aplica AntiLag, Chill, Boost y FRTC. RIS es de solo lectura; RSR, FreeSync y la GPU tuning no existen (`core/Gpu/AdlxSettings.cs`).
- **Tuner y auto-FPS:** no usan el 1 % low ni la varianza de frametimes. Sessions no guarda energía (Wh) ni el modo.
- **Política de energía de Windows:** no se toca EPP, boost ni powercfg (fase H4 del roadmap, sin empezar).
- **Freezer:** solo manual (`core/System/FreezerService.cs`).
- **Pendientes de la sesión anterior:**
  - El ciclo del daemon tarda ~1.2 s (máx. 8.5 s).
  - No se aplica el modo al arrancar ni se reafirma el TDP.
  - El TDP manual no se recuerda.
  - Auto-FPS escribe dentro de su banda muerta.
  - Falta un test de `ryzenadj --info` para Strix Point.

Regla de trabajo en todas las fases:
- TDD, cero defectos (build → tipos → lint → tests → seguridad).
- Actualizar `tests/contract/api-contract.json` y el mock daemon con cada endpoint.
- Revisar el diseño en 1280, 720 y 380px.
- Un commit por fase y push; CHANGELOG `[Unreleased]`.

---

## F0 — Cimientos: telemetría rápida y datos correctos (bloquea todo lo demás)
1. **Muestreador único con caché** (`core/Telemetry/TelemetrySampler.cs`, nuevo):
   - Un solo hilo lee el hardware a 1 Hz.
   - `GET /telemetry`, overlay, UI, FocusProfileWorker, Standby y jobs leen la última muestra (≤1 ms).
   - Los `ManagementObjectSearcher` se crean una sola vez. La batería y la zona térmica ACPI se leen cada 5 s.
2. **Reloj real del CPU:** usar el sensor Clock de LHM (media efectiva de núcleos) y dejar `Win32_Processor` como fallback marcado `null` si es estático.
3. **Loop del ventilador independiente** con temporizador fijo de 1 s (`core/ForgeWorker.cs:198-245` → `FanWorker`), para que un `ryzenadj` lento no lo retrase.
4. **PresentMon fiable** (`PresentMonFrameRateProbe.cs`, `PresentMonCsv.cs`):
   - Objetivo = proceso en primer plano (ya lo resuelve `FocusProfileWorker`) o el que coincida con una regla de juego. Nunca "el que más filas tiene".
   - Usar el tiempo de la propia fila (`CPUStartTime`/`TimeInSeconds`) y tolerar `NA`.
   - **Exponer un buffer de frametimes de los últimos 10 s** para F2.
5. **TDP robusto:**
   - Aplicar el modo activo al arrancar el daemon.
   - Reafirmar cada 30 s solo si `ryzenadj --info` difiere.
   - El TDP manual se recuerda como override hasta que cambie el modo.
   - Auto-FPS no escribe dentro de la banda muerta.
   - Test de parseo con salida real de Strix Point capturada del equipo.

**Criterios:**
- ≥0.95 muestras/s bajo carga y `GET /telemetry` < 5 ms.
- El reloj del CPU varía con la carga.
- PresentMon reporta el juego y no el launcher (prueba con Steam abierto más el juego).

## F1 — Perfiles por juego (automático con aviso)
- Extender `AppRule` con overrides opcionales: `stapmW`, `frameCapFps`, `fanMode`, `gpu { antiLag, chill, rsr }` y `freeze[]` (F5). Compatible con el `app-rules.json` actual.
- `FocusProfileWorker` + `ProfileApplier` aplican modo y overrides al enfocar el juego, y restauran al salir.
- Toast y línea en el overlay: "Perfil *Elden Ring* aplicado: 22 W · 60 FPS · Aggressive".
- **UI:**
  - Página **Juegos** (nueva, con icono en el riel), construida desde Sessions: cada juego detectado con su perfil, sus últimas sesiones y un editor de perfil en hoja lateral.
  - En el overlay, el botón **"Guardar como perfil de este juego"** captura el TDP, el tope y el ventilador actuales.
- **Tests:** unitarios de resolución de overrides, E2E del editor y del botón del overlay, y contrato de `/rules`.

## F2 — Frame pacing y detector de tirones
- Métricas desde el buffer de F0: 1 % y 0.1 % low, desviación del frametime y **tirones** (frames > 2× la mediana y > 25 ms) por minuto.
- `GET /frames` con la serie de frametimes de los últimos 10 s y las métricas.
- **Overlay:** mini-gráfica de frametime (SVG, 380px) y un indicador "Ritmo estable / Tirones: N/min".
- **Sessions:** guardar 0.1 % low, tirones/min, energía (Wh), modo y tope por sesión. La página Juegos compara sesiones.
- **Tests:** métricas con series sintéticas (parejas, con picos, 144 FPS en 60 Hz), y E2E de la gráfica con el mock.

## F3 — Asesor de rendimiento ("Forge Advisor")
Motor de reglas puro (`core/Advisor/`, TDD) que lee la sesión en curso, el historial del juego y el techo térmico aprendido. Propone cambios y, si Alex los acepta, los guarda en el perfil del juego:
- **FPS por encima del refresco:** si los FPS superan los 60 Hz sin tope → tope = refresco.
- **Pegado al techo térmico:** 1 % low < 50 % de la media mientras el Guardian recorta → "tope 30" (parejo en 60 Hz) o "activa RSR/baja resolución" (F4).
- **Techo térmico aprendido:** los W en que se estabiliza el Guardian (media por juego, ~22 W hoy). Sugiere fijar el STAPM ahí para no oscilar entre boost y recorte.
- **Juego ligero:** con FPS de sobra → baja W para ganar batería y silencio.

Superficie:
- Tarjeta "Sugerencias" en el dashboard y la página Juegos.
- Aviso en el overlay con botón **Aplicar**.
- Registro de qué se aceptó.

Tests: tabla de escenarios con la entrada y la sugerencia esperada.

## F4 — GPU y Windows al servicio del límite de 22 W
- **ADLX** (`core/Gpu/AdlxSettings.cs`, `AdlxInterop.cs`): **RSR** (Radeon Super Resolution) on/off con nitidez, y aplicar **RIS**; ambos por perfil de juego. Renderizar a menos resolución es lo que más FPS da dentro del techo térmico.
- **Política de energía por modo** (H4 del roadmap):
  - EPP, modo de boost del procesador y estado máximo del procesador en el esquema activo, vía `powercfg /setacvalueindex|setdcvalueindex` con GUIDs.
  - Se lee de vuelta con `powercfg /q` para verificar.
  - Se restaura en `-Uninstall`/`-Restore`.
  - Gaming: boost eficiente, que evita picos de reloj y calor inútiles con TDP fijo. Battery y windows: EPP alto.
- **Tests:** parseo de `powercfg /q`, planificación de escrituras (puro) e integración con el runner falso existente (`SystemProcessRunner`).

## F5 — Modo juego: menos ruido de fondo
- **Congelar en segundo plano mientras juegas** (opt-in por perfil), usando `FreezerService`:
  - La lista sale de procesos pesados medidos, por ejemplo Ollama, LM Studio, OneDrive y GoogleDriveFS.
  - Se descongela al salir del juego y siempre al cerrar el daemon (ya existe `ThawAll`).
  - Nunca toca la lista protegida.
  - Toast con lo que se congeló.
- Pausar las tareas pesadas propias de Forge mientras hay juego activo: SleepStudy, batteryreport y el refresco de jobs.
- **Tests:** política de qué se congela y cuándo; descongelado garantizado al salir y al fallar.

## F6 — Batería y autonomía
- Energía por sesión (de F2) → presupuesto de batería **por juego** en el overlay: "~1 h 20 m en este juego".
- A/B de `gaming-battery` contra `gaming` con Sessions (pendiente del roadmap ROADMAP.md:375) y ajuste del preset con los datos.
- El asesor sugiere `gaming-battery` al desconectar el cargador si el juego lo tolera.

## F7 — Roadmap pendiente y cierre de release
- Overlay en el botón Home (ROADMAP.md:285): enlazar el listener headless con L4/R4 o Menú mapeado por WinControls.
- Diagnosticar el E2E inestable (1 de cada 3), capturando el stdout del mock (ROADMAP.md:372), y arreglar el test inestable `InferenceHoldWorkerTests`.
- Curva sostenida para el modo AI (ROADMAP.md:172): verificar y cerrar con el ventilador ya controlado.
- H5: separar el host de sesión y métricas de rendimiento de ADLX.
- Actualizar ROADMAP y `docs/api.md`.
- Abrir PR de `tacodececina/restauracion-y-correcion-cmd` a `main` y publicar la **release 0.4.0** con CHANGELOG. La firma con SignPath necesita los pasos de Alex en `docs/signing.md`.

---

## Verificación (cada fase)
- `dotnet build` + `dotnet test`, `npm --prefix ui run build`, `npx playwright test` completo (incluye `fit`, `contrast` y las capturas revisadas a ojo) y `security-review` en los cambios con endpoints.
- **En el equipo:** reinstalar (`scripts/install-gpd-forge.ps1 -Substitute -EnableGpuProfiles`) y medir con `get_history`/`/audit`/`/frames`:
  - F0: muestras/s, latencia de `/telemetry` y el proceso de PresentMon.
  - F1–F3: sesión real de juego con Alex (10 min), comparando 1 % low, tirones/min y temperatura contra la línea base del 2026-09-24 (sin tope: 1 % low 2–17; con tope 60: ~30).
  - F4: `powercfg /q` refleja el modo y RSR aparece en `GET /gpu`.
- **Cierre de cada fase:** doc en `GOD.INC/06-OPERACIONES/`, entrada en la MEMORIA de KRÓNOS y push.

## Orden y tamaño estimado
F0 (base, ~1 sesión) → F1 + F2 (el núcleo de "rendimiento en juego", ~2 sesiones) → F3 (~1) → F4 (~1–2; ADLX RSR es lo de más riesgo) → F5 (~1) → F6 (~1) → F7 (~1). Cada fase queda usable por sí sola.
