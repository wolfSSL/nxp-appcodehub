/* main.c
 *
 * Copyright (C) 2006-2023 wolfSSL Inc.
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

/* wolfSSL Includes Start	*/
/* POSIX socket prototypes come from Zephyr; include them before wolfSSL. */
#include <sys/socket.h>
#include <unistd.h>

#include "user_settings.h"
#include <wolfssl/wolfcrypt/settings.h>
#include <wolfssl/ssl.h>
#include <wolfssl/certs_test.h>
#include <wolfssl/wolfcrypt/hash.h>
#include <wolfssl/wolfcrypt/hash.h>
#include <wolfssl/wolfcrypt/coding.h>
#include <wolfssl/wolfcrypt/wc_port.h>
#include <wolfssl/wolfcrypt/asn.h>
#include <wolfssl/wolfcrypt/asn_public.h>
#include <wolfssl/wolfcrypt/error-crypt.h>
#include <wolfssl/wolfcrypt/ecc.h>

/* wolfSSL Includes End */

/* wolfSSH Includes Start */
#include <wolfssh/ssh.h>
#include <wolfssh/internal.h>
#include <wolfssh/agent.h>
#include <wolfssh/certs_test.h>

#ifdef WOLFCRYPT_TEST
#include <wolfcrypt/test/test.h>
#endif

#ifdef WOLFCRYPT_BENCHMARK
#include <wolfcrypt/benchmark/benchmark.h>
#endif

/* wolfSSH Includes End */

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

/* Sample key */
#include "public_key.h"


/* Program Defines Start */

#define DEFAULT_PORT 11111 /* Define the port we want to use for
                            * the network */

#define LOCAL_DEBUG 0 /* Use for wolfSSL's internal Debugging */

#define BUFFER_SIZE 256

/* Use DHCP auto IP assignment or static assignment */
#undef	DHCP_ON
#define DHCP_ON 1   /* Set to true (1) if you want auto assignment IP, */
                    /* set false (0) for statically defined. */
                    /* Make sure to avoid IP conflicts on the network you */
                    /* assign this to, check the defaults before using. */
                    /* If unsure, leave DHCP_ON set to 1 */

#if DHCP_ON == 0
/* Define Static IP, Gateway, and Netmask */
    #define STATIC_IPV4_ADDR  "192.168.1.70"
    #define STATIC_IPV4_GATEWAY "192.168.1.1"
    #define STATIC_IPV4_NETMASK "255.255.255.0"
#endif

/* Set the TLS Version. Currently only 2 or 3 is available for this */
/* application, defaults to TLSv3 */
#undef TLS_VERSION
#define TLS_VERSION 2

/* wolfSSH Login Defines */
#define USER_NAME "zephyr" 
#define USER_PASSWORD "zephyr"

#define USER_KEY publicKeyRSA
#define USER_KEY_SIZE publicKeyRSA_size

/* The devicetree node identifier for the RGB LED determined */
/* by looking at the device tree alias */

#define RED_LED DT_ALIAS(led0)
#define BLUE_LED DT_ALIAS(led2)
#define GREEN_LED DT_ALIAS(led1)

/* Program Defines End */


/* Global Variables/Structs Start */

static const struct gpio_dt_spec red = GPIO_DT_SPEC_GET(RED_LED, gpios);
static const struct gpio_dt_spec blue = GPIO_DT_SPEC_GET(BLUE_LED, gpios);
static const struct gpio_dt_spec green = GPIO_DT_SPEC_GET(GREEN_LED, gpios);
bool redStatus = true;
bool blueStatus =  true;
bool greenStatus = true;

/* Global Variables/Structs End */


