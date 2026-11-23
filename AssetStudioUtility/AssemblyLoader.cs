using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AssetStudio
{
    public class AssemblyLoader
    {
        private readonly Dictionary<string, AssemblyDefinition> _assemblies =
            new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ModuleDefinition> _modules =
            new Dictionary<string, ModuleDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _loadWarnings = new List<string>();

        public bool Loaded => _modules.Count > 0;
        public int LoadedAssemblyCount => _modules.Count;
        public IReadOnlyList<string> LoadWarnings => _loadWarnings.AsReadOnly();

        public void Load(string path)
        {
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"Assembly folder not found: {path}");

            LoadFiles(Directory.GetFiles(path, "*.dll", SearchOption.AllDirectories));
        }

        public void LoadFiles(IEnumerable<string> paths)
        {
            if (paths == null)
                throw new ArgumentNullException(nameof(paths));
            Clear();

            var completed = false;
            try
            {
                var files = paths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(Path.GetFullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (files.Length == 0)
                    throw new InvalidOperationException("No managed assembly files were selected.");

                var resolver = new MyAssemblyResolver();
                foreach (var directory in files
                    .Select(Path.GetDirectoryName)
                    .Where(directory => !string.IsNullOrEmpty(directory))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    resolver.AddSearchDirectory(directory);
                }

                var readerParameters = new ReaderParameters
                {
                    AssemblyResolver = resolver,
                    InMemory = true,
                    ReadSymbols = false
                };

                foreach (var file in files)
                {
                    if (!File.Exists(file))
                    {
                        _loadWarnings.Add($"Skipped missing file: {Path.GetFileName(file)}");
                        continue;
                    }

                    if (!string.Equals(Path.GetExtension(file), ".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        _loadWarnings.Add($"Skipped non-DLL file: {Path.GetFileName(file)}");
                        continue;
                    }

                    try
                    {
                        var assembly = AssemblyDefinition.ReadAssembly(file, readerParameters);
                        var module = assembly.MainModule;
                        if (_modules.ContainsKey(module.Name))
                        {
                            _loadWarnings.Add($"Skipped duplicate assembly module: {module.Name}");
                            assembly.Dispose();
                            continue;
                        }

                        _assemblies.Add(module.Name, assembly);
                        _modules.Add(module.Name, module);
                        resolver.Register(assembly);
                    }
                    catch (Exception exception) when (
                        exception is BadImageFormatException
                        || exception is IOException
                        || exception is UnauthorizedAccessException
                        || exception is ArgumentException)
                    {
                        _loadWarnings.Add($"Skipped {Path.GetFileName(file)}: {exception.Message}");
                    }
                }

                if (!Loaded)
                {
                    var details = _loadWarnings.Count == 0
                        ? string.Empty
                        : $" Details: {string.Join(" | ", _loadWarnings)}";
                    throw new InvalidDataException($"No readable managed assemblies were found.{details}");
                }

                completed = true;
            }
            finally
            {
                if (!completed)
                    Clear();
            }
        }

        public TypeDefinition GetTypeDefinition(string assemblyName, string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return null;

            if (!string.IsNullOrWhiteSpace(assemblyName)
                && _modules.TryGetValue(NormalizeAssemblyName(assemblyName), out var module))
            {
                var typeDefinition = module.GetType(fullName);
                if (typeDefinition != null)
                    return typeDefinition;
            }

            if (string.IsNullOrWhiteSpace(assemblyName)
                || string.Equals(Path.GetFileName(assemblyName), "UnityEngine.dll", StringComparison.OrdinalIgnoreCase))
            {
                return FindUniqueTypeDefinition(fullName);
            }

            return null;
        }

        public void Clear()
        {
            foreach (var assembly in _assemblies.Values)
                assembly.Dispose();

            _assemblies.Clear();
            _modules.Clear();
            _loadWarnings.Clear();
        }

        private static string NormalizeAssemblyName(string assemblyName)
        {
            var fileName = Path.GetFileName(assemblyName.Trim());
            return Path.HasExtension(fileName) ? fileName : $"{fileName}.dll";
        }

        private TypeDefinition FindUniqueTypeDefinition(string fullName)
        {
            TypeDefinition match = null;
            foreach (var module in _modules.Values)
            {
                var candidate = module.GetType(fullName);
                if (candidate == null)
                    continue;

                if (match != null)
                    return null;
                match = candidate;
            }

            return match;
        }
    }
}
