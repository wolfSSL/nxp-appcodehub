cmake_minimum_required(VERSION 3.20.0)

find_package(Zephyr REQUIRED HINTS $ENV{ZEPHYR_BASE})
project(wolfssh-rgb-server)

# Check if the symbolic link __repo__ exists
if(NOT EXISTS "${CMAKE_SOURCE_DIR}/__repo__")
    # Create a symbolic link from __repo__ to the ZEPHYR_BASE environment variable
    execute_process(
        COMMAND ${CMAKE_COMMAND} -E create_symlink $ENV{ZEPHYR_BASE}/.. ${CMAKE_SOURCE_DIR}/__repo__
        COMMAND_ECHO STDOUT
    )
endif()

target_sources(app PRIVATE src/main.c)
target_sources(app PRIVATE __repo__/modules/crypto/wolfssl/wolfcrypt/test/test.c)
target_sources(app PRIVATE __repo__/modules/crypto/wolfssl/wolfcrypt/benchmark/benchmark.c)
target_include_directories(app PRIVATE src)

add_definitions(-DWOLFSSH_USER_SETTINGS)
add_definitions(-DWOLFSSL_USER_SETTINGS)

# Force-include the send()/recv() -> zsock_* remap for wolfSSH's sources only.
set(_WSX_MODULE_DIR "$ENV{ZEPHYR_BASE}/../modules/lib/wolfssh")
get_filename_component(_WSX_MODULE_DIR "${_WSX_MODULE_DIR}" REALPATH)
if(IS_DIRECTORY "${_WSX_MODULE_DIR}/src")
    file(GLOB _WSX_SOURCES "${_WSX_MODULE_DIR}/src/*.c")
    set_source_files_properties(${_WSX_SOURCES} PROPERTIES COMPILE_OPTIONS
        "-include;${CMAKE_SOURCE_DIR}/src/zephyr_socket_names.h")
    message(STATUS "wolfSSH BSD socket-name remap enabled for "
                   "${_WSX_MODULE_DIR}/src (${CMAKE_SOURCE_DIR}/src/zephyr_socket_names.h)")
else()
    message(WARNING "wolfSSH module sources not found at ${_WSX_MODULE_DIR}/src; "
                    "the send()/recv() name remap was not applied")
endif()