/* fwclient.c
 *
 * Copyright (C) 2006-2025 wolfSSL Inc.
 *
 * This file is part of wolfMQTT.
 *
 * wolfMQTT is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 3 of the License, or
 * (at your option) any later version.
 *
 * wolfMQTT is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA 02110-1335, USA
 */

/* Include the autoconf generated config.h */
#ifdef HAVE_CONFIG_H
    #include <config.h>
#endif

#include "wolfmqtt/mqtt_client.h"

/* This example only works with ENABLE_MQTT_TLS (wolfSSL library). */
#if defined(ENABLE_MQTT_TLS)
    #if !defined(WOLFSSL_USER_SETTINGS) && !defined(USE_WINDOWS_API)
        #include <wolfssl/options.h>
    #endif
    #include <wolfssl/wolfcrypt/settings.h>
    #include <wolfssl/version.h>

    /* Signature wrapper required; added in wolfSSL after 3.7.1.
     * app and libwolfssl use wolfssl-setting/user_settings.h (no NO_SIG_WRAPPER). */
    #if defined(LIBWOLFSSL_VERSION_HEX) && LIBWOLFSSL_VERSION_HEX > 0x03007001 \
            && defined(HAVE_ECC)
        #undef  ENABLE_FIRMWARE_SIG
        #define ENABLE_FIRMWARE_SIG
    #endif
#endif


#if defined(ENABLE_FIRMWARE_SIG)
#include <wolfssl/ssl.h>
#include <wolfssl/wolfcrypt/ecc.h>
#include <wolfssl/wolfcrypt/signature.h>
#include <wolfssl/wolfcrypt/hash.h>
#endif

#include "fwclient.h"
#include "firmware.h"
#include "mqttexample.h"
#include "mqttnet.h"

#if defined(WOLFMQTT_ZEPHYR)
#include <stdint.h>
#include <zephyr/kernel.h>
#include "wolfboot/wolfboot.h"
#include "wolfboot_status.h"
#define SLOT1_NODE DT_NODELABEL(slot1_partition)
#define SLOT1_OFFSET DT_REG_ADDR(SLOT1_NODE)
#define SLOT1_SIZE DT_REG_SIZE(SLOT1_NODE)
#define SLOT1_WRITE_BLOCK_SIZE DT_PROP(DT_CHOSEN(zephyr_flash), write_block_size)
/* The MCXN947 ROM API flash_program requires 512-byte aligned start address
 * (kFLASH_AlignementUnitProgram = 512). CONFIG_FLASH_FILL_BUFFER_SIZE is set
 * to 16 by Zephyr when CONFIG_FLASH=n, which is too small for the ROM API. */
#define WOLFBOOT_FLASH_PROGRAM_ALIGN 512U
#if defined(CONFIG_FLASH_FILL_BUFFER_SIZE) && \
    (CONFIG_FLASH_FILL_BUFFER_SIZE >= WOLFBOOT_FLASH_PROGRAM_ALIGN)
    #define FLASH_WRITE_BLOCK_MAX CONFIG_FLASH_FILL_BUFFER_SIZE
#else
    #define FLASH_WRITE_BLOCK_MAX WOLFBOOT_FLASH_PROGRAM_ALIGN
#endif
#endif

/* Configuration */
#ifndef MAX_BUFFER_SIZE
#define MAX_BUFFER_SIZE         FIRMWARE_MAX_PACKET
#endif

/* Locals */
static int mStopRead = 0;
static int mTestDone = 0;
static byte mMsgBuf[FIRMWARE_MAX_BUFFER] __attribute__((aligned(16)));
static int mSubCommandComplete = 0;
static byte mMsgIsCommand = 0;

static void fw_transfer_reset(void);
static int fwfile_save(const byte* fileBuf, int fileLen, word32 flash_offset);

static int fw_topic_matches(const MqttMessage* msg, const char* topic)
{
    word32 topic_len;

    if (msg == NULL || topic == NULL) {
        return 0;
    }

    topic_len = (word32)XSTRLEN(topic);
    if (msg->topic_name_len != topic_len) {
        return 0;
    }

    return (XSTRNCMP(msg->topic_name, topic, topic_len) == 0);
}

