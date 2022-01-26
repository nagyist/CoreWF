﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace System.Activities;

using Expressions;
using Validation;

/// <summary>
/// A base class for validating text expressions using the Microsoft.CodeAnalysis (Roslyn) package.
/// </summary>
public abstract class RoslynExpressionValidator
{
    private const string Comma = ", ";
    private static readonly Dictionary<Assembly, MetadataReference> MetadataReferenceCache = new();

    /// <summary>
    /// The kind of identifier to look for in the syntax tree as variables that need to be resolved for the expression.
    /// </summary>
    protected abstract int IdentifierKind { get; }

    /// <summary>
    /// Current compilation unit for compiling the expression.
    /// </summary>
    protected Compilation CompilationUnit { get; set; }

    /// <summary>
    /// Gets the MetadataReference objects for all of the referenced assemblies that this compilation unit could use.
    /// </summary>
    protected static IEnumerable<MetadataReference> MetadataReferences => MetadataReferenceCache.Values;

    /// <summary>
    /// Initializes the MetadataReference collection.
    /// </summary>
    /// <param name="referencedAssemblies">Assemblies to seed the collection. Will union with <see cref="JitCompilerHelper.DefaultReferencedAssemblies"/>.</param>
    protected RoslynExpressionValidator(HashSet<Assembly> referencedAssemblies = null)
    {
        referencedAssemblies ??= new HashSet<Assembly>();
        referencedAssemblies.UnionWith(JitCompilerHelper.DefaultReferencedAssemblies);
        foreach (Assembly referencedAssembly in referencedAssemblies)
        {
            _ = MetadataReferenceCache.TryAdd(referencedAssembly, References.GetReference(referencedAssembly));
        }
    }

    /// <summary>
    /// Gets the type name, which can be language-specific.
    /// </summary>
    /// <param name="type">typically the return type of the expression</param>
    /// <returns>type name</returns>
    protected abstract string GetTypeName(Type type);

    /// <summary>
    /// Adds some boilerplate text to hold the expression and allow parameters and return type checking during validation
    /// </summary>
    /// <param name="parameters">list of parameter names and types in comma-separated string</param>
    /// <param name="returnType">return type of expression</param>
    /// <param name="code">expression code</param>
    /// <returns>expression wrapped in a method or function that returns a LambdaExpression</returns>
    protected abstract string CreateValidationCode(string types, string names, string code);

    /// <summary>
    /// Gets the <see cref="Compilation"/> object for the current expression.
    /// </summary>
    /// <param name="expressionToValidate">current expression</param>
    /// <returns>Compilation object</returns>
    protected abstract Compilation GetCompilationUnit(ExpressionToCompile expressionToValidate);

    /// <summary>
    /// Gets the <see cref="SyntaxTree"/> for the expression.
    /// </summary>
    /// <param name="expressionToValidate">contains the text expression</param>
    /// <returns>a syntax tree to use in the <see cref="Compilation"/></returns>
    protected abstract SyntaxTree GetSyntaxTreeForExpression(ExpressionToCompile expressionToValidate);

