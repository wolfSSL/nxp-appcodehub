#include <zephyr/kernel.h>
#include <zephyr/device.h>
#include <zephyr/devicetree.h>
#include <zephyr/drivers/clock_control.h>
#include <fsl_clock.h>

static int flexcomm3_clock_init(const struct device *dev)
{

#if DT_NODE_HAS_STATUS(DT_NODELABEL(flexcomm3), okay)
	CLOCK_SetClkDiv(kCLOCK_DivFlexcom3Clk, 1u);
	CLOCK_AttachClk(kFRO12M_to_FLEXCOMM3);
#endif

}

SYS_INIT(flexcomm3_clock_init, PRE_KERNEL_1,
            CONFIG_KERNEL_INIT_PRIORITY_DEFAULT);