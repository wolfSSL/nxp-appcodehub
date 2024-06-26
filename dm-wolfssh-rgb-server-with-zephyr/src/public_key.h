/* public_key_array.h
 *
 * Copyright (C) 2014-2024 wolfSSL Inc.
 *
 * This file is part of wolfSSH.
 *
 * wolfSSH is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 3 of the License, or
 * (at your option) any later version.
 *
 * wolfSSH is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with wolfSSH.  If not, see <http://www.gnu.org/licenses/>.
 */

/* test_key/public_key.der */

static const unsigned char publicKeyRSA[] = 
{
0x00, 0x00, 0x00, 0x07, 0x73, 0x73, 0x68, 0x2d, 0x72, 0x73, 0x61, 0x00, 0x00, 
0x00, 0x03, 0x01, 0x00, 0x01, 0x00, 0x00, 0x01, 0x01, 0x00, 0xcb, 0x67, 0x22, 
0x81, 0x90, 0xe5, 0xa4, 0x14, 0xfe, 0x5f, 0x63, 0x09, 0x1c, 0x0a, 0x07, 0x9d, 
0xf7, 0x85, 0x7f, 0xaf, 0x8b, 0x1e, 0x6e, 0x81, 0x0e, 0x40, 0x13, 0xf1, 0x5f, 
0x5e, 0x8c, 0x70, 0x38, 0x75, 0x8d, 0x00, 0x7a, 0x96, 0xfa, 0x35, 0xb5, 0x7a, 
0xbb, 0xfa, 0x18, 0x66, 0x5b, 0x9e, 0x28, 0xf7, 0x80, 0x67, 0x71, 0x8d, 0xce, 
0xd4, 0x7d, 0xb3, 0x3a, 0x0b, 0xe8, 0x54, 0xa5, 0x32, 0xf9, 0x1d, 0xf6, 0x8a, 
0xaf, 0x7e, 0x8c, 0x63, 0x4c, 0x6b, 0xbb, 0xd5, 0x3d, 0x3d, 0xf3, 0x5c, 0x9b, 
0x25, 0xe9, 0x6a, 0xa8, 0x60, 0x13, 0x1a, 0xd7, 0xaf, 0x1d, 0x4e, 0x89, 0x92, 
0x8f, 0xc8, 0xd3, 0xe9, 0x60, 0x49, 0x01, 0x4a, 0x59, 0x02, 0x7b, 0x0f, 0x70, 
0xe3, 0xdb, 0x39, 0x59, 0xa4, 0x85, 0xc1, 0xd9, 0x0e, 0xf1, 0x97, 0xf8, 0xbd, 
0x90, 0x56, 0x60, 0x68, 0x8a, 0x85, 0x85, 0x41, 0xdd, 0x58, 0xef, 0x49, 0x30, 
0xec, 0x02, 0x17, 0x0a, 0x03, 0xae, 0x17, 0x7c, 0xbf, 0x9c, 0x64, 0xcb, 0x5b, 
0xf7, 0x38, 0x82, 0x19, 0x0f, 0x18, 0x98, 0xc5, 0x52, 0x0a, 0x6d, 0xfd, 0x89, 
0xd3, 0xb5, 0x9b, 0xc2, 0x24, 0x85, 0x03, 0x15, 0xdf, 0x89, 0x7f, 0x13, 0x8d, 
0x40, 0x43, 0x2c, 0x59, 0xad, 0xaa, 0xdd, 0x40, 0x17, 0xa2, 0xe3, 0x03, 0x21, 
0xb2, 0x69, 0x12, 0x02, 0x5e, 0x46, 0x86, 0x25, 0x0f, 0xd0, 0x27, 0xdb, 0x17, 
0xf3, 0x98, 0x7d, 0x6e, 0x48, 0x8f, 0x94, 0x63, 0x40, 0xb5, 0x01, 0x90, 0xb4, 
0xd5, 0x20, 0xde, 0xd0, 0xbd, 0x1a, 0xa3, 0xa4, 0x67, 0xdb, 0x42, 0x29, 0x8d, 
0x44, 0x7e, 0x1f, 0xeb, 0xd3, 0x3a, 0x2c, 0x38, 0x0e, 0xee, 0x7f, 0x18, 0x3a, 
0xca, 0x62, 0xe9, 0xab, 0x50, 0xb8, 0x2d, 0x4f, 0xf8, 0x10, 0xd1, 0x87, 0x92, 
0xe4, 0x4f, 0x00, 0xab, 0xee, 0xf3
};

static const unsigned int publicKeyRSA_size = sizeof(publicKeyRSA);

