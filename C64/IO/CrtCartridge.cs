// ============================================================================
// Project:     C64
// File:        CrtCartridge.cs
// Description: C64 CRT cartridge image parser and mapper, including standard
//              8K/16K/Ultimax cartridges and EasyFlash banking.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Commodore 64 and related ROMs are property of their respective
//              rights holders. This emulator is for educational purposes only.
// ============================================================================

using System.Text;

namespace C64
{
    /// <summary>Represents a loaded C64 cartridge image in CRT format.</summary>
    internal sealed class CrtCartridge
    {
        private const int HardwareNormal = 0;
        private const int HardwareMagicDesk = 19;
        private const int HardwareEasyFlash = 32;
        private const int BankSize = 0x2000;
        private const int EasyFlashBankCount = 64;

        private readonly Dictionary<int, byte[]> romlBanks = new();
        private readonly Dictionary<int, byte[]> romhBanks = new();
        private readonly byte[] easyFlashRam = new byte[0x100];
        private readonly bool headerExromLow;
        private readonly bool headerGameLow;

        private int activeBank;
        private byte easyFlashControl;
        private bool magicDeskDisabled;

        private CrtCartridge(string name, int hardwareType, bool exromLow, bool gameLow)
        {
            Name = name;
            HardwareType = hardwareType;
            headerExromLow = exromLow;
            headerGameLow = gameLow;
            Reset();
        }

        /// <summary>Gets the cartridge display name from the CRT header.</summary>
        public string Name { get; }

        /// <summary>Gets the CRT hardware type id.</summary>
        public int HardwareType { get; }

        /// <summary>Gets a short user-facing hardware type name.</summary>
        public string HardwareName => HardwareType switch
        {
            HardwareNormal => "Normal cartridge",
            HardwareMagicDesk => "Magic Desk",
            HardwareEasyFlash => "EasyFlash",
            _ => $"CRT type {HardwareType}"
        };

        /// <summary>Parses a CRT cartridge image.</summary>
        /// <param name="raw">The raw CRT file bytes.</param>
        /// <returns>The parsed cartridge.</returns>
        public static CrtCartridge Parse(byte[] raw)
        {
            if (raw.Length < 0x40)
                throw new InvalidDataException("CRT file is too small to contain a valid header.");
            if (Encoding.ASCII.GetString(raw, 0, 16) != "C64 CARTRIDGE   ")
                throw new InvalidDataException("Not a valid CRT file (bad cartridge header).");

            int headerLength = checked((int)ReadBe32(raw, 0x10));
            if (headerLength < 0x40 || headerLength > raw.Length)
                throw new InvalidDataException("CRT file has an invalid header length.");

            int hardwareType = ReadBe16(raw, 0x16);
            if (hardwareType is not HardwareNormal and not HardwareMagicDesk and not HardwareEasyFlash)
                throw new NotSupportedException($"CRT hardware type {hardwareType} is not supported yet.");

            bool exromLow = raw[0x18] == 0;
            bool gameLow = raw[0x19] == 0;
            string name = DecodeText(raw, 0x20, 32);
            var cartridge = new CrtCartridge(name, hardwareType, exromLow, gameLow);

            int offset = headerLength;
            while (offset < raw.Length)
            {
                if (offset + 16 > raw.Length)
                    throw new InvalidDataException("CRT file has a truncated CHIP packet.");
                if (Encoding.ASCII.GetString(raw, offset, 4) != "CHIP")
                    throw new InvalidDataException($"CRT file has an invalid CHIP packet at offset {offset}.");

                int packetLength = checked((int)ReadBe32(raw, offset + 4));
                int chipType = ReadBe16(raw, offset + 8);
                int bank = ReadBe16(raw, offset + 10);
                int loadAddress = ReadBe16(raw, offset + 12);
                int imageSize = ReadBe16(raw, offset + 14);

                if (packetLength < 16 || offset + packetLength > raw.Length || imageSize > packetLength - 16)
                    throw new InvalidDataException("CRT file has an invalid CHIP packet size.");
                if (chipType > 2)
                    throw new NotSupportedException($"CRT CHIP type {chipType} is not supported.");
                if (hardwareType == HardwareEasyFlash && bank >= EasyFlashBankCount)
                    throw new InvalidDataException("EasyFlash CRT bank number is out of range.");

                cartridge.AddChip(bank, loadAddress, raw, offset + 16, imageSize);
                offset += packetLength;
            }

            if (cartridge.romlBanks.Count == 0 && cartridge.romhBanks.Count == 0)
                throw new InvalidDataException("CRT file contains no usable ROM chips.");

            cartridge.Reset();
            return cartridge;
        }

