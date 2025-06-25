using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;

namespace UniMixerServer.Communication.BinaryProtocol {
    /// <summary>
    /// Handles binary frame construction and deconstruction with escape sequences
    /// Frame format: [0x7E][LENGTH_4_BYTES][CRC_2_BYTES][TYPE_1_BYTE][ESCAPED_PAYLOAD][0x7F]
    /// </summary>
    public class BinaryProtocolFramer {
        private const byte START_MARKER = 0x7E;
        private const byte END_MARKER = 0x7F;
        private const byte ESCAPE_MARKER = 0x7D;
        private const byte ESCAPE_XOR = 0x20;
        private const byte JSON_MESSAGE_TYPE = 0x01;
        private const int MAX_PAYLOAD_SIZE = 4096;
        private const int HEADER_SIZE = 7; // LENGTH(4) + CRC(2) + TYPE(1)
        private const int MESSAGE_TIMEOUT_MS = 1000;

        private readonly ILogger _logger;
        private readonly ProtocolStatistics _statistics;

        // Reception state machine
        private ReceiveState _currentState = ReceiveState.WaitingForStart;
        private readonly List<byte> _headerBuffer = new List<byte>();
        private readonly List<byte> _payloadBuffer = new List<byte>();
        private uint _expectedPayloadLength;
        private ushort _expectedCrc;
        private byte _messageType;
        private DateTime _messageStartTime;
        private bool _isEscapeNext;

        public BinaryProtocolFramer(ILogger logger, ProtocolStatistics statistics) {
            _logger = logger;
            _statistics = statistics;
        }

        /// <summary>
        /// Current reception state
        /// </summary>
        public ReceiveState CurrentState => _currentState;

        /// <summary>
        /// Encode a JSON payload into a binary frame
        /// </summary>
        /// <param name="jsonPayload">JSON payload as string</param>
        /// <returns>Complete binary frame</returns>
        public byte[] EncodeMessage(string jsonPayload) {
            if (string.IsNullOrEmpty(jsonPayload)) {
                throw new ArgumentException("JSON payload cannot be null or empty", nameof(jsonPayload));
            }

            // Convert JSON to bytes
            var payloadBytes = Encoding.UTF8.GetBytes(jsonPayload);

            if (payloadBytes.Length > MAX_PAYLOAD_SIZE) {
                throw new ArgumentException($"Payload exceeds maximum size of {MAX_PAYLOAD_SIZE} bytes", nameof(jsonPayload));
            }

            // Calculate CRC16 of original payload
            ushort crc = CRC16Calculator.Calculate(payloadBytes);

            // Apply escape sequences to payload
            var escapedPayload = ApplyEscapeSequences(payloadBytes);

            // Build frame
            var frame = new List<byte>();

            // Start marker
            frame.Add(START_MARKER);

            // Length (4 bytes, little-endian) - length of ORIGINAL payload before escaping
            var lengthBytes = BitConverter.GetBytes((uint)payloadBytes.Length);
            if (!BitConverter.IsLittleEndian) {
                Array.Reverse(lengthBytes);
            }
            frame.AddRange(lengthBytes);

            // CRC (2 bytes, little-endian)
            var crcBytes = BitConverter.GetBytes(crc);
            if (!BitConverter.IsLittleEndian) {
                Array.Reverse(crcBytes);
            }
            frame.AddRange(crcBytes);

            // Message type
            frame.Add(JSON_MESSAGE_TYPE);

            // Escaped payload
            frame.AddRange(escapedPayload);

            // End marker
            frame.Add(END_MARKER);

            _statistics.IncrementMessagesSent();
            _statistics.AddBytesTransmitted(frame.Count);

            return frame.ToArray();
        }

        /// <summary>
        /// Process incoming bytes through the state machine
        /// </summary>
        /// <param name="data">Incoming byte data</param>
        /// <returns>List of decoded JSON messages</returns>
        public List<string> ProcessIncomingBytes(byte[] data) {
            var messages = new List<string>();

            foreach (byte b in data) {
                // Check for timeout
                if (_currentState != ReceiveState.WaitingForStart &&
                    DateTime.UtcNow - _messageStartTime > TimeSpan.FromMilliseconds(MESSAGE_TIMEOUT_MS)) {
                    _logger.LogWarning("Message timeout - resetting state machine");
                    _statistics.IncrementTimeoutErrors();
                    ResetStateMachine();
                }

                try {
                    switch (_currentState) {
                        case ReceiveState.WaitingForStart:
                            if (b == START_MARKER) {
                                _currentState = ReceiveState.ReadingHeader;
                                _headerBuffer.Clear();
                                _payloadBuffer.Clear();
                                _messageStartTime = DateTime.UtcNow;
                                _isEscapeNext = false;
                                _logger.LogTrace("Found start marker, reading header");
                            }
                            break;

                        case ReceiveState.ReadingHeader:
                            _headerBuffer.Add(b);
                            if (_headerBuffer.Count >= HEADER_SIZE) {
                                if (ProcessHeader()) {
                                    _currentState = ReceiveState.ReadingPayload;
                                    _logger.LogTrace("Header processed, reading payload of {Length} bytes", _expectedPayloadLength);
                                }
                                else {
                                    _statistics.IncrementFramingErrors();
                                    ResetStateMachine();
                                }
                            }
                            break;

                        case ReceiveState.ReadingPayload:
                            if (b == END_MARKER && !_isEscapeNext) {
                                // Message complete
                                var decodedMessage = ProcessCompleteMessage();
                                if (decodedMessage != null) {
                                    messages.Add(decodedMessage);
                                    _statistics.IncrementMessagesReceived();
                                    _statistics.AddBytesReceived(_payloadBuffer.Count + HEADER_SIZE + 2); // +2 for start/end markers
                                }
                                ResetStateMachine();
                            }
                            else {
                                ProcessPayloadByte(b);
                            }
                            break;
                    }
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "Error processing byte 0x{Byte:X2} in state {State}", b, _currentState);
                    _statistics.IncrementFramingErrors();
                    ResetStateMachine();
                }
            }