static int fw_command_process(const byte* buffer, word32 len)
{
    const CommandHeader* command;

    if (buffer == NULL || len < sizeof(CommandHeader)) {
        PRINTF("Command message too small: %u", len);
        return EXIT_FAILURE;
    }

    command = (const CommandHeader*)buffer;

    switch (command->commandId) {
        case COMMAND_ID_ERASE:
        {
#if defined(WOLFMQTT_ZEPHYR)
            int rc;
            unsigned int irq_key;

            PRINTF("Erase command received. Erasing update partition...");
            PRINTF("OTA slot1 layout: offset=0x%08x size=0x%08x write_block=%u",
                (unsigned int)SLOT1_OFFSET,
                (unsigned int)SLOT1_SIZE,
                (unsigned int)SLOT1_WRITE_BLOCK_SIZE);
            PRINTF("Erase request: rel_off=0x%08x len=0x%08x abs_start=0x%08x",
                0U,
                (unsigned int)SLOT1_SIZE,
                (unsigned int)SLOT1_OFFSET);
            fw_transfer_reset();

            k_sched_lock();
            irq_key = irq_lock();
            rc = wolfBoot_nsc_erase_update(0U, (uint32_t)SLOT1_SIZE);
            irq_unlock(irq_key);
            k_sched_unlock();

            if (rc != 0) {
                PRINTF("Flash erase failed! rc=%d (slot1_off=0x%08x slot1_size=0x%08x)",
                    rc,
                    (unsigned int)SLOT1_OFFSET,
                    (unsigned int)SLOT1_SIZE);
                return EXIT_FAILURE;
            }
            PRINTF("Update partition erased");
#else
            PRINTF("Erase command received (ignored on this target)");
#endif
            break;
        }

        default:
            PRINTF("Unknown command id: %u", command->commandId);
            break;
    }

    return 0;
}

typedef struct FwClientTransfer_s {
    word32 total_len;
    word32 bytes_written;
    word16 expected_chunk;
    int active;
#if !defined(WOLFMQTT_ZEPHYR)
    FILE* fp;
#else
    word32 flash_written;
    word32 write_block_size;
    word32 pending_len;
    byte pending[FLASH_WRITE_BLOCK_MAX];
#endif
} FwClientTransfer;

static FwClientTransfer mTransfer;

static void fw_transfer_reset(void)
{
#if !defined(WOLFMQTT_ZEPHYR)
    if (mTransfer.fp != NULL) {
        fclose(mTransfer.fp);
        mTransfer.fp = NULL;
    }
#endif
    XMEMSET(&mTransfer, 0, sizeof(mTransfer));
}

static int fw_transfer_begin(MQTTCtx* mqttCtx, word32 total_len)
{
    if (mqttCtx == NULL || total_len == 0) {
        return EXIT_FAILURE;
    }

    fw_transfer_reset();
    mTransfer.total_len = total_len;
    mTransfer.active = 1;

#if !defined(WOLFMQTT_ZEPHYR)
    mTransfer.fp = fopen(mqttCtx->pub_file, "wb");
    if (mTransfer.fp == NULL) {
        PRINTF("File %s open error", mqttCtx->pub_file);
        fw_transfer_reset();
        return EXIT_FAILURE;
    }
#else
    int rc = EXIT_SUCCESS;

    if (total_len > SLOT1_SIZE) {
        PRINTF("Firmware image too large for slot1! len=%u slot=%u",
            total_len, (unsigned int)SLOT1_SIZE);
        fw_transfer_reset();
        return EXIT_FAILURE;
    }

    if (rc == EXIT_SUCCESS) {
        mTransfer.write_block_size = FLASH_WRITE_BLOCK_MAX;
        mTransfer.flash_written = 0;
        mTransfer.pending_len = 0;

        if (mTransfer.write_block_size == 0 ||
            mTransfer.write_block_size > FLASH_WRITE_BLOCK_MAX) {
            PRINTF("Unsupported flash write block size: %u", mTransfer.write_block_size);
            rc = EXIT_FAILURE;
        }
    }

    if (rc != EXIT_SUCCESS) {
        fw_transfer_reset();
        return EXIT_FAILURE;
    }
#endif

    PRINTF("Firmware transfer started: total %u bytes", total_len);
    return 0;
}

