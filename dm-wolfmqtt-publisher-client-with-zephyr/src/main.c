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

/* User Settings file */
/* POSIX socket prototypes come from Zephyr; include them before wolfSSL. */
#include <sys/socket.h>
#include <unistd.h>

#include "user_settings.h"  /* For wolfssl/mqtt zephyr configuration */
#include <wolfssl/wolfcrypt/settings.h>
/* wolfSSL Includes Start */
#include <wolfssl/ssl.h>            /* Basic functionality for TLS */
#include <wolfssl/certs_test.h>     /* Needed for Cert Buffers */
#include <wolfssl/wolfcrypt/hash.h>
#include <wolfssl/wolfcrypt/asn_public.h>
#include <wolfssl/wolfcrypt/coding.h>
#include <wolfssl/wolfcrypt/hmac.h>
/* wolfSSL Includes End */

#ifdef WOLFCRYPT_TEST
#include <wolfcrypt/test/test.h>
#endif

#ifdef WOLFCRYPT_BENCHMARK
#include <wolfcrypt/benchmark/benchmark.h>
#endif

/* wolfMQTT Include Start */
#include <wolfmqtt/mqtt_client.h>
#include <wolfmqtt/version.h>
#include "aws_settings.h"
/* wolfMQTT Include End */

/* Standard Packages Start */
#include <stdio.h>
#include <stdint.h>
#include <stddef.h>
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
#include <zephyr/net/socket.h>
#include <zephyr/net/sntp.h>
#include <zephyr/net/dns_resolve.h>
#include <arpa/inet.h>
#include <netdb.h>
/* Zephyr Includes End */

/* Other */
/* Other end */

/* Program Defines Start */
#define DEFAULT_PORT 11111  /* Define the port we want to use for the network */
#define LOCAL_DEBUG 0       /* Use for wolfSSL's internal Debugging */

/* Use DHCP auto IP assignment or static assignment */
#undef DHCP_ON
#define DHCP_ON 1  /* Set to true (1) if you want auto assignment IP, */ 
                   /* set false (0) for statically defined. */
                   /* Make sure to avoid IP conflicts on the network you */
                   /* assign this to, check the defaults before using. */
                   /* If unsure, leave DHCP_ON set to 1 */
 
#if DHCP_ON == 0
/* Define Static IP, Gateway, and Netmask */
    #define STATIC_IPV4_ADDR  "192.168.1.70"
    #define STATIC_IPV4_GATEWAY "192.168.1.1"
    #define STATIC_IPV4_NETMASK "255.255.255.0"
    #define DNS_SERVER_ADDR "8.8.8.8"
#endif

/* Set the TLS Version. Currently only 2 or 3 is available for this */
/* application, defaults to TLSv3 */
#undef TLS_VERSION
#define TLS_VERSION 1

/* This sets up the correct function for the application via macros */
#undef TLS_METHOD
#if TLS_VERSION == 3
    #define TLS_METHOD wolfTLSv1_3_client_method()
#elif TLS_VERSION == 2
    #define TLS_METHOD wolfTLSv1_2_client_method()
#else 
    #define TLS_METHOD wolfSSLv23_client_method()
#endif

#define NTP_TIMESTAMP_DELTA 2208988800ull
#define DNS_TIMEOUT 2000


/* Local Variables */
static MqttClient mClient;
static MqttNet mNetwork;
static int mSockFd = INVALID_SOCKET_FD;
static byte mSendBuf[MQTT_MAX_PACKET_SZ];
static byte mReadBuf[MQTT_MAX_PACKET_SZ];
static volatile word16 mPacketIdLast;



