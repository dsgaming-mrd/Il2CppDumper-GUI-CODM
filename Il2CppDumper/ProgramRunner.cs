using System;
using System.IO;
using System.Linq;

namespace Il2CppDumper
{
    public static class ProgramRunner
    {
        public static bool RunDump(string binFile, string datFile, string outputDir)
        {
            try
            {
                Console.WriteLine($"Starting dump...");
                Console.WriteLine($"Binary: {binFile}");
                Console.WriteLine($"Metadata: {datFile}");
                if (!outputDir.EndsWith(Path.DirectorySeparatorChar.ToString()) && !outputDir.EndsWith("/"))
                    outputDir += Path.DirectorySeparatorChar;

                Console.WriteLine($"Output: {outputDir}");

                var config = new Config();
                if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json")))
                {
                    try
                    {
                        var json = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"));
                        config = Newtonsoft.Json.JsonConvert.DeserializeObject<Config>(json) ?? config;
                        Console.WriteLine("Loaded config.json");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning reading config.json: {ex.Message}");
                    }
                }

                Console.WriteLine("Reading metadata...");
                byte[] metadataBytes = File.ReadAllBytes(datFile);
                Console.WriteLine($"Metadata size: {metadataBytes.Length} bytes");

                Console.WriteLine("Decrypting metadata...");
                MetadataDecrypter.Decrypt(metadataBytes, datFile);

                Console.WriteLine("Parsing metadata...");
                var metadata = new Metadata(new MemoryStream(metadataBytes));
                Console.WriteLine($"Metadata initialized! Version: {metadata.Version}, TypeDefs: {metadata.typeDefs.Length}, MethodDefs: {metadata.methodDefs.Length}, Images: {metadata.imageDefs.Length}");

                Console.WriteLine("Reading binary...");
                byte[] il2cppBytes = File.ReadAllBytes(binFile);
                uint il2cppMagic = BitConverter.ToUInt32(il2cppBytes, 0);
                var il2cppMemory = new MemoryStream(il2cppBytes);

                Il2Cpp il2Cpp;
                switch (il2cppMagic)
                {
                    case 0x6D736100:
                        var web = new WebAssembly(il2cppMemory);
                        il2Cpp = web.CreateMemory();
                        break;
                    case 0x304F534E:
                        var nso = new NSO(il2cppMemory);
                        il2Cpp = nso.UnCompress();
                        break;
                    case 0x905A4D:
                        il2Cpp = new PE(il2cppMemory);
                        break;
                    case 0x464c457f:
                        if (il2cppBytes[4] == 2)
                            il2Cpp = new Elf64(il2cppMemory);
                        else
                            il2Cpp = new Elf(il2cppMemory);
                        break;
                    case 0xCAFEBABE:
                    case 0xBEBAFECA:
                        var machofat = new MachoFat(new MemoryStream(il2cppBytes));
                        int idx = 0;
                        for (int i = 0; i < machofat.fats.Length; i++)
                        {
                            if (machofat.fats[i].magic == 0xFEEDFACF)
                            {
                                idx = i;
                                break;
                            }
                        }
                        var mMagic = machofat.fats[idx].magic;
                        il2cppBytes = machofat.GetMacho(idx);
                        il2cppMemory = new MemoryStream(il2cppBytes);
                        if (mMagic == 0xFEEDFACF)
                            il2Cpp = new Macho64(il2cppMemory);
                        else
                            il2Cpp = new Macho(il2cppMemory);
                        break;
                    case 0xFEEDFACF:
                        il2Cpp = new Macho64(il2cppMemory);
                        break;
                    case 0xFEEDFACE:
                        il2Cpp = new Macho(il2cppMemory);
                        break;
                    default:
                        throw new NotSupportedException($"ERROR: il2cpp file magic 0x{il2cppMagic:X8} not supported.");
                }

                double version = config.ForceIl2CppVersion ? config.ForceVersion : metadata.Version;
                il2Cpp.SetProperties(version, metadata.metadataUsagesCount);
                Console.WriteLine($"Il2Cpp Version: {il2Cpp.Version}");

                bool flag = false;
                try
                {
                    flag = il2Cpp.PlusSearch(metadata.methodDefs.Count(x => x.methodIndex >= 0), metadata.typeDefs.Length, metadata.imageDefs.Length);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"PlusSearch error: {ex.Message}");
                }

                if (!flag)
                {
                    try
                    {
                        flag = il2Cpp.Search();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Search error: {ex.Message}");
                    }
                }

                if (!flag)
                {
                    try
                    {
                        flag = il2Cpp.SymbolSearch();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"SymbolSearch error: {ex.Message}");
                    }
                }

                if (!flag)
                {
                    Console.WriteLine("Auto-search failed! Could not find CodeRegistration and MetadataRegistration.");
                    return false;
                }
                else
                {
                    Console.WriteLine("Search succeeded automatically!");
                }

                Directory.CreateDirectory(outputDir);

                Console.WriteLine("Creating Il2CppExecutor...");
                var executor = new Il2CppExecutor(metadata, il2Cpp);

                Console.WriteLine("Decompiling dump.cs and script.json...");
                var decompiler = new Il2CppDecompiler(executor);
                decompiler.Decompile(config, outputDir);
                Console.WriteLine("Decompile complete!");

                if (config.GenerateStruct)
                {
                    Console.WriteLine("Generating struct...");
                    try
                    {
                        var scriptGenerator = new StructGenerator(executor);
                        scriptGenerator.WriteScript(outputDir);
                        Console.WriteLine("Struct generation complete!");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Struct generation error: {ex}");
                    }
                }

                if (config.GenerateDummyDll)
                {
                    Console.WriteLine("Generating dummy DLLs...");
                    try
                    {
                        DummyAssemblyExporter.Export(executor, outputDir, config.DummyDllAddToken);
                        Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
                        Console.WriteLine("Dummy DLL export complete!");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Dummy DLL export error: {ex}");
                    }
                }

                Console.WriteLine("DUMP COMPLETED SUCCESSFULLY!");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FATAL ERROR: {ex}");
                return false;
            }
        }
    }
}
