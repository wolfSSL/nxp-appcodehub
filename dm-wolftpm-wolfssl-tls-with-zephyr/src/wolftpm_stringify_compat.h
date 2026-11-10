#ifndef WOLFTPM_STRINGIFY_COMPAT_H_
#define WOLFTPM_STRINGIFY_COMPAT_H_

/* Pre-defines XSTRINGIFY so wolfTPM's tpm2_types.h keeps Zephyr's STRINGIFY. */
#if defined(__ZEPHYR__)
#include <zephyr/toolchain.h>
#ifndef XSTRINGIFY
#define XSTRINGIFY(s) STRINGIFY(s)
#endif
#endif

#endif /* WOLFTPM_STRINGIFY_COMPAT_H_ */
