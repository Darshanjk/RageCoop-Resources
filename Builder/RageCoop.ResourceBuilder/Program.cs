using DiscUtils.Iso9660;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft.Json;
using RageCoop.Core;
using RageCoop.Server;
using RageCoop.Client;
using System.Diagnostics;
using RageCoop.Client.Scripting;
using System.Reflection;
using System.Linq;

internal class ResourceManifest
{
    public string Name = "RageCoop.Resources.Default";
    public string Description = "Resource description";
    public string[] ClientResources = new string[0];
    public string[] ServerResources = new string[0];
    public Version Version = new(0, 0, 0, 0);
}
public class Program
{
    static HashSet<string> ApiAssemblies = new();
    public static void Main(string[] args)
    {
        CoreUtils.GetDependencies(typeof(Server).Assembly, ref ApiAssemblies);
        CoreUtils.GetDependencies(typeof(ClientScript).Assembly, ref ApiAssemblies);

        File.WriteAllText("ResourceManifest.json", JsonConvert.SerializeObject(new ResourceManifest(), Formatting.Indented));
        var targets = args;
        if (targets.Length == 0)
        {
            targets = Directory.GetDirectories("Resources", "*", SearchOption.AllDirectories);
        }
        if (Directory.Exists("bin"))
        {
            Directory.Delete("bin", true);
        }
        Directory.CreateDirectory("bin");
        foreach (var target in targets)
        {
            string dir = target;
            if (!target.Contains('\\') && !target.Contains('/'))
            {
                dir = $"Resources\\{target.Split('.')[0]}\\{target}";
            }
            try
            {
                var manifestPath = Path.Combine(dir, "ResourceManifest.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }
                Console.WriteLine("building resource from directory: " + dir);
                var manifest = JsonConvert.DeserializeObject<ResourceManifest>(File.ReadAllText(manifestPath));
                if (Path.GetFileName(dir) != manifest.Name || manifest.Name.Contains(' '))
                {
                    Console.Error.WriteLine($"Illegal resource name \"{manifest.Name}\" in manifest from directory \"{dir}\"");
                    continue;
                }
                try
                {
                    BuildResource(manifest, Path.GetFullPath(dir));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to build resource:{dir}\n{ex}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

        }
    }

    private static void BuildResource(ResourceManifest manifest, string workingDir)
    {
        List<string> builtFolders = new List<string>();
        var binPath = Path.Combine(workingDir, "bin");
        if (Directory.Exists(binPath)) { Directory.Delete(binPath, true); }
        foreach (var c in manifest.ClientResources)
        {
            Build(c, true);
        }
        foreach (var s in manifest.ServerResources)
        {
            Build(s, false);
        }
        foreach (var fol in builtFolders)
        {
            Pack(fol);
        }
        var output = Path.Combine("bin", manifest.Name + ".respkg");
        foreach (var f in Directory.GetFiles(workingDir, "*.respkg")) { File.Delete(f); }
        Console.WriteLine("Packaging to " + output);
        PackFinal(Path.Combine(workingDir, "bin", "tmp"), output, Path.Combine(workingDir, "ResourceManifest.json"));
        Console.WriteLine($"Resource \"{manifest.Name}\" built successfully");

        void Build(string project, bool client)
        {
            var proc = new Process();
            var s = client ? "Client" : "Server";
            var buildPath = $"bin/tmp/{s}/{Path.GetFileNameWithoutExtension(project)}";
            proc.StartInfo = new ProcessStartInfo()
            {
                WorkingDirectory = workingDir,
                FileName = "dotnet",
                Arguments = $"publish \"{project}\" --configuration Release -o \"{buildPath}\""
            };
            proc.Start();
            proc.WaitForExit();
            if (proc.ExitCode != 0) { throw new Exception("Build failed"); }
            builtFolders.Add(Path.Combine(workingDir, buildPath));
        }

        void Pack(string folder)
        {
            var target = Path.Combine(Directory.GetParent(folder).FullName, Path.GetFileName(folder) + ".res");
            Console.WriteLine("Packing project: " + target);
            using ZipFile zip = ZipFile.Create(target);
            zip.BeginUpdate();
            foreach (var dir in Directory.GetDirectories(folder, "*", SearchOption.AllDirectories))
            {
                zip.AddDirectory(dir[(folder.Length + 1)..]);
            }
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                if ((file.EndsWith(".dll") && ApiAssemblies.Contains(AssemblyName.GetAssemblyName(file).FullName)) || file.CanBeIgnored()) { continue; }
                zip.Add(file, file[(folder.Length + 1)..]);
            }
            zip.CommitUpdate();
            zip.Close();
        }
        void PackFinal(string tmpDir, string output, string manifestPath)
        {
            var server = Path.Combine(tmpDir, "Server");
            var client = Path.Combine(tmpDir, "Client");
            Directory.CreateDirectory(server);
            Directory.CreateDirectory(client);
            var builder = new CDBuilder();
            builder.AddDirectory("Server");
            builder.AddDirectory("Client");
            builder.AddFile("ResourceManifest.json", manifestPath);
            foreach (var file in Directory.GetFiles(server, "*.res", SearchOption.TopDirectoryOnly))
            {
                builder.AddFile(file[(tmpDir.Length + 1)..], file);
            }
            foreach (var file in Directory.GetFiles(client, "*.res", SearchOption.TopDirectoryOnly))
            {
                builder.AddFile(file[(tmpDir.Length + 1)..], file);
            }
            builder.Build(output);

        }

    }
}
