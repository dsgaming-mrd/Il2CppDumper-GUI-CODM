using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using static Il2CppDumper.ArmUtils;

namespace Il2CppDumper
{
    public sealed class Macho64 : Il2Cpp
    {
        private static readonly byte[] FeatureBytes1 = { 0x2, 0x0, 0x80, 0xD2 };//MOV X2, #0
        private static readonly byte[] FeatureBytes2 = { 0x3, 0x0, 0x80, 0x52 };//MOV W3, #0
        private readonly List<MachoSection64Bit> sections = new();
        private readonly ulong vmaddr;

        public Macho64(Stream stream) : base(stream)
        {
            Position += 16; //skip magic, cputype, cpusubtype, filetype
            var ncmds = ReadUInt32();
            Position += 12; //skip sizeofcmds, flags, reserved
            for (var i = 0; i < ncmds; i++)
            {
                var pos = Position;
                var cmd = ReadUInt32();
                var cmdsize = ReadUInt32();
                switch (cmd)
                {
                    case 0x19: //LC_SEGMENT_64
                        var segname = Encoding.UTF8.GetString(ReadBytes(16)).TrimEnd('\0');
                        if (segname == "__TEXT") //__PAGEZERO
                        {
                            vmaddr = ReadUInt64();
                        }
                        else
                        {
                            Position += 8;
                        }
                        Position += 32; //skip vmsize, fileoff, filesize, maxprot, initprot
                        var nsects = ReadUInt32();
                        Position += 4; //skip flags
                        for (var j = 0; j < nsects; j++)
                        {
                            var section = new MachoSection64Bit();
                            sections.Add(section);
                            section.sectname = Encoding.UTF8.GetString(ReadBytes(16)).TrimEnd('\0');
                            Position += 16; //skip segname
                            section.addr = ReadUInt64();
                            section.size = ReadUInt64();
                            section.offset = ReadUInt32();
                            Position += 12; //skip align, reloff, nreloc
                            section.flags = ReadUInt32();
                            Position += 12; //skip reserved1, reserved2, reserved3
                        }
                        break;
                    case 0x2C: //LC_ENCRYPTION_INFO_64
                        Position += 8;
                        var cryptID = ReadUInt32();
                        if (cryptID != 0)
                        {
                            MainForm.Log("ERROR: This Mach-O executable is encrypted and cannot be processed.", Brushes.Orange);
                        }
                        break;
                }
                Position = pos + cmdsize;//skip
            }
        }

        public override ulong MapVATR(ulong addr)
        {
            addr = NormalizeAddress(addr); // new extra

            var section = sections.FirstOrDefault(x => addr >= x.addr && addr < x.addr + x.size);

            if (section == null) // new extra
                return 0; // new extra

            if (section.sectname == "__bss")
                return 0; // new extra

            return addr - section.addr + section.offset;
        }

        public override ulong MapRTVA(ulong addr)
        {
            var section = sections.FirstOrDefault(x => addr >= x.offset && addr <= x.offset + x.size);
            if (section == null)
            {
                return 0;
            }
            if (section.sectname == "__bss")
            {
                throw new Exception();
            }
            return addr - section.offset + section.addr;
        }

