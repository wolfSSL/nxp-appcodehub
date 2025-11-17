# wolfSSL NXP Application Code Hub

<a href="https://www.nxp.com"> <img src="https://mcuxpresso.nxp.com/static/icon/nxp-logo-color.svg" width="125" style="margin-bottom: 40px;" /> </a> <a href="https://www.wolfssl.com"> <img src="../Images/wolfssl_logo_300px.png" width="100" style="margin-bottom: 40px" align=right /> </a>

## wolfSSL MQTT AWS Test using Zephyr RTOS

This demo demonstrates the capabilities of the new FRDM-MCXN947.

### Demo   
Simply connects to an AWS broker, subscribes, and publishes a message.

*This is currently not a stable demo on the FRDM-MCXN947 Board.*

#### Boards:        FRDM-MCXN947
#### Categories:    RTOS, Zephyr, Networking
#### Peripherals:   UART, ETHERNET
#### Toolchains:    Zephyr

## Table of Contents
1. [Software](#step1)
2. [Hardware](#step2)
3. [Setup](#step3)
4. [Project Options](#step4)
5. [Project Flow Chart](#step5)
6. [FAQs](#step6) 
7. [Support](#step7)
8. [Release Notes](#step8)

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

## 2. Hardware<a name="step2"></a>
- [FRDM-MCXN947](https://www.nxp.com/products/processors-and-microcontrollers/arm-microcontrollers/general-purpose-mcus/mcx-arm-cortex-m/mcx-n94x-and-n54x-mcus-with-dual-core-arm-cortex-m33-eiq-neutron-npu-and-edgelock-secure-enclave-core-profile:MCX-N94X-N54X)   
[<img src="Images/FRDM-MCXN947-TOP.jpg" width="300"/>](Images/FRDM-MCXN947-TOP.jpg)
- USB Type-C cable
- Ethernet cable
- Networking/Router
- Personal Computer

## 3. Setup<a name="step3"></a>

### 3.1 Import the Project and Build
1. Follow section 1: `Setup` in the top-level [README](../README.md).
2. Under the "Projects" tab, right-click the project and choose "Build Selected." This should result in no errors.

    [<img src="Images/Setup3-1.png" width="400"/>](Images/Setup3-1.png)

The project should be called `dm-wolfmqtt-publisher-client-with-zephyr`.

### 3.2 Connect Hardware
1. Connect the FRDM-MCXN947 to your computer with the provided USB-C cable.
2. Connect the FRDM-MCXN947 to your network with an Ethernet cable.

### 3.4 Program and Run the Server
1. Flash the `.elf` file to FRDM-MCXN947. This can be done by right-clicking the project and choosing "Flash the Selected Target."
2. Connect to the serial output of the FRDM-MCXN947 via:
    - Screen Command - `screen /dev/tty"MCXN-Port 115200"`
    - Any serial terminal you are familiar with.
3. Press the reset button on the FRDM-MCXN947 board and view the startup message and MQTT demo.

    [<img src="Images/Results.png" width="500"/>](Images/Results.png)

## 4. Project Options<a name="step4"></a>
Currently, there are no extra project options.

## 5. Project Flowchart<a name="step5"></a>
### Overview

```mermaid
flowchart TD
    A[Start mqttsimple_test] --> B[Initialize MQTT Client and Network]
    B --> C{MqttClient_Init Successful?}
    C -- No --> D[Print Initialization Error]
    D --> E[Exit with Error Code]
    C -- Yes --> F[Enable wolfSSL Debugging]
    F --> G[Resolve MQTT Hostname]
    G --> H[MqttClient_NetConnect]
    H --> I{Network Connection Successful?}
    I -- No --> J[Print Connection Error]
    J --> K[Exit with Error Code]
    I -- Yes --> L[Print Network Connection Success]
    L --> M[Prepare MQTT Connection Object]
    M --> N[Set Connection Parameters]
    N --> O[MqttClient_Connect]
    O --> P{MQTT Connection Successful?}
    P -- No --> Q[Print Connection Error]
    Q --> R[Exit with Error Code]
    P -- Yes --> S[Print Broker Connection Success]
    S --> T[Prepare Subscription Object]
    T --> U[Set Subscription Parameters]
    U --> V[MqttClient_Subscribe]
    V --> W{Subscription Successful?}
    W -- No --> X[Print Subscription Error]
    X --> Y[Exit with Error Code]
    W -- Yes --> Z[Print Subscription Success]
    Z --> AA[Prepare Publish Object]
    AA --> AB[Set Publish Parameters]
    AB --> AC[MqttClient_Publish]
    AC --> AD{Publish Successful?}
    AD -- No --> AE[Print Publish Error]
    AE --> AF[Exit with Error Code]
    AD -- Yes --> AG[Print Publish Success]
    AG --> AH[Return Success]
    AH --> AI[End mqttsimple_test]

    subgraph "Initialization"
        B
        C
        D
        E
    end

    subgraph "Network Connection"
        F
        G
        H
        I
        J
        K
        L
    end

    subgraph "MQTT Connection"
        M
        N
        O
        P
        Q
        R
        S
    end

    subgraph "Subscription"
        T
        U
        V
        W
        X
        Y
        Z
    end

    subgraph "Publishing"
        AA
        AB
        AC
        AD
        AE
        AF
        AG
    end

    subgraph "Completion"
        AH
        AI
    end
```


## 6. FAQs<a name="step6"></a>
No FAQs have been identified for this project.

## 7. Support<a name="step7"></a>

#### Project Metadata
<!----- Boards ----->
[![Board badge](https://img.shields.io/badge/Board-FRDM&ndash;MCXN947-blue)](https://github.com/search?q=org%3Anxp-appcodehub+FRDM-MCXN947+in%3Areadme&type=Repositories)

<!----- Categories ----->

<!----- Peripherals ----->
[![Peripheral badge](https://img.shields.io/badge/Peripheral-UART-yellow)](https://github.com/search?q=org%3Anxp-appcodehub+uart+in%3Areadme&type=Repositories) [![Peripheral badge](https://img.shields.io/badge/Peripheral-ETHERNET-yellow)](https://github.com/search?q=org%3Anxp-appcodehub+ethernet+in%3Areadme&type=Repositories)

<!----- Toolchains ----->
[![Toolchain badge](https://img.shields.io/badge/Toolchain-VS%20CODE-orange)](https://github.com/search?q=org%3Anxp-appcodehub+vscode+in%3Areadme&type=Repositories)

Questions regarding the content or correctness of this example can be entered as Issues within this GitHub repository.

>**Warning**: For more general technical questions regarding NXP Microcontrollers and differences in expected functionality, enter your questions on the [NXP Community Forum](https://community.nxp.com/).

## 8. Release Notes<a name="step8"></a>
| Version | Description / Update                           | Date                        |
|:-------:|-----------------------------------------------|----------------------------:|
| 1.0     | Initial release on Application Code Hub      | November 17th 2025 |
