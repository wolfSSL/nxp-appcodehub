#ifndef WOLFBOOT_STATUS_H
#define WOLFBOOT_STATUS_H

#include <stdint.h>

typedef enum {
    BOOT_STATE_NORMAL,             /* BOOT state SUCCESS, running confirmed image */
    BOOT_STATE_AWAITING_CONFIRM,   /* BOOT state TESTING, app must call success() */
    BOOT_STATE_VERIFY_FAILED,      /* UPDATE state UPDATING + UPDATE>BOOT (sig/integrity) */
    BOOT_STATE_DOWNGRADE_REJECTED, /* UPDATE state UPDATING + UPDATE<BOOT */
    BOOT_STATE_ROLLED_BACK,        /* BOOT state unreadable, UPDATE>BOOT (post-rollback) */
    BOOT_STATE_NO_UPDATE,          /* fresh / no pending update */
    BOOT_STATE_UNKNOWN
} boot_state_t;

static inline const char *boot_state_name(boot_state_t s) {
    switch (s) {
        case BOOT_STATE_NORMAL:             return "NORMAL";
        case BOOT_STATE_AWAITING_CONFIRM:   return "AWAITING_CONFIRM";
        case BOOT_STATE_VERIFY_FAILED:      return "VERIFY_FAILED";
        case BOOT_STATE_DOWNGRADE_REJECTED: return "DOWNGRADE_REJECTED";
        case BOOT_STATE_ROLLED_BACK:        return "ROLLED_BACK";
        case BOOT_STATE_NO_UPDATE:          return "NO_UPDATE";
        default:                            return "UNKNOWN";
    }
}

/* Classify combined state from BOTH partitions:
 *  boot_state  = wolfBoot_get_partition_state(PART_BOOT, ...)
 *  update_state= wolfBoot_get_partition_state(PART_UPDATE, ...)
 * BOOT trailer holds the running image's confirm state (TESTING/SUCCESS).
 * UPDATE trailer holds pending-swap diagnostic (UPDATING when interrupted).
 */
static inline boot_state_t boot_state_classify(
    uint32_t boot_v,  uint32_t update_v,
    uint8_t  boot_state, int boot_rc,
    uint8_t  update_state, int update_rc)
{
    /* 1. BOOT partition state - reveals confirm lifecycle */
    if (boot_rc == 0) {
        if (boot_state == 0x10) return BOOT_STATE_AWAITING_CONFIRM; /* TESTING */
        if (boot_state == 0x00) return BOOT_STATE_NORMAL;           /* SUCCESS */
    }

    /* 2. UPDATE partition UPDATING means swap was started but did not complete */
    if (update_rc == 0 && update_state == 0x70) {
        if (update_v > boot_v) return BOOT_STATE_VERIFY_FAILED;
        if (update_v < boot_v) return BOOT_STATE_DOWNGRADE_REJECTED;
        return BOOT_STATE_UNKNOWN;
    }

    /* 3. No conclusive state info: post-rollback or no update at all */
    if (update_v > boot_v) return BOOT_STATE_ROLLED_BACK;
    return BOOT_STATE_NO_UPDATE;
}

/* Shared globals: filled by main(), read by fwclient publisher */
extern volatile boot_state_t g_boot_state;
extern volatile uint32_t     g_boot_version;
extern volatile uint32_t     g_update_version;
extern volatile uint8_t      g_boot_state_byte;
extern volatile int          g_boot_state_rc;
extern volatile uint8_t      g_update_state_byte;
extern volatile int          g_update_state_rc;

#endif /* WOLFBOOT_STATUS_H */
