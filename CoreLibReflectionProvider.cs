using Mono.Cecil;
using System.Reflection;

// The problem with Cecil is that when we import System types that are forwarded to System.Private.CoreLib,
// such as most types in System.Runtime, e.g. EventHandler<>,
// Cecil creates a reference to System.Private.CoreLib instead of System.Runtime, which we don't want.
// This custom ReflectionImporter replaces System.Private.CoreLib assembly references when importing.

class CoreLibReflectionImporterProvider : IReflectionImporterProvider
{
    private readonly string fixedName;

    public CoreLibReflectionImporterProvider(string fixedName)
    {
        this.fixedName = fixedName;
    }

    public IReflectionImporter GetReflectionImporter(ModuleDefinition module)
        => new CoreLibReflectionImporter(module, fixedName);
}

class CoreLibReflectionImporter : DefaultReflectionImporter
{
    readonly Lazy<AssemblyNameReference> fixedAssembly;

	public CoreLibReflectionImporter(ModuleDefinition module, string fixedName) : base(module)
	{
        fixedAssembly = new(() =>
        {
            var assemblyRef = AssemblyNameReference.Parse(fixedName);
            if (module.AssemblyReferences.FirstOrDefault(a => a.Name == assemblyRef.Name) is { } existing)
                return existing;

            module.AssemblyReferences.Add(assemblyRef);
            return assemblyRef;
        });
    }

    public override AssemblyNameReference ImportReference(AssemblyName name)
    {
        return name.Name == "System.Private.CoreLib"
            ? fixedAssembly.Value
            : base.ImportReference(name);
    }
}
