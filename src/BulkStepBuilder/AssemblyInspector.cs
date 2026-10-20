using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using Mono.Cecil;

namespace BulkStepBuilder
{
    public static class AssemblyInspector
    {

        public static AssemblyInfo Inspect(string path)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                var bytes = File.ReadAllBytes(fullPath);
                // Parse metadata without asking the CLR to load any user assembly.
                return Read(bytes, fullPath, typeof(Microsoft.Xrm.Sdk.IPlugin).Assembly.Location);
            }
            catch (Exception ex)
            {
                var reason = ex;
                while (reason is TargetInvocationException && reason.InnerException != null) reason = reason.InnerException;
                var message = reason is InvalidOperationException ? reason.Message :
                    reason is BadImageFormatException ? "This file is not a supported plugin DLL. Select a managed .NET Framework assembly that implements Microsoft.Xrm.Sdk.IPlugin." :
                    reason is FileNotFoundException || reason is DirectoryNotFoundException ? "The plugin DLL or a required dependency could not be found. Keep dependency DLLs beside the selected file." :
                    reason is FileLoadException || reason is TypeLoadException ? "The plugin metadata could not be loaded. Check that required dependency DLLs are beside the selected file." :
                    reason is UnauthorizedAccessException || reason is IOException ? "The DLL could not be read. Check file permissions and try a local copy." :
                    "Bulk Step Builder could not inspect this DLL. This failure does not establish that the DLL is invalid.";
                // Do not replace the actual failure with an unsupported guess about the user's DLL.
                throw new InvalidOperationException(message + "\n\nTechnical reason: " + reason.GetType().Name + ": " + reason.Message, reason);
            }
        }

        private static AssemblyInfo Read(byte[] bytes, string path, string sdkPath)
        {
            using (var resolver = new DefaultAssemblyResolver())
            using (var stream = new MemoryStream(bytes, false))
            {
                resolver.AddSearchDirectory(Path.GetDirectoryName(path));
                resolver.AddSearchDirectory(Path.GetDirectoryName(sdkPath));
                resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location));
                try
                {
                    using (var assembly = AssemblyDefinition.ReadAssembly(stream, new ReaderParameters
                    { AssemblyResolver = resolver, InMemory = true, ReadingMode = ReadingMode.Immediate, ReadSymbols = false }))
                    {
                        var plugins = assembly.Modules.SelectMany(m => m.Types)
                            .Where(t => t.IsClass && t.IsPublic && !t.IsAbstract && !t.HasGenericParameters &&
                                ImplementsPlugin(t, new HashSet<string>()))
                            .Select(t => t.FullName).OrderBy(n => n).ToArray();
                        if (plugins.Length == 0) throw new InvalidOperationException("The DLL contains no public concrete classes implementing IPlugin.");
                        var identity = assembly.Name;
                        var token = identity.PublicKeyToken;
                        return new AssemblyInfo
                        {
                            Name = identity.Name, Version = identity.Version.ToString(), Culture = string.IsNullOrEmpty(identity.Culture) ? "neutral" : identity.Culture,
                            PublicKeyToken = token == null ? "" : BitConverter.ToString(token).Replace("-", "").ToLowerInvariant(),
                            HasStrongNameSignature = identity.HasPublicKey && (assembly.MainModule.Attributes & ModuleAttributes.StrongNameSigned) != 0,
                            PluginTypes = plugins, Content = bytes
                        };
                    }
                }
                catch (AssemblyResolutionException ex)
                {
                    throw new InvalidOperationException("A dependency required to inspect inherited plugin classes is missing: " +
                        ex.AssemblyReference.Name + ". Place that DLL beside the selected plugin DLL.", ex);
                }
            }
        }

        private static bool ImplementsPlugin(TypeDefinition type, HashSet<string> visited)
        {
            if (type == null || !visited.Add(type.Module.Assembly.Name.FullName + ":" + type.FullName)) return false;
            foreach (var implementation in type.Interfaces)
            {
                var contract = implementation.InterfaceType;
                if (contract.FullName == "Microsoft.Xrm.Sdk.IPlugin" && contract.Scope.Name == "Microsoft.Xrm.Sdk") return true;
                if (ImplementsPlugin(contract.Resolve(), visited)) return true;
            }
            var parent = type.BaseType;
            return parent != null && parent.FullName != "System.Object" && ImplementsPlugin(parent.Resolve(), visited);
        }
    }
}
