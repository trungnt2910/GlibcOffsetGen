using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GlibcOffsetGen
{
    class Program
    {
        //const string fileName = "glibc-2.31.tar.xz";
        const string cCode = "#include <ldsodefs.h>";
        const string gnuServer = "https://ftp.gnu.org/gnu/glibc/";

        // .NET core does not exsist before this version.
        private static readonly Version minVersion = new Version(2, 21);

        static async Task Main(string[] args)
        {
            var dir = Directory.GetCurrentDirectory();
            
            try
            {
                var index = (await DownloadFileText(gnuServer)).Split(Environment.NewLine);

                foreach (var line in index)
                {
                    var match = Regex.Match(line, "href=\"(glibc-([\\d.]*).tar.xz)\""); 
                    if (match.Success)
                    {
                        var name = match.Groups[1].Value;
                        var version = Version.Parse(match.Groups[2].Value);

                        if (!File.Exists(name) && version >= minVersion)
                        {
                            try
                            {
                                Console.WriteLine($"Downloading {name}");
                                using var fileStream = File.Create(name);
                                await DownloadFileStream($"{gnuServer}{name}", fileStream);
                            }
                            catch
                            {
                                File.Delete(name);
                                throw;
                            }
                        }
                    }
                }
            }
            catch
            {
                Console.Error.WriteLine("Cannot get glibc releases. Using offline archives instead...");
            }

            // To avoid the file list being updated.
            var fileList = Directory.GetFiles(dir, "*.xz");

            foreach (var file in fileList)
            {
                Console.WriteLine(file);
                
                if (args.Length == 0)
                {
                    goto ok;
                }

                foreach (var arg in args)
                {
                    if (Path.GetFullPath(arg) == file)
                    {
                        goto ok;
                    }
                }

                continue;

                ok:
                if (Regex.Match(file, "glibc-(\\S*).tar.xz$").Success)
                {
                    Work(file, "x86_64-pc-linux-gnu", "-m64", "64");
                    Work(file, "i686-pc-linux-gnu", "-m32", "32");
                }
            }
        }

        static void Work(string fileName, string hostType, string gccMachineFlag, string bits)
        {
            var glibcVersion = Regex.Match(fileName, "glibc-(\\S*).tar.xz").Groups[1].Value;


            var decompressPath = string.Empty;
            var buildPath = string.Empty;
            var currentPath = Directory.GetCurrentDirectory();
            try
            {
                Console.WriteLine("Decompressing...");
                decompressPath = Decompress(fileName);
                Console.WriteLine("Done.");

                var decompressDir = Directory.CreateDirectory(decompressPath);
                decompressPath = decompressDir.FullName;

                buildPath = Path.Combine(Directory.GetCurrentDirectory(), "build");
                Directory.CreateDirectory(buildPath);
                Directory.SetCurrentDirectory(buildPath);

                Configure(decompressPath, hostType, gccMachineFlag);

                var gccArg = CaptureMake();

                Console.WriteLine("Detected gcc build command: ");
                Console.WriteLine(gccArg);

                var processedArg = ProcessGccArg(gccArg);

                var elfPath = Path.Join(decompressPath, "elf");
                Directory.SetCurrentDirectory(elfPath);

                var cFilePath = Path.Join(decompressPath, "elf", "defs.c");
                var cppFilePath = Path.ChangeExtension(cFilePath, ".cpp");

                var writer = File.CreateText(cFilePath);
                writer.WriteLine(cCode);
                writer.Dispose();

                processedArg = $"-DSHARED -E defs.c -o defs.cpp {processedArg}"; 

                processedArg = processedArg.Replace(Environment.NewLine, " ");
                Console.WriteLine(processedArg);

                Gcc(elfPath, processedArg);

                File.Delete(cFilePath);

                File.Move(cppFilePath, cFilePath);

                var codeBase = File.ReadAllText(cFilePath);

                var rtldSymbols = ParseStruct(codeBase, "rtld_global", "_dl");
                var linkmapSymbols = ParseStruct(codeBase, "link_map", "l_");

                var codeLines = codeBase.Split(Environment.NewLine);

                writer = File.CreateText(cFilePath);

                foreach (var codeLine in codeLines)
                {
                    var line = codeLine.Trim();
                    if (line.StartsWith("#"))
                    {
                        continue;
                    }
                    // // Get rid of some asm hacks
                    // if (line.StartsWith("asm ("))
                    // {
                    //     continue;
                    // }
                    writer.WriteLine(line);
                }

                writer.WriteLine(@$"

extern int32_t printf( const char *restrict format, ... );

#define offsetof(TYPE, MEMBER) __builtin_offsetof (TYPE, MEMBER)
#define member_size(type, member) sizeof(((type *)0)->member)

#define BEGIN_STRUCT(NAME) printf(""    [StructLayout(LayoutKind.Explicit, Size = %i)]\n    unsafe partial struct %s\n    {{\n"", (int32_t)sizeof(struct NAME), #NAME""_{glibcVersion.Replace(".", "_")}_{bits}"");
// Some extra handling, as C# does not allow 0-byte arrays.
#define DECLARE(structName, name)    \
if (member_size(struct structName, name) != 0) \
printf(""        [FieldOffset(%i)]\n        fixed byte %s[%i];\n"", (int32_t)offsetof(struct structName, name), #name, (int32_t)member_size(struct structName, name)); \
else \
printf(""        byte* %s\n        {{\n            get\n            {{\n                fixed (void* _thisPtr = &this)\n                {{\n                    return (byte*)_thisPtr + %i;\n                }}\n            }}\n        }}\n"", #name, (int32_t)offsetof(struct structName, name));
#define END_STRUCT(NAME)     printf(""    }}\n\n"");

int main()
{{
    printf(""// Native structure offsets for glibc, on x86 and x86_64.\n// Generated using GlibcOffsetGen. Do not modify.\n\n"");
    printf(""using System;\nusing System.Runtime.InteropServices;\n"");
    printf(""namespace GlibcInterop\n{{\n"");

    BEGIN_STRUCT(rtld_global);
    {string.Join("\n", rtldSymbols.Select(name => $"DECLARE(rtld_global, {name})"))}
    END_STRUCT(rtld_global);
    
    BEGIN_STRUCT(link_map);
    {string.Join("\n", linkmapSymbols.Select(name => $"DECLARE(link_map, {name})"))}
    END_STRUCT(link_map);

    printf(""}}\n"");

    return 0;
}}
");
                writer.Dispose();

                Gcc(elfPath, $"defs.c -o defs {gccMachineFlag} -Wl,--unresolved-symbols=ignore-all");

                var outputPath = Path.Combine(currentPath, "output", glibcVersion.Replace(".", "_"), bits);
                Directory.CreateDirectory(outputPath);

                var outputFile = Path.Combine(outputPath, "Natives.g.cs");

                Run(Path.GetFileNameWithoutExtension(cFilePath), outputFile);

                var otherFile = Path.Combine(outputPath, "Natives.cs");
                using var templateStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GlibcOffsetGen.Templates.cs");
                using var reader = new StreamReader(templateStream);

                var codeString = reader.ReadToEnd();
                File.WriteAllText(otherFile, codeString.Replace("$$0$$", $"_{glibcVersion.Replace(".", "_")}_{bits}"));

                Console.WriteLine("Done");
            }
            finally
            {
                Directory.Delete(decompressPath, true);
                Directory.Delete(buildPath, true);

                Directory.SetCurrentDirectory(currentPath);
            }
        }

        static string Decompress(string name)
        {
            var tarFile = Path.GetFileNameWithoutExtension(name);

            var nameWithoutExt = Path.GetFileNameWithoutExtension(tarFile);

            var procInfo = new ProcessStartInfo()
            {
                Arguments = $"-d -k -f -v {name}",
                FileName = "xz",
                UseShellExecute = true,
                CreateNoWindow = true
            };

            var proc = Process.Start(procInfo);
            
            proc.WaitForExit();

            procInfo = new ProcessStartInfo()
            {
                Arguments = $"-xf {tarFile}",
                FileName = "tar",
                UseShellExecute = true,
                CreateNoWindow = true
            };

            proc = Process.Start(procInfo);
            proc.WaitForExit();

            File.Delete(tarFile);

            return nameWithoutExt;
        }

        static void Configure(string configureDir, string hostType, string gccMachineFlag)
        {
            var procInfo = new ProcessStartInfo()
            {
                Arguments = $"--prefix=/usr/ --build={hostType} --host={hostType} CC=\"gcc {gccMachineFlag}\" CXX=\"g++ {gccMachineFlag}\"",
                FileName = Path.Combine(configureDir, "configure"),
                UseShellExecute  = true,
                CreateNoWindow = true
            };

            var proc = Process.Start(procInfo);

            proc.WaitForExit();
        }

        static string CaptureMake()
        {
            var procInfo = new ProcessStartInfo()
            {
                FileName = "make",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            var proc = Process.Start(procInfo);

            string current = null;
            string potential = null;            

            while (true)
            {
                current = null;
                current = proc.StandardOutput.ReadLine();

                if (current == null)
                {
                    break;
                }

                Console.WriteLine(current);

                if (current.Contains("gcc"))
                {
                    potential = current;
                }

                // Must be building a c file.
                if (current.StartsWith("gcc") && current.Contains(".c") && current.Contains(".o"))
                {
                    proc.Kill(true);
                    proc.WaitForExit();
                    return current;
                }
            }

            proc.WaitForExit();

            return potential;
        }

        static string ProcessGccArg(string gccArg)
        {
            var fragments = gccArg.Split();

            var result = new StringBuilder();

            var lastWasInclude = false;
            var hadOptimization = false;

            foreach (var fragment in fragments)
            {
                /* if (fragment == "gcc")
                {
                    result.AppendLine(fragment);
            
                else */ if (fragment.StartsWith("-I"))
                {
                    var newFragment =  fragment;
                    if (fragment[2] != '/' && fragment.Substring(2, Math.Min(4, fragment.Length) - 2) != "..")
                    {
                        newFragment = $"{fragment.Substring(0, 2)}../{fragment.Substring(2)}";
                    }
                    result.AppendLine(newFragment);
                }
                else if (fragment.StartsWith("-D"))
                {
                    result.AppendLine(fragment);
                }
                else if (fragment.StartsWith("-O"))
                {
                    hadOptimization = true;
                    result.AppendLine(fragment);
                }
                else if (fragment.StartsWith("-std"))
                {
                    result.AppendLine(fragment);
                }
                else if (fragment.StartsWith("-m"))
                {
                    result.AppendLine(fragment);
                }
                else if (fragment == "-include")
                {
                    result.AppendLine(fragment);
                    lastWasInclude = true;
                }
                else if (lastWasInclude)
                {
                    var newFragment =  fragment;
                    // Not absolute path. So mostly, it should be in the parent directory,
                    // as we're working in /elf
                    if (fragment[0] != '/' && fragment[0] != '.')
                    {
                        newFragment = $"../{fragment}";
                    }
                    lastWasInclude = false;
                    result.AppendLine(newFragment);
                }
            }

            // glibc annot be compiled without optimization
            if (!hadOptimization)
            {
                result.AppendLine("-O2");
            }

            return result.ToString();
        }

        static void Gcc(string dir, string lmao)
        {
            var procInfo = new ProcessStartInfo()
            {
                Arguments = lmao,
                FileName = "gcc",
                UseShellExecute  = true,
                CreateNoWindow = true,
                WorkingDirectory = dir,
            };

            var proc = Process.Start(procInfo);

            proc.WaitForExit();
        }

        static void Run(string prog, string outputPath = null)
        {
            var procInfo = new ProcessStartInfo()
            {
                FileName = prog,
                UseShellExecute  = outputPath == null,
                CreateNoWindow = true,
                RedirectStandardOutput = outputPath != null
            };

            var proc = Process.Start(procInfo);

            if (outputPath != null)
            {
                var writer = File.CreateText(outputPath);
                while (true)
                {
                    string current = null;
                    current = proc.StandardOutput.ReadLine();

                    Console.WriteLine(current);

                    if (current == null)
                    {
                        break;
                    }
                    
                    writer.WriteLine(current);
                }

                writer.Dispose();
            }


            proc.WaitForExit();

        }

        // Love the glibc guys here. They use member prefixes, which is really easy for us to parse.
        static List<string> ParseStruct(string code, string structName, string memberPrefix)
        {
            List<string> result = new List<string>();

            code = code.Replace(Environment.NewLine, " ");

            while (true)
            {
                var oldSize = code.Length;
                code = code.Replace("  ", " ");
                if (code.Length == oldSize)
                {
                    break;
                }
            }

            int index = 0;
            
            while (true)
            {
                index = code.IndexOf($"struct {structName}", index);
                if (index == -1)
                {
                    break;
                }
                index += $"struct {structName}".Length;
                while (index < code.Length && code[index] == ' ') ++index;

                if (index == code.Length)
                {
                    return result;
                }

                // Possibly a forward declaration. Lots of them for link_map
                if (code[index] != '{')
                {
                    continue;
                }

                var count = 1;

                var endIndex = index;

                // Hope that they don't have funky string constants. Or we're doomed.
                while (count > 0)
                {
                    ++endIndex;
                    if (code[endIndex] == '}')
                    {
                        --count;
                    }
                    else if (code[endIndex] == '{')
                    {
                        ++count;
                    }
                }

                var structRange = code.Substring(index, endIndex - index + 1);

                var structFragments = structRange.Split();

                foreach (var fragment in structFragments)
                {
                    if (fragment.StartsWith(memberPrefix))
                    {
                        // Fucking bit fields: 
                        if (fragment.Contains(":"))
                        {
                            continue;
                        }
                        result.Add(TrimSpecialChar(fragment));
                    }
                }

                break;
            }

            // Only one definition
            return result;
        }

        static string TrimSpecialChar(string s)
        {
            var beginIndex = 0;

            while (s[beginIndex] != '_' && !Char.IsLetterOrDigit(s[beginIndex]))
            {
                ++beginIndex;
            }

            var endIndex = beginIndex;

            while (endIndex < s.Length && (s[endIndex] == '_' || Char.IsLetterOrDigit(s[endIndex])))
            {
                ++endIndex;
            }

            return s.Substring(beginIndex, endIndex - beginIndex);
        }

        static async Task<string> DownloadFileText(string url)
        {
            using var client = new HttpClient();

            var response = await client.GetAsync(url);
            
            return await response.Content.ReadAsStringAsync();
        }

        static async Task DownloadFileStream(string url, Stream stream)
        {
            using var client = new HttpClient();

            var response = await client.GetAsync(url);
            
            await response.Content.CopyToAsync(stream);
        }
    }
}
