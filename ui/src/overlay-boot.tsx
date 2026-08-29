// GPD Forge — overlay entry point (separate bundle from the main UI). GPL-3.0-or-later.
import React from 'react'
import ReactDOM from 'react-dom/client'
import { OverlayApp } from './Overlay'
import { ToastProvider } from './Toast'
// Its own sheet, not the dashboard's: the overlay is a separate Vite entry and used to pull in the
// whole 24 KB main stylesheet to reach about sixty lines of `.qam-*` rules.
import './overlay.css'

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <ToastProvider>
      <OverlayApp />
    </ToastProvider>
  </React.StrictMode>,
)
