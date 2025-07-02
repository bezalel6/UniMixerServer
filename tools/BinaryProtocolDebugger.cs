using System;
using System.Collections.Generic;
using System.Text;
using UniMixerServer.Communication.BinaryProtocol;

namespace UniMixerServer.Tools {
    /// <summary>
    /// Diagnostic tool for debugging binary protocol CRC mismatches
    /// </summary>
    public static class BinaryProtocolDebugger {
        private const byte START_MARKER = 0x7E;
        private const byte END_MARKER = 0x7F;
        private const byte ESCAPE_MARKER = 0x7D;
        private const byte ESCAPE_XOR = 0x20;

        /// <summary>
        /// Analyze a binary frame and print detailed information
        /// </summary>
        /// <param name="frameData">Binary frame data</param>
        public static void AnalyzeFrame(byte[] frameData) {
            Console.WriteLine("=== Binary Frame Analysis ===");
            Console.WriteLine($"Frame length: {frameData.Length} bytes");
            Console.WriteLine($"Raw bytes: {BitConverter.ToString(frameData)}");
            Console.WriteLine($"ASCII interpretation: {Encoding.ASCII.GetString(frameData)}");
            Console.WriteLine();

            if (frameData.Length < 8) {
                Console.WriteLine("Frame too short - minimum 8 bytes required");
                return;
            }

            // Check start marker
            if (frameData[0] != START_MARKER) {
                Console.WriteLine($"❌ Invalid start marker: 0x{frameData[0]:X2} (expected 0x{START_MARKER:X2})");
                return;
            }
            Console.WriteLine($"✅ Start marker: 0x{frameData[0]:X2}");

            // Extract length (4 bytes, little-endian)
            var lengthBytes = new byte[4];
            Array.Copy(frameData, 1, lengthBytes, 0, 4);
            uint expectedLength = BitConverter.ToUInt32(lengthBytes, 0);
            Console.WriteLine($"Expected payload length: {expectedLength} bytes");

            // Extract CRC (2 bytes, little-endian)
            var crcBytes = new byte[2];
            Array.Copy(frameData, 5, crcBytes, 0, 2);
            ushort expectedCrc = BitConverter.ToUInt16(crcBytes, 0);
            Console.WriteLine($"Expected CRC: 0x{expectedCrc:X4}");

            // Extract message type
            byte messageType = frameData[7];
            Console.WriteLine($"Message type: 0x{messageType:X2}");

            // Check end marker
            if (frameData[frameData.Length - 1] != END_MARKER) {
                Console.WriteLine($"❌ Invalid end marker: 0x{frameData[frameData.Length - 1]:X2} (expected 0x{END_MARKER:X2})");
                return;
            }
            Console.WriteLine($"✅ End marker: 0x{frameData[frameData.Length - 1]:X2}");

            // Extract and unescape payload
            var escapedPayload = new byte[frameData.Length - 9]; // Remove start, header (7), and end
            Array.Copy(frameData, 8, escapedPayload, 0, escapedPayload.Length);

            var unescapedPayload = UnescapePayload(escapedPayload);
            Console.WriteLine($"Unescaped payload length: {unescapedPayload.Length} bytes");

            if (unescapedPayload.Length != expectedLength) {
                Console.WriteLine($"❌ Payload length mismatch: got {unescapedPayload.Length}, expected {expectedLength}");
            }
            else {
                Console.WriteLine($"✅ Payload length matches");
            }

            // Calculate CRC on unescaped payload
            ushort calculatedCrc = CRC16Calculator.Calculate(unescapedPayload);
            Console.WriteLine($"Calculated CRC: 0x{calculatedCrc:X4}");

            if (calculatedCrc == expectedCrc) {
                Console.WriteLine($"✅ CRC matches");
            }
            else {
                Console.WriteLine($"❌ CRC mismatch: calculated 0x{calculatedCrc:X4}, expected 0x{expectedCrc:X4}");
            }

            // Try to decode payload as JSON
            try {
                string jsonPayload = Encoding.UTF8.GetString(unescapedPayload);
                Console.WriteLine($"JSON payload: {jsonPayload}");
            }
            catch (Exception ex) {
                Console.WriteLine($"❌ Failed to decode payload as UTF-8: {ex.Message}");
            }

            Console.WriteLine("=== End Analysis ===");
            Console.WriteLine();
        }

