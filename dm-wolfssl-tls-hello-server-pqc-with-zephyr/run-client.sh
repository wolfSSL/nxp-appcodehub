#!/bin/bash

# Run the client-tls13-filetransfer application
# Usage: ./run-client.sh <IP_ADDRESS> [certificate.pem] [key.pem]

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CLIENT_APP="${SCRIPT_DIR}/client-tls13-filetransfer"

# Check for wolfSSL library directory (standalone or Zephyr modules)
if [ -d "${SCRIPT_DIR}/wolfssl/src/.libs" ]; then
    WOLFSSL_LIB_DIR="${SCRIPT_DIR}/wolfssl/src/.libs"
elif [ -d "${SCRIPT_DIR}/__repo__/modules/crypto/wolfssl/src/.libs" ]; then
    WOLFSSL_LIB_DIR="${SCRIPT_DIR}/__repo__/modules/crypto/wolfssl/src/.libs"
else
    WOLFSSL_LIB_DIR=""
fi

# Default values
IP_ADDRESS="${1:-1.1.1.5}"
CERT_FILE="${2:-${SCRIPT_DIR}/mldsa44_entity_cert.pem}"
KEY_FILE="${3:-${SCRIPT_DIR}/mldsa44_entity_key.pem}"

# Check if client application exists
if [ ! -f "$CLIENT_APP" ]; then
    echo "ERROR: Client application not found at $CLIENT_APP"
    echo "Please build the project first using ./build-client.sh"
    exit 1
fi

# Check if certificate and key files exist
if [ ! -f "$CERT_FILE" ]; then
    echo "ERROR: Certificate file not found: $CERT_FILE"
    exit 1
fi

if [ ! -f "$KEY_FILE" ]; then
    echo "ERROR: Key file not found: $KEY_FILE"
    exit 1
fi

# Check if wolfSSL library exists
if [ -z "$WOLFSSL_LIB_DIR" ] || [ ! -d "$WOLFSSL_LIB_DIR" ]; then
    echo "ERROR: wolfSSL library directory not found"
    echo "Searched locations:"
    echo "  - ${SCRIPT_DIR}/wolfssl/src/.libs (standalone)"
    echo "  - ${SCRIPT_DIR}/__repo__/modules/crypto/wolfssl/src/.libs (Zephyr modules)"
    echo "Please build wolfSSL first using ./build-client.sh"
    exit 1
fi

# Display usage information
echo "======================================"
echo "Running TLS 1.3 File Transfer Client"
echo "======================================"
echo "Server IP:    $IP_ADDRESS"
echo "Certificate:  $CERT_FILE"
echo "Private Key:  $KEY_FILE"
echo "wolfSSL Lib:  $WOLFSSL_LIB_DIR"
echo "======================================"
echo ""

# Set LD_LIBRARY_PATH and run the client
export LD_LIBRARY_PATH="${WOLFSSL_LIB_DIR}:${LD_LIBRARY_PATH}"
export DYLD_LIBRARY_PATH="${WOLFSSL_LIB_DIR}:${DYLD_LIBRARY_PATH}"  # For macOS

"$CLIENT_APP" "$IP_ADDRESS" "$CERT_FILE" "$KEY_FILE"
