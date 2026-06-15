/* fwpush.c
 *
 * Copyright (C) 2006-2026 wolfSSL Inc.
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

#if defined(USE_WINDOWS_API)
    #include <windows.h>
#elif !defined(NO_FILESYSTEM)
    #include <unistd.h>
#endif

#if defined(ENABLE_MQTT_TLS)
    #if !defined(WOLFSSL_USER_SETTINGS) && !defined(USE_WINDOWS_API)
        #include <wolfssl/options.h>
    #endif
    #include <wolfssl/wolfcrypt/settings.h>
#endif

#include "fwserver.h"
#include "firmware.h"
#include "mqttexample.h"
#include "mqttnet.h"

/* Configuration */
#ifndef MAX_BUFFER_SIZE
#define MAX_BUFFER_SIZE         FIRMWARE_MAX_PACKET
#endif

#define FIRMWARE_CHUNK_DATA_MAX ((word32)(FIRMWARE_MAX_BUFFER - sizeof(MessageHeader)))
#define ERASE_COMMAND_WAIT_MS   2000

/* Locals */
static int mStopRead = 0;

#if !defined(NO_FILESYSTEM)
typedef struct FwpushTransfer_s {
    FILE* fp;
    const char* filename;
    word32 total_len;
    word32 bytes_sent;
    word16 chunk_number;
    word16 chunk_payload_len;
    int chunk_ready;
    int done;
    int erase_cmd_ready;
    int erase_cmd_sent;
    int erase_wait_done;
} FwpushTransfer;
#endif

static int mqtt_message_cb(MqttClient *client, MqttMessage *msg,
    byte msg_new, byte msg_done)
{
    MQTTCtx* mqttCtx = (MQTTCtx*)client->ctx;

    (void)mqttCtx;
    (void)msg;
    (void)msg_new;
    (void)msg_done;

    /* Return negative to terminate publish processing */
    return MQTT_CODE_SUCCESS;
}

static void fwpush_publish_delay(void)
{
#if FIRMWARE_PUBLISH_DELAY_MS > 0
    #ifdef USE_WINDOWS_API
    Sleep(FIRMWARE_PUBLISH_DELAY_MS);
    #else
    usleep(FIRMWARE_PUBLISH_DELAY_MS * 1000);
    #endif
#endif
}

#if !defined(NO_FILESYSTEM)
static void fwpush_erase_wait(void)
{
#ifdef USE_WINDOWS_API
    Sleep(ERASE_COMMAND_WAIT_MS);
#else
    usleep(ERASE_COMMAND_WAIT_MS * 1000);
#endif
}
#endif

#if !defined(NO_FILESYSTEM)
static void fwpush_transfer_deinit(FwpushTransfer* transfer)
{
    if (transfer != NULL && transfer->fp != NULL) {
        fclose(transfer->fp);
        transfer->fp = NULL;
    }
}

static int fwpush_transfer_init(MQTTCtx* mqttCtx, FwpushTransfer* transfer,
    const char* filename)
{
    long file_len;

    if (mqttCtx == NULL || transfer == NULL || filename == NULL) {
        return MQTT_CODE_ERROR_BAD_ARG;
    }

    XMEMSET(transfer, 0, sizeof(*transfer));
    transfer->filename = filename;
    transfer->fp = fopen(filename, "rb");
    if (transfer->fp == NULL) {
        PRINTF("Firmware file %s open error", filename);
        return MQTT_CODE_ERROR_SYSTEM;
    }

    if (fseek(transfer->fp, 0, SEEK_END) != 0) {
        PRINTF("Firmware file %s seek end failed", filename);
        return MQTT_CODE_ERROR_SYSTEM;
    }

    file_len = ftell(transfer->fp);
    if (file_len <= 0) {
        PRINTF("Firmware file %s has invalid size %ld", filename, file_len);
        return MQTT_CODE_ERROR_OUT_OF_BUFFER;
    }

    if ((unsigned long)file_len > 0xFFFFFFFFUL) {
        PRINTF("Firmware file %s exceeds max supported size", filename);
        return MQTT_CODE_ERROR_OUT_OF_BUFFER;
    }

    if (fseek(transfer->fp, 0, SEEK_SET) != 0) {
        PRINTF("Firmware file %s seek start failed", filename);
        return MQTT_CODE_ERROR_SYSTEM;
    }

    transfer->total_len = (word32)file_len;

    PRINTF("Firmware file %s is %u bytes", filename, transfer->total_len);

    (void)mqttCtx;
    return MQTT_CODE_SUCCESS;
}

