*** Settings ***
Documentation     Shared keywords for the FRDM-MCXN947 Renode tests.
Library           Process

*** Variables ***
${PLATFORM}       ${CURDIR}/frdm_mcxn947.repl
${UART}           sysbus.lpuart4
${ELF_DIR}        ${CURDIR}/../../renode-elfs
${TAP}            tap0

*** Keywords ***
Create FRDM-MCXN947 With
    [Arguments]    ${elf}
    Execute Command    include @${CURDIR}/NXP_ENET_QOS.cs
    Execute Command    mach create "frdm_mcxn947"
    Execute Command    machine LoadPlatformDescription @${PLATFORM}
    Execute Command    sysbus LoadELF @${ELF_DIR}/${elf}
    Execute Command    cpu VectorTableOffset 0x10000000
    Create Terminal Tester    ${UART}    defaultPauseEmulation=False

Boot On Network
    [Documentation]    Boot ${elf} attached to the host TAP; returns the DHCP address it prints.
    [Arguments]    ${elf}
    Create FRDM-MCXN947 With    ${elf}
    Attach To Host Network
    Start Emulation
    ${line}=    Wait For Line On Uart    IP Address is: (\\d+\\.\\d+\\.\\d+\\.\\d+)    treatAsRegex=true    timeout=120
    RETURN    ${line.Groups[0]}

Attach To Host Network
    Execute Command    emulation CreateSwitch "switch"
    Execute Command    emulation CreateTap "${TAP}" "tap"
    Execute Command    connector Connect host.tap switch
    Execute Command    connector Connect sysbus.enet switch

Host Command Should Succeed
    [Documentation]    Run a host-side client; fail with its output unless it exits with ${rc}.
    [Arguments]    @{cmd}    ${stdin}=${NONE}    ${timeout}=180s    ${cwd}=${CURDIR}    ${rc}=0
    ${r}=    Run Process    @{cmd}    stdin=${stdin}    timeout=${timeout}    stderr=STDOUT    cwd=${cwd}
    Log    ${r.stdout}
    Should Be Equal As Integers    ${r.rc}    ${rc}    host client failed:\n${r.stdout}
    RETURN    ${r.stdout}
