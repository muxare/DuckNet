import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

export default defineConfig({
  plugins: [vue()],
  base: "/",
  build: {
    outDir: "../wwwroot",
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      "/dashboard": "http://localhost:5152",
      "/stats": "http://localhost:5152",
      "/health": "http://localhost:5152",
    },
  },
});
