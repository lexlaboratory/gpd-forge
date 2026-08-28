// GPD Forge UI — shared atoms. GPL-3.0-or-later.
//
// These five names are imported ~52 times across the pages. Rather than rewrite every call site at
// once, the implementations moved to ./components and this file became the compatibility surface:
// `Tile` is a `Readout` without a bar, `Card` is a `Frame`. Call sites migrate to the richer
// components as each page is redesigned.
export { Frame as Card, Readout as Tile, Slider, Toggle, Soon } from './components'