static int fw_transfer_write_chunk(const byte* chunk_data, word16 chunk_len)
{
#if !defined(WOLFMQTT_ZEPHYR)
    int written;
#else
    int rc = 0;
    const byte* p = chunk_data;
    word32 remaining = chunk_len;
    word32 copy_len;
    word32 direct_len;
#endif


    if (!mTransfer.active || chunk_data == NULL || chunk_len == 0) {
        return EXIT_FAILURE;
    }

    if (((word32)chunk_len > mTransfer.total_len) ||
        (mTransfer.bytes_written > (mTransfer.total_len - (word32)chunk_len))) {
        PRINTF("Chunk exceeds expected total length");
        return EXIT_FAILURE;
    }

#if !defined(WOLFMQTT_ZEPHYR)
    written = (int)fwrite(chunk_data, 1, chunk_len, mTransfer.fp);
    if (written != chunk_len) {
        PRINTF("Chunk file write error: %d", written);
        return EXIT_FAILURE;
    }
#else
    if (mTransfer.pending_len > 0) {
        copy_len = mTransfer.write_block_size - mTransfer.pending_len;
        if (copy_len > remaining) {
            copy_len = remaining;
        }

        XMEMCPY(&mTransfer.pending[mTransfer.pending_len], p, copy_len);
        mTransfer.pending_len += copy_len;
        p += copy_len;
        remaining -= copy_len;

        if (mTransfer.pending_len == mTransfer.write_block_size) {
            rc = fwfile_save(mTransfer.pending, (int)mTransfer.pending_len,
                mTransfer.flash_written);
            if (rc != 0) {
                PRINTF("Chunk file save error: %d", rc);
                return EXIT_FAILURE;
            }
            mTransfer.flash_written += mTransfer.pending_len;
            mTransfer.pending_len = 0;
        }
    }

    direct_len = remaining - (remaining % mTransfer.write_block_size);
    if (direct_len > 0) {
        rc = fwfile_save(p, (int)direct_len, mTransfer.flash_written);
        if (rc != 0) {
            PRINTF("Chunk file save error: %d", rc);
            return EXIT_FAILURE;
        }
        mTransfer.flash_written += direct_len;
        p += direct_len;
        remaining -= direct_len;
    }

    if (remaining > 0) {
        XMEMCPY(mTransfer.pending, p, remaining);
        mTransfer.pending_len = remaining;
    }
#endif

    mTransfer.bytes_written += chunk_len;

    return 0;
}

static int fw_transfer_finish(MQTTCtx* mqttCtx)
{
    int rc = 0;

    if (mqttCtx == NULL) {
        return EXIT_FAILURE;
    }

#if defined(WOLFMQTT_ZEPHYR)
    if (mTransfer.pending_len > 0) {
        byte aligned_buf[FLASH_WRITE_BLOCK_MAX];

        XMEMSET(aligned_buf, 0xFF, mTransfer.write_block_size);
        XMEMCPY(aligned_buf, mTransfer.pending, mTransfer.pending_len);

        rc = fwfile_save(aligned_buf, (int)mTransfer.write_block_size,
            mTransfer.flash_written);
        if (rc != 0) {
            PRINTF("Final chunk flush error: %d", rc);
            fw_transfer_reset();
            return EXIT_FAILURE;
        }
        mTransfer.flash_written += mTransfer.write_block_size;
        mTransfer.pending_len = 0;
    }
#endif

    PRINTF("Firmware transfer complete: %u bytes", mTransfer.bytes_written);
    fw_transfer_reset();

    if (mqttCtx->test_mode) {
        mTestDone = 1;
    } else {
        mStopRead = 1;
    }

    return 0;
}


