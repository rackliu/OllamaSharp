using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OllamaSharp;

/// <summary>
/// A source generator that produces tool implementations including invocations
/// for methods marked with [OllamaToolAttribute].
/// </summary>
[Generator]
public class ToolSourceGenerator : IIncrementalGenerator
{
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var methodCandidates = context.SyntaxProvider
			.CreateSyntaxProvider(
				static (syntaxNode, cancellationToken) => IsCandidateMethod(syntaxNode),
				static (ctx, cancellationToken) => GetMethodSymbolIfMarked(ctx)
			)
			.Where(static methodSymbol => methodSymbol is not null)!;

		var compilationAndMethods = context.CompilationProvider.Combine(methodCandidates.Collect());

		context.RegisterSourceOutput(
			compilationAndMethods,
			(spc, source) => ExecuteGeneration(spc, source.Left, source.Right)
		);
	}

	/// <summary>
	/// Creates the final source code for each discovered [OllamaTool] method.
	/// </summary>
	private static void ExecuteGeneration(SourceProductionContext context, Compilation compilation, IReadOnlyList<IMethodSymbol> methods)
	{
		foreach (var methodSymbol in methods)
		{
			var ns = methodSymbol.ContainingType.ContainingNamespace?.ToString() ?? "";
			var className = methodSymbol.ContainingType.Name;
			var toolClassName = methodSymbol.Name + "Tool";

			var docCommentXml = methodSymbol.GetDocumentationCommentXml();
			var (methodSummary, paramComments) = ExtractDocComments(docCommentXml);

			var (propertiesCode, requiredParams) = GeneratePropertiesCode(methodSymbol.Parameters, paramComments);
			var invokeMethodCode = GenerateInvokeMethodCode(methodSymbol);

			var sourceCode = GenerateToolClassCode(
				ns,
				className,
				toolClassName,
				methodSymbol.Name,
				methodSummary,
				propertiesCode,
				requiredParams,
				invokeMethodCode
			);

			var hintName = ns + "." + className + "." + toolClassName + ".g.cs";
			context.AddSource(hintName, sourceCode);
		}
	}

	private static bool IsCandidateMethod(SyntaxNode node) => node is MethodDeclarationSyntax { AttributeLists.Count: > 0 };

	private static IMethodSymbol? GetMethodSymbolIfMarked(GeneratorSyntaxContext context)
	{
		var methodDecl = (MethodDeclarationSyntax)context.Node;
		var model = context.SemanticModel;
		var hasOllamaToolAttribute = methodDecl.AttributeLists
			.SelectMany(al => al.Attributes)
			.Any(a => IsOllamaToolAttribute(a, model));

		return hasOllamaToolAttribute ? model.GetDeclaredSymbol(methodDecl) : null;
	}

	private static bool IsOllamaToolAttribute(AttributeSyntax attr, SemanticModel model)
	{
		var typeInfo = model.GetTypeInfo(attr);
		var name = typeInfo.Type?.ToDisplayString() ?? "";
		return name.EndsWith("OllamaToolAttribute", StringComparison.Ordinal);
	}

	private static (string methodSummary, Dictionary<string, string> paramComments) ExtractDocComments(string? xmlDoc)
	{
		var summary = "";
		var paramDict = new Dictionary<string, string>();
		if (!string.IsNullOrEmpty(xmlDoc))
		{
			var sStart = xmlDoc!.IndexOf("<summary>", StringComparison.OrdinalIgnoreCase);
			var sEnd = xmlDoc.IndexOf("</summary>", StringComparison.OrdinalIgnoreCase);
			if (sStart != -1 && sEnd != -1)
			{
				summary = xmlDoc.Substring(sStart + 9, sEnd - (sStart + 9)).Trim();
			}

			var paramTag = "<param name=\"";
			var idx = 0;
			while (true)
			{
				var pStart = xmlDoc.IndexOf(paramTag, idx, StringComparison.OrdinalIgnoreCase);
				if (pStart == -1)
					break;
				var quoteEnd = xmlDoc.IndexOf("\"", pStart + paramTag.Length, StringComparison.OrdinalIgnoreCase);
				if (quoteEnd == -1)
					break;

				var name = xmlDoc.Substring(pStart + paramTag.Length, quoteEnd - (pStart + paramTag.Length));
				var closeTag = "</param>";
				var pEnd = xmlDoc.IndexOf(closeTag, quoteEnd, StringComparison.OrdinalIgnoreCase);
				if (pEnd == -1)
					break;

				var contentStart = xmlDoc.IndexOf(">", quoteEnd, StringComparison.OrdinalIgnoreCase);
				if (contentStart == -1)
					break;

				var content = xmlDoc.Substring(contentStart + 1, pEnd - (contentStart + 1)).Trim();
				paramDict[name] = content;
				idx = pEnd + closeTag.Length;
			}
		}
		return (summary, paramDict);
	}

	private static (string propertiesCode, string requiredParams) GeneratePropertiesCode(
		IReadOnlyList<IParameterSymbol> parameters,
		Dictionary<string, string> paramComments)
	{
		var lines = new List<string>();
		var requiredList = new List<string>();

		foreach (var param in parameters)
		{
			var paramName = param.Name;
			var pType = param.Type;
			var desc = paramComments.ContainsKey(paramName) ? paramComments[paramName] : "No description.";
			var typeName = pType.Name;
			var jsonType = "string";
			IEnumerable<string>? enumValues = null;

			if (pType.TypeKind == TypeKind.Enum)
			{
				jsonType = "string";
				if (pType is INamedTypeSymbol enumSym)
				{
					enumValues = enumSym.GetMembers()
						.OfType<IFieldSymbol>()
						.Where(f => f.ConstantValue != null)
						.Select(f => f.Name);
				}
			}
			else if (typeName.Equals("Int32", StringComparison.OrdinalIgnoreCase)
				  || typeName.Equals("Int64", StringComparison.OrdinalIgnoreCase)
				  || typeName.Equals("Double", StringComparison.OrdinalIgnoreCase)
				  || typeName.Equals("Single", StringComparison.OrdinalIgnoreCase))
			{
				jsonType = "number";
			}

			if (!param.IsOptional)
				requiredList.Add($"\"{paramName}\"");

			var enumSection = "";
			if (enumValues is not null)
			{
				var joined = string.Join(", ", enumValues.Select(e => $"\"{e}\""));
				enumSection = $", Enum = new[] {{{joined}}}";
			}

			lines.Add($@"                    {{ ""{paramName}"", new OllamaSharp.Models.Chat.Property {{ Type = ""{jsonType}"", Description = ""{Escape(desc)}""{enumSection} }} }}");
		}

		var propsJoined = string.Join(",\r\n", lines);
		var req = requiredList.Count > 0
			? "new[] {" + string.Join(", ", requiredList) + "}"
			: "Array.Empty<string>()";
		return (propsJoined, req);
	}

	private static string GenerateInvokeMethodCode(IMethodSymbol methodSymbol)
	{
		var parameters = methodSymbol.Parameters;
		var methodName = methodSymbol.Name;
		var className = methodSymbol.ContainingType.ToDisplayString();
		var returnType = methodSymbol.ReturnType;
		var isAsync = false;
		string? resultType = null;

		if (returnType.Name.Equals("Task", StringComparison.OrdinalIgnoreCase))
		{
			if (returnType is INamedTypeSymbol named && named.IsGenericType)
			{
				var typeArg = named.TypeArguments.FirstOrDefault();
				if (typeArg != null)
					resultType = typeArg.ToDisplayString();
			}
			isAsync = true;
		}
		else
		{
			resultType = returnType.ToDisplayString();
		}

		var paramLines = new List<string>();
		var usageParams = new List<string>();
		foreach (var p in parameters)
		{
			var pName = p.Name;
			var safeName = ToValidIdentifier(pName);
			var pType = p.Type.ToDisplayString();
			var tName = p.Type.Name;

			if (p.Type.TypeKind == TypeKind.Enum)
			{
				if (p.IsOptional)
					paramLines.Add($@"            {pType} {safeName} = ({pType})Enum.Parse(typeof({pType}), args.ContainsKey(""{pName}"") ? args[""{pName}""]?.ToString() ?? ""{p.ExplicitDefaultValue?.ToString() ?? p.Type.GetMembers().OfType<IFieldSymbol>().FirstOrDefault()?.Name}"" : ""{p.ExplicitDefaultValue?.ToString() ?? p.Type.GetMembers().OfType<IFieldSymbol>().FirstOrDefault()?.Name}"", true);");
				else
					paramLines.Add($@"            {pType} {safeName} = ({pType})Enum.Parse(typeof({pType}), args[""{pName}""]?.ToString() ?? """", true);");
			}
			else if (tName.Equals("Int32", StringComparison.OrdinalIgnoreCase)
				  || tName.Equals("Int64", StringComparison.OrdinalIgnoreCase)
				  || tName.Equals("Double", StringComparison.OrdinalIgnoreCase)
				  || tName.Equals("Single", StringComparison.OrdinalIgnoreCase))
			{
				if (p.IsOptional)
					paramLines.Add($@"            {pType} {safeName} = args.ContainsKey(""{pName}"") ? Convert.To{tName}(args[""{pName}""]) : {(p.ExplicitDefaultValue ?? 0)};");
				else
					paramLines.Add($@"            {pType} {safeName} = Convert.To{tName}(args[""{pName}""]);");
			}
			else
			{
				if (tName.Equals("String", StringComparison.OrdinalIgnoreCase))
				{
					if (p.IsOptional)
					{
						var def = p.ExplicitDefaultValue is null ? "\"\"" : $"\"{p.ExplicitDefaultValue}\"";
						paramLines.Add($@"            {pType} {safeName} = ({pType}?)args[""{pName}""] ?? {def};");
					}
					else
					{
						paramLines.Add($@"            {pType} {safeName} = ({pType}?)args[""{pName}""] ?? """";");
					}
				}
				else
				{
					if (p.IsOptional && p.ExplicitDefaultValue != null)
						paramLines.Add($@"            {pType} {safeName} = ({pType}?)args[""{pName}""] ?? ({pType}){p.ExplicitDefaultValue};");
					else
						paramLines.Add($@"            {pType} {safeName} = ({pType}?)args[""{pName}""];");
				}
			}

			usageParams.Add(safeName);
		}

		var paramBlock = string.Join("\r\n", paramLines);
		var joinedParams = string.Join(", ", usageParams);

		var asyncSignature = isAsync
			? $"        public async Task<object?> InvokeMethodAsync(IDictionary<string, object?>? args)"
			: $"        public object? InvokeMethod(IDictionary<string, object?>? args)";

		var callPart = isAsync
			? $@"var result = await {className}.{methodName}({joinedParams});
            return result;"
			: returnType.SpecialType == SpecialType.System_Void
				? $@"{className}.{methodName}({joinedParams});
            return null;"
				: $@"var result = {className}.{methodName}({joinedParams});
            return result;";

		return