int startNetwork()
{
    struct net_if *iface = net_if_get_default();
    char buf[NET_IPV4_ADDR_LEN];

    #if DHCP_ON == 0
        struct in_addr addr, netmask, gw;
    #endif

    if (!(iface)) { /* See if a network interface (Ethernet) is available */
        printf("No network interface determined\n");
        return 1;
    }

    if (net_if_flag_is_set(iface, NET_IF_DORMANT)) {
        printf("Waiting on network interface to be available\n");
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

void handleClientCommunication(WOLFSSH* ssh)
{
    char buffer[BUFFER_SIZE];
    char message[BUFFER_SIZE];
    int ret;
    int message_len = 0;
    const char* backspace_seq = "\b \b";
    wolfSSH_stream_send(ssh, \
        (byte*)("Send Commands: 'red', 'green', and 'blue' to control led\r\n"), \
        strlen("Send Commands: 'red', 'green', and 'blue' to control led\r\n"));
    wolfSSH_stream_send(ssh, \
        (byte*)("Send Command: 'close' to end connection\r\n"), \
        strlen("Send Command: 'close' to end connection\r\n"));
    while (1) {
        memset(buffer, 0, sizeof(buffer));
        ret = wolfSSH_stream_read(ssh, (byte*)buffer, sizeof(buffer) - 1);
        if (ret <= 0) {
            printf("Connection closed or error occurred\n");
            break;
        }

        for (int i = 0; i < ret; i++) {
            /* Handle backspace character (ASCII 8 or 127) */
            if (buffer[i] == '\b' || buffer[i] == 127) {
                if (message_len > 0) {
                    message_len--;
                    /* Send backspace, space, and backspace to the client to */
                    /* visually remove the character */
                    wolfSSH_stream_send(ssh, (byte*)backspace_seq, \
                                        strlen(backspace_seq));
                }
            } else {
                /* Echo each character back to the client */
                 wolfSSH_stream_send(ssh, (byte*)&buffer[i], 1);

                if (buffer[i] == '\n' || buffer[i] == '\r') {
                    if (message_len > 0) {
                        /* Null-terminate the command */
                        message[message_len] = '\0';
                        /* Server-side logging */
                        printf("Received: %s\n", message);

                        /* Echo back the received message with a newline */
                        /* character (server-side command processing) */
                        wolfSSH_stream_send(ssh, (byte*)"\r\n", 2);

                        if (strcmp(message, "close") == 0) {
                            printf("Closing connection as requested by client\n");
                            return; /* Exit the function to close the connection */
                        }
                        else if (strcmp(message, "red") == 0 || \
                                    strcmp(message, "blue") == 0 || \
                                    strcmp(message, "green") == 0) {
                            switch (message[0]) {
                                case 'r':
                                    redStatus = !redStatus;
                                    wolfSSH_stream_send(ssh, \
                                            (byte*)("Red Led Toggling: "), \
                                            strlen("Red Led Toggling: "));
                                    if(!redStatus){
                                            wolfSSH_stream_send(ssh, \
                                                (byte*)("Off\r\n"), \
                                                strlen("Off\r\n"));
                                    }
                                    else{
                                            wolfSSH_stream_send(ssh, \
                                                (byte*)("On\r\n"), \
                                                strlen("On\r\n"));
                                    }
                                    if(gpio_pin_toggle_dt(&red) < 0){
                                        return;
                                    }
                                    break;
                                case 'g':
                                    greenStatus = !greenStatus;
                                    wolfSSH_stream_send(ssh, \
                                            (byte*)("Green Led Toggling: "), \
                                            strlen("Green Led Toggling: "));
                                    if(!greenStatus){
                                            wolfSSH_stream_send(ssh, \
                                                (byte*)("Off\r\n"), \
                                                strlen("Off\r\n"));
                                    }
                                    else{
                                            wolfSSH_stream_send(ssh, \
                                                (byte*)("On\r\n"), \
                                                strlen("On\r\n"));
                                    }
                                    if(gpio_pin_toggle_dt(&green) < 0){
                                        return;
                                    }
                                    break;
                                case 'b':
                                    blueStatus = !blueStatus;
                                    wolfSSH_stream_send(ssh, \
                                            (byte*)("Blue Led Toggling: "), \
                                            strlen("Blue Led Toggling: "));
                                    if(!blueStatus){
                                            wolfSSH_stream_send(ssh, \
                                                (byte*)("Off\r\n"), \
                                                strlen("Off\r\n"));
                                    }
                                    else{
                                            wolfSSH_stream_send(ssh, \
                                                (byte*)("On\r\n"), \
                                                strlen("On\r\n"));
                                    }
                                    if(gpio_pin_toggle_dt(&blue) < 0){
                                        return;
                                    }
                                    break;
                                default:

                                    break;
                            }
                        }
                        /* Reset the message length for the next message */
                        message_len = 0; 
                    }
                } else {
                    message[message_len++] = buffer[i];
                    if (message_len >= BUFFER_SIZE - 1) {
                        printf("Message too long, resetting buffer\n");
                        message_len = 0;
                    }
                }
            }
        }
    }
}


/* Callback function for user authentication */
static int wsUserAuthCallback(byte authType, WS_UserAuthData* authData, void* ctx) {
    if (authType == WOLFSSH_USERAUTH_PASSWORD) {
        /* Print received username for debugging */
        printf("Received username: %s\n", authData->username);

        /* Print received password as hex for debugging */
        /* (avoid printing raw password) */
        printf("Received password (hex): ");
        for (int i = 0; i < authData->sf.password.passwordSz; i++) {
            printf("%02X", authData->sf.password.password[i]);
        }
        printf("\n");

        /* Ensure the received password is null-terminated */
        /* (add one extra byte to buffer if necessary) */
        char receivedPassword[authData->sf.password.passwordSz + 1];
        memcpy(receivedPassword, authData->sf.password.password, \
                authData->sf.password.passwordSz);
        receivedPassword[authData->sf.password.passwordSz] = '\0';

        /* Compare received username and password with expected values */
        if (strcmp(authData->username, USER_NAME) == 0 &&
            strcmp(receivedPassword, USER_PASSWORD) == 0) {
            printf("Authentication successful\n");
            return WOLFSSH_USERAUTH_SUCCESS;
        } else {
            printf("Authentication failed\n");
        }
    } else if (authType == WOLFSSH_USERAUTH_PUBLICKEY) {
        /* Verify both the username and the public key */
        if (strcmp(authData->username, USER_NAME) == 0) {
            printf("Received public key of size: %d\n", authData->sf.publicKey.publicKeySz);
            printf("Known public key size: %d\n", USER_KEY_SIZE);

            printf("Received public key (hex): ");
            for (int i = 0; i < authData->sf.publicKey.publicKeySz; i++) {
                printf("%02X", authData->sf.publicKey.publicKey[i]);
            }
            printf("\n");

            printf("Known public key (hex): ");
            for (int i = 0; i < USER_KEY_SIZE; i++) {
                printf("%02X", USER_KEY[i]);
            }
            printf("\n");

            if (authData->sf.publicKey.publicKeySz == USER_KEY_SIZE &&
                XMEMCMP(authData->sf.publicKey.publicKey, USER_KEY, USER_KEY_SIZE) == 0) {
                printf("Public key authentication successful\n");
                return WOLFSSH_USERAUTH_SUCCESS;
            } else {
                printf("Public key authentication failed\n");
            }
        } else {
            printf("Username check failed\n");
        }
    }
    return WOLFSSH_USERAUTH_FAILURE;
}


int startServer(void)
{
    WOLFSSH_CTX* sshCTX;
    const char *bannerMSG = "wolfSSH Zephyr Server\n";

#ifdef WOLFCRYPT_TEST
    printf("\nRunning wolfCrypt test\n");
    wolfcrypt_test(NULL);
#endif

#ifdef WOLFCRYPT_BENCHMARK
        printf("\nRunning wolfCrypt benchmark\n");
        benchmark_test(NULL);
#endif


    printf("Starting SSH server\n");

    if (wolfSSH_Init() != WS_SUCCESS) {
        printf("Error with wolfSSH init\n");
        return 1;
    }

    #if LOCAL_DEBUG == 1
        printf("Turning on wolfSSH debugging\n");
        wolfSSH_Debugging_ON();
    #endif

    sshCTX = wolfSSH_CTX_new(WOLFSSH_ENDPOINT_SERVER, NULL);
    if (sshCTX == NULL) {
        printf("wolfSSH ctx allocation failed\n");
        return 1;
    }

    if (wolfSSH_CTX_SetBanner(sshCTX, bannerMSG) != WS_SUCCESS) {
        printf("Failed to set banner Message for client\n");
        return 1;
    }

    wolfSSH_SetUserAuth(sshCTX, wsUserAuthCallback);

    /* Use the embedded server key */
    if (wolfSSH_CTX_UsePrivateKey_buffer(sshCTX, rsa_key_der_2048_ssh, \
            sizeof_rsa_key_der_2048_ssh, WOLFSSH_FORMAT_ASN1) != WS_SUCCESS) {
        printf("Error using server key\n");
        return 1;
    }

    /* Create socket */
    int sockfd = socket(AF_INET, SOCK_STREAM, 0);
    if (sockfd < 0) {
        perror("socket");
        return 1;
    }

    /* Bind socket */
    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = INADDR_ANY;
    addr.sin_port = htons(DEFAULT_PORT);

    if (bind(sockfd, (struct sockaddr*)&addr, sizeof(addr)) < 0) {
        perror("bind");
        return 1;
    }

    /* Listen on socket */
    if (listen(sockfd, 5) < 0) {
        perror("listen");
        return 1;
    }


    /* Accept connections */
    while (1) {
        printf("The Server is ready for connections\n");
        int clientfd = accept(sockfd, NULL, NULL);
        if (clientfd < 0) {
            printf("accept"); 
            perror("accept");
            continue;
        }

        WOLFSSH* ssh = wolfSSH_new(sshCTX);
        if (ssh == NULL) {
            printf("Error creating SSH session\n");
            close(clientfd);
        }

        wolfSSH_set_fd(ssh, clientfd);

        if (wolfSSH_accept(ssh) == WS_SUCCESS) {
            printf("Client connected\n");
            handleClientCommunication(ssh);
        } else {
            printf("SSH accept failed\n");
        }
        wolfSSH_free(ssh);
        close(clientfd);
    }

    /* Cleanup */
    wolfSSH_CTX_free(sshCTX);

    #if LOCAL_DEBUG == 1
        printf("Turning off wolfSSH debugging\n");
        wolfSSH_Debugging_OFF();
    #endif

    if (wolfSSH_Cleanup() != WS_SUCCESS) {
        printf("Error with wolfSSH cleanup\n");
    }

    close(sockfd);

    printf("SSH Server is off\n");
    return 0;
}

int main(int argc, char** argv)
{
    printf("\nRunning wolfSSH example from the %s!\n", CONFIG_BOARD);

    /* Setting Up GPIO for LEDs */
    if (!gpio_is_ready_dt(&red)){
        return 0;
    }
    if (!gpio_is_ready_dt(&blue)){
        return 0;
    }
    if (!gpio_is_ready_dt(&green)) {
        return 0;
    }

    if (gpio_pin_configure_dt(&red, GPIO_OUTPUT_ACTIVE) < 0){
        return 0;
    }
    
    if (gpio_pin_configure_dt(&blue, GPIO_OUTPUT_ACTIVE) < 0){
        return 0;
    }
    if (gpio_pin_configure_dt(&green, GPIO_OUTPUT_ACTIVE) < 0){
        return 0;
    }


    /* Start up the network */
    if (startNetwork() != 0){
        printf("Network Initialization via DHCP Failed\n");
        return 1;
    }

    /* Start SSH server */
    if (startServer() != 0){
        printf("Server has Failed!\n");
        return 1;
    }

    return 0;
}