/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    css: true,
    setupFiles: ['./tests/unit/setup.ts'],
    // coding-guidelines.md: "co-locate a component with its styles and
    // tests" — S-010 onward puts new component tests next to the component
    // under src/, while tests/unit/ keeps the S-002 health-check test.
    include: ['tests/unit/**/*.test.{ts,tsx}', 'src/**/*.test.{ts,tsx}'],
    // CI additionally gets a JUnit file so ci.yml can publish a Checks-tab
    // test report (dorny/test-reporter) — local runs keep just the default
    // console reporter, no extra file clutter.
    reporters: process.env.CI ? ['default', 'junit'] : ['default'],
    outputFile: process.env.CI ? { junit: './test-results/vitest-junit.xml' } : undefined,
  },
})
