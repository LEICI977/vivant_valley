using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.Json;
using System.Text.RegularExpressions;
var root = Path.GetFullPath(args[0]);
var items = new List<object>();
foreach(var path in Directory.EnumerateFiles(root,"*.cs",SearchOption.AllDirectories)) {
 var relative=Path.GetRelativePath(root,path).Replace('\\','/');
 if(new[]{"tests/","obj/","bin/","reviews/"}.Any(relative.StartsWith)) continue;
 var tree=CSharpSyntaxTree.ParseText(File.ReadAllText(path));
 foreach(var node in tree.GetRoot().DescendantNodes().Where(n=>n is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression) || n is InterpolatedStringExpressionSyntax)) {
  if(node.Ancestors().Any(n=>n is InterpolatedStringExpressionSyntax)) continue;
  string value; bool interpolated=node is InterpolatedStringExpressionSyntax;
  if(node is LiteralExpressionSyntax literal) value=literal.Token.ValueText;
  else { int index=0; value=string.Concat(((InterpolatedStringExpressionSyntax)node).Contents.Select(c=>c is InterpolatedStringTextSyntax t ? t.TextToken.ValueText : "{"+(index++)+ ((InterpolationSyntax)c).AlignmentClause?.ToString() + ((InterpolationSyntax)c).FormatClause?.ToString()+"}")); }
  if(!Regex.IsMatch(value,@"[\p{IsCJKUnifiedIdeographs}]"))continue;
  items.Add(new {path=relative,start=node.SpanStart,length=node.Span.Length,value,interpolated,original=node.ToString(),parent=node.Parent?.ToString(),method=node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.Text});
 }
}
File.WriteAllText(Path.Combine(root,"reviews/localization-inventory.json"),JsonSerializer.Serialize(items,new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
Console.WriteLine($"Inventoried {items.Count} strings.");