        /// <summary>Resets the cartridge's bank and control-register state.</summary>
        public void Reset()
        {
            activeBank = 0;
            easyFlashControl = 0x00;
            magicDeskDisabled = false;
        }

        /// <summary>Reads a CPU-visible cartridge ROM byte if the current mapping exposes one.</summary>
        /// <param name="addr">The CPU address being read.</param>
        /// <param name="processorPort">The effective $0001 processor-port value.</param>
        /// <returns>A cartridge byte, or null when the cartridge is not mapped at the address.</returns>
        public byte? ReadMemory(ulong addr, byte processorPort)
        {
            int address = (int)(addr & 0xFFFF);
            if (!TryGetVisibleRom(address, processorPort, out Dictionary<int, byte[]>? banks, out int offset) || banks is null)
                return null;

            return ReadRom(banks, activeBank, offset);
        }

        /// <summary>Handles writes to mapped cartridge ROM space.</summary>
        /// <param name="addr">The CPU address being written.</param>
        /// <param name="processorPort">The effective $0001 processor-port value.</param>
        /// <param name="value">The byte being written.</param>
        /// <returns>True when cartridge ROM was visible and observed the write.</returns>
        public bool WriteMemory(ulong addr, byte processorPort, byte value)
        {
            int address = (int)(addr & 0xFFFF);
            if (!TryGetVisibleRom(address, processorPort, out _, out _))
                return false;

            /// EasyFlash flash programming is intentionally conservative for
            /// now: mapped cartridge writes are observed here, while the
            /// memory backend still writes the byte into C64 RAM underneath.
            /// Flash persistence and command-state emulation are a separate
            /// compatibility step.
            return true;
        }

        /// <summary>Handles cartridge I/O reads.</summary>
        /// <param name="addr">The CPU address being read.</param>
        /// <returns>A cartridge I/O byte, or null when the cartridge does not handle the read.</returns>
        public byte? ReadIo(ulong addr)
        {
            int address = (int)(addr & 0xFFFF);
            if (HardwareType == HardwareEasyFlash && address is >= 0xDF00 and <= 0xDFFF)
                return easyFlashRam[address & 0xFF];

            return null;
        }

        /// <summary>Handles cartridge I/O writes.</summary>
        /// <param name="addr">The CPU address being written.</param>
        /// <param name="value">The byte being written.</param>
        /// <returns>True when the cartridge consumed the write.</returns>
        public bool WriteIo(ulong addr, byte value)
        {
            int address = (int)(addr & 0xFFFF);
            if (HardwareType == HardwareMagicDesk)
            {
                if (address is >= 0xDE00 and <= 0xDEFF)
                {
                    magicDeskDisabled = (value & 0x80) != 0;
                    if (!magicDeskDisabled)
                        activeBank = value & 0x7F;
                    return true;
                }

                return false;
            }

            if (HardwareType != HardwareEasyFlash)
                return false;

            if (address == 0xDE00)
            {
                activeBank = value & 0x3F;
                return true;
            }

            if (address == 0xDE02)
            {
                easyFlashControl = value;
                return true;
            }

            if (address is >= 0xDF00 and <= 0xDFFF)
            {
                easyFlashRam[address & 0xFF] = value;
                return true;
            }

            return false;
        }

        private void AddChip(int bank, int loadAddress, byte[] source, int sourceOffset, int imageSize)
        {
            for (int i = 0; i < imageSize; i++)
            {
                int address = (loadAddress + i) & 0xFFFF;
                int offset = address & (BankSize - 1);
                if (address is >= 0x8000 and < 0xA000)
                    GetOrCreateBank(romlBanks, bank)[offset] = source[sourceOffset + i];
                else if (address is >= 0xA000 and < 0xC000)
                    GetOrCreateBank(romhBanks, bank)[offset] = source[sourceOffset + i];
                else if (address >= 0xE000)
                    GetOrCreateBank(romhBanks, bank)[offset] = source[sourceOffset + i];
            }
        }

