import { defineConfig } from 'vite';
import preact from '@preact/preset-vite';
import { viteSingleFile } from 'vite-plugin-singlefile';

// Everything is inlined into one HTML file so the plugin can serve it as a single
// embedded resource, and so Plugin Pages can drop it straight into its container
// without any asset-path rewriting.
export default defineConfig({
  plugins: [preact(), viteSingleFile()],
  build: {
    outDir: '../src/Jellyfin.Plugin.TagStudio/Web',
    emptyOutDir: false,
    assetsInlineLimit: 100000000,
    cssCodeSplit: false,
    reportCompressedSize: false,
    rollupOptions: {
      output: { inlineDynamicImports: true }
    }
  }
});
