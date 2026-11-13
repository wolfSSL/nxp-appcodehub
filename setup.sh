#!/bin/bash

# Function to sanitize the directory name by removing the trailing slash if it exists
sanitize_directory() {
    local dir_name="$1"
    # Remove trailing slash using string manipulation
    sanitized_dir="${dir_name%/}"
    echo "$sanitized_dir"
}

# Board: the argument, else sample.yaml's first integration platform, else cpu0.
set_board_id() {
    board_id="${2:-}"
    if [ -z "$board_id" ] && [ -f "$1/sample.yaml" ]; then
        board_id="$(awk '/integration_platforms:/ {f=1; next} f && /- / {print $2; exit}' "$1/sample.yaml")"
    fi
    board_id="${board_id:-frdm_mcxn947/mcxn947/cpu0}"
    case "$board_id" in
        */ns) trustzone_type="nonsecure" ;;
        *) trustzone_type="none" ;;
    esac
}

# Function to prompt the user and check the response
prompt_user() {
    local prompt_message="$1"
    local response
    read -p "$prompt_message (y/n): " response
    if [[ "$response" != "y" && "$response" != "Y" ]]; then
        echo "Operation cancelled."
        exit 1
    fi
}

# Create the .vscode directory if it doesn't exist
create_vscode_directory() {
    local subfolder="$1"
    if [ -d "$subfolder/.vscode" ]; then
        prompt_user "The directory '$subfolder/.vscode' already exists. Do you want to remake it?"
        rm -rf "$subfolder/.vscode"
    fi
    mkdir "$subfolder/.vscode"
    echo "Created .vscode directory in $subfolder."
}

# Create cmake-kits.json
create_cmake_kits() {
    local subfolder="$1"
    cat <<EOF > "$subfolder/.vscode/cmake-kits.json"
[
  {
    "name": "Zephyr build tool",
    "environmentVariables": {
      "ZEPHYR_BASE": "",
      "ZEPHYR_SDK_INSTALL_DIR": ""
    },
    "cmakeSettings": {
      "BOARD": "$board_id"
    },
    "keep": true
  }
]
EOF
    echo "cmake-kits.json created."
}

# Create cmake-variants.json
create_cmake_variants() {
    local subfolder="$1"
    cat <<EOF > "$subfolder/.vscode/cmake-variants.json"
{
  "build_type": {
    "default": "debug",
    "choices": {
      "debug": {
        "short": "debug",
        "buildType": "debug"
      },
      "release": {
        "short": "release",
        "buildType": "release"
      }
    }
  }
}
EOF
    echo "cmake-variants.json created."
}

# Create launch.json
create_launch_json() {
    local subfolder="$1"
    cat <<EOF > "$subfolder/.vscode/launch.json"
{
  "configurations": [
    {
      "type": "cppdbg",
      "name": "Debug project configuration",
      "request": "launch",
      "cwd": "\${workspaceRoot}",
      "MIMode": "gdb",
      "setupCommands": [
        {"text": "set remotetimeout 600"},
        {"text": "set debug-file-directory"}
      ],
      "program": "",
      "miDebuggerServerAddress": "",
      "variables": {
        "mcuxStopAtSymbol": "main",
        "mcuxSerialNumber": "",
        "mcuxAttach": "false",
        "mcuxRemoteProbeType": ""
      },
      "logging": {
        "engineLogging": false
      }
    }
  ]
}
EOF
    echo "launch.json created."
}

# Create mcuxpresso-tools.json
create_mcuxpresso_tools() {
    local subfolder="$1"
    cat <<EOF > "$subfolder/.vscode/mcuxpresso-tools.json"
{
  "version": "1.1",
  "toolchainPath": "",
  "toolchainVersion": "",
  "linkedProjects": [],
  "trustZoneType": "$trustzone_type",
  "multicoreType": "none",
  "debug": {
    "linkserver": {},
    "pemicro": {},
    "segger": {}
  },
  "projectType": "zephyr-workspace",
  "sdk": {
    "boardId": "$board_id",
    "version": "",
    "path": ""
  }
}
EOF
    echo "mcuxpresso-tools.json created."
}

# Create settings.json
create_settings_json() {
    local subfolder="$1"
    cat <<EOF > "$subfolder/.vscode/settings.json"
{
  "cmake.configureOnOpen": false,
  "C_Cpp.errorSquiggles": "disabled",
  "cmake.preferredGenerators": [
    "Ninja",
    "Unix Makefiles",
    "MinGW Makefiles"
  ],
  "C_Cpp.default.configurationProvider": "ms-vscode.cmake-tools",
  "cmake.sourceDirectory": "\${workspaceFolder}"
}
EOF
    echo "settings.json created."
}

# Main function to orchestrate the creation of all files
create_vscode_configs() {
    local subfolder="$(sanitize_directory "$1")"

    # Check if subfolder name was provided
    if [ -z "$subfolder" ]; then
        echo "Error: Please provide a subfolder name as an argument."
        exit 1
    fi

    # Set the board ID
    set_board_id "$subfolder" "${2:-}"

    # Check if the input directory exists
    if [ ! -d "$subfolder" ]; then
        echo "Error: The directory '$subfolder' does not exist."
        exit 1
    fi

    create_vscode_directory "$subfolder"
    create_cmake_kits "$subfolder"
    create_cmake_variants "$subfolder"
    create_launch_json "$subfolder"
    create_mcuxpresso_tools "$subfolder"
    create_settings_json "$subfolder"
}

# Call the main function with the script arguments
create_vscode_configs "$@"