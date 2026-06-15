cmake_minimum_required(VERSION 3.20.0)

# ── wolfBoot module path ───────────────────────────────────────────────────────
if(NOT "$ENV{ZEPHYR_BASE}" STREQUAL "")
    set(_wb_zbase "$ENV{ZEPHYR_BASE}")
elseif(NOT "${ZEPHYR_BASE}" STREQUAL "")
    set(_wb_zbase "${ZEPHYR_BASE}")
else()
    message(FATAL_ERROR
        "ZEPHYR_BASE is not set. "
        "Run cmake with --preset debug or export ZEPHYR_BASE in the environment.")
endif()
get_filename_component(WOLFBOOT_MODULE_DIR
    "${_wb_zbase}/../modules/bootloader/wolfboot" ABSOLUTE)
get_filename_component(_ZEPHYRPROJECT_DIR "${_wb_zbase}/.." ABSOLUTE)

set(_MCUX_ROOT    "${_ZEPHYRPROJECT_DIR}/modules/hal/nxp/mcux/mcux-sdk-ng")
set(_MCUX_CMSIS   "${_ZEPHYRPROJECT_DIR}/modules/hal/cmsis_6/CMSIS")
set(_MCUX_DRV     "${_MCUX_ROOT}/devices/MCX/MCXN/MCXN947")
set(_MCUX_TMPL    "${CMAKE_SOURCE_DIR}/wolfbootConfig")
set(_WOLFSSL_ROOT "${_ZEPHYRPROJECT_DIR}/modules/crypto/wolfssl")

# ── Apply wolfBoot board patch (idempotent, configure time) ───────────────────
set(_WB_PATCH "${CMAKE_SOURCE_DIR}/wolfbootConfig/0001-Update-configs-and-memory-map.patch")
execute_process(
    COMMAND git apply --check --reverse "${_WB_PATCH}"
    WORKING_DIRECTORY "${WOLFBOOT_MODULE_DIR}"
    RESULT_VARIABLE _patch_applied
    OUTPUT_QUIET ERROR_QUIET
)
if(NOT _patch_applied EQUAL 0)
    execute_process(
        COMMAND git apply "${_WB_PATCH}"
        WORKING_DIRECTORY "${WOLFBOOT_MODULE_DIR}"
        RESULT_VARIABLE _patch_result
        ERROR_VARIABLE  _patch_error
    )
    if(NOT _patch_result EQUAL 0)
        message(FATAL_ERROR
            "Failed to apply wolfBoot patch\n"
            "  patch:   ${_WB_PATCH}\n"
            "  workdir: ${WOLFBOOT_MODULE_DIR}\n"
            "  git output: ${_patch_error}")
    endif()
endif()

# ── wolfBoot binary build (configure time, before Zephyr) ─────────────────────
# CORTEX_M33=1 must be on the command line so the ifeq in arch.mk (~line 357)
# is evaluated true during Makefile parse. arch.mk sets CORTEX_M33=1 for mcxn
# only at ~line 834, which is too late for the parse-time ifeq. Without this,
# -mcmse is never added to CFLAGS and --cmse-implib is never added to
# SECURE_LDFLAGS, so libwolfboot.o lacks SG stubs and wolfboot_tz_nsc.o is
# never emitted by the linker.
# If wolfboot_tz_nsc.o is absent, remove libwolfboot.o (the only object with
# CMSE entry functions) and the stale elf/bin so make recompiles and relinks.
if(NOT EXISTS "${WOLFBOOT_MODULE_DIR}/src/wolfboot_tz_nsc.o")
    file(REMOVE
        "${WOLFBOOT_MODULE_DIR}/src/libwolfboot.o"
        "${WOLFBOOT_MODULE_DIR}/wolfboot.elf"
        "${WOLFBOOT_MODULE_DIR}/wolfboot.bin"
    )
