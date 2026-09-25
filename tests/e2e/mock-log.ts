// GPD Forge — where the E2E mock daemon writes its request log. GPL-3.0-or-later.
// Shared by playwright.config.ts (which hands it to the mock as MOCK_LOG) and connection-churn.spec.ts
// (which reads it back), so the two cannot drift onto different files.
import { join } from 'node:path'

export const MOCK_LOG = join(__dirname, 'artifacts', 'mock-daemon.log')