void set_time_using_ntp(const char* ntp_server) {
    int sockfd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP); /* NTP is UDP */

    // NTP server address
    struct sockaddr_in server_addr;
    memset(&server_addr, 0, sizeof(server_addr));
    server_addr.sin_family = AF_INET;
    server_addr.sin_port = htons(123); /* NTP uses port 123 */
    inet_pton(AF_INET, ntp_server, &server_addr.sin_addr.s_addr);

    // Send request
    unsigned char packet[48] = {0xE3, 0, 6, 0xEC, 0, 0, 0, 0, 0, 0, 0, 0, 0, \
                                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, \
                                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, \
                                0, 0, 0};

    sendto(sockfd, packet, sizeof(packet), 0, (struct sockaddr*)&server_addr, \
                sizeof(server_addr));

    /* Receive time */
    struct sockaddr_in recv_addr;
    socklen_t addr_len = sizeof(recv_addr);
    recvfrom(sockfd, packet, sizeof(packet), 0, (struct sockaddr*)&recv_addr, \
                &addr_len);

    /* Extract time */
    unsigned long long secsSince1900;
    memcpy(&secsSince1900, &packet[40], sizeof(secsSince1900));
    /* Network byte order to host byte order */
    secsSince1900 = ntohl(secsSince1900);
    time_t time = (time_t)(secsSince1900 - NTP_TIMESTAMP_DELTA);

    struct timespec ts;
    ts.tv_sec = time;
    ts.tv_nsec = 0;
    clock_settime(CLOCK_REALTIME, &ts);

    // Print the time
    char timeStr[50];
    struct tm *timeinfo = localtime(&ts.tv_sec);
    strftime(timeStr, sizeof(timeStr), "%Y-%m-%d %H:%M:%S", timeinfo);
    printf("Time set using NTP: %s\n", timeStr);

    close(sockfd);
}

char* resolve_hostname(const char *hostname) {
    static char ip_str[NET_IPV4_ADDR_LEN];
    struct addrinfo hints, *res;
    struct sockaddr_in *addr;
    int err;

    /* Initialize hints */
    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_INET; /* Specify IPv4 address */

    /* Resolve hostname to IP address */
    err = getaddrinfo(hostname, NULL, &hints, &res);
    if (err != 0) {
        printf("getaddrinfo() failed: %d\n", err);
        return NULL;
    }

    /* Convert the IP address to a human-readable form */
    addr = (struct sockaddr_in *)res->ai_addr;
    inet_ntop(AF_INET, &addr->sin_addr, ip_str, sizeof(ip_str));

    /* Free the address info */
    freeaddrinfo(res);

    return ip_str;
}

static int mqtt_message_cb(MqttClient *client, MqttMessage *msg,
    byte msg_new, byte msg_done)
{
    byte buf[PRINT_BUFFER_SIZE+1];
    word32 len;

    (void)client;

    if (msg_new) {
        /* Determine min size to dump */
        len = msg->topic_name_len;
        if (len > PRINT_BUFFER_SIZE) {
            len = PRINT_BUFFER_SIZE;
        }
        XMEMCPY(buf, msg->topic_name, len);
        buf[len] = '\0'; /* Make sure its null terminated */

        /* Print incoming message */
        PRINTF("MQTT Message: Topic %s, Qos %d, Len %u",
            buf, msg->qos, msg->total_len);
    }

    /* Print message payload */
    len = msg->buffer_len;
    if (len > PRINT_BUFFER_SIZE) {
        len = PRINT_BUFFER_SIZE;
    }
    XMEMCPY(buf, msg->buffer, len);
    buf[len] = '\0'; /* Make sure its null terminated */
    PRINTF("Payload (%d - %d) printing %d bytes:" LINE_END "%s",
        msg->buffer_pos, msg->buffer_pos + msg->buffer_len, len, buf);

    if (msg_done) {
        PRINTF("MQTT Message: Done");
    }

    /* Return negative to terminate publish processing */
    return MQTT_CODE_SUCCESS;
}

static void setup_timeout(struct timeval* tv, int timeout_ms)
{
    tv->tv_sec = timeout_ms / 1000;
    tv->tv_usec = (timeout_ms % 1000) * 1000;

    /* Make sure there is a minimum value specified */
    if (tv->tv_sec < 0 || (tv->tv_sec == 0 && tv->tv_usec <= 0)) {
        tv->tv_sec = 0;
        tv->tv_usec = 100;
    }
}

static int socket_get_error(int sockFd)
{
    int so_error = 0;
    socklen_t len = sizeof(so_error);
    (void)getsockopt(sockFd, SOL_SOCKET, SO_ERROR, &so_error, &len);
    return so_error;
}