$@"{asyncSignature}
        {{
            if (args == null) args = new Dictionary<string, object?>();
{paramBlock}

            {callPart}
        }}";
	}

	private static string GenerateToolClassCode(
		string containingNamespace,
		string containingClass,
		string toolClassName,
		string originalMethodName,
		string methodSummary,
		string propertiesCode,
		string requiredParams,
		string invokeMethodCode)
	{
		var isAsync = invokeMethodCode.Contains("async Task");

		return
$@"using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace {containingNamespace}
{{
    /// <summary>
    /// This class was auto-generated by the OllamaSharp ToolSourceGenerator.
    /// </summary>
    public class {toolClassName} : OllamaSharp.Models.Chat.Tool, {(isAsync ? "OllamaSharp.Tools.IAsyncInvokableTool" : "OllamaSharp.Tools.IInvokableTool")}
    {{
        /// <summary>
        /// Initializes a new instance with metadata about the original method.
        /// </summary>
        public {toolClassName}()
        {{
            this.Function = new OllamaSharp.Models.Chat.Function
            {{
                Name = ""{originalMethodName}"",
                Description = ""{Escape(methodSummary)}""
            }};

            this.Function.Parameters = new OllamaSharp.Models.Chat.Parameters
            {{
                Properties = new Dictionary<string, OllamaSharp.Models.Chat.Property>
                {{
{propertiesCode}
                }},
                Required = {requiredParams}
            }};
        }}

{invokeMethodCode}
    }}
}}
";
	}

	private static string Escape(string input) => input.Replace("\\", "\\\\").Replace("\"", "\\\"");

	private static string ToValidIdentifier(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "_" + name : name;
}
