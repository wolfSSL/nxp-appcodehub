*** Settings ***
Documentation     Every app boots on the emulated FRDM-MCXN947 and reaches DHCP.
Resource          ${CURDIR}/common.resource
Test Timeout      5 minutes

*** Test Cases ***
TLS Hello Server Boots
    Create FRDM-MCXN947 With    sample.wolfssl.tls_hello_server.elf
    Start Emulation
    Wait For Line On Uart    Running wolfSSL example from the frdm_mcxn947    timeout=60
    Wait For Line On Uart    Starting DHCP to obtain IP address    timeout=60

PQC TLS Hello Server Boots
    Create FRDM-MCXN947 With    sample.wolfssl.tls_hello_server_pqc.elf
    Start Emulation
    Wait For Line On Uart    Running wolfSSL example from the frdm_mcxn947    timeout=60
    Wait For Line On Uart    Starting DHCP to obtain IP address    timeout=60

wolfSSH RGB Server Boots
    Create FRDM-MCXN947 With    sample.wolfssh.rgb_server.elf
    Start Emulation
    Wait For Line On Uart    Running wolfSSH example from the frdm_mcxn947    timeout=60
    Wait For Line On Uart    Starting DHCP to obtain IP address    timeout=60

wolfMQTT Publisher Boots
    Create FRDM-MCXN947 With    sample.wolfmqtt.publisher_client.elf
    Start Emulation
    Wait For Line On Uart    Running wolfMQTT example from the frdm_mcxn947    timeout=60
    Wait For Line On Uart    Starting DHCP to obtain IP address    timeout=60

wolfTPM Demo Boots
    Create FRDM-MCXN947 With    sample.wolftpm.tls.elf
    Start Emulation
    Wait For Line On Uart    Starting DHCP to obtain IP address    timeout=60