static int mqtt_net_connect(void *context, const char* host, word16 port,
    int timeout_ms)
{
    int rc;
    int sockFd, *pSockFd = (int*)context;
    struct sockaddr_in addr;
    struct addrinfo *result = NULL;
    struct addrinfo hints;

    if (pSockFd == NULL) {
        return MQTT_CODE_ERROR_BAD_ARG;
    }

    (void)timeout_ms;

    /* get address */
    XMEMSET(&hints, 0, sizeof(hints));
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;

    XMEMSET(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;

    rc = getaddrinfo(host, NULL, &hints, &result);
    if (rc >= 0 && result != NULL) {
        struct addrinfo* res = result;

        /* prefer ip4 addresses */
        while (res) {
            if (res->ai_family == AF_INET) {
                break;
            }
            res = res->ai_next;
        }
        if (res) {
            addr.sin_port = htons(port);
            addr.sin_family = AF_INET;
            addr.sin_addr =
                ((struct sockaddr_in*)(res->ai_addr))->sin_addr;
        }
        else {
            rc = -1;
        }
        freeaddrinfo(result);
    }
    if (rc < 0) {
        return MQTT_CODE_ERROR_NETWORK;
    }

    sockFd = socket(addr.sin_family, SOCK_STREAM, 0);
    if (sockFd < 0) {
        return MQTT_CODE_ERROR_NETWORK;
    }

    /* Start connect */
    rc = connect(sockFd, (struct sockaddr*)&addr, sizeof(addr));
    if (rc < 0) {
        PRINTF("NetConnect: Error %d (Sock Err %d)",
            rc, socket_get_error(*pSockFd));
        close(sockFd);
        return MQTT_CODE_ERROR_NETWORK;
    }

    /* save socket number to context */
    *pSockFd = sockFd;
    return MQTT_CODE_SUCCESS;
}

static int mqtt_net_read(void *context, byte* buf, int buf_len, int timeout_ms)
{
    int rc;
    int *pSockFd = (int*)context;
    int bytes = 0;
    struct timeval tv;

    if (pSockFd == NULL) {
        return MQTT_CODE_ERROR_BAD_ARG;
    }

    /* Setup timeout */
    setup_timeout(&tv, timeout_ms);
    (void)setsockopt(*pSockFd, SOL_SOCKET, SO_RCVTIMEO, (char *)&tv,
            sizeof(tv));

    /* Loop until buf_len has been read, error or timeout */
    while (bytes < buf_len) {
        rc = (int)recv(*pSockFd, &buf[bytes], buf_len - bytes, 0);
        if (rc < 0) {
            rc = socket_get_error(*pSockFd);
            if (rc == 0)
                break; /* timeout */
            PRINTF("NetRead: Error %d", rc);
            return MQTT_CODE_ERROR_NETWORK;
        }
        bytes += rc; /* Data */
    }

    if (bytes == 0) {
        return MQTT_CODE_ERROR_TIMEOUT;
    }
    return bytes;
}

static int mqtt_net_write(void *context, const byte* buf, int buf_len,
    int timeout_ms)
{
    int rc;
    int *pSockFd = (int*)context;
    struct timeval tv;

    if (pSockFd == NULL) {
        return MQTT_CODE_ERROR_BAD_ARG;
    }

    /* Setup timeout */
    setup_timeout(&tv, timeout_ms);
    (void)setsockopt(*pSockFd, SOL_SOCKET, SO_SNDTIMEO, (char *)&tv,
            sizeof(tv));

    rc = (int)send(*pSockFd, buf, buf_len, 0);
    if (rc < 0) {
        PRINTF("NetWrite: Error %d (Sock Err %d)",
            rc, socket_get_error(*pSockFd));
        return MQTT_CODE_ERROR_NETWORK;
    }
    return rc;
}

static int mqtt_net_disconnect(void *context)
{
	PRINTF("ENTER MQTT NET DISCONNECT");
    int *pSockFd = (int*)context;

    if (pSockFd == NULL) {
        return MQTT_CODE_ERROR_BAD_ARG;
    }

    close(*pSockFd);
    *pSockFd = INVALID_SOCKET_FD;
	PRINTF("EXIT MQTT NET DISCONNECT");
    return MQTT_CODE_SUCCESS;
}


static int mqtt_tls_verify_cb(int preverify, WOLFSSL_X509_STORE_CTX* store)
{
    char buffer[WOLFSSL_MAX_ERROR_SZ];

    PRINTF("MQTT TLS Verify Callback: PreVerify %d, Error %d (%s)", preverify,
        store->error, store->error != 0 ?
            wolfSSL_ERR_error_string(store->error, buffer) : "none");
    PRINTF("  Subject's domain name is %s", store->domain);

    if (store->error != 0) {
        /* Allowing to continue */
        /* Should check certificate and return 0 if not okay */
        PRINTF("  Allowing cert anyways");
    }

    return 1;
}


static int mqtt_tls_cb(MqttClient* client)
{

    int rc = WOLFSSL_FAILURE;
    /* Use highest available and allow downgrade. If wolfSSL is built with
     * old TLS support, it is possible for a server to force a downgrade to
     * an insecure version. */
    client->tls.ctx = wolfSSL_CTX_new(TLS_METHOD);
    if (client->tls.ctx) {

        wolfSSL_CTX_set_verify(client->tls.ctx, WOLFSSL_VERIFY_PEER,
                               mqtt_tls_verify_cb);

        /* Load CA certificate buffer */
        rc = wolfSSL_CTX_load_verify_buffer(client->tls.ctx, (const byte*)root_ca, (long)XSTRLEN(root_ca), WOLFSSL_FILETYPE_PEM);
        if (rc != WOLFSSL_SUCCESS) {
            PRINTF("Failed to load CA certificate");
            wolfSSL_CTX_free(client->tls.ctx);
            return rc;
        }

        /* Load client certificate */
        rc = wolfSSL_CTX_use_certificate_buffer(client->tls.ctx, (const unsigned char*)device_cert, strlen(device_cert), WOLFSSL_FILETYPE_PEM);
        if (rc != WOLFSSL_SUCCESS) {
            PRINTF("Failed to load client certificate");
            wolfSSL_CTX_free(client->tls.ctx);
            return rc;
        }

        /* Load client private key */
        rc = wolfSSL_CTX_use_PrivateKey_buffer(client->tls.ctx, (const unsigned char*)device_priv_key, strlen(device_priv_key), WOLFSSL_FILETYPE_PEM);
        if (rc != WOLFSSL_SUCCESS) {
            PRINTF("Failed to load client private key");
            wolfSSL_CTX_free(client->tls.ctx);
            return rc;
        }

        /* Create WOLFSSL object */
        client->tls.ssl = wolfSSL_new(client->tls.ctx);
        if (client->tls.ssl == NULL) {
            PRINTF("Failed to create WOLFSSL object");
            wolfSSL_CTX_free(client->tls.ctx);
            return WOLFSSL_FAILURE;
        }

        /* wolfSSL v5.9 made PQC hybrid key-shares the default; on classic-only
         * builds that causes BAD_FUNC_ARG during ClientHello. Pin P-256. */
        if (wolfSSL_UseKeyShare(client->tls.ssl, WOLFSSL_ECC_SECP256R1)
                != WOLFSSL_SUCCESS) {
            PRINTF("Failed to pin P-256 key share");
            wolfSSL_free(client->tls.ssl);
            wolfSSL_CTX_free(client->tls.ctx);
            return WOLFSSL_FAILURE;
        }
    }

    PRINTF("MQTT TLS Setup (%d)", rc);

    return rc;
}

static word16 mqtt_get_packetid(void)
{
    /* Check rollover */
    if (mPacketIdLast >= MAX_PACKET_ID) {
        mPacketIdLast = 0;
    }

    return ++mPacketIdLast;
}

int mqttsimple_test(void)
{
    int rc = 0;
    MqttObject mqttObj;
    MqttTopic topics[1];

    /* Initialize MQTT client */
    XMEMSET(&mNetwork, 0, sizeof(mNetwork));
    mNetwork.connect = mqtt_net_connect;
    mNetwork.read = mqtt_net_read;
    mNetwork.write = mqtt_net_write;
    mNetwork.disconnect = mqtt_net_disconnect;
    mNetwork.context = &mSockFd;
    rc = MqttClient_Init(&mClient, &mNetwork, mqtt_message_cb,
        mSendBuf, sizeof(mSendBuf), mReadBuf, sizeof(mReadBuf),
        MQTT_CON_TIMEOUT_MS);
    if (rc != MQTT_CODE_SUCCESS) {
        goto exit;
    }
    PRINTF("MQTT Init Success");

    /* Connect to broker */
    wolfSSL_Debugging_ON();
    /* Hostname, not IP: wolfMQTT >= 2.2 checks the cert against it. */
    rc = MqttClient_NetConnect(&mClient, MQTT_HOST, MQTT_PORT,
        MQTT_CON_TIMEOUT_MS, MQTT_USE_TLS, mqtt_tls_cb);
    if (rc != MQTT_CODE_SUCCESS) {
        goto exit;
    }
    PRINTF("MQTT Network Connect Success: Host %s, Port %d, UseTLS %d",
        MQTT_HOST, MQTT_PORT, MQTT_USE_TLS);

    /* Send Connect and wait for Ack */
    XMEMSET(&mqttObj, 0, sizeof(mqttObj));
    mqttObj.connect.keep_alive_sec = MQTT_KEEP_ALIVE_SEC;
    mqttObj.connect.client_id = MQTT_CLIENT_ID;
    mqttObj.connect.username = MQTT_USERNAME;
    mqttObj.connect.password = MQTT_PASSWORD;
    rc = MqttClient_Connect(&mClient, &mqttObj.connect);
    if (rc != MQTT_CODE_SUCCESS) {
        goto exit;
    }
    PRINTF("MQTT Broker Connect Success: ClientID %s, Username %s, Password %s",
        MQTT_CLIENT_ID,
        (MQTT_USERNAME == NULL) ? "Null" : MQTT_USERNAME,
        (MQTT_PASSWORD == NULL) ? "Null" : MQTT_PASSWORD);

    /* Subscribe and wait for Ack */
    XMEMSET(&mqttObj, 0, sizeof(mqttObj));
    /* Zero the unused fields: wolfMQTT >= 2.2 rejects junk v5 sub_options. */
    XMEMSET(topics, 0, sizeof(topics));
    topics[0].topic_filter = MQTT_TOPIC_NAME;
    topics[0].qos = MQTT_QOS;
    mqttObj.subscribe.packet_id = mqtt_get_packetid();
    mqttObj.subscribe.topic_count = sizeof(topics) / sizeof(MqttTopic);
    mqttObj.subscribe.topics = topics;
    rc = MqttClient_Subscribe(&mClient, &mqttObj.subscribe);
    if (rc != MQTT_CODE_SUCCESS) {
        goto exit;
    }
    PRINTF("MQTT Subscribe Success: Topic %s, QoS %d",
        MQTT_TOPIC_NAME, MQTT_QOS);

    /* Publish */
    XMEMSET(&mqttObj, 0, sizeof(mqttObj));
    mqttObj.publish.qos = MQTT_QOS;
    mqttObj.publish.topic_name = MQTT_TOPIC_NAME;
    mqttObj.publish.packet_id = mqtt_get_packetid();
    mqttObj.publish.buffer = (byte*)MQTT_PUBLISH_MSG;
    mqttObj.publish.total_len = XSTRLEN(MQTT_PUBLISH_MSG);
    rc = MqttClient_Publish(&mClient, &mqttObj.publish);
    if (rc != MQTT_CODE_SUCCESS) {
        goto exit;
    }
    PRINTF("MQTT Publish: Topic %s, ID %d, Qos %d, Message %s",
        mqttObj.publish.topic_name, mqttObj.publish.packet_id,
        mqttObj.publish.qos, mqttObj.publish.buffer);

   return 0; 

exit:
    if (rc != MQTT_CODE_SUCCESS) {
        PRINTF("MQTT Error %d: %s", rc, MqttClient_ReturnCodeToString(rc));
    }
    return rc;
}

/* Set up the network using the zephyr network stack */
int startNetwork() {
    struct net_if *iface = net_if_get_default();
    char buf[NET_IPV4_ADDR_LEN];

    #if DHCP_ON == 0
        struct in_addr addr, netmask, gw;
    #endif

    if (!iface) { /* Check if a network interface (ethernet) is available */
        printf("No network interface determined\n");
        return 1;
    }

    if (net_if_flag_is_set(iface, NET_IF_DORMANT)) {
        printf("Waiting on network interface to be available\n");
        while (!net_if_is_up(iface)) {
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








int main(void) {
    printf("\nRunning wolfMQTT example from the %s!\n", CONFIG_BOARD);
    
    /* Start up the network */
    if (startNetwork() != 0) {
        printf("Network Initialization via DHCP Failed\n");
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

    set_time_using_ntp(resolve_hostname("pool.ntp.org"));
    if (wolfSSL_Init() != WOLFSSL_SUCCESS) {
        PRINTF("FAILED");
        return 1;
    }
    mqttsimple_test();
    PRINTF("FINISHED MQTT AWS TEST");
    while(1);
    return 0;
}
