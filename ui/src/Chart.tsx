// GPD Forge UI — reusable telemetry chart primitives. GPL-3.0-or-later.
// Self-contained: pure SVG + React state, zero external charting dependencies.
//
// Usage
// -----
//   import { Sparkline, useHistory } from './Chart'
//
//   // Feed it straight from live telemetry — accumulates the last N samples
//   // and returns a stable array reference (only re-created when it grows):
//   const cpuHistory = useHistory(tele?.cpuTempC ?? NaN, 60)
//   <Sparkline data={cpuHistory} label="CPU" unit="°C" color="var(--accent)" />
//
//   // Or drive it from any number[] you already track yourself:
//   <Sparkline data={wattSamples} label="Power" unit="W" color="var(--accent-2)" />
//
// Sparkline is a compact "trend" figure — meant to sit beside an existing
// numeric readout (e.g. a <Tile>), not replace a full chart — so it skips
// axes/gridlines/legend by design (a single series needs no legend; the
// `label` already names it). Colors default to this app's dark-theme vars
// (var(--accent) etc.), each with a hex fallback so it still renders
// correctly outside this app's CSS. Handles 0 or 1 data points gracefully
// and ignores NaN/±Infinity samples.

import { useEffect, useId, useMemo, useState } from 'react'
import type { CSSProperties, PointerEvent as SvgPointerEvent } from 'react'

export interface SparklineProps {
  /** Numeric samples, oldest first, newest (current) last. */
  data: number[]
  /** Intrinsic width in px — also the SVG viewBox width. Default 120. */
  width?: number
  /** Intrinsic height in px — also the SVG viewBox height. Default 36. */
  height?: number
  /** Line, fill and marker color. Any CSS color, incl. theme vars. Default 'var(--accent)'. */
  color?: string
  /** Surface the end-dot's separation ring is drawn against (match the card it sits in). Default 'var(--bg-elev)'. */
  surface?: string
  /** Unit suffix appended to displayed values, e.g. '°C', 'W'. */
  unit?: string
  /** Short name shown above the chart, e.g. 'CPU'. Also feeds the default accessible name. */
  label?: string
  /** Decimal places for displayed values. Default 1. */
  precision?: number
  /** Show the bold last-value readout above the chart. Default true. */
  showValue?: boolean
  /** Hover/focus crosshair + per-sample tooltip. Default true. */
  interactive?: boolean
  /** Full override of the chart's accessible name (role="img"). */
  ariaLabel?: string
  className?: string
  style?: CSSProperties
  testid?: string
}

interface Pt { x: number; y: number }

const round2 = (n: number) => Math.round(n * 100) / 100
const clamp = (v: number, min: number, max: number) => Math.min(Math.max(v, min), max)
const fmtValue = (v: number | null, precision: number) => (v === null || !Number.isFinite(v) ? '—' : v.toFixed(precision))
const withUnit = (v: string, unit: string) => (unit ? `${v} ${unit}` : v)

function layout(data: number[], width: number, height: number, pad: number): Pt[] {
  const n = data.length
  if (n === 0) return []
  let min = Infinity
  let max = -Infinity
  for (const v of data) {
    if (v < min) min = v
    if (v > max) max = v
  }
  const span = max - min
  const innerW = Math.max(width - pad * 2, 0)
  const innerH = Math.max(height - pad * 2, 0)
  return data.map((v, i) => {
    const t = n === 1 ? 1 : i / (n - 1)
    const norm = span === 0 ? 0.5 : (v - min) / span
    return { x: round2(pad + t * innerW), y: round2(pad + (1 - norm) * innerH) }
  })
}

// Smooth line through the samples: every interior point is approached and left
// via a quadratic curve anchored on the midpoint to its neighbour, so the line
// bends through each sample without the overshoot a full spline can introduce.
function smoothPath(points: Pt[]): string {
  const n = points.length
  if (n === 0) return ''
  if (n === 1) return `M ${points[0].x} ${points[0].y}`
  if (n === 2) return `M ${points[0].x} ${points[0].y} L ${points[1].x} ${points[1].y}`
  let d = `M ${points[0].x} ${points[0].y}`
  for (let i = 1; i < n - 1; i++) {
    const curr = points[i]
    const next = points[i + 1]
    const midX = round2((curr.x + next.x) / 2)
    const midY = round2((curr.y + next.y) / 2)
    d += ` Q ${curr.x} ${curr.y} ${midX} ${midY}`
  }
  const prev = points[n - 2]
  const last = points[n - 1]
  d += ` Q ${prev.x} ${prev.y} ${last.x} ${last.y}`
  return d
}