static int fwpush_transfer_prepare_chunk(MQTTCtx* mqttCtx,
    FwpushTransfer* transfer)
{
    MessageHeader* header;
    size_t bytes_read;
    MqttPublish* publish;

    if (mqttCtx == NULL || transfer == NULL || transfer->fp == NULL) {
        return MQTT_CODE_ERROR_BAD_ARG;
    }

    publish = &mqttCtx->publish;
    header = (MessageHeader*)publish->buffer;

    bytes_read = fread(&publish->buffer[sizeof(MessageHeader)], 1,
        FIRMWARE_CHUNK_DATA_MAX, transfer->fp);
    if (bytes_read == 0) {
        if (feof(transfer->fp)) {
            transfer->done = 1;
            return MQTT_CODE_SUCCESS;
        }
        PRINTF("Firmware file %s read error", transfer->filename);
        return MQTT_CODE_ERROR_SYSTEM;
    }

    if (bytes_read > (size_t)0xFFFFU) {
        PRINTF("Chunk size overflow %u", (unsigned)bytes_read);
        return MQTT_CODE_ERROR_OUT_OF_BUFFER;
    }

    header->chunkNumber = transfer->chunk_number;
    header->chunkSize = (word16)bytes_read;
    header->totalLen = transfer->total_len;

    transfer->chunk_payload_len = (word16)bytes_read;
    transfer->chunk_ready = 1;

    publish->packet_id = mqtt_get_packetid();
    publish->total_len = sizeof(MessageHeader) + (word32)bytes_read;
    publish->buffer_len = publish->total_len;

    return MQTT_CODE_SUCCESS;
}

static int fwpush_transfer_send(MQTTCtx* mqttCtx, FwpushTransfer* transfer)
{
    int rc;

    if (mqttCtx == NULL || transfer == NULL) {
        return MQTT_CODE_ERROR_BAD_ARG;
    }

    while (!transfer->done) {
        if (!transfer->chunk_ready) {
            rc = fwpush_transfer_prepare_chunk(mqttCtx, transfer);
            if (rc != MQTT_CODE_SUCCESS) {
                return rc;
            }

            if (transfer->done) {
                break;
            }
        }

        rc = MqttClient_Publish(&mqttCtx->client, &mqttCtx->publish);
        if (rc == MQTT_CODE_CONTINUE) {
            return rc;
        }
        if (rc != MQTT_CODE_SUCCESS) {
            return rc;
        }

        transfer->bytes_sent += transfer->chunk_payload_len;
        PRINTF("MQTT Publish chunk %u: %u bytes (%u/%u)",
            transfer->chunk_number,
            transfer->chunk_payload_len,
            transfer->bytes_sent,
            transfer->total_len);

        transfer->chunk_ready = 0;

        if (transfer->bytes_sent > transfer->total_len) {
            PRINTF("Transferred bytes exceed expected total");
            return MQTT_CODE_ERROR_MALFORMED_DATA;
        }

        if (transfer->bytes_sent == transfer->total_len) {
            transfer->done = 1;
            break;
        }

        fwpush_publish_delay();

        if (transfer->chunk_number == 0xFFFFU) {
            PRINTF("Firmware requires more than 65536 chunks");
            return MQTT_CODE_ERROR_OUT_OF_BUFFER;
        }

        transfer->chunk_number++;
    }

    return MQTT_CODE_SUCCESS;
}