static int fwfile_save(const byte* fileBuf, int fileLen, word32 flash_offset)
{
    int rc = EXIT_SUCCESS;
#if defined(WOLFMQTT_ZEPHYR)
    word32 write_block_size = mTransfer.write_block_size;
    unsigned int irq_key;

    if (fileBuf == NULL || fileLen <= 0) {
        PRINTF("Invalid firmware file buffer or length!");
        rc = EXIT_FAILURE;
    }

    if (rc == EXIT_SUCCESS &&
        ((word32)fileLen > SLOT1_SIZE || flash_offset > (SLOT1_SIZE - (word32)fileLen))) {
        PRINTF("Firmware chunk write exceeds slot1: off=%u len=%d slot=%u",
            flash_offset, fileLen, (unsigned int)SLOT1_SIZE);
        rc = EXIT_FAILURE;
    }

    if (rc == EXIT_SUCCESS &&
        ((flash_offset % write_block_size) != 0 ||
         ((word32)fileLen % write_block_size) != 0)) {
        PRINTF("Flash write alignment error: off=%u len=%d block=%u",
            flash_offset, fileLen, (unsigned int)write_block_size);
        rc = EXIT_FAILURE;
    }

    if (rc == EXIT_SUCCESS) {
        k_sched_lock();
        irq_key = irq_lock();

        /* Write firmware file to flash through HAL */
        rc = wolfBoot_nsc_write_update((uint32_t)(flash_offset),
            (const uint8_t*)fileBuf, fileLen);

        irq_unlock(irq_key);
        k_sched_unlock();
        if (rc != 0) {
            PRINTF("Flash write failed! rc=%d rel_off=0x%08x len=0x%08x abs=0x%08x",
                rc,
                (unsigned int)flash_offset,
                (unsigned int)fileLen,
                (unsigned int)(SLOT1_OFFSET + flash_offset));
        }
    }

    if (rc == EXIT_SUCCESS) {
        PRINTF("Firmware File Saved to Flash: Len=%d", fileLen);
    }
#else
    PRINTF("Firmware File Save: Len=%d (No Filesystem)", fileLen);
#endif
    return rc;
}

static int fw_message_process(MQTTCtx *mqttCtx, const byte* buffer, word32 len)
{
    const MessageHeader* header;
    const byte* payload;
    word32 payload_len;

    if (mqttCtx == NULL || buffer == NULL) {
        return EXIT_FAILURE;
    }

    if (len < sizeof(MessageHeader)) {
        PRINTF("Chunk too small: %u", len);
        return EXIT_FAILURE;
    }

    header = (const MessageHeader*)buffer;
    payload = buffer + sizeof(MessageHeader);
    payload_len = len - sizeof(MessageHeader);

    if (header->chunkSize != payload_len) {
        PRINTF("Chunk size mismatch: header %u, payload %u",
            header->chunkSize, payload_len);
        return EXIT_FAILURE;
    }

    if (header->totalLen == 0) {
        PRINTF("Invalid total length 0");
        return EXIT_FAILURE;
    }

    if (header->chunkNumber == 0) {
        if (fw_transfer_begin(mqttCtx, header->totalLen) != 0) {
            return EXIT_FAILURE;
        }
    }

    if (!mTransfer.active) {
        PRINTF("Received chunk without active transfer");
        return EXIT_FAILURE;
    }

    if (header->totalLen != mTransfer.total_len) {
        PRINTF("Transfer total length changed: %u -> %u",
            mTransfer.total_len, header->totalLen);
        fw_transfer_reset();
        return EXIT_FAILURE;
    }

    if (header->chunkNumber < mTransfer.expected_chunk) {
        /* Duplicate chunk from retransmit; ignore if already committed. */
        PRINTF("Ignoring duplicate chunk %u", header->chunkNumber);
        return 0;
    }

    if (header->chunkNumber != mTransfer.expected_chunk) {
        PRINTF("Out-of-order chunk: expected %u, got %u",
            mTransfer.expected_chunk, header->chunkNumber);
        fw_transfer_reset();
        return EXIT_FAILURE;
    }

    if (fw_transfer_write_chunk(payload, header->chunkSize) != 0) {
        fw_transfer_reset();
        return EXIT_FAILURE;
    }

    PRINTF("Firmware chunk %u: %u bytes (%u/%u)",
        header->chunkNumber,
        header->chunkSize,
        mTransfer.bytes_written,
        mTransfer.total_len);

    mTransfer.expected_chunk++;

    if (mTransfer.bytes_written == mTransfer.total_len) {
        return fw_transfer_finish(mqttCtx);
    }

    return 0;
}