endif()
execute_process(
    COMMAND make wolfboot.bin keytools tools/bin-assemble/bin-assemble
        WOLFBOOT_ROOT=${WOLFBOOT_MODULE_DIR}
        TARGET=mcxn ARCH=ARM TZEN=1 SIGN=ECC384 HASH=SHA384
        CORTEX_M33=1
        MCUXSDK=1
        MCUXPRESSO=${_MCUX_ROOT}
        MCUXPRESSO_CMSIS=${_MCUX_CMSIS}
        MCUXPRESSO_CPU=MCXN947VDF_cm33_core0
        MCUXPRESSO_DRIVERS=${_MCUX_DRV}
        MCUXPRESSO_PROJECT_TEMPLATE=${_MCUX_TMPL}
        WOLFBOOT_LIB_WOLFSSL=${_WOLFSSL_ROOT}
        NO_MPU=1 NVM_FLASH_WRITEONCE=1 SPMATH=1 RAM_CODE=1 PKA=1 DEBUG_UART=1
        NO_ARM_ASM=1 WOLFBOOT_VERSION=0
        WOLFBOOT_SECTOR_SIZE=0x2000
        WOLFBOOT_KEYVAULT_ADDRESS=0x1000D000 WOLFBOOT_KEYVAULT_SIZE=0
        WOLFBOOT_NSC_ADDRESS=0x1000E000 WOLFBOOT_NSC_SIZE=0x1000
        WOLFBOOT_PARTITION_SIZE=0xC0000
        WOLFBOOT_PARTITION_BOOT_ADDRESS=0x10000
        WOLFBOOT_PARTITION_UPDATE_ADDRESS=0xD0000
        WOLFBOOT_PARTITION_SWAP_ADDRESS=0x190000
        IMAGE_HEADER_SIZE=1024
    WORKING_DIRECTORY "${WOLFBOOT_MODULE_DIR}"
    RESULT_VARIABLE _wb_result
)
if(NOT _wb_result EQUAL 0)
    message(FATAL_ERROR "wolfBoot make failed")
endif()

file(MAKE_DIRECTORY "${CMAKE_BINARY_DIR}/zephyr")
file(COPY "${WOLFBOOT_MODULE_DIR}/wolfboot.bin"
          "${WOLFBOOT_MODULE_DIR}/wolfboot.elf"
     DESTINATION "${CMAKE_BINARY_DIR}/zephyr")

# ── Default board + overlay so no CMakePresets.json edit is required ───────────
# This application runs in the non-secure world and is booted by wolfBoot in the
# secure world: it links the wolfBoot NSC veneer (wolfboot_tz_nsc.o), defines
# TZEN, and uses system_init_ns.c. On FRDM-MCXN947 it must be built for the /ns
# board variant -- a secure (non-/ns) build still links and signs, but wolfBoot
# hands off to the wrong memory map / vector table and the app faults at boot
# (garbled UART, never reaches the Zephyr banner). Default board and overlay here
# so the stock build needs no preset edit; an explicit override is still honored
# for porting to other boards.
set(_DEFAULT_BOARD "frdm_mcxn947/mcxn947/cpu0/ns")
set(_SECURE_BOARD  "frdm_mcxn947/mcxn947/cpu0")  # non-bootable secure variant pinned by mistake
if(NOT DEFINED BOARD OR "${BOARD}" STREQUAL "")
    set(BOARD "${_DEFAULT_BOARD}" CACHE STRING "Target board" FORCE)
elseif("${BOARD}" STREQUAL "${_SECURE_BOARD}")
    message(WARNING
        "BOARD='${BOARD}' is the secure variant and will not boot under wolfBoot; "
        "forcing '${_DEFAULT_BOARD}'. (Delete the build dir if it was already "
        "configured for the secure board.)")
    set(BOARD "${_DEFAULT_BOARD}" CACHE STRING "Target board" FORCE)
elseif(NOT "${BOARD}" STREQUAL "${_DEFAULT_BOARD}")
    message(WARNING
        "BOARD='${BOARD}' overrides the default '${_DEFAULT_BOARD}'. On "
        "FRDM-MCXN947 the app is non-secure-world only and a non-/ns build will "
        "not boot under wolfBoot; ignore this if you are porting to another board.")
endif()
if(NOT DEFINED DTC_OVERLAY_FILE OR "${DTC_OVERLAY_FILE}" STREQUAL "")
    set(DTC_OVERLAY_FILE "${CMAKE_SOURCE_DIR}/app.overlay"
        CACHE STRING "Devicetree overlay" FORCE)
endif()

# ── Normal Zephyr app build ───────────────────────────────────────────────────
find_package(Zephyr REQUIRED HINTS $ENV{ZEPHYR_BASE})
project(wolfssl-ota-client)