        public override bool Search()
        {
            var codeRegistration = 0ul;
            var metadataRegistration = 0ul;

            if (!TryGetInitFunctionAddrs(out var addrs))
            {
                MainForm.Log("ERROR: No __mod_init_func or __init_offsets section found.", Brushes.Orange);
                return false;
            }

            if (Version < 23)
            {
                foreach (var i in addrs)
                {
                    if (i > 0)
                    {
                        var flag = false;
                        var subaddr = 0ul;
                        //Position = MapVATR(i);

                        if (!TryMapVATR(i, out var initPos))
                        {
                            continue;
                        }
                        Position = initPos;

                        var buff = ReadBytes(4);
                        if (FeatureBytes1.SequenceEqual(buff))
                        {
                            buff = ReadBytes(4);
                            if (FeatureBytes2.SequenceEqual(buff))
                            {
                                Position += 8;
                                var inst = ReadBytes(4);
                                if (IsAdr(inst))
                                {
                                    subaddr = DecodeAdr(i + 16, inst);
                                    flag = true;
                                }
                            }
                        }
                        else
                        {
                            Position += 0xc;
                            buff = ReadBytes(4);
                            if (FeatureBytes2.SequenceEqual(buff))
                            {
                                buff = ReadBytes(4);
                                if (FeatureBytes1.SequenceEqual(buff))
                                {
                                    Position -= 0x10;
                                    var inst = ReadBytes(4);
                                    if (IsAdr(inst))
                                    {
                                        subaddr = DecodeAdr(i + 8, inst);
                                        flag = true;
                                    }
                                }
                            }
                        }
                        if (flag)
                        {
                            var rsubaddr = MapVATR(subaddr);
                            Position = rsubaddr;
                            codeRegistration = DecodeAdrp(subaddr, ReadBytes(4));
                            codeRegistration += DecodeAdd(ReadBytes(4));
                            Position = rsubaddr + 8;
                            metadataRegistration = DecodeAdrp(subaddr + 8, ReadBytes(4));
                            metadataRegistration += DecodeAdd(ReadBytes(4));
                        }
                    }
                }
            }
            if (Version == 23)
            {
                /* ADRP X0, unk
                 * ADD X0, X0, unk
                 * ADR X1, sub
                 * NOP
                 * MOV X2, #0
                 * MOV W3, #0
                 * B sub
                 */

                foreach (var i in addrs)
                {
                    if (i > 0)
                    {
                        //Position = MapVATR(i) + 16;

                        if (!TryMapVATR(i, out var initPos))
                        {
                            continue;
                        }
                        Position = initPos + 16;

                        var buff = ReadBytes(4);
                        if (FeatureBytes1.SequenceEqual(buff))
                        {
                            buff = ReadBytes(4);
                            if (FeatureBytes2.SequenceEqual(buff))
                            {
                                //Position -= 16;
                                //var subaddr = DecodeAdr(i + 8, ReadBytes(4));

                                Position = initPos + 8; // new
                                var adrpX1 = ReadBytes(4); // new
                                var addX1 = ReadBytes(4); // new

                                var subaddr = DecodeAdrp(i + 8, adrpX1); // new
                                subaddr += DecodeAdd(addX1); // new

                                var rsubaddr = MapVATR(subaddr);
                                Position = rsubaddr;
                                codeRegistration = DecodeAdrp(subaddr, ReadBytes(4));
                                codeRegistration += DecodeAdd(ReadBytes(4));
                                Position = rsubaddr + 8;
                                metadataRegistration = DecodeAdrp(subaddr + 8, ReadBytes(4));
                                metadataRegistration += DecodeAdd(ReadBytes(4));
                            }
                        }
                    }
                }
            }

            if (Version >= 24)
            {
                /* ADRP X0, unk
                 * ADD X0, X0, unk
                 * ADR X1, sub
                 * NOP
                 * MOV W3, #0
                 * MOV X2, #0
                 * B sub
                 */
                foreach (var i in addrs)
                {
                    if (i > 0)
                    {
                        //Position = MapVATR(i) + 16;

                        if (!TryMapVATR(i, out var initPos))
                        {
                            continue;
                        }
                        Position = initPos + 16;

                        var buff = ReadBytes(4);
                        if (FeatureBytes2.SequenceEqual(buff))
                        {
                            buff = ReadBytes(4);
                            if (FeatureBytes1.SequenceEqual(buff))
                            {
                                Position -= 16;
                                var subaddr = DecodeAdr(i + 8, ReadBytes(4));
                                var rsubaddr = MapVATR(subaddr);
                                Position = rsubaddr;
                                codeRegistration = DecodeAdrp(subaddr, ReadBytes(4));
                                codeRegistration += DecodeAdd(ReadBytes(4));
                                Position = rsubaddr + 8;
                                metadataRegistration = DecodeAdrp(subaddr + 8, ReadBytes(4));
                                metadataRegistration += DecodeAdd(ReadBytes(4));
                            }
                        }
                    }
                }
            }
            if (codeRegistration != 0 && metadataRegistration != 0)
            {
                MainForm.Log("CodeRegistration : {0:x}", codeRegistration);
                MainForm.Log("MetadataRegistration : {0:x}", metadataRegistration);
                Init(codeRegistration, metadataRegistration);
                return true;
            }
            return false;
        }

