/*
 * Early SoC initialization for Non-Secure state on MCXN947.
 *
 * soc_reset_hook() in Zephyr's soc.c skips SystemInit() when
 * CONFIG_TRUSTED_EXECUTION_NONSECURE is set, so these initializations
 * are placed here via soc_early_init_hook() instead.
 */

#include <zephyr/platform/hooks.h>
#include "fsl_device_registers.h"

void soc_early_init_hook(void)
{
	/* Enable coprocessor access in Non-Secure state */
#if ((__FPU_PRESENT == 1) && (__FPU_USED == 1))
	SCB->CPACR |= ((3UL << 10*2) | (3UL << 11*2));    /* CP10, CP11 for FPU */
#endif
	SCB->CPACR |= ((3UL << 0*2) | (3UL << 1*2));      /* CP0, CP1 for PowerQuad */

	/* Disable RAM ECC to maximize available memory for Ethernet and app buffers */
	SYSCON->ECC_ENABLE_CTRL = 0;
	SYSCON->NVM_CTRL |= SYSCON_NVM_CTRL_DIS_MBECC_ERR_DATA_MASK;

	/* Disable flash cache */
	SYSCON->NVM_CTRL |= SYSCON_NVM_CTRL_DIS_FLASH_CACHE_MASK;

	/* Disable aGDET interrupt and reset */
	SPC0->ACTIVE_CFG |= SPC_ACTIVE_CFG_GLITCH_DETECT_DISABLE_MASK;
	SPC0->GLITCH_DETECT_SC &= ~SPC_GLITCH_DETECT_SC_LOCK_MASK;
	SPC0->GLITCH_DETECT_SC = 0x3C;
	SPC0->GLITCH_DETECT_SC |= SPC_GLITCH_DETECT_SC_LOCK_MASK;
}