        /// <summary>
        /// Test different CRC variations to help identify ESP32 implementation differences
        /// </summary>
        /// <param name="payload">The payload data to test</param>
        public static void TestCrcVariations(byte[] payload) {
            Console.WriteLine("=== CRC Variation Tests ===");
            Console.WriteLine($"Payload: {Encoding.UTF8.GetString(payload)}");
            Console.WriteLine($"Payload bytes: {BitConverter.ToString(payload)}");
            Console.WriteLine();

            // Current implementation (CRC-16-ANSI)
            ushort currentCrc = CRC16Calculator.Calculate(payload);
            Console.WriteLine($"Current CRC-16-ANSI (0xA001): 0x{currentCrc:X4}");

            // Test with different polynomials
            ushort crc8005 = CalculateCRC16_8005(payload);
            Console.WriteLine($"CRC-16-CCITT (0x8005): 0x{crc8005:X4}");

            ushort crc1021 = CalculateCRC16_1021(payload);
            Console.WriteLine($"CRC-16-CCITT (0x1021): 0x{crc1021:X4}");

            // Test with different initial values
            ushort crcZeroInit = CalculateCRC16_ZeroInit(payload);
            Console.WriteLine($"CRC-16-ANSI Zero Init: 0x{crcZeroInit:X4}");

            // Test on escaped payload
            var escapedPayload = ApplyEscapeSequences(payload);
            ushort escapedCrc = CRC16Calculator.Calculate(escapedPayload);
            Console.WriteLine($"CRC on escaped payload: 0x{escapedCrc:X4}");

            Console.WriteLine("=== End CRC Tests ===");
            Console.WriteLine();
        }

        /// <summary>
        /// Create a test message frame for debugging
        /// </summary>
        /// <param name="jsonMessage">JSON message to encode</param>
        public static byte[] CreateTestFrame(string jsonMessage) {
            Console.WriteLine($"Creating test frame for: {jsonMessage}");

            var framer = new BinaryProtocolFramer(
                Microsoft.Extensions.Logging.LoggerFactory.Create(builder => { }).CreateLogger("BinaryProtocolFramer"),
                new ProtocolStatistics()
            );

            var frame = framer.EncodeMessage(jsonMessage);

            Console.WriteLine($"Generated frame: {BitConverter.ToString(frame)}");
            AnalyzeFrame(frame);

            return frame;
        }

        private static byte[] UnescapePayload(byte[] escapedData) {
            var result = new List<byte>();
            bool isEscapeNext = false;

            foreach (byte b in escapedData) {
                if (isEscapeNext) {
                    result.Add((byte)(b ^ ESCAPE_XOR));
                    isEscapeNext = false;
                }
                else if (b == ESCAPE_MARKER) {
                    isEscapeNext = true;
                }
                else {
                    result.Add(b);
                }
            }

            return result.ToArray();
        }

        private static byte[] ApplyEscapeSequences(byte[] data) {
            var escaped = new List<byte>();
            foreach (byte b in data) {
                if (b == START_MARKER || b == END_MARKER || b == ESCAPE_MARKER) {
                    escaped.Add(ESCAPE_MARKER);
                    escaped.Add((byte)(b ^ ESCAPE_XOR));
                }
                else {
                    escaped.Add(b);
                }
            }
            return escaped.ToArray();
        }

        // Alternative CRC implementations for testing
        private static ushort CalculateCRC16_8005(byte[] data) {
            ushort crc = 0xFFFF;
            const ushort polynomial = 0x8005;

            foreach (byte b in data) {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++) {
                    if ((crc & 0x8000) != 0) {
                        crc = (ushort)((crc << 1) ^ polynomial);
                    }
                    else {
                        crc <<= 1;
                    }
                }
            }
            return crc;
        }

        private static ushort CalculateCRC16_1021(byte[] data) {
            ushort crc = 0xFFFF;
            const ushort polynomial = 0x1021;

            foreach (byte b in data) {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++) {
                    if ((crc & 0x8000) != 0) {
                        crc = (ushort)((crc << 1) ^ polynomial);
                    }
                    else {
                        crc <<= 1;
                    }
                }
            }
            return crc;
        }

        private static ushort CalculateCRC16_ZeroInit(byte[] data) {
            ushort crc = 0x0000;  // Zero initial value
            const ushort polynomial = 0xA001;

            foreach (byte b in data) {
                crc ^= b;
                for (int i = 0; i < 8; i++) {
                    if ((crc & 0x0001) != 0) {
                        crc = (ushort)((crc >> 1) ^ polynomial);
                    }
                    else {
                        crc >>= 1;
                    }
                }
            }
            return crc;
        }
    }
}