        public override bool PlusSearch(int methodCount, int typeDefinitionsCount, int imageCount)
        {
            var sectionHelper = GetSectionHelper(methodCount, typeDefinitionsCount, imageCount);
            var codeRegistration = sectionHelper.FindCodeRegistration();
            var metadataRegistration = sectionHelper.FindMetadataRegistration();
            return AutoPlusInit(codeRegistration, metadataRegistration);
        }

        public override bool SymbolSearch()
        {
            return false;
        }

        public override ulong GetRVA(ulong pointer)
        {
            return pointer - vmaddr;
        }
        
        public override SectionHelper GetSectionHelper(int methodCount, int typeDefinitionsCount, int imageCount)
        {
            var data = sections.Where(x => x.sectname == "__const" || x.sectname == "__cstring" || x.sectname == "__data").ToArray();
            var code = sections.Where(x => x.flags == 0x80000400).ToArray();
            var bss = sections.Where(x => x.flags == 1u).ToArray();
            var sectionHelper = new SectionHelper(this, methodCount, typeDefinitionsCount, metadataUsagesCount, imageCount);
            sectionHelper.SetSection(SearchSectionType.Exec, code);
            sectionHelper.SetSection(SearchSectionType.Data, data);
            sectionHelper.SetSection(SearchSectionType.Bss, bss);
            return sectionHelper;
        }

        private bool TryGetInitFunctionAddrs(out ulong[] addrs)
        {
            var modInit = sections.FirstOrDefault(x => x.sectname == "__mod_init_func");
            if (modInit != null)
            {
                addrs = ReadClassArray<ulong>(modInit.offset, modInit.size / 8);
                return true;
            }

            var initOffsets = sections.FirstOrDefault(x => x.sectname == "__init_offsets");
            if (initOffsets != null)
            {
                //MainForm.Log("Using __init_offsets for init functions.");
                var offsets = ReadClassArray<uint>(initOffsets.offset, initOffsets.size / 4);
                addrs = offsets.Where(x => x != 0).Select(x => vmaddr + x).ToArray();
                return true;
            }

            addrs = Array.Empty<ulong>();
            return false;
        }

        private ulong NormalizeAddress(ulong addr)
        {
            if (addr == 0)
                throw new Exception();
            if (addr >= vmaddr && addr < vmaddr + 0x100000000)
            {
                return addr;
            }
            return vmaddr + (addr & 0x0FFFFFFFFF);
        }

        public override ulong NormalizePointer(ulong pointer)
        {
            if (pointer == 0)
                return 0;
            if (pointer >= vmaddr && pointer < vmaddr + 0x100000000)
            {
                return pointer;
            }
            return vmaddr + (pointer & 0x0FFFFFFFFF);
        }

        public bool TryMapVATR(ulong addr, out ulong result) // new extra
        {
            result = 0;

            addr = NormalizeAddress(addr);

            var section = sections.FirstOrDefault(x => addr >= x.addr && addr < x.addr + x.size);

            if (section == null)
                return false;

            if (section.sectname == "__bss")
                return false;

            result = addr - section.addr + section.offset;
            return true;
        }

        public override bool CheckDump() => false;
		
		public override ulong ReadUIntPtr()
        {
            var pointer = ReadUInt64();
            if (pointer > vmaddr + 0xFFFFFFFF)
            {
                var addr = Position;
                var section = sections.First(x => addr >= x.offset && addr <= x.offset + x.size);
                if (section.sectname == "__const" || section.sectname == "__data")
                {
                    var rva = pointer - vmaddr;
                    rva &= 0xFFFFFFFF;
                    pointer = rva + vmaddr;
                }
            }
            return pointer;
        }
    }
}
