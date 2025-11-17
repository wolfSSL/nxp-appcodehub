#!/bin/bash

# Build wolfSSL with ML-KEM and ML-DSA support, then build the client application
# This script should be run from the project root directory

set -e  # Exit on error

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WOLFSSL_DIR="${SCRIPT_DIR}/__repo__/modules/crypto/wolfssl"
WOLFSSL_STANDALONE="${SCRIPT_DIR}/wolfssl"
CLIENT_SOURCE="${SCRIPT_DIR}/client-tls13-filetransfer.c"
CLIENT_OUTPUT="${SCRIPT_DIR}/client-tls13-filetransfer"

echo "======================================"
echo "Building wolfSSL with PQC Support"
echo "======================================"
echo ""

# Check if wolfSSL directory exists in __repo__ path
if [ ! -d "$WOLFSSL_DIR" ]; then
    echo "wolfSSL not found in Zephyr modules path."
    echo "Checking for standalone wolfSSL directory..."
    
    if [ ! -d "$WOLFSSL_STANDALONE" ]; then
        echo ""
        echo "Cloning wolfSSL v5.8.0 from GitHub..."
        git clone --depth 1 --branch v5.8.0-stable https://github.com/wolfSSL/wolfssl.git "$WOLFSSL_STANDALONE"
        WOLFSSL_DIR="$WOLFSSL_STANDALONE"
    else
        echo "Using existing standalone wolfSSL directory."
        WOLFSSL_DIR="$WOLFSSL_STANDALONE"
    fi
fi

echo "wolfSSL Directory: $WOLFSSL_DIR"
echo "======================================"
echo ""

# Build wolfSSL with ML-KEM and ML-DSA support
cd "$WOLFSSL_DIR"

echo "Running autogen.sh..."
./autogen.sh

echo ""
echo "Configuring wolfSSL with ML-KEM and ML-DSA support..."
./configure --enable-mlkem --enable-dilithium

echo ""
echo "Building wolfSSL..."
make

echo ""
echo "======================================"
echo "Building Client Application"
echo "======================================"
echo "Source: $CLIENT_SOURCE"
echo "Output: $CLIENT_OUTPUT"
echo "======================================"
echo ""

# Check if client source exists
if [ ! -f "$CLIENT_SOURCE" ]; then
    echo "ERROR: Client source file not found at $CLIENT_SOURCE"
    exit 1
fi

# Build the client application
cd "$SCRIPT_DIR"
gcc "$CLIENT_SOURCE" \
    -o "$CLIENT_OUTPUT" \
    -I"${WOLFSSL_DIR}" \
    -L"${WOLFSSL_DIR}/src/.libs" \
    -lwolfssl \
    -lpthread \
    -lm

echo ""
echo "======================================"
echo "Build Completed Successfully!"
echo "======================================"
echo "Client executable: $CLIENT_OUTPUT"
echo ""
echo "To run the client, use:"
echo "  ./run-client.sh <IP_ADDRESS> [certificate.pem] [key.pem]"
echo "======================================"

