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
    resolve: {
        alias: {
            "@": path.resolve(__dirname, "./src"),
        },
    },
})
