using System.Collections.Specialized;
using AssetStudio;

namespace AssetStudioMobile;

internal sealed class MobileMonoBehaviourAssemblyService
{
    private readonly object _gate = new();
    private readonly AssemblyLoader _assemblyLoader = new();

    public MonoBehaviourAssemblyLoadSummary LoadFiles(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            _assemblyLoader.LoadFiles(paths);
            return new MonoBehaviourAssemblyLoadSummary(
                _assemblyLoader.LoadedAssemblyCount,
                _assemblyLoader.LoadWarnings.ToArray());
        }
    }

    public void Clear()
    {
        lock (_gate)
            _assemblyLoader.Clear();
    }

    public MonoBehaviourConversionResult Convert(MonoBehaviour behaviour)
    {
        ArgumentNullException.ThrowIfNull(behaviour);
        lock (_gate)
        {
            var data = behaviour.ToType();
            if (data != null)
                return new MonoBehaviourConversionResult(data, false, null);

            var typeTree = CreateAssemblyTypeTree(behaviour, out var error);
            if (typeTree == null)
                return new MonoBehaviourConversionResult(null, false, error);

            data = behaviour.ToType(typeTree);
            return data == null
                ? new MonoBehaviourConversionResult(null, true, "The assembly-derived type tree produced no JSON data.")
                : new MonoBehaviourConversionResult(data, true, null);
        }
    }

    public string? GetDump(MonoBehaviour behaviour, out string? error)
    {
        ArgumentNullException.ThrowIfNull(behaviour);
        lock (_gate)
        {
            var dump = behaviour.Dump();
            if (dump != null)
            {
                error = null;
                return dump;
            }

            var typeTree = CreateAssemblyTypeTree(behaviour, out error);
            if (typeTree == null)
                return null;

            dump = behaviour.Dump(typeTree);
            if (dump == null)
                error = "The assembly-derived type tree could not be read as a text dump.";
            return dump;
        }
    }

    private TypeTree? CreateAssemblyTypeTree(MonoBehaviour behaviour, out string? error)
    {
        if (!_assemblyLoader.Loaded)
        {
            error = "No serialized type tree is available. Load the game's managed DLL assemblies to convert this MonoBehaviour.";
            return null;
        }

        if (!behaviour.m_Script.TryGet(out var script) || script == null)
        {
            error = "The MonoScript reference could not be resolved, so its managed type cannot be identified.";
            return null;
        }

        var typeName = string.IsNullOrWhiteSpace(script.m_Namespace)
            ? script.m_ClassName
            : $"{script.m_Namespace}.{script.m_ClassName}";
        if (_assemblyLoader.GetTypeDefinition(script.m_AssemblyName, typeName) == null)
        {
            error = $"Could not find script type '{typeName}' in assembly '{script.m_AssemblyName}'. Select all DLLs from the game's Managed folder.";
            return null;
        }

        error = null;
        return behaviour.ConvertToTypeTree(_assemblyLoader);
    }
}

internal sealed record MonoBehaviourAssemblyLoadSummary(
    int LoadedAssemblyCount,
    IReadOnlyList<string> Warnings);

internal sealed record MonoBehaviourConversionResult(
    OrderedDictionary? Data,
    bool AssemblyAssisted,
    string? Error);