static int mqtt_message_cb(MqttClient *client, MqttMessage *msg,
    byte msg_new, byte msg_done)
{
    MQTTCtx* mqttCtx = (MQTTCtx*)client->ctx;
    byte is_fw_topic;
    byte is_cmd_topic;

    is_fw_topic = (byte)fw_topic_matches(msg, FIRMWARE_TOPIC_NAME);
    is_cmd_topic = (byte)fw_topic_matches(msg, COMMAND_TOPIC_NAME);

    if (msg_new) {
        if (!is_fw_topic && !is_cmd_topic) {
            return MQTT_CODE_SUCCESS;
        }

        if (msg->total_len > sizeof(mMsgBuf)) {
            PRINTF("Incoming publish exceeds firmware message buffer: %u",
                msg->total_len);
            return MQTT_CODE_ERROR_OUT_OF_BUFFER;
        }

        mMsgIsCommand = is_cmd_topic;

        if (is_cmd_topic) {
            PRINTF("MQTT Command Message: Qos %d, Len %u",
                msg->qos, msg->total_len);
        }
        else {
            PRINTF("MQTT Firmware Chunk Message: Qos %d, Len %u",
                msg->qos, msg->total_len);
        }
    }

    if (msg->buffer_pos > sizeof(mMsgBuf)) {
        PRINTF("Incoming payload position exceeds message buffer");
        return MQTT_CODE_ERROR_MALFORMED_DATA;
    }

    if (msg->buffer_len > (sizeof(mMsgBuf) - msg->buffer_pos)) {
        PRINTF("Incoming payload chunk exceeds message buffer");
        return MQTT_CODE_ERROR_MALFORMED_DATA;
    }

    XMEMCPY(&mMsgBuf[msg->buffer_pos], msg->buffer, msg->buffer_len);

    if (msg_done) {
        if (mMsgIsCommand) {
            if (fw_command_process(mMsgBuf, msg->total_len) != 0) {
                return MQTT_CODE_ERROR_MALFORMED_DATA;
            }
        }
        else {
            if (fw_message_process(mqttCtx, mMsgBuf, msg->total_len) != 0) {
                return MQTT_CODE_ERROR_MALFORMED_DATA;
            }
        }

        mMsgIsCommand = 0;
    }

    /* Return negative to terminate publish processing */
    return MQTT_CODE_SUCCESS;
}

