/* main.c
 *
 * Copyright (C) 2006-2024 wolfSSL Inc.
 *
 * This file is part of wolfSSL.
 *
 * wolfSSL is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 *
 * wolfSSL is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA 02110-1335, USA
 */

/* wolfSSL Includes Start */
/* POSIX socket prototypes come from Zephyr; include them before wolfSSL. */
#include <sys/socket.h>
#include <unistd.h>

#include "user_settings.h"      /* For wolfSSL Zephyr configuration */
#include <wolfssl/wolfcrypt/settings.h>
#include <wolfssl/ssl.h>        /* Basic functionality for TLS */
#include <wolfssl/certs_test.h> /* Needed for Cert Buffers */
#include <wolfssl/wolfcrypt/hash.h>
#ifdef WOLFCRYPT_BENCHMARK
#include <wolfcrypt/benchmark/benchmark.h>
#endif

#ifdef WOLFCRYPT_TEST
#include <wolfcrypt/test/test.h>
#endif
/* wolfSSL Includes End */

/* Standard Packages Start */
#include <stdio.h>
#include <time.h>
/* Standard Packages End */

/* Zephyr Includes Start */
#include <zephyr/kernel.h>
#include <zephyr/drivers/gpio.h>
#include <zephyr/net/net_if.h>
#include <zephyr/net/dhcpv4.h>
#include <zephyr/net/net_core.h>
#include <zephyr/net/net_context.h>
#include <zephyr/net/net_mgmt.h>
#include <zephyr/net/net_config.h>
#include <zephyr/net/net_ip.h>
/* Zephyr Includes End */


#include "pem_data_mldsa44.h"

/* Program Defines Start */

#define DEFAULT_PORT 11111  /* Define the port we want to use for the network */
#define BUFFER_SIZE  256  /* Define the buffer size for file transfer */

/* Use DHCP auto IP assignment or static assignment */
#undef  DHCP_ON
#define DHCP_ON 1   /* Set to true (1) if you want auto assignment IP, */
                    /* set false (0) for statically defined. */
                    /* Make sure to avoid IP conflicts on the network you */
                    /* assign this to, check the defaults before using. */
                    /* If unsure, leave DHCP_ON set to 1 */
 
#if DHCP_ON == 0
/* Define Static IP, Gateway, and Netmask */
    #define STATIC_IPV4_ADDR  "1.1.1.5"
    #define STATIC_IPV4_GATEWAY "1.1.1.1"
    #define STATIC_IPV4_NETMASK "255.0.0.0"
#endif

/* Set the TLS Version. Currently only 2 or 3 is available for this */
/* application, defaults to TLSv3 */
#undef TLS_VERSION
#define TLS_VERSION 3

/* This sets up the correct function for the application via macros */
#undef TLS_METHOD
#if TLS_VERSION == 3
    #define TLS_METHOD wolfTLSv1_3_server_method()
#elif TLS_VERSION == 2
    #define TLS_METHOD wolfTLSv1_2_server_method()
#else 
    #define TLS_METHOD wolfTLSv1_3_server_method()
#endif

/* Set up the network using the Zephyr network stack */
int startNetwork() {

    struct net_if *iface = net_if_get_default();
    char buf[NET_IPV4_ADDR_LEN];

    #if DHCP_ON == 0
        struct in_addr addr, netmask, gw;
    #endif

    if (!(iface)) { /* See if a network interface (ethernet) is available */
        printf("No network interface determined");
        return 1;
    }

    if (net_if_flag_is_set(iface, NET_IF_DORMANT)) {
        printf("Waiting on network interface to be available");
        while(!net_if_is_up(iface)){
            k_sleep(K_MSEC(100));
        }
    }

    #if DHCP_ON == 1
        printf("\nStarting DHCP to obtain IP address\n");
        net_dhcpv4_start(iface);
        (void)net_mgmt_event_wait_on_iface(iface, NET_EVENT_IPV4_DHCP_BOUND, \
                                            NULL, NULL, NULL, K_FOREVER);
    #elif DHCP_ON == 0
        /* Static IP Configuration */
        if (net_addr_pton(AF_INET, STATIC_IPV4_ADDR, &addr) < 0 ||
            net_addr_pton(AF_INET, STATIC_IPV4_NETMASK, &netmask) < 0 ||
            net_addr_pton(AF_INET, STATIC_IPV4_GATEWAY, &gw) < 0) {
            printf("Invalid IP address settings.\n");
            return -1;
        }
        net_if_ipv4_set_netmask_by_addr(iface, &addr, &netmask);
        net_if_ipv4_set_gw(iface, &gw);
        net_if_ipv4_addr_add(iface, &addr, NET_ADDR_MANUAL, 0);

    #else
        #error "Please set DHCP_ON to true (1) or false (0), if unsure set to true (1)"
    #endif

    /* Display IP address that was assigned when done */
    printf("IP Address is: %s\n", net_addr_ntop(AF_INET, \
                    &iface->config.ip.ipv4->unicast[0].ipv4.address.in_addr, \
                    buf, sizeof(buf)));

    return 0;
}

