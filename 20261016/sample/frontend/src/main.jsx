import React from 'react'
import { createRoot } from 'react-dom/client'
import Procore from './Procore.jsx' // Procore 風格主畫面（接後端 API）

createRoot(document.getElementById('root')).render(<Procore />)