int fwclient_test(MQTTCtx *mqttCtx)
{
    int rc = MQTT_CODE_SUCCESS, i;

    switch(mqttCtx->stat) {
        case WMQ_BEGIN:
        {
            PRINTF("MQTT Firmware Client: QoS %d, Use TLS %d", mqttCtx->qos, mqttCtx->use_tls);
        }
        FALL_THROUGH;

        case WMQ_NET_INIT:
        {
            mqttCtx->stat = WMQ_NET_INIT;

            /* Initialize Network */
            rc = MqttClientNet_Init(&mqttCtx->net, mqttCtx);
            if (rc == MQTT_CODE_CONTINUE) {
                return rc;
            }
            PRINTF("MQTT Net Init: %s (%d)",
                MqttClient_ReturnCodeToString(rc), rc);
            if (rc != MQTT_CODE_SUCCESS) {
                goto exit;
            }

            /* setup tx/rx buffers */
            mqttCtx->tx_buf = (byte*)WOLFMQTT_MALLOC(MAX_BUFFER_SIZE);
            mqttCtx->rx_buf = (byte*)WOLFMQTT_MALLOC(MAX_BUFFER_SIZE);
        }
        FALL_THROUGH;

        case WMQ_INIT:
        {
            mqttCtx->stat = WMQ_INIT;

            /* Initialize MqttClient structure */
            rc = MqttClient_Init(&mqttCtx->client, &mqttCtx->net,
                mqtt_message_cb,
                mqttCtx->tx_buf, MAX_BUFFER_SIZE,
                mqttCtx->rx_buf, MAX_BUFFER_SIZE,
                mqttCtx->cmd_timeout_ms);
            if (rc == MQTT_CODE_CONTINUE) {
                return rc;
            }
            PRINTF("MQTT Init: %s (%d)",
                MqttClient_ReturnCodeToString(rc), rc);
            if (rc != MQTT_CODE_SUCCESS) {
                goto exit;
            }
            mqttCtx->client.ctx = mqttCtx;
        }
        FALL_THROUGH;

        case WMQ_TCP_CONN:
        {
            mqttCtx->stat = WMQ_TCP_CONN;

            /* Connect to broker */
            rc = MqttClient_NetConnect(&mqttCtx->client, mqttCtx->host,
                mqttCtx->port, DEFAULT_CON_TIMEOUT_MS,
                mqttCtx->use_tls, mqtt_tls_cb);
            if (rc == MQTT_CODE_CONTINUE) {
                return rc;
            }
            PRINTF("MQTT Socket Connect: %s (%d)",
                MqttClient_ReturnCodeToString(rc), rc);
            if (rc != MQTT_CODE_SUCCESS) {
                goto exit;
            }

            /* Build connect packet */
            XMEMSET(&mqttCtx->connect, 0, sizeof(MqttConnect));
            mqttCtx->connect.keep_alive_sec = mqttCtx->keep_alive_sec;
            mqttCtx->connect.clean_session = mqttCtx->clean_session;
            mqttCtx->connect.client_id = mqttCtx->client_id;
            if (mqttCtx->enable_lwt) {
                /* Send client id in LWT payload */
                mqttCtx->lwt_msg.qos = mqttCtx->qos;
                mqttCtx->lwt_msg.retain = 0;
                mqttCtx->lwt_msg.topic_name = FIRMWARE_TOPIC_NAME"lwttopic";
                mqttCtx->lwt_msg.buffer = (byte*)mqttCtx->client_id;
                mqttCtx->lwt_msg.total_len = (word16)XSTRLEN(mqttCtx->client_id);
            }

            /* Optional authentication */
            mqttCtx->connect.username = mqttCtx->username;
            mqttCtx->connect.password = mqttCtx->password;
        }
        FALL_THROUGH;

        case WMQ_MQTT_CONN:
        {
            mqttCtx->stat = WMQ_MQTT_CONN;

            /* Send Connect and wait for Connect Ack */
            rc = MqttClient_Connect(&mqttCtx->client, &mqttCtx->connect);
            if (rc == MQTT_CODE_CONTINUE) {
                return rc;
            }
            PRINTF("MQTT Connect: Proto (%s), %s (%d)",
                MqttClient_GetProtocolVersionString(&mqttCtx->client),
                MqttClient_ReturnCodeToString(rc), rc);

            /* Validate Connect Ack info */
            PRINTF("MQTT Connect Ack: Return Code %u, Session Present %d",
                mqttCtx->connect.ack.return_code,
                (mqttCtx->connect.ack.flags & MQTT_CONNECT_ACK_FLAG_SESSION_PRESENT) ?
                    1 : 0
            );
            if (rc != MQTT_CODE_SUCCESS) {
                goto disconn;
            }

            mSubCommandComplete = 0;

            /* Build list of topics */
            mqttCtx->topics[0].topic_filter = COMMAND_TOPIC_NAME;
            mqttCtx->topics[0].qos = mqttCtx->qos;

            /* Subscribe Topic */
            XMEMSET(&mqttCtx->subscribe, 0, sizeof(MqttSubscribe));
            mqttCtx->subscribe.packet_id = mqtt_get_packetid();
            mqttCtx->subscribe.topic_count = 1;
            mqttCtx->subscribe.topics = mqttCtx->topics;
        }
        FALL_THROUGH;

        case WMQ_SUB:
        {
            mqttCtx->stat = WMQ_SUB;

            rc = MqttClient_Subscribe(&mqttCtx->client, &mqttCtx->subscribe);
            if (rc == MQTT_CODE_CONTINUE) {
                return rc;
            }
            PRINTF("MQTT Subscribe: %s (%d)",
                MqttClient_ReturnCodeToString(rc), rc);

            if (rc != MQTT_CODE_SUCCESS) {
                goto disconn;
            }
            for (i = 0; i < mqttCtx->subscribe.topic_count; i++) {
                MqttTopic *topic = &mqttCtx->subscribe.topics[i];
                PRINTF("  Topic %s, Qos %u, Return Code %u",
                    topic->topic_filter,
                    topic->qos,
                    topic->return_code);
            }

            if (!mSubCommandComplete) {
                mSubCommandComplete = 1;
                mqttCtx->topics[0].topic_filter = mqttCtx->topic_name;
                mqttCtx->subscribe.packet_id = mqtt_get_packetid();
                PRINTF("MQTT Subscribing to firmware topic...");
                return MQTT_CODE_CONTINUE;
            }

            /* Read Loop */
            PRINTF("MQTT Waiting for message...");

            {
                static int s_status_published = 0;
                if (!s_status_published) {
                    static char status_buf[192];
                    static MqttPublish status_pub;
                    int n;
                    int prc;

                    s_status_published = 1;
                    n = snprintf(status_buf, sizeof(status_buf),
                        "{\"boot\":%u,\"update\":%u,\"state\":\"%s\","
                        "\"raw\":%u,\"rc\":%d}",
                        (unsigned)g_boot_version,
                        (unsigned)g_update_version,
                        boot_state_name(g_boot_state),
                        (unsigned)g_update_state_byte,
                        g_update_state_rc);
                    XMEMSET(&status_pub, 0, sizeof(status_pub));
                    status_pub.qos        = MQTT_QOS_0;
                    status_pub.retain     = 1;
                    status_pub.topic_name = "wolfMQTT/example/status";
                    status_pub.packet_id  = mqtt_get_packetid();
                    status_pub.buffer     = (byte *)status_buf;
                    status_pub.total_len  = (word32)n;
                    prc = MqttClient_Publish(&mqttCtx->client, &status_pub);
                    PRINTF("MQTT Status Publish: %s (%d) -> %s",
                           MqttClient_ReturnCodeToString(prc), prc,
                           status_buf);
                }
            }
        }
        FALL_THROUGH;

        case WMQ_WAIT_MSG:
        {
            mqttCtx->stat = WMQ_WAIT_MSG;

            do {
                /* Try and read packet */
                rc = MqttClient_WaitMessage(&mqttCtx->client,
                                                  mqttCtx->cmd_timeout_ms);

            #ifdef WOLFMQTT_NONBLOCK
                /* Track elapsed time with no activity and trigger timeout */
                rc = mqtt_check_timeout(rc, &mqttCtx->start_sec,
                    mqttCtx->cmd_timeout_ms/1000);
            #endif

                /* check return code */
                if (rc == MQTT_CODE_CONTINUE) {
                    return rc;
                }

                /* check for test mode */
                if (mStopRead || mTestDone) {
                    rc = MQTT_CODE_SUCCESS;
                    mqttCtx->stat = WMQ_DISCONNECT;
                    PRINTF("MQTT Exiting...");
                    break;
                }

                if (rc == MQTT_CODE_ERROR_TIMEOUT) {
                    if (mqttCtx->test_mode) {
                        PRINTF("Timeout in test mode, exit early!");
                        mTestDone = 1;
                    }
                    /* Keep Alive */
                    PRINTF("Keep-alive timeout, sending ping");

                    rc = MqttClient_Ping_ex(&mqttCtx->client, &mqttCtx->ping);
                    if (rc == MQTT_CODE_CONTINUE) {
                        return rc;
                    }
                    else if (rc != MQTT_CODE_SUCCESS) {
                        PRINTF("MQTT Ping Keep Alive Error: %s (%d)",
                            MqttClient_ReturnCodeToString(rc), rc);
                        break;
                    }
                }
                else if (rc != MQTT_CODE_SUCCESS) {
                    /* There was an error */
                    PRINTF("MQTT Message Wait: %s (%d)",
                        MqttClient_ReturnCodeToString(rc), rc);
                    break;
                }

                /* Exit if test mode */
                if (mqttCtx->test_mode) {
                    break;
                }
            } while (1);

            /* Check for error */
            if (rc != MQTT_CODE_SUCCESS) {
                goto disconn;
            }
        }
        FALL_THROUGH;

        case WMQ_DISCONNECT:
        {
            /* Disconnect */
            rc = MqttClient_Disconnect(&mqttCtx->client);
            if (rc == MQTT_CODE_CONTINUE) {
                return rc;
            }
            PRINTF("MQTT Disconnect: %s (%d)",
                MqttClient_ReturnCodeToString(rc), rc);
            if (rc != MQTT_CODE_SUCCESS) {
                goto disconn;
            }
        }
        FALL_THROUGH;

        case WMQ_NET_DISCONNECT:
        {
            mqttCtx->stat = WMQ_NET_DISCONNECT;

            rc = MqttClient_NetDisconnect(&mqttCtx->client);
            if (rc == MQTT_CODE_CONTINUE) {
                return rc;
            }
            PRINTF("MQTT Socket Disconnect: %s (%d)",
                MqttClient_ReturnCodeToString(rc), rc);
        }
        FALL_THROUGH;

        case WMQ_DONE:
        {
            mqttCtx->stat = WMQ_DONE;
            rc = mqttCtx->return_code;
            goto exit;
        }

        case WMQ_PUB:
        case WMQ_UNSUB:
        case WMQ_PING:
        default:
            rc = MQTT_CODE_ERROR_STAT;
            goto exit;
    } /* switch */

disconn:
    mqttCtx->stat = WMQ_NET_DISCONNECT;
    mqttCtx->return_code = rc;
    rc = MQTT_CODE_CONTINUE;

exit:

    if (rc != MQTT_CODE_CONTINUE) {
        fw_transfer_reset();

        /* Free resources */
        if (mqttCtx->tx_buf) WOLFMQTT_FREE(mqttCtx->tx_buf);
        if (mqttCtx->rx_buf) WOLFMQTT_FREE(mqttCtx->rx_buf);

        /* Cleanup network */
        MqttClientNet_DeInit(&mqttCtx->net);

        MqttClient_DeInit(&mqttCtx->client);
    }

    return rc;
}