static int fwpush_send_erase_command(MQTTCtx* mqttCtx, FwpushTransfer* transfer)
{
    int rc;
    CommandHeader* command;
    MqttPublish* publish;

    if (mqttCtx == NULL || transfer == NULL) {
        return MQTT_CODE_ERROR_BAD_ARG;
    }

    publish = &mqttCtx->publish;

    if (!transfer->erase_cmd_ready) {
        command = (CommandHeader*)publish->buffer;
        command->commandId = COMMAND_ID_ERASE;
        command->commandLen = 0;

        publish->topic_name = COMMAND_TOPIC_NAME;
        publish->packet_id = mqtt_get_packetid();
        publish->total_len = sizeof(CommandHeader);
        publish->buffer_len = publish->total_len;
        transfer->erase_cmd_ready = 1;
    }

    rc = MqttClient_Publish(&mqttCtx->client, publish);
    if (rc == MQTT_CODE_CONTINUE) {
        return rc;
    }
    if (rc != MQTT_CODE_SUCCESS) {
        return rc;
    }

    transfer->erase_cmd_ready = 0;
    transfer->erase_cmd_sent = 1;
    publish->topic_name = mqttCtx->topic_name;
    publish->buffer_len = FIRMWARE_MAX_BUFFER;

    PRINTF("MQTT Command publish complete: ERASE");

    return MQTT_CODE_SUCCESS;
}
#endif /* !NO_FILESYSTEM */

