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
target_sources(app PRIVATE src/zephyr_compat.c)
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
target_sources(app PRIVATE __repo__/modules/lib/wolftpm/examples/tls/tls_server.c)
target_sources(app PRIVATE __repo__/modules/lib/wolftpm/examples/tls/tls_client.c)
target_sources(app PRIVATE __repo__/modules/crypto/wolfssl/wolfcrypt/test/test.c)
target_sources(app PRIVATE __repo__/modules/crypto/wolfssl/wolfcrypt/benchmark/benchmark.c)

target_include_directories(app PRIVATE src)
target_include_directories(app PRIVATE __repo__/modules/lib/wolftpm/examples/wrap)
target_include_directories(app PRIVATE __repo__/modules/lib/wolftpm/examples/csr)
target_include_directories(app PRIVATE __repo__/modules/lib/wolftpm/examples/bench)
target_include_directories(app PRIVATE __repo__/modules/lib/wolftpm/examples)
target_include_directories(app PRIVATE __repo__/modules/lib/wolftpm/examples/native)
target_include_directories(app PRIVATE __repo__/modules/lib/wolftpm/examples/keygen)
target_include_directories(app PRIVATE __repo__/modules/lib/wolftpm/examples/management)
target_include_directories(app PRIVATE __repo__/modules/lib/wolftpm/examples/tls)
target_include_directories(app PRIVATE __repo__/modules/crypto/wolfssl/wolfcrypt/test)
target_include_directories(app PRIVATE __repo__/modules/crypto/wolfssl/wolfcrypt/benchmark)
add_definitions(-DWOLFSSL_USER_SETTINGS)
add_definitions(-DWOLFTPM_USER_SETTINGS)

# Pre-define XSTRINGIFY so wolfTPM doesn't redefine Zephyr's STRINGIFY.
target_compile_options(app PRIVATE
    -include "${CMAKE_SOURCE_DIR}/src/wolftpm_stringify_compat.h")

# wolftpm's examples/tls/tls_common.h uses POSIX socket names (gethostbyname,
# struct hostent, fd_set, FD_ZERO/SET/ISSET, select, inet_addr) without
# including the POSIX headers. Pre-Zephyr 3.1 CONFIG_NET_SOCKETS_POSIX_NAMES
# globally aliased these inside <zephyr/net/socket.h>, but that Kconfig was
# removed in Zephyr v4.x. Force-include the POSIX headers for the TLS
# example TUs so CONFIG_POSIX_API=y resolves the symbols via Zephyr's POSIX
# wrappers. Remove once the upstream wolftpm headers include these directly.
set_source_files_properties(
    __repo__/modules/lib/wolftpm/examples/tls/tls_server.c
    __repo__/modules/lib/wolftpm/examples/tls/tls_client.c
    PROPERTIES COMPILE_OPTIONS
    "-include;netdb.h;-include;arpa/inet.h;-include;sys/select.h;-include;${CMAKE_SOURCE_DIR}/src/zephyr_compat.h"
)


# Check if the command is executed by redirecting output to a file (Unix-like systems)
# add_custom_command(
#     TARGET app POST_BUILD
#     COMMAND cd ../__repo__/modules/crypto/wolfssl && ./autogen.sh && ./configure && make
#     COMMENT "Echoing HELLO_WORLD after building the app target"
# )