cmake_minimum_required(VERSION 3.20)
project(fwserver C)

set(REPO_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../__repo__)

# wolfSSL options — disable tests/examples for faster build
set(WOLFSSL_EXAMPLES    "no" CACHE STRING "")
set(WOLFSSL_CRYPT_TESTS "no" CACHE STRING "")

# wolfMQTT options — static lib, TLS on by default via wolfSSL tree
set(BUILD_SHARED_LIBS   OFF  CACHE BOOL   "Build static library")
set(WOLFMQTT_EXAMPLES   "no" CACHE STRING "Do not build wolfMQTT examples")
set(WOLFMQTT_TLS        "yes" CACHE STRING "Enable TLS support")
set(WITH_WOLFSSL_TREE   ${REPO_DIR}/modules/crypto/wolfssl
    CACHE PATH "Path to wolfSSL source tree")

add_subdirectory(
    ${REPO_DIR}/modules/lib/wolfmqtt
    ${CMAKE_CURRENT_BINARY_DIR}/wolfmqtt_build
)

add_executable(fwserver
    fwserver.c
    mqttexample.c
    mqttnet.c
    mqttport.c
)

# wolfmqtt exports wolfmqtt + wolfssl include dirs transitively.
# Add fwserver dir for local headers (firmware.h, fwserver.h, mqttexample.h,
# mqttnet.h, mqttport.h).
target_include_directories(fwserver PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})

target_link_libraries(fwserver PRIVATE wolfmqtt)
