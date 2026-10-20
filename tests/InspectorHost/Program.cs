using System;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var pluginPath = Path.GetFullPath(args[0]);
            var sdkDirectory = Path.GetFullPath(args[2]);
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                var file = Path.Combine(sdkDirectory, name + ".dll");
                if (name == "BulkStepBuilder") file = args.Length > 3 ? Path.GetFullPath(args[3]) : pluginPath;
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            var plugin = Assembly.LoadFile(pluginPath);
            var result = plugin.GetType("BulkStepBuilder.AssemblyInspector").GetMethod("Inspect")
                .Invoke(null, new object[] { Path.GetFullPath(args[1]) });
            var classes = (string[])result.GetType().GetField("PluginTypes").GetValue(result);
            if (classes.Length != 3) throw new Exception("Expected three plugin classes.");
            foreach (var name in classes) Console.WriteLine(name);
            Console.WriteLine("PASS: DLL loaded via Assembly.LoadFile discovers three example classes.");
            if (args.Length > 4)
            {
                var extensibility = Assembly.LoadFrom(Path.Combine(sdkDirectory, "XrmToolBox.Extensibility.dll"));
                var json = Assembly.LoadFrom(Path.Combine(sdkDirectory, "Newtonsoft.Json.dll"));
                var manifestType = extensibility.GetType("XrmToolBox.Extensibility.Manifest.Manifest", true);
                var manifest = json.GetType("Newtonsoft.Json.JsonConvert").GetMethod("DeserializeObject", new[] { typeof(string), typeof(Type) })
                    .Invoke(null, new object[] { File.ReadAllText(args[4]), manifestType });
                var loader = extensibility.GetType("XrmToolBox.Extensibility.Manifest.ManifestLoader", true);
                var plugins = (System.Collections.IEnumerable)loader.GetMethods().Single(m => m.Name == "LoadPlugins" && !m.IsGenericMethod)
                    .Invoke(null, new[] { manifest });
                bool found = false;
                foreach (var item in plugins)
                {
                    var metadata = item.GetType().GetProperty("Metadata").GetValue(item);
                    if ((string)metadata.GetType().GetProperty("PluginType").GetValue(metadata) != "BulkStepBuilder.Plugin") continue;
                    var instance = item.GetType().GetProperty("Value").GetValue(item);
                    if (instance == null) throw new Exception("Tool was not instantiated from the catalog.");
                    found = true;
                }
                if (!found) throw new Exception("Tool was not found in the catalog.");
                Console.WriteLine("PASS: The real XrmToolBox ManifestLoader resolves and instantiates the tool from the catalog.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex); return 1;
        }
    }
}
