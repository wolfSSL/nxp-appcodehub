# wolfSSL NXP Application Code Hub

<a href="https://www.nxp.com"> <img src="https://mcuxpresso.nxp.com/static/icon/nxp-logo-color.svg" width="125" style="margin-bottom: 40px;" /> </a> <a href="https://www.wolfssl.com"> <img src="../Images/wolfssl_logo_300px.png" width="100" style="margin-bottom: 40px" align=right /> </a>

## wolfSSL TLSv1.3 Hello Server Doing Post-Quantum Cryptography using Zephyr RTOS

This demo demonstrates capabilities of the new FRDM-MCXN947.  

### Demo
It starts with running our benchmarks for the wolfCrypt library and getting peformance numbers for all enable algorithms which in this case includes ML-KEM and ML-DSA. Once the benchmarks are done, a TLS 1.3 server is started. Importantly, this server is configured to support the post-quantum algorithms ML-KEM and ML-DSA.

#### Boards:        FRDM-MCXN947
#### Categories:    RTOS, Zephyr, Networking
#### Peripherals:   UART, ETHERNET
#### Toolchains:    Zephyr

## Table of Contents
1. [Software](#step1)
2. [Hardware](#step2)
3. [Setup](#step3)
4. [Verification](#step4)
5. [Project Options](#step5)
6. [Project Flow Chart](#step6)
7. [FAQs](#step7) 
8. [Support](#step8)
9. [Release Notes](#step9)

## 1. Software<a name="step1"></a>
- [MCUXpresso for VS Code 1.5.61 or newer](https://www.nxp.com/products/processors-and-microcontrollers/arm-microcontrollers/general-purpose-mcus/lpc800-arm-cortex-m0-plus-/mcuxpresso-for-visual-studio-code:MCUXPRESSO-VSC?cid=wechat_iot_303216)

- [Zephyr Setup](https://docs.zephyrproject.org/latest/develop/getting_started/index.html)
    - [wolfSSL as a Module added to Zephyr](https://github.com/wolfSSL/wolfssl/blob/master/zephyr/README.md)
    - [Adding the Zephyr Repository (Part 5)](https://community.nxp.com/t5/MCUXpresso-for-VSCode-Knowledge/Training-Walkthrough-of-MCUXpresso-for-VS-Code/ta-p/1634002)

- MCUXpresso Installer:
   - MCUXpresso SDK Developer
   - Zephyr Developer
   - Linkserver

- Ubuntu or MacOS with the following packages:
    - autoconf
    - automake
    - libtool
    - make
    - gcc
    - git 

 - Zephyr:
    - SDK 0.16.8
    - Version 4.0.0

- Optional Software:
    - Wireshark

## 2. Hardware<a name="step2"></a>
- [FRDM-MCXN947.](https://www.nxp.com/products/processors-and-microcontrollers/arm-microcontrollers/general-purpose-mcus/mcx-arm-cortex-m/mcx-n94x-and-n54x-mcus-with-dual-core-arm-cortex-m33-eiq-neutron-npu-and-edgelock-secure-enclave-core-profile:MCX-N94X-N54X)   
[<img src="Images/FRDM-MCXN947-TOP.jpg" width="300"/>](Images/FRDM-MCXN947-TOP.jpg)
- USB Type-C cable.
- Ethernet Cable.
- Networking/Router
- Personal Computer.


## 3. Setup<a name="step3"></a>

### 3.1 Import the Project and Build
1. Follow section 1: `Setup` in the top level [README](../README.md)

2. The Zephyr project import requires the user to select the board target.
The board target can be specified under the **PROJECTS** view.
- Expand the project information
- Click **Build Configurations**
- Click **debug Default**
- Click the *pencil* icon to the right of the debug configuration
- Type **MCXN9** in the Board and select the *NXP FRDM_MCXN947
- Click **Save**
3. Under the "Projects" tab, right click the project and choose "build selected", this should result in no errors.

[<img src="Images/Setup3-3.png" width="300"/>](Images/Setup3-3.png)

The project should be called `dm-wolfssl-tls-hello-server-pqc-with-zephyr`.

4. For the full experience on Linux and MacOS, build the client application by running the provided build script:

```bash
cd dm-wolfssl-tls-hello-server-pqc-with-zephyr
./build-client.sh
```

This script will:
- Automatically clone wolfSSL v5.8.0 from GitHub if not found in the Zephyr modules path
- Build wolfSSL with ML-KEM and ML-DSA support enabled
- Compile the `client-tls13-filetransfer` application
- Link it against the built wolfSSL library

**Alternative: Manual wolfSSL setup**

If you prefer to manually clone wolfSSL instead of using the Zephyr-provided version:

```bash
cd dm-wolfssl-tls-hello-server-pqc-with-zephyr
git clone --branch v5.8.0-stable https://github.com/wolfSSL/wolfssl.git
./build-client.sh
```

The build script will automatically detect and use the standalone wolfSSL directory.

Windows support is coming soon.

### 3.2 Connect Hardware
1. Connect the FRDM-MCXN947 to your computer with the provided USB-C Cable

2. Connect the FRDM-MCXN947 to your network with an Ethernet cable

### 3.4 Program and Run the Server
1. Flash the .elf to FRDM-MCXN947. Can be done by right clicking the project and choosing to "flash the selected target"
2. Connect to the Serial Output of the FRDM-MCXN947 via:
    - Screen Command - `screen /dev/tty"MCXN-Port 115200"`
    - Some Serial Terminal you are familiar with 
3. Push reset button on the FRDM-MCXN947 board. Wait for the benchmarks to finish executing and then view the startup message. Note the IP address. It should be 1.1.1.5. It is statically set and can be changed in the application.

    [<img src="Images/Setup3-4.png" width="300"/>](Images/Setup3-4.png)

4. Run the `client-tls13-filetransfer` application. The source code for `client-tls13-filetransfer.c` and the required pem files are in the same directory as this README.md file.

   **Option 1: Using the convenience script (recommended)**
   ```bash
   ./run-client.sh 1.1.1.5 mldsa44_entity_cert.pem mldsa44_entity_key.pem
   ```
   
   The script automatically detects and uses either the standalone wolfSSL (in `wolfssl/`) or the Zephyr-provided version (in `__repo__/modules/crypto/wolfssl/`).

   **Option 2: Manual execution**
   
   If using standalone wolfSSL:
   ```bash
   LD_LIBRARY_PATH=wolfssl/src/.libs ./client-tls13-filetransfer 1.1.1.5 mldsa44_entity_cert.pem mldsa44_entity_key.pem
   ```
   
   If using Zephyr-provided wolfSSL:
   ```bash
   LD_LIBRARY_PATH=__repo__/modules/crypto/wolfssl/src/.libs ./client-tls13-filetransfer 1.1.1.5 mldsa44_entity_cert.pem mldsa44_entity_key.pem
   ```

   **Note:** If you need to rebuild the client application, run:
   ```bash
   ./build-client.sh
   ```

## 4. FAQs<a name="step4"></a>
No FAQs have been identified for this project.

## 5. Support<a name="step5"></a>

#### Project Metadata
<!----- Boards ----->
[![Board badge](https://img.shields.io/badge/Board-FRDM&ndash;MCXN947-blue)](https://github.com/search?q=org%3Anxp-appcodehub+FRDM-MCXN947+in%3Areadme&type=Repositories)

<!----- Categories ----->


<!----- Peripherals ----->
[![Peripheral badge](https://img.shields.io/badge/Peripheral-UART-yellow)](https://github.com/search?q=org%3Anxp-appcodehub+uart+in%3Areadme&type=Repositories) [![Peripheral badge](https://img.shields.io/badge/Peripheral-ETHERNET-yellow)](https://github.com/search?q=org%3Anxp-appcodehub+ethernet+in%3Areadme&type=Repositories)

<!----- Toolchains ----->
[![Toolchain badge](https://img.shields.io/badge/Toolchain-VS%20CODE-orange)](https://github.com/search?q=org%3Anxp-appcodehub+vscode+in%3Areadme&type=Repositories)

Questions regarding the content/correctness of this example can be entered as Issues within this GitHub repository.

>**Warning**: For more general technical questions regarding NXP Microcontrollers and the difference in expected functionality, enter your questions on the [NXP Community Forum](https://community.nxp.com/)



## 6. Release Notes<a name="step6"></a>
| Version | Description / Update                           | Date                        |
|:-------:|------------------------------------------------|----------------------------:|
| 1.0     | Initial release on Application Code Hub        | November 17th 2025|
