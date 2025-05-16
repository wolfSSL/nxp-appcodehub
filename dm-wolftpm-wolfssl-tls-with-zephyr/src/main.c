/* main.c
 *
 * Copyright (C) 2006-2025 wolfSSL Inc.
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
#include "user_settings.h"      /* For wolfssl zephyr configuration */
#include <wolfssl/ssl.h>        /* Basic functionality for TLS */
#include <wolfssl/certs_test.h> /* Needed for Cert Buffers */
#include <wolfssl/wolfcrypt/hash.h>
#include <wolfssl/wolfcrypt/asn_public.h>
/* wolfSSL Includes End */

/* wolfTPM Includes Start */
#include <wolftpm/tpm2.h>
#include <wolftpm/tpm2_wrap.h>
#include <bench.h>
#include <csr.h>
#include <wrap_test.h>
#include <native_test.h>
#include <keygen.h>
#include <management.h>
#include <tls_server.h>
#include <test.h>
#include <benchmark.h>
/* wolfTPM Includes End */


/* Standard Packages Start */
#include <stdio.h>
#include <time.h>
#include <ff.h>
/* Standard Packages End */

/* Zephyr Includes Start */
#include <zephyr/device.h>
#include <zephyr/drivers/gpio.h>
#include <zephyr/drivers/uart.h>
#include <zephyr/fs/fs.h>
#include <zephyr/kernel.h>
#include <zephyr/net/dhcpv4.h>
#include <zephyr/net/dns_resolve.h>
#include <zephyr/net/net_config.h>
#include <zephyr/net/net_context.h>
#include <zephyr/net/net_core.h>
#include <zephyr/net/net_if.h>
#include <zephyr/net/net_ip.h>
#include <zephyr/net/net_mgmt.h>
#include <zephyr/net/sntp.h>
#include <zephyr/net/socket.h>
#include <zephyr/posix/time.h>
#include <zephyr/storage/disk_access.h>
#include <zephyr/sys/timeutil.h>
/* Zephyr Includes End */




/* Program Defines Start */

#define DEFAULT_PORT 11111  /* Define the port we want to use for the network */

#define LOCAL_DEBUG 0       /* Use for wolfSSL's internal Debugging */

#ifndef NO_INTERNET
    #define NO_INTERNET 1
#endif



/* Use DHCP auto ip assignment or static assignment */
#ifndef  DHCP_ON
    #define DHCP_ON 0   /* Set to true (1) if you want auto assignment ip, */ 
                        /* set false (0) for staticly define. */
                        /* Make sure to avoid IP conflicts on the network you */
                        /* assign this to, check the defaults before using. */
                        /* If unsure leave DHCP_ON set to 1 */
#endif

#if DHCP_ON == 0
/* Define Static IP, Gateway, and Netmask */
    #define STATIC_IPV4_ADDR  "192.168.0.44"
    #define STATIC_IPV4_GATEWAY "192.168.0.1"
    #define STATIC_IPV4_NETMASK "255.255.255.0"
#endif

#if NO_INTERNET == 0
#if DHCP_ON == 0
        #define DNS_SERVER_ADDR "8.8.8.8"
#endif
#endif

/* Set the TLS Version Currently only 2 or 3 is avaliable for this */
/* application, defaults to TLSv3 */
#undef TLS_VERSION
#define TLS_VERSION 3

/* This just sets up the correct function for the application via macro's*/
#undef TLS_METHOD
#if TLS_VERSION == 3
    #define TLS_METHOD wolfTLSv1_3_server_method()
#elif TLS_VERSION == 2
    #define TLS_METHOD wolfTLSv1_2_server_method()
#else 
    #define TLS_METHOD wolfTLSv1_3_server_method()
#endif

#if NO_INTERNET == 0
    #define NTP_TIMESTAMP_DELTA 2208988800ull
    #define DNS_TIMEOUT 2000
#endif


#define BENCHMARK_PROMPT "Enter benchmark type (-h,-aes or -xor): "
#define WRAP_PROMPT "Enter wrap test option (-h,-aes or -xor): "
#define DURATION_PROMPT "Set Benchmark Duration (Default 1 Sec)? (yes or no): "
#define NATIVE_PROMPT "Enter native test option (-h,-aes or -xor): "
#define KEYGEN_PROMPT "Enter a Keygen Option (-h for help)"
#define SERVER_PROMPT "Enter a Server Option (-h for help)"
#define TIME_PROMPT "Enter a Time Option (-h for help)"

static FATFS fat_fs;
/* mounting info */
static struct fs_mount_t mp = {
    .type = FS_FATFS,
    .fs_data = &fat_fs,
};



/* Forward declarations for functions called by main() */
int startNetwork(void);
int poll_response(char* input, int len);
void list_of_commands(void);
int fullDemo(void);
int runBenchmark(void);
int generateCSR(void);
int runWrapTest(void);
int runNativeTest(void);
int runKeygen(void);
int tpmClear(void);
int startServer(void);
void setTime(void);
void set_time_manually(int month, int day, int year, int hour, int minute, int second);
int runWolfcryptTest(void);
int runWolfcryptBenchmark(void);
#if NO_INTERNET == 0
void set_time_using_ntp(const char* ntp_server);
char* resolve_hostname(const char *hostname);
#endif
int mount_sd_card(char* disk_name);


