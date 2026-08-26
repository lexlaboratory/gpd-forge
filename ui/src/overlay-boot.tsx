// GPD Forge — overlay entry point (separate bundle from the main UI). GPL-3.0-or-later.
import React from 'react'
import ReactDOM from 'react-dom/client'
import { OverlayApp } from './Overlay'
import { ToastProvider } from './Toast'
import './styles.css'

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <ToastProvider>
      <OverlayApp />
    </ToastProvider>
  </React.StrictMode>,
)
