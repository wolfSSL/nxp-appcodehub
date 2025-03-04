#include <zephyr/kernel.h>
#include <zephyr/device.h>
#include <zephyr/devicetree.h>
#include <zephyr/drivers/clock_control.h>
#include <zephyr/drivers/gpio.h>
#include <fsl_clock.h>

static int pmod_sdhc_init(const struct device *dev)
{
#if DT_NODE_HAS_STATUS(DT_NODELABEL(flexcomm0), okay)
    /* Configure FLEXCOMM0 clock for SPI operation */
    CLOCK_SetClkDiv(kCLOCK_DivFlexcom0Clk, 2u);
    /* Use 12MHz FRO clock for reliable SPI operation */
    CLOCK_AttachClk(kFRO12M_to_FLEXCOMM0);
#endif
    return 0;
}

/* Initialize early in boot process */
SYS_INIT(pmod_sdhc_init, PRE_KERNEL_1,
         CONFIG_KERNEL_INIT_PRIORITY_DEFAULT);