int main(void)
{

    int ret = 0;
    char input[128];

    /* Start up the network */
    if (startNetwork() != 0){
        printf("Network Initialization via DHCP Failed");
        return 1;
    }

    #if NO_INTERNET == 0
        /* If NO_INTERNET is false , we will use the internet to set the time */
        set_time_using_ntp(resolve_hostname("pool.ntp.org"));
    #else
        /* If NO_INTERNET is true, we will set the time manually */
        set_time_manually(1, 1, 2025, 12, 0, 0); /* Set the time to 1st January 2025 12:00:00 */
    #endif

    k_msleep(5000);
    list_of_commands();


    /* Main loop for demo commands*/
    while (1) {
        printk("\nEnter command: ");
        poll_response(input, sizeof(input));

        if (strcmp(input, "-bench") == 0) {
            ret = runBenchmark();
            if (ret != 0) {
                printk("Benchmark failed with return: %d", ret);
            }
        }
        else if (strcmp(input, "-wolfcryptbench") == 0) {
            ret = runWolfcryptBenchmark();
            if (ret != 0) {
                printk("Benchmark failed with return: %d", ret);
            }
        }
        else if (strcmp(input, "-cert") == 0) {
            ret = generateCSR();
            if (ret != 0) {
                printk("CSR generation failed with return: %d", ret);
            }
        }
        else if (strcmp(input, "-demo") == 0) {
            ret = fullDemo();
            if (ret != 0) {
                printk("Full demo failed with return: %d", ret);
            }
        }
        else if (strcmp(input, "-keygen") == 0) {
            ret = runKeygen();
            if (ret != 0) {
                printk("Key generation failed with return code: %d\n", ret);
            }
        }
        else if (strcmp(input, "-native") == 0) {
            ret = runNativeTest();
            if (ret != 0) {
                printk("Native test failed with return: %d", ret);
            }
        }
        else if (strcmp(input, "-server") == 0) {
            ret = startServer();
            if (ret != 0) {
                printk("Server failed with return: %d", ret);
            }
        }
        else if (strcmp(input, "-wolfcrypttest") == 0) {
            ret = runWolfcryptTest();
            if (ret != 0) {
                printk("WolfCrypt test failed with return: %d", ret);
            }
        }
        else if (strcmp(input, "-time") == 0) {
            setTime();
        }
        else if (strcmp(input, "-tpmclear") == 0) {
            ret = tpmClear();
            if (ret != 0) {
                printk("TPM clear failed with return: %d", ret);
            }
        }
        else if (strcmp(input, "-wrap") == 0) {
            ret = runWrapTest();
            if (ret != 0) {
                printk("Wrap test failed with return: %d", ret);
            }
        }
        else if (strcmp(input, "-help") == 0) {
            list_of_commands();
        }
        else if (strcmp(input, "-exit") == 0) {
            break;
        }
        else {
            printk("Invalid command\n");
            list_of_commands();
        }
    }

    printk("\nFinished wolfTPM Example on the %s!\n", CONFIG_BOARD);

    return 0;

}


void list_of_commands(void)
{
    printk("Commands:\n");
    printk("  -bench: Run a benchmark\n");
    printk("  -cert: Generate a CSR\n");
    printk("  -demo: Run the full demo\n");
    printk("  -keygen: Generate a key\n");
    printk("  -native: Run a native test\n");
    printk("  -server: Start the server\n");
    printk("  -time: Set the time\n");
    printk("  -tpmclear: Clear the TPM\n");
    printk("  -wrap: Run a wrap test\n");
    printk("  -wolfcryptbench: Run the wolfcrypt benchmark\n");
    printk("  -wolfcrypttest: Run the wolfcrypt test\n");
    printk("  -help: List of commands\n");
    printk("  -exit: Exit the program\n");
}


int runWolfcryptTest(void)
{
    int ret;

    ret = mount_sd_card("SD");
    if (ret != 0) {
        printk("Failed to mount SD card\n");
        return -1;
    }

    ret = wolfcrypt_test(NULL);

    if (fs_unmount(&mp) != 0) {
        printk("Could not unmount file\n");
    }

    return ret;
}

int runWolfcryptBenchmark(void)
{
    int ret;
    
    ret = mount_sd_card("SD");
    if (ret != 0) {
        printk("Failed to mount SD card\n");
        return -1;
    }

    ret = benchmark_test(NULL);

    if (fs_unmount(&mp) != 0) {
        printk("Could not unmount file\n");
    }

    return ret;
}

