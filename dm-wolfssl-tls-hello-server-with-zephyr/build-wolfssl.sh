#!/bin/bash

# Build wolfSSL for use with the TLS Hello Server application
# This script should be run from the project root directory

set -e  # Exit on error

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WOLFSSL_DIR="${SCRIPT_DIR}/__repo__/modules/crypto/wolfssl"
WOLFSSL_STANDALONE="${SCRIPT_DIR}/wolfssl"

echo "======================================"
echo "Building wolfSSL"
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

# Build wolfSSL
cd "$WOLFSSL_DIR"

echo "Running autogen.sh..."
./autogen.sh

echo ""
echo "Configuring wolfSSL..."
./configure

echo ""
echo "Building wolfSSL..."
make

echo ""
echo "======================================"
echo "Build Completed Successfully!"
echo "======================================"
echo "wolfSSL library built at: ${WOLFSSL_DIR}/src/.libs"
echo "======================================"