/* so overall tests can pull in test function */
#ifdef USE_WINDOWS_API
    #include <windows.h> /* for ctrl handler */

    static BOOL CtrlHandler(DWORD fdwCtrlType)
    {
        if (fdwCtrlType == CTRL_C_EVENT) {
        #if defined(ENABLE_FIRMWARE_SIG)
            mStopRead = 1;
        #endif
            PRINTF("Received Ctrl+c");
            return TRUE;
        }
        return FALSE;
    }
#elif HAVE_SIGNAL
    #include <signal.h>
    static void sig_handler(int signo)
    {
        if (signo == SIGINT) {
        #if defined(ENABLE_FIRMWARE_SIG)
            mStopRead = 1;
        #endif
            PRINTF("Received SIGINT");
        }
    }
#endif

int fwclient_main(void)
{
    int rc;
    MQTTCtx mqttCtx;

#if defined(DEBUG_WOLFSSL)
    printf("Debug enabled\n");
    wolfSSL_Debugging_ON();
#endif
    /* init defaults */
    mqtt_init_ctx(&mqttCtx);
    mqttCtx.app_name = "fwclient";
    mqttCtx.client_id = mqtt_append_random(FIRMWARE_CLIENT_ID,
        (word32)XSTRLEN(FIRMWARE_CLIENT_ID));
    mqttCtx.dynamicClientId = 1;
    mqttCtx.topic_name = FIRMWARE_TOPIC_NAME;
    mqttCtx.qos = FIRMWARE_MQTT_QOS;
    mqttCtx.pub_file = FIRMWARE_DEF_SAVE_AS;
    mqttCtx.use_tls = 1;
    mqttCtx.username = "fwclient";
    mqttCtx.password = "fwclient_pw";

#ifdef USE_WINDOWS_API
    if (SetConsoleCtrlHandler((PHANDLER_ROUTINE)CtrlHandler, TRUE) == FALSE) {
        PRINTF("Error setting Ctrl Handler! Error %d", (int)GetLastError());
    }
#elif HAVE_SIGNAL
    if (signal(SIGINT, sig_handler) == SIG_ERR) {
        PRINTF("Can't catch SIGINT");
    }
#endif

    do {
        rc = fwclient_test(&mqttCtx);
    } while (!mStopRead && rc == MQTT_CODE_CONTINUE);

    mqtt_free_ctx(&mqttCtx);

    return (rc == 0) ? 0 : EXIT_FAILURE;
}
