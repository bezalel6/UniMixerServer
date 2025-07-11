# UniMixerServer

A C# server application for handling ESP32-S3 communication via serial and MQTT protocols, with built-in exception decoding capabilities.

## Features

- **ESP32-S3 Support**: Modern RISC-V architecture support with proper exception decoding
- **Serial Communication**: Direct serial port communication with binary protocol support
- **MQTT Communication**: Publish/subscribe messaging with binary and JSON protocols
- **Exception Decoding**: Automatic crash detection and decoding for ESP32-S3 firmware
- **Binary Protocol**: Efficient binary message format with CRC16 validation
- **Logging**: Comprehensive logging with different levels and file rotation

## ESP32-S3 Exception Decoding

The application includes a modern ESP32-S3 exception decoder that automatically detects and decodes crashes from your ESP32-S3 firmware.

### Features

- **Automatic Detection**: Detects Guru Meditation errors, panics, and access faults
- **RISC-V Support**: Properly handles ESP32-S3's RISC-V architecture
- **Modern Toolchain**: Uses ESP-IDF's `riscv32-esp-elf-addr2line` for accurate decoding
- **Manual Analysis**: Provides detailed crash analysis even without toolchain
- **Crash Logging**: Saves decoded crashes to debug_files/ directory

### Setup

1. **Install ESP-IDF**: Download and install ESP-IDF v5.x with ESP32-S3 support
2. **ELF File**: Place your firmware.elf file in the `debug_files/` directory
3. **Toolchain**: The decoder will automatically find your ESP-IDF toolchain

### Supported Toolchain Paths

The decoder automatically searches for toolchains in these locations:
- `~/.espressif/tools/riscv32-esp-elf/` (ESP-IDF v5.x default)
- `~/.platformio/packages/toolchain-riscv32-esp/` (PlatformIO)
- `C:\Program Files\Espressif\tools\riscv32-esp-elf\` (System installation)
- `C:\esp\esp-idf\tools\riscv32-esp-elf\` (Manual installation)

### Usage

The exception decoder runs automatically when the application detects crash patterns in the serial data. When a crash is detected:

1. **Automatic Capture**: The decoder captures the complete crash dump
2. **Address Extraction**: Extracts MEPC, RA, and backtrace addresses
3. **Symbol Decoding**: Uses addr2line to decode addresses to function names and line numbers
4. **Manual Analysis**: Provides RISC-V register analysis and crash cause description
5. **File Logging**: Saves the decoded crash to a timestamped file

### Example Output

```
🚨 ESP32-S3 CRASH DECODED SUCCESSFULLY:
=====================================
0x420A5D78: app_main at /path/to/src/main.cpp:123
0x42053CF4: main_task at /path/to/esp-idf/components/freertos/app_startup.c:208
0x40384538: vPortTaskWrapper at /path/to/esp-idf/components/freertos/FreeRTOS-Kernel/portable/riscv/port.c:234
=====================================
```

### Manual Decoding

If you need to manually decode a crash file:

```bash
# Using ESP-IDF directly
riscv32-esp-elf-addr2line -pfiaC -e firmware.elf 0x420A5D78

# Using idf.py monitor (automatically decodes crashes)
idf.py monitor
```

### Troubleshooting

- **No Toolchain Found**: Install ESP-IDF with ESP32-S3 support
- **ELF File Missing**: Ensure firmware.elf is in debug_files/ directory
- **Decoding Fails**: Check that ELF file matches the crashed firmware version

## Configuration

The application uses `appsettings.json` for configuration. Key settings include:

- Serial port settings (baud rate, timeout, etc.)
- MQTT broker configuration
- Logging levels and file paths
- Protocol settings

## Development

### Prerequisites

- .NET 8.0 SDK
- ESP-IDF v5.x (for exception decoding)
- Visual Studio or VS Code

### Building

```bash
dotnet build
```

### Running

```bash
dotnet run
```

## License

This project is licensed under the MIT License.
