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
#include "user_settings.h"      /* For wolfSSL Zephyr configuration */
#include <wolfssl/wolfcrypt/settings.h>
#include <wolfssl/ssl.h>        /* Basic functionality for TLS */
#include <wolfssl/certs_test.h> /* Needed for Cert Buffers */
#include <wolfssl/wolfcrypt/hash.h>
/* wolfSSL Includes End */

#ifdef WOLFCRYPT_TEST
#include <wolfcrypt/test/test.h>
#endif

#ifdef WOLFCRYPT_BENCHMARK
#include <wolfcrypt/benchmark/benchmark.h>
#endif

/* Standard Packages Start */
#include <stdio.h>
#include <time.h>
#include <string.h>
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
#include <zephyr/sys/reboot.h>
/* Zephyr Includes End */

/* mqttClient Includes Start */
#include "mqttClient/fwclient.h"
/* mqttClient Includes End */

/* wolfBoot Includes Start */
#include "wolfboot/wolfboot.h"
#include "wolfboot_status.h"
/* wolfBoot Includes End */

volatile boot_state_t g_boot_state         = BOOT_STATE_UNKNOWN;
volatile uint32_t     g_boot_version       = 0;
volatile uint32_t     g_update_version     = 0;
volatile uint8_t      g_boot_state_byte    = 0xAA;
volatile int          g_boot_state_rc      = -1;
volatile uint8_t      g_update_state_byte  = 0xAA;
volatile int          g_update_state_rc    = -1;

/* Program Defines Start */

#define DEFAULT_PORT 11111  /* Define the port we want to use for the network */

#define LOCAL_DEBUG 0       /* Use for wolfSSL's internal Debugging */

/* Use DHCP auto IP assignment or static assignment.
 * Follows CONFIG_NET_DHCPV4 from prj.conf so both stay in sync. */
#undef  DHCP_ON
#ifdef CONFIG_NET_DHCPV4
    #define DHCP_ON 1
#else
    #define DHCP_ON 0
#endif
 
#if DHCP_ON == 0
/* Define Static IP, Gateway, and Netmask */
    #define STATIC_IPV4_ADDR  "192.168.1.70"
    #define STATIC_IPV4_GATEWAY "192.168.1.1"
    #define STATIC_IPV4_NETMASK "255.255.255.0"
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
    printf("IP Address is: %s", net_addr_ntop(AF_INET, \
                    &iface->config.ip.ipv4->unicast[0].ipv4.address.in_addr, \
                    buf, sizeof(buf)));

    return 0;
}

/* Initialize Server for a client connection */
int startServer(void) {
    int                sockfd = SOCKET_INVALID;
    int                connd = SOCKET_INVALID;
    struct sockaddr_in servAddr;
    struct sockaddr_in clientAddr;
    socklen_t          size = sizeof(clientAddr);
    char               buff[256];
    size_t             len;
    int                shutdown = 0;
    int                ret;
    const char*        reply = "I hear ya fa shizzle!\n";

    WOLFSSL_CTX* ctx = NULL;
    WOLFSSL*     ssl = NULL;
    WOLFSSL_CIPHER* cipher;

    #if LOCAL_DEBUG
        wolfSSL_Debugging_ON();
    #endif

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
                server_cert_der_2048, sizeof_server_cert_der_2048,
                WOLFSSL_FILETYPE_ASN1) != WOLFSSL_SUCCESS){
        printf("\nERROR: Cannot load server cert buffer\n");
        return 1; 
    }

    if (wolfSSL_CTX_use_PrivateKey_buffer(ctx, server_key_der_2048,
            sizeof_server_key_der_2048, SSL_FILETYPE_ASN1) != WOLFSSL_SUCCESS){
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

        cipher = wolfSSL_get_current_cipher(ssl);
        printf("SSL cipher suite is %s\n", wolfSSL_CIPHER_get_name(cipher));


        /* Read the client data into our buff array */
        memset(buff, 0, sizeof(buff));
        if ((ret = wolfSSL_read(ssl, buff, sizeof(buff)-1)) == -1) {
            printf("\nERROR: failed to read\n");
            return 1;
        }

        /* Print to stdout any data the client sends */
        printf("Client: %s\n", buff);

        /* Check for server shutdown command */
        if (strncmp(buff, "shutdown", 8) == 0) {
            printf("Shutdown command issued!\n");
            shutdown = 1;
        }



        /* Write our reply into buff */
        memset(buff, 0, sizeof(buff));
        memcpy(buff, reply, strlen(reply));
        len = strnlen(buff, sizeof(buff));

        /* Reply back to the client */
        if ((ret = wolfSSL_write(ssl, buff, len)) != len) {
            printf("\nERROR: failed to write\n");
            return 1;
        }

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
    {
        uint8_t  bst = 0xAA, ust = 0xAA;
        int      brc = wolfBoot_nsc_get_partition_state(PART_BOOT,   &bst);
        int      urc = wolfBoot_nsc_get_partition_state(PART_UPDATE, &ust);
        uint32_t bv  = wolfBoot_nsc_get_image_version(PART_BOOT);
        uint32_t uv  = wolfBoot_nsc_get_image_version(PART_UPDATE);
        boot_state_t cls = boot_state_classify(bv, uv, bst, brc, ust, urc);

        g_boot_version       = bv;
        g_update_version     = uv;
        g_boot_state_byte    = bst;
        g_boot_state_rc      = brc;
        g_update_state_byte  = ust;
        g_update_state_rc    = urc;
        g_boot_state         = cls;

        printf("[wolfBoot] BOOT v=%u state=0x%02x(rc=%d)  UPDATE v=%u state=0x%02x(rc=%d) -> %s\n",
               (unsigned)bv, (unsigned)bst, brc,
               (unsigned)uv, (unsigned)ust, urc,
               boot_state_name(cls));

        if (cls == BOOT_STATE_AWAITING_CONFIRM) {
            printf("[wolfBoot] running new image, will call success() after self-test\n");
            /* Demo: confirm immediately. Real apps should self-test first. */
            wolfBoot_nsc_success();
            g_boot_state = BOOT_STATE_NORMAL;
            printf("[wolfBoot] success() called -> state confirmed\n");
        }
    }

    /* Start up the network */
    if (startNetwork() != 0){
        printf("Network Initialization via DHCP Failed");
        return 1;
    }

#ifdef WOLFCRYPT_TEST
    printf("\nRunning wolfCrypt test\n");
    wolfcrypt_test(NULL);
#endif

#ifdef WOLFCRYPT_BENCHMARK
    printf("\nRunning wolfCrypt benchmark\n");
    benchmark_test(NULL);
#endif

#if defined(CONFIG_WOLFMQTT)
        printf("\nRunning wolfMQTT firmware client for OTA\n");
        if (fwclient_main() != 0) {
            printf("Firmware client has Failed!");
            return 1;
        } else {
            unsigned int irq_key;

            printf("Firmware client completed successfully!\n");

            k_sched_lock();
            irq_key = irq_lock();
            printf("Triggering wolfBoot update\n");
            wolfBoot_nsc_update_trigger();
            irq_unlock(irq_key);
            k_sched_unlock();
            sys_reboot(SYS_REBOOT_COLD);
        }
#else
    if (startServer() != 0){
        printf("Server has Failed!");
        return 1;
    }
#endif
    return 0;
}