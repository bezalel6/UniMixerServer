using System;

namespace UniMixerServer.Communication.BinaryProtocol {
    /// <summary>
    /// CRC16 calculator using polynomial 0xA001 (reversed 0x8005)
    /// Must match ESP32 implementation exactly
    /// </summary>
    public static class CRC16Calculator {
        private const ushort POLYNOMIAL = 0xA001;
        private const ushort INITIAL_VALUE = 0xFFFF;

        /// <summary>
        /// Calculate CRC16 for the given data
        /// </summary>
        /// <param name="data">Data to calculate CRC for</param>
        /// <returns>16-bit CRC value</returns>
        public static ushort Calculate(byte[] data) {
            if (data == null || data.Length == 0) {
                return INITIAL_VALUE;
            }

            ushort crc = INITIAL_VALUE;

            foreach (byte b in data) {
                crc ^= b;

                for (int i = 0; i < 8; i++) {
                    if ((crc & 0x0001) != 0) {
                        crc = (ushort)((crc >> 1) ^ POLYNOMIAL);
                    }
                    else {
                        crc >>= 1;
                    }
                }
            }

            return crc;
        }

        /// <summary>
        /// Calculate CRC16 for a portion of data
        /// </summary>
        /// <param name="data">Data array</param>
        /// <param name="offset">Starting offset</param>
        /// <param name="length">Length of data to process</param>
        /// <returns>16-bit CRC value</returns>
        public static ushort Calculate(byte[] data, int offset, int length) {
            if (data == null || offset < 0 || length <= 0 || offset + length > data.Length) {
                return INITIAL_VALUE;
            }

            ushort crc = INITIAL_VALUE;

            for (int i = offset; i < offset + length; i++) {
                crc ^= data[i];

                for (int j = 0; j < 8; j++) {
                    if ((crc & 0x0001) != 0) {
                        crc = (ushort)((crc >> 1) ^ POLYNOMIAL);
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
