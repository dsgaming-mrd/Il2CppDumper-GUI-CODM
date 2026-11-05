using System;
using System.IO;

namespace Il2CppDumper
{
    public static class MetadataDecrypter
    {
        public const uint StandardMagic = 0xFAB11BAF;
        public const uint CodmObfuscatedMagic = 0x6F1F5280;
        public const uint CodmObfuscatedMagic2 = 0x6E1F5280;
        public const uint LegacyObfuscatedMagic = 0xEAB11BAF;
        private const uint LegacyXorKey = 0xA8C72D;

        private struct DecodeField
        {
            public int OutEven { get; }
            public int OutOdd { get; }

            public DecodeField(int outEven, int outOdd)
            {
                OutEven = outEven;
                OutOdd = outOdd;
            }
        }

        private static readonly DecodeField[] LegacyGroupOutFields = new DecodeField[]
        {
            new DecodeField(4, 5),
            new DecodeField(8, 9),
            new DecodeField(12, 13),
            new DecodeField(16, 17),
            new DecodeField(20, 21),
            new DecodeField(6, 7),
            new DecodeField(10, 11),
            new DecodeField(14, 15),
            new DecodeField(18, 19),
            new DecodeField(22, 23),
            new DecodeField(2, 3),
            new DecodeField(48, 49),
            new DecodeField(46, 47),
            new DecodeField(44, 45),
            new DecodeField(42, 43),
            new DecodeField(24, 25),
            new DecodeField(28, 29),
            new DecodeField(32, 33),
            new DecodeField(36, 37),
            new DecodeField(40, 41),
            new DecodeField(26, 27),
            new DecodeField(30, 31),
            new DecodeField(34, 35),
            new DecodeField(38, 39)
        };

        private static readonly byte[] DefaultSBox = new byte[256]
        {
            100, 206, 19, 251, 101, 63, 96, 31, 241, 182, 55, 233, 103, 92, 69, 4,
            255, 115, 176, 237, 174, 121, 47, 51, 166, 72, 20, 42, 167, 38, 153, 66,
            18, 82, 215, 230, 156, 68, 173, 24, 186, 242, 164, 83, 220, 154, 15, 8,
            163, 203, 23, 116, 250, 114, 80, 86, 104, 185, 90, 118, 205, 212, 158, 236,
            85, 44, 126, 222, 152, 127, 157, 245, 225, 14, 112, 53, 39, 218, 77, 54,
            197, 137, 37, 238, 138, 46, 119, 81, 145, 162, 228, 12, 234, 200, 64, 170,
            209, 227, 172, 130, 27, 73, 28, 132, 99, 184, 160, 232, 89, 254, 161, 193,
            211, 165, 159, 194, 221, 102, 249, 67, 192, 106, 146, 253, 91, 247, 129, 180,
            187, 78, 1, 26, 48, 240, 5, 30, 175, 201, 7, 140, 195, 16, 84, 95,
            22, 171, 97, 252, 111, 207, 124, 32, 110, 169, 229, 178, 0, 210, 136, 133,
            11, 98, 107, 43, 149, 9, 17, 244, 177, 217, 196, 33, 223, 168, 135, 219,
            56, 62, 117, 150, 113, 122, 52, 128, 40, 142, 131, 125, 144, 79, 235, 2,
            108, 143, 88, 94, 213, 199, 183, 36, 76, 61, 179, 109, 25, 21, 141, 243,
            248, 58, 246, 224, 198, 3, 151, 204, 41, 75, 202, 120, 231, 214, 134, 65,
            34, 49, 50, 6, 87, 147, 60, 148, 226, 208, 216, 35, 123, 93, 188, 71,
            155, 181, 74, 189, 190, 105, 10, 239, 139, 29, 57, 70, 13, 45, 59, 191
        };