#ifdef WOLFCRYPT_TEST
int runWolfcryptTest(void)
{
    int ret = 0;

    ret = wolfcrypt_test(NULL);
    printf("wolfCrypt Test returned %d.\n", ret);

    return ret;
}
#endif

#ifdef WOLFCRYPT_BENCHMARK
int runWolfcryptBenchmark(void)
{
    int ret = 0;

    ret = benchmark_test(NULL);
    printf("Benchmark Test returned %d.\n", ret);

    return ret;
}
#endif

/* Return 1 if the artifacts are in NVRAM, 0 otherwise */
static int artifacts_are_in_nvram() {
    /* TODO: Implement this. */
    return 0;
}

/* Append the data to the certificate or key in NVRAM. Return 0 on success. */
static int write_artifact_to_nvram(char *artifact, char *data, int size) {
    /* TODO: Implement this. */
    return 0;
}

static int receive_artifact(WOLFSSL* ssl, char *artifact)
{
    uint32_t artifact_size_network;
    uint32_t artifact_size;
    unsigned char buffer[BUFFER_SIZE];
    int bytes_received;
    uint32_t total_received = 0;
    int ret = 0;

    if ((ret = wolfSSL_read(ssl, &artifact_size_network, sizeof(artifact_size_network))) != sizeof(artifact_size_network)) {
        fprintf(stderr, "ERROR: Failed to receive size of  %s\n", artifact);
        return -1;
    }

    artifact_size = ntohl(artifact_size_network);
    printf("Receiving %s (size: %u bytes)\n", artifact, artifact_size);

    while (total_received < artifact_size) {
        uint32_t bytes_to_read = (artifact_size - total_received > BUFFER_SIZE) ? 
                                 BUFFER_SIZE : (artifact_size - total_received);
        
        bytes_received = wolfSSL_read(ssl, buffer, bytes_to_read);
        if (bytes_received <= 0) {
            fprintf(stderr, "ERROR: Failed to receive data for %s\n", artifact);
            return -1;
        }

        if (write_artifact_to_nvram(artifact, buffer, bytes_received) != 0) {
            fprintf(stderr, "ERROR: Failed to write %s to NVRAM\n", artifact);
            return -1;
        }

        total_received += bytes_received;
        printf("Received %d bytes (total: %u/%u)\n", bytes_received, total_received, artifact_size);

        if (wolfSSL_write(ssl, "ACK!", 4) != 4) {
            fprintf(stderr, "ERROR: Failed to send ack for %s\n", artifact);
            return -1;
        }
    }

    printf("Successfully received %s (%u bytes)\n", artifact, total_received);
    return 0;
}


