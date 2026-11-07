import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // The dashboard calls relative URLs like /api/sites. In development, Vite
    // forwards those to the C# API (the "http" launch profile in Visual Studio),
    // so the browser only ever talks to one origin and no CORS is needed.
    // Override with e.g. `API_URL=http://localhost:5199 npm run dev`.
    proxy: {
      '/api': process.env.API_URL ?? 'http://localhost:5115',
    },
  },
})
