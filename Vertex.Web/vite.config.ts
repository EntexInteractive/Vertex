import react from '@vitejs/plugin-react'
import {defineConfig} from 'vite'

// https://vite.dev/config/
export default defineConfig({
    // Relative asset URLs work both under ASP.NET Core and Photino's local host.
    base: './',
    plugins: [react()],
})
