/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    setupFiles: ['./tests/unit/setup.ts'],
    include: ['tests/unit/**/*.test.{ts,tsx}'],
    // CI additionally gets a JUnit file so ci.yml can publish a Checks-tab
    // test report (dorny/test-reporter) — local runs keep just the default
    // console reporter, no extra file clutter.
    reporters: process.env.CI ? ['default', 'junit'] : ['default'],
    outputFile: process.env.CI ? { junit: './test-results/vitest-junit.xml' } : undefined,
  },
})