        private CartridgeMode GetMode()
        {
            bool gameLow;
            bool exromLow;

            if (HardwareType == HardwareMagicDesk)
            {
                gameLow = false;
                exromLow = !magicDeskDisabled;
            }
            else if (HardwareType == HardwareEasyFlash)
            {
                bool gameModeControlled = (easyFlashControl & 0x04) != 0;
                gameLow = gameModeControlled ? (easyFlashControl & 0x01) != 0 : true;
                exromLow = (easyFlashControl & 0x02) != 0;
            }
            else
            {
                gameLow = headerGameLow;
                exromLow = headerExromLow;
            }

            return (gameLow, exromLow) switch
            {
                (false, false) => CartridgeMode.Invisible,
                (false, true) => CartridgeMode.EightK,
                (true, true) => CartridgeMode.SixteenK,
                (true, false) => CartridgeMode.Ultimax
            };
        }

        private bool TryGetVisibleRom(
            int address,
            byte processorPort,
            out Dictionary<int, byte[]>? banks,
            out int offset)
        {
            CartridgeMode mode = GetMode();
            int loHi = processorPort & 0x03;
            bool loram = (processorPort & 0x01) != 0;
            bool hiram = (processorPort & 0x02) != 0;
            banks = null;
            offset = 0;

            switch (mode)
            {
                case CartridgeMode.EightK when loHi == 0x03 && address is >= 0x8000 and < 0xA000:
                    banks = romlBanks;
                    offset = address - 0x8000;
                    return true;

                case CartridgeMode.SixteenK when loram && hiram && address is >= 0x8000 and < 0xA000:
                    banks = romlBanks;
                    offset = address - 0x8000;
                    return true;

                case CartridgeMode.SixteenK when hiram && address is >= 0xA000 and < 0xC000:
                    banks = romhBanks;
                    offset = address - 0xA000;
                    return true;

                case CartridgeMode.Ultimax when address is >= 0x8000 and < 0xA000:
                    banks = romlBanks;
                    offset = address - 0x8000;
                    return true;

                case CartridgeMode.Ultimax when address >= 0xE000:
                    banks = romhBanks;
                    offset = address - 0xE000;
                    return true;

                default:
                    return false;
            }
        }

        private static byte ReadRom(Dictionary<int, byte[]> banks, int bank, int offset)
        {
            return banks.TryGetValue(bank, out byte[]? rom) ? rom[offset & (BankSize - 1)] : (byte)0xFF;
        }

        private static byte[] GetOrCreateBank(Dictionary<int, byte[]> banks, int bank)
        {
            if (banks.TryGetValue(bank, out byte[]? data))
                return data;

            data = new byte[BankSize];
            Array.Fill(data, (byte)0xFF);
            banks[bank] = data;
            return data;
        }

        private static ushort ReadBe16(byte[] raw, int offset)
        {
            if (offset + 2 > raw.Length)
                throw new InvalidDataException("CRT file header is truncated.");
            return (ushort)((raw[offset] << 8) | raw[offset + 1]);
        }

        private static uint ReadBe32(byte[] raw, int offset)
        {
            if (offset + 4 > raw.Length)
                throw new InvalidDataException("CRT file header is truncated.");
            return (uint)((raw[offset] << 24) | (raw[offset + 1] << 16) | (raw[offset + 2] << 8) | raw[offset + 3]);
        }

        private static string DecodeText(byte[] raw, int offset, int length)
        {
            int available = Math.Max(0, Math.Min(length, raw.Length - offset));
            if (available == 0)
                return string.Empty;

            string text = Encoding.ASCII.GetString(raw, offset, available);
            int nul = text.IndexOf('\0');
            if (nul >= 0)
                text = text[..nul];

            return text.Trim();
        }

        private enum CartridgeMode
        {
            Invisible,
            EightK,
            SixteenK,
            Ultimax
        }
    }
}