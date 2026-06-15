/* firmware.h
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

#ifndef WOLFMQTT_FIRMWARE_H
#define WOLFMQTT_FIRMWARE_H

#ifdef __cplusplus
extern "C" {
#endif

#define FIRMWARE_TOPIC_NAME     "wolfMQTT/example/firmware"
#define COMMAND_TOPIC_NAME      "wolfMQTT/example/command"
#define FIRMWARE_MAX_BUFFER     2048
#define FIRMWARE_MAX_PACKET     (int)(FIRMWARE_MAX_BUFFER + sizeof(MqttPacket) + XSTRLEN(FIRMWARE_TOPIC_NAME) + MQTT_DATA_LEN_SIZE)
#define FIRMWARE_MQTT_QOS		MQTT_QOS_2
#define FIRMWARE_PUBLISH_DELAY_MS 200

typedef struct messageHeader {
    word16 chunkNumber;
    word16 chunkSize;
    word32 totalLen;
} WOLFMQTT_PACK MessageHeader;

typedef struct commandHeader {
    word16 commandId;
    word16 commandLen;
} WOLFMQTT_PACK CommandHeader;

enum CommandIds {
    COMMAND_ID_REBOOT = 1,
    COMMAND_ID_ERASE = 2,
};

#ifdef __cplusplus
}
#endif

#endif /* WOLFMQTT_FIRMWARE_H */