/* Initialize Server for a client connection */
int startServer(void) {
    int                sockfd = SOCKET_INVALID;
    int                connd = SOCKET_INVALID;
    struct sockaddr_in servAddr;
    struct sockaddr_in clientAddr;
    socklen_t          size = sizeof(clientAddr);
    int                shutdown = 0;
    int                ret;

    WOLFSSL_CTX* ctx = NULL;
    WOLFSSL*     ssl = NULL;

    wolfSSL_Init();

    /* Create a socket that uses an internet IPv4 address,
     * Sets the socket to be stream based (TCP),
     * 0 means choose the default protocol. */
    if ((sockfd = socket(AF_INET, SOCK_STREAM, 0)) == -1) {
        printf("\nERROR: failed to create the socket\n");
        return 1;
    }

    ctx = wolfSSL_CTX_new(TLS_METHOD);
    if (ctx == NULL) {
        printf("\nERROR: Failed to create WOLFSSL_CTX\n");
        return 1;
    }

    if (wolfSSL_CTX_use_certificate_chain_buffer_format(ctx,
                cert_pem, sizeof(cert_pem),
                WOLFSSL_FILETYPE_PEM) != WOLFSSL_SUCCESS){
        printf("\nERROR: Cannot load server cert buffer\n");
        return 1; 
    }

    if (wolfSSL_CTX_use_PrivateKey_buffer(ctx,
            key_pem, sizeof(key_pem),
            SSL_FILETYPE_PEM) != WOLFSSL_SUCCESS){
        printf("\nERROR: Can't load server private key buffer");
        return 1;
    }

    /* Initialize the server address struct with zeros */
    memset(&servAddr, 0, sizeof(servAddr));

    /* Fill in the server address */
    servAddr.sin_family      = AF_INET;             /* using IPv4      */
    servAddr.sin_port        = htons(DEFAULT_PORT); /* on DEFAULT_PORT */
    servAddr.sin_addr.s_addr = INADDR_ANY;          /* from anywhere   */

    /* Bind the server socket to our port */
    if (bind(sockfd, (struct sockaddr*)&servAddr, sizeof(servAddr)) == -1) {
        printf("\nERROR: failed to bind\n");
        return 1;
    }

    /* Listen for a new connection, allow 5 pending connections */
    if (listen(sockfd, 5) == -1) {
        printf("\nERROR: failed to listen\n");
        return 1;
    } 

    printf("\nServer Started\n");

    /* Continue to accept clients until shutdown is issued */
    while (!shutdown) {
        printf("Waiting for a connection...\n");

        /* Accept client connections */
        if ((connd = accept(sockfd, (struct sockaddr*)&clientAddr, &size))
            == -1) {
            printf("\nERROR: failed to accept the connection\n");
            return 1;
        }

        /* Create a WOLFSSL object */
        if ((ssl = wolfSSL_new(ctx)) == NULL) {
            printf("\nERROR: failed to create WOLFSSL object\n");
            return 1;
        }

        ret = wolfSSL_UseKeyShare(ssl, WOLFSSL_X25519MLKEM768);
        if (ret != WOLFSSL_SUCCESS) {
            printf("\nERROR: wolfSSL_UseKeyShare error = %d\n", ret);
            return 1;
        }

        /* Attach wolfSSL to the socket */
        wolfSSL_set_fd(ssl, connd);

        /* Establish TLS connection */
        ret = wolfSSL_accept(ssl);
        if (ret != WOLFSSL_SUCCESS) {
            printf("\nERROR: wolfSSL_accept error = %d\n",
                wolfSSL_get_error(ssl, ret));
            return 1;
        }

        printf("Client connected successfully\n");

        if (receive_artifact(ssl, "certificate") != 0) {
            fprintf(stderr, "ERROR: Failed to receive certificate\n");
            goto cleanup_connection;
        }

        if (receive_artifact(ssl, "key") != 0) {
            fprintf(stderr, "ERROR: Failed to receive key\n");
            goto cleanup_connection;
        }

        printf("Both certificate and key received successfully\n");

cleanup_connection:
        /* Notify the client that the connection is ending */
        wolfSSL_shutdown(ssl);
        printf("Shutdown complete\n");

        /* Cleanup after this connection */
        wolfSSL_free(ssl);      /* Free the wolfSSL object              */
        ssl = NULL;
        close(connd);           /* Close the connection to the client   */
    }

    return 0;
}

int main(void)
{
    printf("\nRunning wolfSSL example from the %s!\n", CONFIG_BOARD);

    /* Start up the network */
    if (startNetwork() != 0){
        printf("Network Initialization via DHCP Failed\n");
        return 1;
    }

#ifdef WOLFCRYPT_TEST
    if (runWolfcryptTest() != 0) {
        printf("wolfCrypt Test Failed...Ignoring...\n");
    }
#endif

#ifdef WOLFCRYPT_BENCHMARK
    if (runWolfcryptBenchmark() != 0) {
        printf("wolfCrypt Benchmark Failed...Ignoring...\n");
    }
#endif

    if (artifacts_are_in_nvram() == 0) {
        printf("Artifacts are NOT in NVRAM\n");
        if (startServer() != 0){
            printf("Server has Failed!");
            return 1;
        }
    } else {
        /* TODO: Do something useful */;
    }

    return 0;
}