#if NO_INTERNET == 0
void set_time_using_ntp(const char* ntp_server)
{
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
#endif

#if NO_INTERNET == 0
char* resolve_hostname(const char *hostname)
{
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
#endif

/* Set up the network using the zephyr network stack */
int startNetwork(void)
{

    struct net_if *iface = net_if_get_default();
    char buf[NET_IPV4_ADDR_LEN];

    #if DHCP_ON == 0
        struct in_addr addr, netmask, gw;
    #endif

    if (!(iface)) { /* See if a network interface (ethernet) is avaliable */
        printk("No network interface determined");
        return 1;
    }

    if (net_if_flag_is_set(iface, NET_IF_DORMANT)) {
        printk("Waiting on network interface to be avaliable");
        while(!net_if_is_up(iface)){
            k_sleep(K_MSEC(100));
        }
    }

    #if DHCP_ON == 1
        printk("\nStarting DHCP to obtain IP address\n");
        net_dhcpv4_start(iface);
        (void)net_mgmt_event_wait_on_iface(iface, NET_EVENT_IPV4_DHCP_BOUND, \
                                            NULL, NULL, NULL, K_FOREVER);
    #elif DHCP_ON == 0
        /* Static IP Configuration */
        if (net_addr_pton(AF_INET, STATIC_IPV4_ADDR, &addr) < 0 ||
            net_addr_pton(AF_INET, STATIC_IPV4_NETMASK, &netmask) < 0 ||
            net_addr_pton(AF_INET, STATIC_IPV4_GATEWAY, &gw) < 0) {
            printk("Invalid IP address settings.\n");
            return -1;
        }
        net_if_ipv4_set_netmask_by_addr(iface, &addr, &netmask);
        net_if_ipv4_set_gw(iface, &gw);
        net_if_ipv4_addr_add(iface, &addr, NET_ADDR_MANUAL, 0);

    #else
        #error "Please set DHCP_ON to true (1) or false (2), if unsure set to true (1)"
    #endif

    /* Display IP address that was assigned when done */
    printk("IP Address is: %s\n\n", net_addr_ntop(AF_INET, \
                    &iface->config.ip.ipv4->unicast[0].ipv4.address.in_addr, \
                    buf, sizeof(buf)));

    return 0;
}

/* Initialize Server for a client connection */
int startServer(void)
{
    int ret = 0;
    char input[5];
    char keyType[5] = "-rsa"; /* Default key type */
    char paramEnc[5] = "";    /* Parameter encryption */
    char pkOption[5] = "";    /* PK callbacks option */
    char loopOption[5] = "";  /* Run in loop option */
    char selfSign[6] = "";    /* Self-signed cert option */
    char portOption[10] = ""; /* Custom port option */
    char* argv[7];            /* Arguments array */
    int argc = 1;             /* Start with program name only */

    printk("\n%s\n", SERVER_PROMPT);
    
    /* Prompt for key type */
    printk("Select key type for server:\n");
    printk(" 1. RSA (default)\n");
    printk(" 2. ECC\n");
    printk(" h. Help\n");
    printk("Enter choice: ");
    
    ret = poll_response(input, sizeof(input));
    printk("Input: %s\n", input);
    
    /* Process key type selection */
    if (input[0] == 'h' || input[0] == 'H') {
        /* Call server with help flag */
        argv[0] = "TPM2_TLS_Server";
        argv[1] = "-h";
        return TPM2_TLS_ServerArgs(NULL, 2, argv);
    }
    else if (input[0] == '2') {
        strcpy(keyType, "-ecc");
        printk("Selected ECC key\n");
    }
    else {
        printk("Selected RSA key (default)\n");
    }
    
    /* Prompt for parameter encryption */
    printk("Use parameter encryption? (1=AES, 2=XOR, n=none, default: none): ");
    ret = poll_response(input, sizeof(input));
    printk("Input: %s\n", input);
    
    if (input[0] == '1') {
        strcpy(paramEnc, "-aes");
        printk("Using AES parameter encryption\n");
    }
    else if (input[0] == '2') {
        strcpy(paramEnc, "-xor");
        printk("Using XOR parameter encryption\n");
    }
    else {
        printk("Not using parameter encryption\n");
    }
    
    /* Prompt for PK callbacks */
#if 0
    printk("Use PK callbacks? (y/n, default: n): ");
    ret = poll_response(input, sizeof(input));
    printk("Input: %s\n", input);
    
    if (input[0] == 'y' || input[0] == 'Y') {
        strcpy(pkOption, "-pk");
        printk("Using PK callbacks\n");
    }
    else {
        printk("Using crypto callbacks (default)\n");
    }
#else
    /* Always use PK callbacks for tpm example */
    strcpy(pkOption, "-pk");
#endif

    /* Prompt for loop mode */
    printk("Run in loop mode? (y/n, default: n): ");
    ret = poll_response(input, sizeof(input));
    printk("Input: %s\n", input);
    
    if (input[0] == 'y' || input[0] == 'Y') {
        strcpy(loopOption, "-i");
        printk("Running in loop mode\n");
    }
    else {
        printk("Running once (default)\n");
    }
    
    /* Prompt for self-signed certificates */
    printk("Use self-signed certificates? (y/n, default: n): ");
    ret = poll_response(input, sizeof(input));
    printk("Input: %s\n", input);
    
    if (input[0] == 'y' || input[0] == 'Y') {
        strcpy(selfSign, "-self");
        printk("Using self-signed certificates\n");
    }
    else {
        printk("Using CA-signed certificates (default)\n");
    }
    
    /* Prompt for custom port */
    printk("Use custom port? (y/n, default: n): ");
    ret = poll_response(input, sizeof(input));
    printk("Input: %s\n", input);
    
    if (input[0] == 'y' || input[0] == 'Y') {
        char portNum[10];
        printk("Enter port number (1024-65535, default: 11111): ");
        ret = poll_response(portNum, sizeof(portNum));
        printk("Port: %s\n", portNum);
        
        /* Validate port is a number and in valid range */
        char* endptr;
        long port = strtol(portNum, &endptr, 10);
        if (*endptr != '\0' || port < 1024 || port > 65535) {
            printk("Invalid port number, using default (11111)\n");
        }
        else {
            snprintf(portOption, sizeof(portOption), "-p=%ld", port);
            printk("Using custom port: %ld\n", port);
        }
    }
    else {
        printk("Using default port (11111)\n");
    }
    
    /* Build argument array */
    argv[0] = "TPM2_TLS_Server";  /* Proper program name instead of placeholder */
    argc = 1;
    
    /* Add arguments to array only if they're set */
    if (strlen(keyType) > 0) {
        argv[argc++] = keyType;
    }
    if (strlen(paramEnc) > 0) {
        argv[argc++] = paramEnc;
    }
    if (strlen(pkOption) > 0) {
        argv[argc++] = pkOption;
    }
    if (strlen(loopOption) > 0) {
        argv[argc++] = loopOption;
    }
    if (strlen(selfSign) > 0) {
        argv[argc++] = selfSign;
    }
    if (strlen(portOption) > 0) {
        argv[argc++] = portOption;
    }
    
    printk("Starting TLS server with %d options\n", argc-1);
    

    /** SD card mount **/
    ret = mount_sd_card("SD");
    if (ret != 0) {
        printk("Failed to mount SD card\n");
        return -1;
    }

    ret = TPM2_TLS_ServerArgs(NULL, argc, argv);
    if (ret != 0) {
        printk("Server failed with return: %d\n", ret);
          /* Unmount SD card */
    }

    if (fs_unmount(&mp) != 0) {
        printk("Could not unmount file\n");
    }

    return ret;
}







/*
*  Note the fatfs library is able to mount only strings inside _VOLUME_STRS
*  in ffconf.h
*/
static const char *disk_mount_pt = "/SD:";


int mount_sd_card(char* disk_name)
{
/* raw disk i/o */
    int ret;
    uint64_t memory;
    uint32_t block_count;
    uint32_t block_size;
    static int init_sd_card = 1;

    if (init_sd_card) {
        ret = disk_access_init(disk_name);
        if (ret != 0) {
            printk("Disk Init Error\n");
            return ret;
        }
    }

    ret = disk_access_ioctl(disk_name, DISK_IOCTL_GET_SECTOR_COUNT,
                            &block_count);
    if (ret != 0) {
        printk("Unable to obtain sector count\n");
        return ret;
    }
    printk("Block count %u\n", block_count);

    ret = disk_access_ioctl(disk_name, DISK_IOCTL_GET_SECTOR_SIZE, &block_size);
    if (ret != 0) {
        printk("Unable to obtain the sector size\n");
        return ret;
    }
    printk("Sector size %u\n", block_size);


    memory = (uint64_t)block_count * block_size;
    printk("Memory Size(MB) %u\n", (uint32_t)(memory >> 20));

    mp.mnt_point = disk_mount_pt;

    ret = fs_mount(&mp);

    if (ret != 0) {
        printk("Failed to mount disk\n");
        return ret;
    }

    init_sd_card = 0;
    return 0;
}

/* Returns the length of the input */
int poll_response(char* input, int len)
{
    int ret = -1;
    char c;
    int pos = 0;
    const struct device *uart_dev = DEVICE_DT_GET(DT_NODELABEL(flexcomm4_lpuart4));
    // Clear input buffer
    memset(input, 0, len);
    while (1) {
        // Wait for character
        ret = -1;
        while (ret == -1) {
            ret = uart_poll_in(uart_dev, &c);
            if (ret != 0 && ret != -1) {
                printk("Failed to poll UART: %d\n", ret);
                return -1;
            }
        }
        // Handle Enter key (CR or LF)
        if (c == '\r' || c == '\n') {
            printk("\n");
            fflush(stdout);
            break;
        }
        // Handle backspace
        else if (c == '\b' || c == 127) {
            if (pos > 0) {
                pos--;
                printk("\b \b"); // Move back, print space, move back again
                fflush(stdout);
            }
        }
        // Handle regular characters
        else if (pos < len - 1) { // Leave space for null terminator
            input[pos++] = c;
            printk("%c", c); // Echo character
            fflush(stdout);
        }
    }

    // Ensure null termination
    input[pos] = '\0';
    return pos; // Return the actual length of input
}


int generateCSR(void)
{
    int ret = 0;
    char* argv[] = {" ", "-cert"};
    int argc = sizeof(argv) / sizeof(argv[0]);

    /** SD card mount **/
    ret = mount_sd_card("SD");
    if (ret != 0) {
        printk("Failed to mount SD card\n");
        return -1;
    }

    ret = TPM2_CSR_ExampleArgs(NULL, argc, argv);
    if (ret != 0) {
        printk("Csr Generation failed with return: %d", ret);
        ret = fs_unmount(&mp);
        if (ret != 0) {
            printk("Could not unmount file\n");
        }
        return ret;
    }

    ret = fs_unmount(&mp);
    if (ret != 0) {
        printk("Could not unmount file\n");
    }

    return ret;
}


int runBenchmark(void)
{
    int ret = 0;

    char input[5]; /* 4 characters + null terminator */
    char durationResponse[6] = "1000";
    char duration[20];
    char* argv[] = {" ", NULL, NULL};
    int argc = sizeof(argv) / sizeof(argv[0]);

    char* endptr;
    long duration_val;


    printk("\n%s", BENCHMARK_PROMPT);

    /* Flush any pending output before prompting */
    fflush(stdout);

    ret = poll_response(input, sizeof(input));
    printk("Input: %s\n", input);
    while (strcmp(input, "-aes") != 0 && strcmp(input, "-xor") != 0 \
            && strcmp(input, "-h") != 0) {
        printk("Invalid input\n");
        printk("%s", BENCHMARK_PROMPT);
        ret = poll_response(input, sizeof(input));
        printk("Input: %s\n", input);
    }

    /* If the user did not specify "-h", prompt for the duration */
    if (strcmp(input, "-h") != 0) {

        /* Flush any pending output before prompting */
        fflush(stdout);

        printk("%s", DURATION_PROMPT);
        ret = poll_response(durationResponse, sizeof(durationResponse));
        while (strcmp(durationResponse, "yes") != 0 && \
                        strcmp(durationResponse, "no") != 0) {
            printk("Invalid input\n");
            printk("%s", DURATION_PROMPT);
            ret = poll_response(durationResponse, sizeof(durationResponse));
            printk("Input: %s\n", durationResponse);
        }

        /* Flush any pending output before prompting */
        fflush(stdout);

        if (strcmp(durationResponse, "yes") == 0) {
            printk("Enter duration (1 - 99999 ms): ");
            ret = poll_response(duration, sizeof(duration));
            printk("Duration: %s\n", duration);
            /* Convert duration string to integer */
            duration_val = strtol(duration, &endptr, 10);
            /* Check if conversion was successful and value is in range */
            while (*endptr != '\0' || duration_val < 1 || duration_val > 99999) {
                printk("Invalid duration. Enter duration (1 - 99999 ms): ");
                ret = poll_response(duration, sizeof(duration));
                printk("Duration: %s\n", duration);
                duration_val = strtol(duration, &endptr, 10);
            }
            /* At this point, duration_val contains a valid integer between 1 and 99999 */
            printk("Using benchmark duration: %ld ms\n", duration_val);
        }

        /* Format duration string for benchmark if user specified a custom duration */
        if (strcmp(durationResponse, "yes") == 0) {
            /* Format the duration string as "-maxdur=<duration_val>" */
            snprintk(duration, sizeof(duration), "-maxdur=%ld", duration_val);
            printk("Setting benchmark parameter: %s\n", duration);
        }
        else {
            /* Use default duration */
            strcpy(duration, "-maxdur=1000");
            printk("Using default benchmark duration: 1000 ms\n");
        }

    }


    argv[1] = input;
    argv[2] = duration;
    ret = TPM2_Wrapper_BenchArgs(NULL, argc, argv);

    return ret;
}

int tpmClear(void)
{
    return TPM2_Clear_Tool(NULL, 0, NULL);
}

int runWrapTest(void)
{
    int ret = 0;
    char input[5]; /* 4 characters + null terminator */
    char* argv[] = {" ", NULL};
    int argc = sizeof(argv) / sizeof(argv[0]);

    printk("\n%s", WRAP_PROMPT);

    ret = poll_response(input, sizeof(input));
    printk("Input: %s\n", input);
    while (strcmp(input, "-aes") != 0 && strcmp(input, "-xor") != 0 \
            && strcmp(input, "-h") != 0) {
        printk("Invalid input\n");
        printk("%s", WRAP_PROMPT);
        ret = poll_response(input, sizeof(input));
        printk("Input: %s\n", input);
    }

    argv[1] = input;

    ret = tpmClear();
    if (ret != 0) {
        printk("Failed to clear TPM\n");
        return ret;
    }

    ret = TPM2_Wrapper_TestArgs(NULL, argc, argv);

    return ret;
}

int runNativeTest(void)
{
    return TPM2_Native_Test(NULL);
}


int runKeygen(void)
{
    int ret = 0;
    char input[64]; /* Buffer for user input */
    char keyType[10] = "-rsa"; /* Default key type */
    char paramEnc[10] = "";    /* Parameter encryption */
    char template[5] = "";     /* Template option */
    char extras[20] = "";      /* For additional options */
    char outputFile[128] = "keyblob.bin"; /* Default output filename */
    char* argv[7];             /* Arguments for keygen function (+1 for output file) */
    int argc = 3;              /* Number of arguments (program name, key type, and output file) */
    struct fs_dirent stat;     /* For checking if file exists */

    /* Prompt for key type */
    printk("\n%s\n", KEYGEN_PROMPT);
    printk("Select key type:\n");
    printk(" 1. RSA (default)\n");
    printk(" 2. ECC\n");
    printk(" 3. Symmetric\n");
    printk(" h. Help\n");
    printk("Enter choice: ");
    
    ret = poll_response(input, sizeof(input));
    printk("Input: %s\n", input);
    
    /* Process key type selection */
    if (input[0] == '2') {
        strcpy(keyType, "-ecc");
        printk("Selected ECC key\n");
    }
    else if (input[0] == '3') {
        strcpy(keyType, "-sym");
        printk("Selected Symmetric key\n");
    }
    else if (input[0] == 'h' || input[0] == 'H') {
        /* Call keygen with help flag */
        argv[0] = " ";
        argv[1] = "-h";
        return TPM2_Keygen_Example(NULL, 2, argv);
    }
    else {
        printk("Selected RSA key (default)\n");
    }

    /* Prompt for output file location */
    printk("Enter output file name (default: /SD:/keyblob.bin): ");
    ret = poll_response(input, sizeof(input));
    if (strlen(input) > 0) {
        strcpy(outputFile, input);
    }
    printk("Using output file: %s\n", outputFile);

    /* Prompt for template option */
    printk("Use default template? (y/n, default: n): ");
    ret = poll_response(input, sizeof(input));
    if (input[0] == 'y' || input[0] == 'Y') {
        strcpy(template, "-t");
        printk("Using default template\n");
        argc++;
    }
    else {
        printk("Using AIK template (default)\n");
    }

    /* Prompt for parameter encryption */
    printk("Use parameter encryption? (1=AES, 2=XOR, n=none, default: none): ");
    ret = poll_response(input, sizeof(input));
    if (input[0] == '1') {
        strcpy(paramEnc, "-aes");
        printk("Using AES parameter encryption\n");
        argc++;
    }
    else if (input[0] == '2') {
        strcpy(paramEnc, "-xor");
        printk("Using XOR parameter encryption\n");
        argc++;
    }
    else {
        printk("Not using parameter encryption\n");
    }

    /* Prompt for additional options */
    printk("Additional options (e.g., -eh for endorsement hierarchy, -pem for PEM output, or leave empty): ");
    ret = poll_response(extras, sizeof(extras));
    if (strlen(extras) > 0) {
        printk("Additional options: %s\n", extras);
        argc++;
    }

    /** SD card mount **/
    ret = mount_sd_card("SD");
    if (ret != 0) {
        printk("Failed to mount SD card\n");
        return -1;
    }
    
    /* Check and delete output file if it exists */
    /* Format path with mount point for Zephyr filesystem operations */
    char fullPath[256]; /* Significantly increased buffer size to handle longer paths */
    size_t required_size = strlen(disk_mount_pt) + strlen(outputFile) + 2; /* +2 for slash and null terminator */
    
    if (required_size > sizeof(fullPath)) {
        printk("Path too long for buffer: needed %zu bytes, have %zu bytes\n", 
               required_size, sizeof(fullPath));
        fs_unmount(&mp);
        return -1;
    }
    
    /* Now safely create the full path */
    snprintf(fullPath, sizeof(fullPath), "%s/%s", disk_mount_pt, outputFile);
    /* Check if file exists */
    if (fs_stat(fullPath, &stat) == 0) {
        printk("Output file %s exists. Removing...\n", outputFile);
        if (fs_unlink(fullPath) != 0) {
            printk("Failed to remove existing file\n");
            fs_unmount(&mp);
            return -1;
        }
        printk("Existing file removed successfully\n");
    }

    /* Set up arguments for keygen function */
    argv[0] = " ";           /* Program name placeholder */
    argv[1] = keyType;       /* Key type option */
    argv[2] = outputFile;    /* Output file location */
    
    int argIndex = 3;
    if (strlen(template) > 0) {
        argv[argIndex++] = template;
    }
    if (strlen(paramEnc) > 0) {
        argv[argIndex++] = paramEnc;
    }
    if (strlen(extras) > 0) {
        argv[argIndex++] = extras;
    }

    /* Call the keygen function */
    printk("Generating key...\n");
    ret = TPM2_Keygen_Example(NULL, argc, argv);
    
    if (ret != 0) {
        printk("Key generation failed with return code: %d\n", ret);
    }
    else {
        printk("Key generation completed successfully\n");
    }

    /* Unmount SD card */
    int unmount_ret = fs_unmount(&mp);
    if (unmount_ret != 0) {
        printk("Could not unmount SD card\n");
        if (ret == 0) {
            ret = unmount_ret; /* Only overwrite ret if it was successful */
        }
    }

    return ret;
}



void set_time_manually(int month, int day, int year, int hour, int minute, int second)
{
    struct tm time_info;
    time_t timestamp;
    struct timespec ts;
    
    // Convert to tm structure format
    time_info.tm_year = year - 1900;  // Years since 1900
    time_info.tm_mon = month - 1;     // Month (0-11)
    time_info.tm_mday = day;          // Day of month (1-31)
    time_info.tm_hour = hour;         // Hours (0-23)
    time_info.tm_min = minute;        // Minutes (0-59)
    time_info.tm_sec = second;        // Seconds (0-59)
    time_info.tm_isdst = -1;          // Let the system determine DST
    
    // Convert to Unix timestamp
    timestamp = timeutil_timegm(&time_info);
    
    ts.tv_sec = timestamp;
    ts.tv_nsec = 0;
    
    clock_settime(CLOCK_REALTIME, &ts);
    printk("System time set manually to %04d-%02d-%02d %02d:%02d:%02d\n", 
           year, month, day, hour, minute, second);
}

void setTime(void)
{
    char input[16];
    int month, day, year, hour, minute, second;
    int max_days;
    int i;
    bool valid;
    bool leap_year;

    printk("\n=== Manual Time Setting ===\n");

    /* Get month */
    valid = false;
    while (!valid) {
        printk("Enter month (1-12): ");
        poll_response(input, sizeof(input));
        
        valid = true;
        /* Check if input is a valid integer */
        for (i = 0; input[i] != '\0' && i < sizeof(input); i++) {
            if (input[i] < '0' || input[i] > '9') {
                valid = false;
                break;
            }
        }
        
        if (valid) {
            month = atoi(input);
            valid = (month >= 1 && month <= 12);
        }
        
        if (!valid) {
            printk("Invalid month. Please enter a value between 1 and 12.\n");
        }
    }

    /* Get day */
    valid = false;
    while (!valid) {
        printk("Enter day (1-31): ");
        poll_response(input, sizeof(input));
        
        valid = true;
        /* Check if input is a valid integer */
        for (i = 0; input[i] != '\0' && i < sizeof(input); i++) {
            if (input[i] < '0' || input[i] > '9') {
                valid = false;
                break;
            }
        }
        
        if (valid) {
            day = atoi(input);
            valid = (day >= 1 && day <= 31);
        }
        
        if (!valid) {
            printk("Invalid day. Please enter a value between 1 and 31.\n");
        }
    }

    /* Get year */
    valid = false;
    while (!valid) {
        printk("Enter year (2000-2099): ");
        poll_response(input, sizeof(input));
        
        valid = true;
        /* Check if input is a valid integer */
        for (i = 0; input[i] != '\0' && i < sizeof(input); i++) {
            if (input[i] < '0' || input[i] > '9') {
                valid = false;
                break;
            }
        }
        
        if (valid) {
            year = atoi(input);
            valid = (year >= 2000 && year <= 2099);
        }
        
        if (!valid) {
            printk("Invalid year. Please enter a value between 2000 and 2099.\n");
        }
    }

    /* Validate date further for month-specific day ranges */
    leap_year = ((year % 4 == 0 && year % 100 != 0) || year % 400 == 0);
    
    switch (month) {
        case 4: case 6: case 9: case 11:
            max_days = 30;
            break;
        case 2:
            max_days = leap_year ? 29 : 28;
            break;
        default:
            max_days = 31;
    }
    
    if (day > max_days) {
        printk("Invalid date: %d-%02d-%d has at most %d days. Setting to last day of month.\n", 
               year, month, day, max_days);
        day = max_days;
    }

    /* Get hour */
    valid = false;
    while (!valid) {
        printk("Enter hour (0-23): ");
        poll_response(input, sizeof(input));
        
        valid = true;
        /* Check if input is a valid integer */
        for (i = 0; input[i] != '\0' && i < sizeof(input); i++) {
            if (input[i] < '0' || input[i] > '9') {
                valid = false;
                break;
            }
        }
        
        if (valid) {
            hour = atoi(input);
            valid = (hour >= 0 && hour <= 23);
        }
        
        if (!valid) {
            printk("Invalid hour. Please enter a value between 0 and 23.\n");
        }
    }

    /* Get minute */
    valid = false;
    while (!valid) {
        printk("Enter minute (0-59): ");
        poll_response(input, sizeof(input));
        
        valid = true;
        /* Check if input is a valid integer */
        for (i = 0; input[i] != '\0' && i < sizeof(input); i++) {
            if (input[i] < '0' || input[i] > '9') {
                valid = false;
                break;
            }
        }
        
        if (valid) {
            minute = atoi(input);
            valid = (minute >= 0 && minute <= 59);
        }
        
        if (!valid) {
            printk("Invalid minute. Please enter a value between 0 and 59.\n");
        }
    }

    /* Get second */
    valid = false;
    while (!valid) {
        printk("Enter second (0-59): ");
        poll_response(input, sizeof(input));
        
        valid = true;
        /* Check if input is a valid integer */
        for (i = 0; input[i] != '\0' && i < sizeof(input); i++) {
            if (input[i] < '0' || input[i] > '9') {
                valid = false;
                break;
            }
        }
        
        if (valid) {
            second = atoi(input);
            valid = (second >= 0 && second <= 59);
        }
        
        if (!valid) {
            printk("Invalid second. Please enter a value between 0 and 59.\n");
        }
    }

    /* Confirm setting time */
    printk("\nSetting time to: %04d-%02d-%02d %02d:%02d:%02d\n", 
           year, month, day, hour, minute, second);
    printk("Press 'y' to confirm, any other key to cancel: ");
    poll_response(input, sizeof(input));
    
    if (input[0] == 'y' || input[0] == 'Y') {
        set_time_manually(month, day, year, hour, minute, second);
        printk("Time set successfully!\n");
    }
    else {
        printk("Time setting canceled.\n");
    }
}

int fullDemo(void)
{
    int ret = 0;
    char input[2];
    int regenerate_cert = 0;  /* Using int instead of bool: 0=false, 1=true */
    int unmount_ret;
    
    /* For keygen */
    char* keygen_argv[] = {" ", "/SD:/rsa.raw", "-rsa", "-t", "-aes", "-pem"};
    int keygen_argc = sizeof(keygen_argv) / sizeof(keygen_argv[0]);

    /* For CSR - matching the exact pattern in generateCSR() */
    char* csr_argv[] = {" ", "-cert"};
    int csr_argc = sizeof(csr_argv) / sizeof(csr_argv[0]);
    
    /* For server */
    char* server_argv[] = {" ", "-rsa", "-aes", "-pk", "-self", RSA_FILENAME};
    int server_argc = sizeof(server_argv) / sizeof(server_argv[0]);

    printk("\n=== Running Full Demo ===\n");

        /* Mount SD card */
    ret = mount_sd_card("SD");
    if (ret != 0) {
        printk("Failed to mount SD card. Aborting demo.\n");
        return ret;
    }

    /* Prompt user if they want to regenerate the certificate */
    printk("Do you want to regenerate the certificate? (y/n, default: n): ");
    poll_response(input, sizeof(input));
    
    if (input[0] == 'y' || input[0] == 'Y') {
        regenerate_cert = 1;
    }
    else {
        regenerate_cert = 0;
    }

    if (regenerate_cert) {
        printk("\n--- Starting Key Generation ---\n");
        printk("Generating RSA key with AES parameter encryption and PEM output...\n");

        /* Call the keygen function */
        printk("Generating key...\n");
        ret = TPM2_Keygen_Example(NULL, keygen_argc, keygen_argv);
        
        if (ret != 0) {
            printk("Key generation failed with return code: %d\n", ret);
            /* Unmount SD card before returning */
            unmount_ret = fs_unmount(&mp);
            if (unmount_ret != 0) {
                printk("Could not unmount SD card\n");
            }
            return ret;
        }
        else {
            printk("Key generation completed successfully\n");
        }

        printk("\n--- Generating Certificate ---\n");
        
        /* Call the CSR generation function using the exact pattern from generateCSR() */
        ret = TPM2_CSR_ExampleArgs(NULL, csr_argc, csr_argv);
        
        if (ret != 0) {
            printk("Certificate generation failed with return code: %d\n", ret);
            /* Unmount SD card before returning */
            unmount_ret = fs_unmount(&mp);
            if (unmount_ret != 0) {
                printk("Could not unmount SD card\n");
            }
            return ret;
        }
        else {
            printk("Certificate generation completed successfully\n");
        }
    }
    
    /* Step 3: Start the TLS server */
    printk("\n--- Starting TLS Server ---\n");
    printk("Using RSA key, AES parameter encryption, PK callbacks, self-signed certificates\n");
    
    /* Call the server function */
    printk("Starting TLS server...\n");
    ret = TPM2_TLS_ServerArgs(NULL, server_argc, server_argv);
    
    if (ret != 0) {
        printk("Server execution failed with return code: %d\n", ret);
    }
    else {
        printk("Server execution completed successfully\n");
    }
    
    /* Unmount SD card */
    unmount_ret = fs_unmount(&mp);
    if (unmount_ret != 0) {
        printk("Could not unmount SD card\n");
        if (ret == 0) {
            ret = unmount_ret; /* Only overwrite ret if it was successful */
        }
    }
    
    printk("\n=== Full Demo Completed ===\n");
    return ret;
}

