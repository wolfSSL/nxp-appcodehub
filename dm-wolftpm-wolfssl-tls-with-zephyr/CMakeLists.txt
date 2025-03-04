cmake_minimum_required(VERSION 3.20.0)

#Devicetree Overlays (Must be above find_package call)
# To support SLB9673 click module
set(DTC_OVERLAY_FILE "deviceTreeOverlays/mikroBUS.overlay")
find_package(Zephyr REQUIRED HINTS $ENV{ZEPHYR_BASE})
project(wolfssl-tls-hello-server)

# Check if the symbolic link __repo__ exists
if(NOT EXISTS "${CMAKE_SOURCE_DIR}/__repo__")
    # Create a symbolic link from __repo__ to the ZEPHYR_BASE environment variable
    execute_process(
        COMMAND ${CMAKE_COMMAND} -E create_symlink $ENV{ZEPHYR_BASE}/.. ${CMAKE_SOURCE_DIR}/__repo__
        COMMAND_ECHO STDOUT
    )
endif()

target_sources(app PRIVATE src/main.c)
target_sources(app PRIVATE src/mikroBUS.c)
target_sources(app PRIVATE src/pmodInit.c)
target_sources(app PRIVATE __repo__/modules/lib/wolftpm/examples/csr/csr.c)
target_sources(app PRIVATE __repo__/modules/lib/wolftpm/examples/bench/bench.c)
target_sources(app PRIVATE __repo__/modules/lib/wolftpm/examples/tpm_test_keys.c)
target_sources(app PRIVATE __repo__/modules/lib/wolftpm/examples/wrap/caps.c)
target_sources(app PRIVATE __repo__/modules/lib/wolftpm/examples/wrap/wrap_test.c)
target_sources(app PRIVATE __repo__/modules/lib/wolftpm/examples/native/native_test.c)
target_sources(app PRIVATE __repo__/modules/lib/wolftpm/examples/keygen/create_primary.c)
target_sources(app PRIVATE __repo__/modules/lib/wolftpm/examples/keygen/external_import.c)
target_sources(app PRIVATE __repo__/modules/lib/wolftpm/examples/keygen/keygen.c)
target_sources(app PRIVATE __repo__/modules/lib/wolftpm/examples/keygen/keyimport.c)
target_sources(app PRIVATE __repo__/modules/lib/wolftpm/examples/keygen/keyload.c)
target_sources(app PRIVATE __repo__/modules/lib/wolftpm/examples/management/tpmclear.c)
target_sources(app PRIVATE __repo__/modules/lib/wolftpm/examples/management/flush.c)

target_include_directories(app PRIVATE src)
target_include_directories(app PRIVATE __repo__/modules/lib/wolftpm/examples/wrap)
target_include_directories(app PRIVATE __repo__/modules/lib/wolftpm/examples/csr)
target_include_directories(app PRIVATE __repo__/modules/lib/wolftpm/examples/bench)
target_include_directories(app PRIVATE __repo__/modules/lib/wolftpm/examples)
target_include_directories(app PRIVATE __repo__/modules/lib/wolftpm/examples/native)
target_include_directories(app PRIVATE __repo__/modules/lib/wolftpm/examples/keygen)
target_include_directories(app PRIVATE __repo__/modules/lib/wolftpm/examples/management)
add_definitions(-DWOLFSSL_USER_SETTINGS)
add_definitions(-DWOLFTPM_USER_SETTINGS)


# Check if the command is executed by redirecting output to a file (Unix-like systems)
# add_custom_command(
#     TARGET app POST_BUILD
#     COMMAND cd ../__repo__/modules/crypto/wolfssl && ./autogen.sh && ./configure && make
#     COMMENT "Echoing HELLO_WORLD after building the app target"
# )