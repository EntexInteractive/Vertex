import react from '@vitejs/plugin-react'
import {defineConfig} from 'vite'
import tailwindcss from "@tailwindcss/vite";
import path from "node:path";

// https://vite.dev/config/
export default defineConfig({
    // Relative asset URLs work both under ASP.NET Core and Photino's local host.
    base: './',
    plugins: [
        react(),
        tailwindcss()
    ],
    // `npm run dev` for the desktop app: `dotnet run --project Vertex.Gui -- -debug` loads this server and listens for
    // /api on 127.0.0.1:5180 (VX_GUI_PORT). changeOrigin makes the Host header match what the host accepts.
    server: {
        port: 5173,
        strictPort: true,
        proxy: {
            '/api': {target: `http://127.0.0.1:${process.env.VX_GUI_PORT ?? 5180}`, changeOrigin: true},
        },
    },
    resolve: {
        alias: {
            "@": path.resolve(__dirname, "./src"),
        },
    },
})
