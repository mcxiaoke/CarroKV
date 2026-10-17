package ckv

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Test

// Guards the cross-language default matrix documented in
// `KV-DB-DESIGN-SPEC.md` §5.1.1.1: every implementation must ship the same
// conservative, durability-first defaults. A change here is an ADR-level
// decision and must be applied to all five languages in one batch.
class DefaultOptionsTest {
    @Test
    fun testDefaultOptionsMatrix() {
        val o = DbOptions()

        assertFalse(o.readOnly)
        assertFalse(o.autoRecoverFromBackup) // D1
        assertEquals(SyncMode.IMMEDIATE, o.syncMode)
        assertEquals(100L, o.syncIntervalMs)
        assertEquals(StorageEngineMode.SNAPSHOT, o.engineMode)
        assertEquals(4L * 1024 * 1024, o.compactionThresholdBytes) // C8
        assertEquals(10000L, o.lockTimeoutMs) // C19
        assertFalse(o.strictCompaction) // D2
    }
}