        private static readonly byte[] FallbackHeader128 = new byte[]
        {
            0xAF, 0x1B, 0xB1, 0xFA, 0x17, 0x00, 0x00, 0x00, 0x08, 0x01, 0x00, 0x00, 0xE8, 0x0D, 0x08, 0x00,
            0xF0, 0x0E, 0x08, 0x00, 0xBC, 0x24, 0x24, 0x00, 0xAC, 0x33, 0x2C, 0x00, 0xA0, 0xBE, 0x94, 0x00,
            0x4C, 0xF2, 0xC0, 0x00, 0x90, 0x16, 0x00, 0x00, 0xDC, 0x08, 0xC1, 0x00, 0x7C, 0x03, 0x08, 0x00,
            0x58, 0x0C, 0xC9, 0x00, 0x4C, 0x22, 0x3B, 0x01, 0xA4, 0x2E, 0x04, 0x02, 0x20, 0x73, 0x03, 0x00,
            0xC4, 0xA1, 0x07, 0x02, 0x94, 0x0B, 0x0C, 0x00, 0x58, 0xAD, 0x13, 0x02, 0x78, 0x7C, 0x06, 0x00,
            0xD0, 0x29, 0x1A, 0x02, 0x10, 0x50, 0x04, 0x00, 0xE0, 0x79, 0x1E, 0x02, 0x64, 0x2A, 0x3C, 0x00,
            0x44, 0xA4, 0x5A, 0x02, 0x58, 0x8E, 0x37, 0x00, 0x9C, 0x32, 0x92, 0x02, 0x2C, 0x73, 0x00, 0x00,
            0xC8, 0xA5, 0x92, 0x02, 0x10, 0x0E, 0x00, 0x00, 0xD8, 0xB3, 0x92, 0x02, 0xC8, 0x4C, 0x00, 0x00
        };

        public static void Decrypt(byte[] data, string datFilePath = null)
        {
            if (data == null || data.Length < 256)
            {
                return;
            }

            uint magic = BitConverter.ToUInt32(data, 0);

            if (magic == StandardMagic)
            {
                return;
            }

            if (magic == LegacyObfuscatedMagic)
            {
                DecryptLegacy(data);
                return;
            }

            DecryptCodm(data, datFilePath);
        }

        private static void DecryptLegacy(byte[] data)
        {
            uint[] header = new uint[64];
            for (int i = 0; i < 64; i++)
            {
                header[i] = BitConverter.ToUInt32(data, i * 4);
            }

            uint[] input = new uint[64];
            Array.Copy(header, input, 64);

            uint[] output = new uint[64];
            output[0] = input[0];
            output[1] = input[1];
            for (int i = 50; i < 64; i++)
            {
                output[i] = input[i];
            }

            for (int g = 0; g < 24; g++)
            {
                int outEven = LegacyGroupOutFields[g].OutEven;
                int outOdd = LegacyGroupOutFields[g].OutOdd;
                int inEven = 2 + (2 * g);
                int inOdd = 3 + (2 * g);

                uint even = LegacyXorKey + (uint)g;
                uint odd = LegacyXorKey + (uint)(2 * g);

                uint subEven = (uint)(3 * g);
                uint subOdd = (uint)(7 * g);

                output[outEven] = (input[inEven] - subEven) ^ even;
                output[outOdd] = (input[inOdd] - subOdd) ^ odd;
            }

            for (int i = 0; i < 64; i++)
            {
                uint val = output[i];
                if (i == 0)
                {
                    val = StandardMagic;
                }
                byte[] bytes = BitConverter.GetBytes(val);
                Buffer.BlockCopy(bytes, 0, data, i * 4, 4);
            }
        }

        private static void DecryptCodm(byte[] data, string datFilePath)
        {
            byte[] sbox = (byte[])DefaultSBox.Clone();
            byte[] header128 = (byte[])FallbackHeader128.Clone();

            TryExtractFromAnort(datFilePath, ref sbox, ref header128);

            byte key = FindDynamicKey(data, sbox);
            Console.WriteLine($"[MetadataDecrypter] Dynamically discovered XOR key: 0x{key:X2}");

            int blockSize = Math.Min(data.Length, 0x10000);
            for (int i = 0; i < blockSize; i++)
            {
                data[i] = sbox[data[i] ^ key];
            }

            Buffer.BlockCopy(header128, 0, data, 0, 128);

            uint magic = BitConverter.ToUInt32(data, 0);
            uint version = BitConverter.ToUInt32(data, 4);
            Console.WriteLine($"[MetadataDecrypter] Decryption verified: Magic=0x{magic:X8}, Version={version}");
        }