function areaPath(linePath: string, points: Pt[], baselineY: number): string {
  if (points.length < 2) return ''
  const first = points[0]
  const last = points[points.length - 1]
  return `${linePath} L ${last.x} ${round2(baselineY)} L ${first.x} ${round2(baselineY)} Z`
}

/**
 * Mini area/line chart for a single live-updating series (temperature, watts,
 * FPS…). SVG-only, no external deps. Pair it with `useHistory` to feed it from
 * one telemetry field, or pass any number[] you already maintain.
 */
export function Sparkline({
  data,
  width = 120,
  height = 36,
  color = 'var(--accent, #4cc2ff)',
  surface = 'var(--bg-elev, #131824)',
  unit = '',
  label,
  precision = 1,
  showValue = true,
  interactive = true,
  ariaLabel,
  className,
  style,
  testid,
}: SparklineProps) {
  const reactId = useId()
  const uid = reactId.replace(/[^a-zA-Z0-9]/g, '')
  const gradientId = `spark-grad-${uid}`
  const [hover, setHover] = useState<number | null>(null)

  const dotR = 3
  const strokeW = 2
  const pad = dotR + strokeW

  const clean = useMemo(() => data.filter((v) => Number.isFinite(v)), [data])
  const points = useMemo(() => layout(clean, width, height, pad), [clean, width, height, pad])
  const linePath = useMemo(() => smoothPath(points), [points])
  const fillPath = useMemo(() => areaPath(linePath, points, height - pad), [linePath, points, height, pad])

  const lastValue = clean.length ? clean[clean.length - 1] : null
  const lastPoint = points.length ? points[points.length - 1] : null
  const activeIdx = interactive ? hover : null
  const activePoint = activeIdx !== null ? points[activeIdx] : null
  const activeValue = activeIdx !== null ? clean[activeIdx] : null

  const formattedLast = fmtValue(lastValue, precision)
  const summary = clean.length === 0
    ? `${label ? label + ': ' : ''}no data`
    : `${label ? label + ': ' : ''}${withUnit(formattedLast, unit)} — last ${clean.length} sample${clean.length === 1 ? '' : 's'}`

  const handleMove = (e: SvgPointerEvent<SVGSVGElement>) => {
    if (!interactive || points.length === 0) return
    const ctm = e.currentTarget.getScreenCTM()
    if (!ctm) return
    const svgPt = e.currentTarget.createSVGPoint()
    svgPt.x = e.clientX
    svgPt.y = e.clientY
    const loc = svgPt.matrixTransform(ctm.inverse())
    let nearest = 0
    let best = Infinity
    for (let i = 0; i < points.length; i++) {
      const d = Math.abs(points[i].x - loc.x)
      if (d < best) { best = d; nearest = i }
    }
    setHover(nearest)
  }
  const handleLeave = () => setHover(null)
  const handleFocus = () => { if (interactive && points.length) setHover(points.length - 1) }

  // Tooltip for the active (hovered/focused) sample: clamped inside the
  // viewBox horizontally, flipped below the point when too close to the top.
  let tooltip: { x: number; y: number; w: number; h: number; text: string } | null = null
  if (activePoint && activeValue !== null) {
    const text = withUnit(fmtValue(activeValue, precision), unit)
    const w = Math.max(30, text.length * 6.5 + 12)
    const h = 18
    const above = activePoint.y - dotR - 8 - h
    const y = above < -2 ? activePoint.y + dotR + 8 : above
    const x = clamp(activePoint.x - w / 2, 1, width - w - 1)
    tooltip = { x: round2(x), y: round2(y), w: round2(w), h, text }
  }

  return (
    <div
      className={['gf-spark', className].filter(Boolean).join(' ')}
      style={{ display: 'inline-flex', flexDirection: 'column', gap: 4, minWidth: 0, ...style }}
      data-testid={testid}
    >
      {(label || showValue) && (
        <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 10 }}>
          {label && (
            <span style={{
              fontSize: 12, fontWeight: 700, letterSpacing: '.6px', textTransform: 'uppercase',
              color: 'var(--text-dim, #8a93a6)',
            }}>
              {label}
            </span>
          )}
          {showValue && (
            <span style={{
              fontSize: 15, fontWeight: 700, color: 'var(--text, #e6e9f0)',
              fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap',
            }}>
              {formattedLast}
              {unit && (
                <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--text-dim, #8a93a6)', marginLeft: 3 }}>{unit}</span>
              )}
            </span>
          )}
        </div>
      )}

      <svg
        width={width}
        height={height}
        viewBox={`0 0 ${width} ${height}`}
        preserveAspectRatio="xMidYMid meet"
        role="img"
        aria-label={ariaLabel ?? summary}
        tabIndex={interactive ? 0 : -1}
        onPointerMove={handleMove}
        onPointerLeave={handleLeave}
        onFocus={handleFocus}
        onBlur={handleLeave}
        style={{
          display: 'block', overflow: 'visible',
          cursor: interactive && points.length > 1 ? 'crosshair' : 'default',
        }}
      >
        <title>{summary}</title>

        {points.length === 0 ? (
          <line x1={pad} y1={height / 2} x2={width - pad} y2={height / 2} stroke="var(--border, #232b3d)" strokeWidth={1} />
        ) : (
          <>
            <defs>
              <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor={color} stopOpacity={0.28} />
                <stop offset="100%" stopColor={color} stopOpacity={0} />
              </linearGradient>
            </defs>

            {points.length > 1 && <path d={fillPath} fill={`url(#${gradientId})`} stroke="none" />}
            {points.length > 1 && (
              <path d={linePath} fill="none" stroke={color} strokeWidth={strokeW} strokeLinecap="round" strokeLinejoin="round" />
            )}

            {/* Last value — always highlighted, the headline reading. */}
            {lastPoint && (
              <>
                <circle cx={lastPoint.x} cy={lastPoint.y} r={dotR + 2} fill={surface} />
                <circle cx={lastPoint.x} cy={lastPoint.y} r={dotR} fill={color} />
              </>
            )}

            {/* Hover/focus crosshair — enhances, never gates: the headline
                value above is always visible without touching the chart. */}
            {activePoint && (
              <>
                <line x1={activePoint.x} y1={0} x2={activePoint.x} y2={height} stroke="var(--border, #232b3d)" strokeWidth={1} opacity={0.7} />
                <circle cx={activePoint.x} cy={activePoint.y} r={dotR + 2} fill={surface} />
                <circle cx={activePoint.x} cy={activePoint.y} r={dotR} fill={color} />
              </>
            )}
          </>
        )}

        {tooltip && (
          <g pointerEvents="none">
            <rect x={tooltip.x} y={tooltip.y} width={tooltip.w} height={tooltip.h} rx={5}
              fill={surface} stroke="var(--border, #232b3d)" strokeWidth={1} />
            <text
              x={tooltip.x + tooltip.w / 2} y={tooltip.y + tooltip.h / 2 + 4} textAnchor="middle"
              fontSize={11} fontWeight={700} fill="var(--text, #e6e9f0)"
              style={{ fontVariantNumeric: 'tabular-nums' }}
            >
              {tooltip.text}
            </text>
          </g>
        )}
      </svg>
    </div>
  )
}

/**
 * Accumulates the last `max` values of a number that changes over time — e.g.
 * a live telemetry field polled on an interval. A sample is recorded whenever
 * `value` changes (React's own dependency comparison); a run of identical
 * consecutive readings is stored once, not once per tick. Non-finite values
 * (NaN/±Infinity — e.g. while `tele` is still null) are ignored, so a
 * temporary disconnect just pauses the trend instead of corrupting it.
 *
 *   const cpuHistory = useHistory(tele?.cpuTempC ?? NaN, 60)
 */
export function useHistory(value: number, max = 60): number[] {
  const [history, setHistory] = useState<number[]>(() => (Number.isFinite(value) ? [value] : []))

  useEffect(() => {
    if (!Number.isFinite(value)) return
    setHistory((prev) => {
      const next = prev.length >= max ? prev.slice(prev.length - max + 1) : prev.slice()
      next.push(value)
      return next
    })
  }, [value, max])

  return useMemo(() => (history.length > max ? history.slice(history.length - max) : history), [history, max])
}
