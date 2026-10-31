using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis;
using Qx.Protocol;
using Qx.Scripting.Hosting;
using Qx.Scripting;
using RoslynPad.Roslyn;

namespace Qx.Desktop.Editor;

public sealed class QxRoslynHost : RoslynHost
{
    readonly AnalyzerReference _script_analyzer;

    public QxRoslynHost(IMessageResolver messages)
        : base(
            additionalAssemblies:
            [
                Assembly.Load("RoslynPad.Roslyn.Avalonia"),
                Assembly.Load("RoslynPad.Editor.Avalonia")
            ],
            references: RoslynHostReferences.Empty.With(
                assemblyPathReferences: FrameworkReferencePaths(),
                assemblyReferences: ScriptEngine.ReferenceAssemblies,
                imports: ScriptEngine.Imports))
    {
        _script_analyzer = new AnalyzerImageReference([ScriptEngine.CreateAnalyzer(messages)]);
    }

    protected override IEnumerable<AnalyzerReference> GetSolutionAnalyzerReferences() =>
        base.GetSolutionAnalyzerReferences().Append(_script_analyzer);

    protected override Project CreateProject(Solution solution, DocumentCreationArgs args, CompilationOptions compilationOptions, Project? previousProject = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(compilationOptions);
        string name = args.Name ?? "Script";
        ProjectId id = ProjectId.CreateNewId(name);
        var parse_options = new CSharpParseOptions(kind: SourceCodeKind.Script, languageVersion: LanguageVersion.Latest);
        if (compilationOptions is CSharpCompilationOptions csharp)
            compilationOptions = csharp.WithNullableContextOptions(NullableContextOptions.Disable);
        compilationOptions = compilationOptions
            .WithScriptClassName(name)
            .WithMetadataReferenceResolver(ScriptMetadataResolver.Default.WithBaseDirectory(args.WorkingDirectory))
            .WithSpecificDiagnosticOptions(compilationOptions.SpecificDiagnosticOptions.SetItems(
                ScriptEngine.SuppressedDiagnostics
                    .Append("IDE1006")
                    .Select(diagnostic_id => KeyValuePair.Create(diagnostic_id, ReportDiagnostic.Suppress))));
        solution = solution.AddProject(ProjectInfo.Create(
            id,
            VersionStamp.Create(),
            name,
            name,
            LanguageNames.CSharp,
            isSubmission: true,
            parseOptions: parse_options,
            hostObjectType: typeof(ScriptGlobals),
            compilationOptions: compilationOptions,
            metadataReferences: previousProject is null ? DefaultReferences : [],
            projectReferences: previousProject is null ? null : [new ProjectReference(previousProject.Id)]));
        return solution.GetProject(id) ?? throw new InvalidOperationException("Failed to create the Roslyn project.");
    }

    static IEnumerable<string> FrameworkReferencePaths()
    {
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string trusted)
            return [];
        return trusted.Split(Path.PathSeparator).Where(path =>
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("QX.", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("RoslynPad", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))
                return false;
            bool wanted = name is "netstandard" or "mscorlib" or "System"
                || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Microsoft.VisualBasic", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Microsoft.Win32", StringComparison.OrdinalIgnoreCase);
            return wanted && File.Exists(path);
        });
    }
}