if(NOT EXISTS "${CMAKE_SOURCE_DIR}/__repo__")
    execute_process(
        COMMAND ${CMAKE_COMMAND} -E create_symlink
            $ENV{ZEPHYR_BASE}/.. ${CMAKE_SOURCE_DIR}/__repo__
        COMMAND_ECHO STDOUT)
endif()

target_sources(app PRIVATE src/main.c)
target_sources(app PRIVATE src/system_init_ns.c)
target_sources(app PRIVATE __repo__/modules/crypto/wolfssl/wolfcrypt/test/test.c)
target_sources(app PRIVATE __repo__/modules/crypto/wolfssl/wolfcrypt/benchmark/benchmark.c)
target_include_directories(app PRIVATE src)

target_sources(app PRIVATE src/mqttClient/fwclient.c)
target_sources(app PRIVATE src/mqttClient/mqttexample.c)
target_sources(app PRIVATE src/mqttClient/mqttnet.c)
target_sources(app PRIVATE src/mqttClient/mqttport.c)
target_include_directories(app PRIVATE src/mqttClient)
target_compile_definitions(app PRIVATE WOLFMQTT_USER_SETTINGS)

add_definitions(-DWOLFSSL_USER_SETTINGS)

target_compile_definitions(app PRIVATE TZEN)
target_link_libraries(app PRIVATE "${WOLFBOOT_MODULE_DIR}/src/wolfboot_tz_nsc.o")
target_include_directories(app PRIVATE "${WOLFBOOT_MODULE_DIR}/include")

# ── Auto strip + sign after every build ───────────────────────────────────────
set(_SIGN_TOOL       "${WOLFBOOT_MODULE_DIR}/tools/keytools/sign")
set(_SIGN_KEY        "${WOLFBOOT_MODULE_DIR}/wolfboot_signing_private_key.der")
set(_ZEPHYR_ELF      "${CMAKE_BINARY_DIR}/zephyr/zephyr.elf")
set(_ZEPHYR_BIN      "${CMAKE_BINARY_DIR}/zephyr/zephyr.bin")
set(_ZEPHYR_STRIPPED "${CMAKE_BINARY_DIR}/zephyr/zephyr_stripped.bin")

add_custom_target(sign_images ALL
    COMMENT "Strip header and sign wolfBoot images (v1, v2)"
    COMMAND ${CMAKE_OBJCOPY}
        --gap-fill 0xFF --output-target=binary
        --remove-section=.comment --remove-section=COMMON
        "${_ZEPHYR_ELF}" "${_ZEPHYR_BIN}"
    COMMAND dd "if=${_ZEPHYR_BIN}" "of=${_ZEPHYR_STRIPPED}" bs=1 skip=1024
    COMMAND ${CMAKE_COMMAND} -E env "IMAGE_HEADER_SIZE=1024"
        "${_SIGN_TOOL}" --ecc384 --sha384 "${_ZEPHYR_STRIPPED}" "${_SIGN_KEY}" 1
    COMMAND ${CMAKE_COMMAND} -E env "IMAGE_HEADER_SIZE=1024"
        "${_SIGN_TOOL}" --ecc384 --sha384 "${_ZEPHYR_STRIPPED}" "${_SIGN_KEY}" 2
    VERBATIM
)
add_dependencies(sign_images zephyr_final)

# ── Factory image (wolfboot + signed app) ─────────────────────────────────────
set(_BIN_ASSEMBLE "${WOLFBOOT_MODULE_DIR}/tools/bin-assemble/bin-assemble")
set(_FACTORY_BIN  "${CMAKE_BINARY_DIR}/zephyr/factory.bin")

add_custom_target(factory_bin ALL
    COMMENT "Assemble factory image: wolfboot at 0x0, signed app at 0x10000"
    COMMAND "${_BIN_ASSEMBLE}"
        "${_FACTORY_BIN}"
        0x0     "${CMAKE_BINARY_DIR}/zephyr/wolfboot.bin"
        0x10000 "${CMAKE_BINARY_DIR}/zephyr/zephyr_stripped_v1_signed.bin"
    VERBATIM
)
add_dependencies(factory_bin sign_images)