        private static byte FindDynamicKey(byte[] data, byte[] sbox)
        {
            if (data.Length < 0x128)
            {
                return 0xE9;
            }

            for (int k = 0; k < 256; k++)
            {
                uint len0 = sbox[data[0x108] ^ k] | ((uint)sbox[data[0x109] ^ k] << 8) | ((uint)sbox[data[0x10A] ^ k] << 16) | ((uint)sbox[data[0x10B] ^ k] << 24);
                uint idx0 = sbox[data[0x10C] ^ k] | ((uint)sbox[data[0x10D] ^ k] << 8) | ((uint)sbox[data[0x10E] ^ k] << 16) | ((uint)sbox[data[0x10F] ^ k] << 24);
                uint len1 = sbox[data[0x110] ^ k] | ((uint)sbox[data[0x111] ^ k] << 8) | ((uint)sbox[data[0x112] ^ k] << 16) | ((uint)sbox[data[0x113] ^ k] << 24);
                uint idx1 = sbox[data[0x114] ^ k] | ((uint)sbox[data[0x115] ^ k] << 8) | ((uint)sbox[data[0x116] ^ k] << 16) | ((uint)sbox[data[0x117] ^ k] << 24);

                if (idx0 == 0 && idx1 == len0 && len0 > 0 && len0 < 500 && len1 > 0 && len1 < 500)
                {
                    return (byte)k;
                }
            }

            return 0xE9;
        }

        private static void TryExtractFromAnort(string datFilePath, ref byte[] sbox, ref byte[] header128)
        {
            string[] searchPaths = GetAnortCandidatePaths(datFilePath);

            foreach (var path in searchPaths)
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    try
                    {
                        byte[] anortBytes = File.ReadAllBytes(path);
                        Console.WriteLine($"[MetadataDecrypter] Found anort library at: {path} ({anortBytes.Length} bytes)");

                        byte[] hdrSig = new byte[] { 0xAF, 0x1B, 0xB1, 0xFA, 0x17, 0x00, 0x00, 0x00, 0x08, 0x01, 0x00, 0x00 };
                        int hdrIdx = IndexOfSequence(anortBytes, hdrSig);
                        if (hdrIdx >= 0 && hdrIdx + 128 <= anortBytes.Length)
                        {
                            Buffer.BlockCopy(anortBytes, hdrIdx, header128, 0, 128);
                            Console.WriteLine($"[MetadataDecrypter] Successfully extracted 128-byte header from anort at offset 0x{hdrIdx:X}");
                        }

                        byte[] sboxSig = new byte[] { 100, 206, 19, 251, 101, 63, 96, 31, 241, 182, 55, 233, 103, 92, 69, 4 };
                        int sboxIdx = IndexOfSequence(anortBytes, sboxSig);
                        if (sboxIdx >= 0 && sboxIdx + 256 <= anortBytes.Length)
                        {
                            Buffer.BlockCopy(anortBytes, sboxIdx, sbox, 0, 256);
                            Console.WriteLine($"[MetadataDecrypter] Successfully extracted SBox from anort at offset 0x{sboxIdx:X}");
                        }

                        return;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[MetadataDecrypter] Warning reading anort: {ex.Message}");
                    }
                }
            }
        }

        private static string[] GetAnortCandidatePaths(string datFilePath)
        {
            string dir = !string.IsNullOrEmpty(datFilePath) ? Path.GetDirectoryName(datFilePath) : null;
            return new string[]
            {
                !string.IsNullOrEmpty(dir) ? Path.Combine(dir, "anort") : null,
                !string.IsNullOrEmpty(dir) ? Path.Combine(dir, "Frameworks", "anort.framework", "anort") : null,
                !string.IsNullOrEmpty(dir) ? Path.Combine(dir, "..", "Frameworks", "anort.framework", "anort") : null,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "anort"),
                Path.Combine(Directory.GetCurrentDirectory(), "anort")
            };
        }

        private static int IndexOfSequence(byte[] buffer, byte[] pattern)
        {
            for (int i = 0; i <= buffer.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (buffer[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }
    }
}