            return messages;
        }

        private bool ProcessHeader() {
            if (_headerBuffer.Count < HEADER_SIZE) {
                return false;
            }

            try {
                // Extract length (4 bytes, little-endian)
                var lengthBytes = new byte[4];
                _headerBuffer.CopyTo(0, lengthBytes, 0, 4);
                if (!BitConverter.IsLittleEndian) {
                    Array.Reverse(lengthBytes);
                }
                _expectedPayloadLength = BitConverter.ToUInt32(lengthBytes, 0);

                // Extract CRC (2 bytes, little-endian)
                var crcBytes = new byte[2];
                _headerBuffer.CopyTo(4, crcBytes, 0, 2);
                if (!BitConverter.IsLittleEndian) {
                    Array.Reverse(crcBytes);
                }
                _expectedCrc = BitConverter.ToUInt16(crcBytes, 0);

                // Extract message type
                _messageType = _headerBuffer[6];

                // Validate length
                if (_expectedPayloadLength > MAX_PAYLOAD_SIZE) {
                    _logger.LogWarning("Payload length {Length} exceeds maximum {Max}", _expectedPayloadLength, MAX_PAYLOAD_SIZE);
                    _statistics.IncrementBufferOverflowErrors();
                    return false;
                }

                _logger.LogTrace("Header: Length={Length}, CRC=0x{CRC:X4}, Type=0x{Type:X2}",
                    _expectedPayloadLength, _expectedCrc, _messageType);

                return true;
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error processing header");
                return false;
            }
        }

        private void ProcessPayloadByte(byte b) {
            if (_isEscapeNext) {
                // Un-escape the byte
                byte unescaped = (byte)(b ^ ESCAPE_XOR);
                _payloadBuffer.Add(unescaped);
                _isEscapeNext = false;
                _logger.LogTrace("Un-escaped byte: 0x{Original:X2} -> 0x{Unescaped:X2}", b, unescaped);
            }
            else if (b == ESCAPE_MARKER) {
                // Next byte should be un-escaped
                _isEscapeNext = true;
            }
            else {
                // Regular byte
                _payloadBuffer.Add(b);
            }

            // Check if we've received enough unescaped bytes
            if (_payloadBuffer.Count > _expectedPayloadLength) {
                _logger.LogWarning("Payload buffer overflow - received {Actual} bytes, expected {Expected}",
                    _payloadBuffer.Count, _expectedPayloadLength);
                _statistics.IncrementBufferOverflowErrors();
                ResetStateMachine();
            }
        }

        private string? ProcessCompleteMessage() {
            try {
                // Verify we have the exact expected payload length
                if (_payloadBuffer.Count != _expectedPayloadLength) {
                    _logger.LogWarning("Payload length mismatch - received {Actual} bytes, expected {Expected}",
                        _payloadBuffer.Count, _expectedPayloadLength);
                    _statistics.IncrementFramingErrors();
                    return null;
                }

                // Verify CRC16
                var payloadArray = _payloadBuffer.ToArray();
                ushort calculatedCrc = CRC16Calculator.Calculate(payloadArray);

                if (calculatedCrc != _expectedCrc) {
                    _logger.LogWarning("CRC mismatch - calculated 0x{Calculated:X4}, expected 0x{Expected:X4}",
                        calculatedCrc, _expectedCrc);
                    _statistics.IncrementCrcErrors();
                    return null;
                }

                // Verify message type
                if (_messageType != JSON_MESSAGE_TYPE) {
                    _logger.LogWarning("Unsupported message type: 0x{Type:X2}", _messageType);
                    _statistics.IncrementFramingErrors();
                    return null;
                }

                // Convert payload to JSON string
                string jsonMessage = Encoding.UTF8.GetString(payloadArray);
                _logger.LogTrace("Successfully decoded message: {Length} bytes, CRC OK", payloadArray.Length);

                return jsonMessage;
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error processing complete message");
                _statistics.IncrementFramingErrors();
                return null;
            }
        }

        private byte[] ApplyEscapeSequences(byte[] data) {
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

        private void ResetStateMachine() {
            _currentState = ReceiveState.WaitingForStart;
            _headerBuffer.Clear();
            _payloadBuffer.Clear();
            _isEscapeNext = false;
            _expectedPayloadLength = 0;
            _expectedCrc = 0;
            _messageType = 0;
        }

        /// <summary>
        /// Get current statistics
        /// </summary>
        public ProtocolStatistics Statistics => _statistics;
    }

    /// <summary>
    /// Reception state machine states
    /// </summary>
    public enum ReceiveState {
        WaitingForStart,
        ReadingHeader,
        ReadingPayload
    }
}