int fwpush_test(MQTTCtx *mqttCtx)
{
    int rc;
#if !defined(NO_FILESYSTEM)
    FwpushTransfer* transfer = NULL;
#endif

    if (mqttCtx == NULL) {
        return MQTT_CODE_ERROR_BAD_ARG;
    }

    /* restore callback data */
#if !defined(NO_FILESYSTEM)
    transfer = (FwpushTransfer*)mqttCtx->publish.ctx;
#endif

    /* check for stop */
    if (mStopRead) {
        rc = MQTT_CODE_SUCCESS;
        PRINTF("MQTT Exiting...");
        mStopRead = 0;
        goto disconn;
    }

    switch (mqttCtx->stat)
    {
        case WMQ_BEGIN:
        {
            PRINTF("MQTT Firmware Push Client: QoS %d, Use TLS %d",
                    mqttCtx->qos, mqttCtx->use_tls);
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
                    mqttCtx->port, DEFAULT_CON_TIMEOUT_MS, mqttCtx->use_tls,
                    mqtt_tls_cb);
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
                mqttCtx->lwt_msg.total_len =
                        (word16)XSTRLEN(mqttCtx->client_id);
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
                (mqttCtx->connect.ack.flags &
                        MQTT_CONNECT_ACK_FLAG_SESSION_PRESENT) ?
                    1 : 0
            );

            if (rc != MQTT_CODE_SUCCESS) {
                goto disconn;
            }

            /* setup publish message */
            XMEMSET(&mqttCtx->publish, 0, sizeof(MqttPublish));
            mqttCtx->publish.retain = mqttCtx->retain;
            mqttCtx->publish.qos = mqttCtx->qos;
            mqttCtx->publish.duplicate = 0;
            mqttCtx->publish.topic_name = mqttCtx->topic_name;
            mqttCtx->publish.buffer_len = FIRMWARE_MAX_BUFFER;
            mqttCtx->publish.buffer = (byte*)WOLFMQTT_MALLOC(FIRMWARE_MAX_BUFFER);
            if (mqttCtx->publish.buffer == NULL) {
                rc = MQTT_CODE_ERROR_OUT_OF_BUFFER;
                goto disconn;
            }

#if !defined(NO_FILESYSTEM)
            transfer = (FwpushTransfer*)WOLFMQTT_MALLOC(sizeof(FwpushTransfer));
            if (transfer == NULL) {
                rc = MQTT_CODE_ERROR_OUT_OF_BUFFER;
                goto disconn;
            }

            rc = fwpush_transfer_init(mqttCtx, transfer, mqttCtx->pub_file);
            if (rc != MQTT_CODE_SUCCESS) {
                mqtt_show_usage(mqttCtx);
                goto disconn;
            }

            mqttCtx->publish.ctx = transfer;
#else
            PRINTF("Firmware push requires filesystem support");
            rc = MQTT_CODE_ERROR_SYSTEM;
            goto disconn;
#endif
        }
        FALL_THROUGH;

        case WMQ_PUB:
        {
            mqttCtx->stat = WMQ_PUB;

#if !defined(NO_FILESYSTEM)
            if (!transfer->erase_cmd_sent) {
                rc = fwpush_send_erase_command(mqttCtx, transfer);
                if (rc == MQTT_CODE_CONTINUE) {
                    return rc;
                }
                if (rc != MQTT_CODE_SUCCESS) {
                    goto disconn;
                }
            }

            if (!transfer->erase_wait_done) {
                PRINTF("Waiting %u ms after erase command...", ERASE_COMMAND_WAIT_MS);
                fwpush_erase_wait();
                transfer->erase_wait_done = 1;
            }

            rc = fwpush_transfer_send(mqttCtx, transfer);
            if (rc == MQTT_CODE_CONTINUE) {
                return rc;
            }
            if (rc != MQTT_CODE_SUCCESS) {
                goto disconn;
            }

            PRINTF("MQTT Publish complete: %u bytes sent in %u chunks",
                transfer->bytes_sent, (unsigned)transfer->chunk_number + 1U);
#else
            rc = MQTT_CODE_ERROR_SYSTEM;
            goto disconn;
#endif
        }
        FALL_THROUGH;

        case WMQ_DISCONNECT:
        {
            mqttCtx->stat = WMQ_DISCONNECT;

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

        case WMQ_SUB:
        case WMQ_WAIT_MSG:
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
#if !defined(NO_FILESYSTEM)
        if (transfer != NULL) {
            fwpush_transfer_deinit(transfer);
            WOLFMQTT_FREE(transfer);
            mqttCtx->publish.ctx = NULL;
        }
#endif
        if (mqttCtx->publish.buffer) WOLFMQTT_FREE(mqttCtx->publish.buffer);
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
            mStopRead = 1;
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
            mStopRead = 1;
            PRINTF("Received SIGINT");
            (void)signal(SIGINT, SIG_DFL);
            (void)raise(SIGINT);
        }
    }
#endif

#if defined(NO_MAIN_DRIVER)
int fwpush_main(int argc, char** argv)
#else
int main(int argc, char** argv)
#endif
{
    int rc;
    MQTTCtx mqttCtx;

    /* init defaults */
    mqtt_init_ctx(&mqttCtx);
    mqttCtx.app_name = "fwpush";
    mqttCtx.client_id = mqtt_append_random(FIRMWARE_PUSH_CLIENT_ID,
        (word32)XSTRLEN(FIRMWARE_PUSH_CLIENT_ID));
    mqttCtx.dynamicClientId = 1;
    mqttCtx.topic_name = FIRMWARE_TOPIC_NAME;
    mqttCtx.qos = FIRMWARE_MQTT_QOS;
    mqttCtx.pub_file = FIRMWARE_PUSH_DEF_FILE;

    /* parse arguments */
    rc = mqtt_parse_args(&mqttCtx, argc, argv);
    if (rc != 0) {
        return rc;
    }

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
        rc = fwpush_test(&mqttCtx);
    } while (!mStopRead && rc == MQTT_CODE_CONTINUE);

    mqtt_free_ctx(&mqttCtx);

    return (rc == 0) ? 0 : EXIT_FAILURE;
}