    /// <summary>
    /// Convert diagnostic messages from the compilation into ValidationError objects that can be added to the activity's metadata.
    /// </summary>
    /// <param name="expressionToValidate">expression that was diagnosed</param>
    /// <param name="diagnostics">diagnostics returned from the compilation of an expression</param>
    /// <returns>ValidationError objects for the current activity</returns>
    protected virtual IEnumerable<ValidationError> ProcessDiagnostics(ExpressionToCompile expressionToValidate, IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Severity >= DiagnosticSeverity.Warning)
            {
                yield return new ValidationError(diagnostic.GetMessage(), diagnostic.Severity == DiagnosticSeverity.Warning);
            }
        }
    }

    /// <summary>
    /// Validates an expression and returns any validation errors.
    /// </summary>
    /// <typeparam name="T">Expression return type</typeparam>
    /// <param name="currentActivity">activity containing the expression</param>
    /// <param name="environment">location reference environment</param>
    /// <param name="expressionText">expression text</param>
    /// <returns>validation errors</returns>
    /// <remarks>
    /// Handles common steps for validating expressions with Roslyn. Can be reused for multiple expressions in the same language.
    /// </remarks>
    public IEnumerable<ValidationError> Validate<T>(Activity currentActivity, LocationReferenceEnvironment environment, string expressionText)
    {
        EnsureReturnTypeReferenced<T>();

        JitCompilerHelper.GetAllImportReferences(currentActivity, true, out List<string> localNamespaces, out List<AssemblyReference> localAssemblies);
        EnsureAssembliesInCompilationUnit(localAssemblies);

        var scriptAndTypeScope = new JitCompilerHelper.ScriptAndTypeScope(environment, null);
        var expressionToValidate = new ExpressionToCompile(expressionText, localNamespaces, scriptAndTypeScope.FindVariable, typeof(T));

        CreateExpressionValidator(expressionToValidate);
        var diagnostics = CompilationUnit.GetDiagnostics();
        return ProcessDiagnostics(expressionToValidate, diagnostics);
    }

    private void CreateExpressionValidator(ExpressionToCompile expressionToValidate)
    {
        CompilationUnit = GetCompilationUnit(expressionToValidate);
        SyntaxTree syntaxTree = GetSyntaxTreeForExpression(expressionToValidate);
        SyntaxTree oldSyntaxTree = CompilationUnit?.SyntaxTrees.FirstOrDefault();
        CompilationUnit = oldSyntaxTree == null
            ? CompilationUnit.AddSyntaxTrees(syntaxTree)
            : CompilationUnit.ReplaceSyntaxTree(oldSyntaxTree, syntaxTree);
        PrepValidation(expressionToValidate);
    }

    private void PrepValidation(ExpressionToCompile expressionToValidate)
    {
        var syntaxTree = CompilationUnit.SyntaxTrees.First();
        var identifiers = syntaxTree.GetRoot().DescendantNodesAndSelf().Where(n => n.RawKind == IdentifierKind).Select(n => n.ToString()).Distinct();
        var resolvedIdentifiers =
            identifiers
            .Select(name => (Name: name, Type: expressionToValidate.VariableTypeGetter(name)))
            .Where(var => var.Type != null)
            .ToArray();
        
        var names = string.Join(Comma, resolvedIdentifiers.Select(var => var.Name));
        var types = string.Join(Comma,
            resolvedIdentifiers
            .Select(var => var.Type)
            .Concat(new[] { expressionToValidate.LambdaReturnType })
            .Select(GetTypeName));

        var lambdaFuncCode = CreateValidationCode(types, names, expressionToValidate.Code);
        var sourceText = SourceText.From(lambdaFuncCode);
        var newSyntaxTree = syntaxTree.WithChangedText(sourceText);
        CompilationUnit = CompilationUnit.ReplaceSyntaxTree(syntaxTree, newSyntaxTree);
    }

    private void EnsureReturnTypeReferenced<T>()
    {
        Type expressionReturnType = typeof(T);

        HashSet<Type> allBaseTypes = null;
        JitCompilerHelper.EnsureTypeReferenced(expressionReturnType, ref allBaseTypes);
        List<MetadataReference> newReferences = null;
        foreach (Type baseType in allBaseTypes)
        {
            var asm = baseType.Assembly;
            if (!MetadataReferenceCache.ContainsKey(asm))
            {
                var meta = References.GetReference(asm);
                MetadataReferenceCache.Add(asm, meta);
                newReferences ??= new();
                newReferences.Add(meta);
            }
        }

        UpdateMetadataReferencesInCompilationUnit(newReferences);
    }

    private void EnsureAssembliesInCompilationUnit(List<AssemblyReference> localAssemblies)
    {
        List<MetadataReference> newReferences = null;
        foreach (AssemblyReference assemblyRef in localAssemblies)
        {
            var asm = assemblyRef.Assembly;
            if (asm == null)
            {
                assemblyRef.LoadAssembly();
                asm = assemblyRef.Assembly;
            }

            if (asm != null && !MetadataReferenceCache.ContainsKey(asm))
            {
                var meta = References.GetReference(asm);
                MetadataReferenceCache.Add(asm, meta);
                newReferences ??= new();
                newReferences.Add(meta);
            }
        }

        UpdateMetadataReferencesInCompilationUnit(newReferences);
    }

    private void UpdateMetadataReferencesInCompilationUnit(IEnumerable<MetadataReference> metadataReferences)
    {
        if (metadataReferences != null && CompilationUnit != null)
        {
            CompilationUnit = CompilationUnit.AddReferences(metadataReferences);
        }
    }
}