#!/bin/bash
set -e

# Script configuration
WOLFSSL_REPO_PATH="__repo__/modules/crypto/wolfssl"
WOLFTPM_REPO_PATH="__repo__/modules/lib/wolftpm"

# Display help information
show_help() {
    cat << EOF
SD Card Setup Script for wolfTPM/wolfSSL

USAGE:
    ./setup_sdcard.sh [OPTIONS]

OPTIONS:
    -h, --help     Display this help message

DESCRIPTION:
    This script prepares an SD card by:
    1. Formatting it as FAT32 (if needed and confirmed)
    2. Copying certificate files from wolfSSL and wolfTPM repositories

INSTRUCTIONS:
    1. Insert your SD card
    2. Run this script
    3. Select the correct partition (e.g., /dev/sda1), NOT the whole disk

IMPORTANT:
    - You must specify a partition (e.g., /dev/sdb1), not a whole disk (e.g., /dev/sdb)
    - This is a safety feature to prevent accidentally formatting your system drive
    - The script will show available disks, but you need to know which partition to use

EXAMPLE:
    If your SD card shows as /dev/sda with a partition /dev/sda1:
    Enter: /dev/sda1

EOF
}

# Process command line arguments
if [[ "$1" == "-h" || "$1" == "--help" ]]; then
    show_help
    exit 0
fi

echo "====================================================================="
echo "SD Card Setup Script for wolfTPM/wolfSSL"
echo "====================================================================="
echo "This script will prepare an SD card with certificates from wolfSSL and wolfTPM"
echo "For detailed instructions, run: ./setup_sdcard.sh --help"
echo "====================================================================="
echo ""

# First check that __repo__ exists
if [ ! -d "__repo__" ]; then
    echo "Error: __repo__ directory not found"
    echo "Please run this script after compiling the project"
    exit 1
fi

# Check that wolfssl and wolftpm repos are present
if [ ! -d "$WOLFSSL_REPO_PATH" ]; then
    echo "Error: wolfssl repo not found at $WOLFSSL_REPO_PATH"
    echo "Please run this script after compiling the project"
    exit 1
fi

if [ ! -d "$WOLFTPM_REPO_PATH" ]; then
    echo "Error: wolftpm repo not found at $WOLFTPM_REPO_PATH"
    echo "Please run this script after compiling the project"
    exit 1
fi

# Check for certs directories
if [ ! -d "$WOLFSSL_REPO_PATH/certs" ]; then
    echo "Error: wolfssl certs directory not found"
    exit 1
fi

if [ ! -d "$WOLFTPM_REPO_PATH/certs" ]; then
    echo "Error: wolftpm certs directory not found"
    exit 1
fi

# Display available disks AND partitions to help user identify their SD card
echo "Available disk devices and partitions:"
lsblk -o NAME,SIZE,TYPE,MODEL,MOUNTPOINT
echo ""
echo "IMPORTANT: You must specify a PARTITION (e.g., /dev/sdb1), not the whole disk!"
echo "Look for removable media (likely your SD card) based on size and model name."
echo ""
read -p "Enter the SD card partition device (e.g. /dev/sdb1): " SD_CARD_DEVICE

# Safety check - don't allow whole disk devices without partition numbers
if [[ "$SD_CARD_DEVICE" =~ ^/dev/[a-z]+$ ]]; then
    echo "Error: You must specify a partition (e.g. /dev/sdb1), not a whole disk device"
    echo "This is for your safety to prevent accidentally wiping your system drive"
    echo ""
    echo "To see all disks and partitions, run: lsblk"
    echo "Example output:"
    echo "NAME      MAJ:MIN RM   SIZE RO TYPE MOUNTPOINT"
    echo "sda         8:0    1  32.0G  0 disk "
    echo "└─sda1      8:1    1  32.0G  0 part "
    echo ""
    echo "In this example, you would use /dev/sda1 (the partition) not /dev/sda (the disk)"
    exit 1
fi

# Check if the SD card device is valid
if [ ! -b "$SD_CARD_DEVICE" ]; then
    echo "Error: $SD_CARD_DEVICE is not a valid block device"
    exit 1
fi

# Check if the SD card device is mounted
if mount | grep -q "$SD_CARD_DEVICE "; then
    echo "Warning: $SD_CARD_DEVICE is currently mounted"
    echo "Attempting to unmount it..."
    sudo umount "$SD_CARD_DEVICE" || { echo "Failed to unmount device. Please unmount it manually."; exit 1; }
fi

# Check if the SD card device is FAT32 formatted
echo "Checking filesystem on $SD_CARD_DEVICE..."
if ! sudo blkid "$SD_CARD_DEVICE" | grep -qi "fat32\|vfat"; then
    echo "SD card device is not FAT32 formatted."
    read -p "Do you want to format it to FAT32? (y/n): " FORMAT_SD_CARD
    if [ "$FORMAT_SD_CARD" = "y" ]; then
        echo "Formatting $SD_CARD_DEVICE as FAT32..."
        sudo mkfs.fat -F 32 "$SD_CARD_DEVICE" || { echo "Formatting failed"; exit 1; }
    else
        echo "Aborting: SD card device must be FAT32 formatted"
        exit 1
    fi
fi

# Create mount point
MOUNT_POINT="/tmp/sdcard-$(basename "$SD_CARD_DEVICE")-$$"
sudo mkdir -p "$MOUNT_POINT"

# Check directory exists
if [ ! -d "$MOUNT_POINT" ]; then
    echo "Error: Failed to create mount point directory"
    exit 1
fi

# Mount the SD card device to the mount point
echo "Mounting $SD_CARD_DEVICE to $MOUNT_POINT..."
sudo mount "$SD_CARD_DEVICE" "$MOUNT_POINT" || { echo "Failed to mount device"; sudo rmdir "$MOUNT_POINT"; exit 1; }

# Verify the mount was successful
if ! mount | grep -q "$SD_CARD_DEVICE on $MOUNT_POINT"; then
    echo "Error: Mount verification failed"
    sudo rmdir "$MOUNT_POINT"
    exit 1
fi

# Copy the certs directories from wolfssl and wolftpm repos to the mount point
echo "Copying certs to SD card..."
sudo mkdir -p "$MOUNT_POINT/certs-wolfssl" "$MOUNT_POINT/certs-wolftpm"
sudo cp -r "$WOLFSSL_REPO_PATH/certs/"* "$MOUNT_POINT/certs-wolfssl/" || { echo "Failed to copy wolfssl certs"; sudo umount "$MOUNT_POINT"; sudo rmdir "$MOUNT_POINT"; exit 1; }
sudo cp -r "$WOLFTPM_REPO_PATH/certs/"* "$MOUNT_POINT/certs-wolftpm/" || { echo "Failed to copy wolftpm certs"; sudo umount "$MOUNT_POINT"; sudo rmdir "$MOUNT_POINT"; exit 1; }

# Sync to ensure all data is written
echo "Syncing files to disk..."
sudo sync

# Unmount the mount point
echo "Unmounting $SD_CARD_DEVICE..."
sudo umount "$MOUNT_POINT" || { echo "Warning: Failed to unmount $MOUNT_POINT"; }

# Clean up the mount point directory
sudo rmdir "$MOUNT_POINT" || echo "Warning: Failed to remove mount point directory"

echo ""
echo "====================================================================="
echo "SD card setup completed successfully!"
echo "The following has been copied to your SD card:"
echo "  • wolfSSL certificates in: /certs-wolfssl/"
echo "  • wolfTPM certificates in: /certs-wolftpm/"
echo "====================================================================="
exit 0
