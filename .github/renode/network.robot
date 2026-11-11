*** Settings ***
Documentation     The network demos end to end, against real clients on the host.
Resource          ${CURDIR}/common.resource
Test Timeout      30 minutes

*** Variables ***
# Host side, prepared by the CI job: a wolfSSL build tree and the PQC demo client.
${WOLFSSL_HOST}       ${CURDIR}/../../wolfssl-host
${PQC_CLIENT}         ${WOLFSSL_HOST}/client-tls13-filetransfer
${APPS}               ${CURDIR}/../..

*** Test Cases ***
TLS Hello Server Answers A wolfSSL Client
    ${ip}=    Boot On Network    sample.wolfssl.tls_hello_server.elf
    Wait For Line On Uart    Test complete    timeout=900
    Wait For Line On Uart    Server Started    timeout=1800
    ${out}=    Host Command Should Succeed    ${WOLFSSL_HOST}/examples/client/client
    ...    -h    ${ip}    -p    11111    -v    4    -A    ${WOLFSSL_HOST}/certs/ca-cert.pem
    ...    cwd=${WOLFSSL_HOST}
    Should Contain    ${out}    I hear ya fa shizzle!
    Wait For Line On Uart    Client connected successfully    timeout=60

PQC TLS Hello Server Receives Both Files
    ${ip}=    Boot On Network    sample.wolfssl.tls_hello_server_pqc.elf
    Wait For Line On Uart    wolfCrypt Test returned 0.    timeout=900
    Wait For Line On Uart    Server Started    timeout=1800
    ${dir}=    Set Variable    ${APPS}/dm-wolfssl-tls-hello-server-pqc-with-zephyr
    ${out}=    Host Command Should Succeed    ${PQC_CLIENT}    ${ip}
    ...    ${dir}/mldsa44_entity_cert.pem    ${dir}/mldsa44_entity_key.pem
    ...    cwd=${dir}
    Should Contain    ${out}    Both files sent successfully
    Wait For Line On Uart    Both certificate and key received successfully    timeout=60

wolfSSH RGB Server Accepts Key Login And Commands
    ${ip}=    Boot On Network    sample.wolfssh.rgb_server.elf
    Wait For Line On Uart    The Server is ready for connections    timeout=600
    ${key}=    Set Variable    ${TEMPDIR}/rgb_id_rsa
    Copy Test Key    ${APPS}/dm-wolfssh-rgb-server-with-zephyr/Test_Keys/id_rsa_real    ${key}
    # The server drops the session on 'close', so ssh exits 255 by design.
    ${out}=    Host Command Should Succeed    ssh    -i    ${key}    -p    11111
    ...    -o    StrictHostKeyChecking=no    -o    UserKnownHostsFile=/dev/null
    ...    -o    ConnectTimeout=120    zephyr@${ip}    stdin=red\nclose\n    rc=255
    Should Contain    ${out}    Red Led Toggling: Off
    Wait For Line On Uart    Public key authentication successful    timeout=60
    Wait For Line On Uart    Received: red    timeout=60

wolfMQTT Publisher Publishes To The AWS IoT Demo Broker
    # Needs internet: the host NATs the TAP subnet and DHCP hands out a public DNS.
    Boot On Network    sample.wolfmqtt.publisher_client.elf
    Wait For Line On Uart    Time set using NTP    timeout=2400
    Wait For Line On Uart    MQTT Broker Connect Success    timeout=120
    Wait For Line On Uart    MQTT Subscribe Success    timeout=120
    Wait For Line On Uart    MQTT Publish: Topic wolfMQTT/example/testTopic    timeout=120

*** Keywords ***
Copy Test Key
    [Arguments]    ${src}    ${dst}
    Run Process    cp    ${src}    ${dst}
    Run Process    chmod    600    ${dst}
